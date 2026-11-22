using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Combined tolerance for metric comparisons (TODO-W1): relative fraction <b>plus</b> fixed
    /// absolute floor, analogous to <c>numpy.isclose</c>.
    ///
    /// <para>
    /// A value <c>q</c> is considered within tolerance of reference <c>q_ref</c> when
    /// <c>|q - q_ref| &lt;= rtol * |q_ref| + atol</c>. For large values, the relative deviation
    /// governs; for values near 0 (e.g., lift of a symmetric airfoil at 0 deg), the fixed
    /// absolute floor governs. Previously calibration reported "1,727 %" deviation for
    /// CL = 0.0001 vs -0.0005 and failed even though both values are practically zero.
    /// </para>
    ///
    /// <para>
    /// Reported is the <b>normalized deviation</b> <c>|Δ| / (rtol * |q_ref| + atol)</c>:
    /// at most 1 means within tolerance; 2 means twice as far off as allowed.
    /// </para>
    /// </summary>
    public static class MetricTolerance
    {
        /// <summary>
        /// Default absolute floors per metric (dimensionless coefficients):
        /// lift and side force 0.005 (matching the NASA TMR criterion at alpha = 0 deg),
        /// drag 0.0001 (one drag count, typical resolution limit), moments 0.001.
        /// Unknown metrics: 0 (purely relative, as before).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, float> DefaultAbsolute =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Drag"] = 1e-4f,
                ["Lift"] = 5e-3f,
                ["CSF"] = 5e-3f,
                ["CMx"] = 1e-3f,
                ["CMy"] = 1e-3f,
                ["CMz"] = 1e-3f,
            };

        /// <summary>
        /// Absolute floor for <paramref name="metric"/>: entry from <paramref name="overrides"/>
        /// (<c>MeshCalibration.AbsoluteTolerances</c>), otherwise <see cref="DefaultAbsolute"/>, otherwise 0.
        /// </summary>
        public static float AbsoluteFor(string metric, IReadOnlyDictionary<string, float>? overrides = null)
        {
            if (overrides != null)
            {
                foreach (var entry in overrides)
                {
                    if (string.Equals(entry.Key, metric, StringComparison.OrdinalIgnoreCase))
                        return Math.Max(0f, entry.Value);
                }
            }

            return DefaultAbsolute.TryGetValue(metric, out float value) ? value : 0f;
        }

        /// <summary>Classic relative deviation <c>|q - q_ref| / |q_ref|</c> (for reports).</summary>
        public static float Relative(float value, float reference)
            => Math.Abs(value - reference) / Math.Max(Math.Abs(reference), 1e-7f);

        /// <summary>
        /// Normalized deviation <c>|q - q_ref| / (rtol * |q_ref| + atol)</c>; at most 1 = within tolerance.
        /// If both rtol and atol are 0, any deviation other than exact equality is infinite.
        /// </summary>
        public static float Normalized(float value, float reference, float relativeTolerance, float absoluteTolerance)
        {
            float allowed = Math.Max(relativeTolerance, 0f) * Math.Abs(reference) + Math.Max(absoluteTolerance, 0f);
            float delta = Math.Abs(value - reference);
            if (allowed <= 0f) return delta <= 0f ? 0f : float.PositiveInfinity;
            return delta / allowed;
        }

        /// <summary>
        /// Relative tolerance accounting for oscillation in both compared evaluations
        /// (<see cref="MetricUncertainty"/>): at least twice the combined standard uncertainty.
        /// For cleanly converged runs (uncertainty near 0), the configured tolerance remains unchanged;
        /// under vortex shedding, pure noise is prevented from registering as mesh dependence or instability.
        /// </summary>
        public static float WithUncertainty(float tolerance, double uncertaintyA, double uncertaintyB)
            => Math.Max(tolerance, (float)(2.0 * Math.Sqrt(uncertaintyA * uncertaintyA + uncertaintyB * uncertaintyB)));

        /// <summary>True when <paramref name="value"/> lies within the combined tolerance around <paramref name="reference"/>.</summary>
        public static bool Within(float value, float reference, float relativeTolerance, float absoluteTolerance)
            => Normalized(value, reference, relativeTolerance, absoluteTolerance) <= 1.0f;
    }
}
