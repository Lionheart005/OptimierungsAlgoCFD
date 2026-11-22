using System;
using System.Collections.Generic;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Projects.Naca2D
{
    /// <summary>
    /// Aerodynamic fitness evaluator for the 2D NACA airfoil.
    /// Maximizes the lift-to-drag ratio E = CL / CD:
    /// <code>Fitness = CL / safeDrag (glide ratio Lift / Drag)</code>
    /// </summary>
    public sealed class Naca2DFitnessCalculator : IFitnessCalculator
    {
        /// <summary>
        /// Fallback fitness for non-evaluable models (failed simulation, missing or invalid
        /// lift/drag, or negative lift).
        /// </summary>
        private const float FailedFitness = 0.0001f;

        /// <summary>
        /// Required target keys from <c>OptimizationTargets</c>.
        /// Empty because glide ratio is computed purely from CFD coefficients.
        /// </summary>
        private static readonly string[] RequiredTargets = Array.Empty<string>();

        /// <summary>
        /// Metrics from <c>PassiveParameters</c> required for fitness evaluation.
        /// Both Drag (CD) and Lift (CL) come from SU2's history.csv via solvers/su2.json.
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag", "Lift" };

        /// <summary>
        /// Validates required targets immediately in the constructor.
        /// </summary>
        public Naca2DFitnessCalculator(ProjectConfig project)
        {
            var missing = RequiredTargets
                .Where(key => !project.OptimizationTargets.ContainsKey(key))
                .ToList();

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

        /// <inheritdoc/>
        public IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc/>
        public void CalculateFitness(ModelRecord record, SimulationConfig config)
        {
            if (record.SimulationFailed)
            {
                record.Fitness = FailedFitness;
                return;
            }

            bool hasDrag = record.PassiveParameters.TryGetValue("Drag", out float drag);
            bool hasLift = record.PassiveParameters.TryGetValue("Lift", out float lift);

            if (!hasDrag || !hasLift ||
                float.IsNaN(drag) || float.IsInfinity(drag) ||
                float.IsNaN(lift) || float.IsInfinity(lift))
            {
                record.Fitness = FailedFitness;
                return;
            }

            // Guard drag coefficient against sign and division by (near) zero:
            float safeDrag = Math.Max(Math.Abs(drag), 0.001f);

            // Aerodynamic lift-to-drag ratio E = CL / CD:
            float glideRatio = lift / safeDrag;
            record.PassiveParameters["GlideRatio"] = glideRatio;

            // Clamp lower bound at FailedFitness:
            record.Fitness = Math.Max(glideRatio, FailedFitness);
        }
    }
}
