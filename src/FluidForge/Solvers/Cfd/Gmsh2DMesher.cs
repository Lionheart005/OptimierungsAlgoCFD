using System.Collections.Generic;
using System.IO;
using FluidForge.Core;
using FluidForge.Kernels.Planar2D;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// 2D-CFD-Mesher basierend auf Gmsh (TODO-45). Erzeugt ein 2D-Flächennetz in der xy-Ebene
    /// für zweidimensionale SU2-Simulationen.
    /// Liest Konturdaten über <see cref="Shape2D.LoadContour"/>, erzeugt ein kreisförmiges
    /// Fernfeld, eine strukturierte Quader-Grenzschicht (BoundaryLayer Field) und Hintergrundfelder
    /// für Nahfeld und Nachlauf (Wake Box).
    /// </summary>
    public class Gmsh2DMesher : IMeshGenerator, IMeshMetricsSource
    {
        private readonly Gmsh2DMesherOptions _options;
        private readonly FlowConditions? _flow;
        private Dictionary<string, float> _lastMetrics = new();

        /// <summary>Kennwerte des letzten erzeugten 2D-Netzes (Zellzahlen je Elementklasse).</summary>
        public IReadOnlyDictionary<string, float> LastMeshMetrics => _lastMetrics;

        /// <param name="options">Vorgabedaten aus solvers/gmsh.json; ohne Angabe gelten Standardwerte.</param>
        /// <param name="flow">Physikalische Strömungsbedingungen für die Grenzschichtberechnung.</param>
        public Gmsh2DMesher(Gmsh2DMesherOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new Gmsh2DMesherOptions();
            _flow = flow;
        }

        public Gmsh2DMesherOptions Options => _options;
        public FlowConditions? Flow => _flow;

        /// <summary>Kleinster Anteil der berechneten Wandschichtdicke, der noch versucht wird (1 → ½ → ¼ → ⅛ → 1/16).</summary>
        public const float MinThicknessScale = 0.0625f;

        /// <summary>Name der Fluid-Gruppe im .geo-Skript; in der .su2-Datei kein gültiger Rand.</summary>
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

            // Schlägt Gmsh fehl ("Edge not recovered": die Wandschicht kollidiert an dünnen Stellen
            // wie der Hinterkante mit sich selbst), wird mit halber, dann viertel Schichtdicke
            // wiederholt. y1 und damit y+ bleiben gleich.
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
                    GmshProcessRunner.CopyGeoToFehlerLogs(geoScriptPath, request.WorkingDirectory, "2D-Meshing");
                    throw new MeshGenerationException($"Gmsh konnte {Path.GetFileName(outputMeshPath)} nicht erstellen (ExitCode {run.ExitCode}, auch mit {thicknessScale * 100f:0} % Wandschichtdicke). .geo-Skript nach FehlerLogs kopiert.");
                }

                thicknessScale *= 0.5f;
                SimulationLog.Detail($"[WARNUNG] Wandschicht ließ sich nicht vernetzen (meist Kollision an der Hinterkante) – neuer Versuch mit {thicknessScale * 100f:0} % der Schichtdicke.");
            }

            RemoveMarker(outputMeshPath, FluidMarkerName);
            MeshMetrics.PopulateSu2CellMetrics(_lastMetrics, outputMeshPath);

            SimulationLog.Detail("[OK] 2D-Flächennetz generiert");
            return outputMeshPath;
        }

        /// <summary>
        /// Entfernt einen Marker-Block (MARKER_TAG + MARKER_ELEMS + Elementzeilen) aus einer
        /// .su2-Datei und zählt NMARK herunter.
        /// </summary>
        public static bool RemoveMarker(string su2Path, string markerTag)
            => Gmsh2DGeoScriptBuilder.RemoveMarker(su2Path, markerTag);

        /// <summary>
        /// Schreibt das .geo-Skript für Gmsh im 2D-Modus in die Datei.
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
