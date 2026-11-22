using System;
using System.Collections.Generic;
using System.Globalization;

namespace FluidForge.Core
{
    /// <summary>Result of an automatic stop check.</summary>
    /// <param name="Stop">Whether the run should terminate after this iteration.</param>
    /// <param name="Improvement">Measured relative improvement over the window; <c>null</c> if not yet determinable.</param>
    /// <param name="Message">Human-readable message for the log.</param>
    public sealed record AutoStopDecision(bool Stop, double? Improvement, string Message);

    /// <summary>
    /// Decides whether optimization should stop automatically (<see cref="AutoStopConfig"/>, 09.11.2026).
    /// Pure function over the history so it can be unit-tested without the pipeline and stops at the
    /// exact same point on resume (same history from the journal).
    ///
    /// <para>
    /// Rule: <c>before</c> = best fitness among all variants prior to the last <c>WindowVariants</c>
    /// variants, <c>now</c> = best fitness among all variants. If <c>(now - before) / |before|</c> is
    /// smaller than <c>MinImprovement</c>, the run stops. Failed variants count toward the window
    /// length, but do not contribute a best fitness value.
    /// </para>
    /// </summary>
    public static class AutoStop
    {
        /// <summary>Below this magnitude, the best value is treated as 0 and relative improvement cannot be computed.</summary>
        private const double ZeroThreshold = 1e-9;

        /// <summary>
        /// Checks whether automatic stopping is enabled. Validates all settings upfront and fails
        /// with a configuration error before the first evaluation rather than after hours of runtime.
        /// </summary>
        public static bool IsEnabled(AutoStopConfig? config)
        {
            if (config == null) return false;

            bool enabled;
            switch ((config.Mode ?? "Off").Trim().ToLowerInvariant())
            {
                case "on": enabled = true; break;
                case "off": enabled = false; break;
                default:
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} AutoStop.Mode = \"{config.Mode}\" is unknown. Allowed values: \"On\" or \"Off\".");
            }

            if (!enabled) return false;

            if (float.IsNaN(config.MinImprovement) || config.MinImprovement < 0f)
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} AutoStop.MinImprovement must not be negative (0.01 = 1 %).");
            if (config.WindowVariants < 1)
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} AutoStop.WindowVariants must be at least 1.");

            return true;
        }

        /// <summary>Evaluates whether the run should stop given the current optimization history.</summary>
        public static AutoStopDecision Evaluate(IReadOnlyList<ModelRecord> history, AutoStopConfig config)
        {
            int window = Math.Max(1, config.WindowVariants);
            int splitIndex = history.Count - window;
            string threshold = Percent(config.MinImprovement);

            if (splitIndex < 1)
                return new AutoStopDecision(false, null,
                    $"too few variants yet ({history.Count}, measurement starts at {window + 1}).");

            double? before = BestFitness(history, 0, splitIndex);
            double? now = BestFitness(history, 0, history.Count);

            if (before == null || now == null)
                return new AutoStopDecision(false, null,
                    "no valid variant before the measurement window yet.");

            if (Math.Abs(before.Value) < ZeroThreshold)
                return new AutoStopDecision(false, null,
                    "best fitness before the measurement window is 0; percentage improvement cannot be determined.");

            double improvement = (now.Value - before.Value) / Math.Abs(before.Value);
            string measured = $"Best fitness improvement over the last {window} variants: {Percent(improvement)} (threshold {threshold})";

            return improvement < config.MinImprovement
                ? new AutoStopDecision(true, improvement, measured + " - stopping optimization.")
                : new AutoStopDecision(false, improvement, measured + " - continuing.");
        }

        private static double? BestFitness(IReadOnlyList<ModelRecord> history, int from, int to)
        {
            double? best = null;
            for (int i = from; i < to; i++)
            {
                ModelRecord record = history[i];
                if (record.SimulationFailed) continue;
                if (best == null || record.Fitness > best.Value) best = record.Fitness;
            }
            return best;
        }

        private static string Percent(double fraction)
            => (fraction * 100.0).ToString("0.##", CultureInfo.InvariantCulture) + " %";
    }
}
