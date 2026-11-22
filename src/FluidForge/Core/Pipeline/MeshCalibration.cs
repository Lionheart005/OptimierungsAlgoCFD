using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Orchestrates automated mesh calibration (TODO-42).
    /// </summary>
    public static class MeshCalibration
    {
        public const string CalibrationDirectoryName = OutputConventions.CalibrationDirectory;
        public const string LatestCalibrationJson = "latest_calibration.json";
        public const string LatestCalibrationCsv = "latest_calibration.csv";

        /// <summary>
        /// Version of the boundary-layer and domain meshing scheme. Included in the fingerprint:
        /// when the scheme changes (here: AP-2..AP-5 incl. decoupled tunnel scaling, DetailBox, and
        /// curvature refinement), any older calibration is considered stale.
        /// </summary>
        public const string MesherSchemeVersion = "tunnel-scaled-curvature-2";

        /// <summary>Additional refinement levels beyond the five standard levels (0.35, 0.25, 0.18, 0.13).</summary>
        public const int MaxAdditionalStages = 4;

        /// <summary>Smallest mesh factor that will still be attempted.</summary>
        public const float MinRefinementFactor = 0.1f;

        public static void ValidateConfig(SimulationConfig config, ConfigLoadReport? simulationReport, string projectName)
            => MeshCalibrationValidation.ValidateConfig(config, simulationReport, projectName);

        public static void ValidatePrecalibrationSupport(SimulationConfig config, IProjectDefinition definition)
            => MeshCalibrationValidation.ValidatePrecalibrationSupport(config, definition);

        internal static (float StandardLimit, float CoarseStageLimit) ResolveYPlusLimits(ProjectContext context)
            => MeshCalibrationValidation.ResolveYPlusLimits(context);

        public static void ValidateYPlusConsistency(ProjectContext context, bool calibrationActive, TextWriter? warningWriter = null)
            => MeshCalibrationValidation.ValidateYPlusConsistency(context, calibrationActive, warningWriter);

        public static string ComputeFingerprint(ProjectContext context)
            => MeshCalibrationValidation.ComputeFingerprint(context);

        public static void CheckManualCalibrationHints(ProjectContext context, string workingDirectory, TextWriter? writer = null)
            => MeshCalibrationValidation.CheckManualCalibrationHints(context, workingDirectory, writer);

        public static List<string> ResolveMonitoredMetrics(ProjectContext context)
            => MeshCalibrationReporter.ResolveMonitoredMetrics(context);

        internal static JsonObject BuildGciJsonObject(GciResult gci)
            => MeshCalibrationReporter.BuildGciJsonObject(gci);

        public static string? DescribeLayerMismatch(IReadOnlyList<CalibrationStageData> stages)
            => MeshCalibrationReporter.DescribeLayerMismatch(stages);

        internal static void PrintSummary(CalibrationDecision decision, TextWriter? output = null)
            => MeshCalibrationReporter.PrintSummary(decision, output);

        /// <summary>
        /// Executes mesh calibration via the <paramref name="runStage"/> callback.
        /// </summary>
        public static (CalibrationDecision Decision, string ReportRelativePath) Run(
            ProjectContext context,
            SimulationContext simulationContext,
            Func<float, string, ModelRecord> runStage,
            IReadOnlyCollection<string>? monitoredMetrics = null,
            long maxEstimatedFineCells = 50_000_000,
            int dimensions = 3,
            Func<double?>? durationProvider = null)
        {
            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : ResolveMonitoredMetrics(context);

            var (yPlusLimit, coarseYPlusLimit) = ResolveYPlusLimits(context);
            PrintCalibrationHeader("MESH CALIBRATION STARTED", context, metricsToMonitor, yPlusLimit, coarseYPlusLimit);

            var stages = new List<CalibrationStageData>();
            var cal = context.Simulation.MeshCalibration;
            long maxCells = dimensions == 2 ? cal.MaxCells2D : Math.Min(maxEstimatedFineCells, cal.MaxCells3D);

            // The cell limit applies to the MEASURED cell count and is checked after meshing, before SU2 runs.
            simulationContext.CellLimit = maxCells;
            try
            {
                Console.WriteLine($"\n{OutputConventions.CalibrationTag} Level 1/5: Coarse mesh (factor 2.00)...");
                var stage1 = RunCalibrationStage(runStage, 1, 2.0f, "Cal1", metricsToMonitor, coarseYPlusLimit, durationProvider);
                stages.Add(stage1);

                // Preview: roughly extrapolate cell count for the finest level (hint only; level 5 measured count is decisive).
                if (stage1.MeshCells > 0)
                {
                    long estimatedFineCells = EstimateCells(stage1.MeshCells, 2.0f, 0.5f, dimensions);
                    Console.WriteLine($"       -> Coarse cells: {stage1.MeshCells:N0} | rough preview for reference (0.50): ~{estimatedFineCells:N0} cells");
                    if (estimatedFineCells > maxCells)
                        Console.WriteLine($"       -> [HINT] Rough preview exceeds limit of {maxCells:N0} cells. The measured cell count after meshing level 5 is decisive.");
                }

                Console.WriteLine($"\n{OutputConventions.CalibrationTag} Level 5/5: Fine reference mesh (factor 0.50)...");
                var stage5 = RunCalibrationStage(runStage, 5, 0.5f, "Cal5", metricsToMonitor, yPlusLimit, durationProvider);
                if (stage5.CellLimitExceeded)
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.CalibrationTag} Reference mesh (factor 0.50) measured {stage5.MeshCells:N0} cells and "
                        + $"exceeds the limit of {maxCells:N0}. Calibration aborted (increase MeshCalibration.MaxCells).");
                }
                stages.Add(stage5);

                RunIntermediateStages(stages, runStage, "Cal", "CALIBRATION", metricsToMonitor, cal, yPlusLimit, durationProvider);
                RefineAdaptively(stages, runStage, "Cal", "CALIBRATION", metricsToMonitor, cal, dimensions, maxCells, yPlusLimit, durationProvider);
            }
            finally
            {
                simulationContext.CellLimit = 0;
            }

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, metricsToMonitor, cal.Tolerance, cal.AbsoluteTolerances);

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string calibSubdir = Path.Combine(CalibrationDirectoryName, timestamp);
            string fullCalibDir = Path.Combine(simulationContext.WorkingDirectory, calibSubdir);
            Directory.CreateDirectory(fullCalibDir);

            string fingerprint = ComputeFingerprint(context);
            MeshCalibrationReporter.SaveReports(fullCalibDir, simulationContext.WorkingDirectory, decision, metricsToMonitor, timestamp, fingerprint, cal.Tolerance);
            PrintSummary(decision);

            return (decision, calibSubdir.Replace('\\', '/'));
        }

        /// <summary>
        /// Executes two-stage mesh calibration (TODO-48):
        /// 1) 5-level 2D precalibration on the representative cross-section (yields f2D).
        /// 2) 3-level 3D confirmation around f2D (f2D * √2, f2D, f2D / √2).
        /// </summary>
        public static (CalibrationDecision Decision, string ReportRelativePath) RunWithPrecalibration2D(
            ProjectContext context,
            SimulationContext simulationContext,
            Func<float, string, ModelRecord> runStage2D,
            Func<float, string, ModelRecord> runStage3D,
            IReadOnlyCollection<string>? monitoredMetrics = null,
            long maxEstimatedFineCells = 50_000_000,
            Func<double?>? durationProvider2D = null,
            Func<double?>? durationProvider3D = null)
        {
            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : ResolveMonitoredMetrics(context);

            var (yPlusLimit, coarseYPlusLimit) = ResolveYPlusLimits(context);
            PrintCalibrationHeader("2D PRECALIBRATION + 3D CONFIRMATION STARTED (TODO-48)", context, metricsToMonitor, yPlusLimit, coarseYPlusLimit);

            var cal2 = context.Simulation.MeshCalibration;

            // 1. 2D precalibration
            Console.WriteLine("\n--- PART 1: 2D PRECALIBRATION ON CROSS-SECTION ---");
            var stages2D = new List<CalibrationStageData>();

            Console.WriteLine("\n[2D-CALIBRATION] Level 1/5: Coarse 2D mesh (factor 2.00)...");
            stages2D.Add(RunCalibrationStage(runStage2D, 1, 2.0f, "Cal2D_1", metricsToMonitor, coarseYPlusLimit, durationProvider2D));

            Console.WriteLine("\n[2D-CALIBRATION] Level 5/5: Fine 2D reference mesh (factor 0.50)...");
            stages2D.Add(RunCalibrationStage(runStage2D, 5, 0.5f, "Cal2D_5", metricsToMonitor, yPlusLimit, durationProvider2D));

            RunIntermediateStages(stages2D, runStage2D, "Cal2D_", "2D-CALIBRATION", metricsToMonitor, cal2, yPlusLimit, durationProvider2D);
            RefineAdaptively(stages2D, runStage2D, "Cal2D_", "2D-CALIBRATION", metricsToMonitor, cal2, 2, cal2.MaxCells2D, yPlusLimit, durationProvider2D);

            var decision2D = CalibrationEvaluator.EvaluateCalibration(stages2D, metricsToMonitor, cal2.Tolerance, cal2.AbsoluteTolerances);
            float factor2D = decision2D.SelectedStage?.Factor ?? 1.0f;
            Console.WriteLine($"\n[2D-CALIBRATION] Determined 2D factor: f2D = {factor2D:0.00} (Success={decision2D.Success})");

            // 2. 3D confirmation (three levels around f2D)
            Console.WriteLine("\n--- PART 2: 3D CONFIRMATION AROUND f2D ---");
            float[] factors3D = CalibrationEvaluator.Build3DConfirmationFactors(factor2D);
            float fCoarse = factors3D[0], fMed = factors3D[1], fFine = factors3D[2];
            var stages3D = new List<CalibrationStageData>();

            ModelRecord RunLimited3D(float factor, string tag)
            {
                simulationContext.CellLimit = maxEstimatedFineCells;
                try
                {
                    ModelRecord r = runStage3D(factor, tag);
                    if (r.PassiveParameters.ContainsKey(ModelRecord.CellLimitExceededKey))
                    {
                        r.PassiveParameters.TryGetValue("MeshCells", out float cells);
                        throw new InvalidOperationException(
                            $"[CALIBRATION 3D] Level {tag} (factor {factor:0.00}) measured {cells:N0} cells and exceeds the limit of {maxEstimatedFineCells:N0}.");
                    }
                    return r;
                }
                finally
                {
                    simulationContext.CellLimit = 0;
                }
            }

            Console.WriteLine($"\n[3D-CONFIRMATION] Level 1/3: Coarse 3D mesh (factor {fCoarse:0.00})...");
            CalibrationStageData stage3D_1 = RunCalibrationStage(RunLimited3D, 1, fCoarse, "Cal3D_1", metricsToMonitor, coarseYPlusLimit, durationProvider3D);
            stages3D.Add(stage3D_1);

            if (stage3D_1.MeshCells > 0)
            {
                long estimatedFine = (long)(stage3D_1.MeshCells * Math.Pow(fCoarse / Math.Max(fFine, 0.1f), 3));
                if (estimatedFine > maxEstimatedFineCells)
                    Console.WriteLine($"       -> [HINT] Rough preview ({estimatedFine:N0} cells) exceeds limit of {maxEstimatedFineCells:N0}. Measured count after meshing is decisive.");
            }

            Console.WriteLine($"\n[3D-CONFIRMATION] Level 3/3: Fine 3D reference (factor {fFine:0.00})...");
            stages3D.Add(RunCalibrationStage(RunLimited3D, 3, fFine, "Cal3D_3", metricsToMonitor, yPlusLimit, durationProvider3D));

            Console.WriteLine($"\n[3D-CONFIRMATION] Level 2/3: 2D factor in 3D (factor {fMed:0.00})...");
            stages3D.Add(RunCalibrationStage(RunLimited3D, 2, fMed, "Cal3D_2", metricsToMonitor, yPlusLimit, durationProvider3D));

            var decision3D = CalibrationEvaluator.EvaluateConfirmation3D(factor2D, stages3D, metricsToMonitor, cal2.Tolerance, cal2.AbsoluteTolerances);

            if (!decision3D.Success)
            {
                float fRefined = (float)Math.Round(factor2D / 2.0f, 2);
                if (fRefined > 0.05f && !stages3D.Any(s => Math.Abs(s.Factor - fRefined) < 0.01f))
                {
                    Console.WriteLine($"\n[3D-CONFIRMATION] Not confirmed. Additional 3D refinement level (factor {fRefined:0.00}, Cal3D_4)...");
                    stages3D.Add(RunCalibrationStage(RunLimited3D, 4, fRefined, "Cal3D_4", metricsToMonitor, yPlusLimit, durationProvider3D));
                    decision3D = CalibrationEvaluator.EvaluateCalibration(stages3D, metricsToMonitor, cal2.Tolerance, cal2.AbsoluteTolerances);
                }
            }

            // 3. Write combined reports
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string calibSubdir = Path.Combine(CalibrationDirectoryName, timestamp);
            string fullCalibDir = Path.Combine(simulationContext.WorkingDirectory, calibSubdir);
            Directory.CreateDirectory(fullCalibDir);

            string fingerprint = ComputeFingerprint(context);
            MeshCalibrationReporter.SaveCsvReport(Path.Combine(fullCalibDir, "calibration_2d.csv"), decision2D, metricsToMonitor);
            MeshCalibrationReporter.SaveCsvReport(Path.Combine(fullCalibDir, "calibration.csv"), decision3D, metricsToMonitor);
            MeshCalibrationReporter.SaveCombinedJsonReport(
                Path.Combine(fullCalibDir, "calibration.json"), decision3D, decision2D, factor2D, timestamp, fingerprint, cal2.Tolerance);

            string latestJson = Path.Combine(simulationContext.WorkingDirectory, CalibrationDirectoryName, LatestCalibrationJson);
            string latestCsv = Path.Combine(simulationContext.WorkingDirectory, CalibrationDirectoryName, LatestCalibrationCsv);
            File.Copy(Path.Combine(fullCalibDir, "calibration.json"), latestJson, overwrite: true);
            File.Copy(Path.Combine(fullCalibDir, "calibration.csv"), latestCsv, overwrite: true);

            PrintSummary(decision3D);

            if (decision3D.Success && decision3D.SelectedStage != null)
            {
                simulationContext.EffectiveMeshRefinementFactor = decision3D.SelectedStage.Factor;
                simulationContext.CalibrationReportPath = calibSubdir.Replace('\\', '/');
                simulationContext.MeshRefinementSource = $"CALIBRATED: 2D precalibration, confirmed in 3D, tolerance {cal2.Tolerance * 100:0.0} %";
            }

            return (decision3D, calibSubdir.Replace('\\', '/'));
        }

        private static void PrintCalibrationHeader(
            string title, ProjectContext context, List<string> metricsToMonitor, float yPlusLimit, float coarseYPlusLimit)
        {
            Console.WriteLine("\n==================================================");
            Console.WriteLine($" {title}");
            Console.WriteLine($" Project:           {context.Name}");
            Console.WriteLine($" Tolerance:         {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %");
            Console.WriteLine($" Monitored Metrics: {string.Join(", ", metricsToMonitor)}");
            Console.WriteLine($" y+ Limit (P95):    {yPlusLimit.ToString("0.00", CultureInfo.InvariantCulture)} (coarse level {coarseYPlusLimit.ToString("0.00", CultureInfo.InvariantCulture)})");
            Console.WriteLine("==================================================");
        }

        private static void RunIntermediateStages(
            List<CalibrationStageData> stages,
            Func<float, string, ModelRecord> runStage,
            string tagPrefix,
            string label,
            List<string> metricsToMonitor,
            MeshCalibrationConfig cal,
            float yPlusLimit,
            Func<double?>? durationProvider)
        {
            var intermediateConfigs = new[]
            {
                (Index: 2, Factor: 1.41f, Tag: $"{tagPrefix}2"),
                (Index: 3, Factor: 1.00f, Tag: $"{tagPrefix}3"),
                (Index: 4, Factor: 0.71f, Tag: $"{tagPrefix}4")
            };

            foreach (var inter in intermediateConfigs)
            {
                Console.WriteLine($"\n[{label}] Level {inter.Index}/5: Intermediate level (factor {inter.Factor:0.00})...");
                stages.Add(RunCalibrationStage(runStage, inter.Index, inter.Factor, inter.Tag, metricsToMonitor, yPlusLimit, durationProvider));

                if (stages.Count >= 3)
                {
                    var tempDecision = CalibrationEvaluator.EvaluateCalibration(stages, metricsToMonitor, cal.Tolerance, cal.AbsoluteTolerances);
                    if (tempDecision.Success && tempDecision.SelectedStage != null && tempDecision.SelectedStage.LevelIndex == inter.Index)
                    {
                        Console.WriteLine($"       -> Level {inter.Index} already satisfies the tolerance!");
                        break;
                    }
                }
            }
        }

        /// <summary>Projected cell count when changing from <paramref name="fromFactor"/> to <paramref name="toFactor"/>.</summary>
        public static long EstimateCells(int cells, float fromFactor, float toFactor, int dimensions)
            => (long)(cells * Math.Pow(fromFactor / Math.Max(toFactor, 1e-3f), dimensions));

        /// <summary>
        /// Cell count preview for <paramref name="nextFactor"/> using the MEASURED exponent of the two finest
        /// levels so far: <c>p = ln(N_fine / N_coarse) / ln(f_coarse / f_fine)</c>, clamped to [dimensions/2, dimensions].
        /// </summary>
        public static long EstimateNextCells(IReadOnlyList<CalibrationStageData> stages, float nextFactor, int dimensions)
        {
            var valid = stages.Where(s => s.MeshCells > 0).OrderBy(s => s.Factor).ToList();
            if (valid.Count == 0) return 0;

            var fine = valid[0];
            double exponent = dimensions;
            if (valid.Count >= 2 && valid[1].Factor > fine.Factor + 1e-6f)
            {
                var coarse = valid[1];
                double measured = Math.Log((double)fine.MeshCells / coarse.MeshCells) / Math.Log((double)coarse.Factor / fine.Factor);
                if (double.IsFinite(measured)) exponent = Math.Clamp(measured, dimensions / 2.0, (double)dimensions);
            }

            return (long)(fine.MeshCells * Math.Pow(fine.Factor / Math.Max(nextFactor, 1e-3f), exponent));
        }

        /// <summary>Duration formatted as "3 h 12 min", "45 min", or "30 s" (for the log).</summary>
        public static string FormatDuration(double seconds)
        {
            if (seconds < 90) return $"{seconds:0} s";
            double minutes = seconds / 60.0;
            if (minutes < 90) return $"{minutes:0} min";
            int hours = (int)(seconds / 3600.0);
            int restMinutes = (int)Math.Round((seconds - hours * 3600.0) / 60.0);
            if (restMinutes == 60) { hours++; restMinutes = 0; }
            return $"{hours} h {restMinutes:00} min";
        }

        /// <summary>Next finer factor: ÷ √2, rounded to two decimals (0.50 -> 0.35 -> 0.25 -> 0.18 -> 0.13).</summary>
        public static float NextFinerFactor(float factor)
            => (float)Math.Round(factor / Math.Sqrt(2.0), 2);

        private static CalibrationStageData RunCalibrationStage(
            Func<float, string, ModelRecord> runStage,
            int level,
            float factor,
            string tag,
            List<string> metricsToMonitor,
            float yPlusLimit,
            Func<double?>? durationProvider = null)
        {
            var timer = Stopwatch.StartNew();
            ModelRecord rec = runStage(factor, tag);
            timer.Stop();

            CalibrationStageData stage = BuildStageData(
                rec, level, factor, timer.Elapsed.TotalSeconds, metricsToMonitor, yPlusLimit, durationProvider);
            MeshCalibrationReporter.PrintStageDuration(level, factor, stage.DurationSeconds);
            return stage;
        }

        internal static CalibrationStageData BuildStageData(
            ModelRecord rec,
            int level,
            float factor,
            double measuredSeconds,
            List<string> metricsToMonitor,
            float yPlusLimit,
            Func<double?>? durationProvider = null)
        {
            double durationSeconds = durationProvider?.Invoke() is double provided && provided > 0.0 && double.IsFinite(provided)
                ? provided
                : measuredSeconds;

            int cells = (int)(rec.PassiveParameters.TryGetValue("MeshCells", out float c) ? c : 0);
            bool cellLimitExceeded = rec.PassiveParameters.ContainsKey(ModelRecord.CellLimitExceededKey);
            float p95 = rec.PassiveParameters.TryGetValue("YPlus_P95", out float y) ? y : 0f;
            // "Unsteady, but stable" (vortex shedding) counts as usable.
            bool unsteadyStable = rec.PassiveParameters.TryGetValue("Su2_Unsteady", out float un) && un > 0.5f;
            bool converged = !rec.SimulationFailed
                && (unsteadyStable || !rec.PassiveParameters.TryGetValue("Su2_Converged", out float cv) || cv > 0.5f);
            bool yPlusOk = p95 <= 0f || p95 <= yPlusLimit;

            var uncertainty = new Dictionary<string, float>();
            foreach (string metric in metricsToMonitor)
            {
                if (MetricUncertainty.TryGet(rec, metric, out double u)) uncertainty[metric] = (float)u;
            }

            int ReadInt(string key)
                => rec.PassiveParameters.TryGetValue(key, out float val) && float.IsFinite(val) ? (int)MathF.Round(val) : 0;

            float ReadFloat(string key, float fallback)
                => rec.PassiveParameters.TryGetValue(key, out float val) ? val : fallback;

            return new CalibrationStageData(
                LevelIndex: level, Factor: factor, MeshCells: cells, DurationSeconds: durationSeconds,
                Metrics: MeshCalibrationReporter.ExtractMetrics(rec, metricsToMonitor), Fitness: rec.Fitness,
                Converged: converged, YPlusP95: p95, YPlusOk: yPlusOk,
                Uncertainty: uncertainty.Count > 0 ? uncertainty : null, CellLimitExceeded: cellLimitExceeded,
                BoundaryLayerLayers: ReadInt("BoundaryLayerLayers"),
                BoundaryLayerThicknessMm: ReadFloat("BoundaryLayerThicknessMm", 0f),
                BoundaryLayerStage: ReadInt("BoundaryLayerStage"),
                SurfaceTriangles: ReadInt("MeshCells_Tri"), SurfaceQuads: ReadInt("MeshCells_Quad"),
                VolumePrisms: ReadInt("MeshCells_Prism"), VolumeHexahedra: ReadInt("MeshCells_Hexa"),
                VolumeTetrahedra: ReadInt("MeshCells_Tet"), VolumePyramids: ReadInt("MeshCells_Pyramid"),
                TunnelBlockagePercent: ReadFloat("TunnelBlockagePercent", float.NaN),
                PressureDragCoeff: ReadFloat("Su2_CD_p", float.NaN),
                ViscousDragCoeff: ReadFloat("Su2_CD_v", float.NaN),
                YPlusLimit: yPlusLimit);
        }

        private static void RefineAdaptively(
            List<CalibrationStageData> stages,
            Func<float, string, ModelRecord> runStage,
            string tagPrefix,
            string label,
            List<string> metricsToMonitor,
            MeshCalibrationConfig cal,
            int dimensions,
            long maxCells,
            float yPlusLimit,
            Func<double?>? durationProvider = null)
        {
            for (int extra = 0; extra < MaxAdditionalStages; extra++)
            {
                var decision = CalibrationEvaluator.EvaluateCalibration(stages, metricsToMonitor, cal.Tolerance, cal.AbsoluteTolerances);
                if (decision.Success) return;

                var refStage = stages.OrderBy(s => s.Factor).First();
                if (!refStage.Converged)
                {
                    Console.WriteLine($"       -> [{label}] Finest level did not converge - refining further will not help; solver iterations are insufficient.");
                    return;
                }
                if (!refStage.YPlusOk)
                {
                    string limitNote = float.IsFinite(refStage.YPlusLimit) && refStage.YPlusLimit > 0f
                        ? $" (P95 = {refStage.YPlusP95.ToString("0.00", CultureInfo.InvariantCulture)} > limit {refStage.YPlusLimit.ToString("0.00", CultureInfo.InvariantCulture)} from TargetYPlus * YPlusWarnFactor)"
                        : $" (P95 = {refStage.YPlusP95.ToString("0.00", CultureInfo.InvariantCulture)})";
                    Console.WriteLine($"       -> [{label}] y+ of the finest level is out of tolerance{limitNote} - a finer mesh does not change y1 (check TargetYPlus/YPlusWarnFactor in su2.json & gmsh.json).");
                    return;
                }

                float next = NextFinerFactor(refStage.Factor);
                if (next < MinRefinementFactor) return;

                long estimated = EstimateNextCells(stages, next, dimensions);
                if (refStage.MeshCells > 0 && estimated > maxCells * 1.5)
                {
                    Console.WriteLine($"       -> [{label}] Next level (factor {next:0.00}) would require ~{estimated:N0} cells, well above limit {maxCells:N0} - stopping refinement.");
                    return;
                }

                int level = stages.Max(s => s.LevelIndex) + 1;
                Console.WriteLine($"\n[{label}] Tolerance of {cal.Tolerance * 100:0.0} % not yet reached - adding level {level} (factor {next:0.00}, preview ~{estimated:N0} cells, measured limit {maxCells:N0})...");
                var extraStage = RunCalibrationStage(runStage, level, next, $"{tagPrefix}{level}", metricsToMonitor, yPlusLimit, durationProvider);
                if (extraStage.CellLimitExceeded)
                {
                    Console.WriteLine($"       -> [{label}] Level {level} (factor {next:0.00}) measured {extraStage.MeshCells:N0} cells and exceeds limit {maxCells:N0} - skipping solver, stopping refinement.");
                    return;
                }
                if (!extraStage.Converged || !extraStage.YPlusOk || extraStage.MeshCells <= 0)
                {
                    Console.WriteLine($"       -> [{label}] Extra level {level} (factor {next:0.00}) was invalid (Converged={extraStage.Converged}, y+Ok={extraStage.YPlusOk}, Cells={extraStage.MeshCells}) - keeping level {refStage.LevelIndex} (factor {refStage.Factor:0.00}) as reference and stopping refinement.");
                    return;
                }
                stages.Add(extraStage);
            }
        }
    }
}
