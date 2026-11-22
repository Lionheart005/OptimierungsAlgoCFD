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
        public virtual ProjectConfig CreateDefaults() => new ProjectConfig();

        /// <inheritdoc/>
        public abstract IGeometryGenerator CreateGeometry(ProjectContext context);

        /// <inheritdoc/>
        public abstract IFitnessCalculator CreateFitness(ProjectContext context);

        /// <inheritdoc/>
        public virtual IGeometryConstraintEvaluator? CreateGeometryConstraints(ProjectContext context) => null;

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

            SymmetryAxis su2Sym = StlHalfClipper.ParseConfigValue(su2Options.SymmetryPlane);
            FlowConditions flow = FlowConditions.From(context.Simulation, su2Options);
            IMeshGenerator mesher = CreateMesher(context, flow);

            if (mesher is GmshCfdMesher gmshMesher)
            {
                SymmetryAxis gmshSym = StlHalfClipper.ParseConfigValue(gmshMesher.Options.SymmetryPlane);
                if (su2Sym != SymmetryAxis.None && gmshSym != SymmetryAxis.None && su2Sym != gmshSym)
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} Conflicting SymmetryPlane settings: solvers/gmsh.json specifies '{gmshMesher.Options.SymmetryPlane}', "
                        + $"while solvers/su2.json specifies '{su2Options.SymmetryPlane}'. Both must match or only one may be set.");
                }

                if (gmshSym != SymmetryAxis.None && su2Sym == SymmetryAxis.None)
                {
                    su2Options.SymmetryPlane = gmshMesher.Options.SymmetryPlane;
                }
                else if (su2Sym != SymmetryAxis.None && gmshSym == SymmetryAxis.None)
                {
                    gmshMesher.Options.SymmetryPlane = su2Options.SymmetryPlane;
                }
            }

            return new[]
            {
                new SolverStage(mesher, new Su2Solver(su2Options, flow))
            };
        }
    }
}
