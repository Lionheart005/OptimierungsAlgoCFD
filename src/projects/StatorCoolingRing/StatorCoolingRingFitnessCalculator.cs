using System;
using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Multi-objective fitness evaluator for <c>StatorCoolingRing</c>:
    /// simultaneously minimizes total pressure drop (<c>PressureDrop</c>) and
    /// 12-outlet flow imbalance (<c>FlowImbalance</c>) via <c>BoTorch</c> <c>qNEHVI</c>,
    /// with a guarded scalar fallback for Bouncer and C# Bayes modes.
    /// </summary>
    public sealed class StatorCoolingRingFitnessCalculator : ObjectiveEvaluatorBase
    {
        private static readonly string[] RequiredMetricNames =
        {
            "PressureDrop",
            "FlowImbalance"
        };

        private static readonly string[] ComparisonMetricNames =
        {
            "PressureDrop",
            "PressureDropStatic",
            "FlowImbalance",
            "MassBalanceError",
            "Volume",
            "PrintableSolidVolume",
            "MinBoltClearanceMm"
        };

        public StatorCoolingRingFitnessCalculator(ProjectConfig project)
        {
            ArgumentNullException.ThrowIfNull(project);
        }

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RawComparisonMetrics => ComparisonMetricNames;

        /// <inheritdoc />
        public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (!record.PassiveParameters.TryGetValue("PressureDrop", out float dp) ||
                !record.PassiveParameters.TryGetValue("FlowImbalance", out float imbalance) ||
                !float.IsFinite(dp) ||
                !float.IsFinite(imbalance))
            {
                return new EvaluationOutcome { ScalarFitnessOverride = DefaultFailedFitness };
            }

            float scalarFitness = 1000.0f / (MathF.Max(1.0f, dp) + 100.0f * (1.0f + MathF.Max(0.0f, imbalance)));

            return new EvaluationOutcome
            {
                Objectives =
                {
                    new ObjectiveValue("PressureDrop", dp, OptimizationDirection.Minimize, 0.5f),
                    new ObjectiveValue("FlowImbalance", imbalance, OptimizationDirection.Minimize, 0.5f)
                },
                ScalarFitnessOverride = scalarFitness
            };
        }
    }
}
