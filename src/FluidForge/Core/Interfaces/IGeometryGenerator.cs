using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Generates the geometry for a project and returns an STL/contour path plus metrics.
    /// </summary>
    public interface IGeometryGenerator
    {
        /// <param name="parameters">The (optionally scaled) geometry parameters.</param>
        /// <param name="outputDirectory">Target directory for the generated geometry file.</param>
        /// <param name="config">
        /// Framework configuration — includes smoothing parameters and <see cref="SimulationConfig.UseStlSmoothing"/>.
        /// Passed as a whole object, matching <see cref="IMeshGenerator"/> and <see cref="ISimulationSolver"/>.
        /// </param>
        GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config);
    }
}
