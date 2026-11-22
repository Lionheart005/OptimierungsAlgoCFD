using System.Collections.Generic;
using System.IO;
using FluidForge.Core;
using FluidForge.Kernels.Planar2D;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// 2D CFD mesher based on Gmsh. Generates a 2D surface mesh in the xy-plane
    /// for two-dimensional SU2 simulations.
    /// Reads contour data via <see cref="Shape2D.LoadContour"/>, creates a circular farfield,
    /// a structured quad boundary layer (BoundaryLayer Field), and background fields for nearfield and wake.
    /// </summary>
    public class Gmsh2DMesher : IMeshGenerator, IMeshMetricsSource
    {
        private readonly Gmsh2DMesherOptions _options;
        private readonly FlowConditions? _flow;
        private Dictionary<string, float> _lastMetrics = new();

        /// <summary>Metrics of the last generated 2D mesh (cell counts per element class).</summary>
        public IReadOnlyDictionary<string, float> LastMeshMetrics => _lastMetrics;

        /// <param name="options">Configuration from solvers/gmsh.json; defaults apply if omitted.</param>
        /// <param name="flow">Physical flow conditions for boundary-layer calculations.</param>
        public Gmsh2DMesher(Gmsh2DMesherOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new Gmsh2DMesherOptions();
            _flow = flow;
        }

        public Gmsh2DMesherOptions Options => _options;
        public FlowConditions? Flow => _flow;

        /// <summary>Smallest fraction of computed wall-layer thickness attempted (1 → 1/2 → 1/4 → 1/8 → 1/16).</summary>
        public const float MinThicknessScale = 0.0625f;

        /// <summary>Name of the fluid group in the .geo script; not a valid boundary marker in the .su2 file.</summary>
        public const string FluidMarkerName = Gmsh2DGeoScriptBuilder.FluidMarkerName;

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
            string suffix = request.BaseSuffix;
            string outputMeshPath = Path.Combine(request.WorkingDirectory, $"Model_{suffix}.su2");
            string geoScriptPath = Path.Combine(request.WorkingDirectory, $"Meshing_{suffix}.geo");

            // If Gmsh fails ("Edge not recovered": boundary layer collides with itself at thin regions
            // like the trailing edge), retry with half, then quarter layer thickness.
            // y1 and thus y+ remain unchanged.
            float thicknessScale = 1.0f;
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
                GmshRunResult run = GmshProcessRunner.Run(
                    request.Config,
                    $"\"{geoScriptPath}\" -2 -nt {request.Config.GmshCores} -format su2 -v 0 -o \"{outputMeshPath}\"",
                    request.WorkingDirectory,
                    geoScriptPath,
                    captureOutput: false);

                if (run.ExitCode == 0 && File.Exists(outputMeshPath)) break;

                if (thicknessScale <= MinThicknessScale)
                {
                    GmshProcessRunner.CopyGeoToErrorLogs(geoScriptPath, request.WorkingDirectory, "2D-Meshing");
                    throw new MeshGenerationException($"Gmsh failed to create {Path.GetFileName(outputMeshPath)} (ExitCode {run.ExitCode}, even at {thicknessScale * 100f:0}% boundary-layer thickness). .geo script copied to {OutputConventions.ErrorLogsDirectory}.");
                }

                thicknessScale *= 0.5f;
                SimulationLog.Detail($"{OutputConventions.WarningTag} Boundary layer failed to mesh (likely trailing-edge self-intersection) — retrying with {thicknessScale * 100f:0}% layer thickness.");
            }

            RemoveMarker(outputMeshPath, FluidMarkerName);
            MeshMetrics.PopulateSu2CellMetrics(_lastMetrics, outputMeshPath);

            SimulationLog.Detail("[OK] 2D surface mesh generated");
            return outputMeshPath;
        }

        /// <summary>
        /// Removes a marker block (MARKER_TAG + MARKER_ELEMS + element lines) from an
        /// .su2 file and decrements NMARK.
        /// </summary>
        public static bool RemoveMarker(string su2Path, string markerTag)
            => Gmsh2DGeoScriptBuilder.RemoveMarker(su2Path, markerTag);

        /// <summary>
        /// Writes the 2D Gmsh .geo script to disk.
        /// </summary>
        public void WriteGeoScript(
            string geoScriptPath,
            string contourJsonPath,
            SimulationConfig config,
            float scaleFactor,
            float refinementFactor = 1.0f,
            float thicknessScale = 1.0f)
            => Gmsh2DGeoScriptBuilder.WriteGeoScript(_options, _flow, geoScriptPath, contourJsonPath, config, scaleFactor, refinementFactor, thicknessScale);
    }
}
