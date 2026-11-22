using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Composition
{
    /// <summary>
    /// Predefined base class for two-dimensional CFD projects (TODO-46).
    /// Uses <see cref="DirectGeometryKernel"/> (no PicoGK, no Xvfb required on Linux)
    /// and connects <see cref="Gmsh2DMesher"/> with <see cref="Su2Solver"/> (Dimensions = 2).
    /// </summary>
    public abstract class Cfd2DProjectDefinition : CfdProjectDefinitionBase
    {
        /// <inheritdoc/>
        protected override void ConfigureSu2Options(Su2SolverOptions options)
        {
            if (options.Dimensions != 2)
            {
                options.Dimensions = 2;
            }
        }

        /// <inheritdoc/>
        protected override IMeshGenerator CreateMesher(ProjectContext context, FlowConditions flow)
        {
            var gmshOptions = context.LoadSolverOptions<Gmsh2DMesherOptions>("gmsh");
            return new Gmsh2DMesher(gmshOptions, flow);
        }
    }
}
