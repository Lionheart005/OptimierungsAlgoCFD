using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// CFD-Mesher basierend auf Gmsh. Erzeugt ein Volumen-Mesh für CFD-Simulationen.
    /// Der Windkanal (Tunnel) wird hier erzeugt, da er zur Simulationsdomäne gehört.
    /// </summary>
    public class GmshCfdMesher : IMeshGenerator, IMeshMetricsSource, IMeshResolutionTarget
    {
        private readonly GmshMesherOptions _options;
        private readonly FlowConditions? _flow;
        private readonly GmshTunnelBuilder _tunnelBuilder;
        private Dictionary<string, float> _lastMetrics = new();

        /// <summary>
        /// Kennwerte des letzten Netzes: Schichtzahl, Dicke, erste Zellhöhe, Stufe der Rückfall-Leiter
        /// (1 = A, 2 = B, 3 = Ersatznetz), Kippung, kleinster Dickenfaktor, gedünnter Flächenanteil, Ersatznetz-Kennzeichen.
        /// Leer im Modus "Threshold" (keine Wandschicht).
        /// </summary>
        public IReadOnlyDictionary<string, float> LastMeshMetrics => _lastMetrics;

        /// <param name="options">Vorgabedaten aus solvers/gmsh.json des Projekts; ohne Angabe gelten die Standardwerte.</param>
        /// <param name="flow">Physikalische Strömungsbedingungen (TODO-38); bereitgestellt für Grenzschichtnetz-Berechnungen (TODO-40).</param>
        public GmshCfdMesher(GmshMesherOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new GmshMesherOptions();
            _flow = flow;
            _tunnelBuilder = new GmshTunnelBuilder(_options);
        }

        public GmshMesherOptions Options => _options;
        public FlowConditions? Flow => _flow;

        /// <summary>Ziel-Oberflächenzellgröße (mm) bei Netzfaktor 1.0 für <see cref="VoxelResolutionAdvisor"/>.</summary>
        public float TargetSurfaceCellSizeMm => _options.SurfaceCellSize > 0f ? _options.SurfaceCellSize : _options.BoundaryLayerSizeMin;

        /// <summary>Erzeugt den statischen Referenz-Windkanal (ohne Kernel-Abhängigkeit, TODO-11).</summary>
        public string EnsureTunnel(string workingDirectory) => _tunnelBuilder.EnsureTunnel(workingDirectory);

        /// <summary>
        /// Erzeugt den physikalischen Windkanal im PicoGK-Arbeitsraum (skaliert mit <paramref name="scaleFactor"/>),
        /// garantiert Mindestabstände und den maximalen Versperrungsgrad.
        /// </summary>
        public string EnsureTunnel(
            string workingDirectory,
            MeshBounds? modelBoundsPicoGk,
            float scaleFactor,
            float? realFrontalAreaMm2 = null)
            => _tunnelBuilder.EnsureTunnel(workingDirectory, modelBoundsPicoGk, scaleFactor, realFrontalAreaMm2, _lastMetrics);

        /// <summary>Berechnet die effektiven physikalischen Tunnelabmessungen in realen Millimetern.</summary>
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
                    GmshProcessRunner.CopyGeoToFehlerLogs(geoScriptPath, request.WorkingDirectory, "3D-Threshold");
                    throw new MeshGenerationException($"Gmsh konnte {Path.GetFileName(outputMeshPath)} nicht erstellen (ExitCode {thresholdRun.ExitCode}{thresholdRun.ErrorSuffix}). .geo-Skript nach FehlerLogs kopiert.");
                }

                MeshMetrics.PopulateSu2CellMetrics(_lastMetrics, outputMeshPath);
                Console.WriteLine($"       -> [OK] 3D-Volumennetz generiert ");
                return outputMeshPath;
            }

            return GenerateStructuredWallMesh(request, safeScale, tunnelStlPath, outputMeshPath, geoScriptPath, mode);
        }

        private string GenerateStructuredWallMesh(
            MeshRequest request, float safeScale, string tunnelStlPath, string outputMeshPath, string geoScriptPath, string mode)
        {
            string F(float v) => WakeAndDetailBoxBuilder.Format(v);
            string FormatMm(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);

            // Schritt 1: Körperoberfläche neu vernetzen und als EINE Fläche speichern (mit Retry über ClassifyAngleDeg-Leiter).
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
                        Console.WriteLine($"       -> [WARNUNG] Schritt-1-Wandoberfläche mit Fallback ClassifyAngleDeg = {F(usedAngle)}° erzeugt (statt {F(_options.ClassifyAngleDeg)}°).");
                    }
                    break;
                }

                lastWallRun = wallRun;
                if (idx + 1 < angleLadder.Count)
                {
                    Console.WriteLine($"       -> [WARNUNG] Wandoberfläche ließ sich mit ClassifyAngleDeg = {F(angleDeg)}° nicht erzeugen (ExitCode {wallRun.ExitCode}{wallRun.ErrorSuffix}) – nächster Versuch: {F(angleLadder[idx + 1])}°.");
                }
            }

            if (!wallSucceeded)
            {
                GmshProcessRunner.CopyGeoToFehlerLogs(wallScript, request.WorkingDirectory, "3D-Wand");
                throw new MeshGenerationException($"Gmsh konnte die Wandoberfläche {Path.GetFileName(wallMesh)} nicht erstellen (ExitCode {lastWallRun!.ExitCode}{lastWallRun.ErrorSuffix}). .geo-Skript nach FehlerLogs kopiert.");
            }

            _lastMetrics["BoundaryLayerClassifyAngleDeg"] = usedAngle;

            // Schritt 2: Wandschicht extrudieren, Kern mit Tetraedern füllen.
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
                GmshProcessRunner.CopyGeoToFehlerLogs(wallScript, request.WorkingDirectory, "3D-Wand-Medit");
                throw new MeshGenerationException($"Die Wandoberfläche {Path.GetFileName(wallMesh)} ist nicht lesbar: {ex.Message}", ex);
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
                    Console.WriteLine($"       -> [WARNUNG] Wandschicht ({ladder[attempt].Label}) ließ sich nicht vernetzen{run.ErrorSuffix} – nächster Versuch: {ladder[attempt + 1].Label}.");
                }
            }

            if (field == null)
            {
                GmshProcessRunner.CopyGeoToFehlerLogs(wallScript, request.WorkingDirectory, "3D-Wand");
                GmshProcessRunner.CopyGeoToFehlerLogs(geoScriptPath, request.WorkingDirectory, "3D-Volumen");
                GmshProcessRunner.CopyGeoToFehlerLogs(fieldPath, request.WorkingDirectory, "3D-Wandfeld");
                throw new MeshGenerationException(
                    $"Gmsh konnte {Path.GetFileName(outputMeshPath)} auch mit dem Ersatznetz nicht erstellen{lastError}. Wand-, Meshing-Skript und Wandfeld nach FehlerLogs kopiert.");
            }

            try { File.Delete(fieldPath); } catch { /* aufräumen ist nur Kosmetik */ }

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
            string hexPart = float.IsFinite(hexShare) ? $", {hexShare * 100f:0} % Quader" : string.Empty;
            string stagePart = used.Stage switch
            {
                1 => $", Richtungen geglättet (max. {field.MaxTiltDeg:0}°)",
                2 => $", örtlich gedünnt auf {field.ThinnedShare * 100:0.0} % der Fläche (kleinster Faktor {field.MinScale:0.00})",
                _ => $", [ERSATZNETZ] Dicke überall verringert (kleinster Faktor {field.MinScale:0.00}), Variante nur eingeschränkt vergleichbar",
            };
            Console.WriteLine($"       -> Grenzschicht: {mode}, y1 = {spec.FirstLayerHeightMm.ToString("0.0000", CultureInfo.InvariantCulture)} mm (y+ {spec.TargetYPlus.ToString("0.#", CultureInfo.InvariantCulture)} bei Re {spec.ReynoldsNumber.ToString("0.0e0", CultureInfo.InvariantCulture)}), {layers} Schichten, Dicke {FormatMm(thicknessMm)} mm{hexPart}{stagePart}");
            Console.WriteLine($"       -> [OK] 3D-Volumennetz generiert ");
            return outputMeshPath;
        }

        /// <summary>Wandfeld (Schritt 2) zu einem Meshing-Skript: <c>Meshing_X.geo</c> → <c>Wall_X.pos</c>.</summary>
        public static string WallFieldPath(string geoScriptPath) => Gmsh3DGeoScriptBuilder.WallFieldPath(geoScriptPath);

        /// <summary>Schritt-1-Skript (Wandoberfläche) zu einem Meshing-Skript: <c>Meshing_X.geo</c> → <c>Wall_X.geo</c>.</summary>
        public static string WallScriptPath(string geoScriptPath) => Gmsh3DGeoScriptBuilder.WallScriptPath(geoScriptPath);

        /// <summary>Ergebnis von Schritt 1: die neu vernetzte Wand als eine Fläche (Medit-Format).</summary>
        public static string WallMeshPath(string geoScriptPath) => Gmsh3DGeoScriptBuilder.WallMeshPath(geoScriptPath);

        /// <summary>Entfernt ANSI-Farbcodes (<c>ESC[1m</c>, <c>ESC[31m</c> …).</summary>
        internal static string StripAnsi(string text) => GmshErrorParser.StripAnsi(text);

        /// <summary>Extrahiert bis zu zwei aussagekräftige Fehlerzeilen aus der Gmsh-Ausgabe.</summary>
        public static string? ExtractGmshError(IEnumerable<string> lines) => GmshErrorParser.ExtractGmshError(lines);

        /// <summary>Schreibt das .geo-Skript für Gmsh auf die Festplatte (TODO-34, TODO-39).</summary>
        public void WriteGeoScript(string geoScriptPath, string modelStlPath, string tunnelStlPath, SimulationConfig config, float scaleFactor)
            => WriteGeoScript(geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, 1.0f);

        /// <summary>Schreibt das .geo-Skript für Gmsh auf die Festplatte mit konfigurierbarem Netzfaktor (TODO-39).</summary>
        public void WriteGeoScript(string geoScriptPath, string modelStlPath, string tunnelStlPath, SimulationConfig config, float scaleFactor, float refinementFactor)
            => WriteGeoScript(geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, refinementFactor, null);

        /// <summary>Schreibt die 3D-<c>.geo</c>-Skripte (Threshold oder zweistufiges Hexa/Prism).</summary>
        public void WriteGeoScript(
            string geoScriptPath, string modelStlPath, string tunnelStlPath, SimulationConfig config,
            float scaleFactor, float refinementFactor, string? wallFieldPath, float? classifyAngleDeg = null)
            => Gmsh3DGeoScriptBuilder.WriteGeoScript(_options, _flow, geoScriptPath, modelStlPath, tunnelStlPath, config, scaleFactor, refinementFactor, wallFieldPath, classifyAngleDeg);

        /// <summary>Bildet die Winkel-Leiter für Schritt 1 (<c>ClassifySurfaces</c>).</summary>
        internal static IReadOnlyList<float> BuildClassifyAngleLadder(float configuredAngleDeg)
            => Gmsh3DGeoScriptBuilder.BuildClassifyAngleLadder(configuredAngleDeg);

        /// <summary>
        /// Liest den Anteil der Hexaeder-Zellen an den Grenzschicht-Elementen (Hexaeder + Prismen)
        /// aus einer SU2-Netzdatei (.su2) über <see cref="MeshMetrics.ReadSu2CellBreakdown"/> (TODO-40).
        /// Gibt <c>float.NaN</c> zurück, wenn keine Hexaeder/Prismen vorhanden sind oder die Datei fehlt.
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
