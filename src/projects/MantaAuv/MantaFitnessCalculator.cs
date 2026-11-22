using System;
using System.Collections.Generic;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Projects.MantaAuv
{
    /// <summary>
    /// Manta-Ray AUV aerodynamic/hydrodynamic evaluator.
    /// Computes the dimensionless volumetric drag coefficient
    /// <c>C_{D,V} = C_D * A_front / V^(2/3)</c> and maximizes
    /// <c>HydrodynamicEfficiency = SensorDistance / max(C_{D,V}, 0.001)^DragBalanceFactor</c>.
    /// Volume bounds are enforced strictly pre-CFD via <see cref="MantaGeometryConstraints"/>.
    /// </summary>
    public class MantaFitnessCalculator : ObjectiveEvaluatorBase
    {
        /// <summary>Target keys required by the fitness formula.</summary>
        private static readonly string[] RequiredTargets =
        {
            "MinimumAllowedVolume", "MaximumAllowedVolume", "DragBalanceFactor"
        };

        /// <summary>
        /// Metrics required by the formula: Drag comes from SU2's history.csv, while
        /// FrontalArea, Volume, and SensorDistance come from <see cref="MantaGeometryGenerator"/>.
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag", "FrontalArea", "Volume", "SensorDistance" };

        private static readonly string[] ComparisonMetricNames =
        {
            "Drag", "FrontalArea", "Volume", "SensorDistance", "VolumetricDragCoeff"
        };

        private readonly float _dragBalanceFactor;

        /// <summary>
        /// Validates required targets immediately so missing keys fail during wiring
        /// in <c>Program.cs</c> before the first simulation rather than hours into a run.
        /// </summary>
        /// <exception cref="InvalidOperationException">A required target is missing in configuration.</exception>
        public MantaFitnessCalculator(ProjectConfig project)
        {
            var missing = RequiredTargets.Where(key => !project.OptimizationTargets.ContainsKey(key)).ToList();

            if (missing.Count > 0)
            {
                string present = project.OptimizationTargets.Count > 0
                    ? string.Join(", ", project.OptimizationTargets.Keys)
                    : "(none)";

                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} Project '{project.ProjectName}': missing OptimizationTargets " +
                    $"{string.Join(", ", missing)}. Manta fitness requires {string.Join(", ", RequiredTargets)}. " +
                    $"Present: {present}. Add them in src/projects/{project.ProjectName}/project.json " +
                    $"(or in MantaProjectConfig.Create()).");
            }

            _dragBalanceFactor = project.OptimizationTargets["DragBalanceFactor"];
        }

        /// <inheritdoc/>
        public override IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc/>
        public override IReadOnlyCollection<string> RawComparisonMetrics => ComparisonMetricNames;

        /// <inheritdoc/>
        public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
        {
            if (!record.PassiveParameters.TryGetValue("Drag", out float drag) ||
                !record.PassiveParameters.TryGetValue("FrontalArea", out float frontalArea) ||
                !record.PassiveParameters.TryGetValue("Volume", out float volume) ||
                !record.PassiveParameters.TryGetValue("SensorDistance", out float sensorDist) ||
                drag == float.MaxValue ||
                !float.IsFinite(drag) ||
                !float.IsFinite(frontalArea) ||
                !float.IsFinite(volume) ||
                !float.IsFinite(sensorDist) ||
                frontalArea <= 0f ||
                volume <= 0f)
            {
                return new EvaluationOutcome { ScalarFitnessOverride = DefaultFailedFitness };
            }

            // Volumetric drag coefficient C_{D,V} = C_D * A_front / V^(2/3)
            // (FrontalArea in mm^2, Volume in mm^3 -> V^(2/3) in mm^2 -> dimensionless).
            float safeDrag = Math.Max(Math.Abs(drag), 0.001f);
            float volumeTwoThirds = MathF.Pow(volume, 2.0f / 3.0f);
            float cdV = (safeDrag * frontalArea) / Math.Max(volumeTwoThirds, 1e-6f);
            record.PassiveParameters["VolumetricDragCoeff"] = cdV;

            float adjustedCdV = MathF.Pow(MathF.Max(cdV, 0.001f), _dragBalanceFactor);
            float hydroEfficiency = sensorDist / adjustedCdV;
            record.PassiveParameters["HydrodynamicEfficiency"] = hydroEfficiency;

            return new EvaluationOutcome
            {
                Objectives =
                {
                    new ObjectiveValue("HydrodynamicEfficiency", hydroEfficiency, OptimizationDirection.Maximize)
                }
            };
        }
    }
}
