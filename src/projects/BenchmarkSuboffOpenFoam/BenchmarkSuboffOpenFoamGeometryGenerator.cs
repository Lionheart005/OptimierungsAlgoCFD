using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.BenchmarkSuboffOpenFoam
{
    /// <summary>
    /// Geometry generator for <c>BenchmarkSuboffOpenFoam</c>, delegating to <see cref="DarpaSuboffHullBuilder"/>.
    /// </summary>
    public sealed class BenchmarkSuboffOpenFoamGeometryGenerator : IGeometryGenerator
    {
        private readonly float _modelScaleFactor;

        /// <summary>
        /// Initializes the generator, optionally reading <c>OptimizationTargets.ModelScaleFactor</c>.
        /// </summary>
        public BenchmarkSuboffOpenFoamGeometryGenerator(ProjectConfig? project = null)
        {
            _modelScaleFactor = DarpaSuboffHullBuilder.ReadModelScaleFactor(project);
        }

        /// <inheritdoc />
        public GeometryResult GenerateAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
            => DarpaSuboffHullBuilder.BuildAndExport(
                iteration,
                variant,
                parameters,
                outputDirectory,
                _modelScaleFactor);
    }
}
