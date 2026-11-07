using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// CFD-Mesher basierend auf Gmsh. Erzeugt ein Volumen-Mesh für CFD-Simulationen.
    /// Der Windkanal (Tunnel) wird hier erzeugt, da er zur Simulationsdomäne gehört.
    /// </summary>
    public class GmshCfdMesher : IMeshGenerator
    {
        private readonly GmshMesherOptions _options;
        private readonly FlowConditions? _flow;
        private string? _cachedTunnelPath;

        /// <param name="options">Vorgabedaten aus solvers/gmsh.json des Projekts; ohne Angabe gelten die Standardwerte.</param>
        /// <param name="flow">Physikalische Strömungsbedingungen (TODO-38); bereitgestellt für Grenzschichtnetz-Berechnungen (TODO-40).</param>
        public GmshCfdMesher(GmshMesherOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new GmshMesherOptions();
            _flow = flow;
        }

        public GmshMesherOptions Options => _options;
        public FlowConditions? Flow => _flow;

        /// <summary>
        /// Erzeugt den statischen Referenz-Windkanal (gehört zum CFD-Setup, nicht zur Geometrie).
        /// Die Hülle wird selbst trianguliert und als binäres STL geschrieben — ohne
        /// Geometrie-Kernel, damit die CFD-Schicht unabhängig von PicoGK bleibt (TODO-11).
        /// </summary>
        public string EnsureTunnel(string workingDirectory)
        {
            if (_cachedTunnelPath != null && File.Exists(_cachedTunnelPath))
                return _cachedTunnelPath;

            IReadOnlyList<StlTriangle> tunnel = BuildTunnel();

            Console.WriteLine($"[SYSTEM] Generiere statischen Referenz-Windkanal ({tunnel.Count} Dreiecke)...");

            _cachedTunnelPath = Path.Combine(workingDirectory, "Static_Windtunnel.stl");
            StlWriter.WriteBinary(_cachedTunnelPath, tunnel);

            return _cachedTunnelPath;
        }

        /// <summary>
        /// Baut die Tunnelhülle nach <see cref="GmshMesherOptions.TunnelShape"/>.
        /// Unbekannter Name → Warnung und Rückfall auf den Quader, wie bei der
        /// Algorithmuswahl: ein Tippfehler in der JSON soll den Lauf nicht abbrechen.
        /// </summary>
        private IReadOnlyList<StlTriangle> BuildTunnel()
        {
            Vector3 center = new Vector3(_options.TunnelCenterX, _options.TunnelCenterY, _options.TunnelCenterZ);
            string shape = (_options.TunnelShape ?? string.Empty).Replace(" ", string.Empty).Trim();

            if (shape.Equals("Cylinder", StringComparison.OrdinalIgnoreCase))
            {
                return TunnelGeometry.CreateCylinder(
                    _options.TunnelDiameter,
                    _options.TunnelLength,
                    center,
                    _options.TunnelSegments);
            }

            if (shape.Length > 0 && !shape.Equals("Box", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[WARNUNG] Unbekannte Windkanal-Form '{_options.TunnelShape}' — nutze 'Box'.");
            }

            return TunnelGeometry.CreateBox(
                new Vector3(_options.TunnelSizeX, _options.TunnelSizeY, _options.TunnelSizeZ),
                center);
        }

        public string GenerateMesh(MeshRequest request)
        {
            string tunnelStlPath = EnsureTunnel(request.WorkingDirectory);

            string suffix = request.BaseSuffix;
            string outputMeshPath = Path.Combine(request.WorkingDirectory, $"Model_{suffix}.su2");
            string geoScriptPath = Path.Combine(request.WorkingDirectory, $"Meshing_{suffix}.geo");

            string mode = (_options.BoundaryLayerMode ?? "Threshold").Trim();
            bool isHexa = mode.Equals("Hexa", StringComparison.OrdinalIgnoreCase);
            bool isPrism = mode.Equals("Prism", StringComparison.OrdinalIgnoreCase);

            WriteGeoScript(
                geoScriptPath,
                request.GeometryPath,
                tunnelStlPath,
                request.Config,
                request.ScaleFactor,
                request.RefinementFactor);

            if (!isHexa && !isPrism)
            {
                int rc = RunGmsh(request.Config, $"\"{geoScriptPath}\" -3 -nt {request.Config.GmshCores} -format su2 -v 0 -o \"{outputMeshPath}\"",
                    request.WorkingDirectory, geoScriptPath);
                if (rc != 0 || !File.Exists(outputMeshPath))
                {
                    CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                    throw new Exception($"Gmsh konnte {Path.GetFileName(outputMeshPath)} nicht erstellen (ExitCode {rc}). .geo-Skript nach FehlerLogs kopiert.");
                }

                Console.WriteLine($"       -> [OK] 3D-Volumennetz generiert ");
                return outputMeshPath;
            }

            // --- Strukturierte Wandschicht in zwei Gmsh-Schritten (Servertest 06.11.2026) ---
            // Schritt 1: Körperoberfläche neu vernetzen und als EINE Fläche speichern.
            string wallScript = WallScriptPath(geoScriptPath);
            string wallMesh = WallMeshPath(geoScriptPath);
            if (File.Exists(wallMesh)) File.Delete(wallMesh);
            int wallRc = RunGmsh(request.Config, $"\"{wallScript}\" -2 -nt {request.Config.GmshCores} -format mesh -v 0 -o \"{wallMesh}\"",
                request.WorkingDirectory, wallScript);
            if (wallRc != 0 || !File.Exists(wallMesh))
            {
                CopyGeoToFehlerLogs(request.WorkingDirectory, wallScript);
                throw new Exception($"Gmsh konnte die Wandoberfläche {Path.GetFileName(wallMesh)} nicht erstellen (ExitCode {wallRc}). .geo-Skript nach FehlerLogs kopiert.");
            }

            // Schritt 2: Wandschicht extrudieren, Kern mit Tetraedern füllen. Schneidet sich die
            // Wandschicht an engen/scharfen Stellen selbst, bricht Gmsh ab ("segment-facet
            // intersection"). Dann werden die äußeren Schichten weggelassen: y1 und damit y+ bleiben,
            // nur der strukturierte Bereich wird dünner (Manta: 21 Schichten scheitern, 12 gehen).
            BoundaryLayerSpec spec = ComputeBoundaryLayerSpec(request.Config, request.GeometryPath, request.ScaleFactor);
            int minLayers = Math.Max(1, _options.BoundaryLayerMinLayers);
            int layers = spec.LayerCount;
            while (true)
            {
                if (layers != spec.LayerCount)
                {
                    WriteGeoScript(geoScriptPath, request.GeometryPath, tunnelStlPath, request.Config,
                        request.ScaleFactor, request.RefinementFactor, layers);
                }

                if (File.Exists(outputMeshPath)) File.Delete(outputMeshPath);
                int rc = RunGmsh(request.Config, $"\"{geoScriptPath}\" -3 -nt {request.Config.GmshCores} -format su2 -v 0 -o \"{outputMeshPath}\"",
                    request.WorkingDirectory, geoScriptPath);
                if (rc == 0 && File.Exists(outputMeshPath)) break;

                int next = NextLayerCount(layers);
                if (next < minLayers)
                {
                    CopyGeoToFehlerLogs(request.WorkingDirectory, wallScript);
                    CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                    throw new Exception($"Gmsh konnte {Path.GetFileName(outputMeshPath)} auch mit {layers} Wandschichten nicht erstellen (ExitCode {rc}). .geo-Skripte nach FehlerLogs kopiert.");
                }

                Console.WriteLine($"       -> [WARNUNG] Wandschicht mit {layers} Schichten ({FormatMm(LayerThicknessMm(spec, layers))} mm) "
                    + $"ließ sich nicht vernetzen (meist Selbstdurchdringung an engen Stellen) – neuer Versuch mit {next} Schichten.");
                layers = next;
            }

            // Gmsh schreibt die Physical Volume "Fluid" als Rand-Marker mit (wie in 2D), SU2 kennt ihn nicht.
            Gmsh2DMesher.RemoveMarker(outputMeshPath, Gmsh2DMesher.FluidMarkerName);

            float hexShare = ReadSu2BoundaryLayerHexShare(outputMeshPath);
            string hexPart = float.IsFinite(hexShare) ? $", {hexShare * 100f:0} % Quader" : string.Empty;
            string layerPart = layers == spec.LayerCount
                ? $"{layers} Schichten"
                : $"{layers} von {spec.LayerCount} Schichten";
            Console.WriteLine($"       -> Grenzschicht: {mode}, y1 = {spec.FirstLayerHeightMm.ToString("0.0000", CultureInfo.InvariantCulture)} mm (y+ {spec.TargetYPlus.ToString("0.#", CultureInfo.InvariantCulture)} bei Re {spec.ReynoldsNumber.ToString("0.0e0", CultureInfo.InvariantCulture)}), {layerPart}, Dicke {FormatMm(LayerThicknessMm(spec, layers))} mm{hexPart}");
            Console.WriteLine($"       -> [OK] 3D-Volumennetz generiert ");
            return outputMeshPath;
        }

        /// <summary>Schritt-1-Skript (Wandoberfläche) zu einem Meshing-Skript: <c>Meshing_X.geo</c> → <c>Wall_X.geo</c>.</summary>
        public static string WallScriptPath(string geoScriptPath)
        {
            string name = Path.GetFileNameWithoutExtension(geoScriptPath);
            if (name.StartsWith("Meshing_", StringComparison.Ordinal)) name = name.Substring("Meshing_".Length);
            return Path.Combine(Path.GetDirectoryName(geoScriptPath) ?? string.Empty, $"Wall_{name}.geo");
        }

        /// <summary>Ergebnis von Schritt 1: die neu vernetzte Wand als eine Fläche (Medit-Format).</summary>
        public static string WallMeshPath(string geoScriptPath) => Path.ChangeExtension(WallScriptPath(geoScriptPath), ".mesh");

        /// <summary>
        /// Nächster Versuch, wenn sich die Wandschicht nicht vernetzen ließ: ein Viertel der
        /// Schichten (mindestens eine) fällt weg, außen. 21 → 16 → 12 → 9 → 7 → 6 → 5 → 4 …
        /// </summary>
        public static int NextLayerCount(int current) => current - Math.Max(1, current / 4);

        private static float LayerThicknessMm(BoundaryLayerSpec spec, int layers)
        {
            double r = spec.GrowthRate;
            return (float)(spec.FirstLayerHeightMm * (Math.Pow(r, layers) - 1.0) / (r - 1.0));
        }

        private static string FormatMm(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private BoundaryLayerSpec ComputeBoundaryLayerSpec(SimulationConfig config, string modelStlPath, float scaleFactor)
        {
            FlowConditions flow = _flow ?? FlowConditions.From(config, new Su2SolverOptions());
            float lengthMm = 50.0f;
            if (File.Exists(modelStlPath))
            {
                try
                {
                    var bounds = StlWriter.ReadBounds(modelStlPath);
                    if (!bounds.IsEmpty && bounds.Size.X > 0f)
                        lengthMm = bounds.Size.X / scaleFactor;
                }
                catch { }
            }

            return BoundaryLayerCalculator.Calculate(
                flow.Density, flow.Velocity, flow.DynamicViscosity, lengthMm,
                _options.TargetYPlus, _options.BoundaryLayerGrowthRate,
                _options.BoundaryLayerMinLayers, _options.BoundaryLayerLayers);
        }

        /// <summary>Startet Gmsh und liefert den Exit-Code.</summary>
        private static int RunGmsh(SimulationConfig config, string arguments, string workingDirectory, string scriptForErrorLog)
        {
            using Process mesher = new Process();
            mesher.StartInfo.FileName = ExecutableResolver.Resolve(config.GmshPath, "gmsh");
            mesher.StartInfo.Arguments = arguments;
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
                CopyGeoToFehlerLogs(workingDirectory, scriptForErrorLog);
                throw new Exception($"Gmsh konnte nicht gestartet werden: {ex.Message}. .geo-Skript nach FehlerLogs kopiert.", ex);
            }
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
        /// Schreibt das .geo-Skript für Gmsh auf die Festplatte (TODO-34, TODO-39).
        /// </summary>
        public void WriteGeoScript(
            string geoScriptPath,
            string modelStlPath,
            string tunnelStlPath,
            SimulationConfig config,
            float scaleFactor)
        {
            WriteGeoScript(geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, 1.0f);
        }

        /// <summary>
        /// Schreibt das .geo-Skript für Gmsh auf die Festplatte mit konfigurierbarem Netzfaktor (TODO-39).
        /// </summary>
        public void WriteGeoScript(
            string geoScriptPath,
            string modelStlPath,
            string tunnelStlPath,
            SimulationConfig config,
            float scaleFactor,
            float refinementFactor)
        {
            WriteGeoScript(geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, refinementFactor, null);
        }

        /// <summary>
        /// Wie oben; bei "Hexa"/"Prism" schreibt es ZWEI Skripte: <see cref="WallScriptPath"/>
        /// (Schritt 1, Wandoberfläche) und <paramref name="geoScriptPath"/> (Schritt 2, Volumen).
        /// <paramref name="layerCountOverride"/> lässt äußere Wandschichten weg (Wiederholungsversuch).
        /// </summary>
        public void WriteGeoScript(
            string geoScriptPath,
            string modelStlPath,
            string tunnelStlPath,
            SimulationConfig config,
            float scaleFactor,
            float refinementFactor,
            int? layerCountOverride)
        {
            string mode = (_options.BoundaryLayerMode ?? "Threshold").Trim();
            bool isThreshold = mode.Equals("Threshold", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(mode);
            bool isHexa = mode.Equals("Hexa", StringComparison.OrdinalIgnoreCase);
            bool isPrism = mode.Equals("Prism", StringComparison.OrdinalIgnoreCase);

            if (!isThreshold && !isHexa && !isPrism)
            {
                throw new InvalidOperationException($"Unbekannter BoundaryLayerMode '{_options.BoundaryLayerMode}'. Erlaubt sind 'Threshold', 'Hexa' oder 'Prism'.");
            }

            string safeModelPath = modelStlPath.Replace("\\", "/");
            string safeTunnelPath = tunnelStlPath.Replace("\\", "/"); 
            string scaleString = scaleFactor.ToString(CultureInfo.InvariantCulture);

            float factor = refinementFactor > 0f ? refinementFactor : 1.0f;
            float sizeMin = _options.BoundaryLayerSizeMin * factor;
            float sizeMax = _options.BoundaryLayerSizeMax * factor;

            if (isThreshold)
            {
                var geoLines = new List<string>
                {
                    "// --- ALLGEMEINE NETZ-EINSTELLUNGEN ---",
                    "Mesh.Optimize = 1;",                 
                    "Mesh.OptimizeNetgen = 0;",         

                    "// A) Windkanal importieren",
                    $"Merge \"{safeTunnelPath}\";",
                    "tunnel_surfs[] = Surface{:};",
                    
                    "// B) Bauteil importieren",
                    $"Merge \"{safeModelPath}\";",
                    "model_surfs[] = Surface{:};",
                    "model_surfs[] -= tunnel_surfs[];",
                    
                    "// C) Hohlraum definieren",
                    "Surface Loop(1) = {tunnel_surfs[]};",
                    "Surface Loop(2) = {model_surfs[]};",
                    "Volume(1) = {1, 2};",
                    
                    "// D) Physische SU2 Marker",
                    "// Dieser Name ist der SU2-Marker: Farfield -> MARKER_FAR",
                    "Physical Surface(\"Farfield\") = {tunnel_surfs[]};",
                    "// Dieser Name ist der SU2-Marker: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                    "Physical Surface(\"Wall\") = {model_surfs[]};",
                    "// Physical Volume(\"Fluid\") = {1};",
                    "Mesh.SaveAll = 1;",

                    "// ==========================================",
                    "// E) DISTANZFELD FUER GRENZSCHICHT (WANDNAEHE)",
                    "// ==========================================",
                    "Field[1] = Distance;",
                    "Field[1].SurfacesList = {model_surfs[]};", 
                    
                    "Field[2] = Threshold;",
                    "Field[2].InField = 1;",
                    $"Field[2].SizeMin = {Format(sizeMin)};",
                    $"Field[2].SizeMax = {Format(sizeMax)};",
                    $"Field[2].DistMin = {Format(_options.BoundaryLayerDistMin)};",
                    $"Field[2].DistMax = {Format(_options.BoundaryLayerDistMax)};"
                };

                if (_options.WakeRefinement)
                {
                    geoLines.Add("// ==========================================");
                    geoLines.Add("// F) DYNAMISCHE WAKE-BOX (NACHLAUF)");
                    geoLines.Add("// ==========================================");
                    geoLines.Add("bbox() = BoundingBox Surface {model_surfs[]};");
                    geoLines.Add("minX = bbox(0); minY = bbox(1); minZ = bbox(2);");
                    geoLines.Add("maxX = bbox(3); maxY = bbox(4); maxZ = bbox(5);");
                    geoLines.Add("lenX = maxX - minX;");
                    geoLines.Add("lenY = maxY - minY;");
                    geoLines.Add("lenZ = maxZ - minZ;");
                    geoLines.Add("Field[3] = Box;");
                    geoLines.Add("Field[3].XMin = minX - (0.1 * lenX);");
                    geoLines.Add($"Field[3].XMax = maxX + ({Format(_options.WakeLengthFactor)} * lenX);");
                    geoLines.Add($"Field[3].YMin = minY - ({Format(_options.WakeWidthFactor)} * lenY);");
                    geoLines.Add($"Field[3].YMax = maxY + ({Format(_options.WakeWidthFactor)} * lenY);");
                    geoLines.Add($"Field[3].ZMin = minZ - ({Format(_options.WakeWidthFactor)} * lenZ);");
                    geoLines.Add($"Field[3].ZMax = maxZ + ({Format(_options.WakeWidthFactor)} * lenZ);");
                    string vIn = _options.WakeCellSize > 0f
                        ? Format(_options.WakeCellSize * factor)
                        : $"(lenX / 6.0) * {Format(factor)}";
                    geoLines.Add($"Field[3].VIn = {vIn};");
                    geoLines.Add($"Field[3].VOut = {Format(sizeMax)};");
                    geoLines.Add("// ==========================================");
                    geoLines.Add("// G) FELDER INTELLIGENT KOMBINIEREN");
                    geoLines.Add("// ==========================================");
                }

                // Zusammenfassen: Wandnähe (2), Nachlauf-Box (3) und Detailbox (5), jeweils falls aktiv.
                var backgroundFields = new List<string> { "2" };
                if (_options.WakeRefinement) backgroundFields.Add("3");
                if (_options.DetailBox)
                {
                    AddDetailBox(geoLines, "model_surfs[]", factor);
                    backgroundFields.Add("5");
                }

                if (backgroundFields.Count > 1)
                {
                    geoLines.Add("Field[4] = Min;");
                    geoLines.Add($"Field[4].FieldsList = {{{string.Join(", ", backgroundFields)}}};");
                    geoLines.Add("Background Field = 4;");
                }
                else
                {
                    geoLines.Add("Background Field = 2;");
                }

                geoLines.Add("Mesh.CharacteristicLengthExtendFromBoundary = 0;");

                if (_options.MeshSizeFromCurvature)
                {
                    geoLines.Add("Mesh.MeshSizeFromCurvature = 1;");
                }

                geoLines.Add($"General.NumThreads = {config.GmshCores};");
                geoLines.Add("// H) Re-Skalierung");
                geoLines.Add($"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;");

                File.WriteAllLines(geoScriptPath, geoLines);
            }
            else
            {
                // ===================================================================
                // Strukturierte Wandschicht ("Hexa": Quader, "Prism": Prismen) in ZWEI Schritten.
                // Servertest 06.11.2026 mit Gmsh 4.11.1 – die Gründe für diesen Aufbau:
                //  * ClassifySurfaces zerlegt einen geschlossenen Körper immer in mindestens zwei
                //    Teilflächen, und die Wandschicht-Extrusion über mehrere Teilflächen lässt in
                //    Gmsh 4.11 eine davon mit Dicke 0 liegen (degenerierte Zellen, SU2 bricht in
                //    ParMETIS ab). Deshalb wird die neu vernetzte Oberfläche in Schritt 1 als EINE
                //    Fläche gespeichert (Physical Surface + SaveElementTagType, Medit-Format) und in
                //    Schritt 2 als eine diskrete Fläche extrudiert.
                //  * Flächennummern nach CreateGeometry sind neu; deshalb wird alles erst NACH der
                //    Extrusion gesammelt und der Kanal erst danach eingelesen.
                //  * Das Fluidvolumen liegt zwischen Kanal und den OBERSEITEN der Wandschicht.
                //  * Die Wandzellgröße gilt nur in Schritt 1 (Mesh.MeshSizeMax); in Schritt 2 wäre
                //    sie ein globales Maximum für das ganze Kanalvolumen (Gmsh rechnet dann endlos).
                // Empfehlung: "Prism" für Voxel-Körper mit scharfen Kanten (Manta: 0 ungültige Zellen,
                // SU2 konvergiert, y+ Median 1,0). "Hexa" ist an glatten Körpern sauber (Kugel), an
                // scharfen Kanten kippen ~5 % der Übergangspyramiden, und SU2 divergierte (Manta).
                // ===================================================================
                BoundaryLayerSpec spec = ComputeBoundaryLayerSpec(config, modelStlPath, scaleFactor);
                int layerCount = Math.Max(1, layerCountOverride ?? spec.LayerCount);

                float y1Geo = spec.FirstLayerHeightMm * scaleFactor;
                var layerHeights = new List<float>(layerCount);
                double r = spec.GrowthRate;
                for (int i = 1; i <= layerCount; i++)
                {
                    double h = y1Geo * (Math.Pow(r, i) - 1.0) / (r - 1.0);
                    layerHeights.Add((float)h);
                }

                string subDivs = string.Join(", ", Enumerable.Repeat("1", layerCount));
                string heights = string.Join(", ", layerHeights.Select(h => Format(h)));

                float surfaceSize = (_options.SurfaceCellSize > 0f ? _options.SurfaceCellSize : _options.BoundaryLayerSizeMin) * factor;

                // ---------------- Schritt 1: Wandoberfläche ----------------
                var wallLines = new List<string>
                {
                    "// Schritt 1 von 2: Koerperoberflaeche neu vernetzen und als EINE Flaeche speichern.",
                    "// Aufruf: gmsh <diese Datei> -2 -format mesh -o <Wall_...mesh>",
                    $"Merge \"{safeModelPath}\";",
                    $"ClassifySurfaces{{{Format(_options.ClassifyAngleDeg * MathF.PI / 180.0f)}, 1, 1, Pi}};",
                    "CreateGeometry;",
                    "wall[] = Surface{:};",
                    "// Wandzellgroesse (nur hier global, in Schritt 2 waere es das Maximum fuer den ganzen Kanal)",
                    $"Mesh.MeshSizeMax = {Format(surfaceSize)};",
                    $"Mesh.MeshSizeMin = {Format(surfaceSize * 0.5f)};",
                };

                if (_options.MeshSizeFromCurvature)
                {
                    wallLines.Add($"Mesh.MeshSizeFromCurvature = {(_options.CurvatureElements > 0 ? _options.CurvatureElements : 20)};");
                }

                if (isHexa)
                {
                    wallLines.Add("// Vierecke nur auf der Wand (Mesh.RecombineAll gaelte global und spaeter auch fuer den Kanal)");
                    wallLines.Add("Recombine Surface{wall[]};");
                    wallLines.Add("Mesh.Algorithm = 8; // Frontal-Delaunay for Quads");
                    wallLines.Add("Mesh.RecombinationAlgorithm = 2;");
                }

                wallLines.Add("// Alle Teilflaechen unter EINER Kennung speichern -> in Schritt 2 eine einzige Flaeche");
                wallLines.Add("Physical Surface(1) = {wall[]};");
                wallLines.Add("Mesh.SaveElementTagType = 2;");
                wallLines.Add($"General.NumThreads = {config.GmshCores};");

                File.WriteAllLines(WallScriptPath(geoScriptPath), wallLines);

                // ---------------- Schritt 2: Wandschicht + Volumen ----------------
                string safeWallMesh = WallMeshPath(geoScriptPath).Replace("\\", "/");
                string cellWord = isHexa ? "Quader (Hexaeder)" : "Prismen";

                var geoLines = new List<string>
                {
                    "// Schritt 2 von 2: Wandschicht extrudieren, Kern mit Tetraedern fuellen.",
                    "Mesh.Optimize = 1;",
                    "Mesh.OptimizeNetgen = 0;",

                    "// A) Neu vernetzte Wand aus Schritt 1 (eine diskrete Flaeche)",
                    $"Merge \"{safeWallMesh}\";",
                    "wall[] = Surface{:};",

                    $"// B) Strukturierte Wandschicht: {layerCount} Schichten {cellWord}, erste Zelle y1 = {Format(y1Geo)}",
                    "Geometry.ExtrudeReturnLateralEntities = 0;",
                    $"Extrude {{ Surface{{wall[]}}; Layers{{ {{{subDivs}}}, {{{heights}}} }}; Recombine; }}",
                    "tops[] = Surface{:};",
                    "tops[] -= wall[];",
                    "bl_vols[] = Volume{:};",

                    "// C) Windkanal erst jetzt importieren",
                    $"Merge \"{safeTunnelPath}\";",
                    "tunnel_surfs[] = Surface{:};",
                    "tunnel_surfs[] -= wall[];",
                    "tunnel_surfs[] -= tops[];",

                    "// D) Kernvolumen zwischen Kanal und Oberseite der Wandschicht",
                    "sl_tunnel = newsl; Surface Loop(sl_tunnel) = {tunnel_surfs[]};",
                    "sl_bl = newsl; Surface Loop(sl_bl) = {tops[]};",
                    "v_core = newv; Volume(v_core) = {sl_tunnel, sl_bl};",

                    "// E) Physische SU2 Marker",
                    "// Dieser Name ist der SU2-Marker: Farfield -> MARKER_FAR",
                    "Physical Surface(\"Farfield\") = {tunnel_surfs[]};",
                    "// Dieser Name ist der SU2-Marker: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                    "Physical Surface(\"Wall\") = {wall[]};",
                    "// Fluid sammelt alle Volumen; Gmsh schreibt es zusaetzlich als Marker, das Programm entfernt ihn danach.",
                    $"Physical Volume(\"{Gmsh2DMesher.FluidMarkerName}\") = {{v_core, bl_vols[]}};",

                    "// ==========================================",
                    "// F) DISTANZFELD FUER DEN KERN (WANDNAEHE)",
                    "// ==========================================",
                    "Field[1] = Distance;",
                    "Field[1].SurfacesList = {wall[]};",
                    "Field[2] = Threshold;",
                    "Field[2].InField = 1;",
                    $"Field[2].SizeMin = {Format(surfaceSize)};",
                    $"Field[2].SizeMax = {Format(sizeMax)};",
                    $"Field[2].DistMin = {Format(_options.BoundaryLayerDistMin)};",
                    $"Field[2].DistMax = {Format(_options.BoundaryLayerDistMax)};",
                };

                if (_options.WakeRefinement)
                {
                    geoLines.Add("// ==========================================");
                    geoLines.Add("// G) DYNAMISCHE WAKE-BOX (NACHLAUF)");
                    geoLines.Add("// ==========================================");
                    geoLines.Add("bbox() = BoundingBox Surface {wall[]};");
                    geoLines.Add("minX = bbox(0); minY = bbox(1); minZ = bbox(2);");
                    geoLines.Add("maxX = bbox(3); maxY = bbox(4); maxZ = bbox(5);");
                    geoLines.Add("lenX = maxX - minX;");
                    geoLines.Add("lenY = maxY - minY;");
                    geoLines.Add("lenZ = maxZ - minZ;");
                    geoLines.Add("Field[3] = Box;");
                    geoLines.Add("Field[3].XMin = minX - (0.1 * lenX);");
                    geoLines.Add($"Field[3].XMax = maxX + ({Format(_options.WakeLengthFactor)} * lenX);");
                    geoLines.Add($"Field[3].YMin = minY - ({Format(_options.WakeWidthFactor)} * lenY);");
                    geoLines.Add($"Field[3].YMax = maxY + ({Format(_options.WakeWidthFactor)} * lenY);");
                    geoLines.Add($"Field[3].ZMin = minZ - ({Format(_options.WakeWidthFactor)} * lenZ);");
                    geoLines.Add($"Field[3].ZMax = maxZ + ({Format(_options.WakeWidthFactor)} * lenZ);");
                    string vIn = _options.WakeCellSize > 0f
                        ? Format(_options.WakeCellSize * factor)
                        : $"(lenX / 6.0) * {Format(factor)}";
                    geoLines.Add($"Field[3].VIn = {vIn};");
                    geoLines.Add($"Field[3].VOut = {Format(sizeMax)};");
                }

                // Zusammenfassen: Wandnähe (2), Nachlauf-Box (3) und Detailbox (5), jeweils falls aktiv.
                var backgroundFields = new List<string> { "2" };
                if (_options.WakeRefinement) backgroundFields.Add("3");
                if (_options.DetailBox)
                {
                    AddDetailBox(geoLines, "wall[]", factor);
                    backgroundFields.Add("5");
                }

                if (backgroundFields.Count > 1)
                {
                    geoLines.Add("Field[4] = Min;");
                    geoLines.Add($"Field[4].FieldsList = {{{string.Join(", ", backgroundFields)}}};");
                    geoLines.Add("Background Field = 4;");
                }
                else
                {
                    geoLines.Add("Background Field = 2;");
                }

                geoLines.Add($"Mesh.MeshSizeMax = {Format(sizeMax)};");
                geoLines.Add("Mesh.MeshSizeExtendFromBoundary = 0;");
                geoLines.Add($"General.NumThreads = {config.GmshCores};");
                geoLines.Add("// H) Re-Skalierung");
                geoLines.Add($"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;");

                File.WriteAllLines(geoScriptPath, geoLines);
            }
        }

        /// <summary>
        /// Detailbox um und hinter dem Modell (TODO-W6) als <c>Field[5]</c>: von 0,1 L vor dem Modell bis
        /// <see cref="GmshMesherOptions.DetailBoxLengthFactor"/> · L dahinter, quer Modellausdehnung plus
        /// <see cref="GmshMesherOptions.DetailBoxWidthFactor"/> · Ausdehnung. Zellgröße
        /// <see cref="GmshMesherOptions.DetailBoxCellSize"/> (0 = 2,5 × Oberflächen-Zellgröße) × Netzfaktor.
        /// </summary>
        private void AddDetailBox(List<string> geoLines, string surfaces, float factor)
        {
            float baseSize = _options.SurfaceCellSize > 0f ? _options.SurfaceCellSize : _options.BoundaryLayerSizeMin;
            float cell = (_options.DetailBoxCellSize > 0f ? _options.DetailBoxCellSize : 2.5f * baseSize) * factor;

            geoLines.Add("// ==========================================");
            geoLines.Add("// F2) DETAILBOX UM UND HINTER DEM MODELL (TODO-W6)");
            geoLines.Add("// ==========================================");
            geoLines.Add($"dbbox() = BoundingBox Surface {{{surfaces}}};");
            geoLines.Add("dminX = dbbox(0); dminY = dbbox(1); dminZ = dbbox(2);");
            geoLines.Add("dmaxX = dbbox(3); dmaxY = dbbox(4); dmaxZ = dbbox(5);");
            geoLines.Add("dlenX = dmaxX - dminX; dlenY = dmaxY - dminY; dlenZ = dmaxZ - dminZ;");
            geoLines.Add("Field[5] = Box;");
            geoLines.Add("Field[5].XMin = dminX - (0.1 * dlenX);");
            geoLines.Add($"Field[5].XMax = dmaxX + ({Format(_options.DetailBoxLengthFactor)} * dlenX);");
            geoLines.Add($"Field[5].YMin = dminY - ({Format(_options.DetailBoxWidthFactor)} * dlenY);");
            geoLines.Add($"Field[5].YMax = dmaxY + ({Format(_options.DetailBoxWidthFactor)} * dlenY);");
            geoLines.Add($"Field[5].ZMin = dminZ - ({Format(_options.DetailBoxWidthFactor)} * dlenZ);");
            geoLines.Add($"Field[5].ZMax = dmaxZ + ({Format(_options.DetailBoxWidthFactor)} * dlenZ);");
            geoLines.Add($"Field[5].VIn = {Format(cell)};");
            geoLines.Add($"Field[5].VOut = {Format(_options.BoundaryLayerSizeMax * factor)};");
            geoLines.Add($"Field[5].Thickness = {Format(Math.Max(cell * 5f, 1f))};");
        }

        /// <summary>
        /// Liest den Anteil der Hexaeder-Zellen an den Grenzschicht-Elementen (Hexaeder + Prismen)
        /// aus einer SU2-Netzdatei (.su2) (TODO-40).
        /// VTK-Elementtypen: 10 = Tetraeder, 12 = Hexaeder, 13 = Prisma, 14 = Pyramide.
        /// Gibt float.NaN zurück, wenn keine Hexaeder/Prismen vorhanden sind oder die Datei fehlt.
        /// </summary>
        public static float ReadSu2BoundaryLayerHexShare(string su2MeshPath)
        {
            if (string.IsNullOrWhiteSpace(su2MeshPath) || !File.Exists(su2MeshPath))
                return float.NaN;

            try
            {
                using var reader = new StreamReader(su2MeshPath);
                string? line;
                bool inElements = false;
                int elemRemaining = 0;
                int hexaCount = 0;
                int prismCount = 0;

                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith("%")) continue;

                    if (!inElements)
                    {
                        if (trimmed.StartsWith("NELEM", StringComparison.OrdinalIgnoreCase))
                        {
                            int eq = trimmed.IndexOf('=');
                            if (eq >= 0)
                            {
                                string val = trimmed.Substring(eq + 1).Trim();
                                if (int.TryParse(val, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                                {
                                    inElements = true;
                                    elemRemaining = count;
                                }
                            }
                        }
                    }
                    else
                    {
                        if (trimmed.StartsWith("NPOIN", StringComparison.OrdinalIgnoreCase) ||
                            trimmed.StartsWith("MARKER", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }

                        int spaceIdx = trimmed.IndexOfAny(new[] { ' ', '\t' });
                        string typeToken = spaceIdx > 0 ? trimmed.Substring(0, spaceIdx) : trimmed;
                        if (int.TryParse(typeToken, NumberStyles.Integer, CultureInfo.InvariantCulture, out int typeId))
                        {
                            if (typeId == 12) hexaCount++;
                            else if (typeId == 13) prismCount++;
                        }

                        elemRemaining--;
                        if (elemRemaining <= 0) break;
                    }
                }

                if (hexaCount + prismCount == 0) return float.NaN;
                return (float)hexaCount / (float)(hexaCount + prismCount);
            }
            catch
            {
                return float.NaN;
            }
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

        /// <summary>Zahlen fürs .geo-Skript immer mit Punkt als Dezimaltrenner.</summary>
        private static string Format(float value) => value.ToString(CultureInfo.InvariantCulture);
    }
}
