using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.MyInternalProject
{
    /// <summary>
    /// Project wiring for an OpenFOAM 3D internal-flow project.
    /// <c>scripts\new-project.ps1 &lt;Name&gt; -Template Internal3D</c> copies this folder
    /// and replaces <c>MyInternalProject</c> across filenames, classes, namespaces, and JSON headers.
    /// </summary>
    public sealed class MyInternalProjectProject : OpenFoamProjectDefinition
    {
        /// <inheritdoc />
        public override string Name => "MyInternalProject";

        /// <inheritdoc />
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MyInternalProjectGeometryGenerator(context.Project);

        /// <inheritdoc />
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MyInternalProjectFitnessCalculator(context.Project);
    }
}
