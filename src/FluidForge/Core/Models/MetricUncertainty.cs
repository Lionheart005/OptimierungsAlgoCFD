using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Convention for metric uncertainty. Alongside <c>Drag</c>, a solver may write
    /// <c>Uncertainty_Drag</c> into <see cref="ModelRecord.PassiveParameters"/>: the
    /// <b>relative</b> standard uncertainty (1σ) of the value, e.g., 0.02 for ±2%.
    /// In SU2 this comes from block averages over the trailing window; a smoothly converged run
    /// has near-zero uncertainty, while vortex shedding yields higher values.
    ///
    /// <para>
    /// The core is solver-agnostic and only reads these keys. Bayesian optimization uses them
    /// as per-point observation noise so noisy evaluations pull the surrogate less strongly.
    /// If absent, uncertainty is treated as unknown and the model estimates a shared noise level.
    /// </para>
    /// </summary>
    public static class MetricUncertainty
    {
        public const string Prefix = "Uncertainty_";

        /// <summary>Upper bound so a single outlier cannot dominate the surrogate (±100%).</summary>
        public const double MaximumRelative = 1.0;

        public static string KeyFor(string metric) => Prefix + metric;

        /// <summary>Returns the relative uncertainty of a metric if present, finite, and non-negative.</summary>
        public static bool TryGet(ModelRecord record, string metric, out double relative)
        {
            relative = 0;
            if (!record.PassiveParameters.TryGetValue(KeyFor(metric), out float value) || !float.IsFinite(value) || value < 0)
                return false;
            relative = Math.Min(value, MaximumRelative);
            return true;
        }

        /// <summary>
        /// Approximate relative uncertainty of fitness: root-sum-of-squares of the relative
        /// uncertainties of the specified metrics (first-order approximation for products/quotients).
        /// If no metric names are given, all present uncertainties in the record are combined.
        /// </summary>
        public static bool TryGetFitness(ModelRecord record, IReadOnlyCollection<string> metrics, out double relative)
        {
            double sum = 0;
            bool any = false;

            if (metrics.Count > 0)
            {
                foreach (string metric in metrics)
                {
                    if (TryGet(record, metric, out double u)) { sum += u * u; any = true; }
                }
            }
            else
            {
                foreach (var (key, value) in record.PassiveParameters)
                {
                    if (!key.StartsWith(Prefix, StringComparison.Ordinal) || !float.IsFinite(value) || value < 0) continue;
                    double u = Math.Min(value, MaximumRelative);
                    sum += u * u;
                    any = true;
                }
            }

            relative = Math.Min(Math.Sqrt(sum), MaximumRelative);
            return any;
        }
    }
}
