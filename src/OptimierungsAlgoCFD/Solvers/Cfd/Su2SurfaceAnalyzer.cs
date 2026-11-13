using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
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
    /// Aufschlüsselung des Widerstandsbeiwerts in Druckanteil (<c>CD_p</c>) und Reibungsanteil (<c>CD_v</c>)
    /// durch Integration über die Bauteiloberfläche in <c>surface.vtu</c>.
    /// </summary>
    public sealed record AerodynamicDragBreakdown(
        double PressureDragCoeff,
        double ViscousDragCoeff,
        double TotalDragCoeff,
        int ElementCount,
        bool Is2D);

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

        /// <summary>
        /// Sucht im Arbeitsverzeichnis nach <c>surface.vtu</c> oder <c>surface_flow.vtu</c> und integriert
        /// <c>CD_p</c> und <c>CD_v</c> über die Bauteiloberfläche.
        /// </summary>
        public static AerodynamicDragBreakdown? EvaluateDragBreakdownFromDirectory(
            string workingDirectory,
            float refAreaOrChordM,
            float angleOfAttackDeg,
            int dimensions)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                return null;

            string[] candidates = { "surface.vtu", "surface_flow.vtu" };
            foreach (string file in candidates)
            {
                string path = Path.Combine(workingDirectory, file);
                if (File.Exists(path))
                {
                    AerodynamicDragBreakdown? result = EvaluateAerodynamicDragBreakdown(
                        path, refAreaOrChordM, angleOfAttackDeg, dimensions);
                    if (result != null)
                        return result;
                }
            }

            return null;
        }

        /// <summary>
        /// Integriert <c>Pressure_Coefficient</c> (<c>CD_p</c>) und <c>Skin_Friction_Coefficient</c> (<c>CD_v</c>)
        /// über die Oberflächenzellen einer SU2-<c>.vtu</c>-Datei (ASCII oder appended raw binary,
        /// <c>Float32</c>/<c>Float64</c>, <c>Int32</c>/<c>Int64</c>/<c>UInt8</c>, <c>UInt32</c>/<c>UInt64</c>-Header).
        /// </summary>
        public static AerodynamicDragBreakdown? EvaluateAerodynamicDragBreakdown(
            string vtuPath,
            float refAreaOrChordM,
            float angleOfAttackDeg,
            int dimensions)
        {
            if (string.IsNullOrWhiteSpace(vtuPath) || !File.Exists(vtuPath))
                return null;
            if (refAreaOrChordM <= 0f || !float.IsFinite(refAreaOrChordM))
                return null;

            try
            {
                byte[] fileBytes = File.ReadAllBytes(vtuPath);
                int appIdx = FindAsciiBytesIgnoreCase(fileBytes, "<AppendedData");
                int underscoreByteIdx = appIdx >= 0 ? Array.IndexOf(fileBytes, (byte)'_', appIdx) : -1;

                int headerLength = appIdx >= 0 ? appIdx : fileBytes.Length;
                string headerXml = Encoding.UTF8.GetString(fileBytes, 0, headerLength);

                bool isHeader64 = headerXml.Contains("header_type=\"UInt64\"", StringComparison.OrdinalIgnoreCase)
                                  || headerXml.Contains("header_type='UInt64'", StringComparison.OrdinalIgnoreCase);

                if (!TryExtractSection(headerXml, "Points", out string pointsSection))
                    return null;
                if (!TryExtractSection(headerXml, "Cells", out string cellsSection))
                    return null;
                if (!TryExtractSection(headerXml, "PointData", out string pointDataSection))
                    return null;

                List<VtuDataArrayTag> pointsArrays = ParseDataArrays(pointsSection);
                if (pointsArrays.Count == 0) return null;

                List<VtuDataArrayTag> cellArrays = ParseDataArrays(cellsSection);
                VtuDataArrayTag? connTag = FindDataArray(cellArrays, "connectivity");
                VtuDataArrayTag? offsetsTag = FindDataArray(cellArrays, "offsets");
                VtuDataArrayTag? typesTag = FindDataArray(cellArrays, "types");
                if (connTag == null || offsetsTag == null || typesTag == null)
                    return null;

                List<VtuDataArrayTag> pointArrays = ParseDataArrays(pointDataSection);
                VtuDataArrayTag? cpTag = FindDataArray(pointArrays, "Pressure_Coefficient");
                VtuDataArrayTag? cfTag = FindDataArray(pointArrays, "Skin_Friction_Coefficient");
                if (cpTag == null || cfTag == null)
                    return null;

                double[]? points = ReadDoubleArray(pointsArrays[0], fileBytes, underscoreByteIdx, isHeader64);
                long[]? connectivity = ReadLongArray(connTag, fileBytes, underscoreByteIdx, isHeader64);
                long[]? offsets = ReadLongArray(offsetsTag, fileBytes, underscoreByteIdx, isHeader64);
                long[]? types = ReadLongArray(typesTag, fileBytes, underscoreByteIdx, isHeader64);
                double[]? cp = ReadDoubleArray(cpTag, fileBytes, underscoreByteIdx, isHeader64);
                double[]? cf = ReadDoubleArray(cfTag, fileBytes, underscoreByteIdx, isHeader64);

                if (points == null || connectivity == null || offsets == null || types == null || cp == null || cf == null)
                    return null;

                if (points.Length < 3 || points.Length % 3 != 0)
                    return null;

                int numPoints = points.Length / 3;
                if (cp.Length < numPoints)
                    return null;

                int cfComponents = cfTag.NumberOfComponents > 0
                    ? cfTag.NumberOfComponents
                    : (cf.Length >= numPoints * 3 ? 3 : (cf.Length >= numPoints * 2 ? 2 : 0));
                if (cfComponents < 2 || cf.Length < numPoints * cfComponents)
                    return null;

                int numCells = Math.Min(offsets.Length, types.Length);
                if (numCells == 0)
                    return null;

                var (vx, vy, vz) = Su2ConfigGenerator.ResolveVelocityVector(1.0f, angleOfAttackDeg, dimensions);
                double windLen = Math.Sqrt((double)vx * vx + (double)vy * vy + (double)vz * vz);
                if (windLen <= 0.0 || !double.IsFinite(windLen))
                    return null;

                double dxWind = vx / windLen;
                double dyWind = vy / windLen;
                double dzWind = vz / windLen;

                bool is2D = dimensions == 2;
                int elementCount = 0;

                if (is2D)
                {
                    double shoelaceSum = 0.0;
                    double pressureWindSum = 0.0;
                    double viscousWindSum = 0.0;

                    for (int c = 0; c < numCells; c++)
                    {
                        long start = c == 0 ? 0 : offsets[c - 1];
                        long end = offsets[c];
                        if (start < 0 || end > connectivity.Length || end - start < 2)
                            continue;
                        if (types[c] != 3)
                            continue;

                        long i0 = connectivity[start];
                        long i1 = connectivity[start + 1];
                        if (i0 < 0 || i0 >= numPoints || i1 < 0 || i1 >= numPoints)
                            continue;

                        double x0 = points[i0 * 3];
                        double y0 = points[i0 * 3 + 1];
                        double x1 = points[i1 * 3];
                        double y1 = points[i1 * 3 + 1];

                        double dxSeg = x1 - x0;
                        double dySeg = y1 - y0;
                        double segLen = Math.Sqrt(dxSeg * dxSeg + dySeg * dySeg);
                        if (segLen <= 0.0 || !double.IsFinite(segLen))
                            continue;

                        double cp0 = cp[i0];
                        double cp1 = cp[i1];
                        double cfx0 = cf[i0 * cfComponents];
                        double cfy0 = cf[i0 * cfComponents + 1];
                        double cfz0 = cfComponents >= 3 ? cf[i0 * cfComponents + 2] : 0.0;
                        double cfx1 = cf[i1 * cfComponents];
                        double cfy1 = cf[i1 * cfComponents + 1];
                        double cfz1 = cfComponents >= 3 ? cf[i1 * cfComponents + 2] : 0.0;

                        if (!double.IsFinite(cp0) || !double.IsFinite(cp1) ||
                            !double.IsFinite(cfx0) || !double.IsFinite(cfy0) || !double.IsFinite(cfz0) ||
                            !double.IsFinite(cfx1) || !double.IsFinite(cfy1) || !double.IsFinite(cfz1))
                            continue;

                        shoelaceSum += x0 * y1 - x1 * y0;

                        double nx = dySeg;
                        double ny = -dxSeg;
                        double cpAvg = 0.5 * (cp0 + cp1);
                        pressureWindSum += cpAvg * (nx * dxWind + ny * dyWind);

                        double cfxAvg = 0.5 * (cfx0 + cfx1);
                        double cfyAvg = 0.5 * (cfy0 + cfy1);
                        double cfzAvg = 0.5 * (cfz0 + cfz1);
                        viscousWindSum += (cfxAvg * dxWind + cfyAvg * dyWind + cfzAvg * dzWind) * segLen;

                        elementCount++;
                    }

                    if (elementCount == 0)
                        return null;

                    double orient = shoelaceSum >= 0.0 ? 1.0 : -1.0;
                    double cdP = (-orient * pressureWindSum) / refAreaOrChordM;
                    double cdV = viscousWindSum / refAreaOrChordM;
                    if (!double.IsFinite(cdP) || !double.IsFinite(cdV))
                        return null;

                    return new AerodynamicDragBreakdown(cdP, cdV, cdP + cdV, elementCount, true);
                }
                else
                {
                    double signedVolSum = 0.0;
                    double pressureWindSum = 0.0;
                    double viscousWindSum = 0.0;

                    for (int c = 0; c < numCells; c++)
                    {
                        long start = c == 0 ? 0 : offsets[c - 1];
                        long end = offsets[c];
                        if (start < 0 || end > connectivity.Length)
                            continue;

                        long cellType = types[c];
                        long vertexCount = end - start;
                        if (cellType == 5 && vertexCount >= 3)
                        {
                            long i0 = connectivity[start];
                            long i1 = connectivity[start + 1];
                            long i2 = connectivity[start + 2];
                            if (AccumulateTriangle(
                                    i0, i1, i2, numPoints, points, cp, cf, cfComponents,
                                    dxWind, dyWind, dzWind,
                                    ref signedVolSum, ref pressureWindSum, ref viscousWindSum))
                            {
                                elementCount++;
                            }
                        }
                        else if (cellType == 9 && vertexCount >= 4)
                        {
                            long i0 = connectivity[start];
                            long i1 = connectivity[start + 1];
                            long i2 = connectivity[start + 2];
                            long i3 = connectivity[start + 3];
                            bool triA = AccumulateTriangle(
                                i0, i1, i2, numPoints, points, cp, cf, cfComponents,
                                dxWind, dyWind, dzWind,
                                ref signedVolSum, ref pressureWindSum, ref viscousWindSum);
                            bool triB = AccumulateTriangle(
                                i0, i2, i3, numPoints, points, cp, cf, cfComponents,
                                dxWind, dyWind, dzWind,
                                ref signedVolSum, ref pressureWindSum, ref viscousWindSum);
                            if (triA || triB)
                            {
                                elementCount++;
                            }
                        }
                    }

                    if (elementCount == 0)
                        return null;

                    double orient = signedVolSum >= 0.0 ? 1.0 : -1.0;
                    double cdP = (-orient * pressureWindSum) / refAreaOrChordM;
                    double cdV = viscousWindSum / refAreaOrChordM;
                    if (!double.IsFinite(cdP) || !double.IsFinite(cdV))
                        return null;

                    return new AerodynamicDragBreakdown(cdP, cdV, cdP + cdV, elementCount, false);
                }
            }
            catch
            {
                return null;
            }
        }

        private static bool AccumulateTriangle(
            long i0, long i1, long i2,
            int numPoints,
            double[] points,
            double[] cp,
            double[] cf,
            int cfComponents,
            double dxWind, double dyWind, double dzWind,
            ref double signedVolSum,
            ref double pressureWindSum,
            ref double viscousWindSum)
        {
            if (i0 < 0 || i0 >= numPoints ||
                i1 < 0 || i1 >= numPoints ||
                i2 < 0 || i2 >= numPoints)
                return false;

            double x0 = points[i0 * 3], y0 = points[i0 * 3 + 1], z0 = points[i0 * 3 + 2];
            double x1 = points[i1 * 3], y1 = points[i1 * 3 + 1], z1 = points[i1 * 3 + 2];
            double x2 = points[i2 * 3], y2 = points[i2 * 3 + 1], z2 = points[i2 * 3 + 2];

            double ux = x1 - x0, uy = y1 - y0, uz = z1 - z0;
            double vx = x2 - x0, vy = y2 - y0, vz = z2 - z0;

            double sx = 0.5 * (uy * vz - uz * vy);
            double sy = 0.5 * (uz * vx - ux * vz);
            double sz = 0.5 * (ux * vy - uy * vx);
            double area = Math.Sqrt(sx * sx + sy * sy + sz * sz);
            if (area <= 0.0 || !double.IsFinite(area))
                return false;

            double cp0 = cp[i0], cp1 = cp[i1], cp2 = cp[i2];
            if (!double.IsFinite(cp0) || !double.IsFinite(cp1) || !double.IsFinite(cp2))
                return false;

            double cfx0 = cf[i0 * cfComponents], cfy0 = cf[i0 * cfComponents + 1], cfz0 = cfComponents >= 3 ? cf[i0 * cfComponents + 2] : 0.0;
            double cfx1 = cf[i1 * cfComponents], cfy1 = cf[i1 * cfComponents + 1], cfz1 = cfComponents >= 3 ? cf[i1 * cfComponents + 2] : 0.0;
            double cfx2 = cf[i2 * cfComponents], cfy2 = cf[i2 * cfComponents + 1], cfz2 = cfComponents >= 3 ? cf[i2 * cfComponents + 2] : 0.0;

            if (!double.IsFinite(cfx0) || !double.IsFinite(cfy0) || !double.IsFinite(cfz0) ||
                !double.IsFinite(cfx1) || !double.IsFinite(cfy1) || !double.IsFinite(cfz1) ||
                !double.IsFinite(cfx2) || !double.IsFinite(cfy2) || !double.IsFinite(cfz2))
                return false;

            double cx = (x0 + x1 + x2) / 3.0;
            double cy = (y0 + y1 + y2) / 3.0;
            double cz = (z0 + z1 + z2) / 3.0;
            signedVolSum += (cx * sx + cy * sy + cz * sz) / 3.0;

            double cpAvg = (cp0 + cp1 + cp2) / 3.0;
            pressureWindSum += cpAvg * (sx * dxWind + sy * dyWind + sz * dzWind);

            double cfxAvg = (cfx0 + cfx1 + cfx2) / 3.0;
            double cfyAvg = (cfy0 + cfy1 + cfy2) / 3.0;
            double cfzAvg = (cfz0 + cfz1 + cfz2) / 3.0;
            viscousWindSum += (cfxAvg * dxWind + cfyAvg * dyWind + cfzAvg * dzWind) * area;

            return true;
        }

        private sealed record VtuDataArrayTag(
            string Name,
            string DataType,
            string Format,
            long Offset,
            int NumberOfComponents,
            string InlineContent);

        private static int FindAsciiBytesIgnoreCase(byte[] haystack, string asciiNeedle)
        {
            byte[] lower = Encoding.ASCII.GetBytes(asciiNeedle.ToLowerInvariant());
            byte[] upper = Encoding.ASCII.GetBytes(asciiNeedle.ToUpperInvariant());
            int limit = haystack.Length - lower.Length;
            for (int i = 0; i <= limit; i++)
            {
                bool match = true;
                for (int j = 0; j < lower.Length; j++)
                {
                    byte b = haystack[i + j];
                    if (b != lower[j] && b != upper[j])
                    {
                        match = false;
                        break;
                    }
                }
                if (match) return i;
            }
            return -1;
        }

        private static bool TryExtractSection(string xml, string tagName, out string sectionContent)
        {
            sectionContent = string.Empty;
            var m = Regex.Match(
                xml,
                $@"<{tagName}\b[^>]*>(.*?)</{tagName}>",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);
            if (!m.Success) return false;
            sectionContent = m.Groups[1].Value;
            return true;
        }

        private static List<VtuDataArrayTag> ParseDataArrays(string sectionXml)
        {
            var result = new List<VtuDataArrayTag>();
            var matches = Regex.Matches(
                sectionXml,
                @"<DataArray\b([^>]*?)(?:/>|>(.*?)</DataArray>)",
                RegexOptions.IgnoreCase | RegexOptions.Singleline);

            foreach (Match m in matches)
            {
                string attrs = m.Groups[1].Value;
                string inlineContent = m.Groups[2].Success ? m.Groups[2].Value : string.Empty;

                string name = ExtractAttribute(attrs, "Name") ?? string.Empty;
                string type = ExtractAttribute(attrs, "type") ?? string.Empty;
                string format = ExtractAttribute(attrs, "format") ?? "ascii";
                long offset = long.TryParse(ExtractAttribute(attrs, "offset"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long off)
                    ? off
                    : 0L;
                int components = int.TryParse(ExtractAttribute(attrs, "NumberOfComponents"), NumberStyles.Integer, CultureInfo.InvariantCulture, out int comp)
                    ? comp
                    : 0;

                result.Add(new VtuDataArrayTag(name, type, format, offset, components, inlineContent));
            }

            return result;
        }

        private static VtuDataArrayTag? FindDataArray(List<VtuDataArrayTag> arrays, string name)
        {
            foreach (var arr in arrays)
            {
                if (arr.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
                    return arr;
            }
            return null;
        }

        private static string? ExtractAttribute(string attrs, string attrName)
        {
            var m = Regex.Match(
                attrs,
                $@"\b{attrName}\s*=\s*[""']([^""']*)[""']",
                RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        private static bool TryGetBinarySlice(
            VtuDataArrayTag tag,
            byte[] fileBytes,
            int underscoreByteIdx,
            bool isHeader64,
            out int payloadStart,
            out int payloadLength)
        {
            payloadStart = 0;
            payloadLength = 0;
            if (underscoreByteIdx < 0) return false;

            long dataStart = underscoreByteIdx + 1L + tag.Offset;
            if (dataStart < 0 || dataStart >= fileBytes.Length) return false;

            long numBytes;
            long start;
            if (isHeader64)
            {
                if (dataStart + 8 > fileBytes.Length) return false;
                numBytes = (long)BitConverter.ToUInt64(fileBytes, (int)dataStart);
                start = dataStart + 8;
            }
            else
            {
                if (dataStart + 4 > fileBytes.Length) return false;
                numBytes = BitConverter.ToUInt32(fileBytes, (int)dataStart);
                start = dataStart + 4;
            }

            if (numBytes < 0 || start > fileBytes.Length) return false;
            if (start + numBytes > fileBytes.Length)
                numBytes = fileBytes.Length - start;

            payloadStart = (int)start;
            payloadLength = (int)numBytes;
            return true;
        }

        private static double[]? ReadDoubleArray(
            VtuDataArrayTag tag,
            byte[] fileBytes,
            int underscoreByteIdx,
            bool isHeader64)
        {
            if (tag.Format.Equals("appended", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetBinarySlice(tag, fileBytes, underscoreByteIdx, isHeader64, out int start, out int len))
                    return null;

                if (tag.DataType.Equals("Float64", StringComparison.OrdinalIgnoreCase))
                {
                    int count = len / 8;
                    double[] arr = new double[count];
                    for (int i = 0; i < count; i++)
                        arr[i] = BitConverter.ToDouble(fileBytes, start + i * 8);
                    return arr;
                }
                if (tag.DataType.Equals("Float32", StringComparison.OrdinalIgnoreCase))
                {
                    int count = len / 4;
                    double[] arr = new double[count];
                    for (int i = 0; i < count; i++)
                        arr[i] = BitConverter.ToSingle(fileBytes, start + i * 4);
                    return arr;
                }
                return null;
            }

            string[] tokens = tag.InlineContent.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            double[] values = new double[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!double.TryParse(tokens[i], NumberStyles.Float, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            }
            return values;
        }

        private static long[]? ReadLongArray(
            VtuDataArrayTag tag,
            byte[] fileBytes,
            int underscoreByteIdx,
            bool isHeader64)
        {
            if (tag.Format.Equals("appended", StringComparison.OrdinalIgnoreCase))
            {
                if (!TryGetBinarySlice(tag, fileBytes, underscoreByteIdx, isHeader64, out int start, out int len))
                    return null;

                if (tag.DataType.Equals("Int32", StringComparison.OrdinalIgnoreCase))
                {
                    int count = len / 4;
                    long[] arr = new long[count];
                    for (int i = 0; i < count; i++)
                        arr[i] = BitConverter.ToInt32(fileBytes, start + i * 4);
                    return arr;
                }
                if (tag.DataType.Equals("UInt32", StringComparison.OrdinalIgnoreCase))
                {
                    int count = len / 4;
                    long[] arr = new long[count];
                    for (int i = 0; i < count; i++)
                        arr[i] = BitConverter.ToUInt32(fileBytes, start + i * 4);
                    return arr;
                }
                if (tag.DataType.Equals("Int64", StringComparison.OrdinalIgnoreCase))
                {
                    int count = len / 8;
                    long[] arr = new long[count];
                    for (int i = 0; i < count; i++)
                        arr[i] = BitConverter.ToInt64(fileBytes, start + i * 8);
                    return arr;
                }
                if (tag.DataType.Equals("UInt64", StringComparison.OrdinalIgnoreCase))
                {
                    int count = len / 8;
                    long[] arr = new long[count];
                    for (int i = 0; i < count; i++)
                        arr[i] = (long)BitConverter.ToUInt64(fileBytes, start + i * 8);
                    return arr;
                }
                if (tag.DataType.Equals("UInt8", StringComparison.OrdinalIgnoreCase))
                {
                    long[] arr = new long[len];
                    for (int i = 0; i < len; i++)
                        arr[i] = fileBytes[start + i];
                    return arr;
                }
                if (tag.DataType.Equals("Int8", StringComparison.OrdinalIgnoreCase))
                {
                    long[] arr = new long[len];
                    for (int i = 0; i < len; i++)
                        arr[i] = (sbyte)fileBytes[start + i];
                    return arr;
                }
                return null;
            }

            string[] tokens = tag.InlineContent.Split(new[] { ' ', '\t', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            long[] values = new long[tokens.Length];
            for (int i = 0; i < tokens.Length; i++)
            {
                if (!long.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out values[i]))
                    return null;
            }
            return values;
        }
    }
}
