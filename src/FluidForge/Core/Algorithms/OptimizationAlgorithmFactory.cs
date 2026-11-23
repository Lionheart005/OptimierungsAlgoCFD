using System;

namespace FluidForge.Core
{
    /// <summary>
    /// Creates the optimization algorithm from the name in the framework configuration
    /// (<see cref="SimulationConfig.OptimizationAlgorithm"/>, set in
    /// <c>src/projects/&lt;Name&gt;/simulation.json</c>).
    /// </summary>
    public static class OptimizationAlgorithmFactory
    {
        /// <summary>
        /// Algorithm used when the name is missing or unknown. Defaults to <c>"Bayes"</c>.
        /// </summary>
        public const string DefaultAlgorithm = "Bayes";

        /// <summary>Valid values accepted in configuration.</summary>
        public const string SupportedAlgorithms = "Bayes, IdwBaseline, BoTorch, Pymoo, ExternalPython";

        /// <summary>
        /// Instantiates the algorithm for the given name (case-insensitive, trimmed).
        /// An empty name means "unconfigured" and silently returns <see cref="DefaultAlgorithm"/>;
        /// an unknown name also falls back to it, printing a warning to the console.
        /// </summary>
        /// <param name="random">
        /// Random source for the algorithm. At runtime <c>new Random(seed)</c> from
        /// <see cref="SimulationConfig.RandomSeed"/> so runs are reproducible and
        /// replay-based resume works. <c>null</c> = unseeded.
        /// </param>
        public static IOptimizationAlgorithm Create(string? name, IFitnessCalculator fitnessCalculator, Random? random = null)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "idwbaseline":
                case "idw":
                    return new IdwBaselineAlgorithm(fitnessCalculator, random);

                case "rsm":
                    Console.WriteLine(
                        "[INFO] 'Rsm' is a deprecated alias for 'IdwBaseline' " +
                        "(IDW interpolation, not a true RSM). Please use 'IdwBaseline' or 'Bayes'.");
                    return new IdwBaselineAlgorithm(fitnessCalculator, random);

                case "botorch":
                    return new ExternalProcessOptimizer(fitnessCalculator, algorithm: "BoTorch", random: random);

                case "pymoo":
                    return new ExternalProcessOptimizer(fitnessCalculator, algorithm: "Pymoo", random: random);

                case "externalpython":
                    return new ExternalProcessOptimizer(fitnessCalculator, algorithm: "ExternalPython", random: random);

                case "bayes":
                    return new BayesOptimizationAlgorithm(fitnessCalculator, random: random);

                case "":
                    return new BayesOptimizationAlgorithm(fitnessCalculator, random: random);

                default:
                    Console.WriteLine(
                        $"{OutputConventions.WarningTag} Unknown optimization algorithm '{name}'. " +
                        $"Valid options are: {SupportedAlgorithms}. Using '{DefaultAlgorithm}'.");
                    return new BayesOptimizationAlgorithm(fitnessCalculator, random: random);
            }
        }
    }
}
