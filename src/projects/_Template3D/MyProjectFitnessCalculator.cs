using System;
using System.Collections.Generic;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Projects.MyProject
{
    /// <summary>
    /// Project fitness evaluator. This template minimizes <b>drag only</b>:
    ///
    /// <code>Fitness = 1 / Drag</code>
    ///
    /// <para>
    /// <b>The framework always maximizes fitness.</b> A quantity to be minimized belongs
    /// in the denominator; one to be maximized belongs in the numerator. MantaAuv computes
    /// <c>(Volume * SensorDistance) / Drag^DragBalanceFactor</c> — usable volume and sensor
    /// baseline in the numerator, drag in the denominator with a configurable exponent.
    /// </para>
    ///
    /// <para>
    /// <b>Edit here.</b> The fitness formula is C# code: numbers belong in JSON,
    /// formulas belong in code. Constants used by the formula come from
    /// <c>OptimizationTargets</c> in project.json.
    /// </para>
    /// </summary>
    public sealed class MyProjectFitnessCalculator : IFitnessCalculator
    {
        /// <summary>
        /// Fallback fitness for non-evaluable models (failed simulation or missing drag).
        /// Non-zero so failed runs are distinguishable from a valid zero result, yet small
        /// enough that no optimizer treats them as desirable.
        /// </summary>
        private const float FailedFitness = 0.0001f;

        /// <summary>
        /// Target keys from <c>OptimizationTargets</c> required by the formula.
        ///
        /// <para>
        /// <b>Empty because this template only needs drag.</b> Any key listed here is
        /// validated in the constructor during wiring in <c>Program.cs</c> — failing
        /// immediately at startup instead of hours later with a bare <see cref="KeyNotFoundException"/>.
        /// </para>
        /// </summary>
        private static readonly string[] RequiredTargets = Array.Empty<string>();

        /// <summary>
        /// Metrics from <c>PassiveParameters</c> required by the formula.
        ///
        /// <para>
        /// <c>Drag</c> comes from SU2's <c>history.csv</c> via <c>ResultMetrics</c> in
        /// <c>solvers/su2.json</c>. Geometry metrics such as <c>Volume</c> or <c>SurfaceArea</c>
        /// are reported by the geometry generator via <c>AddMetric</c>; if the formula uses
        /// any of them, add their names here as well.
        /// </para>
        ///
        /// <para>
        /// <see cref="WorkflowController"/> checks this list after the very first completed
        /// variant and aborts if anything is missing.
        /// </para>
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag" };

        /// <summary>
        /// Validates required targets immediately and caches them in <c>readonly</c> fields.
        /// See <c>src/projects/MantaAuv/MantaFitnessCalculator.cs</c> for a full example.
        /// </summary>
        /// <exception cref="InvalidOperationException">A required target is missing in configuration.</exception>
        public MyProjectFitnessCalculator(ProjectConfig project)
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

            // Cache validated targets in readonly fields here, e.g.:
            //   _minimumAllowedVolume = project.OptimizationTargets["MinimumAllowedVolume"];
            // This avoids dictionary lookups inside CalculateFitness.
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        /// <inheritdoc/>
        public void CalculateFitness(ModelRecord record, SimulationConfig config)
        {
            // Failed meshing or simulation: the core signals this generically via SimulationFailed.
            // This check must come FIRST because PassiveParameters of a failed model are incomplete.
            if (record.SimulationFailed)
            {
                record.Fitness = FailedFitness;
                return;
            }

            float drag = record.PassiveParameters.TryGetValue("Drag", out float value)
                ? value
                : float.NaN;

            // Missing or non-finite drag: not evaluable.
            if (float.IsNaN(drag) || float.IsInfinity(drag))
            {
                record.Fitness = FailedFitness;
                return;
            }

            // Guard against sign and division by (near) zero — a single outlier with Drag ~ 0
            // would otherwise produce a fitness in the millions and dominate the entire run.
            float safeDrag = Math.Max(Math.Abs(drag), 0.001f);

            record.Fitness = 1.0f / safeDrag;

            // Add custom constraints here, e.g. a volume window:
            //
            //   float volume = record.PassiveParameters["Volume"];
            //   if (volume < _minimumAllowedVolume || volume > _maximumAllowedVolume)
            //       record.Fitness *= 0.1f;
            //
            // Prefer a penalty factor over 0 so invalid shapes remain comparable and the
            // optimizer can still learn which direction improves.
        }
    }
}
