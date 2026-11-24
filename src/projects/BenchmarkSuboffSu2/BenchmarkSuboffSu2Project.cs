using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.BenchmarkSuboffSu2
{
    /// <summary>
    /// DARPA SUBOFF AFF-1 bare-hull benchmark project using Gmsh 3D and SU2 (<c>External3D</c>, <c>Y0</c> symmetry)
    /// with <see cref="DirectGeometryKernel"/> (no PicoGK required).
    /// </summary>
    public sealed class BenchmarkSuboffSu2Project : CfdProjectDefinition
    {
        /// <inheritdoc />
        public override string Name => "BenchmarkSuboffSu2";

        /// <inheritdoc />
        public override IGeometryKernel CreateKernel() => new DirectGeometryKernel();

        /// <inheritdoc />
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new BenchmarkSuboffSu2GeometryGenerator(context.Project);

        /// <inheritdoc />
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new BenchmarkSuboffSu2FitnessCalculator(context.Project);
    }
}
