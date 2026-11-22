using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Orchestrates the optimization workflow.
    /// Operates strictly against interfaces — no concrete solver or geometry implementations.
    /// </summary>
    public class WorkflowController
    {
        /// <summary>Variant number used for validation re-simulations.</summary>
        private const int ValidationVariantNumber = 99;

        private readonly SimulationContext _context;
        private readonly IOptimizationAlgorithm _optimizer;
        private readonly IGeometryGenerator _geometry;
        private readonly SolverStage[] _stages;
        private readonly IModelValidator _validator;

        // No IFitnessCalculator field: evaluation runs through the optimization algorithm
        // (EvaluateAndSelectBest) and during validation through ChampionValidator.
        // Required metrics therefore enter solely as a list of names rather than a calculator
        // instance — keeping the decoupling introduced in TODO-13 intact (TODO-26).
        private readonly IReadOnlyCollection<string> _requiredMetrics;

        // Run journal for resuming aborted runs (TODO-51). null = exact pre-TODO-51 behavior.
        private readonly RunJournal? _journal;
        private readonly IGeometryConstraintEvaluator? _geometryConstraints;

        /// <summary>
        /// True if the last call to <see cref="RunPipeline"/> was replayed from the journal without
        /// executing solvers. The run-time estimator skips replayed calls (TODO-51).
        /// </summary>
        public bool LastCallWasReplayed { get; private set; }

        /// <summary>
        /// Measured or replayed duration in seconds of the most recent <see cref="RunPipeline"/> call.
        /// </summary>
        public double? LastCallDurationSeconds { get; private set; }

        /// <summary>
        /// 2 or 3: determines how cell count scales during fine verification and thus the time forecast.
        /// Set from <c>su2.json</c>; defaults to 3 if omitted.
        /// </summary>
        public int MeshDimensions { get; init; } = 3;

        /// <param name="requiredMetrics">
        /// Metric names required by the project's <see cref="IFitnessCalculator"/>
        /// (<see cref="IFitnessCalculator.RequiredMetrics"/>). If any is missing after the first
        /// successful variant, the run aborts (TODO-26). <c>null</c> or empty skips the check.
        /// </param>
        /// <param name="journal">
        /// Optional run journal (TODO-51): every result of <see cref="RunPipeline"/> is recorded,
        /// and in replay mode the journal answers instead of running solvers. All controllers in a run
        /// share one instance.
        /// </param>
        /// <param name="geometryConstraints">
        /// Optional pre-CFD geometry constraint evaluator run before meshing and solver execution.
        /// </param>
        public WorkflowController(
            IGeometryGenerator geometry,
            SolverStage[] stages,
            IModelValidator validator,
            IOptimizationAlgorithm optimizer,
            SimulationContext context,
            IReadOnlyCollection<string>? requiredMetrics = null,
            RunJournal? journal = null,
            IGeometryConstraintEvaluator? geometryConstraints = null)
        {
            _journal = journal;
            _geometry = geometry;
            _stages = stages;
            _validator = validator;
            _optimizer = optimizer;
            _context = context;
            _requiredMetrics = requiredMetrics ?? Array.Empty<string>();
            _geometryConstraints = geometryConstraints;
        }

        public void RunOptimization()
        {
            Console.WriteLine($"{OutputConventions.SystemTag} Using optimization algorithm: {_optimizer.Name}");
            Console.WriteLine($"{OutputConventions.SystemTag} Project: {_context.Project.ProjectName}");
            Console.WriteLine($"{OutputConventions.SystemTag} Solver(s): {string.Join(", ", _stages.Select(s => s.Solver.Name))}");

            // Automatic stop (09.11.2026). Invalid settings abort here before the first evaluation.
            AutoStopConfig autoStop = _context.Config.AutoStop ?? new AutoStopConfig();
            bool autoStopEnabled = AutoStop.IsEnabled(autoStop);
            if (autoStopEnabled)
                Console.WriteLine(
                    $"{OutputConventions.SystemTag} Automatic stop: enabled (minimum improvement {autoStop.MinImprovement * 100:0.##} % "
                    + $"over the last {autoStop.WindowVariants} variants; at most "
                    + $"{_context.Config.MaxIterations} x {_context.Config.VariantsPerIteration} variants)");

            // Run-time forecast (time-estimation plan): sliding window, per-iteration validation,
            // and final fine verification are all accounted for.
            var estimator = new RunTimeEstimator(_context.Config, MeshDimensions, autoStopEnabled);

            // Winner of the last completed iteration. Kept only for the closing summary — the
            // OVERALL champion is determined across the entire history (TODO-15), not the last iteration.
            ModelRecord? lastIterationWinner = null;

            // Required metrics are checked once on the first variant that completes without failure
            // (see EnsureRequiredMetrics).
            bool requiredMetricsChecked = false;

            // =========================================================
            // OUTER LOOP: ITERATIONS (e.g., generations)
            // =========================================================
            for (int iter = 1; iter <= _context.Config.MaxIterations; iter++)
            {
                SimulationLog.PrintIterationBanner(iter, _context.Config.MaxIterations);

                List<ModelRecord> currentIterationRecords = new List<ModelRecord>();

                // =========================================================
                // INNER LOOP: VARIANTS (models)
                // =========================================================
                for (int var = 1; var <= _context.Config.VariantsPerIteration; var++)
                {
                    Stopwatch timer = Stopwatch.StartNew();

                    // 1. Generate parameters from the optimization algorithm
                    var activeParams = _optimizer.GenerateParameters(_context, iter, var, _context.Config.VariantsPerIteration);

                    SimulationLog.PrintVariantHeader(iter, _context.Config.MaxIterations, var, _context.Config.VariantsPerIteration);
                    SimulationLog.PrintGeneLine(activeParams);

                    // 2.-6. Core pipeline (scaling -> geometry -> mesh -> solver)
                    ModelRecord record = RunPipeline(activeParams, iter, var);
                    bool replayed = LastCallWasReplayed;

                    // Evaluate immediately per variant (if successful):
                    if (!record.SimulationFailed)
                    {
                        _optimizer.EvaluateAndSelectBest(new List<ModelRecord> { record }, _context);
                    }

                    // 6a. Does the pipeline provide everything the fitness formula requires?
                    if (!requiredMetricsChecked && !record.SimulationFailed && !record.IsPreConstraintInfeasible)
                    {
                        requiredMetricsChecked = true;
                        EnsureRequiredMetrics(record);
                    }

                    // 7. Record result
                    _context.History.Add(record);
                    currentIterationRecords.Add(record);
                    _context.ExportToCsv();

                    SimulationLog.PrintVariantResult(record);
                    SimulationLog.PrintVariantDiagnostics(record);
                    UpdateTimer(timer, replayed, record, estimator, iter, var);
                }

                // =========================================================
                // EVALUATION & VALIDATION
                // =========================================================
                // 1. Algorithm scores the iteration's variants and picks the best
                _optimizer.EvaluateAndSelectBest(currentIterationRecords, _context);

                // 2. Apply stability validator (bouncer)
                ModelRecord trueWinner = _validator.ValidateChampion(
                    currentIterationRecords,
                    _context.Config,
                    (testParams) => ResimulateForValidation(testParams, iter));

                // 2a. Optional per-iteration fine verification (Mode = "EachIteration", TODO-43)
                ChampionFineVerifier.VerifyIterationWinner(
                    trueWinner,
                    iter,
                    _context,
                    (p, f, t) => RunPipeline(p, iter, ValidationVariantNumber, f, t),
                    r => _optimizer.EvaluateAndSelectBest(new List<ModelRecord> { r }, _context),
                    _requiredMetrics);

                lastIterationWinner = trueWinner;

                _context.ExportToCsv();

                SimulationLog.PrintIterationSummary(iter, _context.Config.MaxIterations, currentIterationRecords, trueWinner);
                SimulationLog.PrintTimeForecast(estimator.Estimate(iter, _context.Config.VariantsPerIteration, iterationFinished: true), DateTime.Now);

                // Report immediately if the champion sits on a parameter bound rather than waiting until
                // the very end (after fine verification, or never if stopped manually).
                PrintBoundaryHint(trueWinner, iter);

                // Automatic stop: only between iterations, never mid-generation.
                if (autoStopEnabled && iter < _context.Config.MaxIterations)
                {
                    AutoStopDecision decision = AutoStop.Evaluate(_context.History, autoStop);
                    if (decision.Stop)
                    {
                        SimulationLog.PrintBanner(
                            $"AUTOMATIC STOP AFTER ITERATION {iter} OF {_context.Config.MaxIterations}",
                            "Best fitness is no longer improving sufficiently (AutoStop).");
                        Console.WriteLine($"{OutputConventions.AutoStopTag} {decision.Message}");
                        break;
                    }
                    Console.WriteLine($"{OutputConventions.AutoStopTag} {decision.Message}");
                }

                if (iter < _context.Config.MaxIterations)
                {
                    // 3. Prepare algorithm for the next iteration
                    _optimizer.PrepareNextIteration(_context, currentIterationRecords, trueWinner);
                }
            }

            // Final verification (Mode = "Final" or "FinalTopN", TODO-43)
            ChampionFineVerifier.VerifyFinal(
                _context,
                (p, f, t) => RunPipeline(p, ValidationVariantNumber, ValidationVariantNumber, f, t),
                r => _optimizer.EvaluateAndSelectBest(new List<ModelRecord> { r }, _context),
                _requiredMetrics);

            // Best record across the ENTIRE run, not just the last iteration's winner:
            // with EA they are usually the same, while with RSM/DoE they often differ.
            FinishOptimization(SelectBestRecord(_context.History), lastIterationWinner);
        }

        /// <summary>
        /// Returns the record with the highest fitness, preferring valid feasible records over
        /// infeasible, disqualified, or failed ones. Ties break in favor of the earlier record for
        /// reproducibility. Returns <c>null</c> if the sequence is empty.
        /// </summary>
        public static ModelRecord? SelectBestRecord(IEnumerable<ModelRecord> records)
        {
            ModelRecord? best = null;
            int bestTier = -1;

            foreach (var record in records)
            {
                int tier = SelectionTier(record);
                if (best == null || tier > bestTier)
                {
                    best = record;
                    bestTier = tier;
                    continue;
                }

                if (tier == bestTier)
                {
                    bool currentFinite = float.IsFinite(record.Fitness);
                    bool bestFinite = float.IsFinite(best.Fitness);
                    if ((currentFinite && !bestFinite) || (record.Fitness > best.Fitness))
                    {
                        best = record;
                    }
                }
            }

            return best;
        }

        private static int SelectionTier(ModelRecord record)
        {
            if (!record.SimulationFailed && !record.Disqualified && record.Feasible) return 3;
            if (!record.SimulationFailed && !record.Disqualified) return 2;
            if (!record.SimulationFailed) return 1;
            return 0;
        }

        /// <summary>
        /// Verifies that the record contains all metrics required by the project's
        /// <see cref="IFitnessCalculator"/>, aborting otherwise (TODO-26).
        ///
        /// <para>
        /// Deliberately checked only after the first variant: before that, no metrics exist because
        /// they come from geometry and solver. Also checked only on a non-failed record so a single
        /// SU2 crash in variant 1 is not misreported as a configuration error. If every variant fails,
        /// the solver has already reported the underlying issue.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">A required metric is missing.</exception>
        private void EnsureRequiredMetrics(ModelRecord record)
        {
            if (_requiredMetrics.Count == 0) return;

            var missing = _requiredMetrics.Where(name => !record.PassiveParameters.ContainsKey(name)).ToList();
            if (missing.Count == 0) return;

            string present = record.PassiveParameters.Count == 0
                ? "(none)"
                : string.Join(", ", record.PassiveParameters.Keys);

            throw new InvalidOperationException(
                $"{OutputConventions.ConfigTag} After the first evaluated variant, required metric(s) "
                + $"{string.Join(", ", missing)} are missing; fitness cannot be computed without them. "
                + $"Present: {present}. "
                + "Geometry metrics are reported by the project's IGeometryGenerator via AddMetric(...); "
                + "solver quantities come from 'ResultMetrics' in su2.json. "
                + "Aborting here rather than running the entire optimization with a fallback fitness.");
        }

        /// <summary>
        /// Callback for the model validator: executes a full re-simulation with the given parameters
        /// through the same pipeline as a regular variant, using variant number 99 and without adding
        /// to the history.
        /// </summary>
        private ModelRecord ResimulateForValidation(Dictionary<string, float> testParams, int iteration)
        {
            return RunPipeline(testParams, iteration, ValidationVariantNumber);
        }

        /// <summary>
        /// Core evaluation pipeline for one parameter set:
        /// scaling (rubber-band) -> geometry -> metrics -> meshing -> solver chain.
        /// Optionally supports temporary mesh factor and tag overrides (TODO-42/43).
        /// Returns a populated <see cref="ModelRecord"/>; fitness is intentionally not computed here,
        /// leaving that to the optimization algorithm or validator.
        /// </summary>
        public ModelRecord RunPipeline(
            Dictionary<string, float> parameters,
            int iteration,
            int variant,
            float? customRefinementFactor = null,
            string? customTag = null)
        {
            float oldFactor = _context.EffectiveMeshRefinementFactor;
            string oldTag = _context.CurrentRunTag;

            try
            {
                if (customRefinementFactor.HasValue)
                {
                    _context.EffectiveMeshRefinementFactor = customRefinementFactor.Value;
                }
                if (customTag != null)
                {
                    _context.CurrentRunTag = customTag;
                }

                LastCallWasReplayed = false;
                LastCallDurationSeconds = null;
                if (_journal == null)
                {
                    var directStopwatch = Stopwatch.StartNew();
                    ModelRecord directRecord = RunPipelineCore(parameters, iteration, variant);
                    directStopwatch.Stop();
                    LastCallDurationSeconds = directStopwatch.Elapsed.TotalSeconds;
                    return directRecord;
                }

                // Resume (TODO-51): check journal first; otherwise execute and append.
                // Factor and tag here already reflect overrides (after TODO-42/43).
                float factor = _context.EffectiveMeshRefinementFactor;
                string tag = _context.CurrentRunTag ?? "";

                if (_journal.TryReplay(iteration, variant, factor, tag, parameters, out ModelRecord? replayed)
                    && replayed != null)
                {
                    LastCallWasReplayed = true;
                    LastCallDurationSeconds = _journal.LastReplayedDurationSeconds;
                    return replayed;
                }

                var sw = Stopwatch.StartNew();
                ModelRecord record = RunPipelineCore(parameters, iteration, variant);
                sw.Stop();
                double elapsedSeconds = sw.Elapsed.TotalSeconds;
                LastCallDurationSeconds = elapsedSeconds;

                // Record immediately upon return before callers mutate the record.
                _journal.Append(iteration, variant, factor, tag, parameters, record, elapsedSeconds);
                return record;
            }
            finally
            {
                _context.EffectiveMeshRefinementFactor = oldFactor;
                _context.CurrentRunTag = oldTag;
            }
        }

        private ModelRecord RunPipelineCore(Dictionary<string, float> parameters, int iteration, int variant)
        {
            // Rubber-band scaling using the project's dimensional parameters.
            // When disabled, Create() returns a neutral identity instance (ShrinkFactor = 1).
            RubberBandScaler scaler = RubberBandScaler.Create(
                _context.Config.UseRubberBandScaler,
                parameters,
                _context.Project.DimensionalParameters,
                _context.Config.TargetPicoGkSize);

            ModelRecord record = new ModelRecord
            {
                Iteration = iteration,
                Variant = variant,
                ActiveParameters = parameters
            };
            record.PassiveParameters["MeshRefinementFactor"] = _context.EffectiveMeshRefinementFactor;

            // Generate geometry (IGeometryGenerator) -> GeometryResult
            var geoResult = _geometry.GenerateAndExport(
                iteration, variant,
                scaler.ShrunkParameters,
                _context.WorkingDirectory,
                _context.Config);

            record.StlPath = geoResult.StlPath;

            if (geoResult.HasSurfacePatches)
            {
                foreach (var kvp in geoResult.SurfacePatches)
                {
                    record.Artifacts[$"Patch_{kvp.Key}"] = kvp.Value;
                }
            }

            // Transfer metrics from GeometryResult to record and restore physical scale
            ApplyGeometryMetrics(record, geoResult, scaler);
            record.PassiveParameters["ScaleFactor"] = scaler.ShrinkFactor;

            SimulationLog.PrintGeometry(geoResult.StlPath, record.PassiveParameters);

            if (_geometryConstraints != null)
            {
                IReadOnlyList<ConstraintEvaluation> preConstraints = _geometryConstraints.EvaluateGeometryConstraints(record, _context.Project);
                if (preConstraints != null && preConstraints.Count > 0)
                {
                    foreach (ConstraintEvaluation constraint in preConstraints)
                    {
                        record.Constraints.Add(constraint.IsPreSolve ? constraint : constraint with { IsPreSolve = true });
                    }

                    var violated = record.Constraints.Where(c => c.IsPreSolve && !c.IsSatisfied).ToList();
                    if (violated.Count > 0)
                    {
                        record.Feasible = false;
                        record.FailedStage = EvaluationFailureStage.PreConstraint;
                        record.SimulationFailed = false;
                        string violatedNames = string.Join(
                            ", ",
                            violated.Select(c => $"{c.Name} (margin {c.Margin.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)})"));
                        SimulationLog.Warning($"Pre-CFD constraint violated ({violatedNames}) — skipping meshing and solver.");
                        return record;
                    }
                }
            }

            try
            {
                // Each stage meshes with its own mesher and then runs its solver.
                // Stages sharing the same mesher instance mesh only once.
                var meshCache = new Dictionary<IMeshGenerator, string>(ReferenceEqualityComparer.Instance);

                foreach (var stage in _stages)
                {
                    if (!meshCache.TryGetValue(stage.Mesher, out string? meshPath))
                    {
                        var meshRequest = new MeshRequest(
                            geoResult.StlPath, iteration, variant,
                            _context.Config, scaler.ShrinkFactor,
                            _context.WorkingDirectory,
                            _context.EffectiveMeshRefinementFactor,
                            _context.CurrentRunTag,
                            Geometry: geoResult);

                        meshPath = stage.Mesher.GenerateMesh(meshRequest);
                        meshCache[stage.Mesher] = meshPath;

                        // ModelRecord stores one mesh path — the one from the first stage.
                        if (string.IsNullOrEmpty(record.MeshPath)) record.MeshPath = meshPath;

                        // Mesh metrics (cell counts, layer count, thickness, fallback stage, ...) for CSV export
                        if (stage.Mesher is IMeshMetricsSource metricsSource)
                        {
                            foreach (var kvp in metricsSource.LastMeshMetrics)
                                record.PassiveParameters[kvp.Key] = kvp.Value;
                        }

                        int cellCount = 0;
                        if (record.PassiveParameters.TryGetValue("MeshCells", out float reportedCells)
                            && float.IsFinite(reportedCells) && reportedCells > 0f)
                        {
                            cellCount = (int)MathF.Round(reportedCells);
                        }
                        else
                        {
                            // Backward-compatibility fallback for mock meshers without IMeshMetricsSource or "MeshCells"
                            MeshMetrics.PopulateSu2CellMetrics(record.PassiveParameters, meshPath, overwriteExisting: false);
                            if (record.PassiveParameters.TryGetValue("MeshCells", out float fallbackCells)
                                && float.IsFinite(fallbackCells) && fallbackCells > 0f)
                            {
                                cellCount = (int)MathF.Round(fallbackCells);
                            }
                        }

                        // Cell limit (calibration or MaxCells3D safety cap during optimization):
                        // checked against MEASURED cell count before SU2 runs for hours.
                        bool isCalibrationLimit = _context.CellLimit > 0;
                        long effectiveCellLimit = isCalibrationLimit
                            ? _context.CellLimit
                            : _context.Config.MeshCalibration.MaxCells3D;
                        if (effectiveCellLimit > 0 && cellCount > effectiveCellLimit)
                        {
                            record.PassiveParameters[ModelRecord.CellLimitExceededKey] = 1f;
                            record.SimulationFailed = true;
                            record.FailedStage = EvaluationFailureStage.Mesh;
                            record.Feasible = false;
                            string limitLabel = isCalibrationLimit
                                ? "Cell limit (calibration)"
                                : "Safety cap (MeshCalibration.MaxCells3D)";
                            SimulationLog.Error(
                                $"{limitLabel} exceeded: {cellCount:N0} cells measured, limit {effectiveCellLimit:N0}. Skipping flow solver.");
                            break;
                        }
                    }

                    stage.Solver.Solve(meshPath, record, _context.Config, _context.WorkingDirectory);
                    if (record.SimulationFailed && record.FailedStage == EvaluationFailureStage.None)
                    {
                        record.FailedStage = EvaluationFailureStage.Solver;
                        record.Feasible = false;
                    }
                }
            }
            catch (Exception ex)
            {
                SimulationLog.Error($"Simulation aborted: {ex.Message}");
                // Generic failure marker — the FitnessCalculator handles it.
                // Core remains agnostic to project-specific metric names like "Drag".
                record.SimulationFailed = true;
                record.Feasible = false;

                // A meshing failure is not a solver divergence: Bayes should not avoid the entire
                // shape region for it (boundary-layer cell count plan, FZ7).
                if (ex is MeshGenerationException)
                {
                    record.PassiveParameters[ModelRecord.MeshFailedKey] = 1f;
                    record.FailedStage = EvaluationFailureStage.Mesh;
                }
                else
                {
                    record.FailedStage = EvaluationFailureStage.Solver;
                }
            }

            return record;
        }

        /// <summary>
        /// Copies metrics from <see cref="GeometryResult"/> into the record, restoring them from
        /// PicoGK working scale back to physical units. The project declares each metric's scaling
        /// dimension on <see cref="GeometryResult"/> — Core never guesses.
        /// </summary>
        private static void ApplyGeometryMetrics(ModelRecord record, GeometryResult geoResult, RubberBandScaler scaler)
        {
            foreach (var metric in geoResult.Metrics)
            {
                record.PassiveParameters[metric.Key] = geoResult.ScalingFor(metric.Key) switch
                {
                    MetricScaling.Volume => scaler.RestoreVolume(metric.Value),
                    MetricScaling.Area   => scaler.RestoreArea(metric.Value),
                    MetricScaling.Linear => scaler.RestoreLength(metric.Value),
                    _                    => metric.Value
                };
            }
        }

        public static string FormatParameterValue(float value)
        {
            float abs = MathF.Abs(value);
            if (abs > 0f && abs < 1.0f)
                return value.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
            return value.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
        }

        private void UpdateTimer(
            Stopwatch timer, bool replayed, ModelRecord record, RunTimeEstimator estimator, int iteration, int variantsDone)
        {
            timer.Stop();

            // Live variants contribute their measured duration. Journal-replayed variants contribute
            // their originally recorded duration (DurationSeconds preserved), not replay milliseconds.
            // Failed and pre-CFD-skipped variants are excluded so quick rejections do not pull the average down.
            if (!record.SimulationFailed && !record.IsPreConstraintInfeasible)
            {
                double? measured = replayed ? LastCallDurationSeconds : timer.Elapsed.TotalSeconds;
                if (measured.HasValue) estimator.AddVariant(measured.Value);
            }

            SimulationLog.PrintVariantFooter(
                timer.Elapsed.TotalSeconds,
                estimator.Estimate(iteration, variantsDone, iterationFinished: false),
                DateTime.Now);
        }

        /// <summary>Reports when an iteration champion lies on a parameter bound.</summary>
        private void PrintBoundaryHint(ModelRecord champion, int iteration)
        {
            var atBounds = BoundaryHint.Find(champion.ActiveParameters, _context.Project.ParameterBounds);
            if (atBounds.Count == 0) return;

            SimulationLog.WriteLineColored(
                $"[BOUND] Champion of iteration {iteration} lies on parameter bound: "
                + string.Join(", ", atBounds.Select(f => f.Describe())),
                ConsoleColor.Yellow);
        }

        private void FinishOptimization(ModelRecord? champion, ModelRecord? lastIterationWinner)
        {
            // An optimization where not a single variant succeeded is not "successful"
            // (server test 06.11.2026: Gmsh failed on every variant yet the banner stayed) (TODO-W9).
            int valid = _context.History.Count(r => !r.SimulationFailed);
            bool anySucceeded = valid > 0;

            if (anySucceeded)
            {
                SimulationLog.PrintBanner("OPTIMIZATION COMPLETED SUCCESSFULLY");
            }
            else
            {
                SimulationLog.PrintBanner("OPTIMIZATION FINISHED - NO VALID VARIANT FOUND");
            }

            foreach (string line in MeshRobustnessReport.Lines(_context.History))
            {
                Console.WriteLine(line);
            }

            if (!anySucceeded)
            {
                SimulationLog.Warning($"All {_context.History.Count} variants failed (meshing or SU2). Error logs are located under {OutputConventions.ErrorLogsDirectory}/.");
                return;
            }

            if (champion != null)
            {
                Console.WriteLine();
                SimulationLog.WriteLineColored($"🏆 {OutputConventions.ChampionBanner}: Iteration {champion.Iteration} | Variant {champion.Variant}", ConsoleColor.Green);
                SimulationLog.WriteLineColored($"   Score: {champion.Fitness:F2}", ConsoleColor.Green);

                // Make it explicit when the overall champion differs from the last iteration's winner
                // so the user is not confused by two different variants in the output.
                if (lastIterationWinner != null && !ReferenceEquals(lastIterationWinner, champion))
                {
                    Console.WriteLine(
                        $"   (Winner of the last iteration was Iteration {lastIterationWinner.Iteration} | " +
                        $"Variant {lastIterationWinner.Variant} with Score {lastIterationWinner.Fitness:F2})");
                }

                // Display passive parameters dynamically
                foreach (var kvp in champion.PassiveParameters)
                {
                    Console.WriteLine($"   {kvp.Key}: {kvp.Value:F4}");
                }

                // If the winner sits at the edge of the allowed space, the bound dictates the result.
                // Report this visibly; bounds themselves are never widened automatically.
                var atBounds = BoundaryHint.Find(champion.ActiveParameters, _context.Project.ParameterBounds);
                if (atBounds.Count > 0)
                {
                    Console.WriteLine();
                    foreach (string line in BoundaryHint.Lines(atBounds))
                        SimulationLog.WriteLineColored(line, ConsoleColor.Yellow);
                }

                // Paths come from context rather than hardcoded strings:
                // since TODO-25 results live under results/<Project>/, so hardcoding would mislead the user.
                string analysis = System.IO.Path.Combine(_context.WorkingDirectory, OutputConventions.AnalysisDirectory);

                Console.WriteLine($"\n📂 ParaView files in {analysis}:");
                Console.WriteLine($"   Surface: Surface_Gen{champion.Iteration}_Var{champion.Variant}.vtu");
                Console.WriteLine($"   Volume:  Volume_Gen{champion.Iteration}_Var{champion.Variant}.vtu\n");
            }
        }
    }
}
