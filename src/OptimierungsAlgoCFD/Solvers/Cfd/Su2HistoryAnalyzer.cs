using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Parameter für die Auswertung des Konvergenzverlaufs aus der SU2-<c>history.csv</c> (TODO-35).
    /// </summary>
    public record Su2HistoryOptions
    {
        /// <summary>Maximale Iterationsanzahl aus der Konfiguration (ITER).</summary>
        public int MaxIterations { get; init; } = 500;

        /// <summary>Fenstergröße für die Cauchy-Konvergenzprüfung (CONV_CAUCHY_ELEMS).</summary>
        public int CauchyElems { get; init; } = 150;

        /// <summary>Namen der Spalten (z.B. "CD", "CL"), für die die Schwingung berechnet werden soll.</summary>
        public IReadOnlyList<string> MetricColumns { get; init; } = Array.Empty<string>();

        /// <summary>Synonym für <see cref="MetricColumns"/>.</summary>
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
    /// Ergebnis der Verlaufsauswertung einer SU2-Simulation (TODO-35).
    /// </summary>
    public record Su2HistoryResult(
        int Iterations,
        bool Converged,
        float ResidualDrop,
        IReadOnlyDictionary<string, float> Oscillation,
        IReadOnlyDictionary<string, float> WindowMeans)
    {
        /// <summary>
        /// Länge des Endfensters in Zeilen, über das <see cref="TailMeans"/>, <see cref="WindowDrift"/>
        /// und <see cref="OscillationAmplitude"/> gebildet sind: <c>min(CauchyElems, Zeilen / 2)</c>.
        /// Nie mehr als die halbe Historie, damit der Anlauf das Fenster nicht verfälscht (TODO-W3).
        /// </summary>
        public int TailRows { get; init; }

        /// <summary>Mittelwert je Kennwert über das Endfenster (ohne Anlaufphase).</summary>
        public IReadOnlyDictionary<string, float> TailMeans { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// Absolute Drift je Kennwert: Unterschied der Mittelwerte von erster und zweiter Hälfte des
        /// Endfensters. Maß für den iterativen Fehler (TODO-W3): Pendeln um einen festen Wert ergibt
        /// ~0, ein langsames Weiterwandern einen deutlichen Wert.
        /// </summary>
        public IReadOnlyDictionary<string, float> WindowDrift { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// Echte Schwingungsamplitude je Kennwert: Spannweite der Werte im Endfenster <b>nach Abzug
        /// des linearen Trends</b>, bezogen auf |Mittelwert|. Anders als <see cref="Su2HistoryResult.Oscillation"/>
        /// enthält sie keine Drift und eignet sich für die Warnung vor Wirbelablösung.
        /// </summary>
        public IReadOnlyDictionary<string, float> OscillationAmplitude { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// Relative Standardunsicherheit (1σ) des Fenstermittels je Kennwert, aus Blockmitteln
        /// (<see cref="Su2HistoryAnalyzer.Batches"/> gleich lange Blöcke, um den linearen Trend bereinigt),
        /// bezogen auf |Mittelwert|. Bei einer ruhigen Rechnung nahe 0, bei Wirbelablösung deutlich größer.
        /// Fehlt, wenn das Endfenster kürzer als zwei Zeilen je Block ist.
        /// </summary>
        public IReadOnlyDictionary<string, float> TailUncertainty { get; init; } = new Dictionary<string, float>();

        /// <summary>
        /// Zeigt das Endfenster eine <b>statistisch gesicherte</b> Drift (Trend der Blockmittel größer als
        /// das Dreifache seiner Unsicherheit)? Falsch heißt: der Wert schwankt um einen festen Mittelwert
        /// (instationär, aber stabil), auch wenn die einfache Drift-Prüfung anschlägt.
        /// </summary>
        public IReadOnlyDictionary<string, bool> TrendSignificant { get; init; } = new Dictionary<string, bool>();
    }

    /// <summary>
    /// Reine Analysefunktion für den SU2-Konvergenzverlauf ohne Datei- oder Konsolen-I/O (TODO-35).
    /// </summary>
    public static class Su2HistoryAnalyzer
    {
        /// <summary>Anzahl der Blöcke für Blockmittel (Trend-Test und Unsicherheit).</summary>
        public const int Batches = 8;

        /// <summary>
        /// Wertet den Verlauf einer SU2-Simulation (<c>history.csv</c>) rein rechnerisch aus.
        /// </summary>
        /// <param name="lines">Kopfzeile und Datenzeilen der CSV-Datei.</param>
        /// <param name="options">Konvergenzgrenzen und zu prüfende Spaltennamen.</param>
        /// <returns>Die ermittelten Kennwerte zu Iterationsanzahl, Konvergenz, Residuenabfall und Schwingungen.</returns>
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
            // SU2 zählt innere Iterationen ab 0 (0-basiert). Wenn eine Iterationsspalte (Inner_Iter, ITER)
            // vorhanden ist und in der letzten Zeile der ganzzahlige Wert n steht, sind n + 1 Iterationen gerechnet worden.
            // Fehlt die Spalte oder ist sie nicht lesbar, wird die Anzahl der Datenzeilen verwendet.
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
            // Dekaden, um die das erste RMS-Residuum (Spalte beginnt mit "rms[") zwischen erster und letzter Zeile gefallen ist.
            // In SU2 sind rms[...]-Werte bereits dekadische Logarithmen (log10), daher entspricht der Abfall first - last.
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
            // Über die letzten CauchyElems Zeilen (oder weniger, falls nicht genug Zeilen vorhanden).
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

                // Endfenster: nie mehr als die halbe Historie (TODO-W3).
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
        /// Blockmittel-Statistik: Das Fenster wird in <see cref="Batches"/> gleich lange Blöcke geteilt.
        /// Durch die Blockmittel wird eine Gerade gelegt. <paramref name="significant"/> ist wahr, wenn
        /// ihre Steigung mehr als das Dreifache ihres Standardfehlers beträgt (gesicherte Drift).
        /// <paramref name="standardError"/> ist der Standardfehler des Fenstermittels aus der Streuung
        /// der Blockmittel um die Gerade. Blockmittel statt Einzelwerte, weil aufeinanderfolgende
        /// Iterationen stark voneinander abhängen.
        /// </summary>
        public static bool BatchStatistics(IReadOnlyList<float> values, out double standardError, out bool significant)
        {
            standardError = double.NaN;
            significant = false;

            int size = values.Count / Batches;
            if (size < 2) return false;

            var means = new double[Batches];
            int offset = values.Count - size * Batches;   // Rest vorne weglassen (Anlauf)
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

            // Untergrenze gegen Division durch null bei exakt linearen oder konstanten Verläufen
            double floor = 1e-9 * Math.Max(Math.Abs(meanY), 1e-12);
            double slopeError = Math.Max(residualSd, floor) / Math.Sqrt(sxx);

            significant = Math.Abs(slope) > 3.0 * slopeError && Math.Abs(slope) * (Batches - 1) > floor * 10;
            standardError = residualSd / Math.Sqrt(Batches);
            return true;
        }

        /// <summary>Spannweite der Abweichungen von der Ausgleichsgeraden (kleinste Quadrate).</summary>
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
    }
}
