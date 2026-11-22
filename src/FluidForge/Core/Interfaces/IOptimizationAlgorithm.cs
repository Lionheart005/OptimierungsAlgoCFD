using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Interface for optimization algorithms.
    /// Operates on <see cref="SimulationContext"/>, decoupled from project-specific parameters.
    /// <see cref="IFitnessCalculator"/> is injected via the constructor.
    /// </summary>
    public interface IOptimizationAlgorithm
    {
        /// <summary>
        /// Name of the algorithm for console output.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Generates the parameter set for the current variant.
        /// </summary>
        Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants);

        /// <summary>
        /// Evaluates a completed iteration and returns the best candidate.
        /// </summary>
        ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context);

        /// <summary>
        /// Prepares the algorithm for the next iteration (e.g., updating operating point or surrogate models).
        /// </summary>
        void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord);
    }
}
