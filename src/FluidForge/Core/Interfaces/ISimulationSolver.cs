namespace FluidForge.Core
{
    /// <summary>
    /// Runs a simulation (CFD, FEM, etc.) on a mesh.
    /// Results are written directly into <c>record.PassiveParameters</c>.
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
