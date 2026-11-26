using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using FluidForge.Core;

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
        string Summary,
        bool IsUnsteadyStationary = false,
        bool HasMonotonicDrift = false,
        bool SolverStoppedEarly = false,
        IReadOnlyDictionary<string, float>? MetricUncertainties = null,
        IReadOnlyList<string>? DriftingMetrics = null);

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

            if (ContainsFatalOrCrashLine(log))
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

            // Mass balance gate (hard gate for Internal3D; non-finite values always fail)
            bool isExternal = string.Equals(options.DomainMode?.Trim(), "External3D", StringComparison.OrdinalIgnoreCase);
            float mbErr = postResult.MassBalanceErrorPercent ?? 0f;
            bool massBalanceOk = isExternal ||
                                 !postResult.MassBalanceErrorPercent.HasValue ||
                                 (float.IsFinite(mbErr) && mbErr <= options.MaxMassBalanceErrorPercent);
            if (postResult.MassBalanceErrorPercent.HasValue && !float.IsFinite(mbErr))
            {
                massBalanceOk = false;
                divergedOrNaN = true;
            }

            // Filter monitored series by options.ResultMetrics (fallback to all series when empty)
            var metricsToJudge = new List<(string TargetKey, string SourceKey)>();
            bool hasConfiguredMetrics = options.ResultMetrics != null && options.ResultMetrics.Count > 0;
            if (hasConfiguredMetrics)
            {
                foreach (KeyValuePair<string, string> kvp in options.ResultMetrics!)
                {
                    string targetKey = (kvp.Key ?? string.Empty).Trim();
                    string sourceKey = string.IsNullOrWhiteSpace(kvp.Value) ? targetKey : kvp.Value.Trim();
                    if (targetKey.Length > 0)
                        metricsToJudge.Add((targetKey, sourceKey));
                }
                if (metricsToJudge.Count == 0)
                    hasConfiguredMetrics = false;
            }
            if (!hasConfiguredMetrics)
            {
                foreach (string key in postResult.MonitoredSeries.Keys)
                {
                    if (isExternal && string.Equals(key, "PressureDrop", StringComparison.OrdinalIgnoreCase))
                        continue;
                    if (!isExternal && (string.Equals(key, "Drag", StringComparison.OrdinalIgnoreCase) ||
                                        string.Equals(key, "Lift", StringComparison.OrdinalIgnoreCase)))
                        continue;
                    metricsToJudge.Add((key, key));
                }
            }

            // Trailing-window drift check across monitored series (MetricTolerance + BatchStatistics)
            float rtol = options.RelativeDriftTolerance > 0f ? options.RelativeDriftTolerance : 0.005f;
            float worstDrift = 0f;
            bool driftOk = true;
            bool hasMonotonicDrift = false;
            bool anyUnsteadyStationary = false;
            int matchedSeriesCount = 0;
            int window = Math.Max(4, options.TrailingWindowIterations);
            var metricUncertainties = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var driftingMetrics = new List<string>();

            foreach ((string targetKey, string sourceKey) in metricsToJudge)
            {
                if (!TryGetSeries(postResult.MonitoredSeries, sourceKey, out IReadOnlyList<float>? values) || values == null)
                    continue;

                matchedSeriesCount++;
                if (values.Count < 4) continue;

                // Use at most the final third of iterations (when N < 3*window) so early
                // startup transients do not trigger false drift on fast-converging runs.
                int count = OpenFoamPostProcessor.ResolveEffectiveTailCount(values.Count, window);
                if (count < 4) continue;
                int start = values.Count - count;

                var tail = new List<float>(count);
                bool nonFiniteInTail = false;
                for (int i = start; i < values.Count; i++)
                {
                    float v = values[i];
                    if (!float.IsFinite(v))
                    {
                        nonFiniteInTail = true;
                        break;
                    }
                    tail.Add(v);
                }

                if (nonFiniteInTail)
                {
                    divergedOrNaN = true;
                    driftOk = false;
                    continue;
                }

                int half = tail.Count / 2;
                double firstSum = 0.0;
                for (int i = 0; i < half; i++)
                    firstSum += tail[i];

                double secondSum = 0.0;
                for (int i = tail.Count - half; i < tail.Count; i++)
                    secondSum += tail[i];

                double totalSum = 0.0;
                for (int i = 0; i < tail.Count; i++)
                    totalSum += tail[i];

                double mean1 = firstSum / half;
                double mean2 = secondSum / half;
                double tailMean = totalSum / tail.Count;
                double absDrift = Math.Abs(mean2 - mean1);

                float atol = MetricTolerance.AbsoluteFor(targetKey, options.AbsoluteTolerances);
                if (atol == 0f && !string.Equals(sourceKey, targetKey, StringComparison.OrdinalIgnoreCase))
                    atol = MetricTolerance.AbsoluteFor(sourceKey, options.AbsoluteTolerances);

                double denom = Math.Max(rtol * Math.Abs(tailMean) + atol, 1e-12);
                double normalizedDrift = absDrift / denom;
                float effectiveDrift = (float)(normalizedDrift * rtol);

                if (!double.IsFinite(normalizedDrift) || !float.IsFinite(effectiveDrift))
                {
                    divergedOrNaN = true;
                    driftOk = false;
                    continue;
                }

                worstDrift = MathF.Max(worstDrift, effectiveDrift);

                double se = double.NaN;
                bool trendSignificant = false;
                bool hasBatchStats = tail.Count >= 16 &&
                                     ConvergenceStatistics.BatchStatistics(tail, out se, out trendSignificant);
                if (hasBatchStats && double.IsFinite(se))
                {
                    metricUncertainties[targetKey] = (float)(se / Math.Max(Math.Abs(tailMean), 1e-7));
                }

                if (normalizedDrift > 1.0)
                {
                    if (hasBatchStats && !trendSignificant)
                    {
                        anyUnsteadyStationary = true;
                    }
                    else
                    {
                        hasMonotonicDrift = true;
                        driftOk = false;
                        driftingMetrics.Add(targetKey);
                    }
                }
            }

            bool missingConfiguredSeries = hasConfiguredMetrics &&
                                           postResult.MonitoredSeries.Count > 0 &&
                                           matchedSeriesCount == 0;
            if (missingConfiguredSeries)
                driftOk = false;

            bool isUnsteadyStationary = !divergedOrNaN && !hasMonotonicDrift && anyUnsteadyStationary;

            // Residual check: when residuals were logged, allow generous factor if solver stopped via residualControl or drift is stationary.
            bool residualsOk = true;
            if (!solverConvergedEarly && finalResiduals.Count > 0)
            {
                float target = options.ResidualTarget > 0f ? options.ResidualTarget : 1e-4f;
                float maxResidual = finalResiduals.Values.Max();
                if (maxResidual > target * 10f && !driftOk && !isUnsteadyStationary)
                    residualsOk = false;
                else if (maxResidual > MathF.Max(target * 100f, 0.1f))
                    residualsOk = false;
            }

            bool converged = !divergedOrNaN && massBalanceOk && driftOk && residualsOk;

            string summary = divergedOrNaN
                ? "Solver diverged or produced non-finite values."
                : !massBalanceOk
                    ? $"Mass balance error {mbErr.ToString("0.00", CultureInfo.InvariantCulture)}% exceeds limit {options.MaxMassBalanceErrorPercent.ToString("0.00", CultureInfo.InvariantCulture)}%."
                    : missingConfiguredSeries
                        ? "None of the configured ResultMetrics series were found in postProcessing output."
                        : !driftOk
                            ? $"Trailing-window drift {(worstDrift * 100f).ToString("0.00", CultureInfo.InvariantCulture)}% exceeds limit {(rtol * 100f).ToString("0.00", CultureInfo.InvariantCulture)}%."
                            : !residualsOk
                                ? "Residuals did not reach target."
                                : isUnsteadyStationary
                                    ? "Converged (stationary oscillation, tail-window mean used)."
                                    : "Converged.";

            return new OpenFoamConvergenceVerdict(
                converged,
                divergedOrNaN,
                massBalanceOk,
                mbErr,
                worstDrift,
                iterations,
                finalResiduals,
                summary,
                IsUnsteadyStationary: isUnsteadyStationary,
                HasMonotonicDrift: hasMonotonicDrift,
                SolverStoppedEarly: solverConvergedEarly,
                MetricUncertainties: metricUncertainties,
                DriftingMetrics: driftingMetrics);
        }

        private static bool TryGetSeries(
            IReadOnlyDictionary<string, IReadOnlyList<float>> series,
            string key,
            out IReadOnlyList<float>? values)
        {
            if (series.TryGetValue(key, out values))
                return true;

            foreach (KeyValuePair<string, IReadOnlyList<float>> kvp in series)
            {
                if (string.Equals(kvp.Key, key, StringComparison.OrdinalIgnoreCase))
                {
                    values = kvp.Value;
                    return true;
                }
            }

            values = null;
            return false;
        }

        private static bool ContainsFatalOrCrashLine(string log)
        {
            if (string.IsNullOrEmpty(log))
                return false;

            foreach (string rawLine in log.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.Length == 0)
                    continue;

                // OpenFOAM startup banner prints:
                // "trapFpe: Floating point exception trapping enabled (FOAM_SIGFPE)."
                if (line.StartsWith("trapFpe:", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (line.Contains("FOAM FATAL", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Floating point exception", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("SIGFPE", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
