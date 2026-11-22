using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Composition
{
    /// <summary>
    /// Shared base class for CFD project definitions (3D and 2D).
    /// Consolidates the identical contract and stage-creation logic, while subclasses
    /// provide the kernel, mesher, and optional SU2 adjustments.
    /// </summary>
    public abstract class CfdProjectDefinitionBase : IProjectDefinition
    {
        /// <inheritdoc/>
        public abstract string Name { get; }

        /// <inheritdoc/>
        public abstract ProjectConfig CreateDefaults();

        /// <inheritdoc/>
        public abstract IGeometryGenerator CreateGeometry(ProjectContext context);

        /// <inheritdoc/>
        public abstract IFitnessCalculator CreateFitness(ProjectContext context);

        /// <inheritdoc/>
        public virtual IGeometryKernel CreateKernel() => new DirectGeometryKernel();

        /// <summary>Allows subclasses to adjust loaded SU2 options before building FlowConditions and the solver.</summary>
        protected virtual void ConfigureSu2Options(Su2SolverOptions options) { }

        /// <summary>Creates the project-specific mesher for the use case (e.g., 3D or 2D Gmsh).</summary>
        protected abstract IMeshGenerator CreateMesher(ProjectContext context, FlowConditions flow);

        /// <summary>
        /// Builds the standard CFD stage: loads <c>su2.json</c>, applies <see cref="ConfigureSu2Options"/>,
        /// creates shared <see cref="FlowConditions"/>, and wires <see cref="CreateMesher"/> to <see cref="Su2Solver"/>.
        /// </summary>
        public virtual SolverStage[] CreateStages(ProjectContext context)
        {
            var su2Options = context.LoadSolverOptions<Su2SolverOptions>("su2");
            ConfigureSu2Options(su2Options);

            FlowConditions flow = FlowConditions.From(context.Simulation, su2Options);
            IMeshGenerator mesher = CreateMesher(context, flow);

            return new[]
            {
                new SolverStage(mesher, new Su2Solver(su2Options, flow))
            };
        }
    }
}
