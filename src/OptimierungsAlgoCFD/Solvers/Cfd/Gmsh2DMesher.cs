using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.Planar2D;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// 2D-CFD-Mesher basierend auf Gmsh (TODO-45). Erzeugt ein 2D-Flächennetz in der xy-Ebene
    /// für zweidimensionale SU2-Simulationen.
    /// Liest Konturdaten über <see cref="Shape2D.LoadContour"/>, erzeugt ein kreisförmiges
    /// Fernfeld, eine strukturierte Quader-Grenzschicht (BoundaryLayer Field) und Hintergrundfelder
    /// für Nahfeld und Nachlauf (Wake Box).
    /// </summary>
    public class Gmsh2DMesher : IMeshGenerator
    {
        private readonly Gmsh2DMesherOptions _options;
        private readonly FlowConditions? _flow;

        /// <param name="options">Vorgabedaten aus solvers/gmsh.json; ohne Angabe gelten Standardwerte.</param>
        /// <param name="flow">Physikalische Strömungsbedingungen für die Grenzschichtberechnung.</param>
        public Gmsh2DMesher(Gmsh2DMesherOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new Gmsh2DMesherOptions();
            _flow = flow;
        }

        public Gmsh2DMesherOptions Options => _options;
        public FlowConditions? Flow => _flow;

        public string GenerateMesh(MeshRequest request)
        {
            string suffix = request.BaseSuffix;
            string outputMeshPath = Path.Combine(request.WorkingDirectory, $"Model_{suffix}.su2");
            string geoScriptPath = Path.Combine(request.WorkingDirectory, $"Meshing_{suffix}.geo");

            // Schlägt Gmsh fehl ("Edge not recovered": die Wandschicht kollidiert an dünnen Stellen
            // wie der Hinterkante mit sich selbst), wird mit halber, dann viertel Schichtdicke
            // wiederholt. y1 und damit y+ bleiben gleich (Servertest 06.11.2026: 33 von 64
            // Naca2D-Varianten scheiterten mit voller Dicke, mit halber liefen sie).
            float thicknessScale = 1.0f;
            int exitCode;
            while (true)
            {
                WriteGeoScript(
                    geoScriptPath,
                    request.GeometryPath,
                    request.Config,
                    request.ScaleFactor,
                    request.RefinementFactor,
                    thicknessScale);

                if (File.Exists(outputMeshPath)) File.Delete(outputMeshPath);
                exitCode = RunGmsh(request, geoScriptPath, outputMeshPath);
                if (exitCode == 0 && File.Exists(outputMeshPath)) break;

                if (thicknessScale <= MinThicknessScale)
                {
                    CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                    throw new Exception($"Gmsh konnte {Path.GetFileName(outputMeshPath)} nicht erstellen (ExitCode {exitCode}, auch mit {thicknessScale * 100f:0} % Wandschichtdicke). .geo-Skript nach FehlerLogs kopiert.");
                }

                thicknessScale *= 0.5f;
                SimulationLog.Detail($"[WARNUNG] Wandschicht ließ sich nicht vernetzen (meist Kollision an der Hinterkante) – neuer Versuch mit {thicknessScale * 100f:0} % der Schichtdicke.");
            }

            RemoveMarker(outputMeshPath, FluidMarkerName);

            SimulationLog.Detail("[OK] 2D-Flächennetz generiert");
            return outputMeshPath;
        }

        /// <summary>Kleinster Anteil der berechneten Wandschichtdicke, der noch versucht wird (1 → ½ → ¼ → ⅛ → 1/16).</summary>
        public const float MinThicknessScale = 0.0625f;

        private static int RunGmsh(MeshRequest request, string geoScriptPath, string outputMeshPath)
        {
            using Process mesher = new Process();
            mesher.StartInfo.FileName = ExecutableResolver.Resolve(request.Config.GmshPath, "gmsh");
            mesher.StartInfo.Arguments = $"\"{geoScriptPath}\" -2 -nt {request.Config.GmshCores} -format su2 -v 0 -o \"{outputMeshPath}\"";
            mesher.StartInfo.UseShellExecute = false;
            mesher.StartInfo.CreateNoWindow = true;

            try
            {
                mesher.Start();
                mesher.WaitForExit();
                return mesher.ExitCode;
            }
            catch (Exception ex)
            {
                CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                throw new Exception($"Gmsh konnte nicht gestartet werden: {ex.Message}. .geo-Skript nach FehlerLogs kopiert.", ex);
            }
        }

        /// <summary>Name der Fluid-Gruppe im .geo-Skript; in der .su2-Datei kein gültiger Rand.</summary>
        public const string FluidMarkerName = "Fluid";

        /// <summary>
        /// Entfernt einen Marker-Block (MARKER_TAG + MARKER_ELEMS + Elementzeilen) aus einer
        /// .su2-Datei und zählt NMARK herunter. Gmsh 4.11 schreibt in 2D die Physical Surface
        /// des Fluidgebiets als Rand-Marker mit allen Zellen; SU2 bricht daran ab
        /// ("doesn't have any definition for marker Fluid"). Fehlt der Marker, bleibt die
        /// Datei unverändert.
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
            int endIdx = elemsIdx + 1 + count; // erste Zeile NACH dem Block
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

        public string GenerateMesh(
            string modelStlPath,
            int iteration, int variant,
            SimulationConfig config,
            float scaleFactor,
            string workingDirectory)
        {
            return GenerateMesh(new MeshRequest(modelStlPath, iteration, variant, config, scaleFactor, workingDirectory));
        }

        /// <summary>
        /// Schreibt das .geo-Skript für Gmsh im 2D-Modus in die Datei.
        /// <paramref name="thicknessScale"/> verkleinert die Gesamtdicke der Wandschicht (Wiederholungsversuch);
        /// die erste Zellhöhe bleibt.
        /// </summary>
        public void WriteGeoScript(
            string geoScriptPath,
            string contourJsonPath,
            SimulationConfig config,
            float scaleFactor,
            float refinementFactor = 1.0f,
            float thicknessScale = 1.0f)
        {
            if (!File.Exists(contourJsonPath))
                throw new FileNotFoundException($"2D-Konturdatei nicht gefunden: {contourJsonPath}", contourJsonPath);

            ContourFile2D contourFile = Shape2D.LoadContour(contourJsonPath);

            // Bounding Box ermitteln
            BBox2 bounds = BBox2.Empty;
            if (contourFile.Contours != null)
            {
                foreach (var c in contourFile.Contours)
                {
                    if (c.Points == null) continue;
                    foreach (var p in c.Points)
                    {
                        if (p != null && p.Length >= 2)
                            bounds = bounds.Include(new Vector2(p[0], p[1]));
                    }
                }
            }

            float chordGeo = (!bounds.IsEmpty && bounds.Size.X > 0f) ? bounds.Size.X : 50.0f * scaleFactor;
            float effScale = scaleFactor > 0f ? scaleFactor : 1.0f;
            float chordMm = chordGeo / effScale;

            FlowConditions flow = _flow ?? FlowConditions.From(config, new Su2SolverOptions());

            var spec = BoundaryLayerCalculator.Calculate(
                flow.Density,
                flow.Velocity,
                flow.DynamicViscosity,
                chordMm,
                _options.TargetYPlus,
                _options.BoundaryLayerGrowthRate,
                _options.BoundaryLayerMinLayers,
                _options.BoundaryLayerLayers);

            // Regel F4: Erste Zellhöhe y1 hängt NUR an der Physik (Ziel-y+), NICHT am RefinementFactor!
            float y1Geo = spec.FirstLayerHeightMm * scaleFactor;

            // Alle anderen Zellgrößen werden mit factor skaliert und beziehen sich auf L (chordGeo)
            float factor = refinementFactor > 0f ? refinementFactor : 1.0f;
            float surfaceSize = (_options.SurfaceCellSizeMm > 0f
                ? _options.SurfaceCellSizeMm * scaleFactor
                : _options.SurfaceCellSizeFactor * chordGeo) * factor;

            float nearfieldSize = _options.NearfieldCellSizeFactor * chordGeo * factor;
            float farfieldSize = _options.FarfieldCellSizeFactor * chordGeo * factor;
            float wakeSize = _options.WakeCellSizeFactor * chordGeo * factor;

            float distMin = 0.1f * chordGeo;
            // Die Zellgröße wächst vom Nahfeld bis zur Fernfeldgröße über diese Strecke. Automatisch
            // 0,4 · Fernfeldradius: bei Radius 500 c und Fernfeldzelle 25 c wächst sie gleichmäßig
            // (Steigung ~0,1), statt wie früher in 0,8 c auf das 60-Fache zu springen (TODO-W6).
            float growthDistance = (_options.NearfieldGrowthDistanceFactor > 0f
                ? _options.NearfieldGrowthDistanceFactor
                : 0.4f * _options.FarfieldRadiusFactor) * chordGeo;
            float distMax = Math.Max(0.8f * chordGeo, growthDistance);

            var geoLines = new List<string>
            {
                "// =============================================================================",
                "// Gmsh 2D CFD Netz-Skript (generiert fuer SU2 2D Incompressible RANS)",
                "// =============================================================================",
                "Mesh.Optimize = 1;",
                "Mesh.OptimizeNetgen = 0;",
                ""
            };

            int nextPointId = 1;
            int nextCurveId = 1;
            int nextLoopId = 1;

            var allBodyCurves = new List<int>();
            var allFanPointIds = new List<int>();
            var bodyLoopIds = new List<int>();

            geoLines.Add("// --- A) BAUTEIL-KONTURPUNKTE (xy-Ebene, z=0) ---");

            if (contourFile.Contours != null)
            {
                foreach (var contour in contourFile.Contours)
                {
                    if (contour.Points == null || contour.Points.Count < 3)
                        continue;

                    int n = contour.Points.Count;
                    int startPtId = nextPointId;
                    var ptIdMap = new int[n];

                    // Vorder- und Hinterkante (kleinstes/größtes x). Dort ist die Wandzelle kleiner
                    // (EdgeRefinementFactor), nach 0,1 L (EdgeRefinementDistanceFactor) wieder normal; Gmsh
                    // interpoliert zwischen den Endpunkten der Teilkurven (TODO-W6). Nur über Punktgrößen:
                    // Distanz- und Ball-Felder mit Zellen unter der Wandzellgröße ließen Gmsh 4.11 bei
                    // aktiver Grenzschicht endlos rechnen (Servertest 06.11.2026).
                    int leIdx = 0, teIdx = 0;
                    for (int i = 1; i < n; i++)
                    {
                        if (contour.Points[i][0] < contour.Points[leIdx][0]) leIdx = i;
                        if (contour.Points[i][0] > contour.Points[teIdx][0]) teIdx = i;
                    }
                    bool refineEdges = _options.EdgeRefinementFactor > 0f && _options.EdgeRefinementFactor < 1f;
                    float edgeSize = surfaceSize * _options.EdgeRefinementFactor;
                    var cornerSet = new HashSet<int>(contour.CornerIndices ?? new List<int>());
                    var edgeIdx = new HashSet<int>();
                    if (!cornerSet.Contains(leIdx)) edgeIdx.Add(leIdx);
                    if (!cornerSet.Contains(teIdx)) edgeIdx.Add(teIdx);

                    for (int i = 0; i < n; i++)
                    {
                        var pt = contour.Points[i];
                        ptIdMap[i] = nextPointId++;
                        float pointSize = refineEdges && edgeIdx.Contains(i) ? edgeSize : surfaceSize;
                        geoLines.Add($"Point({ptIdMap[i]}) = {{ {Format(pt[0])}, {Format(pt[1])}, 0, {Format(pointSize)} }};");
                    }

                    // Knickpunkte als Fächerpunkte (FanPoints) sammeln
                    var corners = contour.CornerIndices ?? new List<int>();
                    foreach (int cornerIdx in corners)
                    {
                        if (cornerIdx >= 0 && cornerIdx < n)
                        {
                            allFanPointIds.Add(ptIdMap[cornerIdx]);
                        }
                    }

                    // Schnittpunkte (Splits) für Splines bestimmen:
                    // Scharfe Kanten (z. B. Hinterkante) müssen getrennt werden, damit Splines nicht darüber glätten.
                    var splitIndices = new List<int>();
                    if (corners.Count == 0)
                    {
                        // 0 Knickpunkte (z.B. Zylinder): teile an minX und maxX, um degenerierte geschlossene Splines zu vermeiden
                        int minXIdx = 0, maxXIdx = 0;
                        float minXVal = contour.Points[0][0], maxXVal = contour.Points[0][0];
                        for (int i = 1; i < n; i++)
                        {
                            if (contour.Points[i][0] < minXVal) { minXVal = contour.Points[i][0]; minXIdx = i; }
                            if (contour.Points[i][0] > maxXVal) { maxXVal = contour.Points[i][0]; maxXIdx = i; }
                        }
                        splitIndices.Add(minXIdx);
                        splitIndices.Add(maxXIdx);
                    }
                    else if (corners.Count == 1)
                    {
                        int c0 = corners[0];
                        splitIndices.Add(c0);
                        // Gegenüberliegenden Punkt (Nasenleiste bei minX oder maxX) hinzunehmen
                        int oppIdx = 0;
                        float bestDistSq = -1f;
                        Vector2 c0Vec = new Vector2(contour.Points[c0][0], contour.Points[c0][1]);
                        for (int i = 0; i < n; i++)
                        {
                            if (i == c0) continue;
                            Vector2 ptVec = new Vector2(contour.Points[i][0], contour.Points[i][1]);
                            float d2 = Vector2.DistanceSquared(c0Vec, ptVec);
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
                        // Teilungspunkte, an denen die Wandzelle wieder ihre normale Größe hat: 0,1 L hinter der
                        // Vorderkante und vor der Hinterkante, jeweils oben und unten.
                        float distX = _options.EdgeRefinementDistanceFactor * chordGeo;
                        float minX = contour.Points[leIdx][0], maxX = contour.Points[teIdx][0];
                        bool refineLe = true;
                        bool refineTe = !cornerSet.Contains(teIdx);

                        foreach (int walkDir in new[] { 1, -1 })
                        {
                            if (refineLe)
                            {
                                int fromLe = WalkTo(contour.Points, leIdx, walkDir, pt => pt[0] >= minX + distX);
                                if (fromLe >= 0 && fromLe < n && !splitIndices.Contains(fromLe)) splitIndices.Add(fromLe);
                            }
                            if (refineTe)
                            {
                                int fromTe = WalkTo(contour.Points, teIdx, walkDir, pt => pt[0] <= maxX - distX);
                                if (fromTe >= 0 && fromTe < n && !splitIndices.Contains(fromTe)) splitIndices.Add(fromTe);
                            }
                        }
                        if (refineLe && !splitIndices.Contains(leIdx)) splitIndices.Add(leIdx);
                        if (!splitIndices.Contains(teIdx)) splitIndices.Add(teIdx);
                    }

                    splitIndices.Sort();

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

            geoLines.Add("");
            geoLines.Add("// --- B) FERNFELD-KREIS (Zirkulationsradius = FarfieldRadiusFactor * c) ---");

            Vector2 center = bounds.Center;
            float radius = _options.FarfieldRadiusFactor * chordGeo;

            int centerPtId = nextPointId++;
            int eastPtId = nextPointId++;
            int northPtId = nextPointId++;
            int westPtId = nextPointId++;
            int southPtId = nextPointId++;

            geoLines.Add($"Point({centerPtId}) = {{ {Format(center.X)}, {Format(center.Y)}, 0, {Format(farfieldSize)} }};");
            geoLines.Add($"Point({eastPtId}) = {{ {Format(center.X + radius)}, {Format(center.Y)}, 0, {Format(farfieldSize)} }};");
            geoLines.Add($"Point({northPtId}) = {{ {Format(center.X)}, {Format(center.Y + radius)}, 0, {Format(farfieldSize)} }};");
            geoLines.Add($"Point({westPtId}) = {{ {Format(center.X - radius)}, {Format(center.Y)}, 0, {Format(farfieldSize)} }};");
            geoLines.Add($"Point({southPtId}) = {{ {Format(center.X)}, {Format(center.Y - radius)}, 0, {Format(farfieldSize)} }};");

            int arc1 = nextCurveId++;
            int arc2 = nextCurveId++;
            int arc3 = nextCurveId++;
            int arc4 = nextCurveId++;

            geoLines.Add($"Circle({arc1}) = {{ {eastPtId}, {centerPtId}, {northPtId} }};");
            geoLines.Add($"Circle({arc2}) = {{ {northPtId}, {centerPtId}, {westPtId} }};");
            geoLines.Add($"Circle({arc3}) = {{ {westPtId}, {centerPtId}, {southPtId} }};");
            geoLines.Add($"Circle({arc4}) = {{ {southPtId}, {centerPtId}, {eastPtId} }};");

            int farfieldLoopId = nextLoopId++;
            geoLines.Add($"Curve Loop({farfieldLoopId}) = {{ {arc1}, {arc2}, {arc3}, {arc4} }};");
            var farfieldCurves = new[] { arc1, arc2, arc3, arc4 };

            geoLines.Add("");
            geoLines.Add("// --- C) 2D-FLUIDDOMAENE (Fernfeld minus Koerper) ---");
            var surfaceLoops = new List<int> { farfieldLoopId };
            surfaceLoops.AddRange(bodyLoopIds);
            geoLines.Add($"Plane Surface(1) = {{ {string.Join(", ", surfaceLoops)} }};");

            geoLines.Add("");
            geoLines.Add("// =============================================================================");
            geoLines.Add("// D) STRUKTURIERTE GRENZSCHICHT (BOUNDARY LAYER QUAD-EXTRUSION)");
            geoLines.Add("// =============================================================================");
            geoLines.Add("Field[1] = BoundaryLayer;");
            geoLines.Add($"Field[1].CurvesList = {{ {string.Join(", ", allBodyCurves)} }};");
            geoLines.Add($"Field[1].Size = {Format(y1Geo)};");
            geoLines.Add($"Field[1].Ratio = {Format(_options.BoundaryLayerGrowthRate)};");
            // Gmsh 4.11 wertet NbLayers nur zusammen mit BetaLaw aus. Ohne BetaLaw bestimmt
            // Thickness (Gesamtdicke), wie viele Schichten mit Size und Ratio entstehen. Fehlt
            // Thickness, gilt die Gmsh-Vorgabe 0.01 (Modelleinheiten) und es entsteht nur EINE
            // Schicht (auf BIC_12 am 06.11.2026 gemessen: 205 statt ~4600 Vierecke).
            geoLines.Add($"Field[1].NbLayers = {spec.LayerCount};");
            geoLines.Add($"Field[1].Thickness = {Format(spec.TotalThicknessMm * scaleFactor * Math.Clamp(thicknessScale, 0.01f, 1.0f))};");
            geoLines.Add($"Field[1].SizeFar = {Format(surfaceSize)};");
            geoLines.Add("Field[1].Quads = 1;");

            if (allFanPointIds.Count > 0)
            {
                string fansStr = string.Join(", ", allFanPointIds.Distinct());
                geoLines.Add($"Field[1].FanPointsList = {{ {fansStr} }};");
                geoLines.Add($"Field[1].FanNodesList = {{ {fansStr} }};");
            }

            geoLines.Add("");
            geoLines.Add("// =============================================================================");
            geoLines.Add("// E) DISTANZ- UND SCHWELLENWERTFELD FUER NAHFELD");
            geoLines.Add("// =============================================================================");
            geoLines.Add("Field[2] = Distance;");
            geoLines.Add($"Field[2].CurvesList = {{ {string.Join(", ", allBodyCurves)} }};");

            geoLines.Add("Field[3] = Threshold;");
            geoLines.Add("Field[3].InField = 2;");
            geoLines.Add($"Field[3].SizeMin = {Format(nearfieldSize)};");
            geoLines.Add($"Field[3].SizeMax = {Format(farfieldSize)};");
            geoLines.Add($"Field[3].DistMin = {Format(distMin)};");
            geoLines.Add($"Field[3].DistMax = {Format(distMax)};");

            int activeBackgroundField = 3;
            var combined = new List<int> { 3 };

            if (_options.WakeRefinement)
            {
                geoLines.Add("");
                geoLines.Add("// =============================================================================");
                geoLines.Add("// F) DYNAMISCHE WAKE-BOX (NACHLAUF)");
                geoLines.Add("// =============================================================================");
                float wakeXMin = bounds.Min.X - (0.05f * chordGeo);
                float wakeXMax = bounds.Max.X + (_options.WakeLengthFactor * chordGeo);
                float wakeYMin = bounds.Center.Y - (0.5f * _options.WakeWidthFactor * chordGeo);
                float wakeYMax = bounds.Center.Y + (0.5f * _options.WakeWidthFactor * chordGeo);

                geoLines.Add("Field[4] = Box;");
                geoLines.Add($"Field[4].XMin = {Format(wakeXMin)};");
                geoLines.Add($"Field[4].XMax = {Format(wakeXMax)};");
                geoLines.Add($"Field[4].YMin = {Format(wakeYMin)};");
                geoLines.Add($"Field[4].YMax = {Format(wakeYMax)};");
                geoLines.Add("Field[4].ZMin = -1.0;");
                geoLines.Add("Field[4].ZMax = 1.0;");
                geoLines.Add($"Field[4].VIn = {Format(wakeSize)};");
                geoLines.Add($"Field[4].VOut = {Format(farfieldSize)};");
                combined.Add(4);
            }

            if (_options.DetailBox)
            {
                // Sehr feine Box um und hinter dem Profil (TODO-W6): Nachlauf und Wirbel in Modellnähe.
                // Von 0,1 L vor der Vorderkante bis DetailBoxLengthFactor · L hinter der Hinterkante,
                // quer Profilhöhe plus DetailBoxWidthFactor · L je Seite. Skaliert mit dem Netzfaktor.
                float detailSize = _options.DetailBoxCellSizeFactor * chordGeo * factor;
                float detailXMin = bounds.Min.X - (0.1f * chordGeo);
                float detailXMax = bounds.Max.X + (_options.DetailBoxLengthFactor * chordGeo);
                float detailYMin = bounds.Min.Y - (_options.DetailBoxWidthFactor * chordGeo);
                float detailYMax = bounds.Max.Y + (_options.DetailBoxWidthFactor * chordGeo);

                geoLines.Add("");
                geoLines.Add("// =============================================================================");
                geoLines.Add("// F2) DETAILBOX UM UND HINTER DEM PROFIL (TODO-W6)");
                geoLines.Add("// =============================================================================");
                geoLines.Add("Field[6] = Box;");
                geoLines.Add($"Field[6].XMin = {Format(detailXMin)};");
                geoLines.Add($"Field[6].XMax = {Format(detailXMax)};");
                geoLines.Add($"Field[6].YMin = {Format(detailYMin)};");
                geoLines.Add($"Field[6].YMax = {Format(detailYMax)};");
                geoLines.Add("Field[6].ZMin = -1.0;");
                geoLines.Add("Field[6].ZMax = 1.0;");
                geoLines.Add($"Field[6].VIn = {Format(detailSize)};");
                geoLines.Add($"Field[6].VOut = {Format(farfieldSize)};");
                // Weicher Rand: ohne Übergang springt die Zellgröße an der Boxwand.
                geoLines.Add($"Field[6].Thickness = {Format(0.3f * chordGeo)};");
                combined.Add(6);
            }

            if (combined.Count > 1)
            {
                geoLines.Add("");
                geoLines.Add("// =============================================================================");
                geoLines.Add("// G) HINTERGRUND-FELDER INTELLIGENT KOMBINIEREN");
                geoLines.Add("// =============================================================================");
                geoLines.Add("Field[5] = Min;");
                geoLines.Add($"Field[5].FieldsList = {{ {string.Join(", ", combined)} }};");
                activeBackgroundField = 5;
            }

            geoLines.Add($"Background Field = {activeBackgroundField};");
            // Ein BoundaryLayer-Feld wirkt NICHT als Hintergrundfeld, sondern muss eigens
            // angemeldet werden. Ohne diese Zeile ignoriert Gmsh Field[1] stillschweigend.
            geoLines.Add("BoundaryLayer Field = 1;");

            geoLines.Add("");
            geoLines.Add("// =============================================================================");
            geoLines.Add("// H) PHYSIKALISCHE MARKER FUER SU2");
            geoLines.Add("// =============================================================================");
            geoLines.Add("// Dieser Name ist der SU2-Marker: Farfield -> MARKER_FAR");
            geoLines.Add($"Physical Curve(\"Farfield\") = {{ {string.Join(", ", farfieldCurves)} }};");
            geoLines.Add("// Dieser Name ist der SU2-Marker: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING");
            geoLines.Add($"Physical Curve(\"Wall\") = {{ {string.Join(", ", allBodyCurves)} }};");
            // Ohne diese Gruppe schreibt Gmsh keine einzige Zelle. Mit ihr schreibt der SU2-Export
            // von Gmsh 4.11 sie in 2D zusaetzlich als Rand-Marker, den SU2 nicht kennt. Er wird
            // deshalb nach dem Vernetzen wieder entfernt (RemoveMarker).
            geoLines.Add("// Physical Surface Fluid -> 2D-Fluiddomaene");
            geoLines.Add("Physical Surface(\"Fluid\") = { 1 };");

            geoLines.Add("");
            geoLines.Add($"Mesh.ScalingFactor = (1.0 / {Format(scaleFactor)}) * 0.001;");

            string? dir = Path.GetDirectoryName(geoScriptPath);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllLines(geoScriptPath, geoLines);
        }

        /// <summary>
        /// Läuft ab <paramref name="start"/> in Richtung <paramref name="dir"/> (±1) um die geschlossene Kontur,
        /// bis <paramref name="stop"/> erfüllt ist; liefert diesen Index, sonst -1.
        /// </summary>
        private static int WalkTo(List<float[]> points, int start, int dir, Func<float[], bool> stop)
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

        private static void CopyGeoToFehlerLogs(string workingDirectory, string geoScriptPath)
        {
            try
            {
                string errorLogDir = Path.Combine(workingDirectory, "FehlerLogs");
                Directory.CreateDirectory(errorLogDir);
                string destGeoPath = Path.Combine(errorLogDir, Path.GetFileName(geoScriptPath));
                if (File.Exists(geoScriptPath))
                {
                    File.Copy(geoScriptPath, destGeoPath, overwrite: true);
                }
            }
            catch { }
        }

        private static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
        private static string Format(double value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
