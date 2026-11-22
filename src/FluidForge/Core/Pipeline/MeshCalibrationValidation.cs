using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Configuration and consistency checks as well as fingerprint computation for <see cref="MeshCalibration"/>.
    /// </summary>
    internal static class MeshCalibrationValidation
    {
        /// <summary>
        /// Rule 1 (traceability): validates the calibration configuration prior to running.
        /// </summary>
        public static void ValidateConfig(SimulationConfig config, ConfigLoadReport? simulationReport, string projectName)
        {
            string mode = (config.MeshCalibration.Mode ?? "Manual").Trim();

            if (!mode.Equals("Manual", StringComparison.OrdinalIgnoreCase)
                && !mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} Unknown MeshCalibration.Mode '{config.MeshCalibration.Mode}'. "
                    + "Valid values are 'Manual' and 'Auto'.");
            }

            if (mode.Equals("Auto", StringComparison.OrdinalIgnoreCase))
            {
                if (simulationReport != null && simulationReport.Origins.TryGetValue("MeshRefinementFactor", out string? originFile))
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} In Auto mode, the mesh factor is calibrated automatically. Please remove 'MeshRefinementFactor' "
                        + $"from {originFile} — only MeshCalibration.Tolerance should be specified.");
                }
            }
        }

        /// <summary>
        /// Validates whether the project supports 2D precalibration (TODO-48).
        /// Throws an informative exception if the project has no representative 2D cross-section (<see cref="IHasRepresentativeSection"/>).
        /// </summary>
        public static void ValidatePrecalibrationSupport(SimulationConfig config, IProjectDefinition definition)
        {
            if (config.MeshCalibration.Use2DPrecalibration && definition is not IHasRepresentativeSection)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.CalibrationTag} 'Use2DPrecalibration' is enabled in simulation.json, but project '{definition.Name}' has no representative 2D cross-section (IHasRepresentativeSection). For 3D bodies without a uniform profile (such as MantaAuv), 2D precalibration is not physically meaningful — please set 'Use2DPrecalibration': false.");
            }
        }

        /// <summary>
        /// Resolves the y+ acceptance limits (standard and coarse-level limit) from <see cref="ICalibrationSolverOptions"/>
        /// (or generically from <c>solvers/*.json</c>), consistent with the solver check (<c>TargetYPlus * YPlusWarnFactor</c>).
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

            // Coarse level 1 (factor 2.00) is allowed slightly above the standard limit (at least +0.5 or +15 %).
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
        /// Checks startup consistency between mesher and solver options as well as automatic mesh calibration:
        /// warns if <c>TargetYPlus</c> differs between mesher and solver, and prevents multi-hour doomed runs
        /// when <c>BoundaryLayerMode = "Threshold"</c> (y+ ≈ 30-70) is combined with active auto-calibration
        /// under a low y+ limit.
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
                    + $"and solvers/su2.json (TargetYPlus = {solver.TargetYPlus.ToString("0.##", CultureInfo.InvariantCulture)}) differ. "
                    + "gmsh.json sets the actual first cell height y1, while su2.json * YPlusWarnFactor defines the hard limit in solver and auto-calibration — please align them.";
                if (warningWriter != null)
                    warningWriter.WriteLine($"{OutputConventions.WarningTag} {mismatchMsg}");
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
                        $"{OutputConventions.ConfigTag} BoundaryLayerMode 'Threshold' in solvers/gmsh.json typically produces y+ ≈ 30-70, "
                        + $"but automatic mesh calibration enforces a hard limit of TargetYPlus * YPlusWarnFactor = {limit.ToString("0.00", CultureInfo.InvariantCulture)} "
                        + "and would abort on the reference level (finer meshes do not change y1). "
                        + "Use 'BoundaryLayerMode': \"Prism\" for production runs — or for coarse preliminary runs set 'MeshCalibration.Mode': \"Manual\" "
                        + "with a fixed 'MeshRefinementFactor', or raise 'YPlusWarnFactor' in solvers/su2.json accordingly.");
                }
            }
        }

        /// <summary>
        /// Computes a SHA256 fingerprint over the baseline geometry (BaseParameters) and all solver/mesher
        /// JSON files under <c>solvers/*.json</c> including <c>*.local.json</c> (Rule 6).
        /// </summary>
        public static string ComputeFingerprint(ProjectContext context)
        {
            var sb = new StringBuilder();
            sb.Append("mesher:").Append(MeshCalibration.MesherSchemeVersion).Append(';');

            // 1. Sorted BaseParameters
            foreach (var kvp in context.Project.BaseParameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                sb.Append($"{kvp.Key}={kvp.Value.ToString("R", CultureInfo.InvariantCulture)};");
            }

            // 2. All *.json files under solvers/ (including *.local.json), deterministically sorted alphabetically
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
        /// In Manual mode, checks whether a prior calibration exists and prints hints (Rules 5 &amp; 6).
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
                    targetWriter.WriteLine("[HINT] Calibration is stale (configuration or baseline geometry has changed since then).");
                }

                if (node["SelectedFactor"] != null)
                {
                    float selectedFactor = node["SelectedFactor"]!.GetValue<float>();
                    string timestamp = node["Timestamp"]?.GetValue<string>() ?? "unknown";

                    if (Math.Abs(selectedFactor - context.Simulation.MeshRefinementFactor) > 0.001f)
                    {
                        targetWriter.WriteLine($"[HINT] Last calibration ({timestamp}) recommended {selectedFactor.ToString("0.00", CultureInfo.InvariantCulture)}, configured value is {context.Simulation.MeshRefinementFactor.ToString("0.00", CultureInfo.InvariantCulture)}.");
                    }
                }
            }
            catch
            {
                // Unreadable prior calibration is ignored
            }
        }
    }
}
