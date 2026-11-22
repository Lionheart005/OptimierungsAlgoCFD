using FluidForge.Core;
using FluidForge.Kernels.PicoGk;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Composition
{
    /// <summary>
    /// Standard CFD project setup, pre-wired: PicoGK as the geometry kernel, Gmsh
    /// as the mesher, and SU2 as the solver. A CFD project only needs to supply its name,
    /// defaults, geometry, and fitness calculation — around 20 lines of code.
    /// </summary>
    public abstract class CfdProjectDefinition : CfdProjectDefinitionBase
    {
        /// <summary>
        /// PicoGK. A project whose geometry is created without a voxel model overrides this with
        /// <see cref="DirectGeometryKernel"/> — otherwise startup spins up an unneeded runtime
        /// environment (which requires Xvfb on Linux).
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
