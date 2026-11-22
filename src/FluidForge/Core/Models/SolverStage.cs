namespace FluidForge.Core
{
    /// <summary>
    /// One stage of the simulation chain: a solver paired with the mesher that generates its mesh.
    ///
    /// Different solvers require different meshes (CFD: fluid domain around the model;
    /// FEM: solid mesh of the model itself). Stages that share the same mesher instance
    /// mesh only once.
    /// </summary>
    public record SolverStage(IMeshGenerator Mesher, ISimulationSolver Solver);
}
