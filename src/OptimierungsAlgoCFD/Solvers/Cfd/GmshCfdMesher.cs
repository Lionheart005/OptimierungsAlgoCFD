using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Effektive physikalische Windkanal-Abmessungen in realen Millimetern (AP-3),
    /// entkoppelt vom PicoGK-Skalierungsfaktor.
    /// </summary>
    public readonly record struct TunnelSpec(
        bool IsCylinder,
        float CenterX,
        float CenterY,
        float CenterZ,
        float SizeX,
        float SizeY,
        float SizeZ,
        float Diameter,
        float Length,
        int Segments,
        float ConfiguredLengthMm,
        float ConfiguredWidthMm,
        float ConfiguredHeightMm,
        float FrontalAreaMm2,
        float BlockagePercent,
        float UpstreamL,
        float DownstreamL,
        bool WasExpandedForClearance,
        bool WasExpandedForBlockage)
    {
        public bool WasExpanded => WasExpandedForClearance || WasExpandedForBlockage;
        public float EffectiveLengthMm => IsCylinder ? Length : SizeX;
        public float EffectiveWidthMm => IsCylinder ? Diameter : SizeY;
        public float EffectiveHeightMm => IsCylinder ? Diameter : SizeZ;
    }
    /// <summary>
    /// CFD-Mesher basierend auf Gmsh. Erzeugt ein Volumen-Mesh für CFD-Simulationen.
    /// Der Windkanal (Tunnel) wird hier erzeugt, da er zur Simulationsdomäne gehört.
    /// </summary>
    public class GmshCfdMesher : IMeshGenerator, IMeshMetricsSource
    {
        private readonly GmshMesherOptions _options;
        private readonly FlowConditions? _flow;
        private string? _cachedTunnelPath;
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
        /// Erzeugt den physikalischen Windkanal im PicoGK-Arbeitsraum (skaliert mit <paramref name="scaleFactor"/>),
        /// garantiert Mindestabstände (<see cref="GmshMesherOptions.TunnelMinUpstreamFactor"/> /
        /// <see cref="GmshMesherOptions.TunnelMinDownstreamFactor"/> / <see cref="GmshMesherOptions.TunnelMinLateralFactor"/>)
        /// sowie den maximalen Versperrungsgrad (<see cref="GmshMesherOptions.TunnelMaxBlockagePercent"/>)
        /// und entkoppelt die reale Domain vollständig vom RubberBandScaler.
        /// </summary>
        public string EnsureTunnel(
            string workingDirectory,
            MeshBounds? modelBoundsPicoGk,
            float scaleFactor,
            float? realFrontalAreaMm2 = null)
        {
            float safeScale = SanitizeScaleFactor(scaleFactor);
            TunnelSpec spec = ResolveEffectiveTunnel(modelBoundsPicoGk, safeScale, realFrontalAreaMm2);

            bool isDefaultPlainFile = !spec.WasExpanded && MathF.Abs(safeScale - 1.0f) < 1e-6f;
            string tunnelPath;
            string header;
            if (isDefaultPlainFile)
            {
                tunnelPath = Path.Combine(workingDirectory, "Static_Windtunnel.stl");
                header = StlWriter.DefaultHeader;
            }
            else
            {
                string hash = ComputeTunnelHash(spec, safeScale);
                tunnelPath = Path.Combine(workingDirectory, $"Static_Windtunnel_{hash}.stl");
                header = $"Automatisierung STL mm {hash}";
            }

            int expectedTriCount = spec.IsCylinder ? 4 * spec.Segments : 12;
            long expectedFileLength = 84L + 50L * expectedTriCount;

            if (!IsTunnelCacheValid(tunnelPath, expectedFileLength, header, spec, safeScale, checkBounds: isDefaultPlainFile))
            {
                IReadOnlyList<StlTriangle> tunnel = BuildTunnel(spec, safeScale);
                StlWriter.WriteBinary(tunnelPath, tunnel, header);
            }

            _lastMetrics["TunnelBlockagePercent"] = spec.BlockagePercent;
            _lastMetrics["TunnelLengthMm"] = spec.EffectiveLengthMm;
            _lastMetrics["TunnelWidthMm"] = spec.EffectiveWidthMm;
            _lastMetrics["TunnelHeightMm"] = spec.EffectiveHeightMm;
            _lastMetrics["TunnelUpstreamL"] = spec.UpstreamL;
            _lastMetrics["TunnelDownstreamL"] = spec.DownstreamL;

            LogTunnelSummary(spec, safeScale);

            return tunnelPath;
        }

        /// <summary>
        /// Berechnet die effektiven physikalischen Tunnelabmessungen in realen Millimetern.
        /// Der konfigurierte Windkanal wird bei Bedarf nach außen aufgeweitet, niemals verkleinert.
        /// </summary>
        public TunnelSpec ResolveEffectiveTunnel(
            MeshBounds? modelBoundsPicoGk,
            float scaleFactor,
            float? realFrontalAreaMm2 = null)
        {
            float safeScale = SanitizeScaleFactor(scaleFactor);
            string shape = (_options.TunnelShape ?? string.Empty).Replace(" ", string.Empty).Trim();
            bool isCylinder = shape.Equals("Cylinder", StringComparison.OrdinalIgnoreCase);

            if (!isCylinder && shape.Length > 0 && !shape.Equals("Box", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"[WARNUNG] Unbekannte Windkanal-Form '{_options.TunnelShape}' — nutze 'Box'.");
            }

            float cfgCenterX = _options.TunnelCenterX;
            float cfgCenterY = _options.TunnelCenterY;
            float cfgCenterZ = _options.TunnelCenterZ;

            float cfgLenX = isCylinder ? _options.TunnelLength : _options.TunnelSizeX;
            float cfgWidthY = isCylinder ? _options.TunnelDiameter : _options.TunnelSizeY;
            float cfgHeightZ = isCylinder ? _options.TunnelDiameter : _options.TunnelSizeZ;
            float cfgRadius = _options.TunnelDiameter * 0.5f;

            float cfgMinX = cfgCenterX - cfgLenX * 0.5f;
            float cfgMaxX = cfgCenterX + cfgLenX * 0.5f;
            float cfgMinY = cfgCenterY - cfgWidthY * 0.5f;
            float cfgMaxY = cfgCenterY + cfgWidthY * 0.5f;
            float cfgMinZ = cfgCenterZ - cfgHeightZ * 0.5f;
            float cfgMaxZ = cfgCenterZ + cfgHeightZ * 0.5f;

            float effMinX = cfgMinX;
            float effMaxX = cfgMaxX;
            float effMinY = cfgMinY;
            float effMaxY = cfgMaxY;
            float effMinZ = cfgMinZ;
            float effMaxZ = cfgMaxZ;
            float effRadius = cfgRadius;

            float frontalAreaMm2 = (realFrontalAreaMm2.HasValue && realFrontalAreaMm2.Value > 0f && float.IsFinite(realFrontalAreaMm2.Value))
                ? realFrontalAreaMm2.Value
                : 0f;

            bool wasExpandedForClearance = false;
            bool wasExpandedForBlockage = false;
            float upstreamL = 0f;
            float downstreamL = 0f;

            if (modelBoundsPicoGk is { IsEmpty: false } bounds)
            {
                Vector3 realMin = bounds.Min / safeScale;
                Vector3 realMax = bounds.Max / safeScale;
                Vector3 realSize = realMax - realMin;
                float modelL = Math.Max(0f, realSize.X);
                float modelB = Math.Max(0f, realSize.Y);
                float modelH = Math.Max(0f, realSize.Z);

                if (frontalAreaMm2 <= 0f && modelB > 0f && modelH > 0f)
                {
                    frontalAreaMm2 = modelB * modelH;
                }

                float upFactor = Math.Max(0f, _options.TunnelMinUpstreamFactor);
                float downFactor = Math.Max(0f, _options.TunnelMinDownstreamFactor);
                float latFactor = Math.Max(0f, _options.TunnelMinLateralFactor);

                float reqMinX = realMin.X - upFactor * modelL;
                float reqMaxX = realMax.X + downFactor * modelL;
                float reqMinY = realMin.Y - latFactor * modelB;
                float reqMaxY = realMax.Y + latFactor * modelB;
                float reqMinZ = realMin.Z - latFactor * modelH;
                float reqMaxZ = realMax.Z + latFactor * modelH;

                effMinX = Math.Min(cfgMinX, reqMinX);
                effMaxX = Math.Max(cfgMaxX, reqMaxX);

                if (isCylinder)
                {
                    float dy = Math.Max(Math.Abs(reqMinY - cfgCenterY), Math.Abs(reqMaxY - cfgCenterY));
                    float dz = Math.Max(Math.Abs(reqMinZ - cfgCenterZ), Math.Abs(reqMaxZ - cfgCenterZ));
                    float reqCornerRadius = MathF.Sqrt(dy * dy + dz * dz);
                    effRadius = Math.Max(cfgRadius, reqCornerRadius);

                    wasExpandedForClearance =
                        effMinX < cfgMinX - 1e-4f ||
                        effMaxX > cfgMaxX + 1e-4f ||
                        effRadius > cfgRadius + 1e-4f;

                    if (_options.TunnelMaxBlockagePercent > 0f && frontalAreaMm2 > 0f && effRadius > 0f)
                    {
                        double areaAfterClearance = Math.PI * effRadius * effRadius;
                        double currentBlockagePct = (frontalAreaMm2 / areaAfterClearance) * 100.0;
                        if (currentBlockagePct > _options.TunnelMaxBlockagePercent)
                        {
                            double reqArea = (double)frontalAreaMm2 / (_options.TunnelMaxBlockagePercent / 100.0);
                            float reqBlockageRadius = (float)Math.Sqrt((reqArea * (1.0 + 1e-5)) / Math.PI);
                            if (reqBlockageRadius > effRadius)
                            {
                                effRadius = reqBlockageRadius;
                                wasExpandedForBlockage = true;
                            }
                        }
                    }

                    effMinY = cfgCenterY - effRadius;
                    effMaxY = cfgCenterY + effRadius;
                    effMinZ = cfgCenterZ - effRadius;
                    effMaxZ = cfgCenterZ + effRadius;
                }
                else
                {
                    effMinY = Math.Min(cfgMinY, reqMinY);
                    effMaxY = Math.Max(cfgMaxY, reqMaxY);
                    effMinZ = Math.Min(cfgMinZ, reqMinZ);
                    effMaxZ = Math.Max(cfgMaxZ, reqMaxZ);

                    wasExpandedForClearance =
                        effMinX < cfgMinX - 1e-4f ||
                        effMaxX > cfgMaxX + 1e-4f ||
                        effMinY < cfgMinY - 1e-4f ||
                        effMaxY > cfgMaxY + 1e-4f ||
                        effMinZ < cfgMinZ - 1e-4f ||
                        effMaxZ > cfgMaxZ + 1e-4f;

                    float sizeYAfterClearance = effMaxY - effMinY;
                    float sizeZAfterClearance = effMaxZ - effMinZ;
                    double areaAfterClearance = (double)sizeYAfterClearance * sizeZAfterClearance;

                    if (_options.TunnelMaxBlockagePercent > 0f && frontalAreaMm2 > 0f && areaAfterClearance > 0.0)
                    {
                        double currentBlockagePct = (frontalAreaMm2 / areaAfterClearance) * 100.0;
                        if (currentBlockagePct > _options.TunnelMaxBlockagePercent)
                        {
                            double reqArea = (double)frontalAreaMm2 / (_options.TunnelMaxBlockagePercent / 100.0);
                            double scaleYZ = Math.Sqrt((reqArea * (1.0 + 1e-5)) / areaAfterClearance);
                            float cy = 0.5f * (effMinY + effMaxY);
                            float cz = 0.5f * (effMinZ + effMaxZ);
                            float halfY = 0.5f * (float)(sizeYAfterClearance * scaleYZ);
                            float halfZ = 0.5f * (float)(sizeZAfterClearance * scaleYZ);
                            effMinY = cy - halfY;
                            effMaxY = cy + halfY;
                            effMinZ = cz - halfZ;
                            effMaxZ = cz + halfZ;
                            wasExpandedForBlockage = true;
                        }
                    }
                }

                if (modelL > 0f)
                {
                    upstreamL = (realMin.X - effMinX) / modelL;
                    downstreamL = (effMaxX - realMax.X) / modelL;
                }
            }

            float effCenterX = 0.5f * (effMinX + effMaxX);
            float effCenterY = isCylinder ? cfgCenterY : 0.5f * (effMinY + effMaxY);
            float effCenterZ = isCylinder ? cfgCenterZ : 0.5f * (effMinZ + effMaxZ);
            float effSizeX = effMaxX - effMinX;
            float effSizeY = isCylinder ? effRadius * 2.0f : effMaxY - effMinY;
            float effSizeZ = isCylinder ? effRadius * 2.0f : effMaxZ - effMinZ;
            float effDiameter = effRadius * 2.0f;
            float effLength = effSizeX;

            double effCrossArea = isCylinder
                ? Math.PI * effRadius * effRadius
                : (double)effSizeY * effSizeZ;
            float blockagePercent = effCrossArea > 0.0 && frontalAreaMm2 > 0f
                ? (float)((frontalAreaMm2 / effCrossArea) * 100.0)
                : 0f;

            return new TunnelSpec(
                IsCylinder: isCylinder,
                CenterX: effCenterX,
                CenterY: effCenterY,
                CenterZ: effCenterZ,
                SizeX: effSizeX,
                SizeY: effSizeY,
                SizeZ: effSizeZ,
                Diameter: effDiameter,
                Length: effLength,
                Segments: _options.TunnelSegments,
                ConfiguredLengthMm: cfgLenX,
                ConfiguredWidthMm: cfgWidthY,
                ConfiguredHeightMm: cfgHeightZ,
                FrontalAreaMm2: frontalAreaMm2,
                BlockagePercent: blockagePercent,
                UpstreamL: upstreamL,
                DownstreamL: downstreamL,
                WasExpandedForClearance: wasExpandedForClearance,
                WasExpandedForBlockage: wasExpandedForBlockage);
        }

        private static IReadOnlyList<StlTriangle> BuildTunnel(TunnelSpec spec, float safeScale)
        {
            Vector3 centerPico = new Vector3(spec.CenterX, spec.CenterY, spec.CenterZ) * safeScale;
            if (spec.IsCylinder)
            {
                return TunnelGeometry.CreateCylinder(
                    spec.Diameter * safeScale,
                    spec.Length * safeScale,
                    centerPico,
                    spec.Segments);
            }

            return TunnelGeometry.CreateBox(
                new Vector3(spec.SizeX, spec.SizeY, spec.SizeZ) * safeScale,
                centerPico);
        }

        private static string ComputeTunnelHash(TunnelSpec spec, float safeScale)
        {
            string canonical = string.Create(CultureInfo.InvariantCulture,
                $"{(spec.IsCylinder ? "C" : "B")}|{spec.CenterX:G9}|{spec.CenterY:G9}|{spec.CenterZ:G9}|{spec.SizeX:G9}|{spec.SizeY:G9}|{spec.SizeZ:G9}|{spec.Diameter:G9}|{spec.Length:G9}|{spec.Segments}|{safeScale:G9}");
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        private static bool IsTunnelCacheValid(
            string tunnelPath,
            long expectedFileLength,
            string expectedHeader,
            TunnelSpec spec,
            float safeScale,
            bool checkBounds)
        {
            try
            {
                if (!File.Exists(tunnelPath))
                    return false;

                var info = new FileInfo(tunnelPath);
                if (info.Length != expectedFileLength)
                    return false;

                byte[] expectedHeaderBytes = new byte[80];
                for (int i = 0; i < 80; i++) expectedHeaderBytes[i] = 0x20;
                byte[] text = Encoding.ASCII.GetBytes(expectedHeader);
                Array.Copy(text, expectedHeaderBytes, Math.Min(text.Length, 80));

                byte[] actualHeader = new byte[80];
                using (FileStream fs = File.OpenRead(tunnelPath))
                {
                    if (fs.Read(actualHeader, 0, 80) != 80)
                        return false;
                }

                if (!actualHeader.AsSpan().SequenceEqual(expectedHeaderBytes))
                    return false;

                if (checkBounds)
                {
                    MeshBounds bounds = StlWriter.ReadBounds(tunnelPath);
                    if (bounds.IsEmpty)
                        return false;

                    float halfX = (spec.IsCylinder ? spec.Length : spec.SizeX) * 0.5f * safeScale;
                    float halfY = (spec.IsCylinder ? spec.Diameter : spec.SizeY) * 0.5f * safeScale;
                    float halfZ = (spec.IsCylinder ? spec.Diameter : spec.SizeZ) * 0.5f * safeScale;
                    Vector3 expMin = new Vector3(spec.CenterX * safeScale - halfX, spec.CenterY * safeScale - halfY, spec.CenterZ * safeScale - halfZ);
                    Vector3 expMax = new Vector3(spec.CenterX * safeScale + halfX, spec.CenterY * safeScale + halfY, spec.CenterZ * safeScale + halfZ);
                    float tol = spec.IsCylinder ? MathF.Max(1e-2f, halfY * 0.02f) : 1e-3f;

                    if (MathF.Abs(bounds.Min.X - expMin.X) > tol || MathF.Abs(bounds.Max.X - expMax.X) > tol ||
                        MathF.Abs(bounds.Min.Y - expMin.Y) > tol || MathF.Abs(bounds.Max.Y - expMax.Y) > tol ||
                        MathF.Abs(bounds.Min.Z - expMin.Z) > tol || MathF.Abs(bounds.Max.Z - expMax.Z) > tol)
                    {
                        return false;
                    }
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void LogTunnelSummary(TunnelSpec spec, float safeScale)
        {
            string shapeLabel = spec.IsCylinder ? "Zylinder" : "Box";
            string cfgDims = spec.IsCylinder
                ? $"D={FormatMm(spec.ConfiguredWidthMm)} x L={FormatMm(spec.ConfiguredLengthMm)} mm"
                : $"{FormatMm(spec.ConfiguredLengthMm)} x {FormatMm(spec.ConfiguredWidthMm)} x {FormatMm(spec.ConfiguredHeightMm)} mm";
            string effDims = spec.IsCylinder
                ? $"D={FormatMm(spec.EffectiveWidthMm)} x L={FormatMm(spec.EffectiveLengthMm)} mm"
                : $"{FormatMm(spec.EffectiveLengthMm)} x {FormatMm(spec.EffectiveWidthMm)} x {FormatMm(spec.EffectiveHeightMm)} mm";

            string expandNote = string.Empty;
            if (spec.WasExpanded)
            {
                string reasons = spec.WasExpandedForClearance && spec.WasExpandedForBlockage
                    ? "Mindestabstand + Blockage"
                    : (spec.WasExpandedForClearance ? "Mindestabstand" : "Blockage");
                expandNote = $" [AUTO-ERWEITERT: {reasons}]";
            }

            SimulationLog.Detail(
                $"Windkanal ({shapeLabel}, scale={Format(safeScale)}): Konfig {cfgDims} -> Effektiv {effDims}{expandNote} | " +
                $"Abstand {spec.UpstreamL.ToString("0.0", CultureInfo.InvariantCulture)}L vor / {spec.DownstreamL.ToString("0.0", CultureInfo.InvariantCulture)}L nach | " +
                $"A_frontal={spec.FrontalAreaMm2.ToString("0.0", CultureInfo.InvariantCulture)} mm², Blockage={spec.BlockagePercent.ToString("0.000", CultureInfo.InvariantCulture)} %");
        }

        private static float SanitizeScaleFactor(float scaleFactor)
            => scaleFactor > 0f && float.IsFinite(scaleFactor) ? scaleFactor : 1.0f;

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
            _lastMetrics = new Dictionary<string, float>();
            float safeScale = SanitizeScaleFactor(request.ScaleFactor);
            (MeshBounds? modelBoundsPicoGk, float? realFrontalAreaMm2) = ReadModelMetricsForTunnel(request.GeometryPath, safeScale);
            string tunnelStlPath = EnsureTunnel(request.WorkingDirectory, modelBoundsPicoGk, safeScale, realFrontalAreaMm2);

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
                safeScale,
                request.RefinementFactor);

            if (!isHexa && !isPrism)
            {
                GmshRun thresholdRun = RunGmsh(request.Config, $"\"{geoScriptPath}\" -3 -nt {request.Config.GmshCores} -format su2 -v 2 -o \"{outputMeshPath}\"",
                    request.WorkingDirectory, geoScriptPath);
                if (thresholdRun.ExitCode != 0 || !File.Exists(outputMeshPath))
                {
                    CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                    throw new MeshGenerationException($"Gmsh konnte {Path.GetFileName(outputMeshPath)} nicht erstellen (ExitCode {thresholdRun.ExitCode}{thresholdRun.ErrorSuffix}). .geo-Skript nach FehlerLogs kopiert.");
                }

                Console.WriteLine($"       -> [OK] 3D-Volumennetz generiert ");
                return outputMeshPath;
            }

            // --- Strukturierte Wandschicht in zwei Gmsh-Schritten (Servertest 06.11.2026) ---
            // Schritt 1: Körperoberfläche neu vernetzen und als EINE Fläche speichern.
            string wallScript = WallScriptPath(geoScriptPath);
            string wallMesh = WallMeshPath(geoScriptPath);
            if (File.Exists(wallMesh)) File.Delete(wallMesh);
            GmshRun wallRun = RunGmsh(request.Config, $"\"{wallScript}\" -2 -nt {request.Config.GmshCores} -format mesh -v 2 -o \"{wallMesh}\"",
                request.WorkingDirectory, wallScript);
            if (wallRun.ExitCode != 0 || !File.Exists(wallMesh))
            {
                CopyGeoToFehlerLogs(request.WorkingDirectory, wallScript);
                throw new MeshGenerationException($"Gmsh konnte die Wandoberfläche {Path.GetFileName(wallMesh)} nicht erstellen (ExitCode {wallRun.ExitCode}{wallRun.ErrorSuffix}). .geo-Skript nach FehlerLogs kopiert.");
            }

            // Schritt 2: Wandschicht extrudieren, Kern mit Tetraedern füllen.
            //
            // Schichtzahl, Wachstumsrate und erste Zellhöhe stehen fest (Regel aus Re und Grenzschichtdicke) und ändern sich
            // NIE: Varianten und Kalibrierstufen sollen mit demselben Wandnetz rechnen, sonst sind sie nicht vergleichbar
            // (Bericht-Lauf 09.11.2026: der frühere Rückfall 21 → 16 Schichten verfälschte die Netzstudie). Scheitert Gmsh,
            // ändert sich nur das Feld aus Extrusionsrichtung und Dicke (BoundaryLayerWallField.Ladder):
            // A geglättete Richtungen, B örtlich gedünnte Kehlen und Engstellen, C Ersatznetz mit global dünnerer Schicht.
            BoundaryLayerSpec spec = ComputeBoundaryLayerSpec(request.Config, request.GeometryPath, safeScale);
            int layers = spec.LayerCount;
            float thicknessMm = LayerThicknessMm(spec, layers);
            double thicknessGeo = thicknessMm * (double)safeScale;

            WallSurface wall;
            try
            {
                wall = WallSurface.ReadMedit(wallMesh);
            }
            catch (Exception ex) when (ex is InvalidDataException || ex is IOException || ex is FormatException)
            {
                CopyGeoToFehlerLogs(request.WorkingDirectory, wallScript);
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
                WriteGeoScript(geoScriptPath, request.GeometryPath, tunnelStlPath, request.Config,
                    safeScale, request.RefinementFactor, fieldPath);

                if (File.Exists(outputMeshPath)) File.Delete(outputMeshPath);
                GmshRun run = RunGmsh(request.Config, $"\"{geoScriptPath}\" -3 -nt {request.Config.GmshCores} -format su2 -v 2 -o \"{outputMeshPath}\"",
                    request.WorkingDirectory, geoScriptPath);
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
                CopyGeoToFehlerLogs(request.WorkingDirectory, wallScript);
                CopyGeoToFehlerLogs(request.WorkingDirectory, geoScriptPath);
                CopyGeoToFehlerLogs(request.WorkingDirectory, fieldPath);
                throw new MeshGenerationException(
                    $"Gmsh konnte {Path.GetFileName(outputMeshPath)} auch mit dem Ersatznetz nicht erstellen{lastError}. Wand-, Meshing-Skript und Wandfeld nach FehlerLogs kopiert.");
            }

            try { File.Delete(fieldPath); } catch { /* aufräumen ist nur Kosmetik */ }

            // Gmsh schreibt die Physical Volume "Fluid" als Rand-Marker mit (wie in 2D), SU2 kennt ihn nicht.
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
        public static string WallFieldPath(string geoScriptPath) => Path.ChangeExtension(WallScriptPath(geoScriptPath), ".pos");

        /// <summary>Schritt-1-Skript (Wandoberfläche) zu einem Meshing-Skript: <c>Meshing_X.geo</c> → <c>Wall_X.geo</c>.</summary>
        public static string WallScriptPath(string geoScriptPath)
        {
            string name = Path.GetFileNameWithoutExtension(geoScriptPath);
            if (name.StartsWith("Meshing_", StringComparison.Ordinal)) name = name.Substring("Meshing_".Length);
            return Path.Combine(Path.GetDirectoryName(geoScriptPath) ?? string.Empty, $"Wall_{name}.geo");
        }

        /// <summary>Ergebnis von Schritt 1: die neu vernetzte Wand als eine Fläche (Medit-Format).</summary>
        public static string WallMeshPath(string geoScriptPath) => Path.ChangeExtension(WallScriptPath(geoScriptPath), ".mesh");

        private static float LayerThicknessMm(BoundaryLayerSpec spec, int layers)
        {
            double r = spec.GrowthRate;
            return (float)(spec.FirstLayerHeightMm * (Math.Pow(r, layers) - 1.0) / (r - 1.0));
        }

        private static string FormatMm(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);

        private static (MeshBounds? BoundsPicoGk, float? RealFrontalAreaMm2) ReadModelMetricsForTunnel(string modelStlPath, float safeScale)
        {
            if (string.IsNullOrWhiteSpace(modelStlPath) || !File.Exists(modelStlPath))
                return (null, null);

            try
            {
                IReadOnlyList<StlTriangle> triangles = StlWriter.ReadTriangles(modelStlPath);
                if (triangles.Count > 0)
                {
                    MeshMeasurement measurement = MeshMetrics.Measure(triangles);
                    MeshBounds? bounds = measurement.Bounds.IsEmpty ? null : measurement.Bounds;
                    float? realFrontalArea = measurement.FrontalProjectedArea > 0f && float.IsFinite(measurement.FrontalProjectedArea)
                        ? measurement.FrontalProjectedArea / (safeScale * safeScale)
                        : null;
                    return (bounds, realFrontalArea);
                }

                MeshBounds fallbackBounds = StlWriter.ReadBounds(modelStlPath);
                return (fallbackBounds.IsEmpty ? null : fallbackBounds, null);
            }
            catch
            {
                try
                {
                    MeshBounds fallbackBounds = StlWriter.ReadBounds(modelStlPath);
                    return (fallbackBounds.IsEmpty ? null : fallbackBounds, null);
                }
                catch
                {
                    return (null, null);
                }
            }
        }

        private BoundaryLayerSpec ComputeBoundaryLayerSpec(SimulationConfig config, string modelStlPath, float scaleFactor)
        {
            float safeScale = SanitizeScaleFactor(scaleFactor);
            FlowConditions flow = _flow ?? FlowConditions.From(config, new Su2SolverOptions());
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
                flow.Density, flow.Velocity, flow.DynamicViscosity, lengthMm,
                _options.TargetYPlus, _options.BoundaryLayerGrowthRate,
                _options.BoundaryLayerMinLayers, _options.BoundaryLayerLayers, _options.BoundaryLayerCoverage);
        }

        /// <summary>Ergebnis eines Gmsh-Aufrufs: Exit-Code und die Fehlerzeile(n) aus der Ausgabe (leer, wenn keine).</summary>
        private sealed record GmshRun(int ExitCode, string? FirstError)
        {
            /// <summary>Für Meldungen: " (Gmsh: …)" oder leer.</summary>
            public string ErrorSuffix => string.IsNullOrEmpty(FirstError) ? string.Empty : $" (Gmsh: {FirstError})";
        }

        /// <summary>
        /// Startet Gmsh und liest die Ausgabe mit (Aufruf mit <c>-v 2</c>), damit der Grund eines Abbruchs im Log steht
        /// und das Programm nicht bei jedem Fehler blind weiter verdünnt.
        /// </summary>
        private static GmshRun RunGmsh(SimulationConfig config, string arguments, string workingDirectory, string scriptForErrorLog)
        {
            using Process mesher = new Process();
            mesher.StartInfo.FileName = ExecutableResolver.Resolve(config.GmshPath, "gmsh");
            mesher.StartInfo.Arguments = arguments;
            mesher.StartInfo.UseShellExecute = false;
            mesher.StartInfo.CreateNoWindow = true;
            mesher.StartInfo.RedirectStandardOutput = true;
            mesher.StartInfo.RedirectStandardError = true;

            var errorLines = new List<string>();
            DataReceivedEventHandler collect = (_, e) =>
            {
                if (e.Data == null) return;
                string clean = StripAnsi(e.Data);
                if (clean.StartsWith("Error", StringComparison.Ordinal))
                {
                    lock (errorLines) errorLines.Add(clean);
                }
            };
            mesher.OutputDataReceived += collect;
            mesher.ErrorDataReceived += collect;

            try
            {
                mesher.Start();
                mesher.BeginOutputReadLine();
                mesher.BeginErrorReadLine();
                mesher.WaitForExit();
                string[] lines;
                lock (errorLines) lines = errorLines.ToArray();
                return new GmshRun(mesher.ExitCode, ExtractGmshError(lines));
            }
            catch (Exception ex)
            {
                CopyGeoToFehlerLogs(workingDirectory, scriptForErrorLog);
                throw new Exception($"Gmsh konnte nicht gestartet werden: {ex.Message}. .geo-Skript nach FehlerLogs kopiert.", ex);
            }
        }

        /// <summary>Entfernt ANSI-Farbcodes (<c>ESC[1m</c>, <c>ESC[31m</c> …): Gmsh färbt Fehlerzeilen ein, auch in einer Pipe.</summary>
        internal static string StripAnsi(string text)
            => System.Text.RegularExpressions.Regex.Replace(text, "\u001B\\[[0-9;]*m", string.Empty);

        /// <summary>
        /// Die aussagekräftigen Fehlerzeilen aus der Gmsh-Ausgabe (höchstens zwei, ohne die Schlusszusammenfassung
        /// "Mesh generation error summary", Trennlinien und Zähler). <c>null</c>, wenn keine vorkommt.
        /// </summary>
        public static string? ExtractGmshError(IEnumerable<string> lines)
        {
            var picked = new List<string>();
            foreach (string withCodes in lines)
            {
                string raw = StripAnsi(withCodes);
                if (!raw.StartsWith("Error", StringComparison.Ordinal)) continue;
                int colon = raw.IndexOf(':');
                string text = (colon >= 0 ? raw.Substring(colon + 1) : raw).Trim();
                if (text.Length == 0 || text.StartsWith("-", StringComparison.Ordinal)) continue;
                if (text.Contains("error summary", StringComparison.OrdinalIgnoreCase)) continue;
                if (text.Contains("Check the full log", StringComparison.OrdinalIgnoreCase)) continue;
                if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\s+(warning|error)s?$")) continue;

                picked.Add(text);
                if (picked.Count == 2) break;
            }

            if (picked.Count == 0) return null;
            string joined = string.Join(" | ", picked);
            return joined.Length > 300 ? joined.Substring(0, 300) + "…" : joined;
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
        /// <paramref name="wallFieldPath"/> ist das Wandfeld (<see cref="BoundaryLayerWallField"/>, Richtung × Dicke je Knoten);
        /// ohne Angabe extrudiert Gmsh entlang der rohen Knotennormalen.
        /// </summary>
        public void WriteGeoScript(
            string geoScriptPath,
            string modelStlPath,
            string tunnelStlPath,
            SimulationConfig config,
            float scaleFactor,
            float refinementFactor,
            string? wallFieldPath)
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
            float safeScale = SanitizeScaleFactor(scaleFactor);
            string scaleString = safeScale.ToString(CultureInfo.InvariantCulture);

            float factor = refinementFactor > 0f ? refinementFactor : 1.0f;
            float sizeMin = _options.BoundaryLayerSizeMin * factor * safeScale;
            float sizeMax = _options.BoundaryLayerSizeMax * factor * safeScale;
            float distMin = _options.BoundaryLayerDistMin * safeScale;
            float distMax = _options.BoundaryLayerDistMax * safeScale;
            float curvMinFactor = Math.Clamp(_options.CurvatureMinSizeFactor, 0.1f, 1.0f);

            if (isThreshold)
            {
                var geoLines = new List<string>
                {
                    "// --- ALLGEMEINE NETZ-EINSTELLGEN ---".Replace("EINSTELLGEN", "EINSTELLUNGEN"),
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
                    $"Field[2].DistMin = {Format(distMin)};",
                    $"Field[2].DistMax = {Format(distMax)};"
                };

                if (_options.WakeRefinement)
                {
                    AddWakeBox(geoLines, "model_surfs[]", factor, safeScale, sizeMax, "// F) DYNAMISCHE WAKE-BOX (NACHLAUF)");
                    geoLines.Add("// ==========================================");
                    geoLines.Add("// G) FELDER INTELLIGENT KOMBINIEREN");
                    geoLines.Add("// ==========================================");
                }

                // Zusammenfassen: Wandnähe (2), Nachlauf-Box (3) und Detailbox (5), jeweils falls aktiv.
                var backgroundFields = new List<string> { "2" };
                if (_options.WakeRefinement) backgroundFields.Add("3");
                if (_options.DetailBox)
                {
                    AddDetailBox(geoLines, "model_surfs[]", factor, safeScale);
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
                    geoLines.Add($"Mesh.MeshSizeMin = {Format(sizeMin * curvMinFactor)};");
                    geoLines.Add($"Mesh.MeshSizeMax = {Format(sizeMax)};");
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
                BoundaryLayerSpec spec = ComputeBoundaryLayerSpec(config, modelStlPath, safeScale);
                int layerCount = Math.Max(1, spec.LayerCount);

                float y1Geo = spec.FirstLayerHeightMm * safeScale;
                var layerHeights = new List<float>(layerCount);
                double r = spec.GrowthRate;
                for (int i = 1; i <= layerCount; i++)
                {
                    double h = y1Geo * (Math.Pow(r, i) - 1.0) / (r - 1.0);
                    layerHeights.Add((float)h);
                }

                string subDivs = string.Join(", ", Enumerable.Repeat("1", layerCount));
                string usingView = wallFieldPath != null ? "Using View[0]; " : string.Empty;
                string heights = string.Join(", ", layerHeights.Select(h => Format(h)));

                float surfaceSize = (_options.SurfaceCellSize > 0f ? _options.SurfaceCellSize : _options.BoundaryLayerSizeMin) * factor * safeScale;

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
                    $"Mesh.MeshSizeMin = {Format(surfaceSize * curvMinFactor)};",
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

                    $"// B) Strukturierte Wandschicht: {layerCount} Schichten {cellWord}, erste Zelle y1 = {Format(y1Geo)}",
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
                    $"Field[2].DistMin = {Format(distMin)};",
                    $"Field[2].DistMax = {Format(distMax)};",
                });

                if (_options.WakeRefinement)
                {
                    AddWakeBox(geoLines, "wall[]", factor, safeScale, sizeMax, "// G) DYNAMISCHE WAKE-BOX (NACHLAUF)");
                }

                // Zusammenfassen: Wandnähe (2), Nachlauf-Box (3) und Detailbox (5), jeweils falls aktiv.
                var backgroundFields = new List<string> { "2" };
                if (_options.WakeRefinement) backgroundFields.Add("3");
                if (_options.DetailBox)
                {
                    AddDetailBox(geoLines, "wall[]", factor, safeScale);
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

        private void AddWakeBox(List<string> geoLines, string surfaces, float factor, float safeScale, float sizeMaxGeo, string headerLine)
        {
            geoLines.Add("// ==========================================");
            geoLines.Add(headerLine);
            geoLines.Add("// ==========================================");
            geoLines.Add($"bbox() = BoundingBox Surface {{{surfaces}}};");
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
                ? Format(_options.WakeCellSize * factor * safeScale)
                : $"(lenX / 6.0) * {Format(factor)}";
            geoLines.Add($"Field[3].VIn = {vIn};");
            geoLines.Add($"Field[3].VOut = {Format(sizeMaxGeo)};");
        }

        /// <summary>
        /// Detailbox um und hinter dem Modell (TODO-W6) als <c>Field[5]</c>: von 0,1 L vor dem Modell bis
        /// <see cref="GmshMesherOptions.DetailBoxLengthFactor"/> · L dahinter, quer Modellausdehnung plus
        /// <see cref="GmshMesherOptions.DetailBoxWidthFactor"/> · Ausdehnung (in Z mindestens
        /// <see cref="GmshMesherOptions.DetailBoxMinHeightLengthFactor"/> · L). Zellgröße
        /// <see cref="GmshMesherOptions.DetailBoxCellSize"/> (0 = 2,5 × Oberflächen-Zellgröße) × Netzfaktor.
        /// </summary>
        private void AddDetailBox(List<string> geoLines, string surfaces, float factor, float safeScale)
        {
            float baseSizeGeo = (_options.SurfaceCellSize > 0f ? _options.SurfaceCellSize : _options.BoundaryLayerSizeMin) * safeScale;
            float cell = (_options.DetailBoxCellSize > 0f ? _options.DetailBoxCellSize * safeScale : 2.5f * baseSizeGeo) * factor;
            float minHeightLengthFactor = Math.Max(0f, _options.DetailBoxMinHeightLengthFactor);

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
            geoLines.Add($"dz_width = {Format(_options.DetailBoxWidthFactor)} * dlenZ;");
            geoLines.Add($"dz_min_len = {Format(minHeightLengthFactor)} * dlenX;");
            geoLines.Add("dz_pad = (dz_width > dz_min_len) ? dz_width : dz_min_len;");
            geoLines.Add("Field[5].ZMin = dminZ - dz_pad;");
            geoLines.Add("Field[5].ZMax = dmaxZ + dz_pad;");
            geoLines.Add($"Field[5].VIn = {Format(cell)};");
            geoLines.Add($"Field[5].VOut = {Format(_options.BoundaryLayerSizeMax * factor * safeScale)};");
            geoLines.Add($"Field[5].Thickness = {Format(Math.Max(cell * 5f, 1.0f * safeScale))};");
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
