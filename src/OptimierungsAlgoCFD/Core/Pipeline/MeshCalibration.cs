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

namespace OptimierungsAlgoCFD.Core
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
            sb.Append("mesher:").Append(MesherSchemeVersion).Append(';');

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

                // Maschinenspezifische Überschreibungen (*.local.json) verändern die Physik genauso und
                // gehören in den Fingerabdruck; sonst bliebe eine Kalibrierung trotz geänderter Netz- oder
                // Strömungswerte "aktuell" (TODO-W9).
                foreach (string local in new[] { "gmsh.local.json", "su2.local.json" })
                {
                    string localPath = Path.Combine(context.ProjectDirectory, "solvers", local);
                    if (File.Exists(localPath))
                    {
                        sb.Append(local).Append(':');
                        sb.Append(File.ReadAllText(localPath));
                    }
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
            long maxEstimatedFineCells = 50_000_000,
            int dimensions = 3,
            Func<double?>? durationProvider = null)
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
            var cal = context.Simulation.MeshCalibration;
            long maxCells = dimensions == 2 ? cal.MaxCells2D : Math.Min(maxEstimatedFineCells, cal.MaxCells3D);

            // Die Zellgrenze gilt für die GEMESSENE Zellzahl und wird nach dem Vernetzen geprüft, bevor SU2 rechnet.
            simulationContext.CellLimit = maxCells;
            try
            {
            // Stufe 1: Grob (2.0f, Tag: "Kal1")
            Console.WriteLine("\n[KALIBRIERUNG] Stufe 1/5: Grobes Netz (Faktor 2.00)...");
            var stage1 = RunCalibrationStage(runStage, 1, 2.0f, "Kal1", metricsToMonitor, 3.5f, durationProvider); // bei grobem Netz y+ oft höher
            stages.Add(stage1);

            // Vorschau: Zellzahl der feinsten Stufe grob hochrechnen. Mit nur einer Messung ist das ungenau
            // (Wandschicht und Oberfläche wachsen nicht kubisch; MantaAuv 09.11.2026: 4,4-fach zu hoch geschätzt),
            // deshalb nur ein Hinweis. Entscheidend ist die gemessene Zellzahl der Stufe 5.
            if (stage1.MeshCells > 0)
            {
                long estimatedFineCells = EstimateCells(stage1.MeshCells, 2.0f, 0.5f, dimensions);
                Console.WriteLine($"       -> Zellen grob: {stage1.MeshCells:N0} | grobe Vorschau für Referenz (0.50): ~{estimatedFineCells:N0} Zellen");

                if (estimatedFineCells > maxCells)
                {
                    Console.WriteLine($"       -> [HINWEIS] Die grobe Vorschau liegt über der Grenze von {maxCells:N0} Zellen. Entscheidend ist die gemessene Zellzahl nach dem Vernetzen der Stufe 5.");
                }
            }

            // Stufe 5: Sehr feine Referenz (0.50f, Tag: "Kal5")
            Console.WriteLine("\n[KALIBRIERUNG] Stufe 5/5: Feines Referenznetz (Faktor 0.50)...");
            var stage5 = RunCalibrationStage(runStage, 5, 0.5f, "Kal5", metricsToMonitor, 3.0f, durationProvider);
            if (stage5.CellLimitExceeded)
            {
                throw new InvalidOperationException(
                    $"[KALIBRIERUNG] Das Referenznetz (Faktor 0.50) hat gemessen {stage5.MeshCells:N0} Zellen und "
                    + $"überschreitet das Limit von {maxCells:N0}. Kalibrierung abgebrochen (MeshCalibration.MaxCells erhöhen).");
            }
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
                stages.Add(RunCalibrationStage(runStage, inter.Index, inter.Factor, inter.Tag, metricsToMonitor, 3.0f, durationProvider));

                // Frühabstiegsprüfung: Wenn diese Stufe innerhalb der Toleranz liegt und wir mindestens 3 Stufen für GCI haben
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

            // Hält die Toleranz bei Faktor 0.5 noch nicht, entscheidet die Kalibrierung selbst, wie fein es sein muss (TODO-W5).
            RefineAdaptively(stages, runStage, "Kal", "KALIBRIERUNG", metricsToMonitor, cal, dimensions, maxCells, durationProvider);
            }
            finally
            {
                simulationContext.CellLimit = 0;
            }

            // Vollständige Auswertung
            var decision = CalibrationEvaluator.EvaluateCalibration(stages, metricsToMonitor, cal.Tolerance, cal.AbsoluteTolerances);

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
            long maxEstimatedFineCells = 50_000_000,
            Func<double?>? durationProvider2D = null,
            Func<double?>? durationProvider3D = null)
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

            var cal2 = context.Simulation.MeshCalibration;

            // -------------------------------------------------------------
            // 1. 2D-Vorkalibrierung (fünf Stufen am Schnitt, bei Bedarf weiter verfeinert)
            // -------------------------------------------------------------
            Console.WriteLine("\n--- TEIL 1: 2D-VORKALIBRIERUNG AM QUERSCHNITT ---");
            var stages2D = new List<CalibrationStageData>();

            Console.WriteLine("\n[2D-KALIBRIERUNG] Stufe 1/5: Grobes 2D-Netz (Faktor 2.00)...");
            stages2D.Add(RunCalibrationStage(runStage2D, 1, 2.0f, "Kal2D_1", metricsToMonitor, 3.5f, durationProvider2D));

            Console.WriteLine("\n[2D-KALIBRIERUNG] Stufe 5/5: Feines 2D-Referenznetz (Faktor 0.50)...");
            stages2D.Add(RunCalibrationStage(runStage2D, 5, 0.5f, "Kal2D_5", metricsToMonitor, 3.0f, durationProvider2D));

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
                stages2D.Add(RunCalibrationStage(runStage2D, inter.Index, inter.Factor, inter.Tag, metricsToMonitor, 3.0f, durationProvider2D));

                if (stages2D.Count >= 3)
                {
                    var tempDec = CalibrationEvaluator.EvaluateCalibration(stages2D, metricsToMonitor, cal2.Tolerance, cal2.AbsoluteTolerances);
                    if (tempDec.Success && tempDec.SelectedStage != null && tempDec.SelectedStage.LevelIndex == inter.Index)
                    {
                        Console.WriteLine($"       -> 2D-Stufe {inter.Index} erfüllt bereits die Toleranz!");
                        break;
                    }
                }
            }

            RefineAdaptively(stages2D, runStage2D, "Kal2D_", "2D-KALIBRIERUNG", metricsToMonitor, cal2, 2, cal2.MaxCells2D, durationProvider2D);

            var decision2D = CalibrationEvaluator.EvaluateCalibration(stages2D, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance, context.Simulation.MeshCalibration.AbsoluteTolerances);
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

            // Zellgrenze an der GEMESSENEN Zellzahl, vor SU2 (wie in Run).
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

            // 3D Stufe 1: Grob (f2D * √2, Tag: Kal3D_1)
            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 1/3: Grobes 3D-Netz (Faktor {fCoarse:0.00})...");
            CalibrationStageData stage3D_1 = RunCalibrationStage(
                RunLimited3D, 1, fCoarse, "Kal3D_1", metricsToMonitor, 3.5f, durationProvider3D);
            stages3D.Add(stage3D_1);

            if (stage3D_1.MeshCells > 0)
            {
                long estimatedFine = (long)(stage3D_1.MeshCells * Math.Pow(fCoarse / Math.Max(fFine, 0.1f), 3));
                // Nur ein Hinweis: die grobe Hochrechnung ist ungenau, entscheidend ist die gemessene Zellzahl je Stufe.
                if (estimatedFine > maxEstimatedFineCells)
                {
                    Console.WriteLine($"       -> [HINWEIS] Die grobe Vorschau ({estimatedFine:N0} Zellen) liegt über dem Limit von {maxEstimatedFineCells:N0}. Gemessen wird nach dem Vernetzen.");
                }
            }

            // 3D Stufe 3: Feine 3D-Referenz (f2D / √2, Tag: Kal3D_3)
            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 3/3: Feine 3D-Referenz (Faktor {fFine:0.00})...");
            stages3D.Add(RunCalibrationStage(
                RunLimited3D, 3, fFine, "Kal3D_3", metricsToMonitor, 3.0f, durationProvider3D));

            // 3D Stufe 2: Mittlere Stufe (f2D, Tag: Kal3D_2)
            Console.WriteLine($"\n[3D-BESTÄTIGUNG] Stufe 2/3: 2D-Faktor in 3D (Faktor {fMed:0.00})...");
            stages3D.Add(RunCalibrationStage(
                RunLimited3D, 2, fMed, "Kal3D_2", metricsToMonitor, 3.0f, durationProvider3D));

            // Auswertung der 3-stufigen 3D-Bestätigung
            var decision3D = CalibrationEvaluator.EvaluateConfirmation3D(factor2D, stages3D, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance, context.Simulation.MeshCalibration.AbsoluteTolerances);

            // Falls weder f2D * √2 noch f2D bestätigen, weiter verfeinern (f2D / 2.0)
            if (!decision3D.Success)
            {
                float fRefined = (float)Math.Round(factor2D / 2.0f, 2);
                if (fRefined > 0.05f && !stages3D.Any(s => Math.Abs(s.Factor - fRefined) < 0.01f))
                {
                    Console.WriteLine($"\n[3D-BESTÄTIGUNG] Keine Bestätigung. Zusätzliche 3D-Verfeinerungsstufe (Faktor {fRefined:0.00}, Kal3D_4)...");
                    stages3D.Add(RunCalibrationStage(
                        RunLimited3D, 4, fRefined, "Kal3D_4", metricsToMonitor, 3.0f, durationProvider3D));

                    decision3D = CalibrationEvaluator.EvaluateCalibration(stages3D, metricsToMonitor, context.Simulation.MeshCalibration.Tolerance, context.Simulation.MeshCalibration.AbsoluteTolerances);
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

        // ===================================================================
        //  Gemeinsame Stufenlogik (TODO-W5)
        // ===================================================================

        /// <summary>Weitere Verfeinerungsstufen nach den fünf Standardstufen (0.35, 0.25, 0.18, 0.13).</summary>
        public const int MaxAdditionalStages = 4;

        /// <summary>Kleinster Netzfaktor, der noch versucht wird.</summary>
        public const float MinRefinementFactor = 0.1f;

        /// <summary>Hochgerechnete Zellzahl bei Wechsel von <paramref name="fromFactor"/> auf <paramref name="toFactor"/>.</summary>
        public static long EstimateCells(int cells, float fromFactor, float toFactor, int dimensions)
            => (long)(cells * Math.Pow(fromFactor / Math.Max(toFactor, 1e-3f), dimensions));

        /// <summary>
        /// Vorschau der Zellzahl für <paramref name="nextFactor"/> mit dem GEMESSENEN Exponenten der beiden feinsten
        /// bisherigen Stufen: <c>p = ln(N_fein / N_grob) / ln(f_grob / f_fein)</c>, begrenzt auf [dimensions/2, dimensions].
        /// Die kubische Annahme überschätzte am MantaAuv (09.11.2026) die Zellzahl um das 4,4-Fache, weil Wandschicht und
        /// Oberfläche nur quadratisch wachsen. Mit weniger als zwei Messpunkten gilt der Exponent <paramref name="dimensions"/>.
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

        /// <summary>Rechnet eine Kalibrierstufe und fasst Zellzahl, Konvergenz und y+ zusammen.</summary>
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
            PrintStageDuration(level, factor, stage.DurationSeconds);
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
            // "Instationär, aber stabil" (Wirbelablösung) zählt als brauchbar: das Mittel des Endfensters ist gültig,
            // seine Schwankung geht über Uncertainty in den Vergleich ein.
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
                LevelIndex: level,
                Factor: factor,
                MeshCells: cells,
                DurationSeconds: durationSeconds,
                Metrics: ExtractMetrics(rec, metricsToMonitor),
                Fitness: rec.Fitness,
                Converged: converged,
                YPlusP95: p95,
                YPlusOk: yPlusOk,
                Uncertainty: uncertainty.Count > 0 ? uncertainty : null,
                CellLimitExceeded: cellLimitExceeded,
                BoundaryLayerLayers: ReadInt("BoundaryLayerLayers"),
                BoundaryLayerThicknessMm: ReadFloat("BoundaryLayerThicknessMm", 0f),
                BoundaryLayerStage: ReadInt("BoundaryLayerStage"),
                SurfaceTriangles: ReadInt("MeshCells_Tri"),
                SurfaceQuads: ReadInt("MeshCells_Quad"),
                VolumePrisms: ReadInt("MeshCells_Prism"),
                VolumeHexahedra: ReadInt("MeshCells_Hexa"),
                VolumeTetrahedra: ReadInt("MeshCells_Tet"),
                VolumePyramids: ReadInt("MeshCells_Pyramid"),
                TunnelBlockagePercent: ReadFloat("TunnelBlockagePercent", float.NaN),
                PressureDragCoeff: ReadFloat("Su2_CD_p", float.NaN),
                ViscousDragCoeff: ReadFloat("Su2_CD_v", float.NaN));
        }

        /// <summary>Dauer einer Kalibrierstufe ins Log (Plan wandschicht_zellzahl.md, WZ10).</summary>
        private static void PrintStageDuration(int level, float factor, double seconds)
        {
            Console.WriteLine($"       ⏱ Dauer Stufe {level} (Faktor {factor.ToString("0.00", CultureInfo.InvariantCulture)}): {FormatDuration(seconds)}");
        }

        /// <summary>
        /// Verfeinert weiter (0.35, 0.25, …), solange keine gröbere Stufe die Toleranz gegenüber der
        /// feinsten hält (TODO-W5). Die Kalibrierung entscheidet damit selbst, wie fein das Netz sein muss.
        /// Abbruch, wenn die Toleranz hält, die feinste Stufe selbst nicht konvergiert bzw. y+ verletzt
        /// (feiner hilft dann nicht), die Zellzahl die Grenze überschreitet oder
        /// <see cref="MaxAdditionalStages"/> erreicht ist.
        /// </summary>
        private static void RefineAdaptively(
            List<CalibrationStageData> stages,
            Func<float, string, ModelRecord> runStage,
            string tagPrefix,
            string label,
            List<string> metricsToMonitor,
            MeshCalibrationConfig cal,
            int dimensions,
            long maxCells,
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
                    Console.WriteLine($"       -> [{label}] y+ der feinsten Stufe liegt außerhalb der Toleranz – ein feineres Netz ändert daran nichts (Wandschicht prüfen).");
                    return;
                }

                float next = NextFinerFactor(refStage.Factor);
                if (next < MinRefinementFactor) return;

                // Vorschau mit dem gemessenen Exponenten. Deutlich über der Grenze (> 1,5-fach) lohnt sich nicht einmal das
                // Vernetzen; knapp darüber oder darunter entscheidet die gemessene Zellzahl nach dem Vernetzen (vor SU2).
                long estimated = EstimateNextCells(stages, next, dimensions);
                if (refStage.MeshCells > 0 && estimated > maxCells * 1.5)
                {
                    Console.WriteLine($"       -> [{label}] Nächste Stufe (Faktor {next:0.00}) hätte ~{estimated:N0} Zellen und überschreitet die Grenze von {maxCells:N0} deutlich – Verfeinerung endet.");
                    return;
                }

                int level = stages.Max(s => s.LevelIndex) + 1;
                Console.WriteLine($"\n[{label}] Toleranz von {cal.Tolerance * 100:0.0} % noch nicht erreicht – zusätzliche Stufe {level} (Faktor {next:0.00}, Vorschau ~{estimated:N0} Zellen, Grenze {maxCells:N0} gemessen)...");
                var extraStage = RunCalibrationStage(runStage, level, next, $"{tagPrefix}{level}", metricsToMonitor, 3.0f, durationProvider);
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

        /// <summary>Überwachte Größen = Schlüssel von <c>ResultMetrics</c> aus <c>solvers/su2.json</c>, sonst "Drag".</summary>
        public static List<string> ResolveMonitoredMetrics(ProjectContext context)
        {
            var list = new List<string>();
            if (context.ProjectDirectory != null)
            {
                string su2Path = Path.Combine(context.ProjectDirectory, "solvers", "su2.json");
                if (File.Exists(su2Path))
                {
                    try
                    {
                        // Die Projektdateien tragen //-Kommentare. Ohne CommentHandling scheiterte
                        // das Parsen still, und es wurde nur "Drag" ueberwacht (Servertest 06.11.2026).
                        var node = JsonNode.Parse(
                            File.ReadAllText(su2Path),
                            documentOptions: new JsonDocumentOptions
                            {
                                CommentHandling = JsonCommentHandling.Skip,
                                AllowTrailingCommas = true,
                            });
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
            csv.AppendLine("AbweichungRef;Konvergiert;YPlus_P95;YPlus_Ok;NormiertRef;Schichten;SchichtDicke_mm;Wandstufe;Tri;Quad;Prism;Hexa;Tet;Pyramiden;Blockage_%;CD_p;CD_v");

            foreach (var s in decision.AllStages.OrderBy(st => st.LevelIndex))
            {
                csv.Append($"{s.LevelIndex};{s.Factor.ToString("0.00", CultureInfo.InvariantCulture)};{s.MeshCells};{s.DurationSeconds.ToString("F1", CultureInfo.InvariantCulture)};");
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

                // Normiert nach MetricTolerance (TODO-W1): höchstens 1 = innerhalb der Toleranz inkl. Mindestbetrag.
                float maxNorm = 0f;
                if (decision.NormalizedDeviationsToRef != null
                    && decision.NormalizedDeviationsToRef.TryGetValue(s.LevelIndex, out var norms) && norms.Count > 0)
                {
                    maxNorm = norms.Values.Max();
                }

                string blockageStr = float.IsFinite(s.TunnelBlockagePercent)
                    ? s.TunnelBlockagePercent.ToString("F2", CultureInfo.InvariantCulture)
                    : string.Empty;
                string cdPStr = float.IsFinite(s.PressureDragCoeff)
                    ? s.PressureDragCoeff.ToString("F5", CultureInfo.InvariantCulture)
                    : string.Empty;
                string cdVStr = float.IsFinite(s.ViscousDragCoeff)
                    ? s.ViscousDragCoeff.ToString("F5", CultureInfo.InvariantCulture)
                    : string.Empty;

                csv.AppendLine($"{maxDev.ToString("P2", CultureInfo.InvariantCulture)};{(s.Converged ? 1 : 0)};{s.YPlusP95.ToString("F2", CultureInfo.InvariantCulture)};{(s.YPlusOk ? 1 : 0)};{maxNorm.ToString("F2", CultureInfo.InvariantCulture)};{s.BoundaryLayerLayers};{s.BoundaryLayerThicknessMm.ToString("F2", CultureInfo.InvariantCulture)};{s.BoundaryLayerStage};{s.SurfaceTriangles};{s.SurfaceQuads};{s.VolumePrisms};{s.VolumeHexahedra};{s.VolumeTetrahedra};{s.VolumePyramids};{blockageStr};{cdPStr};{cdVStr}");
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
                    ["YPlusOk"] = s.YPlusOk,
                    ["BoundaryLayerLayers"] = s.BoundaryLayerLayers,
                    ["BoundaryLayerThicknessMm"] = s.BoundaryLayerThicknessMm,
                    ["BoundaryLayerStage"] = s.BoundaryLayerStage,
                    ["SurfaceTriangles"] = s.SurfaceTriangles,
                    ["SurfaceQuads"] = s.SurfaceQuads,
                    ["VolumePrisms"] = s.VolumePrisms,
                    ["VolumeHexahedra"] = s.VolumeHexahedra,
                    ["VolumeTetrahedra"] = s.VolumeTetrahedra,
                    ["VolumePyramids"] = s.VolumePyramids
                };
                if (float.IsFinite(s.TunnelBlockagePercent))
                    stageObj["TunnelBlockagePercent"] = s.TunnelBlockagePercent;
                if (float.IsFinite(s.PressureDragCoeff))
                    stageObj["CD_p"] = s.PressureDragCoeff;
                if (float.IsFinite(s.ViscousDragCoeff))
                    stageObj["CD_v"] = s.ViscousDragCoeff;

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
                    ["Warning"] = decision3D.Gci.Warning,
                    // Richardson-Extrapolation der Hauptgröße (null, wenn die Folge nicht monoton ist)
                    ["ExtrapolatedValue"] = float.IsFinite(decision3D.Gci.ExtrapolatedValue) ? decision3D.Gci.ExtrapolatedValue : null,
                    ["ExtrapolatedDeviationPercent"] = float.IsFinite(decision3D.Gci.ExtrapolatedDeviationPercent) ? decision3D.Gci.ExtrapolatedDeviationPercent : null
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
                    ["Warning"] = decision.Gci.Warning,
                    // Richardson-Extrapolation der Hauptgröße (null, wenn die Folge nicht monoton ist)
                    ["ExtrapolatedValue"] = float.IsFinite(decision.Gci.ExtrapolatedValue) ? decision.Gci.ExtrapolatedValue : null,
                    ["ExtrapolatedDeviationPercent"] = float.IsFinite(decision.Gci.ExtrapolatedDeviationPercent) ? decision.Gci.ExtrapolatedDeviationPercent : null
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

        /// <summary>
        /// Warnt, wenn die Stufen mit unterschiedlicher Wandschicht (Schichtzahl, Dicke oder Ersatznetz) gerechnet wurden:
        /// dann misst die Reihe nicht nur den Netzeinfluss (Bericht-Lauf 09.11.2026: 21 → 16 Schichten).
        /// <c>null</c>, wenn alles gleich ist oder keine Wandschicht-Angaben vorliegen.
        /// </summary>
        public static string? DescribeLayerMismatch(IReadOnlyList<CalibrationStageData> stages)
        {
            var withLayers = stages.Where(st => st.BoundaryLayerLayers > 0).OrderBy(st => st.LevelIndex).ToList();
            if (withLayers.Count == 0) return null;

            var counts = withLayers.Select(st => st.BoundaryLayerLayers).Distinct().ToList();
            if (counts.Count > 1)
            {
                string list = string.Join(", ", withLayers.Select(st => $"Stufe {st.LevelIndex}: {st.BoundaryLayerLayers}"));
                return $"Die Stufen haben unterschiedliche Schichtzahlen ({list}). Die Netzstudie ist dadurch nicht vergleichbar.";
            }

            var fallback = withLayers.Where(st => st.BoundaryLayerStage >= 3).Select(st => st.LevelIndex).ToList();
            if (fallback.Count > 0)
            {
                return $"Stufe(n) {string.Join(", ", fallback)} wurden mit Ersatznetz (überall verdünnte Wandschicht) gerechnet und sind nur eingeschränkt vergleichbar.";
            }

            return null;
        }

        internal static void PrintSummary(CalibrationDecision decision, TextWriter? output = null)
        {
            TextWriter writer = output ?? Console.Out;
            writer.WriteLine("\n==================================================");
            writer.WriteLine(" KALIBRIERUNGSERGEBNIS");
            writer.WriteLine("==================================================");
            writer.WriteLine(decision.SummaryMessage);

            double totalSeconds = decision.AllStages.Sum(st => st.DurationSeconds);
            writer.WriteLine($"   Dauer der Kalibrierung gesamt: {FormatDuration(totalSeconds)} ({decision.AllStages.Count} Stufen)");
            foreach (var st in decision.AllStages.OrderBy(st => st.LevelIndex))
            {
                var extras = new List<string>();
                int totalBreakdown = st.SurfaceTriangles + st.SurfaceQuads + st.VolumePrisms
                                     + st.VolumeHexahedra + st.VolumeTetrahedra + st.VolumePyramids;
                if (totalBreakdown > 0)
                {
                    extras.Add($"Tri={st.SurfaceTriangles:N0}, Quad={st.SurfaceQuads:N0}, Prism={st.VolumePrisms:N0}, Hexa={st.VolumeHexahedra:N0}, Tet={st.VolumeTetrahedra:N0}, Pyr={st.VolumePyramids:N0}");
                }
                if (float.IsFinite(st.TunnelBlockagePercent))
                {
                    extras.Add($"Blockage={st.TunnelBlockagePercent.ToString("0.00", CultureInfo.InvariantCulture)}%");
                }
                if (float.IsFinite(st.PressureDragCoeff) || float.IsFinite(st.ViscousDragCoeff))
                {
                    string cdp = float.IsFinite(st.PressureDragCoeff)
                        ? st.PressureDragCoeff.ToString("0.#####", CultureInfo.InvariantCulture)
                        : "-";
                    string cdv = float.IsFinite(st.ViscousDragCoeff)
                        ? st.ViscousDragCoeff.ToString("0.#####", CultureInfo.InvariantCulture)
                        : "-";
                    extras.Add($"CD_p={cdp}, CD_v={cdv}");
                }
                string extraSuffix = extras.Count > 0 ? $" | {string.Join(" | ", extras)}" : string.Empty;
                writer.WriteLine($"     Stufe {st.LevelIndex} (Faktor {st.Factor.ToString("0.00", CultureInfo.InvariantCulture)}): {FormatDuration(st.DurationSeconds)}, {st.MeshCells:N0} Zellen{extraSuffix}");
            }

            string? layerWarning = DescribeLayerMismatch(decision.AllStages);
            if (layerWarning != null) writer.WriteLine($"   [WARNUNG] {layerWarning}");

            if (decision.Gci != null)
            {
                writer.WriteLine($"   Beobachtete Ordnung p:        {decision.Gci.ObservedOrderP:F2}");
                writer.WriteLine($"   GCI (Feinstes Netz):          {decision.Gci.GciFinePercent:F2} %");
                writer.WriteLine($"   GCI (Gewähltes/Mittleres):    {decision.Gci.GciSelectedPercent:F2} %");
                if (float.IsFinite(decision.Gci.ExtrapolatedValue))
                {
                    writer.WriteLine($"   Extrapolierter netzunabhängiger Wert (Hauptgröße): {decision.Gci.ExtrapolatedValue.ToString("0.#####", CultureInfo.InvariantCulture)} "
                        + $"(feinstes Netz weicht {decision.Gci.ExtrapolatedDeviationPercent:0.0} % davon ab)");
                }
                if (!decision.Gci.IsMonotonic && decision.Gci.Warning != null)
                {
                    writer.WriteLine($"   [WARNUNG] {decision.Gci.Warning}");
                }
            }

            if (decision.Success && decision.SelectedStage != null)
            {
                writer.WriteLine($"\n-> Empfohlener Netzfaktor: {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} (Stufe {decision.SelectedStage.LevelIndex})");
            }
            else if (!decision.Success && decision.FallbackStage != null)
            {
                writer.WriteLine($"   -> Empfohlener Rückfall-Faktor (feinste gültige Kandidatenstufe): {decision.FallbackStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} (Stufe {decision.FallbackStage.LevelIndex})");
            }
            writer.WriteLine("==================================================\n");
        }
    }
}
