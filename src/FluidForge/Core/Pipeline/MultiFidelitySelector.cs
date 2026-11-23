using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
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
    }
}
