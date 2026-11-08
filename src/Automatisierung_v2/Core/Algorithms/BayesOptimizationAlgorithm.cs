using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Womit das Ersatzmodell arbeitet (TODO-55, TODO-56).
    /// </summary>
    public enum SurrogateTargetMode
    {
        /// <summary>Ein Modell für die Fitness als Ganzes (Idee 1).</summary>
        Fitness,
        /// <summary>Ein Modell je Kennwert aus <see cref="IFitnessCalculator.RequiredMetrics"/>; die Fitness rechnet die Formel des Projekts (Idee 2).</summary>
        Metrics
    }

    /// <summary>
    /// Bayes-Optimierung mit Gauß-Prozess und Expected Improvement (TODO-55 bis TODO-58).
    ///
    /// <para>
    /// <b>Idee in Kürze:</b> Aus den bisher gerechneten Varianten lernt ein Ersatzmodell nicht nur eine
    /// Landkarte der Fitness, sondern auch, wo es sich <i>nicht</i> auskennt (Unsicherheit). Die
    /// Auswahlregel Expected Improvement fragt: "Wo ist die Chance am größten, den bisherigen Besten zu
    /// schlagen?" Das kann nahe am Besten sein (verfeinern) oder im Nebel (erkunden). Jeder Vorschlag
    /// wird danach echt gerechnet; das Modell ersetzt keine einzige echte Rechnung.
    /// </para>
    ///
    /// <para>
    /// Im Modus <see cref="SurrogateTargetMode.Metrics"/> lernt jeder Kennwert (z. B. Drag, Volume) sein
    /// eigenes Modell, und die Fitness-Formel des Projekts verrechnet die geschätzten Kennwerte. Strafen
    /// und Grenzen der Formel wirken damit automatisch richtig. Der Kern kennt weder Formel noch
    /// Metriknamen (F18).
    /// </para>
    ///
    /// <para>
    /// Voraussetzung: für jeden Parameter gibt es Grenzen (<c>ParameterBounds</c>). <c>MaxDeviations</c>
    /// wirkt bei diesem Verfahren nicht. Zustand ändert sich nur in <see cref="PrepareNextIteration"/>
    /// (F19); mit demselben Seed und denselben Ergebnissen macht das Verfahren dieselben Vorschläge,
    /// damit die Wiederaufnahme per Wiedergabe greift (F20).
    /// </para>
    /// </summary>
    public sealed class BayesOptimizationAlgorithm : IOptimizationAlgorithm, IOptimizationPreflight
    {
        // Schlüssel der Vorhersagespalten in Simulation_Results.csv (TODO-58)
        public const string PredictedFitnessKey = "Bayes_PredictedFitness";
        public const string PredictedLowKey = "Bayes_PredictedLow";
        public const string PredictedHighKey = "Bayes_PredictedHigh";
        public const string ExplorerKey = "Bayes_Explorer";

        /// <summary>Name der Verlaufsdatei in <c>Analyseergebnisse/</c>.</summary>
        public const string HistoryFileName = "Bayes_Verlauf.csv";

        private const double MinSeparation = 1e-3;
        private const double ImprovementOffset = 0.01;
        private const int FullSamples = 64;
        private const int ScreenSamples = 8;
        private const int ScreenSurvivors = 200;
        private const int LocalStarts = 10;
        private const int LocalSteps = 30;
        private const int ExplorerMinimumVariants = 4;

        private readonly IFitnessCalculator _fitness;
        private readonly BayesConfig? _configOverride;
        private readonly BayesTuning _tuning;
        private readonly Random _random;
        private readonly Func<ISurrogateModel> _modelFactory;

        // Zustand des Laufs (wird beim ersten Aufruf aufgebaut)
        private BayesConfig _config = new();
        private SimulationConfig _simulationConfig = new();
        private ParameterSpace? _space;
        private int _initialSamples;
        private bool _warnedEmptyQueue;

        private readonly Queue<Proposal> _queue = new();
        private readonly Dictionary<(int Iteration, int Variant), Proposal> _issued = new();
        private readonly Dictionary<string, ISurrogateModel> _models = new();
        private readonly HashSet<ModelRecord> _checkedRecords = new();
        private readonly List<string> _historyRows = new();
        private readonly ModelRecord _scratch = new();

        // Vertrauensbereich
        private TrustRegionState? _trust;
        private double[]? _trustCenter;
        private HashSet<ModelRecord>? _trustSnapshot;
        private double? _trustLastBest;
        private bool _lastRoundHadModelPoints;
        private double[]? _lastRelevance;

        /// <param name="fitness">Bewertet die Modelle; im Modus "Metrics" auch die geschätzten Kennwerte.</param>
        /// <param name="config">Nur für Tests. Im Lauf kommt die Konfiguration aus <c>context.Config.Bayes</c>.</param>
        /// <param name="random">Zufallsquelle, im Lauf aus dem Lauf-Seed (TODO-50). Ohne Angabe echter Zufall.</param>
        /// <param name="modelFactory">Erzeugt die Ersatzmodelle. Vorgabe ist der Gauß-Prozess; in Tests austauschbar.</param>
        /// <param name="tuning">Interne Stellschrauben (nur für Tests), siehe <see cref="BayesTuning"/>.</param>
        public BayesOptimizationAlgorithm(
            IFitnessCalculator fitness,
            BayesConfig? config = null,
            Random? random = null,
            Func<ISurrogateModel>? modelFactory = null,
            BayesTuning? tuning = null)
        {
            _fitness = fitness;
            _configOverride = config;
            _tuning = tuning ?? new BayesTuning();
            _random = random ?? new Random();

            if (modelFactory != null)
            {
                _modelFactory = modelFactory;
            }
            else
            {
                // Eigener, aus dem Optimierer abgeleiteter Zufallsstrom für die Startpunkte der Hyperparameter.
                var modelRandom = new Random(_random.Next());
                _modelFactory = () => new GaussianProcess(new Random(modelRandom.Next()));
            }
        }

        public string Name => "Bayes-Optimierung (Gauß-Prozess, Expected Improvement)";

        /// <summary>Wirksamer Modus nach Auflösung von "Auto" und eventuellem Rückfall (TODO-56).</summary>
        public SurrogateTargetMode ActiveMode { get; private set; } = SurrogateTargetMode.Fitness;

        /// <summary>Ist der Vertrauensbereich aktiv (TODO-57)?</summary>
        public bool TrustRegionActive => _trust != null;

        /// <summary>Aktuelle Seitenlänge des Vertrauensbereichs, <c>null</c> wenn aus.</summary>
        public double? TrustRegionLength => _trust?.Length;

        /// <summary>Wie viele Punkte das Modell beim letzten Lernen gesehen hat.</summary>
        public int LastTrainingPointCount { get; private set; }

        /// <summary>Anzahl der Startrechnungen (Versuchsplan) nach Auflösung der Automatik.</summary>
        public int InitialSampleCount => _initialSamples;

        // ===================================================================================
        // Vorprüfung und Aufbau
        // ===================================================================================

        /// <inheritdoc/>
        public void Preflight(SimulationContext context) => EnsureInitialized(context);

        private void EnsureInitialized(SimulationContext context)
        {
            if (_space != null) return;

            BayesConfig config = _configOverride ?? context.Config.Bayes ?? new BayesConfig();
            ValidateConfig(config);

            ParameterSpace space = ParameterSpace.Create(context);
            if (space.Dimension == 0)
            {
                throw new InvalidOperationException(
                    "[KONFIGURATION] Alle Parameter sind fest (Min == Max in ParameterBounds). Der Bayes-Optimierer "
                    + "hat nichts zu optimieren.");
            }

            SurrogateTargetMode mode = ResolveMode(config, _fitness);

            _config = config;
            _simulationConfig = context.Config;
            _space = space;
            ActiveMode = mode;

            int variants = Math.Max(1, context.Config.VariantsPerIteration);
            _initialSamples = config.InitialSamples > 0
                ? config.InitialSamples
                : Math.Max(variants, 2 * space.Dimension + 2);

            if (TrustRegionState.IsActive(_tuning.TrustRegion, space.Dimension))
                _trust = new TrustRegionState(space.Dimension, variants);

            // Versuchsplan: n0 - 1 Punkte, der erste Punkt ist der Startwert (Iteration 1, Variante 1).
            foreach (double[] point in LatinHypercube.Sample(_initialSamples - 1, space.Dimension, _random))
                _queue.Enqueue(MakeProposal(point, fromModel: false));

            Console.WriteLine(
                $"[BAYES] Modus: {ActiveMode}, {space.Dimension} freie(r) Parameter, "
                + $"{_initialSamples} Startrechnungen (Versuchsplan), "
                + $"Vertrauensbereich: {(_trust != null ? "an" : "aus")}.");
        }

        private static void ValidateConfig(BayesConfig config)
        {
            static bool In(string? value, params string[] allowed) =>
                allowed.Contains((value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

            if (!In(config.SurrogateTarget, "Auto", "Fitness", "Metrics"))
                throw new InvalidOperationException(
                    $"[KONFIGURATION] Bayes.SurrogateTarget '{config.SurrogateTarget}' ist unbekannt. Gültig sind 'Auto', 'Fitness' und 'Metrics'.");
            if (config.InitialSamples < 0)
                throw new InvalidOperationException("[KONFIGURATION] Bayes.InitialSamples darf nicht negativ sein (0 = automatisch).");
        }

        private static SurrogateTargetMode ResolveMode(BayesConfig config, IFitnessCalculator fitness)
        {
            string target = (config.SurrogateTarget ?? "Auto").Trim().ToLowerInvariant();
            bool hasMetrics = fitness.RequiredMetrics.Count > 0;

            switch (target)
            {
                case "fitness":
                    return SurrogateTargetMode.Fitness;

                case "metrics":
                    if (!hasMetrics)
                    {
                        throw new InvalidOperationException(
                            "[KONFIGURATION] Bayes.SurrogateTarget = 'Metrics' verlangt, dass die Fitness des Projekts ihre "
                            + "Kennwerte nennt (RequiredMetrics). Sie nennt keine. Entweder 'Fitness' oder 'Auto' eintragen.");
                    }
                    return SurrogateTargetMode.Metrics;

                default:
                    return hasMetrics ? SurrogateTargetMode.Metrics : SurrogateTargetMode.Fitness;
            }
        }

        // ===================================================================================
        // IOptimizationAlgorithm
        // ===================================================================================

        public Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants)
        {
            EnsureInitialized(context);

            if (iteration == 1 && variant == 1)
                return _space!.Clamp(context.CurrentBaseParameters);

            Proposal proposal;
            if (_queue.Count > 0)
            {
                proposal = _queue.Dequeue();
            }
            else
            {
                if (!_warnedEmptyQueue)
                {
                    _warnedEmptyQueue = true;
                    SimulationLog.Warning("[BAYES] Keine vorbereiteten Vorschläge mehr; nehme einen Zufallspunkt (Versuchsplan).");
                }
                proposal = MakeProposal(LatinHypercube.Sample(1, _space!.Dimension, _random)[0], fromModel: false);
            }

            if (proposal.FromModel) _issued[(iteration, variant)] = proposal;

            return new Dictionary<string, float>(proposal.Parameters);
        }

        public ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context)
        {
            // Nur Fitness und Vorhersagespalten setzen, sonst keinen Zustand verändern (F19):
            // diese Methode läuft auch für einzelne Feinprüf-Datensätze.
            foreach (ModelRecord record in records)
            {
                _fitness.CalculateFitness(record, context.Config);
                AttachPrediction(record);
            }

            return records.OrderByDescending(r => r.Fitness).First();
        }

        public void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord)
        {
            EnsureInitialized(context);

            var leftoverDoe = _queue.ToList();
            _queue.Clear();

            Accuracy? accuracy = MeasureAccuracy(currentRecords);
            ReportAccuracy(accuracy);

            SurrogateTrainingSet training = SurrogateTrainingSet.Build(context, _space!);
            int usable = training.Records.Count;
            int slots = Math.Max(1, context.Config.VariantsPerIteration);
            int d = _space!.Dimension;

            var proposals = new List<Proposal>();
            var deferredDoe = new List<Proposal>();
            Diagnostics? diagnostics = null;

            if (usable < _initialSamples)
            {
                // Versuchsplan fortsetzen. Was über diese Runde hinausgeht, bleibt für die nächsten Runden
                // in der Warteschlange (bis 08.11.2026 ging es hier verloren, wenn der Plan länger als
                // eine Runde reichte).
                proposals.AddRange(leftoverDoe.Take(slots));
                deferredDoe.AddRange(leftoverDoe.Skip(slots));
                int missing = Math.Max(0, _initialSamples - usable);
                int failed = training.ExcludedPoints.Count;
                string note = failed > 0 ? $" ({failed} fehlgeschlagene oder verworfene Rechnung(en) zählen nicht mit)" : "";
                Console.WriteLine($"\n[INFO] {Name}: Aufbau des Ersatzmodells. Benötige noch {missing} Basis-Simulationen (Versuchsplan).{note}");
            }

            int remaining = slots - proposals.Count;
            bool modelUsed = false;

            if (remaining > 0)
            {
                if (usable >= d + 2)
                {
                    try
                    {
                        diagnostics = ProposeFromModel(context, training, remaining, proposals);
                        modelUsed = true;
                    }
                    catch (Exception ex)
                    {
                        // Das Modell bricht nie den Lauf ab (F22): warnen und mit Versuchsplan-Punkten füllen.
                        SimulationLog.Warning($"[BAYES] Das Ersatzmodell konnte nicht gelernt werden ({ex.Message}). "
                            + "Diese Iteration wird mit Zufallspunkten (Versuchsplan) gefüllt.");
                        diagnostics = null;
                    }
                }

                int stillMissing = slots - proposals.Count;
                if (stillMissing > 0)
                {
                    foreach (double[] point in LatinHypercube.Sample(stillMissing, d, _random))
                        proposals.Add(MakeProposal(point, fromModel: false));
                }
            }

            foreach (Proposal proposal in proposals) _queue.Enqueue(proposal);
            foreach (Proposal proposal in deferredDoe) _queue.Enqueue(proposal);
            _lastRoundHadModelPoints = modelUsed;

            int iterationNumber = currentRecords.Count > 0 ? currentRecords[0].Iteration : _historyRows.Count + 1;
            double? bestFitness = training.Records.Count > 0 ? training.Records.Max(r => r.Fitness) : null;
            AppendHistoryRow(context, iterationNumber, modelUsed ? ActiveMode.ToString() : "DoE", usable, accuracy, bestFitness, diagnostics);
        }

        // ===================================================================================
        // Vorschläge aus dem Modell
        // ===================================================================================

        private sealed class Proposal
        {
            public double[] Unit = Array.Empty<double>();
            public Dictionary<string, float> Parameters = new();
            public bool FromModel;
            public bool Explorer;
            public double Median, Low, High;
        }

        private sealed class Slot
        {
            public string Name = "";
            public ISurrogateModel Model = null!;
            public bool Log;
            public double OutputVariance = 1.0;

            public double Forward(double value) => Log ? Math.Log(value) : value;
            public double Back(double value) => Log ? Math.Exp(value) : value;
        }

        private sealed class Fitted
        {
            public List<Slot> Slots = new();
            public bool FitnessLog;
            public double BestTransformed;
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

        private sealed class Diagnostics
        {
            public int TrainingPoints;
            public double[] Relevance = Array.Empty<double>();
        }

        private Proposal MakeProposal(double[] unit, bool fromModel, bool explorer = false)
        {
            return new Proposal
            {
                Unit = unit,
                Parameters = _space!.FromUnit(unit),
                FromModel = fromModel,
                Explorer = explorer
            };
        }

        private Diagnostics ProposeFromModel(SimulationContext context, SurrogateTrainingSet training, int count, List<Proposal> output)
        {
            ParameterSpace space = _space!;
            int d = space.Dimension;
            int variants = Math.Max(1, context.Config.VariantsPerIteration);

            if (ActiveMode == SurrogateTargetMode.Metrics) VerifyMetricsMode(training, context);

            UpdateTrustRegionCounters(training);

            int bestIndex = BestIndex(training);
            double[] bestPoint = training.Inputs[bestIndex];
            double[] capCenter = _trustCenter ?? bestPoint;
            List<int> selected = SelectTrainingIndices(training, capCenter);

            Fitted fit = FitModels(training, selected);
            LastTrainingPointCount = selected.Count;
            _lastRelevance = fit.Relevance;

            fit.Excluded = training.ExcludedPoints;
            // Erfolgsmodell (Paket B, 08.11.2026): ein eigener Gauß-Prozess auf 1 = gelungen, 0 = abgestürzt
            // (vom Türsteher verworfene Varianten zählen nicht, sie stoßen nur lokal ab). Seine Vorhersage ist eine geglättete Erfolgswahrscheinlichkeit, mit der die
            // Auswahlregel multipliziert wird. Anders als eine feste Abstoßung verallgemeinert es: liegt das
            // Optimum direkt an einer Absturzzone (Strömungsabriss), meidet es die ganze Zone, nicht nur die
            // Umgebung einzelner Abstürze (gemessen: 87 → 65 Abstürze je 100 Rechnungen, besseres Ergebnis).
            if (training.FailedPoints.Count > 0)
            {
                var inputs = training.Inputs.Concat(training.FailedPoints).ToList();
                var labels = training.Inputs.Select(_ => 1.0).Concat(training.FailedPoints.Select(_ => 0.0)).ToList();
                fit.SuccessModel = ModelFor("__Erfolg");
                fit.SuccessModel.Fit(inputs, labels);
            }
            fit.RepulsionRadius = 0.05 * Math.Sqrt(d);

            // Zufallssätze für das Monte-Carlo-Verfahren, einmal je Iteration (ruhiger Vergleich)
            if (fit.Slots.Count > 0 && ActiveMode == SurrogateTargetMode.Metrics)
            {
                fit.Z = new double[FullSamples][];
                for (int s = 0; s < FullSamples; s++)
                {
                    fit.Z[s] = new double[fit.Slots.Count];
                    for (int m = 0; m < fit.Slots.Count; m++) fit.Z[s][m] = NormalDistribution.Sample(_random);
                }
            }

            // Vertrauensbereich
            double[] lo = new double[d];
            double[] hi = Enumerable.Repeat(1.0, d).ToArray();
            if (_trust != null)
            {
                if (_trust.NeedsRestart)
                {
                    Console.WriteLine("[BAYES] Vertrauensbereich ausgereizt, erkunde neues Gebiet.");
                    _trustCenter = FindRestartCenter(fit, space);
                    _trust.Restart();
                    _trustSnapshot = new HashSet<ModelRecord>(training.Records);
                    _trustLastBest = null;
                }
                else
                {
                    var (regionalCenter, _) = RegionalBest(training);
                    if (regionalCenter != null) _trustCenter = regionalCenter;
                }

                double[] center = _trustCenter ?? bestPoint;
                (lo, hi) = TrustBox(center, _trust.Length, fit.Relevance);
                Console.WriteLine($"[BAYES] Vertrauensbereich: L = {_trust.Length:F3} (Erfolge {_trust.Successes}, Misserfolge {_trust.Failures}/{_trust.FailureTolerance}).");
            }

            // Kandidatenpool: halb gleichverteilt im Bereich, halb gaußsche Streuung um die besten Rechnungen
            double[][] pool = BuildPool(training, lo, hi, _tuning.CandidatePool);

            var chosen = fit.Chosen;
            var known = training.Inputs.ToList();

            for (int slot = 0; slot < count; slot++)
            {
                bool explorer = variants >= ExplorerMinimumVariants && slot == count - 1;
                double[]? x = explorer
                    ? PickExplorer(fit, known, chosen, d)
                    : PickByExpectedImprovement(fit, pool, known, chosen, lo, hi);

                bool fromFallback = x == null;
                x ??= LatinHypercube.Sample(1, d, _random)[0];

                Proposal proposal = MakeProposal(x, fromModel: true, explorer: explorer);
                (proposal.Median, proposal.Low, proposal.High) = PredictFitnessBand(fit, x, proposal.Parameters);
                if (fromFallback) proposal.FromModel = false;

                output.Add(proposal);
                chosen.Add(x);
                Believe(fit, x, proposal.Parameters);
            }

            LogSummary(fit, selected.Count, space);

            return new Diagnostics { TrainingPoints = selected.Count, Relevance = fit.Relevance };
        }

        private static int BestIndex(SurrogateTrainingSet training)
        {
            int best = 0;
            for (int i = 1; i < training.Records.Count; i++)
                if (training.Records[i].Fitness > training.Records[best].Fitness) best = i;
            return best;
        }

        // -------------------------------------------------------------------- Lernmenge

        /// <summary>
        /// Gibt es mehr nutzbare Rechnungen als <c>MaxTrainingPoints</c>, bleiben die besten 10 % des
        /// Deckels und die dem Zentrum nächsten (mit den Längenskalen gewichtet).
        /// </summary>
        private List<int> SelectTrainingIndices(SurrogateTrainingSet training, double[] center)
        {
            int n = training.Records.Count;
            int cap = _tuning.MaxTrainingPoints;
            if (n <= cap) return Enumerable.Range(0, n).ToList();

            int keepBest = Math.Max(1, (int)Math.Ceiling(0.1 * cap));
            var byFitness = Enumerable.Range(0, n).OrderByDescending(i => training.Records[i].Fitness).ThenBy(i => i).ToList();
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

        // -------------------------------------------------------------------- Modelle lernen

        private ISurrogateModel ModelFor(string name)
        {
            if (!_models.TryGetValue(name, out ISurrogateModel? model))
            {
                model = _modelFactory();
                _models[name] = model;
            }
            return model;
        }

        private Fitted FitModels(SurrogateTrainingSet training, List<int> selected)
        {
            var fit = new Fitted();
            double bestFitness = training.Records.Max(r => r.Fitness);
            bool allPositive = training.Records.All(r => r.Fitness > 0);
            fit.FitnessLog = allPositive;
            fit.BestTransformed = fit.T(bestFitness);

            if (ActiveMode == SurrogateTargetMode.Fitness)
            {
                var inputs = selected.Select(i => training.Inputs[i]).ToList();
                var slot = new Slot { Name = "Fitness", Log = allPositive };
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
                    var slot = new Slot { Name = metric, Log = values.All(v => v > 0) };
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

            fit.Relevance = CombineRelevance(fit);
            return fit;
        }

        private static double Variance(IReadOnlyList<double> values)
        {
            double mean = values.Average();
            double variance = values.Sum(v => (v - mean) * (v - mean)) / values.Count;
            return variance > 1e-18 ? variance : 1.0;
        }

        private double[] CombineRelevance(Fitted fit)
        {
            int d = _space!.Dimension;
            var sums = new double[d];
            int models = 0;

            foreach (Slot slot in fit.Slots)
            {
                IReadOnlyList<double>? relevance = slot.Model.ParameterRelevance;
                for (int i = 0; i < d; i++) sums[i] += relevance != null && relevance.Count == d ? relevance[i] : 1.0;
                models++;
            }

            if (models == 0) return Enumerable.Repeat(1.0, d).ToArray();
            for (int i = 0; i < d; i++) sums[i] /= models;
            return sums;
        }

        // -------------------------------------------------------------------- Gegenprobe der Fitness (Modus Metrics)

        /// <summary>
        /// Prüft für jede neue Rechnung, ob die Fitness allein aus den deklarierten Kennwerten und den
        /// Parametern folgt. Wenn nicht, liest die Formel offenbar weitere Kennwerte: dann fällt der Rest
        /// des Laufs auf den Modus "Fitness" zurück. Robustheit geht vor Strenge.
        /// </summary>
        private void VerifyMetricsMode(SurrogateTrainingSet training, SimulationContext context)
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
        private double FitnessOf(Dictionary<string, float> active, Dictionary<string, float> passive)
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

        // -------------------------------------------------------------------- Vertrauensbereich

        private (double[]? Center, double Best) RegionalBest(SurrogateTrainingSet training)
        {
            int best = -1;
            for (int i = 0; i < training.Records.Count; i++)
            {
                if (_trustSnapshot != null && _trustSnapshot.Contains(training.Records[i])) continue;
                if (best < 0 || training.Records[i].Fitness > training.Records[best].Fitness) best = i;
            }

            if (best < 0) return (null, double.NegativeInfinity);
            return (training.Inputs[best], training.Records[best].Fitness);
        }

        private void UpdateTrustRegionCounters(SurrogateTrainingSet training)
        {
            if (_trust == null) return;

            var (_, best) = RegionalBest(training);
            if (double.IsNegativeInfinity(best)) return;

            if (_trustLastBest.HasValue && _lastRoundHadModelPoints)
            {
                double previous = _trustLastBest.Value;
                bool success = best > previous + 1e-3 * Math.Max(Math.Abs(previous), 1e-12);
                _trust.Register(success);
            }

            _trustLastBest = best;
        }

        private (double[] Lo, double[] Hi) TrustBox(double[] center, double length, double[] relevance)
        {
            int d = center.Length;
            var logWeights = new double[d];
            for (int i = 0; i < d; i++) logWeights[i] = -Math.Log(Math.Max(relevance[i], 1e-6));
            double mean = logWeights.Average();

            var lo = new double[d];
            var hi = new double[d];
            for (int i = 0; i < d; i++)
            {
                double w = Math.Clamp(Math.Exp(logWeights[i] - mean), 0.05, 20.0);
                double half = 0.5 * length * w;
                lo[i] = Math.Max(0.0, center[i] - half);
                hi[i] = Math.Min(1.0, center[i] + half);
            }
            return (lo, hi);
        }

        /// <summary>Neues Zentrum: der aussichtsreichste Punkt in einem gleichverteilten Pool über den ganzen Raum.</summary>
        private double[] FindRestartCenter(Fitted fit, ParameterSpace space)
        {
            int d = space.Dimension;
            double[][] pool = UniformPool(Math.Max(500, _tuning.CandidatePool / 5), d, _random);

            double[] values;
            if (ActiveMode == SurrogateTargetMode.Fitness)
            {
                values = new double[pool.Length];
                Slot slot = fit.Slots[0];
                for (int i = 0; i < pool.Length; i++)
                {
                    var (mean, variance) = slot.Model.Predict(pool[i]);
                    values[i] = mean + 2.0 * Math.Sqrt(variance);
                }
            }
            else
            {
                values = Score(fit, pool, ScreenSamples).Ei;
            }

            int best = 0;
            for (int i = 1; i < values.Length; i++) if (values[i] > values[best]) best = i;
            return pool[best];
        }

        // -------------------------------------------------------------------- Kandidaten

        private double[][] BuildPool(SurrogateTrainingSet training, double[] lo, double[] hi, int size)
        {
            int d = lo.Length;
            var pool = new double[size][];
            int uniform = size / 2;

            for (int i = 0; i < uniform; i++)
            {
                var p = new double[d];
                for (int k = 0; k < d; k++) p[k] = lo[k] + _random.NextDouble() * (hi[k] - lo[k]);
                pool[i] = p;
            }

            var bestPoints = Enumerable.Range(0, training.Records.Count)
                .OrderByDescending(i => training.Records[i].Fitness)
                .ThenBy(i => i)
                .Take(5)
                .Select(i => training.Inputs[i])
                .ToList();

            for (int i = uniform; i < size; i++)
            {
                double[] anchor = bestPoints[(i - uniform) % bestPoints.Count];
                var p = new double[d];
                for (int k = 0; k < d; k++)
                    p[k] = Math.Clamp(anchor[k] + 0.1 * NormalDistribution.Sample(_random), lo[k], hi[k]);
                pool[i] = p;
            }

            return pool;
        }

        private static double[][] UniformPool(int size, int d, Random random)
        {
            var pool = new double[size][];
            for (int i = 0; i < size; i++)
            {
                var p = new double[d];
                for (int k = 0; k < d; k++) p[k] = random.NextDouble();
                pool[i] = p;
            }
            return pool;
        }

        private static bool IsTooClose(double[] x, IEnumerable<double[]> others)
        {
            foreach (double[] other in others)
            {
                double sum = 0.0;
                for (int k = 0; k < x.Length; k++)
                {
                    double diff = x[k] - other[k];
                    sum += diff * diff;
                }
                if (sum < MinSeparation * MinSeparation) return true;
            }
            return false;
        }

        private double[]? PickByExpectedImprovement(
            Fitted fit, double[][] pool, List<double[]> known, List<double[]> chosen, double[] lo, double[] hi)
        {
            bool monteCarlo = ActiveMode == SurrogateTargetMode.Metrics;

            // Stufe 1: ganzer Pool (im Modus "Metrics" mit wenigen Zufallssätzen vorsortiert)
            var (ei, unc) = Score(fit, pool, monteCarlo ? ScreenSamples : FullSamples);
            var order = Enumerable.Range(0, pool.Length).OrderByDescending(i => ei[i]).ThenBy(i => i).ToList();

            var candidates = new List<double[]>();
            var scores = new List<double>();

            if (monteCarlo)
            {
                var survivors = order.Take(ScreenSurvivors).Select(i => pool[i]).ToList();
                var refined = Score(fit, survivors, FullSamples);
                candidates.AddRange(survivors);
                scores.AddRange(refined.Ei);
            }
            else
            {
                candidates.AddRange(pool);
                scores.AddRange(ei);
            }

            // Lokale Nachschärfung der besten Kandidaten
            var top = Enumerable.Range(0, candidates.Count).OrderByDescending(i => scores[i]).ThenBy(i => i).Take(LocalStarts).ToList();
            foreach (int start in top)
            {
                double[] x = (double[])candidates[start].Clone();
                double score = scores[start];
                double sigma = 0.05;

                for (int step = 0; step < LocalSteps; step++)
                {
                    var trial = new double[x.Length];
                    for (int k = 0; k < x.Length; k++)
                        trial[k] = Math.Clamp(x[k] + sigma * (hi[k] - lo[k]) * NormalDistribution.Sample(_random), lo[k], hi[k]);

                    double trialScore = Score(fit, new[] { trial }, FullSamples).Ei[0];
                    if (trialScore > score)
                    {
                        x = trial;
                        score = trialScore;
                    }
                    else
                    {
                        sigma *= 0.5;
                    }
                }

                candidates.Add(x);
                scores.Add(score);
            }

            // Bester Kandidat, der nicht auf einer Rechnung oder einem anderen Vorschlag liegt.
            // Ist der Erwartungsgewinn überall null, entscheidet die Unsicherheit (erkunden).
            bool allZero = scores.All(s => !(s > 0.0));
            IEnumerable<int> ranking;
            if (allZero)
            {
                ranking = Enumerable.Range(0, pool.Length).OrderByDescending(i => unc[i]).ThenBy(i => i);
                return ranking.Select(i => pool[i]).FirstOrDefault(p => !IsTooClose(p, known) && !IsTooClose(p, chosen));
            }

            ranking = Enumerable.Range(0, candidates.Count).OrderByDescending(i => scores[i]).ThenBy(i => i);
            return ranking.Select(i => candidates[i]).FirstOrDefault(p => !IsTooClose(p, known) && !IsTooClose(p, chosen));
        }

        /// <summary>Der Erkunder: größte Unsicherheit im ganzen Raum (Sicherheitsnetz gegen ein selbstsicheres, falsches Modell).</summary>
        private double[]? PickExplorer(Fitted fit, List<double[]> known, List<double[]> chosen, int d)
        {
            double[][] pool = UniformPool(Math.Max(500, _tuning.CandidatePool / 2), d, _random);
            var (_, unc) = Score(fit, pool, 1);
            return Enumerable.Range(0, pool.Length)
                .OrderByDescending(i => unc[i])
                .ThenBy(i => i)
                .Select(i => pool[i])
                .FirstOrDefault(p => !IsTooClose(p, known) && !IsTooClose(p, chosen));
        }

        // -------------------------------------------------------------------- Bewertung

        /// <summary>
        /// Erwartete Verbesserung und Unsicherheit für viele Kandidaten. Beides ist mit der Abstoßung von
        /// fehlgeschlagenen Orten multipliziert.
        /// </summary>
        private (double[] Ei, double[] Unc) Score(Fitted fit, IReadOnlyList<double[]> points, int samples)
        {
            int n = points.Count;
            int m = fit.Slots.Count;

            var mean = new double[m][];
            var variance = new double[m][];
            for (int s = 0; s < m; s++)
            {
                mean[s] = new double[n];
                variance[s] = new double[n];
            }

            void Predict(int i)
            {
                for (int s = 0; s < m; s++)
                {
                    var (mu, v) = fit.Slots[s].Model.Predict(points[i]);
                    mean[s][i] = mu;
                    variance[s][i] = v;
                }
            }

            if (n >= 64) Parallel.For(0, n, Predict);
            else for (int i = 0; i < n; i++) Predict(i);

            var ei = new double[n];
            var unc = new double[n];

            for (int i = 0; i < n; i++)
            {
                double u = 0.0;
                for (int s = 0; s < m; s++) u += variance[s][i] / fit.Slots[s].OutputVariance;
                unc[i] = u;

                if (ActiveMode == SurrogateTargetMode.Fitness)
                {
                    double sd = Math.Sqrt(variance[0][i]);
                    double improvement = mean[0][i] - fit.BestTransformed - fit.Offset;
                    if (sd < 1e-12)
                    {
                        ei[i] = Math.Max(improvement, 0.0);
                    }
                    else
                    {
                        double z = improvement / sd;
                        ei[i] = improvement * NormalDistribution.Cdf(z) + sd * NormalDistribution.Pdf(z);
                    }
                }
                else
                {
                    ei[i] = MonteCarloImprovement(fit, points[i], mean, variance, i, samples);
                }

                double repulsion = Repulsion(fit, points[i]);
                ei[i] *= repulsion;
                unc[i] *= repulsion;
            }

            return (ei, unc);
        }

        private double MonteCarloImprovement(Fitted fit, double[] point, double[][] mean, double[][] variance, int index, int samples)
        {
            Dictionary<string, float> active = _space!.FromUnit(point);
            var passive = new Dictionary<string, float>();
            double sum = 0.0;
            int used = Math.Min(samples, fit.Z.Length);

            for (int s = 0; s < used; s++)
            {
                passive.Clear();
                bool valid = true;
                for (int k = 0; k < fit.Slots.Count; k++)
                {
                    double t = mean[k][index] + Math.Sqrt(variance[k][index]) * fit.Z[s][k];
                    double value = fit.Slots[k].Back(t);
                    if (!double.IsFinite(value) || !float.IsFinite((float)value)) { valid = false; break; }
                    passive[fit.Slots[k].Name] = (float)value;
                }
                if (!valid) continue;

                double f = FitnessOf(active, passive);
                if (!double.IsFinite(f) || (fit.FitnessLog && f <= 0.0)) continue;

                double improvement = fit.T(f) - fit.BestTransformed;
                if (improvement > 0.0) sum += improvement;
            }

            return used > 0 ? sum / used : 0.0;
        }

        /// <summary>
        /// Dämpfung der Auswahlregel (Faktor 0..1): Erfolgswahrscheinlichkeit aus dem Erfolgsmodell,
        /// dazu die harte Abstoßung um fehlgeschlagene und verworfene Orte (Radius <c>0,05·√d</c>), die bereits gewählten Vorschläge derselben Runde mit dem halben Radius (höchstens 0,05, sonst wirkt er bei vielen Parametern zu stark):
        /// Kriging Believer allein reicht nicht, wenn das Modell kaum Streuung hat (gemessen am
        /// 08.11.2026: vier Vorschläge einer Runde lagen fast aufeinander).
        /// </summary>
        private static double Repulsion(Fitted fit, double[] x)
        {
            double factor = 1.0;
            if (fit.SuccessModel != null)
                factor *= Math.Clamp(fit.SuccessModel.Predict(x).Mean, 0.0, 1.0);
            factor *= RepulsionFrom(fit.Excluded, x, fit.RepulsionRadius);
            factor *= RepulsionFrom(fit.Chosen, x, Math.Min(0.5 * fit.RepulsionRadius, 0.05));
            return factor;
        }

        private static double RepulsionFrom(IReadOnlyList<double[]> points, double[] x, double radius)
        {
            if (points.Count == 0) return 1.0;

            double r2 = 2.0 * radius * radius;
            double factor = 1.0;
            foreach (double[] e in points)
            {
                double sum = 0.0;
                for (int k = 0; k < x.Length; k++)
                {
                    double diff = x[k] - e[k];
                    sum += diff * diff;
                }
                factor *= 1.0 - Math.Exp(-sum / r2);
            }
            return factor;
        }

        // -------------------------------------------------------------------- Vorhersage und Kriging Believer

        private (double Median, double Low, double High) PredictFitnessBand(Fitted fit, double[] x, Dictionary<string, float> parameters)
        {
            if (ActiveMode == SurrogateTargetMode.Fitness)
            {
                Slot slot = fit.Slots[0];
                var (mean, variance) = slot.Model.Predict(x);
                double sd = Math.Sqrt(variance);
                return (slot.Back(mean), slot.Back(mean - 1.96 * sd), slot.Back(mean + 1.96 * sd));
            }

            var predictions = fit.Slots.Select(s => s.Model.Predict(x)).ToArray();

            double Evaluate(Func<int, double> z)
            {
                var passive = new Dictionary<string, float>();
                for (int k = 0; k < fit.Slots.Count; k++)
                {
                    double value = fit.Slots[k].Back(predictions[k].Mean + Math.Sqrt(predictions[k].Variance) * z(k));
                    if (!float.IsFinite((float)value)) return double.NaN;
                    passive[fit.Slots[k].Name] = (float)value;
                }
                return FitnessOf(parameters, passive);
            }

            double median = Evaluate(_ => 0.0);
            var samples = new List<double>();
            for (int s = 0; s < fit.Z.Length; s++)
            {
                int sample = s;
                double f = Evaluate(k => fit.Z[sample][k]);
                if (double.IsFinite(f)) samples.Add(f);
            }

            if (samples.Count == 0) return (median, median, median);
            samples.Sort();
            double low = samples[(int)Math.Floor(0.025 * (samples.Count - 1))];
            double high = samples[(int)Math.Ceiling(0.975 * (samples.Count - 1))];
            return (median, Math.Min(low, median), Math.Max(high, median));
        }

        /// <summary>Kriging Believer: der Vorschlag gilt als gerechnet, mit der vorhergesagten Mitte als Ergebnis.</summary>
        private void Believe(Fitted fit, double[] x, Dictionary<string, float> parameters)
        {
            var passive = new Dictionary<string, float>();
            var means = new double[fit.Slots.Count];

            for (int k = 0; k < fit.Slots.Count; k++)
            {
                means[k] = fit.Slots[k].Model.Predict(x).Mean;
                fit.Slots[k].Model.AddPseudoObservation(x, means[k]);
                passive[fit.Slots[k].Name] = (float)fit.Slots[k].Back(means[k]);
            }

            double believed;
            if (ActiveMode == SurrogateTargetMode.Fitness)
            {
                believed = means[0];
            }
            else
            {
                double f = FitnessOf(parameters, passive);
                if (!double.IsFinite(f) || (fit.FitnessLog && f <= 0.0)) return;
                believed = fit.T(f);
            }

            if (believed > fit.BestTransformed) fit.BestTransformed = believed;
        }

        // ===================================================================================
        // Transparenz (TODO-58)
        // ===================================================================================

        private void AttachPrediction(ModelRecord record)
        {
            if (!_issued.TryGetValue((record.Iteration, record.Variant), out Proposal? proposal)) return;
            if (!SameParameters(record.ActiveParameters, proposal.Parameters)) return;

            record.PassiveParameters[PredictedFitnessKey] = (float)proposal.Median;
            record.PassiveParameters[PredictedLowKey] = (float)proposal.Low;
            record.PassiveParameters[PredictedHighKey] = (float)proposal.High;
            record.PassiveParameters[ExplorerKey] = proposal.Explorer ? 1f : 0f;
        }

        private static bool SameParameters(Dictionary<string, float> a, Dictionary<string, float> b)
        {
            if (a.Count != b.Count) return false;
            foreach (var (key, value) in a)
            {
                if (!b.TryGetValue(key, out float other)) return false;
                if (Math.Abs(value - other) > 1e-6f * Math.Max(1f, Math.Abs(value))) return false;
            }
            return true;
        }

        private sealed record Accuracy(double MeanDeviation, int InBand, int Count)
        {
            public double Share => Count > 0 ? (double)InBand / Count : 0.0;
        }

        /// <summary>
        /// Treffsicherheit der eben beendeten Runde: mittlere relative Abweichung zwischen Vorhersage und
        /// echter Fitness und Anteil der Varianten im 95-%-Band. Nur Modellvorschläge zählen.
        /// </summary>
        private static Accuracy? MeasureAccuracy(IEnumerable<ModelRecord> records)
        {
            double deviationSum = 0.0;
            int inBand = 0, count = 0;

            foreach (ModelRecord r in records)
            {
                if (r.SimulationFailed || r.Disqualified) continue;
                if (!r.PassiveParameters.TryGetValue(PredictedFitnessKey, out float predicted)) continue;
                r.PassiveParameters.TryGetValue(PredictedLowKey, out float low);
                r.PassiveParameters.TryGetValue(PredictedHighKey, out float high);

                double actual = r.Fitness;
                deviationSum += Math.Abs(predicted - actual) / Math.Max(Math.Abs(actual), 1e-6);
                if (actual >= low && actual <= high) inBand++;
                count++;
            }

            return count == 0 ? null : new Accuracy(deviationSum / count, inBand, count);
        }

        private static void ReportAccuracy(Accuracy? accuracy)
        {
            if (accuracy == null) return;

            Console.WriteLine(
                $"[BAYES] Treffsicherheit der letzten Runde: Ø Abweichung {accuracy.MeanDeviation * 100:F0} %, "
                + $"{accuracy.InBand} von {accuracy.Count} im Erwartungsband.");

            if (accuracy.Share < 0.5)
                SimulationLog.Warning("[BAYES] Modell noch unsicher oder Ergebnisse stark verrauscht.");
        }

        private void LogSummary(Fitted fit, int trainingPoints, ParameterSpace space)
        {
            Console.WriteLine($"[BAYES] Ersatzmodell gelernt aus {trainingPoints} Rechnungen (Modus {ActiveMode}).");

            foreach (Slot slot in fit.Slots)
            {
                IReadOnlyList<double>? relevance = slot.Model.ParameterRelevance;
                if (relevance == null || relevance.Count != space.Dimension) continue;

                var ranked = Enumerable.Range(0, space.Dimension)
                    .OrderByDescending(i => relevance[i])
                    .ThenBy(i => i)
                    .ToList();

                var parts = ranked.Take(8).Select(i => $"{space.FreeNames[i]} {Bar(relevance[i])}").ToList();
                if (ranked.Count > 8) parts.Add("…");
                Console.WriteLine($"[BAYES]   {slot.Name}: {string.Join("  ", parts)}");
            }
        }

        private static string Bar(double relevance)
        {
            int blocks = (int)Math.Round(Math.Clamp(relevance, 0.0, 1.0) * 10.0);
            return blocks <= 0 ? "▏" : new string('█', blocks);
        }

        private void AppendHistoryRow(
            SimulationContext context, int iteration, string mode, int usable, Accuracy? accuracy, double? bestFitness, Diagnostics? diagnostics)
        {
            var inv = CultureInfo.InvariantCulture;
            var row = new StringBuilder();
            row.Append(iteration.ToString(inv)).Append(';');
            row.Append(mode).Append(';');
            row.Append((diagnostics?.TrainingPoints ?? usable).ToString(inv)).Append(';');
            row.Append(_trust != null ? _trust.Length.ToString("F4", inv) : string.Empty).Append(';');
            row.Append(accuracy != null ? accuracy.MeanDeviation.ToString("F4", inv) : string.Empty).Append(';');
            row.Append(accuracy != null ? accuracy.Share.ToString("F3", inv) : string.Empty).Append(';');
            row.Append(bestFitness.HasValue ? bestFitness.Value.ToString("G6", inv) : string.Empty);

            for (int i = 0; i < _space!.Dimension; i++)
            {
                row.Append(';');
                if (diagnostics != null && diagnostics.Relevance.Length == _space.Dimension)
                    row.Append(diagnostics.Relevance[i].ToString("F4", inv));
            }

            _historyRows.Add(row.ToString());

            try
            {
                string directory = Path.Combine(context.WorkingDirectory, "Analyseergebnisse");
                Directory.CreateDirectory(directory);

                var header = "Iteration;Modus;Lernpunkte;Vertrauensbereich_L;Abweichung_Mittel;Anteil_im_Band;BesteFitness;"
                    + string.Join(";", _space.FreeNames.Select(n => "Relevanz_" + n));
                header = header.TrimEnd(';');

                var text = new StringBuilder();
                text.AppendLine(header);
                foreach (string line in _historyRows) text.AppendLine(line);

                File.WriteAllText(Path.Combine(directory, HistoryFileName), text.ToString(), Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                SimulationLog.Warning($"[BAYES] {HistoryFileName} konnte nicht geschrieben werden: {ex.Message}");
            }
        }
    }
}
