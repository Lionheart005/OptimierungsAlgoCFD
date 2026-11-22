using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.Planar2D;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Generiert 2D-<c>.geo</c>-Skripte für Gmsh (Kontur-Splines, Fernfeldkreis, Quad-BoundaryLayer,
    /// Nahfeld-, Wake- und Detail-Box) und bereinigt den 2D-Fluid-Marker aus <c>.su2</c>-Dateien.
    /// </summary>
    internal static class Gmsh2DGeoScriptBuilder
    {
        public const string FluidMarkerName = "Fluid";

        public static void WriteGeoScript(
            Gmsh2DMesherOptions options,
            FlowConditions? flow,
            string geoScriptPath,
            string contourJsonPath,
            SimulationConfig config,
            float scaleFactor,
            float refinementFactor = 1.0f,
            float thicknessScale = 1.0f)
        {
            if (!File.Exists(contourJsonPath))
                throw new FileNotFoundException($"2D-Konturdatei nicht gefunden: {contourJsonPath}", contourJsonPath);

            string F(float v) => WakeAndDetailBoxBuilder.Format(v);
            ContourFile2D contourFile = Shape2D.LoadContour(contourJsonPath);
            BBox2 bounds = ComputeBounds(contourFile);

            float chordGeo = (!bounds.IsEmpty && bounds.Size.X > 0f) ? bounds.Size.X : 50.0f * scaleFactor;
            float effScale = scaleFactor > 0f ? scaleFactor : 1.0f;
            float chordMm = chordGeo / effScale;

            FlowConditions effectiveFlow = flow ?? FlowConditions.From(config, new Su2SolverOptions());
            var spec = BoundaryLayerCalculator.Calculate(
                effectiveFlow.Density, effectiveFlow.Velocity, effectiveFlow.DynamicViscosity, chordMm,
                options.TargetYPlus, options.BoundaryLayerGrowthRate,
                options.BoundaryLayerMinLayers, options.BoundaryLayerLayers);

            // Regel F4: Erste Zellhöhe y1 hängt NUR an der Physik (Ziel-y+), NICHT am RefinementFactor!
            float y1Geo = spec.FirstLayerHeightMm * scaleFactor;

            float factor = refinementFactor > 0f ? refinementFactor : 1.0f;
            float surfaceSize = (options.SurfaceCellSizeMm > 0f
                ? options.SurfaceCellSizeMm * scaleFactor
                : options.SurfaceCellSizeFactor * chordGeo) * factor;

            float nearfieldSize = options.NearfieldCellSizeFactor * chordGeo * factor;
            float farfieldSize = options.FarfieldCellSizeFactor * chordGeo * factor;
            float wakeSize = options.WakeCellSizeFactor * chordGeo * factor;

            float distMin = 0.1f * chordGeo;
            float growthDistance = (options.NearfieldGrowthDistanceFactor > 0f
                ? options.NearfieldGrowthDistanceFactor
                : 0.4f * options.FarfieldRadiusFactor) * chordGeo;
            float distMax = Math.Max(0.8f * chordGeo, growthDistance);

            var geoLines = new List<string>
            {
                "// =============================================================================",
                "// Gmsh 2D CFD Netz-Skript (generiert fuer SU2 2D Incompressible RANS)",
                "// =============================================================================",
                "Mesh.Optimize = 1;",
                "Mesh.OptimizeNetgen = 0;",
                string.Empty
            };

            int nextPointId = 1, nextCurveId = 1, nextLoopId = 1;
            var allBodyCurves = new List<int>();
            var allFanPointIds = new List<int>();
            var bodyLoopIds = new List<int>();

            geoLines.Add("// --- A) BAUTEIL-KONTURPUNKTE (xy-Ebene, z=0) ---");
            AppendContours(geoLines, contourFile, options, chordGeo, surfaceSize,
                ref nextPointId, ref nextCurveId, ref nextLoopId,
                allBodyCurves, allFanPointIds, bodyLoopIds);

            geoLines.Add(string.Empty);
            geoLines.Add("// --- B) FERNFELD-KREIS (Zirkulationsradius = FarfieldRadiusFactor * c) ---");

            Vector2 center = bounds.Center;
            float radius = options.FarfieldRadiusFactor * chordGeo;
            int centerPtId = nextPointId++, eastPtId = nextPointId++, northPtId = nextPointId++, westPtId = nextPointId++, southPtId = nextPointId++;

            geoLines.Add($"Point({centerPtId}) = {{ {F(center.X)}, {F(center.Y)}, 0, {F(farfieldSize)} }};");
            geoLines.Add($"Point({eastPtId}) = {{ {F(center.X + radius)}, {F(center.Y)}, 0, {F(farfieldSize)} }};");
            geoLines.Add($"Point({northPtId}) = {{ {F(center.X)}, {F(center.Y + radius)}, 0, {F(farfieldSize)} }};");
            geoLines.Add($"Point({westPtId}) = {{ {F(center.X - radius)}, {F(center.Y)}, 0, {F(farfieldSize)} }};");
            geoLines.Add($"Point({southPtId}) = {{ {F(center.X)}, {F(center.Y - radius)}, 0, {F(farfieldSize)} }};");

            int arc1 = nextCurveId++, arc2 = nextCurveId++, arc3 = nextCurveId++, arc4 = nextCurveId++;
            geoLines.Add($"Circle({arc1}) = {{ {eastPtId}, {centerPtId}, {northPtId} }};");
            geoLines.Add($"Circle({arc2}) = {{ {northPtId}, {centerPtId}, {westPtId} }};");
            geoLines.Add($"Circle({arc3}) = {{ {westPtId}, {centerPtId}, {southPtId} }};");
            geoLines.Add($"Circle({arc4}) = {{ {southPtId}, {centerPtId}, {eastPtId} }};");

            int farfieldLoopId = nextLoopId++;
            geoLines.Add($"Curve Loop({farfieldLoopId}) = {{ {arc1}, {arc2}, {arc3}, {arc4} }};");
            var farfieldCurves = new[] { arc1, arc2, arc3, arc4 };

            geoLines.Add(string.Empty);
            geoLines.Add("// --- C) 2D-FLUIDDOMAENE (Fernfeld minus Koerper) ---");
            var surfaceLoops = new List<int> { farfieldLoopId };
            surfaceLoops.AddRange(bodyLoopIds);
            geoLines.Add($"Plane Surface(1) = {{ {string.Join(", ", surfaceLoops)} }};");

            WakeAndDetailBoxBuilder.AppendSectionHeader(geoLines, "// D) STRUKTURIERTE GRENZSCHICHT (BOUNDARY LAYER QUAD-EXTRUSION)", wideBanner: true, leadingBlankLine: true);
            geoLines.Add("Field[1] = BoundaryLayer;");
            geoLines.Add($"Field[1].CurvesList = {{ {string.Join(", ", allBodyCurves)} }};");
            geoLines.Add($"Field[1].Size = {F(y1Geo)};");
            geoLines.Add($"Field[1].Ratio = {F(options.BoundaryLayerGrowthRate)};");
            geoLines.Add($"Field[1].NbLayers = {spec.LayerCount};");
            geoLines.Add($"Field[1].Thickness = {F(spec.TotalThicknessMm * scaleFactor * Math.Clamp(thicknessScale, 0.01f, 1.0f))};");
            geoLines.Add($"Field[1].SizeFar = {F(surfaceSize)};");
            geoLines.Add("Field[1].Quads = 1;");

            if (allFanPointIds.Count > 0)
            {
                string fansStr = string.Join(", ", allFanPointIds.Distinct());
                geoLines.Add($"Field[1].FanPointsList = {{ {fansStr} }};");
                geoLines.Add($"Field[1].FanNodesList = {{ {fansStr} }};");
            }

            WakeAndDetailBoxBuilder.AppendSectionHeader(geoLines, "// E) DISTANZ- UND SCHWELLENWERTFELD FUER NAHFELD", wideBanner: true, leadingBlankLine: true);
            geoLines.Add("Field[2] = Distance;");
            geoLines.Add($"Field[2].CurvesList = {{ {string.Join(", ", allBodyCurves)} }};");
            geoLines.Add("Field[3] = Threshold;");
            geoLines.Add("Field[3].InField = 2;");
            geoLines.Add($"Field[3].SizeMin = {F(nearfieldSize)};");
            geoLines.Add($"Field[3].SizeMax = {F(farfieldSize)};");
            geoLines.Add($"Field[3].DistMin = {F(distMin)};");
            geoLines.Add($"Field[3].DistMax = {F(distMax)};");

            int activeBackgroundField = 3;
            var combined = new List<int> { 3 };
            if (options.WakeRefinement)
            {
                WakeAndDetailBoxBuilder.Append2DWakeBox(geoLines, options, bounds, chordGeo, wakeSize, farfieldSize);
                combined.Add(4);
            }
            if (options.DetailBox)
            {
                WakeAndDetailBoxBuilder.Append2DDetailBox(geoLines, options, bounds, chordGeo, factor, farfieldSize);
                combined.Add(6);
            }

            if (combined.Count > 1)
            {
                WakeAndDetailBoxBuilder.AppendSectionHeader(geoLines, "// G) HINTERGRUND-FELDER INTELLIGENT KOMBINIEREN", wideBanner: true, leadingBlankLine: true);
                geoLines.Add("Field[5] = Min;");
                geoLines.Add($"Field[5].FieldsList = {{ {string.Join(", ", combined)} }};");
                activeBackgroundField = 5;
            }

            geoLines.Add($"Background Field = {activeBackgroundField};");
            geoLines.Add("BoundaryLayer Field = 1;");

            WakeAndDetailBoxBuilder.AppendSectionHeader(geoLines, "// H) PHYSIKALISCHE MARKER FUER SU2", wideBanner: true, leadingBlankLine: true);
            geoLines.Add("// Dieser Name ist der SU2-Marker: Farfield -> MARKER_FAR");
            geoLines.Add($"Physical Curve(\"Farfield\") = {{ {string.Join(", ", farfieldCurves)} }};");
            geoLines.Add("// Dieser Name ist der SU2-Marker: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING");
            geoLines.Add($"Physical Curve(\"Wall\") = {{ {string.Join(", ", allBodyCurves)} }};");
            geoLines.Add("// Physical Surface Fluid -> 2D-Fluiddomaene");
            geoLines.Add($"Physical Surface(\"{FluidMarkerName}\") = {{ 1 }};");
            geoLines.Add(string.Empty);
            geoLines.Add($"Mesh.ScalingFactor = (1.0 / {F(scaleFactor)}) * 0.001;");

            string? dir = Path.GetDirectoryName(geoScriptPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllLines(geoScriptPath, geoLines);
        }

        /// <summary>
        /// Läuft ab <paramref name="start"/> in Richtung <paramref name="dir"/> (±1) um die geschlossene Kontur,
        /// bis <paramref name="stop"/> erfüllt ist; liefert diesen Index, sonst -1.
        /// </summary>
        public static int WalkTo(List<float[]> points, int start, int dir, Func<float[], bool> stop)
        {
            int n = points.Count;
            int i = start;
            for (int step = 0; step < n; step++)
            {
                i = ((i + dir) % n + n) % n;
                if (stop(points[i])) return i;
            }
            return -1;
        }

        /// <summary>
        /// Entfernt einen Marker-Block (MARKER_TAG + MARKER_ELEMS + Elementzeilen) aus einer
        /// <c>.su2</c>-Datei und zählt NMARK herunter.
        /// </summary>
        public static bool RemoveMarker(string su2Path, string markerTag)
        {
            string[] lines = File.ReadAllLines(su2Path);
            int tagIdx = Array.FindIndex(lines, l => l.Trim() == $"MARKER_TAG= {markerTag}");
            if (tagIdx < 0) return false;

            int elemsIdx = tagIdx + 1;
            if (elemsIdx >= lines.Length || !lines[elemsIdx].TrimStart().StartsWith("MARKER_ELEMS="))
                throw new InvalidDataException($"{Path.GetFileName(su2Path)}: auf MARKER_TAG= {markerTag} folgt kein MARKER_ELEMS.");

            int count = int.Parse(lines[elemsIdx].Split('=')[1].Trim(), CultureInfo.InvariantCulture);
            int endIdx = elemsIdx + 1 + count;
            if (endIdx > lines.Length)
                throw new InvalidDataException($"{Path.GetFileName(su2Path)}: Marker {markerTag} ist unvollständig.");

            var result = new List<string>(lines.Length - (endIdx - tagIdx));
            for (int i = 0; i < lines.Length; i++)
            {
                if (i >= tagIdx && i < endIdx) continue;
                string line = lines[i];
                if (line.TrimStart().StartsWith("NMARK="))
                {
                    int nmark = int.Parse(line.Split('=')[1].Trim(), CultureInfo.InvariantCulture);
                    line = $"NMARK= {nmark - 1}";
                }
                result.Add(line);
            }

            File.WriteAllLines(su2Path, result);
            return true;
        }

        private static BBox2 ComputeBounds(ContourFile2D contourFile)
        {
            BBox2 bounds = BBox2.Empty;
            if (contourFile.Contours == null) return bounds;
            foreach (var c in contourFile.Contours)
            {
                if (c.Points == null) continue;
                foreach (var p in c.Points)
                {
                    if (p != null && p.Length >= 2)
                        bounds = bounds.Include(new Vector2(p[0], p[1]));
                }
            }
            return bounds;
        }

        private static void AppendContours(
            List<string> geoLines, ContourFile2D contourFile, Gmsh2DMesherOptions options, float chordGeo, float surfaceSize,
            ref int nextPointId, ref int nextCurveId, ref int nextLoopId,
            List<int> allBodyCurves, List<int> allFanPointIds, List<int> bodyLoopIds)
        {
            if (contourFile.Contours == null) return;
            string F(float v) => WakeAndDetailBoxBuilder.Format(v);

            foreach (var contour in contourFile.Contours)
            {
                if (contour.Points == null || contour.Points.Count < 3)
                    continue;

                int n = contour.Points.Count;
                var ptIdMap = new int[n];
                int leIdx = 0, teIdx = 0;
                for (int i = 1; i < n; i++)
                {
                    if (contour.Points[i][0] < contour.Points[leIdx][0]) leIdx = i;
                    if (contour.Points[i][0] > contour.Points[teIdx][0]) teIdx = i;
                }

                bool refineEdges = options.EdgeRefinementFactor > 0f && options.EdgeRefinementFactor < 1f;
                float edgeSize = surfaceSize * options.EdgeRefinementFactor;
                var corners = contour.CornerIndices ?? new List<int>();
                var cornerSet = new HashSet<int>(corners);
                var edgeIdx = new HashSet<int>();
                if (!cornerSet.Contains(leIdx)) edgeIdx.Add(leIdx);
                if (!cornerSet.Contains(teIdx)) edgeIdx.Add(teIdx);

                for (int i = 0; i < n; i++)
                {
                    var pt = contour.Points[i];
                    ptIdMap[i] = nextPointId++;
                    float pointSize = refineEdges && edgeIdx.Contains(i) ? edgeSize : surfaceSize;
                    geoLines.Add($"Point({ptIdMap[i]}) = {{ {F(pt[0])}, {F(pt[1])}, 0, {F(pointSize)} }};");
                }

                foreach (int cornerIdx in corners)
                {
                    if (cornerIdx >= 0 && cornerIdx < n)
                        allFanPointIds.Add(ptIdMap[cornerIdx]);
                }

                var splitIndices = BuildSplitIndices(contour.Points, corners, cornerSet, leIdx, teIdx, refineEdges, options, chordGeo);
                var loopCurves = new List<int>();
                int m = splitIndices.Count;
                for (int s = 0; s < m; s++)
                {
                    int fromIdx = splitIndices[s];
                    int toIdx = splitIndices[(s + 1) % m];
                    var splinePts = new List<int>();
                    int curr = fromIdx;
                    while (true)
                    {
                        splinePts.Add(ptIdMap[curr]);
                        if (curr == toIdx) break;
                        curr = (curr + 1) % n;
                    }

                    int splineId = nextCurveId++;
                    geoLines.Add($"Spline({splineId}) = {{ {string.Join(", ", splinePts)} }};");
                    loopCurves.Add(splineId);
                    allBodyCurves.Add(splineId);
                }

                int bodyLoopId = nextLoopId++;
                geoLines.Add($"Curve Loop({bodyLoopId}) = {{ {string.Join(", ", loopCurves)} }};");
                bodyLoopIds.Add(bodyLoopId);
            }
        }

        private static List<int> BuildSplitIndices(
            List<float[]> points, List<int> corners, HashSet<int> cornerSet, int leIdx, int teIdx,
            bool refineEdges, Gmsh2DMesherOptions options, float chordGeo)
        {
            int n = points.Count;
            var splitIndices = new List<int>();
            if (corners.Count == 0)
            {
                splitIndices.Add(leIdx);
                splitIndices.Add(teIdx);
            }
            else if (corners.Count == 1)
            {
                int c0 = corners[0];
                splitIndices.Add(c0);
                int oppIdx = 0;
                float bestDistSq = -1f;
                Vector2 c0Vec = new Vector2(points[c0][0], points[c0][1]);
                for (int i = 0; i < n; i++)
                {
                    if (i == c0) continue;
                    float d2 = Vector2.DistanceSquared(c0Vec, new Vector2(points[i][0], points[i][1]));
                    if (d2 > bestDistSq) { bestDistSq = d2; oppIdx = i; }
                }
                splitIndices.Add(oppIdx);
            }
            else
            {
                splitIndices.AddRange(corners.Where(idx => idx >= 0 && idx < n).Distinct());
            }

            if (refineEdges)
            {
                float distX = options.EdgeRefinementDistanceFactor * chordGeo;
                float minX = points[leIdx][0], maxX = points[teIdx][0];
                bool refineTe = !cornerSet.Contains(teIdx);

                foreach (int walkDir in new[] { 1, -1 })
                {
                    int fromLe = WalkTo(points, leIdx, walkDir, pt => pt[0] >= minX + distX);
                    if (fromLe >= 0 && fromLe < n && !splitIndices.Contains(fromLe)) splitIndices.Add(fromLe);

                    if (refineTe)
                    {
                        int fromTe = WalkTo(points, teIdx, walkDir, pt => pt[0] <= maxX - distX);
                        if (fromTe >= 0 && fromTe < n && !splitIndices.Contains(fromTe)) splitIndices.Add(fromTe);
                    }
                }
                if (!splitIndices.Contains(leIdx)) splitIndices.Add(leIdx);
                if (!splitIndices.Contains(teIdx)) splitIndices.Add(teIdx);
            }

            splitIndices.Sort();
            return splitIndices;
        }
    }
}
