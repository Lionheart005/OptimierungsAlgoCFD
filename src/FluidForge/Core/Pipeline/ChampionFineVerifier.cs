using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FluidForge.Core
{
    /// <summary>
    /// Fine-mesh re-evaluation of champions (TODO-43).
    /// Verifies mesh independence of optimization results.
    /// </summary>
    public static class ChampionFineVerifier
    {
        public const string VerificationCsvFileName = OutputConventions.VerificationCsvFileName;

        /// <summary>
        /// Computes the fine mesh factor: effectiveFactor / (√2 ^ RefinementSteps).
        /// </summary>
        public static float CalculateFineFactor(float effectiveFactor, int refinementSteps = 1)
        {
            int steps = Math.Max(1, refinementSteps);
            return effectiveFactor / MathF.Pow(MathF.Sqrt(2.0f), steps);
        }

        /// <summary>
        /// Computes the maximum relative deviation across all monitored metrics between the normal and fine runs.
        /// </summary>
        public static float ComputeMaxRelativeDeviation(
            ModelRecord normalRecord,
            ModelRecord fineRecord,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            var metricsToCheck = new List<string>();

            if (monitoredMetrics != null && monitoredMetrics.Count > 0)
            {
                metricsToCheck.AddRange(monitoredMetrics);
            }
            else
            {
                // Fallback: all numeric passive parameters that are not framework metadata
                var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                {
                    "ScaleFactor", "MeshCells", "MeshRefinementFactor", "FineVerified", "FineDeviation",
                    "BoundaryLayerHexShare", "ReynoldsNumber"
                };

                // Su2_* (iterations, convergence, oscillation, iterative error) and YPlus_* are
                // solver diagnostics, not geometry performance metrics.
                metricsToCheck.AddRange(normalRecord.PassiveParameters.Keys.Where(k =>
                    !excluded.Contains(k)
                    && !ModelRecord.IsMeshMetadataKey(k)
                    && !k.StartsWith("Su2_", StringComparison.OrdinalIgnoreCase)
                    && !k.StartsWith("YPlus_", StringComparison.OrdinalIgnoreCase)));
            }

            if (metricsToCheck.Count == 0)
            {
                float refFit = Math.Max(Math.Abs(normalRecord.Fitness), 1e-7f);
                return Math.Abs(fineRecord.Fitness - normalRecord.Fitness) / refFit;
            }

            float maxDev = 0f;
            foreach (var metric in metricsToCheck)
            {
                if (normalRecord.PassiveParameters.TryGetValue(metric, out float qNormal))
                {
                    float qFine = fineRecord.PassiveParameters.TryGetValue(metric, out float qf) ? qf : 0f;
                    float dev = Math.Abs(qFine - qNormal) / Math.Max(Math.Abs(qNormal), 1e-7f);
                    if (dev > maxDev) maxDev = dev;
                }
            }

            return maxDev;
        }

        /// <summary>
        /// Maximum <b>normalized</b> deviation (TODO-W1): <c>|fine - normal| / (tolerance * |normal| + absoluteFloor)</c>
        /// across all metrics; at most 1 = within tolerance. Near 0, the absolute floor from
        /// <see cref="MetricTolerance"/> dominates; for larger values, the relative deviation dominates.
        /// Without metrics: evaluates Fitness.
        /// </summary>
        public static float ComputeMaxNormalizedDeviation(
            ModelRecord normalRecord,
            ModelRecord fineRecord,
            IReadOnlyCollection<string>? monitoredMetrics,
            float tolerance,
            IReadOnlyDictionary<string, float>? absoluteTolerances)
        {
            var metrics = monitoredMetrics != null && monitoredMetrics.Count > 0
                ? monitoredMetrics
                : (IReadOnlyCollection<string>)Array.Empty<string>();

            if (metrics.Count == 0)
            {
                MetricUncertainty.TryGetFitness(normalRecord, Array.Empty<string>(), out double uNormal);
                MetricUncertainty.TryGetFitness(fineRecord, Array.Empty<string>(), out double uFine);
                return MetricTolerance.Normalized(fineRecord.Fitness, normalRecord.Fitness, MetricTolerance.WithUncertainty(tolerance, uNormal, uFine), 0f);
            }

            float max = 0f;
            foreach (string metric in metrics)
            {
                if (!normalRecord.PassiveParameters.TryGetValue(metric, out float qNormal)) continue;
                float qFine = fineRecord.PassiveParameters.TryGetValue(metric, out float qf) ? qf : 0f;

                // Oscillating runs (vortex shedding): allowable deviation grows with the uncertainty of both values.
                MetricUncertainty.TryGet(normalRecord, metric, out double uA);
                MetricUncertainty.TryGet(fineRecord, metric, out double uB);
                float n = MetricTolerance.Normalized(
                    qFine, qNormal, MetricTolerance.WithUncertainty(tolerance, uA, uB), MetricTolerance.AbsoluteFor(metric, absoluteTolerances));
                if (n > max) max = n;
            }
            return max;
        }

        /// <summary>
        /// Runs per-iteration fine verification (Mode = "EachIteration").
        /// Does not alter selection or history (Rule F7).
        /// </summary>
        public static void VerifyIterationWinner(
            ModelRecord winner,
            int iteration,
            SimulationContext context,
            Func<Dictionary<string, float>, float, string, ModelRecord> resimulate,
            Action<ModelRecord> evaluateFitness,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            string mode = (context.Config.ChampionVerification.Mode ?? "Off").Trim();
            if (!mode.Equals("EachIteration", StringComparison.OrdinalIgnoreCase)) return;

            float fineFactor = CalculateFineFactor(
                context.EffectiveMeshRefinementFactor,
                context.Config.ChampionVerification.RefinementSteps);

            string tag = $"Fine_Gen{iteration}";

            SimulationLog.Detail($"{OutputConventions.VerificationTag} Fine-verifying iteration winner Gen {iteration} (factor: {fineFactor:0.00}, tag: {tag})...");

            ModelRecord fineRecord = resimulate(winner.ActiveParameters, fineFactor, tag);
            evaluateFitness(fineRecord);

            float maxRelDev = ComputeMaxRelativeDeviation(winner, fineRecord, monitoredMetrics);
            float maxNorm = ComputeMaxNormalizedDeviation(winner, fineRecord, monitoredMetrics,
                context.Config.ChampionVerification.Tolerance, context.Config.MeshCalibration.AbsoluteTolerances);

            // Document on record
            winner.PassiveParameters["FineVerified"] = 1f;
            winner.PassiveParameters["FineDeviation"] = maxRelDev;

            SimulationLog.Detail($"Normal Fitness: {winner.Fitness:F4} | Fine Fitness: {fineRecord.Fitness:F4} | Max. Metric Deviation: {maxRelDev * 100:0.0} %");

            if (maxNorm > 1.0f)
            {
                SimulationLog.Warning(
                    $"Result is mesh-dependent - fine-mesh deviation is {maxRelDev * 100:0.0} % "
                    + $"(allowed: {context.Config.ChampionVerification.Tolerance * 100:0.0} % plus floor; normalized {maxNorm:0.0}, allowed <= 1) - check calibration!");
            }
        }

        /// <summary>
        /// Runs final verification at the end of optimization (Mode = "Final" or "FinalTopN").
        /// </summary>
        public static void VerifyFinal(
            SimulationContext context,
            Func<Dictionary<string, float>, float, string, ModelRecord> resimulate,
            Action<ModelRecord> evaluateFitness,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            string mode = (context.Config.ChampionVerification.Mode ?? "Off").Trim();

            if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || mode.Equals("EachIteration", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            float fineFactor = CalculateFineFactor(
                context.EffectiveMeshRefinementFactor,
                context.Config.ChampionVerification.RefinementSteps);

            var validRecords = context.History.Where(r => !r.SimulationFailed).ToList();
            if (validRecords.Count == 0) return;

            var nonCoarseRecords = validRecords.Where(r => !FidelityLevels.IsCoarse(r.FidelityLevel)).ToList();
            var selectionPool = nonCoarseRecords.Count > 0 ? nonCoarseRecords : validRecords;

            var itemsToVerify = new List<ModelRecord>();

            if (mode.Equals("FinalTopN", StringComparison.OrdinalIgnoreCase))
            {
                int topN = Math.Max(1, context.Config.ChampionVerification.TopN);
                itemsToVerify = selectionPool.OrderByDescending(r => r.Fitness).Take(topN).ToList();
            }
            else // "Final"
            {
                ModelRecord? champ = WorkflowController.SelectBestRecord(selectionPool);
                if (champ != null) itemsToVerify.Add(champ);
            }

            if (itemsToVerify.Count == 0) return;

            SimulationLog.PrintBanner(
                $"FINAL CHAMPION VERIFICATION (Mode: {mode})",
                $"Fine mesh factor: {fineFactor:0.00} (refinement steps: {context.Config.ChampionVerification.RefinementSteps})",
                $"Candidates to verify: {itemsToVerify.Count}");

            var results = new List<VerificationRow>();

            for (int i = 0; i < itemsToVerify.Count; i++)
            {
                var candidate = itemsToVerify[i];
                // Tag identifies the candidate (Fine1_Gen5_Var1); otherwise all files would be named Gen99_Var99_Fine1.
                string tag = $"Fine{i + 1}_Gen{candidate.Iteration}_Var{candidate.Variant}";

                SimulationLog.Detail($"{OutputConventions.VerificationTag} Verifying candidate {i + 1}/{itemsToVerify.Count}: Iteration {candidate.Iteration}, Variant {candidate.Variant}...");

                ModelRecord fineRecord = resimulate(candidate.ActiveParameters, fineFactor, tag);
                evaluateFitness(fineRecord);

                float maxDev = ComputeMaxRelativeDeviation(candidate, fineRecord, monitoredMetrics);
                float maxNorm = ComputeMaxNormalizedDeviation(candidate, fineRecord, monitoredMetrics,
                    context.Config.ChampionVerification.Tolerance, context.Config.MeshCalibration.AbsoluteTolerances);

                // A failed fine evaluation (meshing or SU2) verifies nothing.
                // Previously FineVerified = 1 and "ranking holds" were reported (server test 06.11.2026).
                candidate.PassiveParameters["FineVerified"] = fineRecord.SimulationFailed ? 0f : 1f;
                candidate.PassiveParameters["FineDeviation"] = maxDev;

                results.Add(new VerificationRow
                {
                    Candidate = candidate,
                    FineRecord = fineRecord,
                    NormalRank = i + 1,
                    NormalFitness = candidate.Fitness,
                    FineFitness = fineRecord.Fitness,
                    MaxDeviation = maxDev,
                    MaxNormalized = maxNorm
                });
            }

            // Determine ranking on fine mesh
            var fineRanked = results.OrderByDescending(r => r.FineFitness).ToList();
            for (int r = 0; r < fineRanked.Count; r++)
            {
                fineRanked[r].FineRank = r + 1;
            }

            int failedFine = results.Count(r => r.FineRecord.SimulationFailed);
            bool rankingHolds = failedFine == 0 && results.All(r => r.NormalRank == r.FineRank);

            // Write verification.csv
            WriteVerificationCsv(Path.Combine(context.WorkingDirectory, VerificationCsvFileName), results, rankingHolds, monitoredMetrics);

            // Update results.csv with FineVerified/FineDeviation
            context.ExportToCsv();

            // Console output
            SimulationLog.PrintBanner("VERIFICATION RESULT");
            if (failedFine > 0)
            {
                SimulationLog.Warning($"{failedFine} of {results.Count} fine evaluation(s) failed - nothing verified for them (FineVerified = 0).");
            }

            string rankingText = rankingHolds ? "YES (ranking holds)" : failedFine > 0 ? "NOT CHECKABLE (fine evaluation failed)" : "NO (ranking changed)";
            if (rankingHolds)
            {
                SimulationLog.Success($"Ranking stable on fine mesh: {rankingText}");
            }
            else
            {
                SimulationLog.Warning($"Ranking stable on fine mesh: {rankingText}");
            }

            Console.WriteLine("Iter | Var | Normal-Rank | Fine-Rank | Normal-Score | Fine-Score | Max. Dev. | normalized (<= 1 ok)");
            Console.WriteLine("--------------------------------------------------------------------------------------------------");

            bool anyWarning = false;
            foreach (var row in results)
            {
                Console.WriteLine(
                    $"{row.Candidate.Iteration,4} | {row.Candidate.Variant,3} | {row.NormalRank,11} | {row.FineRank,9} | "
                    + $"{row.NormalFitness,12:F4} | {row.FineFitness,10:F4} | {row.MaxDeviation * 100,8:0.0} % | {row.MaxNormalized,8:0.00}");

                if (row.MaxNormalized > 1.0f)
                {
                    anyWarning = true;
                }
            }

            if (anyWarning)
            {
                Console.WriteLine();
                SimulationLog.Warning(
                    $"Result is mesh-dependent - fine-mesh deviation exceeds tolerance "
                    + $"of {context.Config.ChampionVerification.Tolerance * 100:0.0} % - check calibration!");
            }
            Console.WriteLine("==================================================\n");
        }

        private static void WriteVerificationCsv(
            string filePath,
            List<VerificationRow> rows,
            bool rankingHolds,
            IReadOnlyCollection<string>? monitoredMetrics)
        {
            var sb = new StringBuilder();

            var metrics = monitoredMetrics != null && monitoredMetrics.Count > 0
                ? monitoredMetrics.ToList()
                : new List<string> { "Drag" };

            // Header
            sb.Append("Iteration;Variant;NormalRank;FineRank;NormalFitness;FineFitness;MaxMetricDeviation;RankingHolds;MaxNormalizedDeviation");
            foreach (var m in metrics)
            {
                sb.Append($";{m}_Normal;{m}_Fine");
            }
            sb.AppendLine();

            foreach (var r in rows)
            {
                sb.Append($"{r.Candidate.Iteration};{r.Candidate.Variant};{r.NormalRank};{r.FineRank};");
                sb.Append($"{r.NormalFitness.ToString("F5", CultureInfo.InvariantCulture)};");
                sb.Append($"{r.FineFitness.ToString("F5", CultureInfo.InvariantCulture)};");
                sb.Append($"{r.MaxDeviation.ToString("P2", CultureInfo.InvariantCulture)};");
                sb.Append(rankingHolds ? "1" : "0");
                sb.Append($";{r.MaxNormalized.ToString("F3", CultureInfo.InvariantCulture)}");

                foreach (var m in metrics)
                {
                    float normVal = r.Candidate.PassiveParameters.TryGetValue(m, out float nv) ? nv : 0f;
                    float fineVal = r.FineRecord.PassiveParameters.TryGetValue(m, out float fv) ? fv : 0f;
                    sb.Append($";{normVal.ToString("F5", CultureInfo.InvariantCulture)};{fineVal.ToString("F5", CultureInfo.InvariantCulture)}");
                }
                sb.AppendLine();
            }

            File.WriteAllText(filePath, sb.ToString(), Encoding.UTF8);
        }

        private sealed class VerificationRow
        {
            public ModelRecord Candidate { get; set; } = null!;
            public ModelRecord FineRecord { get; set; } = null!;
            public int NormalRank { get; set; }
            public int FineRank { get; set; }
            public float NormalFitness { get; set; }
            public float FineFitness { get; set; }
            public float MaxDeviation { get; set; }
            public float MaxNormalized { get; set; }
        }
    }
}
