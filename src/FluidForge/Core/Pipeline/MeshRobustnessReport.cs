using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// End-of-run summary: how robust was meshing? Counts variants by boundary-layer fallback stage
    /// (A smoothed directions, B locally thinned, C fallback mesh) and lists shapes that could only be
    /// meshed with the fallback mesh or failed meshing altogether, so edge cases are not lost silently
    /// (boundary-layer cell count plan, section 4). Pure function for unit testability.
    /// </summary>
    public static class MeshRobustnessReport
    {
        /// <summary>Lines for the log; empty if no variant recorded boundary-layer metrics or meshing failures.</summary>
        public static IReadOnlyList<string> Lines(IEnumerable<ModelRecord> history)
        {
            var records = history.ToList();
            int stageA = 0, stageB = 0, stageC = 0, meshFailed = 0;
            var fallback = new List<ModelRecord>();
            var failed = new List<ModelRecord>();

            foreach (ModelRecord r in records)
            {
                if (r.PassiveParameters.TryGetValue(ModelRecord.MeshFailedKey, out float mf) && mf > 0.5f)
                {
                    meshFailed++;
                    failed.Add(r);
                    continue;
                }

                if (!r.PassiveParameters.TryGetValue("BoundaryLayerStage", out float stage)) continue;
                int st = (int)System.Math.Round(stage);
                if (st == 1) stageA++;
                else if (st == 2) stageB++;
                else if (st >= 3) { stageC++; fallback.Add(r); }
            }

            var lines = new List<string>();
            if (stageA + stageB + stageC + meshFailed == 0) return lines;

            lines.Add($"Mesh robustness ({records.Count} variants): {stageA} with standard boundary layer (Stage A), {stageB} locally thinned (Stage B), "
                      + $"{stageC} with fallback mesh (Stage C), {meshFailed} failed to mesh.");

            if (fallback.Count > 0)
            {
                lines.Add("Shapes using fallback mesh (globally thinned boundary layer, limited comparability; inspect manually and recheck):");
                foreach (ModelRecord r in fallback)
                    lines.Add($"  Iteration {r.Iteration} | Variant {r.Variant} | Fitness {r.Fitness.ToString("0.##", CultureInfo.InvariantCulture)} | {r.StlPath}");
            }

            if (failed.Count > 0)
            {
                lines.Add($"Unmeshable shapes (do not count as solver divergence; see error logs under {OutputConventions.ErrorLogsDirectory}/):");
                foreach (ModelRecord r in failed)
                    lines.Add($"  Iteration {r.Iteration} | Variant {r.Variant} | {r.StlPath}");
            }

            return lines;
        }
    }
}
