using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPicoGkProject.Core
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
        /// Regel 1 (Nachvollziehbarkeit): Validiert die Konfiguration vor dem Lauf.
        /// </summary>
        public static void ValidateConfig(SimulationConfig config, ConfigLoadReport? simulationReport, string projectName)
        {
            string mode = (config.MeshCalibration.Mode ?? "Manual").Trim();

            if (!mode.Equals("Manual", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"[KONFIGURATION] Unbekannter MeshCalibration.Mode '{config.MeshCalibration.Mode}'. "
                    + "Gültig sind 'Manual' und 'Auto'.");
            }

            if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (simulationReport != null && simulationReport.Origins.TryGetValue("MeshRefinementFactor", out string? originFile))
                {
                    throw new InvalidOperationException(
                        $"[KONFIGURATION] Im Auto-Modus wird der Netzfaktor kalibriert. Bitte 'MeshRefinementFactor' "
                        + $"aus {originFile} entfernen – einzutragen ist nur MeshCalibration.Tolerance.");
                }
            }
        }

        /// <summary>
        /// Validiert, ob das Projekt die 2D-Vorkalibrierung unterstützt (TODO-48).
        /// Besitzt das Projekt keinen repräsentativen 2D-Querschnitt (IHasRepresentativeSection),
        /// bricht die Methode mit einer aussagekräftigen Ausnahme ab.
        /// </summary>
        public static void ValidatePrecalibrationSupport(SimulationConfig config, IProjectDefinition definition)
        {
            if (config.MeshCalibration.Use2DPrecalibration && definition is not IHasRepresentativeSection)
            {
                throw new InvalidOperationException(
                    $"[KALIBRIERUNG] 'Use2DPrecalibration' ist in simulation.json aktiviert, aber das Projekt '{definition.Name}' besitzt keinen repräsentativen 2D-Querschnitt (IHasRepresentativeSection). Für 3D-Körper ohne einheitliches Profil (wie MantaAuv) ist eine 2D-Vorkalibrierung physikalisch nicht aussagekräftig – bitte 'Use2DPrecalibration': false setzen.");
            }
        }

        /// <summary>
        /// Berechnet einen SHA256-Fingerabdruck über Startgeometrie (BaseParameters), gmsh.json und su2.json (Regel 6).
        /// </summary>
        public static string ComputeFingerprint(ProjectContext context)
        {
            var sb = new StringBuilder();

            // 1. Sortierte BaseParameters
            foreach (var kvp in context.Project.BaseParameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append($"{kvp.Key}={kvp.Value.ToString("R", CultureInfo.InvariantCulture)};");
            }

            // 2. gmsh.json & su2.json falls vorhanden
            if (context.ProjectDirectory != null)
            {
                string gmshPath = Path.Combine(context.ProjectDirectory, "solvers", "gmsh.json");
                if (File.Exists(gmshPath))
                {
                    sb.Append("gmsh:");
                    sb.Append(File.ReadAllText(gmshPath));
                }

                string su2Path = Path.Combine(context.ProjectDirectory, "solvers", "su2.json");
                if (File.Exists(su2Path))
                {
                    sb.Append("su2:");
                    sb.Append(File.ReadAllText(su2Path));
                }
            }

            using var sha = SHA256.Create();
            byte[] hash = sha.ComputeHash(Encoding.UTF8.GetBytes(sb.ToString()));
            return Convert.ToHexString(hash).ToLowerInvariant();
        }

        /// <summary>
        /// Prüft im manuellen Modus, ob eine frühere Kalibrierung vorliegt und gibt Hinweise aus (Regeln 5 &amp; 6).
        /// </summary>
        public static void CheckManualCalibrationHints(ProjectContext context, string workingDirectory, TextWriter? writer = null)
        {
            var targetWriter = writer ?? Console.Out;
            string latestJsonPath = Path.Combine(workingDirectory, CalibrationDirectoryName, LatestCalibrationJson);
            if (!File.Exists(latestJsonPath)) return;

            try
            {
                string jsonText = File.ReadAllText(latestJsonPath);
                var node = JsonNode.Parse(jsonText);
                if (node == null) return;

                string? storedFingerprint = node["Fingerprint"]?.GetValue<string>();
                string currentFingerprint = ComputeFingerprint(context);

                if (!string.IsNullOrEmpty(storedFingerprint) && !storedFingerprint.Equals(currentFingerprint, StringComparison.OrdinalIgnoreCase))
                {
                    targetWriter.WriteLine("[HINWEIS] Kalibrierung veraltet (Konfiguration oder Startgeometrie hat sich seitdem geändert).");
                }

                if (node["SelectedFactor"] != null)
                {
                    float selectedFactor = node["SelectedFactor"]!.GetValue<float>();
                    string timestamp = node["Timestamp"]?.GetValue<string>() ?? "unbekannt";

                    if (Math.Abs(selectedFactor - context.Simulation.MeshRefinementFactor) > 0.001f)
                    {
                        targetWriter.WriteLine($"[HINWEIS] Letzte Kalibrierung ({timestamp}) empfahl {selectedFactor.ToString("0.00", CultureInfo.InvariantCulture)}, eingetragen ist {context.Simulation.MeshRefinementFactor.ToString("0.00", CultureInfo.InvariantCulture)}.");
                    }
                }
            }
            catch
            {
                // Unlesbare Vorgängerkalibrierung wird ignoriert
            }
        }

        /// <summary>
        /// Führt die Kalibrierung über den Rückruf <paramref name="runStage"/> aus.
        /// </summary>
        public static (CalibrationDecision Decision, string ReportRelativePath) Run(
            ProjectContext context,
            SimulationContext simulationContext,
            Func<float, string, ModelRecord> runStage,
            IReadOnlyCollection<string>? monitoredMetrics = null,
            long maxEstimatedFineCells = 50_000_000)
        {
            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : ResolveMonitoredMetrics(context);

            Console.WriteLine("\n==================================================");
            Console.WriteLine(" NETZKALIBRIERUNG GESTARTET");
            Console.WriteLine($" Projekt:           {context.Name}");
            Console.WriteLine($" Toleranz:          {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %");
            Console.WriteLine($" Überwachte Größen: {string.Join(", ", metricsToMonitor)}");
            Console.WriteLine("==================================================");

            var stages = new List<CalibrationStageData>();
            var timer = new Stopwatch();

            // Stufe 1: Grob (2.0f, Tag: "Kal1")
            Console.WriteLine("\n[KALIBRIERUNG] Stufe 1/5: Grobes Netz (Faktor 2.00)...");
            timer.Restart();
            ModelRecord recCoarse = runStage(2.0f, "Kal1");
            timer.Stop();

            int coarseCells = (int)(recCoarse.PassiveParameters.TryGetValue("MeshCells", out float cc) ? cc : 0);
            float coarseP95 = recCoarse.PassiveParameters.TryGetValue("YPlus_P95", out float cp95) ? cp95 : 0f;
            bool coarseConv = !recCoarse.SimulationFailed && (!recCoarse.PassiveParameters.TryGetValue("Su2_Converged", out float cconv) || cconv > 0.5f);
            bool coarseYPlusOk = coarseP95 <= 0f || coarseP95 <= 3.0f; // Bei grobem Netz oft höher

            var stage1 = new CalibrationStageData(
                LevelIndex: 1,
                Factor: 2.0f,
                MeshCells: coarseCells,
                DurationSeconds: timer.Elapsed.TotalSeconds,
                Metrics: ExtractMetrics(recCoarse, metricsToMonitor),
                Fitness: recCoarse.Fitness,
                Converged: coarseConv,
                YPlusP95: coarseP95,
                YPlusOk: coarseYPlusOk);
            stages.Add(stage1);

            // Sicherheitsprüfung: Zellzahl der feinsten Stufe hochrechnen (in 3D Faktor (2.0 / 0.5)^3 = 64)
            if (coarseCells > 0)
            {
                long estimatedFineCells = (long)coarseCells * 64;
                Console.WriteLine($"       -> Zellen grob: {coarseCells:N0} | Hochgerechnet für Referenz (0.50): ~{estimatedFineCells:N0} Zellen");

                if (estimatedFineCells > maxEstimatedFineCells)
                {
                    throw new InvalidOperationException(
                        $"[KALIBRIERUNG] Hochgerechnete Zellzahl für die feinste Stufe ({estimatedFineCells:N0} Zellen) "
                        + $"überschreitet das Limit von {maxEstimatedFineCells:N0}. Kalibrierung abgebrochen.");
                }
            }

            // Stufe 5: Sehr feine Referenz (0.50f, Tag: "Kal5")
            Console.WriteLine("\n[KALIBRIERUNG] Stufe 5/5: Feines Referenznetz (Faktor 0.50)...");
            timer.Restart();
            ModelRecord recRef = runStage(0.5f, "Kal5");
            timer.Stop();

            int refCells = (int)(recRef.PassiveParameters.TryGetValue("MeshCells", out float rc) ? rc : 0);
            float refP95 = recRef.PassiveParameters.TryGetValue("YPlus_P95", out float rp95) ? rp95 : 0f;
            bool refConv = !recRef.SimulationFailed && (!recRef.PassiveParameters.TryGetValue("Su2_Converged", out float rconv) || rconv > 0.5f);
            bool refYPlusOk = refP95 <= 0f || refP95 <= 2.5f;

            var stage5 = new CalibrationStageData(
                LevelIndex: 5,
                Factor: 0.5f,
                MeshCells: refCells,
                DurationSeconds: timer.Elapsed.TotalSeconds,
                Metrics: ExtractMetrics(recRef, metricsToMonitor),
                Fitness: recRef.Fitness,
                Converged: refConv,
                YPlusP95: refP95,
                YPlusOk: refYPlusOk);
            stages.Add(stage5);

            // Zwischenstufen von grob nach fein: 1.41 (Kal2), 1.0 (Kal3), 0.71 (Kal4)
            var intermediateConfigs = new[]
            {
                (Index: 2, Factor: 1.41f, Tag: "Kal2"),
                (Index: 3, Factor: 1.00f, Tag: "Kal3"),
                (Index: 4, Factor: 0.71f, Tag: "Kal4")
            };

            foreach (var inter in intermediateConfigs)
            {
                Console.WriteLine($"\n[KALIBRIERUNG] Stufe {inter.Index}/5: Zwischenstufe (Faktor {inter.Factor:0.00})...");
                timer.Restart();
                ModelRecord recInter = runStage(inter.Factor, inter.Tag);
                timer.Stop();

                int iCells = (int)(recInter.PassiveParameters.TryGetValue("MeshCells", out float ic) ? ic : 0);
                float iP95 = recInter.PassiveParameters.TryGetValue("YPlus_P95", out float ip95) ? ip95 : 0f;
                bool iConv = !recInter.SimulationFailed && (!recInter.PassiveParameters.TryGetValue("Su2_Converged", out float iconv) || iconv > 0.5f);
                bool iYPlusOk = iP95 <= 0f || iP95 <= 2.5f;

                var stage = new CalibrationStageData(
                    LevelIndex: inter.Index,
                    Factor: inter.Factor,
                    MeshCells: iCells,
                    DurationSeconds: timer.Elapsed.TotalSeconds,
                    Metrics: ExtractMetrics(recInter, metricsToMonitor),
                    Fitness: recInter.Fitness,
                    Converged: iConv,
                    YPlusP95: iP95,
                    YPlusOk: iYPlusOk);
                stages.Add(stage);

                // Frühabstiegsprüfung: Wenn diese Stufe innerhalb der Toleranz liegt und wir mindestens 3 Stufen für GCI haben
                if (stages.Count >= 3)
                {
                    var tempDecision = CalibrationEvaluator.EvaluateCalibration(stages, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance);
                    if (tempDecision.Success && tempDecision.SelectedStage != null && tempDecision.SelectedStage.LevelIndex == inter.Index)
                    {
                        Console.WriteLine($"       -> Stufe {inter.Index} erfüllt bereits die Toleranz!");
                        break;
                    }
                }
            }

            // Vollständige Auswertung
            var decision = CalibrationEvaluator.EvaluateCalibration(stages, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance);

            // Berichte schreiben
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string calibSubdir = Path.Combine(CalibrationDirectoryName, timestamp);
            string fullCalibDir = Path.Combine(simulationContext.WorkingDirectory, calibSubdir);
            Directory.CreateDirectory(fullCalibDir);

            string fingerprint = ComputeFingerprint(context);
            SaveReports(fullCalibDir, simulationContext.WorkingDirectory, decision, metricsToMonitor, timestamp, fingerprint, context.Simulation.MeshCalibration.Tolerance);

            PrintSummary(decision);

            return (decision, calibSubdir.Replace('\\', '/'));
        }

        /// <summary>
        /// Führt die zweistufige Netzkalibrierung aus (TODO-48):
        /// 1) 5-Stufen-2D-Vorkalibrierung am repräsentativen Querschnitt (liefert f2D).
        /// 2) 3-Stufen-3D-Bestätigung um f2D (f2D * √2, f2D, f2D / √2).
        /// Schreibt kombinierten Bericht mit beiden Tabellen (calibration_2d.csv und calibration.csv + calibration.json).
        /// </summary>
        public static (CalibrationDecision Decision, string ReportRelativePath) RunWithPrecalibration2D(
            ProjectContext context,
            SimulationContext simulationContext,
            Func<float, string, ModelRecord> runStage2D,
            Func<float, string, ModelRecord> runStage3D,
            IReadOnlyCollection<string>? monitoredMetrics = null,
            long maxEstimatedFineCells = 50_000_000)
        {
            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : ResolveMonitoredMetrics(context);

            Console.WriteLine("\n==================================================");
            Console.WriteLine(" 2D-VORKALIBRIERUNG + 3D-BESTÄTIGUNG GESTARTET (TODO-48)");
            Console.WriteLine($" Projekt:           {context.Name}");
            Console.WriteLine($" Toleranz:          {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %");
            Console.WriteLine($" Überwachte Größen: {string.Join(", ", metricsToMonitor)}");
            Console.WriteLine("==================================================");

            var timer = new Stopwatch();

            // -------------------------------------------------------------
            // 1. 2D-Vorkalibrierung (fünf Stufen am Schnitt)
            // -------------------------------------------------------------
            Console.WriteLine("\n--- TEIL 1: 2D-VORKALIBRIERUNG AM QUERSCHNITT ---");
            var stages2D = new List<CalibrationStageData>();

            // Stufe 1: Grob (2.00, Tag: Kal2D_1)
            Console.WriteLine("\n[2D-KALIBRIERUNG] Stufe 1/5: Grobes 2D-Netz (Faktor 2.00)...");
            timer.Restart();
            ModelRecord rec2D_1 = runStage2D(2.0f, "Kal2D_1");
            timer.Stop();

            int coarseCells2D = (int)(rec2D_1.PassiveParameters.TryGetValue("MeshCells", out float c2c) ? c2c : 0);
            float coarseP95_2D = rec2D_1.PassiveParameters.TryGetValue("YPlus_P95", out float c2p) ? c2p : 0f;
            bool coarseConv2D = !rec2D_1.SimulationFailed && (!rec2D_1.PassiveParameters.TryGetValue("Su2_Converged", out float c2cv) || c2cv > 0.5f);
            bool coarseYPlusOk2D = coarseP95_2D <= 0f || coarseP95_2D <= 3.0f;

            stages2D.Add(new CalibrationStageData(
                LevelIndex: 1,
                Factor: 2.0f,
                MeshCells: coarseCells2D,
                DurationSeconds: timer.Elapsed.TotalSeconds,
                Metrics: ExtractMetrics(rec2D_1, metricsToMonitor),
                Fitness: rec2D_1.Fitness,
                Converged: coarseConv2D,
                YPlusP95: coarseP95_2D,
                YPlusOk: coarseYPlusOk2D));

            // Stufe 5: Feines Referenznetz (0.50, Tag: Kal2D_5)
            Console.WriteLine("\n[2D-KALIBRIERUNG] Stufe 5/5: Feines 2D-Referenznetz (Faktor 0.50)...");
            timer.Restart();
            ModelRecord rec2D_5 = runStage2D(0.5f, "Kal2D_5");
            timer.Stop();

            int refCells2D = (int)(rec2D_5.PassiveParameters.TryGetValue("MeshCells", out float r2c) ? r2c : 0);
            float refP95_2D = rec2D_5.PassiveParameters.TryGetValue("YPlus_P95", out float r2p) ? r2p : 0f;
            bool refConv2D = !rec2D_5.SimulationFailed && (!rec2D_5.PassiveParameters.TryGetValue("Su2_Converged", out float r2cv) || r2cv > 0.5f);
            bool refYPlusOk2D = refP95_2D <= 0f || refP95_2D <= 2.5f;

            stages2D.Add(new CalibrationStageData(
                LevelIndex: 5,
                Factor: 0.5f,
                MeshCells: refCells2D,
                DurationSeconds: timer.Elapsed.TotalSeconds,
                Metrics: ExtractMetrics(rec2D_5, metricsToMonitor),
                Fitness: rec2D_5.Fitness,
                Converged: refConv2D,
                YPlusP95: refP95_2D,
                YPlusOk: refYPlusOk2D));

            // Zwischenstufen 2D: 1.41 (Kal2D_2), 1.00 (Kal2D_3), 0.71 (Kal2D_4)
            var intermediateConfigs2D = new[]
            {
                (Index: 2, Factor: 1.41f, Tag: "Kal2D_2"),
                (Index: 3, Factor: 1.00f, Tag: "Kal2D_3"),
                (Index: 4, Factor: 0.71f, Tag: "Kal2D_4")
            };

            foreach (var inter in intermediateConfigs2D)
            {
                Console.WriteLine($"\n[2D-KALIBRIERUNG] Stufe {inter.Index}/5: Zwischenstufe (Faktor {inter.Factor:0.00})...");
                timer.Restart();
                ModelRecord recInter = runStage2D(inter.Factor, inter.Tag);
                timer.Stop();

                int iCells = (int)(recInter.PassiveParameters.TryGetValue("MeshCells", out float ic) ? ic : 0);
                float iP95 = recInter.PassiveParameters.TryGetValue("YPlus_P95", out float ip95) ? ip95 : 0f;
                bool iConv = !recInter.SimulationFailed && (!recInter.PassiveParameters.TryGetValue("Su2_Converged", out float iconv) || iconv > 0.5f);
                bool iYPlusOk = iP95 <= 0f || iP95 <= 2.5f;

                stages2D.Add(new CalibrationStageData(
                    LevelIndex: inter.Index,
                    Factor: inter.Factor,
                    MeshCells: iCells,
                    DurationSeconds: timer.Elapsed.TotalSeconds,
                    Metrics: ExtractMetrics(recInter, metricsToMonitor),
                    Fitness: recInter.Fitness,
                    Converged: iConv,
                    YPlusP95: iP95,
                    YPlusOk: iYPlusOk));

                if (stages2D.Count >= 3)
                {
                    var tempDec = CalibrationEvaluator.EvaluateCalibration(stages2D, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance);
                    if (tempDec.Success && tempDec.SelectedStage != null && tempDec.SelectedStage.LevelIndex == inter.Index)
                    {
                        Console.WriteLine($"       -> 2D-Stufe {inter.Index} erfüllt bereits die Toleranz!");
                        break;
                    }
                }
            }

            var decision2D = CalibrationEvaluator.EvaluateCalibration(stages2D, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance);
            float factor2D = decision2D.SelectedStage?.Factor ?? 1.0f;
            Console.WriteLine($"\n[2D-KALIBRIERUNG] Ermittelter 2D-Faktor: f2D = {factor2D:0.00} (Erfolg={decision2D.Success})");

            // -------------------------------------------------------------
            // 2. 3D-Bestätigung (drei Stufen um f2D)
            // -------------------------------------------------------------
            Console.WriteLine("\n--- TEIL 2: 3D-BESTÄTIGUNG UM f2D ---");
            float[] factors3D = CalibrationEvaluator.Build3DConfirmationFactors(factor2D);
            float fCoarse = factors3D[0];  // f2D * √2
            float fMed = factors3D[1];     // f2D
            float fFine = factors3D[2];    // f2D / √2

            var stages3D = new List<CalibrationStageData>();

            // 3D Stufe 1: Grob (f2D * √2, Tag: Kal3D_1)
            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 1/3: Grobes 3D-Netz (Faktor {fCoarse:0.00})...");
            timer.Restart();
            ModelRecord rec3D_1 = runStage3D(fCoarse, "Kal3D_1");
            timer.Stop();

            int c3Cells = (int)(rec3D_1.PassiveParameters.TryGetValue("MeshCells", out float c3c) ? c3c : 0);
            float c3P95 = rec3D_1.PassiveParameters.TryGetValue("YPlus_P95", out float c3p) ? c3p : 0f;
            bool c3Conv = !rec3D_1.SimulationFailed && (!rec3D_1.PassiveParameters.TryGetValue("Su2_Converged", out float c3cv) || c3cv > 0.5f);
            bool c3YPlusOk = c3P95 <= 0f || c3P95 <= 3.0f;

            stages3D.Add(new CalibrationStageData(
                LevelIndex: 1,
                Factor: fCoarse,
                MeshCells: c3Cells,
                DurationSeconds: timer.Elapsed.TotalSeconds,
                Metrics: ExtractMetrics(rec3D_1, metricsToMonitor),
                Fitness: rec3D_1.Fitness,
                Converged: c3Conv,
                YPlusP95: c3P95,
                YPlusOk: c3YPlusOk));

            if (c3Cells > 0)
            {
                long estimatedFine = (long)(c3Cells * Math.Pow(fCoarse / Math.Max(fFine, 0.1f), 3));
                if (estimatedFine > maxEstimatedFineCells)
                {
                    throw new InvalidOperationException(
                        $"[KALIBRIERUNG 3D] Hochgerechnete Zellzahl ({estimatedFine:N0}) überschreitet Limit ({maxEstimatedFineCells:N0}).");
                }
            }

            // 3D Stufe 3: Feine 3D-Referenz (f2D / √2, Tag: Kal3D_3)
            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 3/3: Feine 3D-Referenz (Faktor {fFine:0.00})...");
            timer.Restart();
            ModelRecord rec3D_3 = runStage3D(fFine, "Kal3D_3");
            timer.Stop();

            int f3Cells = (int)(rec3D_3.PassiveParameters.TryGetValue("MeshCells", out float f3c) ? f3c : 0);
            float f3P95 = rec3D_3.PassiveParameters.TryGetValue("YPlus_P95", out float f3p) ? f3p : 0f;
            bool f3Conv = !rec3D_3.SimulationFailed && (!rec3D_3.PassiveParameters.TryGetValue("Su2_Converged", out float f3cv) || f3cv > 0.5f);
            bool f3YPlusOk = f3P95 <= 0f || f3P95 <= 2.5f;

            stages3D.Add(new CalibrationStageData(
                LevelIndex: 3,
                Factor: fFine,
                MeshCells: f3Cells,
                DurationSeconds: timer.Elapsed.TotalSeconds,
                Metrics: ExtractMetrics(rec3D_3, metricsToMonitor),
                Fitness: rec3D_3.Fitness,
                Converged: f3Conv,
                YPlusP95: f3P95,
                YPlusOk: f3YPlusOk));

            // 3D Stufe 2: Mittlere Stufe (f2D, Tag: Kal3D_2)
            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 2/3: 2D-Faktor in 3D (Faktor {fMed:0.00})...");
            timer.Restart();
            ModelRecord rec3D_2 = runStage3D(fMed, "Kal3D_2");
            timer.Stop();

            int m3Cells = (int)(rec3D_2.PassiveParameters.TryGetValue("MeshCells", out float m3c) ? m3c : 0);
            float m3P95 = rec3D_2.PassiveParameters.TryGetValue("YPlus_P95", out float m3p) ? m3p : 0f;
            bool m3Conv = !rec3D_2.SimulationFailed && (!rec3D_2.PassiveParameters.TryGetValue("Su2_Converged", out float m3cv) || m3cv > 0.5f);
            bool m3YPlusOk = m3P95 <= 0f || m3P95 <= 2.5f;

            stages3D.Add(new CalibrationStageData(
                LevelIndex: 2,
                Factor: fMed,
                MeshCells: m3Cells,
                DurationSeconds: timer.Elapsed.TotalSeconds,
                Metrics: ExtractMetrics(rec3D_2, metricsToMonitor),
                Fitness: rec3D_2.Fitness,
                Converged: m3Conv,
                YPlusP95: m3P95,
                YPlusOk: m3YPlusOk));

            // Auswertung der 3-stufigen 3D-Bestätigung
            var decision3D = CalibrationEvaluator.EvaluateConfirmation3D(factor2D, stages3D, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance);

            // Falls weder f2D * √2 noch f2D bestätigen, weiter verfeinern (f2D / 2.0)
            if (!decision3D.Success)
            {
                float fRefined = (float)Math.Round(factor2D / 2.0f, 2);
                if (fRefined > 0.05f && !stages3D.Any(s => Math.Abs(s.Factor - fRefined) < 0.01f))
                {
                    Console.WriteLine($"\n[3D-BESTÄTIGUNG] Keine Bestätigung. Zusätzliche 3D-Verfeinerungsstufe (Faktor {fRefined:0.00}, Kal3D_4)...");
                    timer.Restart();
                    ModelRecord recRefined = runStage3D(fRefined, "Kal3D_4");
                    timer.Stop();

                    int rCells = (int)(recRefined.PassiveParameters.TryGetValue("MeshCells", out float rc) ? rc : 0);
                    float rP95 = recRefined.PassiveParameters.TryGetValue("YPlus_P95", out float rp) ? rp : 0f;
                    bool rConv = !recRefined.SimulationFailed && (!recRefined.PassiveParameters.TryGetValue("Su2_Converged", out float rcv) || rcv > 0.5f);
                    bool rYPlusOk = rP95 <= 0f || rP95 <= 2.5f;

                    stages3D.Add(new CalibrationStageData(
                        LevelIndex: 4,
                        Factor: fRefined,
                        MeshCells: rCells,
                        DurationSeconds: timer.Elapsed.TotalSeconds,
                        Metrics: ExtractMetrics(recRefined, metricsToMonitor),
                        Fitness: recRefined.Fitness,
                        Converged: rConv,
                        YPlusP95: rP95,
                        YPlusOk: rYPlusOk));

                    decision3D = CalibrationEvaluator.EvaluateCalibration(stages3D, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance);
                }
            }

            // -------------------------------------------------------------
            // 3. Kombinierte Berichte schreiben
            // -------------------------------------------------------------
            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string calibSubdir = Path.Combine(CalibrationDirectoryName, timestamp);
            string fullCalibDir = Path.Combine(simulationContext.WorkingDirectory, calibSubdir);
            Directory.CreateDirectory(fullCalibDir);

            string fingerprint = ComputeFingerprint(context);

            SaveCsvReport(Path.Combine(fullCalibDir, "calibration_2d.csv"), decision2D, metricsToMonitor);
            SaveCsvReport(Path.Combine(fullCalibDir, "calibration.csv"), decision3D, metricsToMonitor);

            SaveCombinedJsonReport(
                Path.Combine(fullCalibDir, "calibration.json"),
                decision3D,
                decision2D,
                factor2D,
                timestamp,
                fingerprint,
                context.Simulation.MeshCalibration.Tolerance);

            string latestJson = Path.Combine(simulationContext.WorkingDirectory, CalibrationDirectoryName, LatestCalibrationJson);
            string latestCsv = Path.Combine(simulationContext.WorkingDirectory, CalibrationDirectoryName, LatestCalibrationCsv);
            File.Copy(Path.Combine(fullCalibDir, "calibration.json"), latestJson, overwrite: true);
            File.Copy(Path.Combine(fullCalibDir, "calibration.csv"), latestCsv, overwrite: true);

            PrintSummary(decision3D);

            if (decision3D.Success && decision3D.SelectedStage != null)
            {
                simulationContext.EffectiveMeshRefinementFactor = decision3D.SelectedStage.Factor;
                simulationContext.CalibrationReportPath = calibSubdir.Replace('\\', '/');
                simulationContext.MeshRefinementSource = $"KALIBRIERT: 2D-Vorkalibrierung, in 3D bestätigt, Toleranz {context.Simulation.MeshCalibration.Tolerance * 100:0.0} %";
            }

            return (decision3D, calibSubdir.Replace('\\', '/'));
        }

        private static List<string> ResolveMonitoredMetrics(ProjectContext context)
        {
            var list = new List<string>();
            if (context.ProjectDirectory != null)
            {
                string su2Path = Path.Combine(context.ProjectDirectory, "solvers", "su2.json");
                if (File.Exists(su2Path))
                {
                    try
                    {
                        var node = JsonNode.Parse(File.ReadAllText(su2Path));
                        if (node?["ResultMetrics"] is JsonObject rm)
                        {
                            foreach (var entry in rm) list.Add(entry.Key);
                        }
                    }
                    catch { }
                }
            }

            if (list.Count == 0) list.Add("Drag");
            return list;
        }

        private static Dictionary<string, float> ExtractMetrics(ModelRecord record, List<string> metricsToMonitor)
        {
            var dict = new Dictionary<string, float>();
            foreach (var m in metricsToMonitor)
            {
                if (record.PassiveParameters.TryGetValue(m, out float val))
                {
                    dict[m] = val;
                }
            }
            return dict;
        }

        private static void SaveCsvReport(string csvPath, CalibrationDecision decision, List<string> metrics)
        {
            var csv = new StringBuilder();
            csv.Append("Stufe;Faktor;MeshCells;Dauer_s;");
            foreach (var m in metrics) csv.Append($"{m};");
            csv.AppendLine("AbweichungRef;Konvergiert;YPlus_P95;YPlus_Ok");

            foreach (var s in decision.AllStages.OrderBy(st => st.LevelIndex))
            {
                csv.Append($"{s.LevelIndex};{s.Factor.ToString("0.00", CultureInfo.InvariantCulture)};{s.MeshCells};{s.DurationSeconds:F1};");
                foreach (var m in metrics)
                {
                    float val = s.Metrics.TryGetValue(m, out float v) ? v : 0f;
                    csv.Append($"{val.ToString("F5", CultureInfo.InvariantCulture)};");
                }

                float maxDev = 0f;
                if (decision.RelativeDeviationsToRef.TryGetValue(s.LevelIndex, out var devs))
                {
                    maxDev = devs.Count > 0 ? devs.Values.Max() : 0f;
                }

                csv.AppendLine($"{maxDev.ToString("P2", CultureInfo.InvariantCulture)};{(s.Converged ? 1 : 0)};{s.YPlusP95.ToString("F2", CultureInfo.InvariantCulture)};{(s.YPlusOk ? 1 : 0)}");
            }

            File.WriteAllText(csvPath, csv.ToString(), Encoding.UTF8);
        }

        private static JsonArray BuildStagesJsonArray(IReadOnlyList<CalibrationStageData> stages)
        {
            var stagesArray = new JsonArray();
            foreach (var s in stages.OrderBy(st => st.LevelIndex))
            {
                var stageObj = new JsonObject
                {
                    ["LevelIndex"] = s.LevelIndex,
                    ["Factor"] = s.Factor,
                    ["MeshCells"] = s.MeshCells,
                    ["DurationSeconds"] = s.DurationSeconds,
                    ["Converged"] = s.Converged,
                    ["YPlusP95"] = s.YPlusP95,
                    ["YPlusOk"] = s.YPlusOk
                };
                var mObj = new JsonObject();
                foreach (var kvp in s.Metrics) mObj[kvp.Key] = kvp.Value;
                stageObj["Metrics"] = mObj;
                stagesArray.Add(stageObj);
            }
            return stagesArray;
        }

        private static void SaveCombinedJsonReport(
            string jsonPath,
            CalibrationDecision decision3D,
            CalibrationDecision decision2D,
            float factor2D,
            string timestamp,
            string fingerprint,
            float tolerance)
        {
            var root = new JsonObject
            {
                ["Timestamp"] = timestamp,
                ["Fingerprint"] = fingerprint,
                ["Tolerance"] = tolerance,
                ["Mode"] = "2D-Precalibration+3D-Confirmation",
                ["Success"] = decision3D.Success,
                ["Factor2D"] = factor2D,
                ["SelectedFactor"] = decision3D.SelectedStage?.Factor,
                ["SelectedLevelIndex"] = decision3D.SelectedStage?.LevelIndex,
                ["Summary"] = decision3D.SummaryMessage,
                ["Stages3D"] = BuildStagesJsonArray(decision3D.AllStages),
                ["Stages2D"] = BuildStagesJsonArray(decision2D.AllStages)
            };

            if (decision3D.Gci != null)
            {
                root["Gci"] = new JsonObject
                {
                    ["ObservedOrderP"] = decision3D.Gci.ObservedOrderP,
                    ["GciFinePercent"] = decision3D.Gci.GciFinePercent,
                    ["GciSelectedPercent"] = decision3D.Gci.GciSelectedPercent,
                    ["IsMonotonic"] = decision3D.Gci.IsMonotonic,
                    ["Warning"] = decision3D.Gci.Warning
                };
            }

            string jsonContent = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);
        }

        private static void SaveReports(
            string fullCalibDir,
            string workingDir,
            CalibrationDecision decision,
            List<string> metrics,
            string timestamp,
            string fingerprint,
            float tolerance)
        {
            // 1. calibration.csv
            string csvPath = Path.Combine(fullCalibDir, "calibration.csv");
            SaveCsvReport(csvPath, decision, metrics);

            // 2. calibration.json
            var stagesArray = BuildStagesJsonArray(decision.AllStages);

            var root = new JsonObject
            {
                ["Timestamp"] = timestamp,
                ["Fingerprint"] = fingerprint,
                ["Tolerance"] = tolerance,
                ["Success"] = decision.Success,
                ["SelectedFactor"] = decision.SelectedStage?.Factor,
                ["SelectedLevelIndex"] = decision.SelectedStage?.LevelIndex,
                ["Summary"] = decision.SummaryMessage,
                ["Stages"] = stagesArray
            };

            if (decision.Gci != null)
            {
                root["Gci"] = new JsonObject
                {
                    ["ObservedOrderP"] = decision.Gci.ObservedOrderP,
                    ["GciFinePercent"] = decision.Gci.GciFinePercent,
                    ["GciSelectedPercent"] = decision.Gci.GciSelectedPercent,
                    ["IsMonotonic"] = decision.Gci.IsMonotonic,
                    ["Warning"] = decision.Gci.Warning
                };
            }

            string jsonContent = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            string jsonPath = Path.Combine(fullCalibDir, "calibration.json");
            File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);

            // Kopie nach latest_calibration.* im Kalibrierungsverzeichnis
            string latestJson = Path.Combine(workingDir, CalibrationDirectoryName, LatestCalibrationJson);
            string latestCsv = Path.Combine(workingDir, CalibrationDirectoryName, LatestCalibrationCsv);
            File.WriteAllText(latestJson, jsonContent, Encoding.UTF8);
            File.Copy(csvPath, latestCsv, overwrite: true);
        }

        private static void PrintSummary(CalibrationDecision decision)
        {
            Console.WriteLine("\n==================================================");
            Console.WriteLine(" KALIBRIERUNGSERGEBNIS");
            Console.WriteLine("==================================================");
            Console.WriteLine(decision.SummaryMessage);

            if (decision.Gci != null)
            {
                Console.WriteLine($"   Beobachtete Ordnung p:        {decision.Gci.ObservedOrderP:F2}");
                Console.WriteLine($"   GCI (Feinstes Netz):          {decision.Gci.GciFinePercent:F2} %");
                Console.WriteLine($"   GCI (Gewähltes/Mittleres):    {decision.Gci.GciSelectedPercent:F2} %");
                if (!decision.Gci.IsMonotonic && decision.Gci.Warning != null)
                {
                    Console.WriteLine($"   [WARNUNG] {decision.Gci.Warning}");
                }
            }

            if (decision.Success && decision.SelectedStage != null)
            {
                Console.WriteLine($"\n-> Empfohlener Netzfaktor: {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} (Stufe {decision.SelectedStage.LevelIndex})");
            }
            Console.WriteLine("==================================================\n");
        }
    }
}
