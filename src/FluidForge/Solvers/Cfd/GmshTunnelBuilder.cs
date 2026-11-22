using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Effective physical wind-tunnel dimensions in real-world millimeters,
    /// decoupled from the PicoGK scale factor.
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
    /// Sizing, generation, caching, hashing, and logging of the 3D wind-tunnel STL.
    /// </summary>
    internal sealed class GmshTunnelBuilder
    {
        private readonly GmshMesherOptions _options;
        private string? _cachedTunnelPath;

        public GmshTunnelBuilder(GmshMesherOptions options)
        {
            _options = options;
        }

        public static float SanitizeScaleFactor(float scaleFactor)
            => scaleFactor > 0f && float.IsFinite(scaleFactor) ? scaleFactor : 1.0f;

        /// <summary>Generates the static reference wind tunnel without automatic expansion.</summary>
        public string EnsureTunnel(string workingDirectory)
        {
            if (_cachedTunnelPath != null && File.Exists(_cachedTunnelPath))
                return _cachedTunnelPath;

            IReadOnlyList<StlTriangle> tunnel = BuildTunnel();
            Console.WriteLine($"{OutputConventions.SystemTag} Generating static reference wind tunnel ({tunnel.Count} triangles)...");
            _cachedTunnelPath = Path.Combine(workingDirectory, "Static_Windtunnel.stl");
            StlWriter.WriteBinary(_cachedTunnelPath, tunnel);
            return _cachedTunnelPath;
        }

        /// <summary>
        /// Generates the physical wind tunnel in the PicoGK workspace (scaled by <paramref name="scaleFactor"/>),
        /// guaranteeing minimum clearances and maximum blockage ratio.
        /// </summary>
        public string EnsureTunnel(
            string workingDirectory,
            MeshBounds? modelBoundsPicoGk,
            float scaleFactor,
            float? realFrontalAreaMm2,
            IDictionary<string, float> metrics)
        {
            float safeScale = SanitizeScaleFactor(scaleFactor);
            SymmetryAxis symAxis = StlHalfClipper.ParseConfigValue(_options.SymmetryPlane);
            TunnelSpec spec = ResolveEffectiveTunnel(modelBoundsPicoGk, safeScale, realFrontalAreaMm2);

            bool isDefaultPlainFile = !spec.WasExpanded && MathF.Abs(safeScale - 1.0f) < 1e-6f && symAxis == SymmetryAxis.None;
            string tunnelPath;
            string header;
            if (isDefaultPlainFile)
            {
                tunnelPath = Path.Combine(workingDirectory, "Static_Windtunnel.stl");
                header = StlWriter.DefaultHeader;
            }
            else
            {
                string hash = ComputeTunnelHash(spec, safeScale, symAxis);
                tunnelPath = Path.Combine(workingDirectory, $"Static_Windtunnel_{hash}.stl");
                header = $"FluidForge STL mm {hash}";
            }

            int expectedTriCount = spec.IsCylinder ? 4 * spec.Segments : 12;
            long expectedFileLength = 84L + 50L * expectedTriCount;

            if (!IsTunnelCacheValid(tunnelPath, expectedFileLength, header, spec, safeScale, checkBounds: isDefaultPlainFile))
            {
                IReadOnlyList<StlTriangle> tunnel = BuildTunnel(spec, safeScale);
                StlWriter.WriteBinary(tunnelPath, tunnel, header);
            }

            metrics["TunnelBlockagePercent"] = spec.BlockagePercent;
            metrics["TunnelLengthMm"] = spec.EffectiveLengthMm;
            metrics["TunnelWidthMm"] = spec.EffectiveWidthMm;
            metrics["TunnelHeightMm"] = spec.EffectiveHeightMm;
            metrics["TunnelUpstreamL"] = spec.UpstreamL;
            metrics["TunnelDownstreamL"] = spec.DownstreamL;

            LogTunnelSummary(spec, safeScale);
            return tunnelPath;
        }

        /// <summary>
        /// Computes effective physical tunnel dimensions in real-world millimeters.
        /// The configured wind tunnel is expanded outward if needed, never shrunk.
        /// </summary>
        public TunnelSpec ResolveEffectiveTunnel(
            MeshBounds? modelBoundsPicoGk,
            float scaleFactor,
            float? realFrontalAreaMm2 = null)
        {
            float safeScale = SanitizeScaleFactor(scaleFactor);
            SymmetryAxis symAxis = StlHalfClipper.ParseConfigValue(_options.SymmetryPlane);
            string shape = (_options.TunnelShape ?? string.Empty).Replace(" ", string.Empty).Trim();
            bool isCylinder = shape.Equals("Cylinder", StringComparison.OrdinalIgnoreCase);

            if (!isCylinder && shape.Length > 0 && !shape.Equals("Box", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{OutputConventions.WarningTag} Unknown wind-tunnel shape '{_options.TunnelShape}' — using 'Box'.");
            }

            if (isCylinder && symAxis != SymmetryAxis.None)
            {
                SimulationLog.Detail($"{OutputConventions.WarningTag} TunnelShape='Cylinder' with SymmetryPlane='{symAxis}' — falling back to half-Box wind tunnel.");
                isCylinder = false;
            }

            bool cylinderConfigSource = shape.Equals("Cylinder", StringComparison.OrdinalIgnoreCase);
            float cfgCenterX = _options.TunnelCenterX;
            float cfgCenterY = _options.TunnelCenterY;
            float cfgCenterZ = _options.TunnelCenterZ;

            float cfgLenX = cylinderConfigSource ? _options.TunnelLength : _options.TunnelSizeX;
            float cfgWidthY = cylinderConfigSource ? _options.TunnelDiameter : _options.TunnelSizeY;
            float cfgHeightZ = cylinderConfigSource ? _options.TunnelDiameter : _options.TunnelSizeZ;
            float cfgRadius = _options.TunnelDiameter * 0.5f;

            float cfgMinX = cfgCenterX - cfgLenX * 0.5f, cfgMaxX = cfgCenterX + cfgLenX * 0.5f;
            float cfgMinY = symAxis == SymmetryAxis.Y0 ? 0f : cfgCenterY - cfgWidthY * 0.5f;
            float cfgMaxY = symAxis == SymmetryAxis.Y0
                ? MathF.Max(cfgCenterY + cfgWidthY * 0.5f, cfgWidthY * 0.5f)
                : cfgCenterY + cfgWidthY * 0.5f;
            float cfgMinZ = symAxis == SymmetryAxis.Z0 ? 0f : cfgCenterZ - cfgHeightZ * 0.5f;
            float cfgMaxZ = symAxis == SymmetryAxis.Z0
                ? MathF.Max(cfgCenterZ + cfgHeightZ * 0.5f, cfgHeightZ * 0.5f)
                : cfgCenterZ + cfgHeightZ * 0.5f;

            float effMinX = cfgMinX, effMaxX = cfgMaxX;
            float effMinY = cfgMinY, effMaxY = cfgMaxY;
            float effMinZ = cfgMinZ, effMaxZ = cfgMaxZ;
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

                bool inputWasFullAlongSym = symAxis switch
                {
                    SymmetryAxis.Y0 => realMin.Y < -MathF.Max(1e-4f * MathF.Max(modelB, 1e-3f), 1e-5f) && realMax.Y > 0f,
                    SymmetryAxis.Z0 => realMin.Z < -MathF.Max(1e-4f * MathF.Max(modelH, 1e-3f), 1e-5f) && realMax.Z > 0f,
                    _ => false
                };

                if (symAxis != SymmetryAxis.None && inputWasFullAlongSym)
                {
                    // Half-model tunnel sees half the frontal area against half the cross-section (same blockage).
                    frontalAreaMm2 *= 0.5f;
                }

                float fullBForClearance = symAxis == SymmetryAxis.Y0 && !inputWasFullAlongSym
                    ? 2.0f * Math.Max(0f, realMax.Y)
                    : modelB;
                float fullHForClearance = symAxis == SymmetryAxis.Z0 && !inputWasFullAlongSym
                    ? 2.0f * Math.Max(0f, realMax.Z)
                    : modelH;

                float upFactor = Math.Max(0f, _options.TunnelMinUpstreamFactor);
                float downFactor = Math.Max(0f, _options.TunnelMinDownstreamFactor);
                float latFactor = Math.Max(0f, _options.TunnelMinLateralFactor);

                float reqMinX = realMin.X - upFactor * modelL, reqMaxX = realMax.X + downFactor * modelL;
                float reqMinY = symAxis == SymmetryAxis.Y0 ? 0f : realMin.Y - latFactor * fullBForClearance;
                float reqMaxY = realMax.Y + latFactor * fullBForClearance;
                float reqMinZ = symAxis == SymmetryAxis.Z0 ? 0f : realMin.Z - latFactor * fullHForClearance;
                float reqMaxZ = realMax.Z + latFactor * fullHForClearance;

                effMinX = Math.Min(cfgMinX, reqMinX);
                effMaxX = Math.Max(cfgMaxX, reqMaxX);

                if (isCylinder)
                {
                    float dy = Math.Max(Math.Abs(reqMinY - cfgCenterY), Math.Abs(reqMaxY - cfgCenterY));
                    float dz = Math.Max(Math.Abs(reqMinZ - cfgCenterZ), Math.Abs(reqMaxZ - cfgCenterZ));
                    effRadius = Math.Max(cfgRadius, MathF.Sqrt(dy * dy + dz * dz));

                    wasExpandedForClearance =
                        effMinX < cfgMinX - 1e-4f || effMaxX > cfgMaxX + 1e-4f || effRadius > cfgRadius + 1e-4f;

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

                    effMinY = cfgCenterY - effRadius; effMaxY = cfgCenterY + effRadius;
                    effMinZ = cfgCenterZ - effRadius; effMaxZ = cfgCenterZ + effRadius;
                }
                else
                {
                    effMinY = symAxis == SymmetryAxis.Y0 ? 0f : Math.Min(cfgMinY, reqMinY);
                    effMaxY = Math.Max(cfgMaxY, reqMaxY);
                    effMinZ = symAxis == SymmetryAxis.Z0 ? 0f : Math.Min(cfgMinZ, reqMinZ);
                    effMaxZ = Math.Max(cfgMaxZ, reqMaxZ);

                    wasExpandedForClearance =
                        effMinX < cfgMinX - 1e-4f || effMaxX > cfgMaxX + 1e-4f ||
                        effMinY < cfgMinY - 1e-4f || effMaxY > cfgMaxY + 1e-4f ||
                        effMinZ < cfgMinZ - 1e-4f || effMaxZ > cfgMaxZ + 1e-4f;

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

                            if (symAxis == SymmetryAxis.Y0)
                            {
                                effMinY = 0f;
                                effMaxY = (float)(sizeYAfterClearance * scaleYZ);
                                float cz = 0.5f * (effMinZ + effMaxZ);
                                float halfZ = 0.5f * (float)(sizeZAfterClearance * scaleYZ);
                                effMinZ = cz - halfZ; effMaxZ = cz + halfZ;
                            }
                            else if (symAxis == SymmetryAxis.Z0)
                            {
                                float cy = 0.5f * (effMinY + effMaxY);
                                float halfY = 0.5f * (float)(sizeYAfterClearance * scaleYZ);
                                effMinY = cy - halfY; effMaxY = cy + halfY;
                                effMinZ = 0f;
                                effMaxZ = (float)(sizeZAfterClearance * scaleYZ);
                            }
                            else
                            {
                                float cy = 0.5f * (effMinY + effMaxY);
                                float cz = 0.5f * (effMinZ + effMaxZ);
                                float halfY = 0.5f * (float)(sizeYAfterClearance * scaleYZ);
                                float halfZ = 0.5f * (float)(sizeZAfterClearance * scaleYZ);
                                effMinY = cy - halfY; effMaxY = cy + halfY;
                                effMinZ = cz - halfZ; effMaxZ = cz + halfZ;
                            }
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

            double effCrossArea = isCylinder ? Math.PI * effRadius * effRadius : (double)effSizeY * effSizeZ;
            float blockagePercent = effCrossArea > 0.0 && frontalAreaMm2 > 0f
                ? (float)((frontalAreaMm2 / effCrossArea) * 100.0)
                : 0f;

            return new TunnelSpec(
                IsCylinder: isCylinder,
                CenterX: effCenterX, CenterY: effCenterY, CenterZ: effCenterZ,
                SizeX: effSizeX, SizeY: effSizeY, SizeZ: effSizeZ,
                Diameter: effDiameter, Length: effSizeX, Segments: _options.TunnelSegments,
                ConfiguredLengthMm: cfgLenX, ConfiguredWidthMm: cfgWidthY, ConfiguredHeightMm: cfgHeightZ,
                FrontalAreaMm2: frontalAreaMm2, BlockagePercent: blockagePercent,
                UpstreamL: upstreamL, DownstreamL: downstreamL,
                WasExpandedForClearance: wasExpandedForClearance,
                WasExpandedForBlockage: wasExpandedForBlockage);
        }

        public static (MeshBounds? BoundsPicoGk, float? RealFrontalAreaMm2) ReadModelMetricsForTunnel(string modelStlPath, float safeScale)
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
                catch { return (null, null); }
            }
        }

        private IReadOnlyList<StlTriangle> BuildTunnel()
        {
            Vector3 center = new Vector3(_options.TunnelCenterX, _options.TunnelCenterY, _options.TunnelCenterZ);
            string shape = (_options.TunnelShape ?? string.Empty).Replace(" ", string.Empty).Trim();

            if (shape.Equals("Cylinder", StringComparison.OrdinalIgnoreCase))
            {
                return TunnelGeometry.CreateCylinder(_options.TunnelDiameter, _options.TunnelLength, center, _options.TunnelSegments);
            }

            if (shape.Length > 0 && !shape.Equals("Box", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine($"{OutputConventions.WarningTag} Unknown wind-tunnel shape '{_options.TunnelShape}' — using 'Box'.");
            }

            return TunnelGeometry.CreateBox(new Vector3(_options.TunnelSizeX, _options.TunnelSizeY, _options.TunnelSizeZ), center);
        }

        private static IReadOnlyList<StlTriangle> BuildTunnel(TunnelSpec spec, float safeScale)
        {
            Vector3 centerPico = new Vector3(spec.CenterX, spec.CenterY, spec.CenterZ) * safeScale;
            return spec.IsCylinder
                ? TunnelGeometry.CreateCylinder(spec.Diameter * safeScale, spec.Length * safeScale, centerPico, spec.Segments)
                : TunnelGeometry.CreateBox(new Vector3(spec.SizeX, spec.SizeY, spec.SizeZ) * safeScale, centerPico);
        }

        private static string ComputeTunnelHash(TunnelSpec spec, float safeScale, SymmetryAxis symAxis)
        {
            string canonical = string.Create(CultureInfo.InvariantCulture,
                $"{(spec.IsCylinder ? "C" : "B")}|{symAxis}|{spec.CenterX:G9}|{spec.CenterY:G9}|{spec.CenterZ:G9}|{spec.SizeX:G9}|{spec.SizeY:G9}|{spec.SizeZ:G9}|{spec.Diameter:G9}|{spec.Length:G9}|{spec.Segments}|{safeScale:G9}");
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical));
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }

        private static bool IsTunnelCacheValid(
            string tunnelPath, long expectedFileLength, string expectedHeader, TunnelSpec spec, float safeScale, bool checkBounds)
        {
            try
            {
                if (!File.Exists(tunnelPath) || new FileInfo(tunnelPath).Length != expectedFileLength)
                    return false;

                byte[] expectedHeaderBytes = new byte[80];
                Array.Fill(expectedHeaderBytes, (byte)0x20);
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
            catch { return false; }
        }

        private static void LogTunnelSummary(TunnelSpec spec, float safeScale)
        {
            string FormatMm(float v) => v.ToString("0.00", CultureInfo.InvariantCulture);
            string shapeLabel = spec.IsCylinder ? "Cylinder" : "Box";
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
                    ? "clearance + blockage"
                    : (spec.WasExpandedForClearance ? "clearance" : "blockage");
                expandNote = $" [AUTO-EXPANDED: {reasons}]";
            }

            SimulationLog.Detail(
                $"Wind tunnel ({shapeLabel}, scale={WakeAndDetailBoxBuilder.Format(safeScale)}): config {cfgDims} -> effective {effDims}{expandNote} | " +
                $"clearance {spec.UpstreamL.ToString("0.0", CultureInfo.InvariantCulture)}L upstream / {spec.DownstreamL.ToString("0.0", CultureInfo.InvariantCulture)}L downstream | " +
                $"A_frontal={spec.FrontalAreaMm2.ToString("0.0", CultureInfo.InvariantCulture)} mm², blockage={spec.BlockagePercent.ToString("0.000", CultureInfo.InvariantCulture)} %");
        }
    }
}
