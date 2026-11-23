using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluidForge.Core
{
    public sealed record StageCandidateComparison(
        int Variant,
        float CoarseFitness,
        float HighFitness,
        float RelativeDelta,
        bool HighFailed);

    public sealed record StageComparisonReport(
        IReadOnlyList<StageCandidateComparison> Comparisons,
        float MaxRelativeDelta,
        bool RankReversed,
        bool HighDiscrepancy,
        string SummaryLine,
        string? WarningLine);
    /// <summary>
    /// Selection and configuration logic for the two-stage multi-fidelity filter
    /// (<see cref="MultiFidelityConfig"/>): coarse Stage-1 screening followed by promotion
    /// of the strongest candidates to full-fidelity Stage-2 evaluation.
    /// </summary>
    public static class MultiFidelitySelector
    {
        /// <summary>
        /// Checks whether <c>TwoStageFilter</c> is enabled and validates all <see cref="MultiFidelityConfig"/> settings upfront.
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown with <see cref="OutputConventions.ConfigTag"/> on invalid mode or bounds.</exception>
        public static bool IsTwoStageEnabled(MultiFidelityConfig? config)
        {
            if (config == null || string.IsNullOrWhiteSpace(config.Mode))
                return false;

            string mode = config.Mode.Trim();
            if (mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
                return false;

            if (!mode.Equals("TwoStageFilter", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} MultiFidelity.Mode = \"{config.Mode}\" is unknown. Allowed values: \"Off\" or \"TwoStageFilter\".");
            }

            if (!float.IsFinite(config.CoarseMeshFactor) || config.CoarseMeshFactor <= 1.0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} MultiFidelity.CoarseMeshFactor must be finite and > 1.0 (got {config.CoarseMeshFactor}).");
            }

            if (config.CoarseMaxIterations < 0)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} MultiFidelity.CoarseMaxIterations must be >= 0 (got {config.CoarseMaxIterations}).");
            }

            if (config.PromoteTopK < 0)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} MultiFidelity.PromoteTopK must be >= 0 (got {config.PromoteTopK}).");
            }

            if (!float.IsFinite(config.PromoteTopFraction) || config.PromoteTopFraction < 0f || config.PromoteTopFraction > 1.0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} MultiFidelity.PromoteTopFraction must be in [0, 1] (got {config.PromoteTopFraction}).");
            }

            return true;
        }

        /// <summary>
        /// Computes the Stage-1 coarse mesh refinement factor from the baseline effective factor.
        /// Larger factors produce coarser meshes in FluidForge.
        /// </summary>
        public static float ComputeCoarseFactor(float effectiveFactor, MultiFidelityConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            return effectiveFactor * config.CoarseMeshFactor;
        }

        /// <summary>
        /// Computes how many eligible Stage-1 candidates to promote to full fidelity.
        /// </summary>
        public static int ComputePromotionCount(int eligibleCount, MultiFidelityConfig config)
        {
            ArgumentNullException.ThrowIfNull(config);
            if (eligibleCount <= 0) return 0;

            int byFraction = config.PromoteTopFraction > 0f
                ? (int)MathF.Ceiling(eligibleCount * config.PromoteTopFraction)
                : 0;
            int requested = Math.Max(Math.Max(0, config.PromoteTopK), byFraction);
            if (requested <= 0) requested = 1;

            return Math.Clamp(requested, 1, eligibleCount);
        }

        /// <summary>
        /// Selects the top Stage-1 coarse records eligible for promotion to full fidelity.
        /// Excludes failed, disqualified, and pre-constraint-rejected variants.
        /// </summary>
        public static IReadOnlyList<ModelRecord> SelectForPromotion(
            IReadOnlyList<ModelRecord> coarseRecords,
            MultiFidelityConfig config)
        {
            ArgumentNullException.ThrowIfNull(coarseRecords);
            ArgumentNullException.ThrowIfNull(config);

            var eligible = coarseRecords
                .Where(r => !r.SimulationFailed && !r.Disqualified && !r.IsPreConstraintInfeasible)
                .ToList();

            if (eligible.Count == 0)
                return Array.Empty<ModelRecord>();

            int count = ComputePromotionCount(eligible.Count, config);
            bool isMultiObjective = eligible.Any(r => r.Objectives.Count >= 2);

            if (isMultiObjective)
            {
                bool[] pareto = ParetoFrontCalculator.ComputeParetoMask(eligible);
                return eligible
                    .Select((record, index) => (Record: record, IsPareto: pareto[index]))
                    .OrderByDescending(x => x.Record.Feasible)
                    .ThenByDescending(x => x.IsPareto)
                    .ThenByDescending(x => float.IsFinite(x.Record.Fitness))
                    .ThenByDescending(x => float.IsFinite(x.Record.Fitness) ? x.Record.Fitness : float.NegativeInfinity)
                    .ThenBy(x => x.Record.Variant)
                    .Take(count)
                    .Select(x => x.Record)
                    .ToList();
            }

            return eligible
                .OrderByDescending(r => r.Feasible)
                .ThenByDescending(r => float.IsFinite(r.Fitness))
                .ThenByDescending(r => float.IsFinite(r.Fitness) ? r.Fitness : float.NegativeInfinity)
                .ThenBy(r => r.Variant)
                .Take(count)
                .ToList();
        }

        /// <summary>
        /// Returns startup warnings for <c>TwoStageFilter</c> combinations known to cause
        /// false failures or incomplete surrogate training (e.g., tight SU2 <c>CoarseMaxIterations</c>
        /// or built-in C# <c>Bayes</c>).
        /// </summary>
        public static IReadOnlyList<string> GetStartupWarnings(
            SimulationConfig? config,
            IReadOnlyList<string>? solverNames,
            string? optimizerName)
        {
            if (config?.MultiFidelity == null || !IsTwoStageEnabled(config.MultiFidelity))
                return Array.Empty<string>();

            MultiFidelityConfig mf = config.MultiFidelity;
            var warnings = new List<string>();

            bool usesSu2 = solverNames != null && solverNames.Any(
                s => !string.IsNullOrWhiteSpace(s) && s.Contains("SU2", StringComparison.OrdinalIgnoreCase));

            if (usesSu2 && mf.CoarseMaxIterations > 0 &&
                (mf.CoarseMaxIterations <= 2 * config.CauchyElements || mf.CoarseMaxIterations < 1000))
            {
                warnings.Add(
                    $"Multi-fidelity CoarseMaxIterations = {mf.CoarseMaxIterations} is tight for SU2 "
                    + $"(CauchyElements = {config.CauchyElements}): with TreatUnconvergedAsFailed = true, "
                    + "unconverged Stage-1 runs may be marked failed instead of ranked. "
                    + "Set MultiFidelity.CoarseMaxIterations = 0 (uncapped) or >= 1500.");
            }

            bool isBuiltInBayes = !string.IsNullOrWhiteSpace(optimizerName)
                ? optimizerName.Contains("Bayes", StringComparison.OrdinalIgnoreCase)
                  && !optimizerName.Contains("BoTorch", StringComparison.OrdinalIgnoreCase)
                  && !optimizerName.Contains("External", StringComparison.OrdinalIgnoreCase)
                : string.Equals(config.OptimizationAlgorithm?.Trim(), "Bayes", StringComparison.OrdinalIgnoreCase);

            if (isBuiltInBayes)
            {
                warnings.Add(
                    "Built-in C# Bayes trains its objective GP only on Stage-2 high-fidelity records; "
                    + "Stage-1 coarse objective scores are not modeled (Stage-1 PreConstraint and failed locations are still tracked). "
                    + "For true multi-fidelity surrogate modeling, set OptimizationAlgorithm = \"BoTorch\".");
            }

            return warnings;
        }

        /// <summary>
        /// Compares promoted Stage-1 coarse candidates against their Stage-2 full-fidelity evaluations,
        /// reporting per-candidate relative deltas, rank reversals, and discrepancy warnings.
        /// </summary>
        public static StageComparisonReport CompareStage1AndStage2(
            IReadOnlyList<ModelRecord> coarsePromoted,
            IReadOnlyList<ModelRecord> highPromoted,
            float discrepancyWarningThreshold = 0.25f)
        {
            ArgumentNullException.ThrowIfNull(coarsePromoted);
            ArgumentNullException.ThrowIfNull(highPromoted);

            var usedHighIndices = new HashSet<int>();
            var pairs = new List<(ModelRecord Coarse, ModelRecord? High, StageCandidateComparison Comparison)>(coarsePromoted.Count);

            for (int i = 0; i < coarsePromoted.Count; i++)
            {
                ModelRecord coarse = coarsePromoted[i];
                int matchedIdx = -1;

                for (int j = 0; j < highPromoted.Count; j++)
                {
                    if (!usedHighIndices.Contains(j) && highPromoted[j].Variant == coarse.Variant)
                    {
                        matchedIdx = j;
                        break;
                    }
                }

                if (matchedIdx < 0 && i < highPromoted.Count && !usedHighIndices.Contains(i))
                {
                    matchedIdx = i;
                }

                ModelRecord? high = matchedIdx >= 0 ? highPromoted[matchedIdx] : null;
                if (matchedIdx >= 0) usedHighIndices.Add(matchedIdx);

                bool highFailed = high == null
                    || high.SimulationFailed
                    || !float.IsFinite(high.Fitness)
                    || !float.IsFinite(coarse.Fitness);

                float highFitness = high != null ? high.Fitness : float.NaN;
                float relativeDelta = highFailed
                    ? float.NaN
                    : MathF.Abs(high!.Fitness - coarse.Fitness) / MathF.Max(MathF.Abs(high.Fitness), 1e-6f);

                var comparison = new StageCandidateComparison(
                    coarse.Variant,
                    coarse.Fitness,
                    highFitness,
                    relativeDelta,
                    highFailed);
                pairs.Add((coarse, high, comparison));
            }

            float maxRelativeDelta = 0f;
            foreach (var p in pairs)
            {
                if (!p.Comparison.HighFailed && float.IsFinite(p.Comparison.RelativeDelta) && p.Comparison.RelativeDelta > maxRelativeDelta)
                {
                    maxRelativeDelta = p.Comparison.RelativeDelta;
                }
            }

            bool highDiscrepancy = maxRelativeDelta > discrepancyWarningThreshold;
            bool anyHighFailed = pairs.Any(p => p.Comparison.HighFailed);

            bool rankReversed = false;
            for (int i = 0; i < pairs.Count && !rankReversed; i++)
            {
                var pi = pairs[i];
                if (pi.Comparison.HighFailed || pi.High == null) continue;

                for (int j = i + 1; j < pairs.Count; j++)
                {
                    var pj = pairs[j];
                    if (pj.Comparison.HighFailed || pj.High == null) continue;

                    bool fitnessReversed = (pi.Coarse.Fitness > pj.Coarse.Fitness + 1e-6f
                                            && pi.High.Fitness < pj.High.Fitness - 1e-6f)
                                           || (pi.Coarse.Feasible == pj.Coarse.Feasible
                                               && pj.Coarse.Fitness > pi.Coarse.Fitness + 1e-6f
                                               && pj.High.Fitness < pi.High.Fitness - 1e-6f);

                    bool feasibilityReversed = pi.Coarse.Feasible && !pi.High.Feasible && pj.High.Feasible;

                    if (fitnessReversed || feasibilityReversed)
                    {
                        rankReversed = true;
                        break;
                    }
                }
            }

            var itemSummaries = new List<string>(pairs.Count);
            foreach (var p in pairs)
            {
                StageCandidateComparison c = p.Comparison;
                string coarseStr = float.IsFinite(c.CoarseFitness)
                    ? c.CoarseFitness.ToString("0.0000", CultureInfo.InvariantCulture)
                    : "NaN";

                if (c.HighFailed)
                {
                    itemSummaries.Add($"Var{c.Variant} (coarse={coarseStr} -> high=FAILED)");
                }
                else
                {
                    string highStr = c.HighFitness.ToString("0.0000", CultureInfo.InvariantCulture);
                    string deltaStr = (c.RelativeDelta * 100f).ToString("0.0", CultureInfo.InvariantCulture);
                    itemSummaries.Add($"Var{c.Variant} (coarse={coarseStr} -> high={highStr}, delta={deltaStr}%)");
                }
            }

            string maxDeltaStr = (maxRelativeDelta * 100f).ToString("0.0", CultureInfo.InvariantCulture);
            string summaryLine = itemSummaries.Count > 0
                ? $"[MULTI-FIDELITY] Stage-1 vs Stage-2 delta: {string.Join(", ", itemSummaries)} | max delta = {maxDeltaStr}%"
                : "[MULTI-FIDELITY] Stage-1 vs Stage-2 delta: no promoted candidates.";

            string? warningLine = null;
            if (rankReversed || highDiscrepancy || anyHighFailed)
            {
                var reasons = new List<string>();
                if (rankReversed) reasons.Add("rank reversal");
                if (highDiscrepancy)
                {
                    string threshStr = (discrepancyWarningThreshold * 100f).ToString("0.#", CultureInfo.InvariantCulture);
                    reasons.Add($"max delta > {threshStr}% ({maxDeltaStr}%)");
                }
                if (anyHighFailed) reasons.Add("Stage-2 failure");

                warningLine =
                    $"[MULTI-FIDELITY] Stage-1 vs Stage-2 discrepancy detected ({string.Join(", ", reasons)}). "
                    + "Consider lowering MultiFidelity.CoarseMeshFactor or setting MultiFidelity.Mode = \"Off\".";
            }

            return new StageComparisonReport(
                pairs.Select(p => p.Comparison).ToList(),
                maxRelativeDelta,
                rankReversed,
                highDiscrepancy,
                summaryLine,
                warningLine);
        }
    }
}
