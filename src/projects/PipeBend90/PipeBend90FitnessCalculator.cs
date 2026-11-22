using System;
using System.Collections.Generic;
using System.Linq;
using FluidForge.Core;

namespace FluidForge.Projects.PipeBend90
{
    /// <summary>
    /// Fitness evaluator for <c>PipeBend90</c>: minimizes total pressure drop (<c>PressureDrop</c>)
    /// via <see cref="ObjectiveEvaluatorBase"/>.
    /// </summary>
    public sealed class PipeBend90FitnessCalculator : ObjectiveEvaluatorBase
    {
        private static readonly string[] RequiredTargets =
        {
            "MinimumAllowedVolume",
            "MaximumAllowedVolume",
            "MinBendRadiusRatio"
        };

        private static readonly string[] RequiredMetricNames = { "PressureDrop" };

        private static readonly string[] ComparisonMetricNames =
        {
            "PressureDrop",
            "PressureDropStatic",
            "MassBalanceError",
            "Volume"
        };

        public PipeBend90FitnessCalculator(ProjectConfig project)
        {
            ArgumentNullException.ThrowIfNull(project);

            var missing = RequiredTargets.Where(k => !project.OptimizationTargets.ContainsKey(k)).ToList();
            if (missing.Count > 0)
            {
                string present = project.OptimizationTargets.Count > 0
                    ? string.Join(", ", project.OptimizationTargets.Keys)
                    : "(none)";

                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} Project '{project.ProjectName}': missing OptimizationTargets "
                    + $"{string.Join(", ", missing)}. Present: {present}. "
                    + $"Add them in src/projects/{project.ProjectName}/project.json.");
            }
        }

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RawComparisonMetrics => ComparisonMetricNames;

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
