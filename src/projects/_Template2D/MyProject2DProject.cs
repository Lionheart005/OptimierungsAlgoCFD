using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.MyProject2D
{
    /// <summary>
    /// Project wiring for a 2D CFD project.
    /// Based on <see cref="Cfd2DProjectDefinition"/>: uses the direct 2D kernel
    /// (no PicoGK, no Xvfb) and connects Gmsh2DMesher with 2D SU2.
    ///
    /// <para>
    /// Parameters, bounds, and targets are defined in <c>project.json</c> (Single Source of Truth).
    /// </para>
    /// </summary>
    public sealed class MyProject2DProject : Cfd2DProjectDefinition
    {
        /// <summary>
        /// Must match the folder name under <c>src/projects/</c> <b>exactly</b>.
        /// </summary>
        public override string Name => "MyProject2D";

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MyProject2DGeometryGenerator();

        /// <inheritdoc/>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MyProject2DFitnessCalculator(context.Project);
    }
}
