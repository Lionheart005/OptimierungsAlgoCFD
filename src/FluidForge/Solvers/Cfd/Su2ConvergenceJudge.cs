using System;
using System.Collections.Generic;

using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>Verdict on the iterative error of an SU2 simulation.</summary>
    /// <param name="Assessed">
    /// False if the tail window was too short to assess (fewer than
    /// <see cref="Su2ConvergenceJudge.MinTailRows"/> rows). In that case the fallback rule
    /// "SU2 stopped before ITER" applies.
    /// </param>
    /// <param name="Converged">All metrics have a normalized iterative error of at most 1.</param>
    /// <param name="Normalized">Normalized iterative error per metric name (&lt;= 1 = acceptable).</param>
    /// <param name="WorstMetric">The metric with the largest normalized error.</param>
    public sealed record Su2ConvergenceVerdict(
        bool Assessed,
        bool Converged,
        IReadOnlyDictionary<string, float> Normalized,
        string? WorstMetric,
        float WorstNormalized);

    /// <summary>
    /// Judges whether an SU2 run has converged based on the tail window of <c>history.csv</c>.
    ///
    /// <para>
    /// Measures the <b>drift</b> of each metric in the tail window (difference between first-half and
    /// second-half means), normalized by the combined tolerance from <see cref="MetricTolerance"/>:
    /// <c>drift / (rtol * |mean| + atol)</c>, &lt;= 1 = acceptable.
    /// <c>rtol</c> and <c>atol</c> are a fraction of the mesh calibration tolerance so that iterative
    /// error remains well below discretization error.
    /// </para>
    /// </summary>
    public static class Su2ConvergenceJudge
    {
        /// <summary>Shortest tail window that can still be assessed (rows).</summary>
        public const int MinTailRows = 20;

        /// <param name="history">Evaluated history.csv.</param>
        /// <param name="metricColumns">Metric name (e.g. "Drag") -> history column (e.g. "CD").</param>
        /// <param name="relativeTolerance">Allowed relative drift (fraction of mesh tolerance).</param>
        /// <param name="absoluteScale">Fraction scaling the metric's <c>atol</c> defaults.</param>
        /// <param name="absoluteOverrides"><c>MeshCalibration.AbsoluteTolerances</c> (may be empty).</param>
        public static Su2ConvergenceVerdict Judge(
            Su2HistoryResult history,
            IReadOnlyDictionary<string, string> metricColumns,
            float relativeTolerance,
            float absoluteScale,
            IReadOnlyDictionary<string, float>? absoluteOverrides)
        {
            var normalized = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            if (history.TailRows < MinTailRows)
                return new Su2ConvergenceVerdict(false, false, normalized, null, 0f);

            string? worst = null;
            float worstValue = 0f;

            foreach (KeyValuePair<string, string> entry in metricColumns)
            {
                string metric = (entry.Key ?? string.Empty).Trim();
                string column = (entry.Value ?? string.Empty).Trim();
                if (metric.Length == 0 || column.Length == 0) continue;

                if (!history.WindowDrift.TryGetValue(column, out float drift)
                    || !history.TailMeans.TryGetValue(column, out float mean))
                    continue;

                float rtol = Math.Max(relativeTolerance, 0f);
                float atol = MetricTolerance.AbsoluteFor(metric, absoluteOverrides) * Math.Max(absoluteScale, 0f);

                // Mean as reference: drift is measured relative to the magnitude of the metric.
                float value = MetricTolerance.Normalized(mean + drift, mean, rtol, atol);
                normalized[metric] = value;

                if (worst == null || value > worstValue)
                {
                    worst = metric;
                    worstValue = value;
                }
            }

            if (normalized.Count == 0)
                return new Su2ConvergenceVerdict(false, false, normalized, null, 0f);

            return new Su2ConvergenceVerdict(true, worstValue <= 1.0f, normalized, worst, worstValue);
        }
    }
}
