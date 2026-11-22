using System;

namespace FluidForge.Core
{
    /// <summary>
    /// TuRBO-style trust region state: in high dimensions, the Bayesian optimizer searches
    /// within an axis-aligned box around the best point rather than the full space. The box
    /// expands on consecutive successes and shrinks on consecutive failures; once below the
    /// minimum size, search restarts at a new location.
    /// </summary>
    public sealed class TrustRegionState
    {
        /// <summary>Minimum number of free parameters to activate trust region in "Auto" mode.</summary>
        public const int AutoThreshold = 10;

        public const double InitialLength = 0.8;
        public const double MaximumLength = 1.6;
        /// <summary>0.5⁷: after seven halvings, the trust region is exhausted.</summary>
        public static readonly double MinimumLength = Math.Pow(0.5, 7);

        /// <summary>Consecutive successes required to double the side length.</summary>
        public const int SuccessTolerance = 3;

        private int _successes;
        private int _failures;

        /// <param name="freeDimensions">Number of free parameters.</param>
        /// <param name="variantsPerIteration">Variants per iteration (V).</param>
        public TrustRegionState(int freeDimensions, int variantsPerIteration)
        {
            int v = Math.Max(1, variantsPerIteration);
            FailureTolerance = (int)Math.Ceiling(Math.Max(4.0 / v, freeDimensions / (double)v));
        }

        /// <summary>Current side length L (in unit-cube coordinates).</summary>
        public double Length { get; private set; } = InitialLength;

        /// <summary>Consecutive failures required to halve L: ⌈max(4/V, d/V)⌉.</summary>
        public int FailureTolerance { get; }

        public int Successes => _successes;
        public int Failures => _failures;

        /// <summary>Whether the region has shrunk enough to trigger a restart elsewhere.</summary>
        public bool NeedsRestart => Length < MinimumLength;

        /// <summary>Active in "Auto" mode starting at <see cref="AutoThreshold"/> free parameters, or via "On"/"Off".</summary>
        public static bool IsActive(string? setting, int freeDimensions)
        {
            switch ((setting ?? "Auto").Trim().ToLowerInvariant())
            {
                case "on": return true;
                case "off": return false;
                default: return freeDimensions >= AutoThreshold;
            }
        }

        /// <summary>
        /// Records the outcome of a round. Three consecutive successes double L (up to
        /// <see cref="MaximumLength"/>); <see cref="FailureTolerance"/> consecutive failures halve it.
        /// </summary>
        public void Register(bool success)
        {
            if (success)
            {
                _successes++;
                _failures = 0;
                if (_successes >= SuccessTolerance)
                {
                    Length = Math.Min(Length * 2.0, MaximumLength);
                    _successes = 0;
                }
            }
            else
            {
                _failures++;
                _successes = 0;
                if (_failures >= FailureTolerance)
                {
                    Length /= 2.0;
                    _failures = 0;
                }
            }
        }

        /// <summary>Restarts at a new location: resets L to initial length and clears counters.</summary>
        public void Restart()
        {
            Length = InitialLength;
            _successes = 0;
            _failures = 0;
        }
    }
}
