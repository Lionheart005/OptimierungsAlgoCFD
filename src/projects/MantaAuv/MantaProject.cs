using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.MantaAuv
{
    /// <summary>
    /// Project definition for the Manta-Ray AUV.
    ///
    /// <para>
    /// Kernel and solver pipeline are inherited unchanged from <see cref="CfdProjectDefinition"/>:
    /// PicoGK, Gmsh, and SU2.
    /// </para>
    /// </summary>
    public sealed class MantaProject : CfdProjectDefinition
    {
        /// <summary>Must match the folder name under <c>src/projects/</c>.</summary>
        public override string Name => "MantaAuv";

        /// <inheritdoc/>
        public override ProjectConfig CreateDefaults() => MantaProjectConfig.Create();

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MantaGeometryGenerator();

        /// <inheritdoc/>
        public override IGeometryConstraintEvaluator? CreateGeometryConstraints(ProjectContext context)
            => new MantaGeometryConstraints();

        /// <summary>
        /// Validates required optimization targets in the constructor so missing keys
        /// fail immediately rather than hours into a run.
        /// </summary>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MantaFitnessCalculator(context.Project);
    }
}
