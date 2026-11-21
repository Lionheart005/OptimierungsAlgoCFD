using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Generiert 3D-<c>.geo</c>-Skripte für Gmsh im Threshold-, Prism- oder Hexa-Modus
    /// inklusive zweistufiger Wandvernetzung und Verfeinerungsfeldern.
    /// </summary>
    internal static class Gmsh3DGeoScriptBuilder
    {
        public static string WallFieldPath(string geoScriptPath) => Path.ChangeExtension(WallScriptPath(geoScriptPath), ".pos");

        public static string WallScriptPath(string geoScriptPath)
        {
            string name = Path.GetFileNameWithoutExtension(geoScriptPath);
            if (name.StartsWith("Meshing_", StringComparison.Ordinal)) name = name.Substring("Meshing_".Length);
            return Path.Combine(Path.GetDirectoryName(geoScriptPath) ?? string.Empty, $"Wall_{name}.geo");
        }

        public static string WallMeshPath(string geoScriptPath) => Path.ChangeExtension(WallScriptPath(geoScriptPath), ".mesh");

        /// <summary>
        /// Bildet die Winkel-Leiter für Schritt 1 (<c>ClassifySurfaces</c>): dedupliziert
        /// <c>[configuredAngleDeg, 30, 20, 50]</c> über gültige Winkel (<c>&gt; 0 &amp;&amp; &lt; 180</c>).
        /// </summary>
        public static IReadOnlyList<float> BuildClassifyAngleLadder(float configuredAngleDeg)
        {
            var ladder = new List<float>(4);
            foreach (float candidate in new[] { configuredAngleDeg, 30f, 20f, 50f })
            {
                if (!float.IsFinite(candidate) || candidate <= 0f || candidate >= 180f)
                    continue;
                if (ladder.Exists(existing => MathF.Abs(existing - candidate) < 1e-4f))
                    continue;
                ladder.Add(candidate);
            }
            return ladder;
        }

        public static BoundaryLayerSpec ComputeBoundaryLayerSpec(
            GmshMesherOptions options, FlowConditions? flow, SimulationConfig config, string modelStlPath, float scaleFactor)
        {
            float safeScale = GmshTunnelBuilder.SanitizeScaleFactor(scaleFactor);
            FlowConditions effectiveFlow = flow ?? FlowConditions.From(config, new Su2SolverOptions());
            float lengthMm = 50.0f;
            if (File.Exists(modelStlPath))
            {
                try
                {
                    var bounds = StlWriter.ReadBounds(modelStlPath);
                    if (!bounds.IsEmpty && bounds.Size.X > 0f)
                        lengthMm = bounds.Size.X / safeScale;
                }
                catch { }
            }

            return BoundaryLayerCalculator.Calculate(
                effectiveFlow.Density, effectiveFlow.Velocity, effectiveFlow.DynamicViscosity, lengthMm,
                options.TargetYPlus, options.BoundaryLayerGrowthRate,
                options.BoundaryLayerMinLayers, options.BoundaryLayerLayers, options.BoundaryLayerCoverage);
        }

        public static float LayerThicknessMm(BoundaryLayerSpec spec, int layers)
        {
            double r = spec.GrowthRate;
            return (float)(spec.FirstLayerHeightMm * (Math.Pow(r, layers) - 1.0) / (r - 1.0));
        }

        public static void WriteGeoScript(
            GmshMesherOptions options,
            FlowConditions? flow,
            string geoScriptPath,
            string modelStlPath,
            string tunnelStlPath,
            SimulationConfig config,
            float scaleFactor,
            float refinementFactor,
            string? wallFieldPath,
            float? classifyAngleDeg = null)
        {
            string mode = (options.BoundaryLayerMode ?? "Threshold").Trim();
            bool isThreshold = mode.Equals("Threshold", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(mode);
            bool isHexa = mode.Equals("Hexa", StringComparison.OrdinalIgnoreCase);
            bool isPrism = mode.Equals("Prism", StringComparison.OrdinalIgnoreCase);

            if (!isThreshold && !isHexa && !isPrism)
            {
                throw new InvalidOperationException($"Unbekannter BoundaryLayerMode '{options.BoundaryLayerMode}'. Erlaubt sind 'Threshold', 'Hexa' oder 'Prism'.");
            }

            string safeModelPath = modelStlPath.Replace("\\", "/");
            string safeTunnelPath = tunnelStlPath.Replace("\\", "/");
            float safeScale = GmshTunnelBuilder.SanitizeScaleFactor(scaleFactor);
            string scaleString = safeScale.ToString(CultureInfo.InvariantCulture);

            float factor = refinementFactor > 0f ? refinementFactor : 1.0f;
            float sizeMin = options.BoundaryLayerSizeMin * factor * safeScale;
            float sizeMax = options.BoundaryLayerSizeMax * factor * safeScale;
            float distMin = options.BoundaryLayerDistMin * safeScale;
            float distMax = options.BoundaryLayerDistMax * safeScale;
            float curvMinFactor = Math.Clamp(options.CurvatureMinSizeFactor, 0.1f, 1.0f);

            if (isThreshold)
            {
                WriteThresholdGeo(options, geoScriptPath, safeModelPath, safeTunnelPath, config, safeScale, scaleString, factor, sizeMin, sizeMax, distMin, distMax, curvMinFactor);
            }
            else
            {
                WriteStructuredWallGeo(options, flow, geoScriptPath, modelStlPath, safeModelPath, safeTunnelPath, config, safeScale, scaleString, factor, sizeMax, distMin, distMax, curvMinFactor, isHexa, wallFieldPath, classifyAngleDeg);
            }
        }

        private static void WriteThresholdGeo(
            GmshMesherOptions options, string geoScriptPath, string safeModelPath, string safeTunnelPath,
            SimulationConfig config, float safeScale, string scaleString, float factor,
            float sizeMin, float sizeMax, float distMin, float distMax, float curvMinFactor)
        {
            string F(float v) => WakeAndDetailBoxBuilder.Format(v);
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
                $"Field[2].SizeMin = {F(sizeMin)};",
                $"Field[2].SizeMax = {F(sizeMax)};",
                $"Field[2].DistMin = {F(distMin)};",
                $"Field[2].DistMax = {F(distMax)};"
            };

            if (options.WakeRefinement)
            {
                WakeAndDetailBoxBuilder.Append3DWakeBox(geoLines, options, "model_surfs[]", factor, safeScale, sizeMax, "// F) DYNAMISCHE WAKE-BOX (NACHLAUF)");
                WakeAndDetailBoxBuilder.AppendSectionHeader(geoLines, "// G) FELDER INTELLIGENT KOMBINIEREN");
            }

            Append3DBackgroundFields(geoLines, options, "model_surfs[]", factor, safeScale);
            geoLines.Add("Mesh.CharacteristicLengthExtendFromBoundary = 0;");

            if (options.MeshSizeFromCurvature)
            {
                geoLines.Add("Mesh.MeshSizeFromCurvature = 1;");
                geoLines.Add($"Mesh.MeshSizeMin = {F(sizeMin * curvMinFactor)};");
                geoLines.Add($"Mesh.MeshSizeMax = {F(sizeMax)};");
            }

            geoLines.Add($"General.NumThreads = {config.GmshCores};");
            geoLines.Add("// H) Re-Skalierung");
            geoLines.Add($"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;");
            File.WriteAllLines(geoScriptPath, geoLines);
        }

        private static void WriteStructuredWallGeo(
            GmshMesherOptions options, FlowConditions? flow, string geoScriptPath,
            string modelStlPath, string safeModelPath, string safeTunnelPath, SimulationConfig config,
            float safeScale, string scaleString, float factor, float sizeMax, float distMin, float distMax,
            float curvMinFactor, bool isHexa, string? wallFieldPath, float? classifyAngleDeg)
        {
            string F(float v) => WakeAndDetailBoxBuilder.Format(v);
            BoundaryLayerSpec spec = ComputeBoundaryLayerSpec(options, flow, config, modelStlPath, safeScale);
            int layerCount = Math.Max(1, spec.LayerCount);

            float y1Geo = spec.FirstLayerHeightMm * safeScale;
            var layerHeights = new List<float>(layerCount);
            double r = spec.GrowthRate;
            for (int i = 1; i <= layerCount; i++)
            {
                layerHeights.Add((float)(y1Geo * (Math.Pow(r, i) - 1.0) / (r - 1.0)));
            }

            string subDivs = string.Join(", ", Enumerable.Repeat("1", layerCount));
            string usingView = wallFieldPath != null ? "Using View[0]; " : string.Empty;
            string heights = string.Join(", ", layerHeights.Select(h => F(h)));
            float surfaceSize = (options.SurfaceCellSize > 0f ? options.SurfaceCellSize : options.BoundaryLayerSizeMin) * factor * safeScale;
            float effectiveClassifyAngleDeg = classifyAngleDeg ?? options.ClassifyAngleDeg;

            // Schritt 1: Wandoberfläche
            var wallLines = new List<string>
            {
                "// Schritt 1 von 2: Koerperoberflaeche neu vernetzen und als EINE Flaeche speichern.",
                "// Aufruf: gmsh <diese Datei> -2 -format mesh -o <Wall_...mesh>",
                $"Merge \"{safeModelPath}\";",
                $"ClassifySurfaces{{{F(effectiveClassifyAngleDeg * MathF.PI / 180.0f)}, 1, 1, Pi}};",
                "CreateGeometry;",
                "wall[] = Surface{:};",
                "// Wandzellgroesse (nur hier global, in Schritt 2 waere es das Maximum fuer den ganzen Kanal)",
                $"Mesh.MeshSizeMax = {F(surfaceSize)};",
                $"Mesh.MeshSizeMin = {F(surfaceSize * curvMinFactor)};",
            };

            if (options.MeshSizeFromCurvature)
            {
                wallLines.Add($"Mesh.MeshSizeFromCurvature = {(options.CurvatureElements > 0 ? options.CurvatureElements : 20)};");
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

            // Schritt 2: Wandschicht + Volumen
            string safeWallMesh = WallMeshPath(geoScriptPath).Replace("\\", "/");
            string cellWord = isHexa ? "Quader (Hexaeder)" : "Prismen";
            var geoLines = new List<string>();
            if (wallFieldPath != null)
            {
                geoLines.Add("// Wandfeld: Extrusionsrichtung x Dickenfaktor je Knoten. Muss als ERSTE Ansicht (View[0]) eingelesen werden.");
                geoLines.Add($"Merge \"{wallFieldPath.Replace("\\", "/")}\";");
            }

            geoLines.AddRange(new[]
            {
                "// Schritt 2 von 2: Wandschicht extrudieren, Kern mit Tetraedern fuellen.",
                "Mesh.Optimize = 1;",
                "Mesh.OptimizeNetgen = 0;",
                "// A) Neu vernetzte Wand aus Schritt 1 (eine diskrete Flaeche)",
                $"Merge \"{safeWallMesh}\";",
                "wall[] = Surface{:};",
                $"// B) Strukturierte Wandschicht: {layerCount} Schichten {cellWord}, erste Zelle y1 = {F(y1Geo)}",
                "Geometry.ExtrudeReturnLateralEntities = 0;",
                $"Extrude {{ Surface{{wall[]}}; Layers{{ {{{subDivs}}}, {{{heights}}} }}; {usingView}Recombine; }}",
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
                $"Physical Volume(\"{Gmsh2DGeoScriptBuilder.FluidMarkerName}\") = {{v_core, bl_vols[]}};",
                "// ==========================================",
                "// F) DISTANZFELD FUER DEN KERN (WANDNAEHE)",
                "// ==========================================",
                "Field[1] = Distance;",
                "Field[1].SurfacesList = {wall[]};",
                "Field[2] = Threshold;",
                "Field[2].InField = 1;",
                $"Field[2].SizeMin = {F(surfaceSize)};",
                $"Field[2].SizeMax = {F(sizeMax)};",
                $"Field[2].DistMin = {F(distMin)};",
                $"Field[2].DistMax = {F(distMax)};",
            });

            if (options.WakeRefinement)
            {
                WakeAndDetailBoxBuilder.Append3DWakeBox(geoLines, options, "wall[]", factor, safeScale, sizeMax, "// G) DYNAMISCHE WAKE-BOX (NACHLAUF)");
            }

            Append3DBackgroundFields(geoLines, options, "wall[]", factor, safeScale);
            geoLines.Add($"Mesh.MeshSizeMax = {F(sizeMax)};");
            geoLines.Add("Mesh.MeshSizeExtendFromBoundary = 0;");
            geoLines.Add($"General.NumThreads = {config.GmshCores};");
            geoLines.Add("// H) Re-Skalierung");
            geoLines.Add($"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;");
            File.WriteAllLines(geoScriptPath, geoLines);
        }

        private static void Append3DBackgroundFields(
            List<string> geoLines, GmshMesherOptions options, string surfaces, float factor, float safeScale)
        {
            var backgroundFields = new List<string> { "2" };
            if (options.WakeRefinement) backgroundFields.Add("3");
            if (options.DetailBox)
            {
                WakeAndDetailBoxBuilder.Append3DDetailBox(geoLines, options, surfaces, factor, safeScale);
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
        }
    }
}
