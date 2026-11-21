using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Composition
{
    /// <summary>
    /// Vordefinierte Basisklasse für zweidimensionale CFD-Projekte (TODO-46).
    /// Nutzt <see cref="DirectGeometryKernel"/> (kein PicoGK, kein Xvfb unter Linux erforderlich)
    /// und verbindet <see cref="Gmsh2DMesher"/> mit <see cref="Su2Solver"/> (Dimensions = 2).
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
