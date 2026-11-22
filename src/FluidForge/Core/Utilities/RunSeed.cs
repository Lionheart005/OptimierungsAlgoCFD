using System;

namespace FluidForge.Core
{
    /// <summary>
    /// Random seed for a run. All random streams derive from this seed so runs are
    /// reproducible and replay-based resume regenerates identical parameters.
    /// </summary>
    public static class RunSeed
    {
        /// <summary>
        /// Returns the configured seed, or a freshly drawn non-zero seed when <c>0</c>.
        /// </summary>
        public static int Resolve(int configured)
            => configured != 0 ? configured : Random.Shared.Next(1, int.MaxValue);

        /// <summary>Random stream for the optimization algorithm.</summary>
        public static Random ForOptimizer(int seed) => new Random(seed);

        /// <summary>
        /// Random stream for the bouncer validator. Intentionally separate from the optimizer
        /// stream so bouncer jitter and subsequent candidates remain independent.
        /// </summary>
        public static Random ForValidator(int seed) => new Random(unchecked(seed + 1));

        /// <summary>Log line describing the resolved seed for reproducibility.</summary>
        public static string Describe(int seed, int configured)
            => configured != 0
                ? $"{OutputConventions.SystemTag} Random seed: {seed} (from simulation.json \"RandomSeed\")"
                : $"{OutputConventions.SystemTag} Random seed: {seed} (auto-selected; set \"RandomSeed\": {seed} in simulation.json to replay)";
    }
}
