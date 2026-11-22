using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Centralized logging and formatting framework for console and log file output.
    /// <para>
    /// Core principles:
    /// 1. Clean, properly formatted UTF-8 text without raw ANSI escape codes in files
    ///    (when <see cref="Console.IsOutputRedirected"/> is active).
    /// 2. Local terminal colors (<see cref="Console.ForegroundColor"/>) when interactive.
    /// 3. Prominent banners, clear variant blocks, and compact iteration tables.
    /// </para>
    /// </summary>
    public static class SimulationLog
    {
        private const int BoxWidth = 80;

        #region Colored Output Helpers

        public static void WriteLineColored(string text, ConsoleColor color)
        {
            if (!Console.IsOutputRedirected)
            {
                ConsoleColor prev = Console.ForegroundColor;
                try
                {
                    Console.ForegroundColor = color;
                    Console.WriteLine(text);
                }
                finally
                {
                    Console.ForegroundColor = prev;
                }
            }
            else
            {
                Console.WriteLine(text);
            }
        }

        #endregion

        #region Banners

        /// <summary>
        /// Prints a prominent double-bordered banner (80 characters wide).
        /// </summary>
        public static void PrintBanner(string title, params string[] subtitleLines)
        {
            Console.WriteLine();
            string top = "╔" + new string('═', BoxWidth - 2) + "╗";
            string bottom = "╚" + new string('═', BoxWidth - 2) + "╝";

            WriteLineColored(top, ConsoleColor.Cyan);

            PrintBannerLine(title, ConsoleColor.Cyan);
            foreach (var line in subtitleLines)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    PrintBannerLine(line, ConsoleColor.DarkCyan);
            }

            WriteLineColored(bottom, ConsoleColor.Cyan);
        }

        private static void PrintBannerLine(string text, ConsoleColor color)
        {
            int maxContentWidth = BoxWidth - 5; // ║  ... ║
            string content = text.Length > maxContentWidth ? text.Substring(0, maxContentWidth - 3) + "..." : text;
            string formatted = "║  " + content.PadRight(maxContentWidth) + "║";
            WriteLineColored(formatted, color);
        }

        public static void PrintIterationBanner(int iteration, int maxIterations)
        {
            PrintBanner($"ITERATION {iteration} OF {maxIterations}");
        }

        #endregion

        #region Variant Header and Genes

        public static void PrintVariantHeader(int iter, int maxIter, int variant, int maxVariants)
        {
            Console.WriteLine();
            string title = $"┌─ [Iteration {iter}/{maxIter} · Variant {variant}/{maxVariants}] ";
            int remaining = Math.Max(2, BoxWidth - title.Length);
            string header = title + new string('─', remaining);
            WriteLineColored(header, ConsoleColor.Cyan);
        }

        public static void PrintGeneLine(Dictionary<string, float> parameters)
        {
            var parts = parameters.Select(kvp => $"{kvp.Key} = {FormatNumber(kvp.Value)}");
            string genes = string.Join(" | ", parts);
            WriteLineColored($"│  ▸ Genes:     {genes}", ConsoleColor.Cyan);
        }

        #endregion

        #region Details, Warnings, and Errors

        /// <summary>
        /// Subtly indented technical intermediate step (e.g., mesh, solver, convergence).
        /// </summary>
        public static void Detail(string message)
        {
            if (message.StartsWith(OutputConventions.WarningTag, StringComparison.OrdinalIgnoreCase))
            {
                WriteLineColored($"│  · {message}", ConsoleColor.Yellow);
            }
            else if (message.StartsWith(OutputConventions.ErrorTag, StringComparison.OrdinalIgnoreCase) ||
                     message.StartsWith("[REJECTED]", StringComparison.OrdinalIgnoreCase))
            {
                WriteLineColored($"│  ✖ {message}", ConsoleColor.Red);
            }
            else if (message.StartsWith("[OK]", StringComparison.OrdinalIgnoreCase))
            {
                WriteLineColored($"│  · {message}", ConsoleColor.Green);
            }
            else
            {
                WriteLineColored($"│  · {message}", ConsoleColor.DarkGray);
            }
        }

        /// <summary>
        /// Prints a formatted warning line.
        /// </summary>
        public static void Warning(string message)
        {
            WriteLineColored($"│  · {OutputConventions.WarningTag} {message}", ConsoleColor.Yellow);
        }

        /// <summary>
        /// Prints a formatted error line.
        /// </summary>
        public static void Error(string message)
        {
            WriteLineColored($"│  ✖ {OutputConventions.ErrorTag} {message}", ConsoleColor.Red);
        }

        /// <summary>
        /// Prints a formatted success line.
        /// </summary>
        public static void Success(string message)
        {
            WriteLineColored($"│  ★ {message}", ConsoleColor.Green);
        }

        /// <summary>
        /// Logs geometry creation, distinguishing between 2D and 3D.
        /// </summary>
        public static void PrintGeometry(string? stlOrJsonPath, IReadOnlyDictionary<string, float> passiveParams)
        {
            bool is2D = !string.IsNullOrEmpty(stlOrJsonPath) &&
                        stlOrJsonPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

            string typeStr = is2D ? "2D profile contour created" : "3D voxel geometry created";

            var relevantMetrics = passiveParams
                .Where(p => p.Key != "ScaleFactor" && p.Key != "MeshRefinementFactor" && !p.Key.StartsWith("Su2_") && !p.Key.StartsWith("YPlus_"))
                .Take(4)
                .Select(p => $"{p.Key}={FormatNumber(p.Value)}");

            string metricsStr = string.Join(", ", relevantMetrics);
            if (!string.IsNullOrEmpty(metricsStr))
            {
                Detail($"Geometry: {typeStr}. (Metrics: {metricsStr})");
            }
            else
            {
                Detail($"Geometry: {typeStr}.");
            }
        }

        #endregion

        #region Variant Result, Diagnostics & Footer

        /// <summary>
        /// Prominent result line per variant.
        /// </summary>
        public static void PrintVariantResult(ModelRecord record)
        {
            if (record.SimulationFailed)
            {
                WriteLineColored("│  ✖ Result:    SIMULATION FAILED", ConsoleColor.Red);
                return;
            }

            var priorityKeys = new[] { "Drag", "Lift", "GlideRatio", "SideForce", "Volume", "FrontalArea", "MaxThickness", "Area" };
            var foundMetrics = new List<string>();

            foreach (var key in priorityKeys)
            {
                if (record.PassiveParameters.TryGetValue(key, out float val))
                {
                    foundMetrics.Add($"{key} = {FormatMetricValue(key, val)}");
                }
            }

            if (foundMetrics.Count == 0)
            {
                var others = record.PassiveParameters
                    .Where(p => !p.Key.StartsWith("Su2_") && !p.Key.StartsWith("YPlus_") && !ModelRecord.IsMeshMetadataKey(p.Key) &&
                                p.Key != "ScaleFactor" && p.Key != "MeshCells" &&
                                p.Key != "MeshRefinementFactor" && p.Key != "FineVerified" &&
                                p.Key != "FineDeviation" && p.Key != "ReynoldsNumber")
                    .Take(3);
                foreach (var kvp in others)
                {
                    foundMetrics.Add($"{kvp.Key} = {FormatMetricValue(kvp.Key, kvp.Value)}");
                }
            }

            string metricsPart = foundMetrics.Count > 0 ? $"   ({string.Join(" | ", foundMetrics)})" : "";
            string resultLine = $"│  ★ Result:    Fitness = {record.Fitness.ToString("0.###", CultureInfo.InvariantCulture)}{metricsPart}";
            WriteLineColored(resultLine, ConsoleColor.Green);
        }

        /// <summary>
        /// Subtle diagnostics line (mesh cells, y+, convergence, Reynolds number).
        /// </summary>
        public static void PrintVariantDiagnostics(ModelRecord record)
        {
            if (record.SimulationFailed) return;

            var diagItems = new List<string>();

            if (record.PassiveParameters.TryGetValue("MeshCells", out float cells) && cells > 0)
            {
                diagItems.Add($"Cells = {(long)cells}");
            }

            if (record.PassiveParameters.TryGetValue("YPlus_P95", out float p95))
            {
                if (record.PassiveParameters.TryGetValue("YPlus_Max", out float max))
                {
                    diagItems.Add($"y+ P95 = {p95.ToString("0.00", CultureInfo.InvariantCulture)} (Max {max.ToString("0.00", CultureInfo.InvariantCulture)})");
                }
                else
                {
                    diagItems.Add($"y+ P95 = {p95.ToString("0.00", CultureInfo.InvariantCulture)}");
                }
            }

            if (record.PassiveParameters.TryGetValue("Su2_Iterations", out float su2It))
            {
                bool conv = record.PassiveParameters.TryGetValue("Su2_Converged", out float cVal) && cVal > 0.5f;
                string convText = conv ? "converged" : "not converged";
                diagItems.Add($"SU2 = {(int)su2It} it. ({convText})");
            }

            if (record.PassiveParameters.TryGetValue("ReynoldsNumber", out float re) && re > 0)
            {
                diagItems.Add($"Re = {re.ToString("0.0e0", CultureInfo.InvariantCulture)}");
            }

            if (diagItems.Count > 0)
            {
                string diagLine = $"│  · Diagnostics: {string.Join(" | ", diagItems)}";
                WriteLineColored(diagLine, ConsoleColor.DarkGray);
            }
        }

        /// <summary>
        /// Closing line of a variant with runtime and forecast.
        /// Forecast lines contain the word "Forecast:", which <c>sim-runner.sh status</c> searches for.
        /// </summary>
        public static void PrintVariantFooter(double durationSeconds, RunTimeEstimate? estimate, DateTime now)
        {
            foreach (string line in VariantFooterLines(durationSeconds, estimate, now))
                WriteLineColored(line, ConsoleColor.DarkGray);
        }

        /// <summary>The lines of <see cref="PrintVariantFooter"/> as text (testable without console).</summary>
        public static IReadOnlyList<string> VariantFooterLines(double durationSeconds, RunTimeEstimate? estimate, DateTime now)
        {
            var lines = new List<string>
            {
                $"└─ ⏱ Duration: {FormatDuration(TimeSpan.FromSeconds(durationSeconds))}"
            };
            if (estimate == null) return lines;

            string prefix = estimate.UpperBound ? "Optimization at most until" : "Optimization approx.";
            lines[0] += $" | Forecast: {prefix} {FormatDateTime(now + estimate.Remaining)} (Remaining ≈ {FormatDuration(estimate.Remaining)})";

            if (estimate.FineCandidates > 0 && estimate.FineVerification > TimeSpan.Zero)
            {
                lines.Add(
                    $"   Forecast: followed by fine verification up to ≈ {FormatDuration(estimate.FineVerification)} "
                    + $"({estimate.FineCandidates} {(estimate.FineCandidates == 1 ? "candidate" : "candidates")}), "
                    + $"all done {(estimate.UpperBound ? "at most until" : "approx.")} "
                    + FormatDateTime(now + estimate.Remaining + estimate.FineVerification));
            }
            return lines;
        }

        /// <summary>
        /// One line after the iteration table: when the next iteration will finish.
        /// Useful when manually stopping a run after iteration N.
        /// </summary>
        public static void PrintTimeForecast(RunTimeEstimate? estimate, DateTime now)
        {
            string? line = TimeForecastLine(estimate, now);
            if (line != null) WriteLineColored(line, ConsoleColor.Cyan);
        }

        /// <summary>The line of <see cref="PrintTimeForecast"/>; <c>null</c> if there is nothing to forecast.</summary>
        public static string? TimeForecastLine(RunTimeEstimate? estimate, DateTime now)
        {
            if (estimate == null) return null;

            string bound = estimate.UpperBound ? "at most " : "";
            string text = estimate.IterationNumber > 0
                ? $"[FORECAST] Iteration {estimate.IterationNumber} done {bound}approx. {FormatDateTime(now + estimate.IterationRemaining)}"
                  + $" | Optimization done {bound}approx. {FormatDateTime(now + estimate.Remaining)}"
                : $"[FORECAST] Optimization done";

            if (estimate.FineCandidates > 0 && estimate.FineVerification > TimeSpan.Zero)
                text += $" | followed by fine verification up to ≈ {FormatDuration(estimate.FineVerification)}";
            return text;
        }

        /// <summary>
        /// Human-readable duration with days: <c>4 s</c>, <c>34 min</c>, <c>4 h 49 min</c>, <c>12 d 17 h</c>.
        /// </summary>
        public static string FormatDuration(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;

            // Round first, then pick the unit: 59.6 s is "1 min", not "60 s".
            int totalSeconds = (int)Math.Round(duration.TotalSeconds);
            if (totalSeconds < 60) return $"{totalSeconds} s";

            int totalMinutes = (int)Math.Round(duration.TotalMinutes);
            if (totalMinutes < 60) return $"{totalMinutes} min";
            if (totalMinutes < 24 * 60) return $"{totalMinutes / 60} h {totalMinutes % 60} min";

            int totalHours = (int)Math.Round(duration.TotalHours);
            return $"{totalHours / 24} d {totalHours % 24} h";
        }

        private static readonly string[] WeekdayShort = { "Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat" };

        /// <summary>Date with short weekday, e.g., <c>Mon 23.11. 09:00</c> (culture-independent formatting).</summary>
        public static string FormatDateTime(DateTime time)
            => $"{WeekdayShort[(int)time.DayOfWeek]} {time.Day:00}.{time.Month:00}. {time.Hour:00}:{time.Minute:00}";

        #endregion

        #region Iteration Summary (Table)

        /// <summary>
        /// Prints a compact, aligned table of all variants at the end of an iteration.
        /// </summary>
        public static void PrintIterationSummary(int iteration, int maxIterations, List<ModelRecord> records, ModelRecord winner)
        {
            Console.WriteLine();
            var priorityMetrics = new[] { "Drag", "Lift", "GlideRatio", "Volume", "FrontalArea" };
            var activeMetrics = priorityMetrics.Where(m => records.Any(r => r.PassiveParameters.ContainsKey(m))).Take(3).ToList();

            if (activeMetrics.Count == 0)
            {
                activeMetrics = records
                    .SelectMany(r => r.PassiveParameters.Keys)
                    .Where(k => !k.StartsWith("Su2_") && !k.StartsWith("YPlus_") && !ModelRecord.IsMeshMetadataKey(k) &&
                                k != "ScaleFactor" && k != "MeshCells" &&
                                k != "MeshRefinementFactor" && k != "FineVerified" &&
                                k != "FineDeviation" && k != "ReynoldsNumber")
                    .Distinct()
                    .Take(2)
                    .ToList();
            }

            // Define column widths
            int colVar = 5;
            var colWidths = new Dictionary<string, int>();
            foreach (var m in activeMetrics) colWidths[m] = Math.Max(m.Length + 2, 10);
            int colFit = 11;
            int colStatus = 14;

            // Draw table borders
            string BuildBorder(char left, char sep, char right, char fill)
            {
                var parts = new List<string> { new string(fill, colVar) };
                foreach (var m in activeMetrics) parts.Add(new string(fill, colWidths[m]));
                parts.Add(new string(fill, colFit));
                parts.Add(new string(fill, colStatus));
                return left + string.Join(sep.ToString(), parts) + right;
            }

            WriteLineColored(BuildBorder('┌', '┬', '┐', '─'), ConsoleColor.DarkGray);

            // Header row
            var headers = new List<string> { "Var".PadLeft(colVar - 1) + " " };
            foreach (var m in activeMetrics) headers.Add(m.PadLeft(colWidths[m] - 1) + " ");
            headers.Add("Fitness".PadLeft(colFit - 1) + " ");
            headers.Add(" " + "Status".PadRight(colStatus - 1));
            WriteLineColored("│" + string.Join("│", headers) + "│", ConsoleColor.Cyan);

            WriteLineColored(BuildBorder('├', '┼', '┤', '─'), ConsoleColor.DarkGray);

            // Data rows
            foreach (var r in records)
            {
                bool isWinner = r == winner || (r.Iteration == winner.Iteration && r.Variant == winner.Variant);
                string varStr = r.Variant.ToString().PadLeft(colVar - 1) + " ";
                var rowCells = new List<string> { varStr };

                foreach (var m in activeMetrics)
                {
                    string valStr = r.PassiveParameters.TryGetValue(m, out float val)
                        ? FormatMetricValue(m, val)
                        : "-";
                    rowCells.Add(valStr.PadLeft(colWidths[m] - 1) + " ");
                }

                string fitStr = r.SimulationFailed ? "FAIL" : r.Fitness.ToString("0.###", CultureInfo.InvariantCulture);
                rowCells.Add(fitStr.PadLeft(colFit - 1) + " ");

                string statusStr;
                if (r.SimulationFailed) statusStr = "✖ ERROR";
                else if (isWinner) statusStr = "★ CHAMPION";
                else if (r.Fitness > 0f && r.Fitness <= 0.0002f) statusStr = "⚠ REJECTED";
                else statusStr = "OK";
                rowCells.Add(" " + statusStr.PadRight(colStatus - 1));

                string rowLine = "│" + string.Join("│", rowCells) + "│";
                if (isWinner)
                {
                    WriteLineColored(rowLine, ConsoleColor.Green);
                }
                else if (r.SimulationFailed)
                {
                    WriteLineColored(rowLine, ConsoleColor.Red);
                }
                else
                {
                    WriteLineColored(rowLine, ConsoleColor.Gray);
                }
            }

            WriteLineColored(BuildBorder('└', '┴', '┘', '─'), ConsoleColor.DarkGray);

            // Clear result line
            string champMsg = $"[RESULT] Iteration {iteration} completed — Champion is Variant {winner.Variant} (Score: {winner.Fitness:F3})";
            WriteLineColored(champMsg, ConsoleColor.Green);
        }

        #endregion

        #region Formatting Helpers

        public static string FormatNumber(float value)
        {
            float abs = MathF.Abs(value);
            if (abs > 0f && abs < 1.0f)
                return value.ToString("0.####", CultureInfo.InvariantCulture);
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        public static string FormatMetricValue(string metricName, float value)
        {
            float abs = MathF.Abs(value);
            if (abs > 0f && abs < 0.1f)
                return value.ToString("0.#####", CultureInfo.InvariantCulture);
            if (abs >= 0.1f && abs < 10.0f)
                return value.ToString("0.####", CultureInfo.InvariantCulture);
            return value.ToString("0.##", CultureInfo.InvariantCulture);
        }

        #endregion
    }
}
