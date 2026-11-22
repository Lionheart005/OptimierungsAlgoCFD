using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.PipeBend90
{
    /// <summary>
    /// Project wiring for the 90-degree pipe bend (<c>Internal3D</c> OpenFOAM example project).
    /// </summary>
    public sealed class PipeBend90Project : OpenFoamProjectDefinition
    {
        /// <inheritdoc />
        public override string Name => "PipeBend90";

        /// <inheritdoc />
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new PipeBend90GeometryGenerator();

        /// <inheritdoc />
        public override IGeometryConstraintEvaluator? CreateGeometryConstraints(ProjectContext context)
            => new PipeBend90GeometryConstraints();

        /// <inheritdoc />
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new PipeBend90FitnessCalculator(context.Project);
    }
}
