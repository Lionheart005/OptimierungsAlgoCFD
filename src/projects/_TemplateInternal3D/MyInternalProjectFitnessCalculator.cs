using System;
using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.MyInternalProject
{
    /// <summary>
    /// Internal-flow fitness evaluator template that minimizes total pressure drop (<c>PressureDrop</c>).
    /// </summary>
    public sealed class MyInternalProjectFitnessCalculator : ObjectiveEvaluatorBase
    {
        private static readonly string[] RequiredMetricNames = { "PressureDrop" };

        public MyInternalProjectFitnessCalculator(ProjectConfig project)
        {
            ArgumentNullException.ThrowIfNull(project);
        }

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RawComparisonMetrics => RequiredMetricNames;

        /// <inheritdoc />
        public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
        {
            if (!record.PassiveParameters.TryGetValue("PressureDrop", out float pressureDrop) ||
                !float.IsFinite(pressureDrop))
            {
                return new EvaluationOutcome { ScalarFitnessOverride = DefaultFailedFitness };
            }

            return new EvaluationOutcome
            {
                Objectives =
                {
                    new ObjectiveValue("PressureDrop", pressureDrop, OptimizationDirection.Minimize)
                }
            };
        }
    }
}
