using FluidForge.Core;
using FluidForge.Kernels.PicoGk;
using FluidForge.Solvers.OpenFoam;

namespace FluidForge.Composition
{
    /// <summary>
    /// Base class for OpenFOAM 3D internal/external CFD projects:
    /// wires <see cref="PicoGkKernel"/>, <see cref="OpenFoamSnappyMesher"/>, and
    /// <see cref="OpenFoamSimpleSolver"/> using <c>solvers/openfoam.json</c>.
    /// </summary>
    public abstract class OpenFoamProjectDefinition : IProjectDefinition
    {
        /// <inheritdoc />
        public abstract string Name { get; }

        /// <inheritdoc />
        public virtual ProjectConfig CreateDefaults() => new ProjectConfig();

        /// <inheritdoc />
        public virtual IGeometryKernel CreateKernel() => new PicoGkKernel();

        /// <inheritdoc />
        public abstract IGeometryGenerator CreateGeometry(ProjectContext context);

        /// <inheritdoc />
        public abstract IFitnessCalculator CreateFitness(ProjectContext context);

        /// <inheritdoc />
        public virtual IGeometryConstraintEvaluator? CreateGeometryConstraints(ProjectContext context) => null;

        /// <summary>Allows subclasses to adjust loaded OpenFOAM options before constructing mesher and solver.</summary>
        protected virtual void ConfigureOpenFoamOptions(OpenFoamOptions options) { }

        /// <inheritdoc />
        public virtual SolverStage[] CreateStages(ProjectContext context)
        {
            var options = context.LoadSolverOptions<OpenFoamOptions>("openfoam");
            ConfigureOpenFoamOptions(options);

            return new[]
            {
                new SolverStage(new OpenFoamSnappyMesher(options), new OpenFoamSimpleSolver(options))
            };
        }
    }
}
