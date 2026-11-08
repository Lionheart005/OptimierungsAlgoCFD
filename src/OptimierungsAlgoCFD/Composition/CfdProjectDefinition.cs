using System;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.PicoGk;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Composition
{
    /// <summary>
    /// Der Normalfall dieses Frameworks, fertig verdrahtet: PicoGK als Geometrie-Kernel, Gmsh
    /// als Vernetzer, SU2 als Solver. Ein CFD-Projekt muss dann nur noch seinen Namen, seine
    /// Vorgaben, seine Geometrie und seine Fitness beisteuern — rund 20 Zeilen.
    ///
    /// <para>
    /// Wer eine andere Kette braucht, überschreibt <see cref="CreateStages"/>; wer ohne
    /// Voxel-Kernel auskommt, <see cref="CreateKernel"/>. Beides im eigenen Projektordner,
    /// ohne den Kern anzufassen.
    /// </para>
    ///
    /// <para>
    /// <b>Warum diese Klasse nicht in <c>Core/</c> liegt:</b> sie muss <c>PicoGkKernel</c>,
    /// <c>GmshCfdMesher</c> und <c>Su2Solver</c> beim Namen nennen. TODO-20 hat nachgewiesen und
    /// vom Compiler erzwingen lassen, dass <c>Core/</c> <b>kein einziges</b> <c>using</c> auf
    /// <c>Kernels</c>, <c>Solvers</c> oder <c>Projects</c> hat — genau das war der Nachweis, dass
    /// die Schichtentrennung mehr ist als eine Ordner-Konvention. Eine Bequemlichkeits-Basisklasse
    /// ist es nicht wert, diese Eigenschaft aufzugeben. <c>Composition/</c> ist deshalb die
    /// Schicht, die alle anderen kennen darf — dieselbe Rolle wie <c>Program.cs</c>.
    /// </para>
    /// </summary>
    public abstract class CfdProjectDefinition : IProjectDefinition
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
        /// PicoGK. Ein Projekt, dessen Geometrie ohne Voxel-Modell entsteht, überschreibt das
        /// mit <see cref="DirectGeometryKernel"/> — sonst fährt beim Start eine Laufzeitumgebung
        /// hoch, die niemand braucht (und die unter Linux ein Xvfb verlangt).
        /// </summary>
        public virtual IGeometryKernel CreateKernel() => new PicoGkKernel();

        /// <summary>
        /// Eine Stufe: Gmsh vernetzt, SU2 rechnet. Die Zahlen dazu kommen aus
        /// <c>solvers/gmsh.json</c> und <c>solvers/su2.json</c> im Projektordner.
        /// Übergibt gemeinsame FlowConditions an Mesher und Solver (TODO-38).
        /// </summary>
        public virtual SolverStage[] CreateStages(ProjectContext context)
        {
            var gmshOptions = context.LoadSolverOptions<GmshMesherOptions>("gmsh");
            var su2Options = context.LoadSolverOptions<Su2SolverOptions>("su2");

            FlowConditions flow = FlowConditions.From(context.Simulation, su2Options);

            return new[]
            {
                new SolverStage(
                    new GmshCfdMesher(gmshOptions, flow),
                    new Su2Solver(su2Options, flow))
            };
        }
    }
}
