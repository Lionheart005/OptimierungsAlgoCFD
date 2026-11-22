namespace FluidForge.Core
{
    /// <summary>
    /// Self-wiring definition for a project.
    ///
    /// <para>
    /// Each project provides an implementation of this interface in its own folder under
    /// <c>src/projects/</c>, and <see cref="ProjectRegistry"/> discovers it via reflection at startup.
    /// </para>
    ///
    /// <para>
    /// Standard CFD projects (PicoGK, Gmsh, SU2) can inherit from <c>CfdProjectDefinition</c>
    /// and only implement <see cref="Name"/>, <see cref="CreateDefaults"/>,
    /// <see cref="CreateGeometry"/>, and <see cref="CreateFitness"/>.
    /// </para>
    /// </summary>
    public interface IProjectDefinition
    {
        /// <summary>
        /// Must match the folder name under <c>src/projects/</c> exactly.
        /// <see cref="ProjectRegistry"/> validates both directions at startup.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Code-level project defaults: initial parameters, bounds, and optimization targets.
        /// <c>project.json</c> in the project folder overrides any keys it specifies.
        /// </summary>
        ProjectConfig CreateDefaults();

        /// <summary>
        /// Runtime environment for geometry creation. Typically <c>PicoGkKernel</c>;
        /// non-voxel projects use <see cref="DirectGeometryKernel"/>.
        /// </summary>
        IGeometryKernel CreateKernel();

        /// <summary>Creates the project's geometry generator.</summary>
        IGeometryGenerator CreateGeometry(ProjectContext context);

        /// <summary>Creates the project's fitness calculator.</summary>
        IFitnessCalculator CreateFitness(ProjectContext context);

        /// <summary>
        /// Pipeline of mesher and solver stages. Stages sharing a single mesher <b>instance</b>
        /// mesh only once.
        /// </summary>
        SolverStage[] CreateStages(ProjectContext context);
    }
}
