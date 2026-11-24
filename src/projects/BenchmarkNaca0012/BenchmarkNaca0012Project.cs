using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.BenchmarkNaca0012
{
    /// <summary>
    /// NASA TMR NACA 0012 2D validation benchmark project (<c>External2D</c> / SU2 / SA).
    /// </summary>
    public sealed class BenchmarkNaca0012Project : Cfd2DProjectDefinition
    {
        /// <inheritdoc />
        public override string Name => "BenchmarkNaca0012";

        /// <inheritdoc />
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new BenchmarkNaca0012GeometryGenerator(context.Project);

        /// <inheritdoc />
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new BenchmarkNaca0012FitnessCalculator(context.Project);
    }
}
