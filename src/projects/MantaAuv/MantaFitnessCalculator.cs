using System;
using System.Collections.Generic;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Projects.MantaAuv
{
    /// <summary>
    /// Manta-Ray AUV fitness calculation.
    /// Formula: Fitness = (Volume * SensorDistance) / Drag^DragBalanceFactor
    /// </summary>
    public class MantaFitnessCalculator : IFitnessCalculator
    {
        /// <summary>Target keys required by the fitness formula.</summary>
        private static readonly string[] RequiredTargets =
        {
            "MinimumAllowedVolume", "MaximumAllowedVolume", "DragBalanceFactor"
        };

        /// <summary>
        /// Metrics required by the formula: Drag comes from SU2's history.csv
        /// (mapped in su2.json under 'ResultMetrics'), while Volume and SensorDistance
        /// are reported by <see cref="MantaGeometryGenerator"/>.
        ///
        /// FrontalArea is intentionally omitted here: the fitness formula does not use it,
        /// it is passed as REF_AREA to the solver (which warns if it is missing).
        /// </summary>
        private static readonly string[] RequiredMetricNames = { "Drag", "Volume", "SensorDistance" };

        private readonly float _minimumAllowedVolume;
        private readonly float _maximumAllowedVolume;
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

            _minimumAllowedVolume = project.OptimizationTargets["MinimumAllowedVolume"];
            _maximumAllowedVolume = project.OptimizationTargets["MaximumAllowedVolume"];
            _dragBalanceFactor = project.OptimizationTargets["DragBalanceFactor"];
        }

        /// <inheritdoc/>
        public IReadOnlyCollection<string> RequiredMetrics => RequiredMetricNames;

        public void CalculateFitness(ModelRecord record, SimulationConfig config)
        {
            // Failed meshing or simulation: the core signals this generically via SimulationFailed.
            if (record.SimulationFailed)
            {
                record.Fitness = 0.0001f;
                return;
            }

            float drag = record.PassiveParameters.ContainsKey("Drag") ? record.PassiveParameters["Drag"] : float.MaxValue;
            float volume = record.PassiveParameters.ContainsKey("Volume") ? record.PassiveParameters["Volume"] : 0f;
            float sensorDist = record.PassiveParameters.ContainsKey("SensorDistance") ? record.PassiveParameters["SensorDistance"] : 0f;

            if (drag == float.MaxValue || float.IsNaN(drag))
            {
                record.Fitness = 0.0001f;
                return;
            }

            // Targets were validated and cached in the constructor.
            float minVol = _minimumAllowedVolume;
            float maxVol = _maximumAllowedVolume;
            float dragBalance = _dragBalanceFactor;

            // 1. Guard denominator against sign and near-zero values
            float realDrag = Math.Abs(drag);
            float safeDrag = Math.Max(realDrag, 0.001f);

            // 2. Mathematical fitness equation for the AUV concept
            float adjustedDrag = (float)Math.Pow(safeDrag, dragBalance);

            // Fitness = (Volume * SensorDistance) / adjustedDrag
            record.Fitness = (volume * sensorDist) / adjustedDrag;

            // 3. Volume constraint penalty
            if (volume < minVol || volume > maxVol)
            {
                record.Fitness *= 0.1f; // Strong penalty, but non-zero so partial shapes remain comparable
            }
        }
    }
}
