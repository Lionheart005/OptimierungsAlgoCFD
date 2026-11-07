using System.Collections.Generic;

using MyPicoGkProject.Composition;
using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.MeinProjekt2D
{
    /// <summary>
    /// Die Verdrahtung eines zweidimensionalen CFD-Projekts (TODO-47).
    /// Basiert auf <see cref="Cfd2DProjectDefinition"/>: nutzt den direkten 2D-Kernel
    /// (kein PicoGK, kein Xvfb) und verbindet Gmsh2DMesher mit 2D-SU2.
    ///
    /// <para>
    /// <b>Der Name <c>MeinProjekt2D</c> ist ein Platzhalter.</b>
    /// <c>scripts\new-project.ps1 &lt;Name&gt; -Template 2D</c> kopiert diesen Ordner und ersetzt das Wort
    /// überall — in Dateinamen, Klassennamen, Namensraum und in den Kommentarköpfen der JSONs.
    /// </para>
    ///
    /// <para>
    /// <b>Warum die Vorlage selbst nicht startbar ist:</b> <c>src/projects/_Vorlage2D/</c> ist aus
    /// dem Build des Programms ausgeschlossen (siehe <c>Automatisierung_v2.csproj</c>).
    /// Compiliert wird der Ordner trotzdem — vom Testprojekt, damit Schnittstellenänderungen sofort auffallen.
    /// </para>
    /// </summary>
    public sealed class MeinProjekt2DProject : Cfd2DProjectDefinition
    {
        /// <summary>
        /// Muss <b>exakt</b> dem Ordnernamen unter <c>src/projects/</c> entsprechen.
        /// </summary>
        public override string Name => "MeinProjekt2D";

        /// <summary>
        /// Schicht 1 der Konfiguration: die Code-Standards dieses 2D-Projekts.
        /// <c>project.json</c> nebenan überlagert sie — was dort fehlt, gilt von hier.
        /// </summary>
        public override ProjectConfig CreateDefaults() => new ProjectConfig
        {
            // Startwerte der aktiven Parameter (mm).
            BaseParameters = new Dictionary<string, float>
            {
                { "Chord", 50.0f },
                { "Thickness", 6.0f }
            },

            // Anfängliche Mutationsstärke je Parameter:
            MaxDeviations = new Dictionary<string, float>
            {
                { "Chord", 5.0f },
                { "Thickness", 0.6f }
            },

            // Harte Grenzen für physikalische Plausibilität:
            ParameterBounds = new Dictionary<string, (float Min, float Max)>
            {
                { "Chord", (20.0f, 100.0f) },
                { "Thickness", (2.0f, 25.0f) }
            },

            // Leer, da die Standard-Fitness nur den Widerstand minimiert:
            OptimizationTargets = new Dictionary<string, float>(),

            // Längenparameter in mm:
            DimensionalParameters = new HashSet<string> { "Chord", "Thickness" }
        };

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MeinProjekt2DGeometryGenerator();

        /// <inheritdoc/>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MeinProjekt2DFitnessCalculator(context.Project);
    }
}
