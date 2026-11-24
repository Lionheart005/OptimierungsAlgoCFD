using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.BenchmarkSuboffOpenFoam
{
    /// <summary>
    /// DARPA SUBOFF AFF-1 bare-hull benchmark project using OpenFOAM (<c>External3D</c>, <c>Y0</c> symmetry)
    /// and <see cref="DirectGeometryKernel"/> (no PicoGK required).
    /// </summary>
    public sealed class BenchmarkSuboffOpenFoamProject : OpenFoamProjectDefinition
    {
        /// <inheritdoc />
        public override string Name => "BenchmarkSuboffOpenFoam";

        /// <inheritdoc />
        public override IGeometryKernel CreateKernel() => new DirectGeometryKernel();

        /// <inheritdoc />
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new BenchmarkSuboffOpenFoamGeometryGenerator(context.Project);

        /// <inheritdoc />
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new BenchmarkSuboffOpenFoamFitnessCalculator(context.Project);
    }
}
