using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Project wiring for the 1-to-12 EV hairpin stator end-winding cooling manifold
    /// (<c>Internal3D</c> multi-objective OpenFOAM optimization project).
    /// </summary>
    public sealed class StatorCoolingRingProject : OpenFoamProjectDefinition
    {
        /// <inheritdoc />
        public override string Name => "StatorCoolingRing";

        /// <inheritdoc />
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new StatorCoolingRingGeometryGenerator(context.Project);

        /// <inheritdoc />
        public override IGeometryConstraintEvaluator? CreateGeometryConstraints(ProjectContext context)
            => new StatorCoolingRingGeometryConstraints();

        /// <inheritdoc />
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new StatorCoolingRingFitnessCalculator(context.Project);
    }
}
