using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Statistische Auswertung des dimensionslosen Wandabstands y+ über die Bauteiloberfläche (TODO-41).
    /// </summary>
    /// <param name="SampleCount">Anzahl der ausgewerteten Oberflächenpunkte bzw. Knoten.</param>
    /// <param name="Min">Minimaler y+-Wert an der Wand.</param>
    /// <param name="Median">Median (50 %-Quantil) von y+.</param>
    /// <param name="P95">95 %-Quantil (P95) von y+.</param>
    /// <param name="Max">Maximaler y+-Wert an der Wand.</param>
    public sealed record YPlusStatistics(
        int SampleCount,
        float Min,
        float Median,
        float P95,
        float Max);

    /// <summary>
    /// Liest und analysiert Wandwerte (insbesondere y+) aus SU2-Oberflächendateien (surface.csv bzw. surface.vtu).
    /// </summary>
    public static class Su2SurfaceAnalyzer
    {
        /// <summary>
        /// Wertet ein Zeilen-Array aus einer SU2-surface.csv aus und berechnet die y+-Statistik.
        /// </summary>
        /// <param name="lines">Zeilen der CSV-Datei (Kopfzeile + Datenzeilen).</param>
        /// <returns>Die berechneten Statistiken, oder <c>null</c>, wenn keine Daten oder Spalte vorhanden sind.</returns>
        public static YPlusStatistics? EvaluateSurfaceCsv(string[] lines)
        {
            if (lines == null || lines.Length < 2)
                return null;

            string[] headers = Su2Solver.SplitCsvLine(lines[0]);
            int yPlusCol = FindYPlusColumn(headers);
            if (yPlusCol < 0)
                return null;

            var values = new List<float>(lines.Length - 1);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                string[] fields = Su2Solver.SplitCsvLine(line);
                if (yPlusCol < fields.Length &&
                    float.TryParse(fields[yPlusCol], NumberStyles.Float, CultureInfo.InvariantCulture, out float val) &&
                    float.IsFinite(val) && val >= 0f)
                {
                    values.Add(val);
                }
            }

            if (values.Count == 0)
                return null;

            return ComputeStatistics(values);
        }

        /// <summary>
        /// Sucht in einem Arbeitsverzeichnis nach <c>surface.csv</c> (oder <c>surface_flow.csv</c>) und
        /// optional als Rückfallebene nach <c>surface.vtu</c>, um die y+-Statistik zu bestimmen.
        /// </summary>
        public static YPlusStatistics? EvaluateFromDirectory(string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                return null;

            // 1. Primär: surface.csv oder surface_flow.csv
            string[] csvCandidates = { "surface.csv", "surface_flow.csv" };
            foreach (string file in csvCandidates)
            {
                string path = Path.Combine(workingDirectory, file);
                if (File.Exists(path))
                {
                    try
                    {
                        string[] lines = File.ReadAllLines(path);
                        var stats = EvaluateSurfaceCsv(lines);
                        if (stats != null) return stats;
                    }
                    catch
                    {
                        // Defensiver Fallback auf nächsten Kandidaten
                    }
                }
            }

            // 2. Rückfallebene: surface.vtu
            string[] vtuCandidates = { "surface.vtu", "surface_flow.vtu" };
            foreach (string file in vtuCandidates)
            {
                string path = Path.Combine(workingDirectory, file);
                if (File.Exists(path))
                {
                    try
                    {
                        var stats = EvaluateSurfaceVtu(path);
                        if (stats != null) return stats;
                    }
                    catch
                    {
                        // Defensiv ignorieren
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// Rückfall-Leser für VTK XML UnstructuredGrid (.vtu), sucht nach dem DataArray "Y_Plus" in PointData.
        /// Unterstützt sowohl ASCII als auch appended raw binary (UInt32 / UInt64 Header).
        /// </summary>
        public static YPlusStatistics? EvaluateSurfaceVtu(string vtuPath)
        {
            if (string.IsNullOrWhiteSpace(vtuPath) || !File.Exists(vtuPath))
                return null;

            try
            {
                byte[] fileBytes = File.ReadAllBytes(vtuPath);
                string text = Encoding.UTF8.GetString(fileBytes);

                // Suche nach DataArray mit Name="Y_Plus" (case-insensitive)
                var match = Regex.Match(text, @"<DataArray[^>]*Name\s*=\s*[""'](Y_Plus|y_plus|YPlus)[""'][^>]*>", RegexOptions.IgnoreCase);
                if (!match.Success)
                    return null;

                string tag = match.Value;
                bool isAppended = tag.Contains("format=\"appended\"", StringComparison.OrdinalIgnoreCase) ||
                                  tag.Contains("format='appended'", StringComparison.OrdinalIgnoreCase);
                bool isFloat64 = tag.Contains("type=\"Float64\"", StringComparison.OrdinalIgnoreCase) ||
                                 tag.Contains("type='Float64'", StringComparison.OrdinalIgnoreCase);

                if (isAppended)
                {
                    var offsetMatch = Regex.Match(tag, @"offset\s*=\s*[""'](\d+)[""']", RegexOptions.IgnoreCase);
                    if (!offsetMatch.Success) return null;
                    if (!long.TryParse(offsetMatch.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset))
                        return null;

                    // Suche den Start von <AppendedData encoding="raw">_
                    int appIdx = text.IndexOf("<AppendedData", StringComparison.OrdinalIgnoreCase);
                    if (appIdx < 0) return null;
                    int underscoreIdx = text.IndexOf('_', appIdx);
                    if (underscoreIdx < 0) return null;

                    long dataStart = underscoreIdx + 1 + offset;
                    if (dataStart >= fileBytes.Length) return null;

                    // Header-Typ ermitteln (Standard in neuem VTK ist UInt64, sonst UInt32)
                    bool isHeader64 = text.Contains("header_type=\"UInt64\"", StringComparison.OrdinalIgnoreCase) ||
                                      text.Contains("header_type='UInt64'", StringComparison.OrdinalIgnoreCase);

                    long numBytes;
                    long arrayStart;
                    if (isHeader64)
                    {
                        if (dataStart + 8 > fileBytes.Length) return null;
                        numBytes = (long)BitConverter.ToUInt64(fileBytes, (int)dataStart);
                        arrayStart = dataStart + 8;
                    }
                    else
                    {
                        if (dataStart + 4 > fileBytes.Length) return null;
                        numBytes = BitConverter.ToUInt32(fileBytes, (int)dataStart);
                        arrayStart = dataStart + 4;
                    }

                    if (arrayStart + numBytes > fileBytes.Length)
                        numBytes = fileBytes.Length - arrayStart;

                    var values = new List<float>();
                    if (isFloat64)
                    {
                        int count = (int)(numBytes / 8);
                        for (int i = 0; i < count; i++)
                        {
                            double d = BitConverter.ToDouble(fileBytes, (int)(arrayStart + i * 8));
                            if (double.IsFinite(d) && d >= 0)
                                values.Add((float)d);
                        }
                    }
                    else
                    {
                        int count = (int)(numBytes / 4);
                        for (int i = 0; i < count; i++)
                        {
                            float f = BitConverter.ToSingle(fileBytes, (int)(arrayStart + i * 4));
                            if (float.IsFinite(f) && f >= 0)
                                values.Add(f);
                        }
                    }

                    if (values.Count == 0) return null;
                    return ComputeStatistics(values);
                }
                else
                {
                    // ASCII format: suche Inhalt zwischen <DataArray ...> und </DataArray>
                    int startContent = match.Index + match.Length;
                    int endContent = text.IndexOf("</DataArray>", startContent, StringComparison.OrdinalIgnoreCase);
                    if (endContent < 0) return null;

                    string content = text.Substring(startContent, endContent - startContent);
                    string[] tokens = content.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    var values = new List<float>(tokens.Length);
                    foreach (string t in tokens)
                    {
                        if (float.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out float f) &&
                            float.IsFinite(f) && f >= 0f)
                        {
                            values.Add(f);
                        }
                    }

                    if (values.Count == 0) return null;
                    return ComputeStatistics(values);
                }
            }
            catch
            {
                return null;
            }
        }

        private static int FindYPlusColumn(string[] headers)
        {
            string[] candidates = { "Y_Plus", "y_plus", "YPlus" };
            foreach (string name in candidates)
            {
                int idx = Su2Solver.FindColumn(headers, name, out _);
                if (idx >= 0) return idx;
            }

            for (int i = 0; i < headers.Length; i++)
            {
                string h = headers[i].Trim();
                if (h.Equals("Y_Plus", StringComparison.OrdinalIgnoreCase) ||
                    h.Equals("y_plus", StringComparison.OrdinalIgnoreCase) ||
                    h.Equals("YPlus", StringComparison.OrdinalIgnoreCase) ||
                    h.StartsWith("Y_Plus(", StringComparison.OrdinalIgnoreCase))
                {
                    return i;
                }
            }

            return -1;
        }

        private static YPlusStatistics ComputeStatistics(List<float> values)
        {
            values.Sort();
            int count = values.Count;
            float min = values[0];
            float max = values[count - 1];
            float median = ComputePercentile(values, 50.0f);
            float p95 = ComputePercentile(values, 95.0f);

            return new YPlusStatistics(count, min, median, p95, max);
        }

        /// <summary>
        /// Berechnet ein beliebiges Perzentil aus einer aufsteigend sortierten Liste per linearer Interpolation.
        /// </summary>
        public static float ComputePercentile(List<float> sorted, float percentile)
        {
            if (sorted == null || sorted.Count == 0) return 0f;
            if (sorted.Count == 1) return sorted[0];
            if (percentile <= 0f) return sorted[0];
            if (percentile >= 100f) return sorted[sorted.Count - 1];

            float rank = (percentile / 100.0f) * (sorted.Count - 1);
            int lower = (int)Math.Floor(rank);
            int upper = (int)Math.Ceiling(rank);
            float weight = rank - lower;

            return sorted[lower] * (1.0f - weight) + sorted[upper] * weight;
        }
    }
}
