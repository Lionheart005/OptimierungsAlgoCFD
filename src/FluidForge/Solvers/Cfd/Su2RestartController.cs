using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Manages cleanup of history/restart files and the automatic steady restart loop
    /// including Cauchy tolerance tightening and CSV stitching.
    /// </summary>
    internal static class Su2RestartController
    {
        internal static void CleanHistoryFiles(string workingDirectory, string historyPath)
        {
            if (File.Exists(historyPath)) File.Delete(historyPath);
            // Otherwise stale restart histories (history_00002.csv) would be picked up erroneously.
            foreach (string stale in Directory.GetFiles(workingDirectory, "history_*.csv"))
            {
                try { File.Delete(stale); } catch (IOException) { }
            }
        }

        internal static void CleanSteadyRestartFiles(string workingDirectory)
        {
            foreach (string name in new[] { "restart.dat", "restart_flow.dat" })
            {
                string fullPath = Path.Combine(workingDirectory, name);
                if (File.Exists(fullPath))
                {
                    try { File.Delete(fullPath); } catch (IOException) { }
                }
            }
        }

        internal static void ResolveBlockHistoryFile(string workingDirectory, string historyPath)
        {
            if (!File.Exists(historyPath))
            {
                string? restarted = Directory.GetFiles(workingDirectory, "history_*.csv")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (restarted != null) File.Copy(restarted, historyPath, overwrite: true);
            }
        }

        internal static bool TryCheckNeedsSteadyRestart(
            string[] lines,
            SimulationConfig originalEffectiveConfig,
            Dictionary<string, string>? wanted,
            out int totalIterations,
            out float worstNormalized)
        {
            totalIterations = 0;
            worstNormalized = 0f;

            if (wanted == null || wanted.Count == 0)
                return false;

            var metricCols = wanted.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
            if (metricCols.Count == 0)
                return false;

            var historyOptions = new Su2HistoryOptions(
                originalEffectiveConfig.Su2MaxIterations,
                originalEffectiveConfig.CauchyElements,
                metricCols);
            Su2HistoryResult historyResult = Su2HistoryAnalyzer.Evaluate(lines, historyOptions);
            totalIterations = historyResult.Iterations;

            if (totalIterations <= 0 || totalIterations >= originalEffectiveConfig.Su2MaxIterations)
                return false;

            Su2ConvergenceVerdict verdict = Su2ConvergenceJudge.Judge(
                historyResult,
                wanted,
                originalEffectiveConfig.IterativeToleranceFraction * originalEffectiveConfig.MeshCalibration.Tolerance,
                originalEffectiveConfig.IterativeToleranceFraction,
                originalEffectiveConfig.MeshCalibration.AbsoluteTolerances);

            if (!verdict.Assessed)
                return false;

            worstNormalized = verdict.WorstNormalized;
            if (verdict.Converged)
                return false;

            if (Su2DiagnosticsExtractor.IsStationary(historyResult, wanted))
                return false;

            return true;
        }

        /// <summary>
        /// Searches the working directory for an existing steady restart solution
        /// (<c>restart.dat</c> or <c>restart_flow.dat</c>, size &gt; 0) and picks the newest if both exist.
        /// </summary>
        internal static string? FindSteadyRestartFile(string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                return null;

            var candidates = new[] { "restart.dat", "restart_flow.dat" };
            FileInfo? best = null;
            foreach (string name in candidates)
            {
                string fullPath = Path.Combine(workingDirectory, name);
                if (!File.Exists(fullPath)) continue;
                var info = new FileInfo(fullPath);
                if (info.Length <= 0) continue;
                if (best == null || info.LastWriteTimeUtc > best.LastWriteTimeUtc)
                {
                    best = info;
                }
            }
            return best?.Name;
        }

        /// <summary>
        /// Tightens the Cauchy tolerance for the next restart block by a factor of 10
        /// (floor <c>1e-7</c>).
        /// </summary>
        internal static string TightenTolerance(string? currentTolerance)
        {
            const double Floor = 1e-7;
            if (!double.TryParse(currentTolerance, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)
                || value <= 0.0)
            {
                return "1e-6";
            }

            double tightened = Math.Max(value / 10.0, Floor);
            return tightened.ToString("0.###e-0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Appends a restart block's CSV data seamlessly to the cumulative history
        /// and keeps the iteration column monotonic if SU2 reset iteration numbering on restart.
        /// </summary>
        internal static string[] StitchHistoryCsv(string[] previousLines, string[] newLines)
        {
            string[] prevValid = previousLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            string[] newValid = newLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

            if (prevValid.Length < 2) return newValid;
            if (newValid.Length < 2) return prevValid;

            string[] prevHeaders = Su2DiagnosticsExtractor.SplitCsvLine(prevValid[0]);
            string[] newHeaders = Su2DiagnosticsExtractor.SplitCsvLine(newValid[0]);

            int prevIterCol = FindIterationColumn(prevHeaders);
            int newIterCol = FindIterationColumn(newHeaders);

            int prevLastIter = prevValid.Length - 2;
            if (prevIterCol >= 0)
            {
                string[] prevLastFields = Su2DiagnosticsExtractor.SplitCsvLine(prevValid[^1]);
                if (prevIterCol < prevLastFields.Length &&
                    int.TryParse(prevLastFields[prevIterCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLast) &&
                    parsedLast >= 0)
                {
                    prevLastIter = parsedLast;
                }
            }

            int firstNewIter = 0;
            if (newIterCol >= 0)
            {
                string[] firstNewFields = Su2DiagnosticsExtractor.SplitCsvLine(newValid[1]);
                if (newIterCol < firstNewFields.Length &&
                    int.TryParse(firstNewFields[newIterCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFirst) &&
                    parsedFirst >= 0)
                {
                    firstNewIter = parsedFirst;
                }
            }

            int iterOffset = firstNewIter <= prevLastIter ? (prevLastIter + 1 - firstNewIter) : 0;

            var combined = new List<string>(prevValid.Length + newValid.Length - 1);
            combined.AddRange(prevValid);

            for (int i = 1; i < newValid.Length; i++)
            {
                string row = newValid[i];
                if (newIterCol >= 0 && iterOffset > 0)
                {
                    string[] rawFields = row.Split(',');
                    if (newIterCol < rawFields.Length)
                    {
                        string cleaned = rawFields[newIterCol].Replace("\"", "").Trim();
                        if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowIter))
                        {
                            rawFields[newIterCol] = (rowIter + iterOffset).ToString(CultureInfo.InvariantCulture);
                            row = string.Join(", ", rawFields.Select(f => f.Trim()));
                        }
                    }
                }
                combined.Add(row);
            }

            return combined.ToArray();
        }

        internal static int FindIterationColumn(string[] headers)
        {
            int col = Su2DiagnosticsExtractor.FindColumn(headers, "Inner_Iter", out _);
            if (col < 0) col = Su2DiagnosticsExtractor.FindColumn(headers, "ITER", out _);
            if (col < 0) col = Su2DiagnosticsExtractor.FindColumn(headers, "Iter", out _);
            return col;
        }

        internal static string ResolveConvergenceColumn(Su2SolverOptions options, Dictionary<string, string> wanted)
        {
            string convField = (options.ConvergenceField ?? string.Empty).Trim();
            if (convField.Length > 0)
            {
                foreach (var entry in wanted)
                {
                    if (entry.Key.Equals(convField, StringComparison.OrdinalIgnoreCase) ||
                        entry.Value.Equals(convField, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value;
                    }
                }
            }

            return wanted.Values.FirstOrDefault() ?? "CD";
        }
    }
}
