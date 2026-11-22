using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
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

        private const int ExplorerMinimumVariants = 4;

        private readonly IFitnessCalculator _fitness;
        private readonly BayesConfig? _configOverride;
        private readonly BayesTuning _tuning;
        private readonly Random _random;
        private readonly SurrogateModelEnsemble _ensemble;
        private readonly AcquisitionOptimizer _acquisition;
        private readonly BayesDiagnosticsReporter _reporter;

        // Zustand des Laufs (wird beim ersten Aufruf aufgebaut)
        private ParameterSpace? _space;
        private int _initialSamples;
        private bool _warnedEmptyQueue;
        private FittedEnsemble? _lastFit;

        private readonly Queue<BayesProposal> _queue = new();
        private readonly Dictionary<(int Iteration, int Variant), BayesProposal> _issued = new();

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

            Func<ISurrogateModel> resolvedFactory;
            if (modelFactory != null)
            {
                resolvedFactory = modelFactory;
            }
            else
            {
                // Eigener, aus dem Optimierer abgeleiteter Zufallsstrom für die Startpunkte der Hyperparameter.
                var modelRandom = new Random(_random.Next());
                resolvedFactory = () => new GaussianProcess(new Random(modelRandom.Next()));
            }

            _ensemble = new SurrogateModelEnsemble(_fitness, _tuning, resolvedFactory);
            _acquisition = new AcquisitionOptimizer(_tuning, _random, _ensemble);
            _reporter = new BayesDiagnosticsReporter(_ensemble);
        }

        public string Name => "Bayes-Optimierung (Gauß-Prozess, Expected Improvement)";

        /// <summary>Wirksamer Modus nach Auflösung von "Auto" und eventuellem Rückfall (TODO-56).</summary>
        public SurrogateTargetMode ActiveMode => _ensemble.ActiveMode;

        /// <summary>Ist der Vertrauensbereich aktiv (TODO-57)?</summary>
        public bool TrustRegionActive => _acquisition.TrustRegionActive;

        /// <summary>Aktuelle Seitenlänge des Vertrauensbereichs, <c>null</c> wenn aus.</summary>
        public double? TrustRegionLength => _acquisition.TrustRegionLength;

        /// <summary>Wie viele Punkte das Modell beim letzten Lernen gesehen hat.</summary>
        public int LastTrainingPointCount { get; private set; }

        /// <summary>Wie oft die Auswahl auf die Ausweichregel (statt auf die erwartete Verbesserung) zurückfiel. Nur Diagnose.</summary>
        public int FallbackSelections => _acquisition.FallbackSelections;

        /// <summary>Nur Diagnose: Fitness-Vorhersage (Median) des zuletzt gelernten Modells an einem Parameterpunkt.</summary>
        public double? PredictFitnessAt(IReadOnlyDictionary<string, float> parameters)
        {
            if (_lastFit == null || _space == null) return null;
            double[] unit = _space.ToUnit(parameters);
            return _reporter.PredictFitnessBand(_lastFit, unit, _space.FromUnit(unit)).Median;
        }

        /// <summary>Anzahl der Startrechnungen (Versuchsplan) nach Auflösung der Automatik.</summary>
        public int InitialSampleCount => _initialSamples;

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

            _space = space;
            _ensemble.Initialize(ResolveMode(config, _fitness), context.Config);

            int variants = Math.Max(1, context.Config.VariantsPerIteration);
            _initialSamples = config.InitialSamples > 0 ? config.InitialSamples : Math.Max(variants, 2 * space.Dimension + 2);
            _acquisition.InitializeTrustRegion(space.Dimension, variants);

            // Versuchsplan: n0 - 1 Punkte, der erste Punkt ist der Startwert (Iteration 1, Variante 1).
            foreach (double[] point in LatinHypercube.Sample(_initialSamples - 1, space.Dimension, _random))
                _queue.Enqueue(MakeProposal(point, fromModel: false));

            Console.WriteLine(
                $"[BAYES] Modus: {ActiveMode}, {space.Dimension} freie(r) Parameter, "
                + $"{_initialSamples} Startrechnungen (Versuchsplan), "
                + $"Vertrauensbereich: {(TrustRegionActive ? "an" : "aus")}.");
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

            return target switch
            {
                "fitness" => SurrogateTargetMode.Fitness,
                "metrics" when !hasMetrics => throw new InvalidOperationException(
                    "[KONFIGURATION] Bayes.SurrogateTarget = 'Metrics' verlangt, dass die Fitness des Projekts ihre "
                    + "Kennwerte nennt (RequiredMetrics). Sie nennt keine. Entweder 'Fitness' oder 'Auto' eintragen."),
                "metrics" => SurrogateTargetMode.Metrics,
                _ => hasMetrics ? SurrogateTargetMode.Metrics : SurrogateTargetMode.Fitness
            };
        }

        public Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants)
        {
            EnsureInitialized(context);

            if (iteration == 1 && variant == 1)
                return _space!.Clamp(context.CurrentBaseParameters);

            BayesProposal proposal;
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
                BayesDiagnosticsReporter.AttachPrediction(record, _issued);
            }

            return records.OrderByDescending(r => r.Fitness).First();
        }

        public void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord)
        {
            EnsureInitialized(context);

            var leftoverDoe = _queue.ToList();
            _queue.Clear();

            BayesAccuracy? accuracy = BayesDiagnosticsReporter.MeasureAccuracy(currentRecords);
            BayesDiagnosticsReporter.ReportAccuracy(accuracy);

            SurrogateTrainingSet training = SurrogateTrainingSet.Build(context, _space!);
            int usable = training.Records.Count;
            int slots = Math.Max(1, context.Config.VariantsPerIteration);
            int d = _space!.Dimension;

            var proposals = new List<BayesProposal>();
            var deferredDoe = new List<BayesProposal>();
            BayesDiagnostics? diagnostics = null;

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
                    foreach (double[] point in LatinHypercube.Sample(stillMissing, d, _random))
                        proposals.Add(MakeProposal(point, fromModel: false));
            }

            foreach (BayesProposal proposal in proposals) _queue.Enqueue(proposal);
            foreach (BayesProposal proposal in deferredDoe) _queue.Enqueue(proposal);
            _acquisition.LastRoundHadModelPoints = modelUsed;

            int iterationNumber = currentRecords.Count > 0 ? currentRecords[0].Iteration : _reporter.HistoryRowCount + 1;
            double? bestFitness = training.Records.Count > 0 ? training.Records.Max(r => r.Fitness) : null;
            _reporter.AppendHistoryRow(
                context, _space, iterationNumber, modelUsed ? ActiveMode.ToString() : "DoE",
                usable, TrustRegionLength, accuracy, bestFitness, diagnostics);
        }

        private BayesProposal MakeProposal(double[] unit, bool fromModel, bool explorer = false) =>
            new() { Unit = unit, Parameters = _space!.FromUnit(unit), FromModel = fromModel, Explorer = explorer };

        private BayesDiagnostics ProposeFromModel(
            SimulationContext context, SurrogateTrainingSet training, int count, List<BayesProposal> output)
        {
            ParameterSpace space = _space!;
            int d = space.Dimension;
            int variants = Math.Max(1, context.Config.VariantsPerIteration);

            if (ActiveMode == SurrogateTargetMode.Metrics) _ensemble.VerifyMetricsMode(training);
            _acquisition.UpdateTrustRegionCounters(training);

            double[] bestPoint = training.Inputs[BestIndex(training)];
            double[] capCenter = _acquisition.TrustCenter ?? bestPoint;
            List<int> selected = _ensemble.SelectTrainingIndices(training, capCenter);

            FittedEnsemble fit = _ensemble.FitModels(training, selected, d);
            LastTrainingPointCount = selected.Count;
            _lastFit = fit;

            fit.Excluded = training.ExcludedPoints;
            // Erfolgsmodell (Paket B, 08.11.2026): ein eigener Gauß-Prozess auf 1 = gelungen, 0 = abgestürzt
            // (vom Türsteher verworfene Varianten zählen nicht, sie stoßen nur lokal ab). Seine Vorhersage ist eine geglättete Erfolgswahrscheinlichkeit, mit der die
            // Auswahlregel multipliziert wird. Anders als eine feste Abstoßung verallgemeinert es: liegt das
            // Optimum direkt an einer Absturzzone (Strömungsabriss), meidet es die ganze Zone, nicht nur die
            // Umgebung einzelner Abstürze (gemessen: 87 → 65 Abstürze je 100 Rechnungen, besseres Ergebnis).
            _ensemble.FitSuccessModel(training, fit);
            fit.RepulsionRadius = 0.05 * Math.Sqrt(d);

            // Zufallssätze für das Monte-Carlo-Verfahren, einmal je Iteration (ruhiger Vergleich)
            if (fit.Slots.Count > 0 && ActiveMode == SurrogateTargetMode.Metrics)
            {
                fit.Z = new double[SurrogateModelEnsemble.FullSamples][];
                for (int s = 0; s < SurrogateModelEnsemble.FullSamples; s++)
                {
                    fit.Z[s] = new double[fit.Slots.Count];
                    for (int m = 0; m < fit.Slots.Count; m++) fit.Z[s][m] = NormalDistribution.Sample(_random);
                }
            }

            // Vertrauensbereich und Kandidatenpool
            var (lo, hi) = _acquisition.PrepareTrustBounds(fit, training, space, bestPoint);
            double[][] pool = _acquisition.BuildPool(training, lo, hi, _tuning.CandidatePool);

            var chosen = fit.Chosen;
            var known = training.Inputs.ToList();
            for (int slot = 0; slot < count; slot++)
            {
                bool explorer = variants >= ExplorerMinimumVariants && slot == count - 1;
                double[]? x = explorer
                    ? _acquisition.PickExplorer(fit, space, known, chosen, d)
                    : _acquisition.PickByExpectedImprovement(fit, space, pool, known, chosen, lo, hi);

                bool fromFallback = x == null;
                x ??= LatinHypercube.Sample(1, d, _random)[0];

                BayesProposal proposal = MakeProposal(x, fromModel: true, explorer: explorer);
                (proposal.Median, proposal.Low, proposal.High) = _reporter.PredictFitnessBand(fit, x, proposal.Parameters);
                if (fromFallback) proposal.FromModel = false;

                output.Add(proposal);
                chosen.Add(x);
                _acquisition.Believe(fit, x, proposal.Parameters);
            }

            _reporter.LogSummary(fit, selected.Count, space);
            return new BayesDiagnostics { TrainingPoints = selected.Count, Relevance = fit.Relevance };
        }

        private static int BestIndex(SurrogateTrainingSet training)
        {
            int best = 0;
            for (int i = 1; i < training.Records.Count; i++)
                if (training.Records[i].Fitness > training.Records[best].Fitness) best = i;
            return best;
        }
    }
}
