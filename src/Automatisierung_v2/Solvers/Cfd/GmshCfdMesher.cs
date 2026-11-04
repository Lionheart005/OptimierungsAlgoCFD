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

            WriteGeoScript(
                geoScriptPath,
                request.GeometryPath,
                tunnelStlPath,
                request.Config,
                request.ScaleFactor,
                request.RefinementFactor);

            Process mesher = new Process();
            mesher.StartInfo.FileName = request.Config.GmshPath; 
            mesher.StartInfo.Arguments = $"\"{geoScriptPath}\" -3 -nt {request.Config.GmshCores} -format su2 -v 0 -o \"{outputMeshPath}\"";
            mesher.StartInfo.UseShellExecute = false;
            mesher.StartInfo.CreateNoWindow = true;

            try
            {
                mesher.Start();
                mesher.WaitForExit();
            }
            catch (Exception ex)
            {
                CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                throw new Exception($"Gmsh konnte nicht gestartet werden: {ex.Message}. .geo-Skript nach FehlerLogs kopiert.", ex);
            }

            if (mesher.ExitCode != 0 || !File.Exists(outputMeshPath))
            {
                CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                throw new Exception($"Gmsh konnte {Path.GetFileName(outputMeshPath)} nicht erstellen (ExitCode {mesher.ExitCode}). .geo-Skript nach FehlerLogs kopiert.");
            }

            string mode = (_options.BoundaryLayerMode ?? "Threshold").Trim();
            bool isHexa = mode.Equals("Hexa", StringComparison.OrdinalIgnoreCase);
            bool isPrism = mode.Equals("Prism", StringComparison.OrdinalIgnoreCase);

            if (isHexa || isPrism)
            {
                float hexShare = ReadSu2BoundaryLayerHexShare(outputMeshPath);
                FlowConditions flow = _flow ?? FlowConditions.From(request.Config, new Su2SolverOptions());
                float lengthMm = 50.0f;
                if (File.Exists(request.GeometryPath))
                {
                    try
                    {
                        var bounds = StlWriter.ReadBounds(request.GeometryPath);
                        if (!bounds.IsEmpty && bounds.Size.X > 0f)
                            lengthMm = bounds.Size.X / request.ScaleFactor;
                    }
                    catch { }
                }

                var spec = BoundaryLayerCalculator.Calculate(
                    flow.Density, flow.Velocity, flow.DynamicViscosity, lengthMm,
                    _options.TargetYPlus, _options.BoundaryLayerGrowthRate,
                    _options.BoundaryLayerMinLayers, _options.BoundaryLayerLayers);

                string hexPart = float.IsFinite(hexShare) ? $", {hexShare * 100f:0} % Quader" : string.Empty;
                Console.WriteLine($"       -> Grenzschicht: {mode}, y1 = {spec.FirstLayerHeightMm.ToString("0.0000", CultureInfo.InvariantCulture)} mm (y+ {spec.TargetYPlus.ToString("0.#", CultureInfo.InvariantCulture)} bei Re {spec.ReynoldsNumber.ToString("0.0e0", CultureInfo.InvariantCulture)}), {spec.LayerCount} Schichten, Dicke {spec.TotalThicknessMm.ToString("0.00", CultureInfo.InvariantCulture)} mm{hexPart}");
            }

            Console.WriteLine($"       -> [OK] 3D-Volumennetz generiert ");
            return outputMeshPath;
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
                    geoLines.Add("Field[4] = Min;");
                    geoLines.Add("Field[4].FieldsList = {2, 3};");
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

                var spec = BoundaryLayerCalculator.Calculate(
                    flow.Density, flow.Velocity, flow.DynamicViscosity, lengthMm,
                    _options.TargetYPlus, _options.BoundaryLayerGrowthRate,
                    _options.BoundaryLayerMinLayers, _options.BoundaryLayerLayers);

                float y1Geo = spec.FirstLayerHeightMm * scaleFactor;
                var layerHeights = new List<float>(spec.LayerCount);
                double r = spec.GrowthRate;
                for (int i = 1; i <= spec.LayerCount; i++)
                {
                    double h = y1Geo * (Math.Pow(r, i) - 1.0) / (r - 1.0);
                    layerHeights.Add((float)h);
                }

                string subDivs = string.Join(", ", Enumerable.Repeat("1", spec.LayerCount));
                string heights = string.Join(", ", layerHeights.Select(h => Format(h)));

                float surfaceSize = (_options.SurfaceCellSize > 0f ? _options.SurfaceCellSize : _options.BoundaryLayerSizeMin) * factor;

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

                    "// B1) Oberflaechen-Neuvernetzung (ClassifySurfaces)",
                    $"ClassifySurfaces{{{Format(_options.ClassifyAngleDeg * MathF.PI / 180.0f)}, 1, 1, Pi}};",
                    "CreateGeometry;"
                };

                if (isHexa)
                {
                    geoLines.Add("// B2) Oberflaeche in Vierecke rekombinieren (Quad-Dominant)");
                    geoLines.Add("Mesh.RecombineAll = 1;");
                    geoLines.Add("Mesh.Algorithm = 8; // Frontal-Delaunay for Quads");
                    geoLines.Add("Mesh.RecombinationAlgorithm = 2;");
                    geoLines.Add($"Extrude {{ Surface{{model_surfs[]}}; Layers{{ {{{subDivs}}}, {{{heights}}} }}; Recombine; }}");
                }
                else
                {
                    geoLines.Add("// B2) Strukturierte Prismen-Extrusion");
                    geoLines.Add($"Extrude {{ Surface{{model_surfs[]}}; Layers{{ {{{subDivs}}}, {{{heights}}} }}; }}");
                }

                geoLines.Add("// C) Hohlraum definieren");
                geoLines.Add("Surface Loop(1) = {tunnel_surfs[]};");
                geoLines.Add("Surface Loop(2) = {model_surfs[]};");
                geoLines.Add("Volume(1) = {1, 2};");

                geoLines.Add("// D) Physische SU2 Marker");
                geoLines.Add("// Dieser Name ist der SU2-Marker: Farfield -> MARKER_FAR");
                geoLines.Add("Physical Surface(\"Farfield\") = {tunnel_surfs[]};");
                geoLines.Add("// Dieser Name ist der SU2-Marker: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING");
                geoLines.Add("Physical Surface(\"Wall\") = {model_surfs[]};");
                geoLines.Add("Mesh.SaveAll = 1;");

                geoLines.Add("// ==========================================");
                geoLines.Add("// E) DISTANZFELD FUER GRENZSCHICHT (WANDNAEHE)");
                geoLines.Add("// ==========================================");
                geoLines.Add($"Mesh.CharacteristicLengthMin = {Format(surfaceSize * 0.5f)};");
                geoLines.Add("Field[1] = Distance;");
                geoLines.Add("Field[1].SurfacesList = {model_surfs[]};");
                geoLines.Add("Field[2] = Threshold;");
                geoLines.Add("Field[2].InField = 1;");
                geoLines.Add($"Field[2].SizeMin = {Format(surfaceSize)};");
                geoLines.Add($"Field[2].SizeMax = {Format(sizeMax)};");
                geoLines.Add($"Field[2].DistMin = {Format(_options.BoundaryLayerDistMin)};");
                geoLines.Add($"Field[2].DistMax = {Format(_options.BoundaryLayerDistMax)};");

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
                    geoLines.Add("Field[4] = Min;");
                    geoLines.Add("Field[4].FieldsList = {2, 3};");
                    geoLines.Add("Background Field = 4;");
                }
                else
                {
                    geoLines.Add("Background Field = 2;");
                }

                geoLines.Add("Mesh.CharacteristicLengthExtendFromBoundary = 0;");

                if (_options.MeshSizeFromCurvature)
                {
                    geoLines.Add($"Mesh.MeshSizeFromCurvature = {(_options.CurvatureElements > 0 ? _options.CurvatureElements : 20)};");
                }

                geoLines.Add($"General.NumThreads = {config.GmshCores};");
                geoLines.Add("// H) Re-Skalierung");
                geoLines.Add($"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;");

                File.WriteAllLines(geoScriptPath, geoLines);
            }
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
