namespace FluidForge.Core
{
    /// <summary>
    /// Optional extension to <see cref="IOptimizationAlgorithm"/>: algorithms with prerequisites
    /// (e.g., complete parameter bounds) validate them <b>before</b> the first expensive
    /// evaluation or mesh calibration (fail-fast).
    /// </summary>
    public interface IOptimizationPreflight
    {
        /// <summary>
        /// Validates prerequisites.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">Prerequisite not met; message states what is missing and where to configure it.</exception>
        void Preflight(SimulationContext context);
    }
}
