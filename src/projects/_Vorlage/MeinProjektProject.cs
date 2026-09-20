using System.Collections.Generic;

using MyPicoGkProject.Composition;
using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.MeinProjekt
{
    /// <summary>
    /// Die Verdrahtung eines Projekts (TODO-27). <b>Diese Klasse ist alles, was das Framework
    /// von einem Projekt wissen muss</b> — es gibt keinen <c>switch</c> in <c>Program.cs</c>,
    /// keine Liste und keinen Eintrag in einer JSON: die
    /// <see cref="ProjectRegistry"/> findet sie per Reflection (Entscheidung 5).
    ///
    /// <para>
    /// <b>Der Name <c>MeinProjekt</c> ist ein Platzhalter.</b>
    /// <c>scripts\new-project.ps1 &lt;Name&gt;</c> kopiert diesen Ordner und ersetzt das Wort
    /// überall — in Dateinamen, Klassennamen, Namensraum und in den Kommentarköpfen der JSONs.
    /// Von Hand kopieren geht genauso, dann ist das Ersetzen dein Job.
    /// </para>
    ///
    /// <para>
    /// <b>Warum die Vorlage selbst nicht startbar ist:</b> <c>src/projects/_Vorlage/</c> ist aus
    /// dem Build des Programms ausgeschlossen (siehe <c>Automatisierung_v2.csproj</c>), damit hier
    /// keine zweite Projektdefinition mit einem Namen auftaucht, zu dem es keinen Ordner gibt.
    /// Compiliert wird der Ordner trotzdem — vom Testprojekt. Änderte sich eine Schnittstelle des
    /// Frameworks, wäre die Vorlage sonst irgendwann unbrauchbar, und gemerkt hätte man es erst
    /// beim Anlegen des nächsten Projekts.
    /// </para>
    /// </summary>
    public sealed class MeinProjektProject : CfdProjectDefinition
    {
        /// <summary>
        /// Muss <b>exakt</b> dem Ordnernamen unter <c>src/projects/</c> entsprechen — der
        /// Ordnername ist der Projektname (Entscheidung 1). Die
        /// <see cref="ProjectRegistry"/> prüft beide Richtungen beim Start, ein Auseinanderlaufen
        /// fällt also sofort auf und nicht erst im Lauf.
        /// </summary>
        public override string Name => "MeinProjekt";

        /// <summary>
        /// Schicht 1 der Konfiguration: die Code-Standards dieses Projekts.
        /// <c>project.json</c> nebenan überlagert sie — was dort fehlt, gilt von hier.
        ///
        /// <para>
        /// Beide Stellen führen dieselben Zahlen, und das ist Absicht: die JSON ist der Ort, an
        /// dem du sie änderst, der Code der Ort, der sie auch ohne Datei kennt. Ein Test hält die
        /// beiden zusammen. MantaAuv legt dieselbe Methode in eine eigene Datei
        /// (<c>MantaProjectConfig.cs</c>) — beides ist in Ordnung.
        /// </para>
        ///
        /// <para>
        /// <c>ProjectName</c> wird hier <b>nicht</b> gesetzt: <c>Program.cs</c> trägt den
        /// Ordnernamen ein. Eine zweite Stelle mit dem Namen wäre eine Stelle zu viel.
        /// </para>
        /// </summary>
        public override ProjectConfig CreateDefaults() => new ProjectConfig
        {
            // Startwerte der aktiven Parameter (mm). Diese Namen liest der Geometrie-Generator
            // wieder heraus — sie sind der Vertrag zwischen Konfiguration und Geometrie.
            BaseParameters = new Dictionary<string, float>
            {
                { "Length", 50.0f },
                { "Width", 30.0f },
                { "Height", 20.0f }
            },

            // Anfängliche Mutationsstärke je Parameter.
            MaxDeviations = new Dictionary<string, float>
            {
                { "Length", 5.0f },
                { "Width", 3.0f },
                { "Height", 2.0f }
            },

            // Harte Grenzen. Der Algorithmus verlässt sie nie — das ist die einzige Stelle, die
            // eine Variante mit 0 mm Höhe verhindert.
            ParameterBounds = new Dictionary<string, (float Min, float Max)>
            {
                { "Length", (20.0f, 100.0f) },
                { "Width", (10.0f, 80.0f) },
                { "Height", (5.0f, 50.0f) }
            },

            // Leer, weil die Fitness dieser Vorlage nur den Widerstand minimiert. Kommt ein Ziel
            // dazu, gehört sein Name zusätzlich in MeinProjektFitnessCalculator.RequiredTargets —
            // dann prüft der Konstruktor es beim Verdrahten und nicht der Lauf nach Stunden.
            OptimizationTargets = new Dictionary<string, float>(),

            // Nur Längen gehören hier hinein: der RubberBandScaler rechnet sie auf die
            // PicoGK-Arbeitsgröße herunter und zurück. Ein dimensionsloser Parameter (Verhältnis,
            // Winkel, Zählwert) würde dabei verfälscht — MantaAuv lässt "TailTaper" deshalb weg.
            DimensionalParameters = new HashSet<string> { "Length", "Width", "Height" }
        };

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new MeinProjektGeometryGenerator();

        /// <summary>
        /// Der Rechner prüft im Konstruktor, ob seine Pflicht-Ziele vorliegen (TODO-16) — der
        /// Lauf bricht dadurch hier ab, vor der ersten Simulation.
        /// </summary>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new MeinProjektFitnessCalculator(context.Project);

        // ------------------------------------------------------------------
        // Was NICHT hier steht, kommt aus CfdProjectDefinition: PicoGK als
        // Geometrie-Kernel und eine Stufe Gmsh -> SU2, deren Zahlen aus
        // solvers/gmsh.json und solvers/su2.json in diesem Ordner kommen.
        //
        // Braucht dein Projekt etwas anderes, überschreibe es HIER — nicht im
        // Framework:
        //
        //   // Geometrie ohne Voxel-Modell: kein PicoGK, kein Xvfb auf dem Server.
        //   public override IGeometryKernel CreateKernel() => new DirectGeometryKernel();
        //
        //   // Eigene Solver-Kette, z.B. zwei Stufen oder ein anderer Vernetzer.
        //   public override SolverStage[] CreateStages(ProjectContext c) => new[]
        //   {
        //       new SolverStage(
        //           new GmshCfdMesher(c.LoadSolverOptions<GmshMesherOptions>("gmsh")),
        //           new Su2Solver(c.LoadSolverOptions<Su2SolverOptions>("su2")))
        //   };
        //
        // LoadSolverOptions<T>("name") liest src/projects/<Name>/solvers/name.json über alle
        // Konfigurationsschichten — ein neuer Solver bringt also seine eigene JSON mit, ohne
        // dass der Kern davon wissen muss.
        // ------------------------------------------------------------------
    }
}
