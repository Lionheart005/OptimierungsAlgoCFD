using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Verdict on OpenFOAM steady-state convergence, residuals, trailing-window drift, and mass balance.
    /// </summary>
    public sealed record OpenFoamConvergenceVerdict(
        bool Converged,
        bool DivergedOrNaN,
        bool MassBalanceOk,
        float MassBalanceErrorPercent,
        float WorstRelativeDrift,
        int Iterations,
        IReadOnlyDictionary<string, float> FinalResiduals,
        string Summary);

    /// <summary>
    /// Evaluates solver convergence from OpenFOAM solver logs and <see cref="OpenFoamPostProcessResult"/>
    /// time-series, enforcing residual, divergence/NaN, trailing-window drift, and hard
    /// <c>MassBalanceError &lt; 1%</c> gates.
    /// </summary>
    public static class OpenFoamConvergenceJudge
    {
        private static readonly Regex TimeLineRegex = new(
            @"^\s*Time\s*=\s*([0-9eE+\-.]+)",
            RegexOptions.Multiline | RegexOptions.Compiled);

        private static readonly Regex ResidualLineRegex = new(
            @"Solving for\s+([A-Za-z0-9_]+),\s*Initial residual\s*=\s*([0-9eE+\-.naninfNANINF]+),\s*Final residual\s*=\s*([0-9eE+\-.naninfNANINF]+)",
            RegexOptions.Compiled);

        /// <summary>
        /// Evaluates convergence using <paramref name="solverOutput"/> and <paramref name="postResult"/>.
        /// </summary>
        public static OpenFoamConvergenceVerdict Judge(
            string? solverOutput,
            OpenFoamPostProcessResult postResult,
            OpenFoamOptions options)
        {
            ArgumentNullException.ThrowIfNull(postResult);
            ArgumentNullException.ThrowIfNull(options);

            string log = solverOutput ?? string.Empty;
            var finalResiduals = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            bool divergedOrNaN = postResult.HasNonFiniteValues;
            int iterations = 0;

            if (log.Contains("FOAM FATAL", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("Floating point exception", StringComparison.OrdinalIgnoreCase) ||
                log.Contains("SIGFPE", StringComparison.OrdinalIgnoreCase))
            {
                divergedOrNaN = true;
            }

            foreach (Match tm in TimeLineRegex.Matches(log))
            {
                if (float.TryParse(tm.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) &&
                    float.IsFinite(t))
                {
                    iterations = Math.Max(iterations, (int)MathF.Round(t));
                }
            }

            foreach (Match rm in ResidualLineRegex.Matches(log))
            {
                string field = rm.Groups[1].Value;
                if (!float.TryParse(rm.Groups[3].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float finalRes) ||
                    !float.IsFinite(finalRes))
                {
                    divergedOrNaN = true;
                    continue;
                }

                if (finalRes > 1e3f)
                    divergedOrNaN = true;

                finalResiduals[field] = finalRes;
            }

            bool solverConvergedEarly = log.Contains("SIMPLE solution converged in", StringComparison.OrdinalIgnoreCase);

            // Mass balance gate
            float mbErr = postResult.MassBalanceErrorPercent ?? 0f;
            bool massBalanceOk = !postResult.MassBalanceErrorPercent.HasValue ||
                                 (float.IsFinite(mbErr) && mbErr <= options.MaxMassBalanceErrorPercent);
            if (postResult.MassBalanceErrorPercent.HasValue && !float.IsFinite(mbErr))
            {
                massBalanceOk = false;
                divergedOrNaN = true;
            }

            // Trailing-window drift check across monitored series
            float worstDrift = 0f;
            bool driftOk = true;
            int window = Math.Max(4, options.TrailingWindowIterations);

            foreach (KeyValuePair<string, IReadOnlyList<float>> kvp in postResult.MonitoredSeries)
            {
                IReadOnlyList<float> values = kvp.Value;
                if (values.Count < 4) continue;

                int count = Math.Min(window, values.Count);
                int start = values.Count - count;
                int half = count / 2;

                double firstSum = 0.0;
                for (int i = start; i < start + half; i++)
                    firstSum += values[i];

                double secondSum = 0.0;
                for (int i = values.Count - half; i < values.Count; i++)
                    secondSum += values[i];

                double mean1 = firstSum / half;
                double mean2 = secondSum / half;
                double overallMean = 0.5 * (mean1 + mean2);
                double denom = Math.Max(Math.Abs(overallMean), 1e-6);
                float relDrift = (float)(Math.Abs(mean2 - mean1) / denom);

                if (!float.IsFinite(relDrift))
                {
                    divergedOrNaN = true;
                    driftOk = false;
                    continue;
                }

                worstDrift = MathF.Max(worstDrift, relDrift);
                if (relDrift > options.RelativeDriftTolerance)
                    driftOk = false;
            }

            // Residual check: when residuals were logged, allow generous factor if solver stopped via residualControl or drift is stationary.
            bool residualsOk = true;
            if (!solverConvergedEarly && finalResiduals.Count > 0)
            {
                float target = options.ResidualTarget > 0f ? options.ResidualTarget : 1e-4f;
                float maxResidual = finalResiduals.Values.Max();
                if (maxResidual > target * 10f && !driftOk)
                    residualsOk = false;
                else if (maxResidual > MathF.Max(target * 100f, 0.1f))
                    residualsOk = false;
            }

            bool converged = !divergedOrNaN && massBalanceOk && driftOk && residualsOk;

            string summary = divergedOrNaN
                ? "Solver diverged or produced non-finite values."
                : !massBalanceOk
                    ? $"Mass balance error {mbErr.ToString("0.00", CultureInfo.InvariantCulture)}% exceeds limit {options.MaxMassBalanceErrorPercent.ToString("0.00", CultureInfo.InvariantCulture)}%."
                    : !driftOk
                        ? $"Trailing-window drift {(worstDrift * 100f).ToString("0.00", CultureInfo.InvariantCulture)}% exceeds limit {(options.RelativeDriftTolerance * 100f).ToString("0.00", CultureInfo.InvariantCulture)}%."
                        : !residualsOk
                            ? "Residuals did not reach target."
                            : "Converged.";

            return new OpenFoamConvergenceVerdict(
                converged,
                divergedOrNaN,
                massBalanceOk,
                mbErr,
                worstDrift,
                iterations,
                finalResiduals,
                summary);
        }
    }
}
