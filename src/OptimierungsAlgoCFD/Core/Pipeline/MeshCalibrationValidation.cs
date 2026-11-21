using System;
using System.Collections.Generic;
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
    /// Konfigurations- und Konsistenzprüfungen sowie Fingerabdruck-Berechnung für <see cref="MeshCalibration"/>.
    /// </summary>
    internal static class MeshCalibrationValidation
    {
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
        /// Ermittelt die y+-Akzeptanzgrenzen (Standard- und Grobstufen-Limit) aus <see cref="ICalibrationSolverOptions"/>
        /// (oder generisch aus <c>solvers/*.json</c>), konsistent zur Solver-Prüfung (<c>TargetYPlus × YPlusWarnFactor</c>).
        /// </summary>
        public static (float StandardLimit, float CoarseStageLimit) ResolveYPlusLimits(ProjectContext context)
        {
            ICalibrationSolverOptions? solver = context.Loaded
                .Select(l => l.Value)
                .OfType<ICalibrationSolverOptions>()
                .LastOrDefault();

            float target = float.NaN;
            float warnFactor = float.NaN;

            if (solver != null)
            {
                target = solver.TargetYPlus;
                warnFactor = solver.YPlusWarnFactor;
            }
            else if (context.ProjectDirectory != null)
            {
                string solversDir = Path.Combine(context.ProjectDirectory, "solvers");
                if (Directory.Exists(solversDir))
                {
                    float fallbackTarget = float.NaN;
                    foreach (string file in EnumerateSolverJsonFiles(solversDir))
                    {
                        try
                        {
                            var node = JsonNode.Parse(
                                File.ReadAllText(file),
                                documentOptions: new JsonDocumentOptions
                                {
                                    CommentHandling = JsonCommentHandling.Skip,
                                    AllowTrailingCommas = true,
                                });
                            if (node is not JsonObject obj) continue;

                            bool hasWarnFactor = TryReadPositiveFloat(obj["YPlusWarnFactor"], out float fileWarn);
                            if (hasWarnFactor)
                                warnFactor = fileWarn;

                            if (TryReadPositiveFloat(obj["TargetYPlus"], out float fileTarget))
                            {
                                bool isSolverConfig = hasWarnFactor
                                    || obj["ResultMetrics"] != null
                                    || obj["Dimensions"] != null;
                                if (isSolverConfig)
                                    target = fileTarget;
                                else
                                    fallbackTarget = fileTarget;
                            }
                        }
                        catch { }
                    }

                    if (!(target > 0f && float.IsFinite(target)) && fallbackTarget > 0f && float.IsFinite(fallbackTarget))
                    {
                        target = fallbackTarget;
                    }
                }
            }

            float effectiveTarget = target > 0f && float.IsFinite(target) ? target : 1.5f;
            float effectiveWarnFactor = warnFactor > 0f && float.IsFinite(warnFactor) ? warnFactor : 3.0f;
            float standardLimit = effectiveTarget * effectiveWarnFactor;

            // Grobe Stufe 1 (Faktor 2.00) darf wie bisher leicht über dem Standard-Limit liegen (mind. +0.5 bzw. +15 %).
            float coarseLimit = MathF.Round(MathF.Max(standardLimit + 0.5f, standardLimit * 1.15f), 2);
            return (standardLimit, coarseLimit);
        }

        internal static IEnumerable<string> EnumerateSolverJsonFiles(string solversDir)
            => Directory.EnumerateFiles(solversDir, "*.json", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase);

        internal static bool TryReadPositiveFloat(JsonNode? node, out float value)
        {
            value = 0f;
            if (node == null) return false;
            try
            {
                float parsed = node.GetValue<float>();
                if (parsed > 0f && float.IsFinite(parsed))
                {
                    value = parsed;
                    return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// Prüft beim Start die Konsistenz zwischen Vernetzer- und Solver-Optionen sowie der
        /// automatischen Netzkalibrierung: warnt bei abweichendem <c>TargetYPlus</c> zwischen Vernetzer und Solver
        /// und verhindert einen stundenlangen Fehlstart, wenn <c>BoundaryLayerMode = "Threshold"</c> (y+ ≈ 30–70)
        /// mit aktiver Auto-Kalibrierung unter niedrigem y+-Limit kombiniert wird.
        /// </summary>
        public static void ValidateYPlusConsistency(
            ProjectContext context,
            bool calibrationActive,
            TextWriter? warningWriter = null)
        {
            ICalibrationSolverOptions? solver = context.Loaded
                .Select(l => l.Value)
                .OfType<ICalibrationSolverOptions>()
                .LastOrDefault();
            if (solver == null) return;

            ICalibrationMesherOptions? mesher = context.Loaded
                .Select(l => l.Value)
                .OfType<ICalibrationMesherOptions>()
                .LastOrDefault();

            float? mesherTarget = mesher?.TargetYPlus;
            if (mesherTarget.HasValue && MathF.Abs(mesherTarget.Value - solver.TargetYPlus) > 0.05f)
            {
                string mismatchMsg =
                    $"solvers/gmsh.json (TargetYPlus = {mesherTarget.Value.ToString("0.##", CultureInfo.InvariantCulture)}) "
                    + $"und solvers/su2.json (TargetYPlus = {solver.TargetYPlus.ToString("0.##", CultureInfo.InvariantCulture)}) weichen voneinander ab. "
                    + "gmsh.json erzeugt die reale erste Zellhöhe y1, su2.json × YPlusWarnFactor bildet das harte Limit in Solver und Auto-Kalibrierung – bitte angleichen.";
                if (warningWriter != null)
                    warningWriter.WriteLine($"[WARNUNG] {mismatchMsg}");
                else
                    SimulationLog.Warning(mismatchMsg);
            }

            if (calibrationActive
                && mesher is { Is3D: true }
                && string.Equals((mesher.BoundaryLayerMode ?? "Prism").Trim(), "Threshold", StringComparison.OrdinalIgnoreCase))
            {
                float target = solver.TargetYPlus > 0f && float.IsFinite(solver.TargetYPlus) ? solver.TargetYPlus : 1.5f;
                float warnFactor = solver.YPlusWarnFactor > 0f && float.IsFinite(solver.YPlusWarnFactor) ? solver.YPlusWarnFactor : 3.0f;
                float limit = target * warnFactor;

                if (limit < 30f)
                {
                    throw new InvalidOperationException(
                        $"[KONFIGURATION] BoundaryLayerMode 'Threshold' in solvers/gmsh.json erzeugt typischerweise y+ ≈ 30–70, "
                        + $"die automatische Netzkalibrierung prüft aber hart gegen TargetYPlus × YPlusWarnFactor = {limit.ToString("0.00", CultureInfo.InvariantCulture)} "
                        + "und würde auf der Referenzstufe abbrechen (feinere Netze ändern an y1 nichts). "
                        + "Für belastbare Läufe 'BoundaryLayerMode': \"Prism\" nutzen – oder für grobe Vorabläufe 'MeshCalibration.Mode': \"Manual\" "
                        + "mit festem 'MeshRefinementFactor' setzen bzw. 'YPlusWarnFactor' in solvers/su2.json entsprechend anheben.");
                }
            }
        }

        /// <summary>
        /// Berechnet einen SHA256-Fingerabdruck über Startgeometrie (BaseParameters) und alle Solver-/Mesher-JSONs
        /// unter <c>solvers/*.json</c> inkl. <c>*.local.json</c> (Regel 6).
        /// </summary>
        public static string ComputeFingerprint(ProjectContext context)
        {
            var sb = new StringBuilder();
            sb.Append("mesher:").Append(MeshCalibration.MesherSchemeVersion).Append(';');

            // 1. Sortierte BaseParameters
            foreach (var kvp in context.Project.BaseParameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append($"{kvp.Key}={kvp.Value.ToString("R", CultureInfo.InvariantCulture)};");
            }

            // 2. Alle *.json-Dateien unter solvers/ (inkl. *.local.json), deterministisch alphabetisch sortiert
            if (context.ProjectDirectory != null)
            {
                string solversDir = Path.Combine(context.ProjectDirectory, "solvers");
                if (Directory.Exists(solversDir))
                {
                    foreach (string file in EnumerateSolverJsonFiles(solversDir))
                    {
                        string fileName = Path.GetFileName(file);
                        sb.Append(fileName).Append(':');
                        sb.Append(File.ReadAllText(file));
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
            string latestJsonPath = Path.Combine(workingDirectory, MeshCalibration.CalibrationDirectoryName, MeshCalibration.LatestCalibrationJson);
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
    }
}
