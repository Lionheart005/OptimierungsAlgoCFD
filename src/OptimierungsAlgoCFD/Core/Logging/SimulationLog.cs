using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Zentrales Logging- und Formatierungs-Framework für Konsolen- und Logdateiausgaben.
    /// <para>
    /// Grundprinzipien:
    /// 1. Reiner, perfekt formatierter UTF-8-Text ohne rohe ANSI-Steuerzeichen in Dateien
    ///    (wenn <see cref="Console.IsOutputRedirected"/> aktiv ist).
    /// 2. Lokale Terminalfarben (<see cref="Console.ForegroundColor"/>), wenn interaktiv.
    /// 3. Prominente Banners, übersichtliche Varianten-Blöcke und kompakte Iterations-Tabellen.
    /// </para>
    /// </summary>
    public static class SimulationLog
    {
        private const int BoxWidth = 80;

        #region Farbausgabe-Hilfsmethoden

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

        #region Banner

        /// <summary>
        /// Gibt ein prominentes Banner mit Doppelrahmen (80 Zeichen Breite) aus.
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
            PrintBanner($"ITERATION {iteration} VON {maxIterations}");
        }

        #endregion

        #region Varianten-Kopf und Gene

        public static void PrintVariantHeader(int iter, int maxIter, int variant, int maxVariants)
        {
            Console.WriteLine();
            string title = $"┌─ [Iteration {iter}/{maxIter} · Variante {variant}/{maxVariants}] ";
            int remaining = Math.Max(2, BoxWidth - title.Length);
            string header = title + new string('─', remaining);
            WriteLineColored(header, ConsoleColor.Cyan);
        }

        public static void PrintGeneLine(Dictionary<string, float> parameters)
        {
            var parts = parameters.Select(kvp => $"{kvp.Key} = {FormatNumber(kvp.Value)}");
            string genes = string.Join(" | ", parts);
            WriteLineColored($"│  ▸ Gene:      {genes}", ConsoleColor.Cyan);
        }

        #endregion

        #region Details, Warnungen und Fehler

        /// <summary>
        /// Dezent eingerückter technischer Zwischenschritt (z.B. Netz, Solver, Konvergenz).
        /// </summary>
        public static void Detail(string message)
        {
            if (message.StartsWith("[WARNUNG]", StringComparison.OrdinalIgnoreCase))
            {
                WriteLineColored($"│  · {message}", ConsoleColor.Yellow);
            }
            else if (message.StartsWith("[FEHLER]", StringComparison.OrdinalIgnoreCase) ||
                     message.StartsWith("[ABGELEHNT]", StringComparison.OrdinalIgnoreCase))
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
        /// Gibt eine formatierte Warnzeile aus.
        /// </summary>
        public static void Warning(string message)
        {
            WriteLineColored($"│  · [WARNUNG] {message}", ConsoleColor.Yellow);
        }

        /// <summary>
        /// Gibt eine formatierte Fehlerzeile aus.
        /// </summary>
        public static void Error(string message)
        {
            WriteLineColored($"│  ✖ [FEHLER] {message}", ConsoleColor.Red);
        }

        /// <summary>
        /// Gibt eine formatierte Erfolgszeile aus.
        /// </summary>
        public static void Success(string message)
        {
            WriteLineColored($"│  ★ {message}", ConsoleColor.Green);
        }

        /// <summary>
        /// Ausgabe der Geometrie-Erstellung, sauber unterschieden nach 2D und 3D.
        /// </summary>
        public static void PrintGeometry(string? stlOrJsonPath, IReadOnlyDictionary<string, float> passiveParams)
        {
            bool is2D = !string.IsNullOrEmpty(stlOrJsonPath) &&
                        stlOrJsonPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

            string typeStr = is2D ? "2D-Profilkontur erstellt" : "3D-Voxel-Geometrie erstellt";

            var relevantMetrics = passiveParams
                .Where(p => p.Key != "ScaleFactor" && p.Key != "MeshRefinementFactor" && !p.Key.StartsWith("Su2_") && !p.Key.StartsWith("YPlus_"))
                .Take(4)
                .Select(p => $"{p.Key}={FormatNumber(p.Value)}");

            string metricsStr = string.Join(", ", relevantMetrics);
            if (!string.IsNullOrEmpty(metricsStr))
            {
                Detail($"Geometrie: {typeStr}. (Metriken: {metricsStr})");
            }
            else
            {
                Detail($"Geometrie: {typeStr}.");
            }
        }

        #endregion

        #region Varianten-Ergebnis, Diagnose & Abschluss

        /// <summary>
        /// Prominente Ergebniszeile je Variante.
        /// </summary>
        public static void PrintVariantResult(ModelRecord record)
        {
            if (record.SimulationFailed)
            {
                WriteLineColored("│  ✖ Ergebnis:  SIMULATION FEHLGESCHLAGEN", ConsoleColor.Red);
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
                    .Where(p => !p.Key.StartsWith("Su2_") && !p.Key.StartsWith("YPlus_") &&
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
            string resultLine = $"│  ★ Ergebnis:  Fitness = {record.Fitness.ToString("0.###", CultureInfo.InvariantCulture)}{metricsPart}";
            WriteLineColored(resultLine, ConsoleColor.Green);
        }

        /// <summary>
        /// Dezente Diagnosezeile (Gitterzellen, y+, Konvergenz, Reynoldszahl).
        /// </summary>
        public static void PrintVariantDiagnostics(ModelRecord record)
        {
            if (record.SimulationFailed) return;

            var diagItems = new List<string>();

            if (record.PassiveParameters.TryGetValue("MeshCells", out float cells) && cells > 0)
            {
                diagItems.Add($"Zellen = {(long)cells}");
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
                string convText = conv ? "konvergiert" : "nicht konvergiert";
                diagItems.Add($"SU2 = {(int)su2It} It. ({convText})");
            }

            if (record.PassiveParameters.TryGetValue("ReynoldsNumber", out float re) && re > 0)
            {
                diagItems.Add($"Re = {re.ToString("0.0e0", CultureInfo.InvariantCulture)}");
            }

            if (diagItems.Count > 0)
            {
                string diagLine = $"│  · Diagnose:  {string.Join(" | ", diagItems)}";
                WriteLineColored(diagLine, ConsoleColor.DarkGray);
            }
        }

        /// <summary>
        /// Abschlusszeile einer Variante mit Laufzeit und geschätzter Restzeit.
        /// </summary>
        public static void PrintVariantFooter(double durationSeconds, TimeSpan? estimatedRemaining)
        {
            string remainingStr = estimatedRemaining.HasValue
                ? $"  (Geschätzte Restzeit: {estimatedRemaining.Value:hh\\:mm\\:ss})"
                : "";
            string footer = $"└─ ⏱ Dauer: {durationSeconds.ToString("0.0", CultureInfo.InvariantCulture)}s{remainingStr}";
            WriteLineColored(footer, ConsoleColor.DarkGray);
        }

        #endregion

        #region Iterations-Zusammenfassung (Tabelle)

        /// <summary>
        /// Gibt am Ende einer Iteration eine kompakte, ausgerichtete Tabelle aller Varianten aus.
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
                    .Where(k => !k.StartsWith("Su2_") && !k.StartsWith("YPlus_") &&
                                k != "ScaleFactor" && k != "MeshCells" &&
                                k != "MeshRefinementFactor" && k != "FineVerified" &&
                                k != "FineDeviation" && k != "ReynoldsNumber")
                    .Distinct()
                    .Take(2)
                    .ToList();
            }

            // Spaltenbreiten definieren
            int colVar = 5;
            var colWidths = new Dictionary<string, int>();
            foreach (var m in activeMetrics) colWidths[m] = Math.Max(m.Length + 2, 10);
            int colFit = 11;
            int colStatus = 14;

            // Tabellen-Rahmen zeichnen
            string BuildBorder(char left, char sep, char right, char fill)
            {
                var parts = new List<string> { new string(fill, colVar) };
                foreach (var m in activeMetrics) parts.Add(new string(fill, colWidths[m]));
                parts.Add(new string(fill, colFit));
                parts.Add(new string(fill, colStatus));
                return left + string.Join(sep.ToString(), parts) + right;
            }

            WriteLineColored(BuildBorder('┌', '┬', '┐', '─'), ConsoleColor.DarkGray);

            // Kopfzeile
            var headers = new List<string> { "Var".PadLeft(colVar - 1) + " " };
            foreach (var m in activeMetrics) headers.Add(m.PadLeft(colWidths[m] - 1) + " ");
            headers.Add("Fitness".PadLeft(colFit - 1) + " ");
            headers.Add(" " + "Status".PadRight(colStatus - 1));
            WriteLineColored("│" + string.Join("│", headers) + "│", ConsoleColor.Cyan);

            WriteLineColored(BuildBorder('├', '┼', '┤', '─'), ConsoleColor.DarkGray);

            // Datenzeilen
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
                if (r.SimulationFailed) statusStr = "✖ FEHLER";
                else if (isWinner) statusStr = "★ CHAMPION";
                else if (r.Fitness > 0f && r.Fitness <= 0.0002f) statusStr = "⚠ ABGELEHNT";
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

            // Klare Ergebniszeile
            string champMsg = $"[ERGEBNIS] Iteration {iteration} abgeschlossen — Champion ist Variante {winner.Variant} (Score: {winner.Fitness:F3})";
            WriteLineColored(champMsg, ConsoleColor.Green);
        }

        #endregion

        #region Formatierungs-Helfer

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
