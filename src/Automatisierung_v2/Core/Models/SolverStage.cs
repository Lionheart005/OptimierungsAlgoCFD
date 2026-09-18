namespace MyPicoGkProject
{
    /// <summary>
    /// Eine Stufe der Simulationskette: ein Solver mit dem Vernetzer, der sein Netz erzeugt.
    ///
    /// Verschiedene Solver brauchen verschiedene Netze (CFD: Fluidraum um das Modell,
    /// FEM: Solid-Netz des Modells selbst). Wer alles über Gmsh laufen lassen will,
    /// referenziert in mehreren Stufen einfach dieselbe Mesher-Instanz — der Controller
    /// vernetzt dann nur einmal.
    /// </summary>
    public record SolverStage(IMeshGenerator Mesher, ISimulationSolver Solver);
}
