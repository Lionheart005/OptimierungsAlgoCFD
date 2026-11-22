using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.MyProject
{
    /// <summary>
    /// Project wiring. <b>This class is all the framework needs to know about a project</b> —
    /// <see cref="ProjectRegistry"/> discovers it via reflection (no switch statement or manual list).
    ///
    /// <para>
    /// <b><c>MyProject</c> is a placeholder name.</b>
    /// <c>scripts\new-project.ps1 &lt;Name&gt;</c> copies this folder and replaces the placeholder
    /// in filenames, class names, namespaces, and JSON comment headers.
    /// </para>
    ///
    /// <para>
    /// Parameters, bounds, and targets are defined in <c>project.json</c> (Single Source of Truth).
    /// </para>
    /// </summary>
    public sealed class MyProjectProject : CfdProjectDefinition
    {
        /// <summary>
        /// Must match the folder name under <c>src/projects/</c> <b>exactly</b>.
        /// <see cref="ProjectRegistry"/> validates both directions at startup.
        /// </summary>
        public override string Name => "MyProject";

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MyProjectGeometryGenerator();

        /// <summary>
        /// Validates required optimization targets in the constructor so missing keys fail fast.
        /// </summary>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MyProjectFitnessCalculator(context.Project);
    }
}
