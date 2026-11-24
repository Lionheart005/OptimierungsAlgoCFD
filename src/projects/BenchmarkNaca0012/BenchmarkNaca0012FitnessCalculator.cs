using System;
using System.Collections.Generic;
using FluidForge.Core;
using FluidForge.Projects.Naca2D;

namespace FluidForge.Projects.BenchmarkNaca0012
{
    /// <summary>
    /// Fitness and NASA TMR verification evaluator for <c>BenchmarkNaca0012</c>.
    /// Minimizes drag and records relative/absolute errors against NASA TMR data at alpha = 0 and 10 deg.
    /// </summary>
    public sealed class BenchmarkNaca0012FitnessCalculator : ObjectiveEvaluatorBase
    {
        private static readonly string[] RequiredMetricNames = { "Drag", "Lift" };

        private static readonly string[] ComparisonMetricNames =
        {
            "Drag",
            "Lift",
            "Drag_OP1",
            "Lift_OP1",
            "Drag_OP2",
            "Lift_OP2",
            "Tmr_CdRelErr_Alpha0",
            "Tmr_ClAbsErr_Alpha0",
            "Tmr_CdRelErr_Alpha10",
            "Tmr_ClRelErr_Alpha10"
        };

        /// <summary>Initializes the evaluator.</summary>
        public BenchmarkNaca0012FitnessCalculator(ProjectConfig? project = null)
        {
            _ = project;
        }

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RawComparisonMetrics => ComparisonMetricNames;

        /// <inheritdoc />
        public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (!record.PassiveParameters.TryGetValue("Drag", out float drag) ||
                !record.PassiveParameters.TryGetValue("Lift", out float lift) ||
                !float.IsFinite(drag) ||
                !float.IsFinite(lift))
            {
                return new EvaluationOutcome { ScalarFitnessOverride = DefaultFailedFitness };
            }

            if (record.PassiveParameters.TryGetValue("Drag_OP1", out float dragOp1) &&
                record.PassiveParameters.TryGetValue("Lift_OP1", out float liftOp1) &&
                float.IsFinite(dragOp1) &&
                float.IsFinite(liftOp1))
            {
                TmrValidationResult res0 = Naca2DTmrBenchmark.EvaluateAgainstTmr(0f, liftOp1, dragOp1);
                record.PassiveParameters["Tmr_CdRelErr_Alpha0"] = res0.CdRelError;
                record.PassiveParameters["Tmr_ClAbsErr_Alpha0"] = res0.ClRelError;
            }

            if (record.PassiveParameters.TryGetValue("Drag_OP2", out float dragOp2) &&
                record.PassiveParameters.TryGetValue("Lift_OP2", out float liftOp2) &&
                float.IsFinite(dragOp2) &&
                float.IsFinite(liftOp2))
            {
                TmrValidationResult res10 = Naca2DTmrBenchmark.EvaluateAgainstTmr(10f, liftOp2, dragOp2);
                record.PassiveParameters["Tmr_CdRelErr_Alpha10"] = res10.CdRelError;
                record.PassiveParameters["Tmr_ClRelErr_Alpha10"] = res10.ClRelError;
            }

            return new EvaluationOutcome
            {
                Objectives =
                {
                    new ObjectiveValue("Drag", drag, OptimizationDirection.Minimize)
                },
                ScalarFitnessOverride = 1.0f / MathF.Max(MathF.Abs(drag), 1e-4f)
            };
        }
    }
}
