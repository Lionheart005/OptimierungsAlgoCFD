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
    /// Orchestriert die automatisierte Netzkalibrierung (TODO-42).
    /// </summary>
    public static class MeshCalibration
    {
        public const string CalibrationDirectoryName = "Kalibrierung";
        public const string LatestCalibrationJson = "latest_calibration.json";
        public const string LatestCalibrationCsv = "latest_calibration.csv";

        /// <summary>
        /// Version des Wandschicht- und Domänen-Verfahrens im Mesher. Geht in den Fingerabdruck ein: ändert sich das
        /// Verfahren (hier: AP-2..AP-5 inkl. entkoppelter Tunnel-Skalierung, DetailBox und Krümmungsverfeinerung), ist
        /// eine ältere Kalibrierung veraltet.
        /// </summary>
        public const string MesherSchemeVersion = "tunnel-scaled-curvature-2";

        /// <summary>Weitere Verfeinerungsstufen nach den fünf Standardstufen (0.35, 0.25, 0.18, 0.13).</summary>
        public const int MaxAdditionalStages = 4;

        /// <summary>Kleinster Netzfaktor, der noch versucht wird.</summary>
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
        /// Führt die Kalibrierung über den Rückruf <paramref name="runStage"/> aus.
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
            PrintCalibrationHeader("NETZKALIBRIERUNG GESTARTET", context, metricsToMonitor, yPlusLimit, coarseYPlusLimit);

            var stages = new List<CalibrationStageData>();
            var cal = context.Simulation.MeshCalibration;
            long maxCells = dimensions == 2 ? cal.MaxCells2D : Math.Min(maxEstimatedFineCells, cal.MaxCells3D);

            // Die Zellgrenze gilt für die GEMESSENE Zellzahl und wird nach dem Vernetzen geprüft, bevor SU2 rechnet.
            simulationContext.CellLimit = maxCells;
            try
            {
                Console.WriteLine("\n[KALIBRIERUNG] Stufe 1/5: Grobes Netz (Faktor 2.00)...");
                var stage1 = RunCalibrationStage(runStage, 1, 2.0f, "Kal1", metricsToMonitor, coarseYPlusLimit, durationProvider);
                stages.Add(stage1);

                // Vorschau: Zellzahl der feinsten Stufe grob hochrechnen (nur Hinweis; entscheidend ist Stufe 5).
                if (stage1.MeshCells > 0)
                {
                    long estimatedFineCells = EstimateCells(stage1.MeshCells, 2.0f, 0.5f, dimensions);
                    Console.WriteLine($"       -> Zellen grob: {stage1.MeshCells:N0} | grobe Vorschau für Referenz (0.50): ~{estimatedFineCells:N0} Zellen");
                    if (estimatedFineCells > maxCells)
                        Console.WriteLine($"       -> [HINWEIS] Die grobe Vorschau liegt über der Grenze von {maxCells:N0} Zellen. Entscheidend ist die gemessene Zellzahl nach dem Vernetzen der Stufe 5.");
                }

                Console.WriteLine("\n[KALIBRIERUNG] Stufe 5/5: Feines Referenznetz (Faktor 0.50)...");
                var stage5 = RunCalibrationStage(runStage, 5, 0.5f, "Kal5", metricsToMonitor, yPlusLimit, durationProvider);
                if (stage5.CellLimitExceeded)
                {
                    throw new InvalidOperationException(
                        $"[KALIBRIERUNG] Das Referenznetz (Faktor 0.50) hat gemessen {stage5.MeshCells:N0} Zellen und "
                        + $"überschreitet das Limit von {maxCells:N0}. Kalibrierung abgebrochen (MeshCalibration.MaxCells erhöhen).");
                }
                stages.Add(stage5);

                RunIntermediateStages(stages, runStage, "Kal", "KALIBRIERUNG", metricsToMonitor, cal, yPlusLimit, durationProvider);
                RefineAdaptively(stages, runStage, "Kal", "KALIBRIERUNG", metricsToMonitor, cal, dimensions, maxCells, yPlusLimit, durationProvider);
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
        /// Führt die zweistufige Netzkalibrierung aus (TODO-48):
        /// 1) 5-Stufen-2D-Vorkalibrierung am repräsentativen Querschnitt (liefert f2D).
        /// 2) 3-Stufen-3D-Bestätigung um f2D (f2D * √2, f2D, f2D / √2).
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
            PrintCalibrationHeader("2D-VORKALIBRIERUNG + 3D-BESTÄTIGUNG GESTARTET (TODO-48)", context, metricsToMonitor, yPlusLimit, coarseYPlusLimit);

            var cal2 = context.Simulation.MeshCalibration;

            // 1. 2D-Vorkalibrierung
            Console.WriteLine("\n--- TEIL 1: 2D-VORKALIBRIERUNG AM QUERSCHNITT ---");
            var stages2D = new List<CalibrationStageData>();

            Console.WriteLine("\n[2D-KALIBRIERUNG] Stufe 1/5: Grobes 2D-Netz (Faktor 2.00)...");
            stages2D.Add(RunCalibrationStage(runStage2D, 1, 2.0f, "Kal2D_1", metricsToMonitor, coarseYPlusLimit, durationProvider2D));

            Console.WriteLine("\n[2D-KALIBRIERUNG] Stufe 5/5: Feines 2D-Referenznetz (Faktor 0.50)...");
            stages2D.Add(RunCalibrationStage(runStage2D, 5, 0.5f, "Kal2D_5", metricsToMonitor, yPlusLimit, durationProvider2D));

            RunIntermediateStages(stages2D, runStage2D, "Kal2D_", "2D-KALIBRIERUNG", metricsToMonitor, cal2, yPlusLimit, durationProvider2D);
            RefineAdaptively(stages2D, runStage2D, "Kal2D_", "2D-KALIBRIERUNG", metricsToMonitor, cal2, 2, cal2.MaxCells2D, yPlusLimit, durationProvider2D);

            var decision2D = CalibrationEvaluator.EvaluateCalibration(stages2D, metricsToMonitor, cal2.Tolerance, cal2.AbsoluteTolerances);
            float factor2D = decision2D.SelectedStage?.Factor ?? 1.0f;
            Console.WriteLine($"\n[2D-KALIBRIERUNG] Ermittelter 2D-Faktor: f2D = {factor2D:0.00} (Erfolg={decision2D.Success})");

            // 2. 3D-Bestätigung (drei Stufen um f2D)
            Console.WriteLine("\n--- TEIL 2: 3D-BESTÄTIGUNG UM f2D ---");
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
                            $"[KALIBRIERUNG 3D] Stufe {tag} (Faktor {factor:0.00}) hat gemessen {cells:N0} Zellen und überschreitet das Limit von {maxEstimatedFineCells:N0}.");
                    }
                    return r;
                }
                finally
                {
                    simulationContext.CellLimit = 0;
                }
            }

            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 1/3: Grobes 3D-Netz (Faktor {fCoarse:0.00})...");
            CalibrationStageData stage3D_1 = RunCalibrationStage(RunLimited3D, 1, fCoarse, "Kal3D_1", metricsToMonitor, coarseYPlusLimit, durationProvider3D);
            stages3D.Add(stage3D_1);

            if (stage3D_1.MeshCells > 0)
            {
                long estimatedFine = (long)(stage3D_1.MeshCells * Math.Pow(fCoarse / Math.Max(fFine, 0.1f), 3));
                if (estimatedFine > maxEstimatedFineCells)
                    Console.WriteLine($"       -> [HINWEIS] Die grobe Vorschau ({estimatedFine:N0} Zellen) liegt über dem Limit von {maxEstimatedFineCells:N0}. Gemessen wird nach dem Vernetzen.");
            }

            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 3/3: Feine 3D-Referenz (Faktor {fFine:0.00})...");
            stages3D.Add(RunCalibrationStage(RunLimited3D, 3, fFine, "Kal3D_3", metricsToMonitor, yPlusLimit, durationProvider3D));

            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 2/3: 2D-Faktor in 3D (Faktor {fMed:0.00})...");
            stages3D.Add(RunCalibrationStage(RunLimited3D, 2, fMed, "Kal3D_2", metricsToMonitor, yPlusLimit, durationProvider3D));

            var decision3D = CalibrationEvaluator.EvaluateConfirmation3D(factor2D, stages3D, metricsToMonitor, cal2.Tolerance, cal2.AbsoluteTolerances);

            if (!decision3D.Success)
            {
                float fRefined = (float)Math.Round(factor2D / 2.0f, 2);
                if (fRefined > 0.05f && !stages3D.Any(s => Math.Abs(s.Factor - fRefined) < 0.01f))
                {
                    Console.WriteLine($"\n[3D-BESTÄTIGUNG] Keine Bestätigung. Zusätzliche 3D-Verfeinerungsstufe (Faktor {fRefined:0.00}, Kal3D_4)...");
                    stages3D.Add(RunCalibrationStage(RunLimited3D, 4, fRefined, "Kal3D_4", metricsToMonitor, yPlusLimit, durationProvider3D));
                    decision3D = CalibrationEvaluator.EvaluateCalibration(stages3D, metricsToMonitor, cal2.Tolerance, cal2.AbsoluteTolerances);
                }
            }

            // 3. Kombinierte Berichte schreiben
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
                simulationContext.MeshRefinementSource = $"KALIBRIERT: 2D-Vorkalibrierung, in 3D bestätigt, Toleranz {cal2.Tolerance * 100:0.0} %";
            }

            return (decision3D, calibSubdir.Replace('\\', '/'));
        }

        private static void PrintCalibrationHeader(
            string title, ProjectContext context, List<string> metricsToMonitor, float yPlusLimit, float coarseYPlusLimit)
        {
            Console.WriteLine("\n==================================================");
            Console.WriteLine($" {title}");
            Console.WriteLine($" Projekt:           {context.Name}");
            Console.WriteLine($" Toleranz:          {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %");
            Console.WriteLine($" Überwachte Größen: {string.Join(", ", metricsToMonitor)}");
            Console.WriteLine($" y+-Limit (P95):    {yPlusLimit.ToString("0.00", CultureInfo.InvariantCulture)} (Grobstufe {coarseYPlusLimit.ToString("0.00", CultureInfo.InvariantCulture)})");
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
                Console.WriteLine($"\n[{label}] Stufe {inter.Index}/5: Zwischenstufe (Faktor {inter.Factor:0.00})...");
                stages.Add(RunCalibrationStage(runStage, inter.Index, inter.Factor, inter.Tag, metricsToMonitor, yPlusLimit, durationProvider));

                if (stages.Count >= 3)
                {
                    var tempDecision = CalibrationEvaluator.EvaluateCalibration(stages, metricsToMonitor, cal.Tolerance, cal.AbsoluteTolerances);
                    if (tempDecision.Success && tempDecision.SelectedStage != null && tempDecision.SelectedStage.LevelIndex == inter.Index)
                    {
                        Console.WriteLine($"       -> Stufe {inter.Index} erfüllt bereits die Toleranz!");
                        break;
                    }
                }
            }
        }

        /// <summary>Hochgerechnete Zellzahl bei Wechsel von <paramref name="fromFactor"/> auf <paramref name="toFactor"/>.</summary>
        public static long EstimateCells(int cells, float fromFactor, float toFactor, int dimensions)
            => (long)(cells * Math.Pow(fromFactor / Math.Max(toFactor, 1e-3f), dimensions));

        /// <summary>
        /// Vorschau der Zellzahl für <paramref name="nextFactor"/> mit dem GEMESSENEN Exponenten der beiden feinsten
        /// bisherigen Stufen: <c>p = ln(N_fein / N_grob) / ln(f_grob / f_fein)</c>, begrenzt auf [dimensions/2, dimensions].
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

        /// <summary>Dauer als "3 h 12 min", "45 min" oder "30 s" (für das Log).</summary>
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

        /// <summary>Nächstfeinerer Faktor: ÷ √2, auf zwei Stellen gerundet (0.50 → 0.35 → 0.25 → 0.18 → 0.13).</summary>
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
            // "Instationär, aber stabil" (Wirbelablösung) zählt als brauchbar.
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
                    Console.WriteLine($"       -> [{label}] Feinste Stufe ist nicht auskonvergiert – feiner rechnen hilft nicht, die Iterationen reichen nicht.");
                    return;
                }
                if (!refStage.YPlusOk)
                {
                    string limitNote = float.IsFinite(refStage.YPlusLimit) && refStage.YPlusLimit > 0f
                        ? $" (P95 = {refStage.YPlusP95.ToString("0.00", CultureInfo.InvariantCulture)} > Limit {refStage.YPlusLimit.ToString("0.00", CultureInfo.InvariantCulture)} aus TargetYPlus × YPlusWarnFactor)"
                        : $" (P95 = {refStage.YPlusP95.ToString("0.00", CultureInfo.InvariantCulture)})";
                    Console.WriteLine($"       -> [{label}] y+ der feinsten Stufe liegt außerhalb der Toleranz{limitNote} – ein feineres Netz ändert an y1 nichts (TargetYPlus/YPlusWarnFactor in su2.json & gmsh.json prüfen).");
                    return;
                }

                float next = NextFinerFactor(refStage.Factor);
                if (next < MinRefinementFactor) return;

                long estimated = EstimateNextCells(stages, next, dimensions);
                if (refStage.MeshCells > 0 && estimated > maxCells * 1.5)
                {
                    Console.WriteLine($"       -> [{label}] Nächste Stufe (Faktor {next:0.00}) hätte ~{estimated:N0} Zellen und überschreitet die Grenze von {maxCells:N0} deutlich – Verfeinerung endet.");
                    return;
                }

                int level = stages.Max(s => s.LevelIndex) + 1;
                Console.WriteLine($"\n[{label}] Toleranz von {cal.Tolerance * 100:0.0} % noch nicht erreicht – zusätzliche Stufe {level} (Faktor {next:0.00}, Vorschau ~{estimated:N0} Zellen, Grenze {maxCells:N0} gemessen)...");
                var extraStage = RunCalibrationStage(runStage, level, next, $"{tagPrefix}{level}", metricsToMonitor, yPlusLimit, durationProvider);
                if (extraStage.CellLimitExceeded)
                {
                    Console.WriteLine($"       -> [{label}] Stufe {level} (Faktor {next:0.00}) hat gemessen {extraStage.MeshCells:N0} Zellen und überschreitet die Grenze von {maxCells:N0} – sie wird nicht gerechnet, Verfeinerung endet.");
                    return;
                }
                if (!extraStage.Converged || !extraStage.YPlusOk || extraStage.MeshCells <= 0)
                {
                    Console.WriteLine($"       -> [{label}] Zusätzliche Stufe {level} (Faktor {next:0.00}) war nicht gültig (Konvergiert={extraStage.Converged}, y+Ok={extraStage.YPlusOk}, Zellen={extraStage.MeshCells}) – behalte Stufe {refStage.LevelIndex} (Faktor {refStage.Factor:0.00}) als Referenz und beende Verfeinerung.");
                    return;
                }
                stages.Add(extraStage);
            }
        }
    }
}
