using System;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Composition
{
    /// <summary>
    /// Vordefinierte Basisklasse für zweidimensionale CFD-Projekte (TODO-46).
    /// Nutzt <see cref="DirectGeometryKernel"/> (kein PicoGK, kein Xvfb unter Linux erforderlich)
    /// und verbindet <see cref="Gmsh2DMesher"/> mit <see cref="Su2Solver"/> (Dimensions = 2).
    /// </summary>
    public abstract class Cfd2DProjectDefinition : IProjectDefinition
    {
        /// <inheritdoc/>
        public abstract string Name { get; }

        /// <inheritdoc/>
        public abstract ProjectConfig CreateDefaults();

        /// <inheritdoc/>
        public abstract IGeometryGenerator CreateGeometry(ProjectContext context);

        /// <inheritdoc/>
        public abstract IFitnessCalculator CreateFitness(ProjectContext context);

        /// <summary>
        /// DirectGeometryKernel: 2D-CFD-Geometrie entsteht ohne PicoGK-Voxelmodell
        /// und benötigt kein Xvfb oder OpenGL unter Linux.
        /// </summary>
        public virtual IGeometryKernel CreateKernel() => new DirectGeometryKernel();

        /// <summary>
        /// Eine Stufe: Gmsh2DMesher vernetzt die 2D-Kontur, Su2Solver rechnet in 2D (Dimensions = 2).
        /// Die Optionen kommen aus solvers/gmsh.json und solvers/su2.json.
        /// </summary>
        public virtual SolverStage[] CreateStages(ProjectContext context)
        {
            var gmshOptions = context.LoadSolverOptions<Gmsh2DMesherOptions>("gmsh");
            var su2Options = context.LoadSolverOptions<Su2SolverOptions>("su2");

            if (su2Options.Dimensions != 2)
            {
                su2Options.Dimensions = 2;
            }

            FlowConditions flow = FlowConditions.From(context.Simulation, su2Options);

            return new[]
            {
                new SolverStage(
                    new Gmsh2DMesher(gmshOptions, flow),
                    new Su2Solver(su2Options, flow))
            };
        }
    }
}
