using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    internal sealed class SurrogateSlot
    {
        public string Name = "";
        public ISurrogateModel Model = null!;
        public bool Log;
        public double OutputVariance = 1.0;

        public double Forward(double value) => Log ? Math.Log(value) : value;
        public double Back(double value) => Log ? Math.Exp(value) : value;
    }

    internal sealed class FittedEnsemble
    {
        public List<SurrogateSlot> Slots = new();
        public bool FitnessLog;
        public double BestTransformed;

        /// <summary>
        /// Wie weit unter dem bisher Besten (in transformierten Einheiten) ein Punkt noch als „möglicherweise gut"
        /// gilt: halbe Fitness bei logarithmischer Fitness, sonst die halbe Streuung der bisherigen Werte.
        /// </summary>
        public double PlausibleGap;

        /// <summary>Kleinere erwartete Verbesserung (transformierte Einheiten) zählt als „keine": 0,2 % bei logarithmischer Fitness.</summary>
        public double MeaningfulImprovement;
        public double Offset;
        public double[][] Z = Array.Empty<double[]>();
        public IReadOnlyList<double[]> Excluded = Array.Empty<double[]>();

        /// <summary>Vorschläge der laufenden Runde: untereinander abgestoßen, damit ein Block nicht aus Beinahe-Dubletten besteht.</summary>
        public List<double[]> Chosen = new();

        /// <summary>Lernt „Rechnung gelingt" (1) gegen „stürzt ab" (0); <c>null</c> ohne Abstürze.</summary>
        public ISurrogateModel? SuccessModel;
        public double RepulsionRadius;
        public double[] Relevance = Array.Empty<double>();

        public double T(double fitness) => FitnessLog ? Math.Log(fitness) : fitness;
    }

    /// <summary>
    /// Verwaltet die Ersatzmodelle (Fitness oder Kennwerte inkl. Erfolgsmodell) und ihre Lernmengen-Auswahl.
    /// </summary>
    internal sealed class SurrogateModelEnsemble
    {
        internal const int FullSamples = 64;
        private const double ImprovementOffset = 0.01;

        private readonly IFitnessCalculator _fitness;
        private readonly BayesTuning _tuning;
        private readonly Func<ISurrogateModel> _modelFactory;
        private readonly Dictionary<string, ISurrogateModel> _models = new();
        private readonly HashSet<ModelRecord> _checkedRecords = new();
        private readonly ModelRecord _scratch = new();

        private SimulationConfig _simulationConfig = new();
        private double[]? _lastRelevance;

        public SurrogateModelEnsemble(
            IFitnessCalculator fitness,
            BayesTuning tuning,
            Func<ISurrogateModel> modelFactory)
        {
            _fitness = fitness;
            _tuning = tuning;
            _modelFactory = modelFactory;
        }

        /// <summary>Wirksamer Modus nach Auflösung von "Auto" und eventuellem Rückfall (TODO-56).</summary>
        public SurrogateTargetMode ActiveMode { get; private set; } = SurrogateTargetMode.Fitness;

        public void Initialize(SurrogateTargetMode mode, SimulationConfig simulationConfig)
        {
            ActiveMode = mode;
            _simulationConfig = simulationConfig;
        }

        /// <summary>
        /// Gibt es mehr nutzbare Rechnungen als <c>MaxTrainingPoints</c>, bleiben die besten 10 % des
        /// Deckels und die dem Zentrum nächsten (mit den Längenskalen gewichtet).
        /// </summary>
        public List<int> SelectTrainingIndices(SurrogateTrainingSet training, double[] center)
        {
            int n = training.Records.Count;
            int cap = _tuning.MaxTrainingPoints;
            if (n <= cap) return Enumerable.Range(0, n).ToList();

            int keepBest = Math.Max(1, (int)Math.Ceiling(0.1 * cap));
            var byFitness = Enumerable.Range(0, n)
                .OrderByDescending(i => training.Records[i].Fitness)
                .ThenBy(i => i)
                .ToList();
            var chosen = new HashSet<int>(byFitness.Take(keepBest));

            double[] weights = _lastRelevance != null && _lastRelevance.Length == center.Length
                ? _lastRelevance
                : Enumerable.Repeat(1.0, center.Length).ToArray();

            var byDistance = Enumerable.Range(0, n)
                .Where(i => !chosen.Contains(i))
                .OrderBy(i => WeightedDistance(training.Inputs[i], center, weights))
                .ThenBy(i => i)
                .Take(cap - chosen.Count);
            foreach (int i in byDistance) chosen.Add(i);

            return chosen.OrderBy(i => i).ToList();
        }

        private static double WeightedDistance(double[] a, double[] b, double[] weights)
        {
            double sum = 0.0;
            for (int i = 0; i < a.Length; i++)
            {
                double diff = (a[i] - b[i]) * weights[i];
                sum += diff * diff;
            }
            return sum;
        }

        private ISurrogateModel ModelFor(string name)
        {
            if (!_models.TryGetValue(name, out ISurrogateModel? model))
            {
                model = _modelFactory();
                _models[name] = model;
            }
            return model;
        }

        public FittedEnsemble FitModels(SurrogateTrainingSet training, List<int> selected, int dimension)
        {
            var fit = new FittedEnsemble();
            double bestFitness = training.Records.Max(r => r.Fitness);
            bool allPositive = training.Records.All(r => r.Fitness > 0);
            fit.FitnessLog = allPositive;
            fit.BestTransformed = fit.T(bestFitness);
            double fitnessSpread = Math.Sqrt(Variance(training.Records.Select(r => (double)r.Fitness).ToList()));
            fit.PlausibleGap = allPositive ? Math.Log(2.0) : 0.5 * fitnessSpread;
            fit.MeaningfulImprovement = allPositive ? 0.002 : 0.002 * fitnessSpread;

            if (ActiveMode == SurrogateTargetMode.Fitness)
            {
                var inputs = selected.Select(i => training.Inputs[i]).ToList();
                var slot = new SurrogateSlot { Name = "Fitness", Log = allPositive };
                var outputs = selected.Select(i => slot.Forward(training.Records[i].Fitness)).ToList();

                slot.Model = ModelFor(slot.Name);
                slot.OutputVariance = Variance(outputs);

                // Bekannte Unsicherheit je Rechnung (z. B. Wirbelablösung) als Rauschen je Punkt.
                var noise = selected.Select(i =>
                {
                    ModelRecord r = training.Records[i];
                    if (!MetricUncertainty.TryGetFitness(r, _fitness.RequiredMetrics, out double u)) return 0.0;
                    return slot.Log ? u * u : Math.Pow(u * r.Fitness, 2);
                }).ToList();

                slot.Model.Fit(inputs, outputs, noise.Any(v => v > 0) ? noise : null);
                fit.Slots.Add(slot);

                fit.Offset = ImprovementOffset * Math.Sqrt(slot.OutputVariance);
            }
            else
            {
                foreach (string metric in _fitness.RequiredMetrics)
                {
                    var rows = selected
                        .Where(i => training.Records[i].PassiveParameters.TryGetValue(metric, out float v) && float.IsFinite(v))
                        .ToList();

                    if (rows.Count < 3)
                    {
                        throw new SurrogateFitException(
                            $"Für den Kennwert '{metric}' gibt es nur {rows.Count} gültige Werte (mindestens 3 nötig).");
                    }

                    var values = rows.Select(i => (double)training.Records[i].PassiveParameters[metric]).ToList();
                    var slot = new SurrogateSlot { Name = metric, Log = values.All(v => v > 0) };
                    var outputs = values.Select(slot.Forward).ToList();

                    slot.Model = ModelFor(metric);
                    slot.OutputVariance = Variance(outputs);

                    var noise = rows.Select(i =>
                    {
                        ModelRecord r = training.Records[i];
                        if (!MetricUncertainty.TryGet(r, metric, out double u)) return 0.0;
                        return slot.Log ? u * u : Math.Pow(u * r.PassiveParameters[metric], 2);
                    }).ToList();

                    slot.Model.Fit(rows.Select(i => training.Inputs[i]).ToList(), outputs, noise.Any(v => v > 0) ? noise : null);
                    fit.Slots.Add(slot);
                }

                fit.Offset = 0.0;
            }

            fit.Relevance = CombineRelevance(fit, dimension);
            _lastRelevance = fit.Relevance;
            return fit;
        }

        public void FitSuccessModel(SurrogateTrainingSet training, FittedEnsemble fit)
        {
            if (training.FailedPoints.Count <= 0) return;

            var inputs = training.Inputs.Concat(training.FailedPoints).ToList();
            var labels = training.Inputs.Select(_ => 1.0).Concat(training.FailedPoints.Select(_ => 0.0)).ToList();
            fit.SuccessModel = ModelFor("__Erfolg");
            fit.SuccessModel.Fit(inputs, labels);
        }

        private static double Variance(IReadOnlyList<double> values)
        {
            double mean = values.Average();
            double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            return variance > 1e-18 ? variance : 1.0;
        }

        private static double[] CombineRelevance(FittedEnsemble fit, int dimension)
        {
            var sums = new double[dimension];
            int models = 0;

            foreach (SurrogateSlot slot in fit.Slots)
            {
                IReadOnlyList<double>? relevance = slot.Model.ParameterRelevance;
                for (int i = 0; i < dimension; i++) sums[i] += relevance != null && relevance.Count == dimension ? relevance[i] : 1.0;
                models++;
            }

            if (models == 0) return Enumerable.Repeat(1.0, dimension).ToArray();
            for (int i = 0; i < dimension; i++) sums[i] /= models;
            return sums;
        }

        /// <summary>
        /// Prüft für jede neue Rechnung, ob die Fitness allein aus den deklarierten Kennwerten und den
        /// Parametern folgt. Wenn nicht, liest die Formel offenbar weitere Kennwerte: dann fällt der Rest
        /// des Laufs auf den Modus "Fitness" zurück. Robustheit geht vor Strenge.
        /// </summary>
        public void VerifyMetricsMode(SurrogateTrainingSet training)
        {
            foreach (ModelRecord record in training.Records)
            {
                if (!_checkedRecords.Add(record)) continue;

                var passive = new Dictionary<string, float>();
                bool complete = true;
                foreach (string metric in _fitness.RequiredMetrics)
                {
                    if (record.PassiveParameters.TryGetValue(metric, out float value) && float.IsFinite(value))
                        passive[metric] = value;
                    else
                        complete = false;
                }
                if (!complete) continue;

                double probe = FitnessOf(new Dictionary<string, float>(record.ActiveParameters), passive);
                double reference = record.Fitness;
                if (!(Math.Abs(probe - reference) <= 1e-5 * Math.Max(1.0, Math.Abs(reference))))
                {
                    SimulationLog.Warning(
                        $"[BAYES] Die Fitness liest mehr als die deklarierten Kennwerte ({string.Join(", ", _fitness.RequiredMetrics)}): "
                        + $"Gegenprobe {probe:G6} statt {reference:G6} bei Iteration {record.Iteration}, Variante {record.Variant}. "
                        + "Modus 'Metrics' ist damit nicht möglich; der Lauf geht mit 'Fitness' weiter.");
                    ActiveMode = SurrogateTargetMode.Fitness;
                    _models.Clear();
                    return;
                }
            }
        }

        /// <summary>Fitness für einen Wegwerf-Datensatz mit der Formel des Projekts. Kommt nie in die History.</summary>
        public double FitnessOf(Dictionary<string, float> active, Dictionary<string, float> passive)
        {
            _scratch.ActiveParameters = active;
            _scratch.PassiveParameters.Clear();
            foreach (var (key, value) in passive) _scratch.PassiveParameters[key] = value;
            _scratch.Fitness = 0f;
            _scratch.SimulationFailed = false;
            _scratch.Disqualified = false;

            _fitness.CalculateFitness(_scratch, _simulationConfig);
            return _scratch.Fitness;
        }
    }
}
