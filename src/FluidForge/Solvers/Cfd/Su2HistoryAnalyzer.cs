using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Parameters for evaluating convergence history from SU2's <c>history.csv</c>.
    /// </summary>
    public record Su2HistoryOptions
    {
        /// <summary>Maximum iteration count from configuration (ITER).</summary>
        public int MaxIterations { get; init; } = 500;

        /// <summary>Window size for Cauchy convergence check (CONV_CAUCHY_ELEMS).</summary>
        public int CauchyElems { get; init; } = 150;

        /// <summary>Column names (e.g. "CD", "CL") for which oscillation and drift are computed.</summary>
        public IReadOnlyList<string> MetricColumns { get; init; } = Array.Empty<string>();

        /// <summary>Alias for <see cref="MetricColumns"/>.</summary>
        public IReadOnlyList<string> Metrics => MetricColumns;

        public Su2HistoryOptions() { }

        public Su2HistoryOptions(
            int maxIterations,
            int cauchyElems,
            IEnumerable<string>? metricColumns = null)
        {
            MaxIterations = maxIterations;
            CauchyElems = cauchyElems;
            MetricColumns = metricColumns?.ToList() ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
    }

    /// <summary>
    /// Result of evaluating an SU2 simulation's convergence history.
    /// </summary>
    public record Su2HistoryResult(
        int Iterations,
        bool Converged,
        float ResidualDrop,
        IReadOnlyDictionary<string, float> Oscillation,
        IReadOnlyDictionary<string, float> WindowMeans)
    {
        /// <summary>
        /// Length of the tail window in rows over which <see cref="TailMeans"/>, <see cref="WindowDrift"/>,
        /// and <see cref="OscillationAmplitude"/> are computed: <c>min(CauchyElems, rows / 2)</c>.
        /// Never exceeds half the history so startup transients do not skew the window.
        /// </summary>
        public int TailRows { get; init; }

        /// <summary>Mean per metric over the tail window (excluding startup transient).</summary>
        public IReadOnlyDictionary<string, float> TailMeans { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// Absolute drift per metric: difference between first-half and second-half means of the tail window.
        /// Measures iterative error: oscillation around a fixed mean yields ~0, slow drift yields a clear value.
        /// </summary>
        public IReadOnlyDictionary<string, float> WindowDrift { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// True oscillation amplitude per metric: peak-to-peak range in the tail window <b>after detrending</b>,
        /// divided by |mean|. Unlike <see cref="Su2HistoryResult.Oscillation"/>, it excludes drift and is
        /// suited for vortex-shedding warnings.
        /// </summary>
        public IReadOnlyDictionary<string, float> OscillationAmplitude { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// Relative standard uncertainty (1σ) of the tail mean per metric from batch means
        /// (<see cref="Su2HistoryAnalyzer.Batches"/> equal-length batches, detrended), divided by |mean|.
        /// Near 0 for steady runs, larger under vortex shedding. Omitted if fewer than 2 rows per batch.
        /// </summary>
        public IReadOnlyDictionary<string, float> TailUncertainty { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// Whether the tail window exhibits a <b>statistically significant</b> drift (batch-mean slope exceeds
        /// 3x its standard error). False means the metric oscillates around a stationary mean (unsteady but stable).
        /// </summary>
        public IReadOnlyDictionary<string, bool> TrendSignificant { get; init; } = new Dictionary<string, bool>();
    }

    /// <summary>
    /// Result of simulating SU2's Cauchy stopping criterion offline on a <c>history.csv</c> trace.
    /// </summary>
    public sealed record CauchyStopSimulation(
        int StopIteration,
        int TotalIterations,
        float ValueAtStop,
        float FinalValue,
        float RelativeError,
        bool TriggeredEarly);

    /// <summary>
    /// Pure analysis functions for SU2 convergence history without file or console I/O.
    /// </summary>
    public static class Su2HistoryAnalyzer
    {
        /// <summary>Number of batches for batch-mean statistics (trend test and uncertainty).</summary>
        public const int Batches = 8;

        /// <summary>
        /// Evaluates the convergence history of an SU2 simulation (<c>history.csv</c>).
        /// </summary>
        /// <param name="lines">Header line and data rows of the CSV file.</param>
        /// <param name="options">Convergence limits and column names to evaluate.</param>
        /// <returns>Computed iteration count, convergence status, residual drop, and oscillation statistics.</returns>
        public static Su2HistoryResult Evaluate(string[] lines, Su2HistoryOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            var emptyOscillation = new Dictionary<string, float>();
            var emptyWindowMeans = new Dictionary<string, float>();

            if (lines == null || lines.Length == 0)
            {
                return new Su2HistoryResult(0, false, float.NaN, emptyOscillation, emptyWindowMeans);
            }

            var validLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (validLines.Length < 2)
            {
                return new Su2HistoryResult(0, false, float.NaN, emptyOscillation, emptyWindowMeans);
            }

            string[] headers = Su2Solver.SplitCsvLine(validLines[0]);
            var dataRows = new List<string[]>(validLines.Length - 1);
            for (int i = 1; i < validLines.Length; i++)
            {
                dataRows.Add(Su2Solver.SplitCsvLine(validLines[i]));
            }

            if (dataRows.Count == 0)
            {
                return new Su2HistoryResult(0, false, float.NaN, emptyOscillation, emptyWindowMeans);
            }

            // 1. Iterations:
            // SU2 counts inner iterations from 0 (0-based). If an iteration column (Inner_Iter, ITER)
            // is present and the last row contains integer n, n + 1 iterations were executed.
            // Falls back to data row count if missing or unparseable.
            int iterations = dataRows.Count;
            int iterCol = Su2Solver.FindColumn(headers, "Inner_Iter", out _);
            if (iterCol < 0) iterCol = Su2Solver.FindColumn(headers, "ITER", out _);
            if (iterCol < 0) iterCol = Su2Solver.FindColumn(headers, "Iter", out _);

            if (iterCol >= 0)
            {
                string[] lastRow = dataRows[^1];
                if (iterCol < lastRow.Length &&
                    int.TryParse(lastRow[iterCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out int lastIterVal) &&
                    lastIterVal >= 0)
                {
                    iterations = lastIterVal + 1;
                }
            }

            // 2. Converged: stopped before ITER
            bool converged = options.MaxIterations > 0 && iterations > 0 && iterations < options.MaxIterations;

            // 3. ResidualDrop:
            // Decades by which the first RMS residual (column starting with "rms[") dropped from first to last row.
            // In SU2 rms[...] values are already log10, so drop = first - last.
            float residualDrop = float.NaN;
            int rmsCol = -1;
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i].StartsWith("rms[", StringComparison.OrdinalIgnoreCase))
                {
                    rmsCol = i;
                    break;
                }
            }

            if (rmsCol >= 0)
            {
                string[] firstRow = dataRows[0];
                string[] lastRow = dataRows[^1];

                if (rmsCol < firstRow.Length && rmsCol < lastRow.Length &&
                    float.TryParse(firstRow[rmsCol], NumberStyles.Float, CultureInfo.InvariantCulture, out float firstVal) &&
                    float.IsFinite(firstVal) &&
                    float.TryParse(lastRow[rmsCol], NumberStyles.Float, CultureInfo.InvariantCulture, out float lastVal) &&
                    float.IsFinite(lastVal))
                {
                    residualDrop = firstVal - lastVal;
                }
            }

            // 4. Oscillation & WindowMeans:
            // Over the last CauchyElems rows (or fewer if not enough rows exist).
            var oscillation = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var windowMeans = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            var tailMeans = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var drift = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var amplitude = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var uncertainty = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            var trendSignificant = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            int tailRows = Math.Min(options.CauchyElems > 0 ? options.CauchyElems : dataRows.Count, dataRows.Count / 2);

            var columnsToEvaluate = options.MetricColumns ?? Array.Empty<string>();
            foreach (string metricCol in columnsToEvaluate)
            {
                if (string.IsNullOrWhiteSpace(metricCol)) continue;

                int colIndex = Su2Solver.FindColumn(headers, metricCol, out _);
                if (colIndex < 0) continue;

                int windowSize = options.CauchyElems > 0 ? options.CauchyElems : dataRows.Count;
                int count = Math.Min(windowSize, dataRows.Count);
                int startIndex = dataRows.Count - count;

                var windowValues = new List<float>(count);
                for (int i = startIndex; i < dataRows.Count; i++)
                {
                    string[] row = dataRows[i];
                    if (colIndex < row.Length &&
                        float.TryParse(row[colIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out float val) &&
                        float.IsFinite(val))
                    {
                        windowValues.Add(val);
                    }
                }

                if (windowValues.Count > 0)
                {
                    float min = windowValues.Min();
                    float max = windowValues.Max();
                    float mean = windowValues.Average();
                    windowMeans[metricCol] = mean;

                    float osc;
                    if (Math.Abs(mean) < 1e-7f)
                    {
                        // guard mean ≈ 0
                        osc = 0f;
                    }
                    else
                    {
                        osc = (max - min) / Math.Abs(mean);
                    }
                    oscillation[metricCol] = osc;
                }

                // Tail window: never more than half the history.
                if (tailRows >= 4)
                {
                    var tail = new List<float>(tailRows);
                    for (int i = dataRows.Count - tailRows; i < dataRows.Count; i++)
                    {
                        string[] row = dataRows[i];
                        if (colIndex < row.Length &&
                            float.TryParse(row[colIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out float tv) &&
                            float.IsFinite(tv))
                        {
                            tail.Add(tv);
                        }
                    }

                    if (tail.Count >= 4)
                    {
                        float tailMean = tail.Average();
                        tailMeans[metricCol] = tailMean;

                        int half = tail.Count / 2;
                        float firstHalf = tail.Take(half).Average();
                        float secondHalf = tail.Skip(tail.Count - half).Average();
                        drift[metricCol] = Math.Abs(secondHalf - firstHalf);

                        amplitude[metricCol] = DetrendedPeakToPeak(tail) / Math.Max(Math.Abs(tailMean), 1e-7f);

                        if (BatchStatistics(tail, out double standardError, out bool significant))
                        {
                            uncertainty[metricCol] = (float)(standardError / Math.Max(Math.Abs(tailMean), 1e-7));
                            trendSignificant[metricCol] = significant;
                        }
                    }
                }
            }

            return new Su2HistoryResult(iterations, converged, residualDrop, oscillation, windowMeans)
            {
                TailRows = tailRows,
                TailMeans = tailMeans,
                WindowDrift = drift,
                OscillationAmplitude = amplitude,
                TailUncertainty = uncertainty,
                TrendSignificant = trendSignificant,
            };
        }

        /// <summary>
        /// Batch-mean statistics: splits the window into <see cref="Batches"/> equal-length batches and fits
        /// a line through batch means. <paramref name="significant"/> is true if the slope exceeds 3x its
        /// standard error (statistically significant drift). <paramref name="standardError"/> is the standard
        /// error of the window mean from batch scatter around the trend line.
        /// </summary>
        public static bool BatchStatistics(IReadOnlyList<float> values, out double standardError, out bool significant)
        {
            standardError = double.NaN;
            significant = false;

            int size = values.Count / Batches;
            if (size < 2) return false;

            var means = new double[Batches];
            int offset = values.Count - size * Batches;   // Drop leading remainder (startup)
            for (int b = 0; b < Batches; b++)
            {
                double sum = 0;
                for (int i = 0; i < size; i++) sum += values[offset + b * size + i];
                means[b] = sum / size;
            }

            double meanX = (Batches - 1) / 2.0, meanY = means.Average();
            double sxy = 0, sxx = 0;
            for (int b = 0; b < Batches; b++)
            {
                sxy += (b - meanX) * (means[b] - meanY);
                sxx += (b - meanX) * (b - meanX);
            }
            double slope = sxy / sxx;

            double ss = 0;
            for (int b = 0; b < Batches; b++)
            {
                double r = means[b] - (meanY + slope * (b - meanX));
                ss += r * r;
            }
            double residualSd = Math.Sqrt(ss / (Batches - 2));

            // Floor against division by zero on exact linear or constant histories
            double floor = 1e-9 * Math.Max(Math.Abs(meanY), 1e-12);
            double slopeError = Math.Max(residualSd, floor) / Math.Sqrt(sxx);

            significant = Math.Abs(slope) > 3.0 * slopeError && Math.Abs(slope) * (Batches - 1) > floor * 10;
            standardError = residualSd / Math.Sqrt(Batches);
            return true;
        }

        /// <summary>Peak-to-peak range of residuals from the least-squares trend line.</summary>
        private static float DetrendedPeakToPeak(IReadOnlyList<float> values)
        {
            int n = values.Count;
            double meanX = (n - 1) / 2.0;
            double meanY = 0;
            for (int i = 0; i < n; i++) meanY += values[i];
            meanY /= n;

            double sxy = 0, sxx = 0;
            for (int i = 0; i < n; i++)
            {
                sxy += (i - meanX) * (values[i] - meanY);
                sxx += (i - meanX) * (i - meanX);
            }
            double slope = sxx > 0 ? sxy / sxx : 0.0;

            double min = double.MaxValue, max = double.MinValue;
            for (int i = 0; i < n; i++)
            {
                double residual = values[i] - (meanY + slope * (i - meanX));
                if (residual < min) min = residual;
                if (residual > max) max = residual;
            }
            return (float)(max - min);
        }

        /// <summary>
        /// Simulates SU2's Cauchy convergence criterion offline on <paramref name="lines"/> by comparing
        /// adjacent window means <c>|mean(i - W .. i) - mean(i - 2W .. i - W)| &lt; cauchyEps</c>
        /// starting after <paramref name="startIter"/>.
        /// </summary>
        public static CauchyStopSimulation SimulateCauchyStop(
            string[] lines,
            int cauchyElems,
            double cauchyEps,
            string columnName = "CD",
            int startIter = 100)
        {
            if (lines == null || lines.Length < 2)
                return new CauchyStopSimulation(0, 0, float.NaN, float.NaN, float.NaN, TriggeredEarly: false);

            string[] validLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (validLines.Length < 2)
                return new CauchyStopSimulation(0, 0, float.NaN, float.NaN, float.NaN, TriggeredEarly: false);

            string[] headers = Su2Solver.SplitCsvLine(validLines[0]);
            int colIndex = Su2Solver.FindColumn(headers, columnName, out _);
            if (colIndex < 0)
                return new CauchyStopSimulation(0, 0, float.NaN, float.NaN, float.NaN, TriggeredEarly: false);

            int iterCol = Su2Solver.FindColumn(headers, "Inner_Iter", out _);
            if (iterCol < 0) iterCol = Su2Solver.FindColumn(headers, "ITER", out _);
            if (iterCol < 0) iterCol = Su2Solver.FindColumn(headers, "Iter", out _);

            var values = new List<double>(validLines.Length - 1);
            var iterations = new List<int>(validLines.Length - 1);

            for (int rowIdx = 1; rowIdx < validLines.Length; rowIdx++)
            {
                string[] row = Su2Solver.SplitCsvLine(validLines[rowIdx]);
                if (colIndex >= row.Length) continue;
                if (!double.TryParse(row[colIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out double val) ||
                    !double.IsFinite(val))
                    continue;

                int iterCount = values.Count + 1;
                if (iterCol >= 0 && iterCol < row.Length &&
                    int.TryParse(row[iterCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedIter) &&
                    parsedIter >= 0)
                {
                    iterCount = parsedIter + 1;
                }

                values.Add(val);
                iterations.Add(iterCount);
            }

            int n = values.Count;
            if (n == 0)
                return new CauchyStopSimulation(0, 0, float.NaN, float.NaN, float.NaN, TriggeredEarly: false);

            int w = Math.Max(1, cauchyElems);
            int totalIterations = iterations[^1];

            var prefix = new double[n + 1];
            for (int i = 0; i < n; i++)
                prefix[i + 1] = prefix[i] + values[i];

            int finalCount = Math.Min(w, n);
            float finalValue = (float)((prefix[n] - prefix[n - finalCount]) / finalCount);

            int minCheckRow = Math.Max(2 * w, Math.Max(0, startIter) + 1);
            for (int i = minCheckRow; i <= n; i++)
            {
                double meanCurrent = (prefix[i] - prefix[i - w]) / w;
                double meanPrevious = (prefix[i - w] - prefix[i - 2 * w]) / w;
                if (Math.Abs(meanCurrent - meanPrevious) < cauchyEps)
                {
                    int stopIter = iterations[i - 1];
                    float valueAtStop = (float)meanCurrent;
                    float relError = Math.Abs(valueAtStop - finalValue) / Math.Max(Math.Abs(finalValue), 1e-7f);
                    return new CauchyStopSimulation(stopIter, totalIterations, valueAtStop, finalValue, relError, TriggeredEarly: i < n);
                }
            }

            return new CauchyStopSimulation(totalIterations, totalIterations, finalValue, finalValue, 0f, TriggeredEarly: false);
        }

        /// <summary>
        /// Steps through <paramref name="lines"/> in increments of <paramref name="step"/> rows and returns the
        /// earliest iteration count where <see cref="Su2ConvergenceJudge"/> passes and all monitored metrics
        /// show no statistically significant trend (stationary), or <c>null</c> if never achieved.
        /// </summary>
        public static int? FindEarliestStationaryIteration(
            string[] lines,
            Su2HistoryOptions options,
            float relativeTolerance,
            float absoluteTolerance,
            int step = 100)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (lines == null || lines.Length < 2) return null;

            string[] validLines = lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            if (validLines.Length < 2) return null;

            List<string> metricCols = (options.MetricColumns ?? Array.Empty<string>())
                .Where(c => !string.IsNullOrWhiteSpace(c))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (metricCols.Count == 0) return null;

            var metricMap = metricCols.ToDictionary(c => c, c => c, StringComparer.OrdinalIgnoreCase);
            var atolMap = metricCols.ToDictionary(c => c, _ => Math.Max(0f, absoluteTolerance), StringComparer.OrdinalIgnoreCase);

            int dataCount = validLines.Length - 1;
            int stepSize = Math.Max(1, step);
            int firstCheck = Math.Min(dataCount, Math.Max(Su2ConvergenceJudge.MinTailRows * 2, stepSize));

            for (int endRow = firstCheck; endRow <= dataCount;)
            {
                var prefixLines = new string[endRow + 1];
                Array.Copy(validLines, prefixLines, endRow + 1);

                Su2HistoryResult history = Evaluate(prefixLines, options);
                Su2ConvergenceVerdict verdict = Su2ConvergenceJudge.Judge(
                    history, metricMap, relativeTolerance, absoluteScale: 1.0f, atolMap);

                if (verdict.Assessed && verdict.Converged && Su2DiagnosticsExtractor.IsStationary(history, metricMap))
                    return history.Iterations;

                if (endRow == dataCount) break;
                endRow = Math.Min(dataCount, endRow + stepSize);
            }

            return null;
        }
    }
}
