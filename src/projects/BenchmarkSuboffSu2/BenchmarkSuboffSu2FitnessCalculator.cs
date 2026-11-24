using System.Collections.Generic;
using FluidForge.Core;
using FluidForge.Projects.BenchmarkSuboffOpenFoam;

namespace FluidForge.Projects.BenchmarkSuboffSu2
{
    /// <summary>
    /// Fitness evaluator for <c>BenchmarkSuboffSu2</c>: minimizes drag and evaluates
    /// <c>Suboff_CtRelErr</c> against the DARPA SUBOFF AFF-1 reference total drag coefficient.
    /// </summary>
    public sealed class BenchmarkSuboffSu2FitnessCalculator : ObjectiveEvaluatorBase
    {
        private static readonly string[] RequiredMetricNames = { "Drag" };
        private static readonly string[] ComparisonMetricNames = { "Drag", "Lift", "Suboff_CtRelErr" };

        private readonly float _referenceCt;

        /// <summary>
        /// Initializes the evaluator, optionally reading <c>OptimizationTargets.ReferenceCt</c>.
        /// </summary>
        public BenchmarkSuboffSu2FitnessCalculator(ProjectConfig? project = null)
        {
            _referenceCt = DarpaSuboffHullBuilder.ReadReferenceCt(project);
        }

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RawComparisonMetrics => ComparisonMetricNames;

        /// <inheritdoc />
        public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
            => DarpaSuboffHullBuilder.EvaluateSuboffObjectives(record, _referenceCt);
    }
}
