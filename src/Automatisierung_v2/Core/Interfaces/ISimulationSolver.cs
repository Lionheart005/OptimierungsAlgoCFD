namespace MyPicoGkProject
{
    /// <summary>
    /// Führt eine Simulation (CFD, FEM, etc.) auf einem Mesh aus.
    /// Ergebnisse werden direkt in record.PassiveParameters geschrieben.
    /// </summary>
    public interface ISimulationSolver
    {
        string Name { get; }

        void Solve(
            string meshPath,
            ModelRecord record,
            SimulationConfig config,
            string workingDirectory);
    }
}
