using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// CFD mesher based on Gmsh. Generates a volume mesh for CFD simulations.
    /// The wind tunnel is generated here because it belongs to the simulation domain.
    /// </summary>
    public class GmshCfdMesher : IMeshGenerator, IMeshMetricsSource, IMeshResolutionTarget
    {
        private readonly GmshMesherOptions _options;
        private readonly FlowConditions? _flow;
        private readonly GmshTunnelBuilder _tunnelBuilder;
        private Dictionary<string, float> _lastMetrics = new();

        /// <summary>
        /// Diagnostics of the last generated mesh: layer count, thickness, first cell height,
        /// fallback ladder stage (1 = A, 2 = B, 3 = fallback mesh), tilt, minimum thickness scale,
        /// thinned surface share, and fallback flag. Empty in "Threshold" mode (no structured wall layer).
        /// </summary>
        public IReadOnlyDictionary<string, float> LastMeshMetrics => _lastMetrics;

        /// <param name="options">Configuration from the project's solvers/gmsh.json; defaults apply if omitted.</param>
        /// <param name="flow">Physical flow conditions provided for boundary-layer calculations.</param>
        public GmshCfdMesher(GmshMesherOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new GmshMesherOptions();
            _flow = flow;
            _tunnelBuilder = new GmshTunnelBuilder(_options);
        }

        public GmshMesherOptions Options => _options;
        public FlowConditions? Flow => _flow;

        /// <summary>Target surface cell size (mm) at mesh factor 1.0 for <see cref="VoxelResolutionAdvisor"/>.</summary>
        public float TargetSurfaceCellSizeMm => _options.SurfaceCellSize > 0f ? _options.SurfaceCellSize : _options.BoundaryLayerSizeMin;

        /// <summary>Generates the static reference wind tunnel (without geometry-kernel dependency).</summary>
        public string EnsureTunnel(string workingDirectory) => _tunnelBuilder.EnsureTunnel(workingDirectory);

        /// <summary>
        /// Generates the physical wind tunnel in the PicoGK workspace (scaled by <paramref name="scaleFactor"/>),
        /// guaranteeing minimum clearances and maximum blockage ratio.
        /// </summary>
        public string EnsureTunnel(
            string workingDirectory,
            MeshBounds? modelBoundsPicoGk,
            float scaleFactor,
            float? realFrontalAreaMm2 = null)
            => _tunnelBuilder.EnsureTunnel(workingDirectory, modelBoundsPicoGk, scaleFactor, realFrontalAreaMm2, _lastMetrics);

        /// <summary>Computes effective physical tunnel dimensions in real-world millimeters.</summary>
        public TunnelSpec ResolveEffectiveTunnel(
            MeshBounds? modelBoundsPicoGk,
            float scaleFactor,
            float? realFrontalAreaMm2 = null)
            => _tunnelBuilder.ResolveEffectiveTunnel(modelBoundsPicoGk, scaleFactor, realFrontalAreaMm2);

        public string GenerateMesh(
            string modelStlPath,
            int iteration, int variant,
            SimulationConfig config,
            float scaleFactor,
            string workingDirectory)
            => GenerateMesh(new MeshRequest(modelStlPath, iteration, variant, config, scaleFactor, workingDirectory));

        public string GenerateMesh(MeshRequest request)
        {
            _lastMetrics = new Dictionary<string, float>();
            float safeScale = GmshTunnelBuilder.SanitizeScaleFactor(request.ScaleFactor);
            (MeshBounds? modelBoundsPicoGk, float? realFrontalAreaMm2) = GmshTunnelBuilder.ReadModelMetricsForTunnel(request.GeometryPath, safeScale);
            string tunnelStlPath = EnsureTunnel(request.WorkingDirectory, modelBoundsPicoGk, safeScale, realFrontalAreaMm2);

            string suffix = request.BaseSuffix;
            string outputMeshPath = Path.Combine(request.WorkingDirectory, $"Model_{suffix}.su2");
            string geoScriptPath = Path.Combine(request.WorkingDirectory, $"Meshing_{suffix}.geo");

            string mode = (_options.BoundaryLayerMode ?? "Threshold").Trim();
            bool isHexa = mode.Equals("Hexa", StringComparison.OrdinalIgnoreCase);
            bool isPrism = mode.Equals("Prism", StringComparison.OrdinalIgnoreCase);

            WriteGeoScript(geoScriptPath, request.GeometryPath, tunnelStlPath, request.Config, safeScale, request.RefinementFactor);

            if (!isHexa && !isPrism)
            {
                GmshRunResult thresholdRun = GmshProcessRunner.Run(
                    request.Config,
                    $"\"{geoScriptPath}\" -3 -nt {request.Config.GmshCores} -format su2 -v 2 -o \"{outputMeshPath}\"",
                    request.WorkingDirectory,
                    geoScriptPath);
                if (thresholdRun.ExitCode != 0 || !File.Exists(outputMeshPath))
                {
                    GmshProcessRunner.CopyGeoToErrorLogs(geoScriptPath, request.WorkingDirectory, "3D-Threshold");
                    throw new MeshGenerationException($"Gmsh failed to create {Path.GetFileName(outputMeshPath)} (ExitCode {thresholdRun.ExitCode}{thresholdRun.ErrorSuffix}). .geo script copied to {OutputConventions.ErrorLogsDirectory}.");
                }

                MeshMetrics.PopulateSu2CellMetrics(_lastMetrics, outputMeshPath);
                Console.WriteLine($"       -> [OK] 3D volume mesh generated");
                return outputMeshPath;
            }

            return GenerateStructuredWallMesh(request, safeScale, tunnelStlPath, outputMeshPath, geoScriptPath, mode);
        }

        private string GenerateStructuredWallMesh(
            MeshRequest request, float safeScale, string tunnelStlPath, string outputMeshPath, string geoScriptPath, string mode)
        {
            string F(float v) => WakeAndDetailBoxBuilder.Format(v);
            string FormatMm(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

            // Step 1: Remesh body surface and save as ONE surface (with retry across ClassifyAngleDeg ladder).
            string wallScript = WallScriptPath(geoScriptPath);
            string wallMesh = WallMeshPath(geoScriptPath);
            IReadOnlyList<float> angleLadder = BuildClassifyAngleLadder(_options.ClassifyAngleDeg);
            float usedAngle = angleLadder[0];
            bool wallSucceeded = false;
            GmshRunResult? lastWallRun = null;

            for (int idx = 0; idx < angleLadder.Count; idx++)
            {
                float angleDeg = angleLadder[idx];
                if (idx > 0 || MathF.Abs(angleDeg - _options.ClassifyAngleDeg) > 1e-4f)
                {
                    WriteGeoScript(geoScriptPath, request.GeometryPath, tunnelStlPath, request.Config, safeScale, request.RefinementFactor, null, angleDeg);
                }

                if (File.Exists(wallMesh)) File.Delete(wallMesh);
                GmshRunResult wallRun = GmshProcessRunner.Run(
                    request.Config,
                    $"\"{wallScript}\" -2 -nt {request.Config.GmshCores} -format mesh -v 2 -o \"{wallMesh}\"",
                    request.WorkingDirectory,
                    wallScript);

                if (wallRun.ExitCode == 0 && File.Exists(wallMesh))
                {
                    usedAngle = angleDeg;
                    wallSucceeded = true;
                    if (idx > 0)
                    {
                        Console.WriteLine($"       -> {OutputConventions.WarningTag} Step-1 wall surface generated with fallback ClassifyAngleDeg = {F(usedAngle)}° (instead of {F(_options.ClassifyAngleDeg)}°).");
                    }
                    break;
                }

                lastWallRun = wallRun;
                if (idx + 1 < angleLadder.Count)
                {
                    Console.WriteLine($"       -> {OutputConventions.WarningTag} Wall surface failed with ClassifyAngleDeg = {F(angleDeg)}° (ExitCode {wallRun.ExitCode}{wallRun.ErrorSuffix}) — next attempt: {F(angleLadder[idx + 1])}°.");
                }
            }

            if (!wallSucceeded)
            {
                GmshProcessRunner.CopyGeoToErrorLogs(wallScript, request.WorkingDirectory, "3D-Wall");
                throw new MeshGenerationException($"Gmsh failed to create wall surface {Path.GetFileName(wallMesh)} (ExitCode {lastWallRun!.ExitCode}{lastWallRun.ErrorSuffix}). .geo script copied to {OutputConventions.ErrorLogsDirectory}.");
            }

            _lastMetrics["BoundaryLayerClassifyAngleDeg"] = usedAngle;

            // Step 2: Extrude boundary layer, fill core with tetrahedra.
            BoundaryLayerSpec spec = Gmsh3DGeoScriptBuilder.ComputeBoundaryLayerSpec(_options, _flow, request.Config, request.GeometryPath, safeScale);
            int layers = spec.LayerCount;
            float thicknessMm = Gmsh3DGeoScriptBuilder.LayerThicknessMm(spec, layers);
            double thicknessGeo = thicknessMm * (double)safeScale;

            WallSurface wall;
            try
            {
                wall = WallSurface.ReadMedit(wallMesh);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is FormatException)
            {
                GmshProcessRunner.CopyGeoToErrorLogs(wallScript, request.WorkingDirectory, "3D-Wall-Medit");
                throw new MeshGenerationException($"Wall surface {Path.GetFileName(wallMesh)} is unreadable: {ex.Message}", ex);
            }

            string fieldPath = WallFieldPath(geoScriptPath);
            WallFieldResult? field = null;
            string lastError = string.Empty;
            var ladder = BoundaryLayerWallField.Ladder;
            for (int attempt = 0; attempt < ladder.Count; attempt++)
            {
                WallFieldResult candidate = BoundaryLayerWallField.Build(wall, thicknessGeo, ladder[attempt]);
                BoundaryLayerWallField.WritePos(fieldPath, wall, candidate);
                WriteGeoScript(geoScriptPath, request.GeometryPath, tunnelStlPath, request.Config, safeScale, request.RefinementFactor, fieldPath, usedAngle);

                if (File.Exists(outputMeshPath)) File.Delete(outputMeshPath);
                GmshRunResult run = GmshProcessRunner.Run(
                    request.Config,
                    $"\"{geoScriptPath}\" -3 -nt {request.Config.GmshCores} -format su2 -v 2 -o \"{outputMeshPath}\"",
                    request.WorkingDirectory,
                    geoScriptPath);
                if (run.ExitCode == 0 && File.Exists(outputMeshPath))
                {
                    field = candidate;
                    break;
                }

                lastError = run.ErrorSuffix;
                if (attempt + 1 < ladder.Count)
                {
                    Console.WriteLine($"       -> {OutputConventions.WarningTag} Boundary layer ({ladder[attempt].Label}) failed to mesh{run.ErrorSuffix} — next attempt: {ladder[attempt + 1].Label}.");
                }
            }

            if (field == null)
            {
                GmshProcessRunner.CopyGeoToErrorLogs(wallScript, request.WorkingDirectory, "3D-Wall");
                GmshProcessRunner.CopyGeoToErrorLogs(geoScriptPath, request.WorkingDirectory, "3D-Volume");
                GmshProcessRunner.CopyGeoToErrorLogs(fieldPath, request.WorkingDirectory, "3D-WallField");
                throw new MeshGenerationException(
                    $"Gmsh failed to create {Path.GetFileName(outputMeshPath)} even with fallback mesh{lastError}. Wall script, meshing script, and wall field copied to {OutputConventions.ErrorLogsDirectory}.");
            }

            try { File.Delete(fieldPath); } catch { /* cleanup is best-effort */ }

            Gmsh2DMesher.RemoveMarker(outputMeshPath, Gmsh2DMesher.FluidMarkerName);

            WallFieldSettings used = field.Settings;
            _lastMetrics["BoundaryLayerLayers"] = layers;
            _lastMetrics["BoundaryLayerThicknessMm"] = thicknessMm;
            _lastMetrics["BoundaryLayerFirstCellMm"] = spec.FirstLayerHeightMm;
            _lastMetrics["BoundaryLayerStage"] = used.Stage;
            _lastMetrics["BoundaryLayerMaxTiltDeg"] = (float)field.MaxTiltDeg;
            _lastMetrics["BoundaryLayerMinScale"] = (float)field.MinScale;
            _lastMetrics["BoundaryLayerThinnedShare"] = (float)field.ThinnedShare;
            _lastMetrics["MeshFallback"] = used.Stage >= 3 ? 1f : 0f;
            MeshMetrics.PopulateSu2CellMetrics(_lastMetrics, outputMeshPath);

            float hexShare = ReadSu2BoundaryLayerHexShare(outputMeshPath);
            string hexPart = float.IsFinite(hexShare) ? $", {hexShare * 100f:0}% hexa" : string.Empty;
            string stagePart = used.Stage switch
            {
                1 => $", directions smoothed (max {field.MaxTiltDeg:0}°)",
                2 => $", locally thinned on {field.ThinnedShare * 100:0.0}% of surface (min scale {field.MinScale:0.00})",
                _ => $", [FALLBACK MESH] thickness globally reduced (min scale {field.MinScale:0.00}), variant only partially comparable",
            };
            Console.WriteLine($"       -> Boundary layer: {mode}, y1 = {spec.FirstLayerHeightMm.ToString("0.0000", CultureInfo.InvariantCulture)} mm (y+ {spec.TargetYPlus.ToString("0.#", CultureInfo.InvariantCulture)} at Re {spec.ReynoldsNumber.ToString("0.0e0", CultureInfo.InvariantCulture)}), {layers} layers, thickness {FormatMm(thicknessMm)} mm{hexPart}{stagePart}");
            Console.WriteLine($"       -> [OK] 3D volume mesh generated");
            return outputMeshPath;
        }

        /// <summary>Wall field (step 2) for a meshing script: <c>Meshing_X.geo</c> -> <c>Wall_X.pos</c>.</summary>
        public static string WallFieldPath(string geoScriptPath) => Gmsh3DGeoScriptBuilder.WallFieldPath(geoScriptPath);

        /// <summary>Step-1 script (wall surface) for a meshing script: <c>Meshing_X.geo</c> -> <c>Wall_X.geo</c>.</summary>
        public static string WallScriptPath(string geoScriptPath) => Gmsh3DGeoScriptBuilder.WallScriptPath(geoScriptPath);

        /// <summary>Result of step 1: remeshed wall as a single surface (Medit format).</summary>
        public static string WallMeshPath(string geoScriptPath) => Gmsh3DGeoScriptBuilder.WallMeshPath(geoScriptPath);

        /// <summary>Strips ANSI color codes (<c>ESC[1m</c>, <c>ESC[31m</c>, ...).</summary>
        internal static string StripAnsi(string text) => GmshErrorParser.StripAnsi(text);

        /// <summary>Extracts up to two meaningful error lines from Gmsh output.</summary>
        public static string? ExtractGmshError(IEnumerable<string> lines) => GmshErrorParser.ExtractGmshError(lines);

        /// <summary>Writes the .geo script for Gmsh to disk.</summary>
        public void WriteGeoScript(string geoScriptPath, string modelStlPath, string tunnelStlPath, SimulationConfig config, float scaleFactor)
            => WriteGeoScript(geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, 1.0f);

        /// <summary>Writes the .geo script for Gmsh to disk with a configurable mesh refinement factor.</summary>
        public void WriteGeoScript(string geoScriptPath, string modelStlPath, string tunnelStlPath, SimulationConfig config, float scaleFactor, float refinementFactor)
            => WriteGeoScript(geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, refinementFactor, null);

        /// <summary>Writes the 3D <c>.geo</c> scripts (Threshold or two-step Hexa/Prism).</summary>
        public void WriteGeoScript(
            string geoScriptPath, string modelStlPath, string tunnelStlPath, SimulationConfig config,
            float scaleFactor, float refinementFactor, string? wallFieldPath, float? classifyAngleDeg = null)
            => Gmsh3DGeoScriptBuilder.WriteGeoScript(_options, _flow, geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, refinementFactor, wallFieldPath, classifyAngleDeg);

        /// <summary>Builds the angle ladder for step 1 (<c>ClassifySurfaces</c>).</summary>
        internal static IReadOnlyList<float> BuildClassifyAngleLadder(float configuredAngleDeg)
            => Gmsh3DGeoScriptBuilder.BuildClassifyAngleLadder(configuredAngleDeg);

        /// <summary>
        /// Reads the hexahedral share of boundary-layer elements (hexahedra + prisms)
        /// from an SU2 mesh file (.su2) via <see cref="MeshMetrics.ReadSu2CellBreakdown"/>.
        /// Returns <c>float.NaN</c> if no hexahedra/prisms are present or the file is missing.
        /// </summary>
        public static float ReadSu2BoundaryLayerHexShare(string su2MeshPath)
        {
            Su2CellBreakdown? breakdown = MeshMetrics.ReadSu2CellBreakdown(su2MeshPath);
            if (breakdown == null)
                return float.NaN;

            int boundaryCells = breakdown.Hexahedra + breakdown.Prisms;
            if (boundaryCells == 0)
                return float.NaN;

            return (float)breakdown.Hexahedra / boundaryCells;
        }
    }
}
