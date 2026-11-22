using FluidForge.Core;
using FluidForge.Kernels.PicoGk;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Composition
{
    /// <summary>
    /// Der Normalfall dieses Frameworks, fertig verdrahtet: PicoGK als Geometrie-Kernel, Gmsh
    /// als Vernetzer, SU2 als Solver. Ein CFD-Projekt muss dann nur noch seinen Namen, seine
    /// Vorgaben, seine Geometrie und seine Fitness beisteuern — rund 20 Zeilen.
    /// </summary>
    public abstract class CfdProjectDefinition : CfdProjectDefinitionBase
    {
        /// <summary>
        /// PicoGK. Ein Projekt, dessen Geometrie ohne Voxel-Modell entsteht, überschreibt das
        /// mit <see cref="DirectGeometryKernel"/> — sonst fährt beim Start eine Laufzeitumgebung
        /// hoch, die niemand braucht (und die unter Linux ein Xvfb verlangt).
        /// </summary>
        public override IGeometryKernel CreateKernel() => new PicoGkKernel();

        /// <inheritdoc/>
        protected override IMeshGenerator CreateMesher(ProjectContext context, FlowConditions flow)
        {
            var gmshOptions = context.LoadSolverOptions<GmshMesherOptions>("gmsh");
            return new GmshCfdMesher(gmshOptions, flow);
        }
    }
}
