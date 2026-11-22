using System;
using System.Collections.Generic;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Projects.Naca2D
{
    /// <summary>
    /// Aerodynamic fitness evaluator for the 2D NACA airfoil.
    /// Maximizes the single-point or multi-point weighted lift-to-drag ratio <c>E = CL / CD</c>
    /// while enforcing <c>Lift &gt;= MinLift</c> as a post-CFD constraint.
    /// </summary>
    public sealed class Naca2DFitnessCalculator : ObjectiveEvaluatorBase
    {
        /// <summary>Default minimum lift coefficient enforced as a post-CFD constraint.</summary>
        public const float DefaultMinLift = 0.1f;

        /// <summary>
        /// Metrics from <c>PassiveParameters</c> required for fitness evaluation.
        /// Both Drag (CD) and Lift (CL) come from SU2's history.csv via solvers/su2.json.
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag", "Lift" };

        private static readonly string[] ComparisonMetricNames = { "Drag", "Lift", "GlideRatio" };

        private readonly float _minLift;
        private readonly float[]? _normalizedWeights;

        public Naca2DFitnessCalculator(ProjectConfig project, IReadOnlyList<float>? operatingPointWeights = null)
        {
            if (project == null) throw new ArgumentNullException(nameof(project));

            _minLift = project.OptimizationTargets.TryGetValue("MinLift", out float configuredMinLift)
                ? configuredMinLift
                : DefaultMinLift;

            if (operatingPointWeights != null && operatingPointWeights.Count > 0)
            {
                double sum = operatingPointWeights.Where(w => float.IsFinite(w) && w > 0f).Sum(w => (double)w);
                if (sum > 0.0 && operatingPointWeights.All(w => float.IsFinite(w) && w > 0f))
                {
                    _normalizedWeights = operatingPointWeights.Select(w => (float)(w / sum)).ToArray();
                }
            }
        }

        /// <inheritdoc/>
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc/>
        public override IReadOnlyCollection<string> RawComparisonMetrics => ComparisonMetricNames;

        /// <inheritdoc/>
        public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
        {
            bool hasDrag = record.PassiveParameters.TryGetValue("Drag", out float drag);
            bool hasLift = record.PassiveParameters.TryGetValue("Lift", out float lift);

            if (!hasDrag || !hasLift ||
                !float.IsFinite(drag) || !float.IsFinite(lift))
            {
                return new EvaluationOutcome { ScalarFitnessOverride = DefaultFailedFitness };
            }

            float glideRatio = ComputeGlideRatio(record, drag, lift);
            record.PassiveParameters["GlideRatio"] = glideRatio;

            return new EvaluationOutcome
            {
                Objectives =
                {
                    new ObjectiveValue("GlideRatio", glideRatio, OptimizationDirection.Maximize)
                },
                Constraints =
                {
                    ConstraintEvaluation.AtLeast("MinLift", lift, _minLift)
                },
                ScalarFitnessOverride = glideRatio < DefaultFailedFitness ? DefaultFailedFitness : null
            };
        }

        private float ComputeGlideRatio(ModelRecord record, float aggregatedDrag, float aggregatedLift)
        {
            var opGlides = new List<float>();
            for (int idx = 1; ; idx++)
            {
                if (!record.PassiveParameters.TryGetValue($"Drag_OP{idx}", out float opDrag) ||
                    !record.PassiveParameters.TryGetValue($"Lift_OP{idx}", out float opLift) ||
                    !float.IsFinite(opDrag) || !float.IsFinite(opLift))
                {
                    break;
                }

                float opGlide = opLift / Math.Max(Math.Abs(opDrag), 0.001f);
                record.PassiveParameters[$"GlideRatio_OP{idx}"] = opGlide;
                opGlides.Add(opGlide);
            }

            if (opGlides.Count == 0)
            {
                return aggregatedLift / Math.Max(Math.Abs(aggregatedDrag), 0.001f);
            }

            double weightedSum = 0.0;
            bool useConfiguredWeights = _normalizedWeights != null && _normalizedWeights.Length == opGlides.Count;
            double uniformWeight = 1.0 / opGlides.Count;

            for (int j = 0; j < opGlides.Count; j++)
            {
                double w = useConfiguredWeights ? _normalizedWeights![j] : uniformWeight;
                weightedSum += w * opGlides[j];
            }

            return (float)weightedSum;
        }
    }
}
