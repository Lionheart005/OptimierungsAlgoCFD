using System;
using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.BenchmarkPipeBend90
{
    /// <summary>
    /// Fitness and Idelchik/Miller loss-coefficient evaluator for <c>BenchmarkPipeBend90</c>.
    /// Minimizes total pressure drop and records <c>TotalLossCoeff_Zeta</c>,
    /// <c>StraightFriction_Zeta</c>, and <c>BendLossCoeff_Zeta</c> in <c>PassiveParameters</c>.
    /// </summary>
    public sealed class BenchmarkPipeBend90FitnessCalculator : ObjectiveEvaluatorBase
    {
        public const float DefaultBendRadiusRatio = 3.0f;

        private static readonly string[] RequiredMetricNames = { "PressureDrop" };

        private static readonly string[] ComparisonMetricNames =
        {
            "PressureDrop",
            "PressureDropStatic",
            "MassBalanceError",
            "TotalLossCoeff_Zeta",
            "StraightFriction_Zeta",
            "BendLossCoeff_Zeta",
            "Volume"
        };

        private readonly float _pipeRadiusMm;
        private readonly float _inletStraightRatio;
        private readonly float _outletStraightRatio;
        private readonly float _density;
        private readonly float _viscosity;
        private readonly float _inletVelocity;

        /// <summary>
        /// Initializes the evaluator from <c>OptimizationTargets</c> in <paramref name="project"/>.
        /// </summary>
        public BenchmarkPipeBend90FitnessCalculator(ProjectConfig? project = null)
        {
            _pipeRadiusMm = ReadPositiveTarget(project, "PipeRadiusMm", BenchmarkPipeBend90GeometryGenerator.DefaultPipeRadiusMm);
            _inletStraightRatio = ReadPositiveTarget(project, "InletStraightRatio", BenchmarkPipeBend90GeometryGenerator.DefaultInletStraightRatio);
            _outletStraightRatio = ReadPositiveTarget(project, "OutletStraightRatio", BenchmarkPipeBend90GeometryGenerator.DefaultOutletStraightRatio);
            _density = ReadPositiveTarget(project, "ReferenceDensity", 998.2f);
            _viscosity = ReadPositiveTarget(project, "ReferenceViscosity", 0.001002f);
            _inletVelocity = ReadPositiveTarget(project, "ReferenceInletVelocity", 3.34f);
        }

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc />
        public override IReadOnlyCollection<string> RawComparisonMetrics => ComparisonMetricNames;

        /// <inheritdoc />
        public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
        {
            ArgumentNullException.ThrowIfNull(record);

            if (!record.PassiveParameters.TryGetValue("PressureDrop", out float pressureDrop) ||
                !float.IsFinite(pressureDrop))
            {
                return new EvaluationOutcome { ScalarFitnessOverride = DefaultFailedFitness };
            }

            float bendRadiusRatio = record.ActiveParameters.TryGetValue("BendRadiusRatio", out float activeRatio) &&
                                    float.IsFinite(activeRatio) && activeRatio > 0f
                ? activeRatio
                : DefaultBendRadiusRatio;

            double diameterM = 2.0 * _pipeRadiusMm * 0.001;
            double dynamicPressure = 0.5 * _density * _inletVelocity * _inletVelocity;
            double totalLossZeta = pressureDrop / Math.Max(dynamicPressure, 1e-12);

            double reynolds = (_density * _inletVelocity * diameterM) / Math.Max(_viscosity, 1e-12);
            double blasiusLambda = 0.3164 / Math.Pow(Math.Max(reynolds, 1.0), 0.25);

            // L_total / D = (L_in + L_out + (pi/2) * R_c) / (2 * r_pipe)
            double lengthOverDiameter = 0.5 * (_inletStraightRatio + _outletStraightRatio + 0.5 * Math.PI * bendRadiusRatio);
            double straightFrictionZeta = blasiusLambda * lengthOverDiameter;
            double bendLossZeta = totalLossZeta - straightFrictionZeta;

            record.PassiveParameters["TotalLossCoeff_Zeta"] = (float)totalLossZeta;
            record.PassiveParameters["StraightFriction_Zeta"] = (float)straightFrictionZeta;
            record.PassiveParameters["BendLossCoeff_Zeta"] = (float)bendLossZeta;

            return new EvaluationOutcome
            {
                Objectives =
                {
                    new ObjectiveValue("PressureDrop", pressureDrop, OptimizationDirection.Minimize)
                }
            };
        }

        private static float ReadPositiveTarget(ProjectConfig? project, string key, float fallback)
        {
            if (project?.OptimizationTargets == null || !project.OptimizationTargets.TryGetValue(key, out float value))
                return fallback;

            if (!float.IsFinite(value) || value <= 0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} BenchmarkPipeBend90 OptimizationTargets.{key} must be finite and > 0 (got {value}).");
            }

            return value;
        }
    }
}
