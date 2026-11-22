using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Generates 3D <c>.geo</c> scripts for Gmsh in Threshold, Prism, or Hexa mode,
    /// including two-step wall meshing and refinement fields.
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
        /// Builds the angle ladder for step 1 (<c>ClassifySurfaces</c>): deduplicates
        /// <c>[configuredAngleDeg, 30, 20, 50]</c> over valid angles (<c>&gt; 0 &amp;&amp; &lt; 180</c>).
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
                throw new InvalidOperationException($"Unknown BoundaryLayerMode '{options.BoundaryLayerMode}'. Allowed values are 'Threshold', 'Hexa', or 'Prism'.");
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
            SymmetryAxis symAxis = StlHalfClipper.ParseConfigValue(options.SymmetryPlane);

            if (isThreshold)
            {
                WriteThresholdGeo(options, geoScriptPath, safeModelPath, safeTunnelPath, tunnelStlPath, config, safeScale, scaleString, factor, sizeMin, sizeMax, distMin, distMax, curvMinFactor, symAxis);
            }
            else
            {
                WriteStructuredWallGeo(options, flow, geoScriptPath, modelStlPath, safeModelPath, safeTunnelPath, tunnelStlPath, config, safeScale, scaleString, factor, sizeMax, distMin, distMax, curvMinFactor, isHexa, wallFieldPath, classifyAngleDeg, symAxis);
            }
        }

        private static void WriteThresholdGeo(
            GmshMesherOptions options, string geoScriptPath, string safeModelPath, string safeTunnelPath,
            string tunnelStlPath, SimulationConfig config, float safeScale, string scaleString, float factor,
            float sizeMin, float sizeMax, float distMin, float distMax, float curvMinFactor, SymmetryAxis symAxis)
        {
            string F(float v) => WakeAndDetailBoxBuilder.Format(v);
            var geoLines = new List<string>
            {
                "// --- GENERAL MESH SETTINGS ---",
                "Mesh.Optimize = 1;",
                "Mesh.OptimizeNetgen = 0;",
            };

            if (symAxis == SymmetryAxis.None)
            {
                geoLines.AddRange(new[]
                {
                    "// A) Import wind tunnel",
                    $"Merge \"{safeTunnelPath}\";",
                    "tunnel_surfs[] = Surface{:};",

                    "// B) Import part",
                    $"Merge \"{safeModelPath}\";",
                    "model_surfs[] = Surface{:};",
                    "model_surfs[] -= tunnel_surfs[];",

                    "// C) Define fluid cavity",
                    "Surface Loop(1) = {tunnel_surfs[]};",
                    "Surface Loop(2) = {model_surfs[]};",
                    "Volume(1) = {1, 2};",

                    "// D) Physical SU2 Markers",
                    "// SU2 marker name: Farfield -> MARKER_FAR",
                    "Physical Surface(\"Farfield\") = {tunnel_surfs[]};",
                    "// SU2 marker name: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                    "Physical Surface(\"Wall\") = {model_surfs[]};",
                    "// Physical Volume(\"Fluid\") = {1};",
                    "Mesh.SaveAll = 1;",
                });
            }
            else
            {
                geoLines.AddRange(new[]
                {
                    "// A) Import clipped half-model part",
                    $"Merge \"{safeModelPath}\";",
                    "CreateTopology;",
                    "model_surfs[] = Surface{:};",
                    "b_model[] = Boundary { Surface{model_surfs[]}; };",
                });

                AppendBuiltInHalfTunnel(geoLines, options, tunnelStlPath, safeScale, sizeMax, symAxis);

                geoLines.AddRange(new[]
                {
                    "// C) Symmetry plane with body cutout and fluid volume",
                    "sym_loops[] = {ll_sym_outer};",
                    "For k In {0 : #b_model[] - 1}",
                    "  ll_h = newll; Curve Loop(ll_h) = {b_model[k]};",
                    "  sym_loops[] += ll_h;",
                    "EndFor",
                    "s_sym = news; Plane Surface(s_sym) = {sym_loops[]};",
                    "If (#b_model[] > 0)",
                    "  sl_core = newsl; Surface Loop(sl_core) = {farfield_surfs[], s_sym, model_surfs[]};",
                    "  v_core = newv; Volume(v_core) = {sl_core};",
                    "Else",
                    "  sl_tunnel = newsl; Surface Loop(sl_tunnel) = {farfield_surfs[], s_sym};",
                    "  sl_model = newsl; Surface Loop(sl_model) = {model_surfs[]};",
                    "  v_core = newv; Volume(v_core) = {sl_tunnel, sl_model};",
                    "EndIf",

                    "// D) Physical SU2 Markers",
                    "// SU2 marker name: Farfield -> MARKER_FAR",
                    "Physical Surface(\"Farfield\") = {farfield_surfs[]};",
                    "// SU2 marker name: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                    "Physical Surface(\"Wall\") = {model_surfs[]};",
                    "// SU2 marker name: Symmetry -> MARKER_SYM",
                    "Physical Surface(\"Symmetry\") = {s_sym};",
                    $"Physical Volume(\"{Gmsh2DGeoScriptBuilder.FluidMarkerName}\") = {{v_core}};",
                });
            }

            geoLines.AddRange(new[]
            {
                "// ==========================================",
                "// E) DISTANCE FIELD FOR BOUNDARY LAYER (NEAR-WALL)",
                "// ==========================================",
                "Field[1] = Distance;",
                "Field[1].SurfacesList = {model_surfs[]};",

                "Field[2] = Threshold;",
                "Field[2].InField = 1;",
                $"Field[2].SizeMin = {F(sizeMin)};",
                $"Field[2].SizeMax = {F(sizeMax)};",
                $"Field[2].DistMin = {F(distMin)};",
                $"Field[2].DistMax = {F(distMax)};"
            });

            if (options.WakeRefinement)
            {
                WakeAndDetailBoxBuilder.Append3DWakeBox(geoLines, options, "model_surfs[]", factor, safeScale, sizeMax, "// F) DYNAMIC WAKE BOX");
                WakeAndDetailBoxBuilder.AppendSectionHeader(geoLines, "// G) COMBINE FIELDS");
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
            geoLines.Add("// H) Rescaling (mm -> m)");
            geoLines.Add($"Mesh.ScalingFactor = (1.0 / {scaleString}) * 0.001;");
            File.WriteAllLines(geoScriptPath, geoLines);
        }

        private static void WriteStructuredWallGeo(
            GmshMesherOptions options, FlowConditions? flow, string geoScriptPath,
            string modelStlPath, string safeModelPath, string safeTunnelPath, string tunnelStlPath,
            SimulationConfig config, float safeScale, string scaleString, float factor,
            float sizeMax, float distMin, float distMax, float curvMinFactor, bool isHexa,
            string? wallFieldPath, float? classifyAngleDeg, SymmetryAxis symAxis)
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

            // Step 1: Wall surface
            var wallLines = new List<string>
            {
                "// Step 1 of 2: Remesh body surface and save as ONE discrete surface.",
                "// Invocation: gmsh <this file> -2 -format mesh -o <Wall_...mesh>",
                $"Merge \"{safeModelPath}\";",
                $"ClassifySurfaces{{{F(effectiveClassifyAngleDeg * MathF.PI / 180.0f)}, 1, 1, Pi}};",
                "CreateGeometry;",
                "wall[] = Surface{:};",
                "// Wall cell size (global only here; in step 2 it would limit the entire tunnel)",
                $"Mesh.MeshSizeMax = {F(surfaceSize)};",
                $"Mesh.MeshSizeMin = {F(surfaceSize * curvMinFactor)};",
            };

            if (options.MeshSizeFromCurvature)
            {
                wallLines.Add($"Mesh.MeshSizeFromCurvature = {(options.CurvatureElements > 0 ? options.CurvatureElements : 20)};");
            }

            if (isHexa)
            {
                wallLines.Add("// Quads on wall only (Mesh.RecombineAll would apply globally to the tunnel as well)");
                wallLines.Add("Recombine Surface{wall[]};");
                wallLines.Add("Mesh.Algorithm = 8; // Frontal-Delaunay for Quads");
                wallLines.Add("Mesh.RecombinationAlgorithm = 2;");
            }

            wallLines.Add("// Save all patches under ONE tag -> single surface in step 2");
            wallLines.Add("Physical Surface(1) = {wall[]};");
            wallLines.Add("Mesh.SaveElementTagType = 2;");
            wallLines.Add($"General.NumThreads = {config.GmshCores};");
            File.WriteAllLines(WallScriptPath(geoScriptPath), wallLines);

            // Step 2: Boundary layer + volume
            string safeWallMesh = WallMeshPath(geoScriptPath).Replace("\\", "/");
            string cellWord = isHexa ? "hexahedra" : "prisms";
            var geoLines = new List<string>();
            if (wallFieldPath != null)
            {
                geoLines.Add("// Wall field: extrusion direction * thickness scale per vertex. Must be loaded as FIRST view (View[0]).");
                geoLines.Add($"Merge \"{wallFieldPath.Replace("\\", "/")}\";");
            }

            if (symAxis == SymmetryAxis.None)
            {
                geoLines.AddRange(new[]
                {
                    "// Step 2 of 2: Extrude boundary layer, fill core with tetrahedra.",
                    "Mesh.Optimize = 1;",
                    "Mesh.OptimizeNetgen = 0;",
                    "// A) Remeshed wall from step 1 (single discrete surface)",
                    $"Merge \"{safeWallMesh}\";",
                    "wall[] = Surface{:};",
                    $"// B) Structured boundary layer: {layerCount} layers ({cellWord}), first cell y1 = {F(y1Geo)}",
                    "Geometry.ExtrudeReturnLateralEntities = 0;",
                    $"Extrude {{ Surface{{wall[]}}; Layers{{ {{{subDivs}}}, {{{heights}}} }}; {usingView}Recombine; }}",
                    "tops[] = Surface{:};",
                    "tops[] -= wall[];",
                    "bl_vols[] = Volume{:};",
                    "// C) Import wind tunnel afterwards",
                    $"Merge \"{safeTunnelPath}\";",
                    "tunnel_surfs[] = Surface{:};",
                    "tunnel_surfs[] -= wall[];",
                    "tunnel_surfs[] -= tops[];",
                    "// D) Core volume between tunnel and boundary-layer top",
                    "sl_tunnel = newsl; Surface Loop(sl_tunnel) = {tunnel_surfs[]};",
                    "sl_bl = newsl; Surface Loop(sl_bl) = {tops[]};",
                    "v_core = newv; Volume(v_core) = {sl_tunnel, sl_bl};",
                    "// E) Physical SU2 Markers",
                    "// SU2 marker name: Farfield -> MARKER_FAR",
                    "Physical Surface(\"Farfield\") = {tunnel_surfs[]};",
                    "// SU2 marker name: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                    "Physical Surface(\"Wall\") = {wall[]};",
                    "// Fluid collects all volumes; Gmsh also writes it as a marker, which is stripped afterwards.",
                    $"Physical Volume(\"{Gmsh2DGeoScriptBuilder.FluidMarkerName}\") = {{v_core, bl_vols[]}};",
                });
            }
            else
            {
                geoLines.AddRange(new[]
                {
                    "// Step 2 of 2: Extrude symmetry-aligned boundary layer, build half-tunnel + symmetry core.",
                    "Mesh.Optimize = 1;",
                    "Mesh.OptimizeNetgen = 0;",
                    "// A) Remeshed half-model wall from step 1",
                    $"Merge \"{safeWallMesh}\";",
                    "CreateTopology;",
                    "wall[] = Surface{:};",
                    $"// B) Structured boundary layer: {layerCount} layers ({cellWord}), first cell y1 = {F(y1Geo)}",
                    "Geometry.ExtrudeReturnLateralEntities = 1;",
                    $"ext[] = Extrude {{ Surface{{wall[]}}; Layers{{ {{{subDivs}}}, {{{heights}}} }}; {usingView}Recombine; }};",
                    "tops[] = {ext[0]};",
                    "bl_vols[] = {ext[1]};",
                    "bl_laterals[] = {};",
                    "For k In {2 : #ext[] - 1}",
                    "  bl_laterals[] += ext[k];",
                    "EndFor",
                    "b_tops[] = Boundary { Surface{tops[]}; };",
                });

                AppendBuiltInHalfTunnel(geoLines, options, tunnelStlPath, safeScale, sizeMax, symAxis);

                geoLines.AddRange(new[]
                {
                    "// D) Symmetry plane core surface and core volume",
                    "sym_loops[] = {ll_sym_outer};",
                    "For k In {0 : #b_tops[] - 1}",
                    "  ll_h = newll; Curve Loop(ll_h) = {b_tops[k]};",
                    "  sym_loops[] += ll_h;",
                    "EndFor",
                    "s_sym_core = news; Plane Surface(s_sym_core) = {sym_loops[]};",
                    "If (#b_tops[] > 0)",
                    "  sl_core = newsl; Surface Loop(sl_core) = {farfield_surfs[], s_sym_core, tops[]};",
                    "  v_core = newv; Volume(v_core) = {sl_core};",
                    "Else",
                    "  sl_tunnel = newsl; Surface Loop(sl_tunnel) = {farfield_surfs[], s_sym_core};",
                    "  sl_bl = newsl; Surface Loop(sl_bl) = {tops[]};",
                    "  v_core = newv; Volume(v_core) = {sl_tunnel, sl_bl};",
                    "EndIf",
                    "// E) Physical SU2 Markers",
                    "// SU2 marker name: Farfield -> MARKER_FAR",
                    "Physical Surface(\"Farfield\") = {farfield_surfs[]};",
                    "// SU2 marker name: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                    "Physical Surface(\"Wall\") = {wall[]};",
                    "// SU2 marker name: Symmetry -> MARKER_SYM",
                    "Physical Surface(\"Symmetry\") = {s_sym_core, bl_laterals[]};",
                    "// Fluid collects all volumes; Gmsh also writes it as a marker, which is stripped afterwards.",
                    $"Physical Volume(\"{Gmsh2DGeoScriptBuilder.FluidMarkerName}\") = {{v_core, bl_vols[]}};",
                });
            }

            geoLines.AddRange(new[]
            {
                "// ==========================================",
                "// F) DISTANCE FIELD FOR CORE (NEAR-WALL)",
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
                WakeAndDetailBoxBuilder.Append3DWakeBox(geoLines, options, "wall[]", factor, safeScale, sizeMax, "// G) DYNAMIC WAKE BOX");
            }

            Append3DBackgroundFields(geoLines, options, "wall[]", factor, safeScale);
            geoLines.Add($"Mesh.MeshSizeMax = {F(sizeMax)};");
            geoLines.Add("Mesh.MeshSizeExtendFromBoundary = 0;");
            geoLines.Add($"General.NumThreads = {config.GmshCores};");
            geoLines.Add("// H) Rescaling (mm -> m)");
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

        private static void AppendBuiltInHalfTunnel(
            List<string> geoLines,
            GmshMesherOptions options,
            string tunnelStlPath,
            float safeScale,
            float sizeMax,
            SymmetryAxis symAxis)
        {
            string F(float v) => WakeAndDetailBoxBuilder.Format(v);
            (float minX, float maxX, float minY, float maxY, float minZ, float maxZ) =
                ResolveHalfTunnelBounds(options, tunnelStlPath, safeScale, symAxis);

            geoLines.Add($"// C0) Built-in half-box wind tunnel ({symAxis})");
            if (symAxis == SymmetryAxis.Y0)
            {
                geoLines.AddRange(new[]
                {
                    $"p1 = newp; Point(p1) = {{{F(minX)}, 0, {F(minZ)}, {F(sizeMax)}}};",
                    $"p2 = newp; Point(p2) = {{{F(maxX)}, 0, {F(minZ)}, {F(sizeMax)}}};",
                    $"p3 = newp; Point(p3) = {{{F(maxX)}, 0, {F(maxZ)}, {F(sizeMax)}}};",
                    $"p4 = newp; Point(p4) = {{{F(minX)}, 0, {F(maxZ)}, {F(sizeMax)}}};",
                    $"p5 = newp; Point(p5) = {{{F(minX)}, {F(maxY)}, {F(minZ)}, {F(sizeMax)}}};",
                    $"p6 = newp; Point(p6) = {{{F(maxX)}, {F(maxY)}, {F(minZ)}, {F(sizeMax)}}};",
                    $"p7 = newp; Point(p7) = {{{F(maxX)}, {F(maxY)}, {F(maxZ)}, {F(sizeMax)}}};",
                    $"p8 = newp; Point(p8) = {{{F(minX)}, {F(maxY)}, {F(maxZ)}, {F(sizeMax)}}};",
                });
            }
            else
            {
                geoLines.AddRange(new[]
                {
                    $"p1 = newp; Point(p1) = {{{F(minX)}, {F(minY)}, 0, {F(sizeMax)}}};",
                    $"p2 = newp; Point(p2) = {{{F(maxX)}, {F(minY)}, 0, {F(sizeMax)}}};",
                    $"p3 = newp; Point(p3) = {{{F(maxX)}, {F(maxY)}, 0, {F(sizeMax)}}};",
                    $"p4 = newp; Point(p4) = {{{F(minX)}, {F(maxY)}, 0, {F(sizeMax)}}};",
                    $"p5 = newp; Point(p5) = {{{F(minX)}, {F(minY)}, {F(maxZ)}, {F(sizeMax)}}};",
                    $"p6 = newp; Point(p6) = {{{F(maxX)}, {F(minY)}, {F(maxZ)}, {F(sizeMax)}}};",
                    $"p7 = newp; Point(p7) = {{{F(maxX)}, {F(maxY)}, {F(maxZ)}, {F(sizeMax)}}};",
                    $"p8 = newp; Point(p8) = {{{F(minX)}, {F(maxY)}, {F(maxZ)}, {F(sizeMax)}}};",
                });
            }

            geoLines.AddRange(new[]
            {
                "l1 = newl; Line(l1) = {p1, p2};",
                "l2 = newl; Line(l2) = {p2, p3};",
                "l3 = newl; Line(l3) = {p3, p4};",
                "l4 = newl; Line(l4) = {p4, p1};",
                "l5 = newl; Line(l5) = {p5, p6};",
                "l6 = newl; Line(l6) = {p6, p7};",
                "l7 = newl; Line(l7) = {p7, p8};",
                "l8 = newl; Line(l8) = {p8, p5};",
                "l9 = newl; Line(l9) = {p1, p5};",
                "l10 = newl; Line(l10) = {p2, p6};",
                "l11 = newl; Line(l11) = {p3, p7};",
                "l12 = newl; Line(l12) = {p4, p8};",
                "ll_far_xm = newll; Curve Loop(ll_far_xm) = {-l4, l12, l8, -l9}; s_xm = news; Plane Surface(s_xm) = {ll_far_xm};",
                "ll_far_xp = newll; Curve Loop(ll_far_xp) = {-l2, l10, l6, -l11}; s_xp = news; Plane Surface(s_xp) = {ll_far_xp};",
                "ll_far_m = newll; Curve Loop(ll_far_m) = {l1, l10, -l5, -l9}; s_m = news; Plane Surface(s_m) = {ll_far_m};",
                "ll_far_p = newll; Curve Loop(ll_far_p) = {l3, l12, -l7, -l11}; s_p = news; Plane Surface(s_p) = {ll_far_p};",
                "ll_far_top = newll; Curve Loop(ll_far_top) = {l5, l6, l7, l8}; s_top = news; Plane Surface(s_top) = {ll_far_top};",
                "farfield_surfs[] = {s_xm, s_xp, s_m, s_p, s_top};",
                "ll_sym_outer = newll; Curve Loop(ll_sym_outer) = {l1, l2, l3, l4};",
            });
        }

        private static (float MinX, float MaxX, float MinY, float MaxY, float MinZ, float MaxZ) ResolveHalfTunnelBounds(
            GmshMesherOptions options, string tunnelStlPath, float safeScale, SymmetryAxis symAxis)
        {
            if (!string.IsNullOrWhiteSpace(tunnelStlPath) && File.Exists(tunnelStlPath))
            {
                try
                {
                    MeshBounds bounds = StlWriter.ReadBounds(tunnelStlPath);
                    if (!bounds.IsEmpty)
                    {
                        return (
                            bounds.Min.X,
                            bounds.Max.X,
                            symAxis == SymmetryAxis.Y0 ? 0f : bounds.Min.Y,
                            bounds.Max.Y,
                            symAxis == SymmetryAxis.Z0 ? 0f : bounds.Min.Z,
                            bounds.Max.Z);
                    }
                }
                catch { }
            }

            float minX = (options.TunnelCenterX - options.TunnelSizeX * 0.5f) * safeScale;
            float maxX = (options.TunnelCenterX + options.TunnelSizeX * 0.5f) * safeScale;
            float minY = symAxis == SymmetryAxis.Y0 ? 0f : (options.TunnelCenterY - options.TunnelSizeY * 0.5f) * safeScale;
            float maxY = (symAxis == SymmetryAxis.Y0
                ? MathF.Max(options.TunnelCenterY + options.TunnelSizeY * 0.5f, options.TunnelSizeY * 0.5f)
                : options.TunnelCenterY + options.TunnelSizeY * 0.5f) * safeScale;
            float minZ = symAxis == SymmetryAxis.Z0 ? 0f : (options.TunnelCenterZ - options.TunnelSizeZ * 0.5f) * safeScale;
            float maxZ = (symAxis == SymmetryAxis.Z0
                ? MathF.Max(options.TunnelCenterZ + options.TunnelSizeZ * 0.5f, options.TunnelSizeZ * 0.5f)
                : options.TunnelCenterZ + options.TunnelSizeZ * 0.5f) * safeScale;

            return (minX, maxX, minY, maxY, minZ, maxZ);
        }
    }
}
