namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Ein Projekt verdrahtet sich selbst (TODO-23).
    ///
    /// <para>
    /// Vorher stand in <c>Program.cs</c> ein <c>switch</c> über den Projektnamen, der Geometrie,
    /// Kernel und Fitness fest verband. Solange der existierte, war ein neues Projekt eben
    /// <b>nicht</b> nur ein Ordner — man musste den Kern anfassen. Jetzt bringt jedes Projekt
    /// eine Implementierung dieser Schnittstelle in seinem eigenen Ordner mit, und die
    /// <see cref="ProjectRegistry"/> findet sie beim Start von selbst.
    /// </para>
    ///
    /// <para>
    /// Gefunden wird per Reflection und nicht über einen Typnamen in einer JSON-Datei
    /// (Entscheidung 5): ein <c>Type.GetType("…MantaGeometryGenerator")</c> in einer JSON ist
    /// ein Tippfehler, der erst im Lauf auffällt — eine Klasse ist compilergeprüft.
    /// </para>
    ///
    /// <para>
    /// Der Normalfall — PicoGK, Gmsh, SU2 — steht fertig in <c>CfdProjectDefinition</c>;
    /// ein Projekt muss dann nur noch <see cref="Name"/>, <see cref="CreateDefaults"/>,
    /// <see cref="CreateGeometry"/> und <see cref="CreateFitness"/> ausfüllen.
    /// </para>
    /// </summary>
    public interface IProjectDefinition
    {
        /// <summary>
        /// Muss exakt dem Ordnernamen unter <c>src/projects/</c> entsprechen — der Ordnername ist
        /// der Projektname (Entscheidung 1). Die <see cref="ProjectRegistry"/> prüft beide
        /// Richtungen beim Start, damit Ordner und Klasse nicht auseinanderlaufen.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Die Code-Vorgaben des Projekts: Startwerte, Grenzen, Optimierungsziele.
        /// Die <c>project.json</c> im Projektordner überlagert davon, was sie selbst nennt.
        /// </summary>
        ProjectConfig CreateDefaults();

        /// <summary>
        /// Die Laufzeitumgebung, in der die Geometrie entsteht. Vorgabe im Normalfall
        /// <c>PicoGkKernel</c>; ein Projekt ohne Voxel-Modell nimmt
        /// <see cref="DirectGeometryKernel"/>.
        /// </summary>
        IGeometryKernel CreateKernel();

        /// <summary>Der Geometrie-Erzeuger des Projekts.</summary>
        IGeometryGenerator CreateGeometry(ProjectContext context);

        /// <summary>Die Zielfunktion des Projekts.</summary>
        IFitnessCalculator CreateFitness(ProjectContext context);

        /// <summary>
        /// Die Kette aus Vernetzer und Solver. Stufen, die sich eine Mesher-<b>Instanz</b>
        /// teilen, vernetzen nur einmal (TODO-4) — wer das will, gibt hier bewusst dieselbe
        /// Instanz in mehrere Stufen.
        /// </summary>
        SolverStage[] CreateStages(ProjectContext context);
    }
}
