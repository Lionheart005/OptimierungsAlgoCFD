using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.BenchmarkPipeBend90
{
    /// <summary>
    /// 90-degree smooth pipe bend literature benchmark (<c>Internal3D</c> / OpenFOAM, Rc/D = 1.5, Re_D ~ 1e5).
    /// </summary>
    public sealed class BenchmarkPipeBend90Project : OpenFoamProjectDefinition
    {
        /// <inheritdoc />
        public override string Name => "BenchmarkPipeBend90";

        /// <inheritdoc />
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new BenchmarkPipeBend90GeometryGenerator(context.Project);

        /// <inheritdoc />
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new BenchmarkPipeBend90FitnessCalculator(context.Project);
    }
}
