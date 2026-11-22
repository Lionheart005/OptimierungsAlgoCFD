using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Report generation, metric extraction, and console summary output for <see cref="MeshCalibration"/>.
    /// </summary>
    internal static class MeshCalibrationReporter
    {
        /// <summary>Monitored metrics from <see cref="ICalibrationSolverOptions.MonitoredMetricNames"/> or generically from <c>solvers/*.json</c>, defaulting to "Drag".</summary>
        public static List<string> ResolveMonitoredMetrics(ProjectContext context)
        {
            var list = new List<string>();
            var loadedMetrics = context.Loaded
                .Select(l => l.Value)
                .OfType<ICalibrationSolverOptions>()
                .LastOrDefault()
                ?.MonitoredMetricNames;

            if (loadedMetrics != null)
            {
                foreach (string metric in loadedMetrics)
                {
                    if (!string.IsNullOrWhiteSpace(metric) && !list.Contains(metric))
                        list.Add(metric);
                }
            }

            if (list.Count == 0 && context.ProjectDirectory != null)
            {
                string solversDir = Path.Combine(context.ProjectDirectory, "solvers");
                if (Directory.Exists(solversDir))
                {
                    foreach (string file in MeshCalibrationValidation.EnumerateSolverJsonFiles(solversDir))
                    {
                        try
                        {
                            // Project JSON files contain //-comments. Without CommentHandling, parsing
                            // failed silently and only "Drag" was monitored (server test 06.11.2026).
                            var node = JsonNode.Parse(
                                File.ReadAllText(file),
                                documentOptions: new JsonDocumentOptions
                                {
                                    CommentHandling = JsonCommentHandling.Skip,
                                    AllowTrailingCommas = true,
                                });
                            if (node?["ResultMetrics"] is JsonObject rm)
                            {
                                foreach (var entry in rm)
                                {
                                    if (!string.IsNullOrWhiteSpace(entry.Key) && !list.Contains(entry.Key))
                                        list.Add(entry.Key);
                                }
                            }
                        }
                        catch { }
                    }
                }
            }

            if (list.Count == 0) list.Add("Drag");
            return list;
        }

        public static Dictionary<string, float> ExtractMetrics(ModelRecord record, List<string> metricsToMonitor)
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

        public static void SaveCsvReport(string csvPath, CalibrationDecision decision, List<string> metrics)
        {
            var csv = new StringBuilder();
            csv.Append("Level;ScaleFactor;MeshCells;Duration_s;");
            foreach (var m in metrics) csv.Append($"{m};");
            csv.AppendLine("DeviationFromRef;Converged;YPlus_P95;YPlus_Ok;NormalizedToRef;Layers;LayerThickness_mm;WallStage;Tri;Quad;Prism;Hex;Tet;Pyramid;Blockage_pct;CD_p;CD_v");

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

                // Normalized per MetricTolerance (TODO-W1): at most 1 = within tolerance including absolute floor.
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

        public static JsonArray BuildStagesJsonArray(IReadOnlyList<CalibrationStageData> stages)
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

        public static void SaveCombinedJsonReport(
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
                root["Gci"] = BuildGciJsonObject(decision3D.Gci);
            }

            string jsonContent = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);
        }

        public static JsonObject BuildGciJsonObject(GciResult gci) => new()
        {
            ["ObservedOrderP"] = float.IsFinite(gci.ObservedOrderP) ? gci.ObservedOrderP : null,
            ["GciFinePercent"] = float.IsFinite(gci.GciFinePercent) ? gci.GciFinePercent : null,
            ["GciSelectedPercent"] = float.IsFinite(gci.GciSelectedPercent) ? gci.GciSelectedPercent : null,
            ["IsMonotonic"] = gci.IsMonotonic,
            ["IsAsymptotic"] = gci.IsAsymptotic,
            ["Warning"] = gci.Warning,
            // Richardson extrapolation of the primary metric (null when not in the asymptotic range)
            ["ExtrapolatedValue"] = float.IsFinite(gci.ExtrapolatedValue) ? gci.ExtrapolatedValue : null,
            ["ExtrapolatedDeviationPercent"] = float.IsFinite(gci.ExtrapolatedDeviationPercent) ? gci.ExtrapolatedDeviationPercent : null
        };

        public static void SaveReports(
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
                root["Gci"] = BuildGciJsonObject(decision.Gci);
            }

            string jsonContent = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            string jsonPath = Path.Combine(fullCalibDir, "calibration.json");
            File.WriteAllText(jsonPath, jsonContent, Encoding.UTF8);

            // Copy to latest_calibration.* in the calibration directory
            string latestJson = Path.Combine(workingDir, MeshCalibration.CalibrationDirectoryName, MeshCalibration.LatestCalibrationJson);
            string latestCsv = Path.Combine(workingDir, MeshCalibration.CalibrationDirectoryName, MeshCalibration.LatestCalibrationCsv);
            File.WriteAllText(latestJson, jsonContent, Encoding.UTF8);
            File.Copy(csvPath, latestCsv, overwrite: true);
        }

        /// <summary>
        /// Warns if levels were meshed with differing boundary-layer settings (layer count, thickness, or
        /// fallback mesh): in that case the sequence does not isolate pure mesh refinement (run 09.11.2026:
        /// 21 -> 16 layers). Returns <c>null</c> when consistent or when no boundary-layer data is present.
        /// </summary>
        public static string? DescribeLayerMismatch(IReadOnlyList<CalibrationStageData> stages)
        {
            var withLayers = stages.Where(st => st.BoundaryLayerLayers > 0).OrderBy(st => st.LevelIndex).ToList();
            if (withLayers.Count == 0) return null;

            var counts = withLayers.Select(st => st.BoundaryLayerLayers).Distinct().ToList();
            if (counts.Count > 1)
            {
                string list = string.Join(", ", withLayers.Select(st => $"Level {st.LevelIndex}: {st.BoundaryLayerLayers}"));
                return $"Levels have differing boundary-layer counts ({list}). The grid convergence study is not directly comparable.";
            }

            var fallback = withLayers.Where(st => st.BoundaryLayerStage >= 3).Select(st => st.LevelIndex).ToList();
            if (fallback.Count > 0)
            {
                return $"Level(s) {string.Join(", ", fallback)} used the fallback mesh (globally thinned boundary layer) and have limited comparability.";
            }

            return null;
        }

        public static void PrintSummary(CalibrationDecision decision, TextWriter? output = null)
        {
            TextWriter writer = output ?? Console.Out;
            writer.WriteLine("\n==================================================");
            writer.WriteLine(" CALIBRATION RESULT");
            writer.WriteLine("==================================================");
            writer.WriteLine(decision.SummaryMessage);

            double totalSeconds = decision.AllStages.Sum(st => st.DurationSeconds);
            writer.WriteLine($"   Total calibration duration: {MeshCalibration.FormatDuration(totalSeconds)} ({decision.AllStages.Count} levels)");
            foreach (var st in decision.AllStages.OrderBy(st => st.LevelIndex))
            {
                var extras = new List<string>();
                int totalBreakdown = st.SurfaceTriangles + st.SurfaceQuads + st.VolumePrisms
                                     + st.VolumeHexahedra + st.VolumeTetrahedra + st.VolumePyramids;
                if (totalBreakdown > 0)
                {
                    extras.Add($"Tri={st.SurfaceTriangles:N0}, Quad={st.SurfaceQuads:N0}, Prism={st.VolumePrisms:N0}, Hex={st.VolumeHexahedra:N0}, Tet={st.VolumeTetrahedra:N0}, Pyr={st.VolumePyramids:N0}");
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
                writer.WriteLine($"     Level {st.LevelIndex} (factor {st.Factor.ToString("0.00", CultureInfo.InvariantCulture)}): {MeshCalibration.FormatDuration(st.DurationSeconds)}, {st.MeshCells:N0} cells{extraSuffix}");
            }

            string? layerWarning = DescribeLayerMismatch(decision.AllStages);
            if (layerWarning != null) writer.WriteLine($"   {OutputConventions.WarningTag} {layerWarning}");

            if (decision.Gci != null)
            {
                string pStr = float.IsFinite(decision.Gci.ObservedOrderP)
                    ? decision.Gci.ObservedOrderP.ToString("F2", CultureInfo.InvariantCulture)
                    : "n/a";
                string gciFineStr = float.IsFinite(decision.Gci.GciFinePercent)
                    ? $"{decision.Gci.GciFinePercent.ToString("F2", CultureInfo.InvariantCulture)} %"
                    : "n/a";
                string gciSelStr = float.IsFinite(decision.Gci.GciSelectedPercent)
                    ? $"{decision.Gci.GciSelectedPercent.ToString("F2", CultureInfo.InvariantCulture)} %"
                    : "n/a";

                writer.WriteLine($"   Observed order p:              {pStr}");
                writer.WriteLine($"   GCI (Finest mesh):             {gciFineStr}");
                writer.WriteLine($"   GCI (Selected/Medium):         {gciSelStr}");
                if (float.IsFinite(decision.Gci.ExtrapolatedValue))
                {
                    writer.WriteLine($"   Extrapolated grid-independent value (primary metric): {decision.Gci.ExtrapolatedValue.ToString("0.#####", CultureInfo.InvariantCulture)} "
                        + $"(finest mesh deviates by {decision.Gci.ExtrapolatedDeviationPercent:0.0} %)");
                }
                if (decision.Gci.Warning != null)
                {
                    writer.WriteLine($"   {OutputConventions.WarningTag} {decision.Gci.Warning}");
                }
            }

            if (decision.Success && decision.SelectedStage != null)
            {
                writer.WriteLine($"\n-> Recommended mesh factor: {decision.SelectedStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} (Level {decision.SelectedStage.LevelIndex})");
            }
            else if (!decision.Success && decision.FallbackStage != null)
            {
                writer.WriteLine($"   -> Recommended fallback factor (finest valid candidate level): {decision.FallbackStage.Factor.ToString("0.00", CultureInfo.InvariantCulture)} (Level {decision.FallbackStage.LevelIndex})");
            }
            writer.WriteLine("==================================================\n");
        }

        /// <summary>Logs the duration of a calibration level (boundary-layer cell count plan, WZ10).</summary>
        public static void PrintStageDuration(int level, float factor, double seconds)
        {
            Console.WriteLine($"       ⏱ Duration Level {level} (factor {factor.ToString("0.00", CultureInfo.InvariantCulture)}): {MeshCalibration.FormatDuration(seconds)}");
        }
    }
}
