using System;
using System.Collections.Generic;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Projects.MyProject2D
{
    /// <summary>
    /// Fitness evaluator for a 2D CFD project.
    /// This template minimizes <b>drag only</b>:
    ///
    /// <code>Fitness = 1 / Drag</code>
    ///
    /// <para>
    /// <b>The framework always maximizes fitness.</b> A quantity to be minimized belongs
    /// in the denominator; one to be maximized belongs in the numerator.
    /// </para>
    /// </summary>
    public sealed class MyProject2DFitnessCalculator : IFitnessCalculator
    {
        /// <summary>
        /// Fallback fitness for non-evaluable models (failed simulation or missing drag).
        /// </summary>
        private const float FailedFitness = 0.0001f;

        /// <summary>
        /// Required target keys from <c>OptimizationTargets</c>.
        /// Empty because this template formula only needs drag.
        /// </summary>
        private static readonly string[] RequiredTargets = Array.Empty<string>();

        /// <summary>
        /// Metrics from <c>PassiveParameters</c> required for fitness evaluation.
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag" };

        /// <summary>
        /// Validates required targets immediately in the constructor.
        /// </summary>
        public MyProject2DFitnessCalculator(ProjectConfig project)
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

            float drag = record.PassiveParameters.TryGetValue("Drag", out float value)
                ? value
                : float.NaN;

            if (float.IsNaN(drag) || float.IsInfinity(drag))
            {
                record.Fitness = FailedFitness;
                return;
            }

            // Guard drag coefficient against sign and division by (near) zero:
            float safeDrag = Math.Max(Math.Abs(drag), 0.001f);

            record.Fitness = 1.0f / safeDrag;
        }
    }
}
