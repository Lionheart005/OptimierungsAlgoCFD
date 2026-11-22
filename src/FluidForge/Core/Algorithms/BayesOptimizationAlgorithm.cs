using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Target mode for the surrogate model.
    /// </summary>
    public enum SurrogateTargetMode
    {
        /// <summary>A single surrogate model for overall fitness.</summary>
        Fitness,
        /// <summary>One surrogate model per metric in <see cref="IFitnessCalculator.RequiredMetrics"/>; fitness is computed by the project formula.</summary>
        Metrics
    }

    /// <summary>
    /// Bayesian optimization using Gaussian Processes and Expected Improvement.
    ///
    /// <para>
    /// <b>Concept:</b> From previously evaluated variants, a surrogate model learns both a
    /// fitness landscape and its own uncertainty. Expected Improvement asks: "Where is the
    /// greatest chance of beating the current best?" This balances local refinement near the
    /// best known point with exploration of uncertain regions. Every proposal is subsequently
    /// evaluated by the real solver; the surrogate never replaces an actual simulation.
    /// </para>
    ///
    /// <para>
    /// In <see cref="SurrogateTargetMode.Metrics"/> mode, each metric (e.g., Drag, Volume) has its
    /// own surrogate model, and the project's fitness formula evaluates the predicted metrics.
    /// Penalties and bounds of the formula take effect naturally without the core knowing metric names.
    /// </para>
    ///
    /// <para>
    /// Prerequisite: every parameter must have bounds (<c>ParameterBounds</c>). <c>MaxDeviations</c>
    /// does not apply here. State changes only in <see cref="PrepareNextIteration"/>; with the
    /// same seed and identical outcomes, proposals are deterministic for replay-based resume.
    /// </para>
    /// </summary>
    public sealed class BayesOptimizationAlgorithm : IOptimizationAlgorithm, IOptimizationPreflight
    {
        // Prediction column keys in results.csv
        public const string PredictedFitnessKey = "Bayes_PredictedFitness";
        public const string PredictedLowKey = "Bayes_PredictedLow";
        public const string PredictedHighKey = "Bayes_PredictedHigh";
        public const string ExplorerKey = "Bayes_Explorer";

        /// <summary>History filename inside <c>analysis/</c>.</summary>
        public const string HistoryFileName = OutputConventions.BayesHistoryCsvFileName;

        private const int ExplorerMinimumVariants = 4;

        private readonly IFitnessCalculator _fitness;
        private readonly BayesConfig? _configOverride;
        private readonly BayesTuning _tuning;
        private readonly Random _random;
        private readonly SurrogateModelEnsemble _ensemble;
        private readonly AcquisitionOptimizer _acquisition;
        private readonly BayesDiagnosticsReporter _reporter;

        // Run state (initialized on first call)
        private ParameterSpace? _space;
        private int _initialSamples;
        private bool _warnedEmptyQueue;
        private FittedEnsemble? _lastFit;

        private readonly Queue<BayesProposal> _queue = new();
        private readonly Dictionary<(int Iteration, int Variant), BayesProposal> _issued = new();

        /// <param name="fitness">Evaluates models; in "Metrics" mode also evaluates predicted metrics.</param>
        /// <param name="config">For tests only. At runtime, config comes from <c>context.Config.Bayes</c>.</param>
        /// <param name="random">Random source, derived from the run seed at runtime. Defaults to unseeded.</param>
        /// <param name="modelFactory">Creates surrogate models. Defaults to Gaussian Process; replaceable in tests.</param>
        /// <param name="tuning">Internal tuning parameters (for tests), see <see cref="BayesTuning"/>.</param>
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
                // Dedicated random stream derived from the optimizer for hyperparameter multi-starts.
                var modelRandom = new Random(_random.Next());
                resolvedFactory = () => new GaussianProcess(new Random(modelRandom.Next()));
            }

            _ensemble = new SurrogateModelEnsemble(_fitness, _tuning, resolvedFactory);
            _acquisition = new AcquisitionOptimizer(_tuning, _random, _ensemble);
            _reporter = new BayesDiagnosticsReporter(_ensemble);
        }

        public string Name => "Bayesian Optimization (Gaussian Process, Expected Improvement)";

        /// <summary>Effective mode after resolving "Auto" and any fallback.</summary>
        public SurrogateTargetMode ActiveMode => _ensemble.ActiveMode;

        /// <summary>Whether the trust region is active.</summary>
        public bool TrustRegionActive => _acquisition.TrustRegionActive;

        /// <summary>Current trust region side length, or <c>null</c> if disabled.</summary>
        public double? TrustRegionLength => _acquisition.TrustRegionLength;

        /// <summary>Number of training points seen during the last fit.</summary>
        public int LastTrainingPointCount { get; private set; }

        /// <summary>Number of times selection fell back to predicted fitness instead of EI. Diagnostics only.</summary>
        public int FallbackSelections => _acquisition.FallbackSelections;

        /// <summary>Diagnostics only: median fitness prediction of the last fitted model at a parameter point.</summary>
        public double? PredictFitnessAt(IReadOnlyDictionary<string, float> parameters)
        {
            if (_lastFit == null || _space == null || _lastFit.Slots.Count == 0) return null;
            double[] unit = _space.ToUnit(parameters);
            double median = _reporter.PredictFitnessBand(_lastFit, unit, _space.FromUnit(unit)).Median;
            return double.IsFinite(median) ? median : null;
        }

        /// <summary>Number of initial DoE evaluations after resolving auto-sizing.</summary>
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
                    $"{OutputConventions.ConfigTag} All parameters are fixed (Min == Max in ParameterBounds). "
                    + "Bayesian optimization has nothing to optimize.");
            }

            _space = space;
            _ensemble.Initialize(ResolveMode(config, _fitness), context.Config);

            int variants = Math.Max(1, context.Config.VariantsPerIteration);
            _initialSamples = config.InitialSamples > 0 ? config.InitialSamples : Math.Max(variants, 2 * space.Dimension + 2);
            _acquisition.InitializeTrustRegion(space.Dimension, variants);

            // Initial DoE: n0 - 1 points, as the first point is the base parameter set (Iteration 1, Variant 1).
            foreach (double[] point in LatinHypercube.Sample(_initialSamples - 1, space.Dimension, _random))
                _queue.Enqueue(MakeProposal(point, fromModel: false));

            Console.WriteLine(
                $"{OutputConventions.BayesTag} Mode: {ActiveMode}, {space.Dimension} free parameter(s), "
                + $"{_initialSamples} initial samples (DoE), "
                + $"trust region: {(TrustRegionActive ? "on" : "off")}.");
        }

        private static void ValidateConfig(BayesConfig config)
        {
            static bool In(string? value, params string[] allowed) =>
                allowed.Contains((value ?? string.Empty).Trim(), StringComparer.OrdinalIgnoreCase);

            if (!In(config.SurrogateTarget, "Auto", "Fitness", "Metrics"))
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} Bayes.SurrogateTarget '{config.SurrogateTarget}' is unknown. Valid values are 'Auto', 'Fitness', and 'Metrics'.");
            if (config.InitialSamples < 0)
                throw new InvalidOperationException($"{OutputConventions.ConfigTag} Bayes.InitialSamples must not be negative (0 = automatic).");
        }

        private static SurrogateTargetMode ResolveMode(BayesConfig config, IFitnessCalculator fitness)
        {
            string target = (config.SurrogateTarget ?? "Auto").Trim().ToLowerInvariant();
            bool hasMetrics = fitness.RequiredMetrics.Count > 0;

            return target switch
            {
                "fitness" => SurrogateTargetMode.Fitness,
                "metrics" when !hasMetrics => throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} Bayes.SurrogateTarget = 'Metrics' requires the project's fitness calculator to "
                    + "declare its metrics (RequiredMetrics), but it declares none. Use 'Fitness' or 'Auto'."),
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
                    SimulationLog.Warning($"{OutputConventions.BayesTag} No prepared proposals remaining; falling back to a random DoE point.");
                }
                proposal = MakeProposal(LatinHypercube.Sample(1, _space!.Dimension, _random)[0], fromModel: false);
            }

            if (proposal.FromModel) _issued[(iteration, variant)] = proposal;
            return new Dictionary<string, float>(proposal.Parameters);
        }

        public ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context)
        {
            // Only set fitness and prediction columns without mutating other state:
            // this method also runs for individual fine-verification records.
            foreach (ModelRecord record in records)
            {
                _fitness.CalculateFitness(record, context.Config);
                BayesDiagnosticsReporter.AttachPrediction(record, _issued);
            }

            return WorkflowController.SelectBestRecord(records) ?? records.First();
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
                // Continue initial DoE; any remaining points stay queued for subsequent rounds.
                proposals.AddRange(leftoverDoe.Take(slots));
                deferredDoe.AddRange(leftoverDoe.Skip(slots));
                int missing = Math.Max(0, _initialSamples - usable);
                int failed = training.ExcludedPoints.Count;
                string note = failed > 0 ? $" ({failed} failed or rejected evaluation(s) excluded)" : "";
                Console.WriteLine($"\n[INFO] {Name}: Building surrogate training set. Need {missing} more baseline simulation(s) (DoE).{note}");
            }

            int remaining = slots - proposals.Count;
            bool modelUsed = false;

            if (remaining > 0)
            {
                bool canFitObjective = usable >= d + 2;
                bool canFitConstraintsOnly = !canFitObjective && training.AllConstraintRecords.Count >= d + 2;

                if (canFitObjective || canFitConstraintsOnly)
                {
                    try
                    {
                        diagnostics = ProposeFromModel(context, training, remaining, proposals, constraintOnly: !canFitObjective);
                        modelUsed = true;
                    }
                    catch (Exception ex)
                    {
                        // Surrogate failures never abort the run: warn and fill with DoE points.
                        SimulationLog.Warning($"{OutputConventions.BayesTag} Surrogate model training failed ({ex.Message}). "
                            + "Filling this iteration with random DoE points.");
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
            double? bestFitness = training.FeasibleRecords.Count > 0
                ? training.FeasibleRecords.Max(r => r.Fitness)
                : (training.Records.Count > 0 ? training.Records.Max(r => r.Fitness) : null);
            string modeLabel = modelUsed
                ? (usable >= d + 2 ? ActiveMode.ToString() : "ConstraintOnly")
                : "DoE";
            _reporter.AppendHistoryRow(
                context, _space, iterationNumber, modeLabel,
                usable, TrustRegionLength, accuracy, bestFitness, diagnostics);
        }

        private BayesProposal MakeProposal(double[] unit, bool fromModel, bool explorer = false) =>
            new() { Unit = unit, Parameters = _space!.FromUnit(unit), FromModel = fromModel, Explorer = explorer };

        private BayesDiagnostics ProposeFromModel(
            SimulationContext context, SurrogateTrainingSet training, int count, List<BayesProposal> output, bool constraintOnly)
        {
            ParameterSpace space = _space!;
            int d = space.Dimension;
            int variants = Math.Max(1, context.Config.VariantsPerIteration);

            if (!constraintOnly && ActiveMode == SurrogateTargetMode.Metrics) _ensemble.VerifyMetricsMode(training);
            _acquisition.UpdateTrustRegionCounters(training);

            double[] bestPoint;
            int trainedCount;
            FittedEnsemble fit;

            if (constraintOnly)
            {
                bestPoint = training.AllConstraintInputs[0];
                trainedCount = training.AllConstraintRecords.Count;
                fit = _ensemble.FitConstraintOnly(training, d);
            }
            else
            {
                bestPoint = training.FeasibleInputs.Count > 0
                    ? training.FeasibleInputs[BestIndex(training.FeasibleRecords)]
                    : training.Inputs[BestIndex(training.Records)];
                double[] capCenter = _acquisition.TrustCenter ?? bestPoint;
                List<int> selected = _ensemble.SelectTrainingIndices(training, capCenter);
                trainedCount = selected.Count;
                fit = _ensemble.FitModels(training, selected, d);
            }

            LastTrainingPointCount = trainedCount;
            _lastFit = fit;

            fit.Excluded = training.ExcludedPoints;
            // Success model: dedicated Gaussian Process on 1 = succeeded, 0 = crashed
            // (gatekeeper-rejected variants are excluded and only apply local repulsion).
            _ensemble.FitSuccessModel(training, fit);
            fit.RepulsionRadius = 0.05 * Math.Sqrt(d);

            // Common random numbers for Monte Carlo integration via Sobol-QMC, generated once per iteration.
            if (fit.Slots.Count > 0 && ActiveMode == SurrogateTargetMode.Metrics)
            {
                fit.Z = SobolSequence.GenerateNormalSamples(SurrogateModelEnsemble.FullSamples, fit.Slots.Count, _random);
            }

            // Trust region and candidate pool
            var (lo, hi) = _acquisition.PrepareTrustBounds(fit, training, space, bestPoint);
            double[][] pool = _acquisition.BuildPool(training, lo, hi, _tuning.CandidatePool);

            var chosen = fit.Chosen;
            var known = training.Inputs.Concat(training.PreConstraintPoints).ToList();
            for (int slot = 0; slot < count; slot++)
            {
                bool explorer = variants >= ExplorerMinimumVariants && slot == count - 1;
                double[]? x = explorer
                    ? _acquisition.PickExplorer(fit, space, known, chosen, d)
                    : _acquisition.PickByExpectedImprovement(fit, space, pool, known, chosen, lo, hi);

                bool fromFallback = x == null;
                x ??= LatinHypercube.Sample(1, d, _random)[0];

                BayesProposal proposal = MakeProposal(x, fromModel: true, explorer: explorer);
                if (fit.Slots.Count > 0)
                {
                    (proposal.Median, proposal.Low, proposal.High) = _reporter.PredictFitnessBand(fit, x, proposal.Parameters);
                }
                else
                {
                    proposal.Median = proposal.Low = proposal.High = double.NaN;
                }
                if (fromFallback) proposal.FromModel = false;

                output.Add(proposal);
                chosen.Add(x);
                _acquisition.Believe(fit, x, proposal.Parameters);
            }

            _reporter.LogSummary(fit, trainedCount, space);
            return new BayesDiagnostics { TrainingPoints = trainedCount, Relevance = fit.Relevance };
        }

        private static int BestIndex(IReadOnlyList<ModelRecord> records)
        {
            int best = 0;
            for (int i = 1; i < records.Count; i++)
                if (records[i].Fitness > records[best].Fitness) best = i;
            return best;
        }
    }
}
