using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Composition
{
    /// <summary>
    /// Gemeinsame Basisklasse für CFD-Projektdefinitionen (3D und 2D).
    /// Bündelt die identische Vertrags- und Stage-Erzeugungslogik, während Unterklassen
    /// Kernel, Mesher und optionale SU2-Anpassungen beisteuern.
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

        /// <summary>Ermöglicht Unterklassen, die geladenen SU2-Optionen vor dem Bau von FlowConditions/Solver anzupassen.</summary>
        protected virtual void ConfigureSu2Options(Su2SolverOptions options) { }

        /// <summary>Erzeugt den projektspezifischen Mesher für den jeweiligen Anwendungsfall (z. B. 3D- oder 2D-Gmsh).</summary>
        protected abstract IMeshGenerator CreateMesher(ProjectContext context, FlowConditions flow);

        /// <summary>
        /// Baut die Standard-CFD-Stufe: lädt <c>su2.json</c>, wendet <see cref="ConfigureSu2Options"/> an,
        /// erstellt gemeinsame <see cref="FlowConditions"/> und verdrahtet <see cref="CreateMesher"/> mit <see cref="Su2Solver"/>.
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
