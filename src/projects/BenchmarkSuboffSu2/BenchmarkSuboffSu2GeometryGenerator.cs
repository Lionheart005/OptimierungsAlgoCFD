using System.Collections.Generic;
using FluidForge.Core;
using FluidForge.Projects.BenchmarkSuboffOpenFoam;

namespace FluidForge.Projects.BenchmarkSuboffSu2
{
    /// <summary>
    /// Geometry generator for <c>BenchmarkSuboffSu2</c>, reusing <see cref="DarpaSuboffHullBuilder"/>.
    /// </summary>
    public sealed class BenchmarkSuboffSu2GeometryGenerator : IGeometryGenerator
    {
        private readonly float _modelScaleFactor;

        /// <summary>
        /// Initializes the generator, optionally reading <c>OptimizationTargets.ModelScaleFactor</c>.
        /// </summary>
        public BenchmarkSuboffSu2GeometryGenerator(ProjectConfig? project = null)
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
