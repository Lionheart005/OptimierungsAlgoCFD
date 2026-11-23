using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using FluidForge.Core;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Metadata returned by <see cref="OpenFoamCaseGenerator.Generate"/> describing the prepared OpenFOAM case.
    /// </summary>
    public sealed record OpenFoamCaseInfo(
        string CaseDirectory,
        string TriSurfaceStlPath,
        Vector3 LocationInMeshMeters,
        Vector3 InletDirection,
        float InletAreaM2,
        float ReferenceAreaM2,
        IReadOnlyList<string> PatchNames,
        IReadOnlyList<string> OutletPatchNames);

    /// <summary>
    /// Generates ESI OpenFOAM <c>v2506</c>-compatible case directories (<c>0/</c>, <c>constant/</c>,
    /// <c>system/</c>) for <c>Internal3D</c> and <c>External3D</c> simulations, including automatic
    /// SI-meter STL scaling, multi-solid ASCII STL export, FunctionObjects, and <c>cfMesh</c> <c>meshDict</c>.
    /// </summary>
    public static class OpenFoamCaseGenerator
    {
        private static string F(double value) => (value == 0.0 ? 0.0 : value).ToString("G7", CultureInfo.InvariantCulture);
        private static string F(float value) => (value == 0f ? 0f : value).ToString("G6", CultureInfo.InvariantCulture);

        /// <summary>
        /// Writes a complete OpenFOAM case into <paramref name="caseDirectory"/> for <paramref name="request"/>.
        /// </summary>
        public static OpenFoamCaseInfo Generate(
            string caseDirectory,
            MeshRequest request,
            OpenFoamOptions options,
            ModelRecord? record = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(caseDirectory);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(options);

            Directory.CreateDirectory(caseDirectory);
            Directory.CreateDirectory(Path.Combine(caseDirectory, "0"));
            Directory.CreateDirectory(Path.Combine(caseDirectory, "constant", "triSurface"));
            Directory.CreateDirectory(Path.Combine(caseDirectory, "system"));

            bool isExternal = string.Equals(options.DomainMode?.Trim(), "External3D", StringComparison.OrdinalIgnoreCase);
            float safeScaleFactor = request.ScaleFactor > 0f && float.IsFinite(request.ScaleFactor) ? request.ScaleFactor : 1.0f;
            string meshUnit = request.Geometry?.MeshUnit ?? "mm";
            float unitScale = string.Equals(meshUnit.Trim(), "m", StringComparison.OrdinalIgnoreCase) ? 1.0f : 0.001f;
            float scaleToMeters = (1.0f / safeScaleFactor) * unitScale;

            string wallName = string.IsNullOrWhiteSpace(options.WallPatchName) ? "wall" : options.WallPatchName.Trim();
            string inletName = string.IsNullOrWhiteSpace(options.InletPatchName) ? "inlet" : options.InletPatchName.Trim();
            string outletPrefix = string.IsNullOrWhiteSpace(options.OutletPatchPrefix) ? "outlet" : options.OutletPatchPrefix.Trim();

            var scaledPatches = LoadAndScalePatches(request, options, scaleToMeters, isExternal, wallName);
            string triSurfacePath = Path.Combine(caseDirectory, "constant", "triSurface", "geometry.stl");
            StlWriter.WriteAsciiMultiSolid(triSurfacePath, scaledPatches);

            (Vector3 minBounds, Vector3 maxBounds) = ComputeBounds(scaledPatches);
            float diagM = MathF.Max((maxBounds - minBounds).Length(), 1e-4f);
            float cellSizeM = MathF.Max(options.BaseCellSizeMm * 0.001f * MathF.Max(request.RefinementFactor, 0.05f), 1e-5f);

            SymmetryAxis symAxis = isExternal ? StlHalfClipper.ParseConfigValue(options.SymmetryPlane) : SymmetryAxis.None;

            Vector3 locationInMesh = ResolveLocationInMesh(
                request, options, isExternal, scaleToMeters, scaledPatches, minBounds, maxBounds, symAxis);

            List<string> patchNames = scaledPatches.Keys.ToList();
            List<string> outletPatchNames = patchNames
                .Where(p => p.StartsWith(outletPrefix, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (isExternal)
            {
                if (!patchNames.Contains(inletName, StringComparer.OrdinalIgnoreCase))
                    patchNames.Add(inletName);
                if (outletPatchNames.Count == 0)
                {
                    outletPatchNames.Add(outletPrefix);
                    if (!patchNames.Contains(outletPrefix, StringComparer.OrdinalIgnoreCase))
                        patchNames.Add(outletPrefix);
                }
            }

            Vector3 inletDirection = ResolveInletDirection(options, scaledPatches, inletName, isExternal);
            float inletAreaM2 = ComputePatchArea(scaledPatches, inletName);
            float referenceAreaM2 = ResolveReferenceAreaM2(options, record, minBounds, maxBounds, symAxis);

            WriteBlockMeshDict(
                caseDirectory, options, isExternal, symAxis, minBounds, maxBounds, diagM, cellSizeM, inletName, outletPrefix);

            WriteZeroFields(
                caseDirectory, options, isExternal, symAxis, patchNames, inletName, outletPrefix,
                inletDirection, inletAreaM2);

            WriteConstantDictionaries(caseDirectory, options);

            WriteSystemDictionaries(
                caseDirectory, request, options, isExternal, symAxis, scaledPatches,
                inletName, outletPrefix, outletPatchNames, wallName,
                locationInMesh, cellSizeM, referenceAreaM2, inletDirection);

            return new OpenFoamCaseInfo(
                caseDirectory,
                triSurfacePath,
                locationInMesh,
                inletDirection,
                inletAreaM2,
                referenceAreaM2,
                patchNames,
                outletPatchNames);
        }

        private static Dictionary<string, IReadOnlyList<StlTriangle>> LoadAndScalePatches(
            MeshRequest request,
            OpenFoamOptions options,
            float scaleToMeters,
            bool isExternal,
            string wallName)
        {
            var result = new Dictionary<string, IReadOnlyList<StlTriangle>>(StringComparer.OrdinalIgnoreCase);

            if (!isExternal && request.Geometry != null && request.Geometry.HasSurfacePatches)
            {
                foreach (KeyValuePair<string, string> kvp in request.Geometry.SurfacePatches)
                {
                    IReadOnlyList<StlTriangle> raw = ReadTrianglesAny(kvp.Value);
                    result[kvp.Key.Trim()] = ScaleTriangles(raw, scaleToMeters);
                }
                return result;
            }

            string stlPath = !string.IsNullOrWhiteSpace(request.Geometry?.StlPath)
                ? request.Geometry.StlPath
                : request.GeometryPath;

            if (string.IsNullOrWhiteSpace(stlPath) || !File.Exists(stlPath))
                throw new FileNotFoundException($"Geometry STL file not found for OpenFOAM case: '{stlPath}'.", stlPath);

            IReadOnlyList<StlTriangle> triangles = ReadTrianglesAny(stlPath);
            if (isExternal)
            {
                SymmetryAxis symAxis = StlHalfClipper.ParseConfigValue(options.SymmetryPlane);
                if (symAxis != SymmetryAxis.None)
                {
                    HalfClipResult clipped = StlHalfClipper.ClipToHalfSpace(triangles, symAxis);
                    if (clipped.HalfTriangles.Count > 0)
                        triangles = clipped.HalfTriangles;
                }
            }

            result[wallName] = ScaleTriangles(triangles, scaleToMeters);
            return result;
        }

        /// <summary>Reads binary or ASCII STL triangles cleanly.</summary>
        public static IReadOnlyList<StlTriangle> ReadTrianglesAny(string stlPath)
        {
            if (string.IsNullOrWhiteSpace(stlPath))
                throw new ArgumentException("STL path must not be empty.", nameof(stlPath));
            if (!File.Exists(stlPath))
                throw new FileNotFoundException("STL file not found.", stlPath);

            byte[] headerBytes = new byte[Math.Min(80, new FileInfo(stlPath).Length)];
            using (FileStream fs = File.OpenRead(stlPath))
            {
                _ = fs.Read(headerBytes, 0, headerBytes.Length);
            }

            string headerText = Encoding.ASCII.GetString(headerBytes).TrimStart();
            if (headerText.StartsWith("solid", StringComparison.OrdinalIgnoreCase) && IsLikelyAsciiStl(stlPath))
            {
                return ReadAsciiTriangles(stlPath);
            }

            IReadOnlyList<StlTriangle> binary = StlWriter.ReadTriangles(stlPath);
            return binary.Count > 0 ? binary : ReadAsciiTriangles(stlPath);
        }

        private static bool IsLikelyAsciiStl(string stlPath)
        {
            long length = new FileInfo(stlPath).Length;
            if (length >= 84)
            {
                using var br = new BinaryReader(File.OpenRead(stlPath));
                br.BaseStream.Seek(80, SeekOrigin.Begin);
                uint triCount = br.ReadUInt32();
                if (length == 84L + (long)triCount * 50L)
                    return false;
            }
            return true;
        }

        private static IReadOnlyList<StlTriangle> ReadAsciiTriangles(string stlPath)
        {
            var triangles = new List<StlTriangle>();
            var verts = new List<Vector3>(3);

            foreach (string rawLine in File.ReadLines(stlPath))
            {
                string line = rawLine.Trim();
                if (!line.StartsWith("vertex", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 4 &&
                    float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float x) &&
                    float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out float y) &&
                    float.TryParse(parts[3], NumberStyles.Float, CultureInfo.InvariantCulture, out float z))
                {
                    verts.Add(new Vector3(x, y, z));
                    if (verts.Count == 3)
                    {
                        triangles.Add(new StlTriangle(verts[0], verts[1], verts[2]));
                        verts.Clear();
                    }
                }
            }

            return triangles;
        }

        private static IReadOnlyList<StlTriangle> ScaleTriangles(IReadOnlyList<StlTriangle> triangles, float scale)
        {
            if (MathF.Abs(scale - 1.0f) < 1e-7f)
                return triangles;

            var scaled = new StlTriangle[triangles.Count];
            for (int i = 0; i < triangles.Count; i++)
            {
                StlTriangle t = triangles[i];
                scaled[i] = new StlTriangle(t.A * scale, t.B * scale, t.C * scale);
            }
            return scaled;
        }

        private static (Vector3 Min, Vector3 Max) ComputeBounds(IReadOnlyDictionary<string, IReadOnlyList<StlTriangle>> patches)
        {
            Vector3 min = new(float.MaxValue);
            Vector3 max = new(float.MinValue);
            bool any = false;

            foreach (IReadOnlyList<StlTriangle> tris in patches.Values)
            {
                foreach (StlTriangle t in tris)
                {
                    any = true;
                    min = Vector3.Min(min, Vector3.Min(t.A, Vector3.Min(t.B, t.C)));
                    max = Vector3.Max(max, Vector3.Max(t.A, Vector3.Max(t.B, t.C)));
                }
            }

            return any ? (min, max) : (Vector3.Zero, Vector3.One);
        }

        private static Vector3 ResolveLocationInMesh(
            MeshRequest request,
            OpenFoamOptions options,
            bool isExternal,
            float scaleToMeters,
            IReadOnlyDictionary<string, IReadOnlyList<StlTriangle>> scaledPatches,
            Vector3 bodyMin,
            Vector3 bodyMax,
            SymmetryAxis symAxis)
        {
            if (!isExternal && request.Geometry?.FluidSeedPoint is Vector3 seed)
            {
                return seed * scaleToMeters;
            }

            if (isExternal)
            {
                Vector3 size = bodyMax - bodyMin;
                float lenX = MathF.Max(size.X, 1e-3f);
                float xUp = bodyMin.X - MathF.Max(options.TunnelUpstreamFactor, 1.0f) * lenX;
                float xSample = 0.5f * (xUp + bodyMin.X);
                float ySample = symAxis == SymmetryAxis.Y0
                    ? bodyMax.Y + MathF.Max(size.Y, lenX * 0.25f) * 0.5f
                    : 0.5f * (bodyMin.Y + bodyMax.Y);
                float zSample = symAxis == SymmetryAxis.Z0
                    ? bodyMax.Z + MathF.Max(size.Z, lenX * 0.25f) * 0.5f
                    : 0.5f * (bodyMin.Z + bodyMax.Z);
                return new Vector3(xSample, ySample, zSample);
            }

            // Fallback for internal flows without explicit FluidSeedPoint: center of scaled bounding box.
            if (scaledPatches.Count > 0)
                return 0.5f * (bodyMin + bodyMax);

            return Vector3.Zero;
        }

        /// <summary>
        /// Computes the unit inflow velocity direction vector in SI coordinates.
        /// For <c>Internal3D</c>, negates the area-weighted outward normal of the <c>inlet</c> STL patch.
        /// </summary>
        public static Vector3 ResolveInletDirection(
            OpenFoamOptions options,
            IReadOnlyDictionary<string, IReadOnlyList<StlTriangle>> patches,
            string inletPatchName,
            bool isExternal)
        {
            if (options.InletDirection != null && options.InletDirection.Length == 3)
            {
                var explicitDir = new Vector3(
                    options.InletDirection[0],
                    options.InletDirection[1],
                    options.InletDirection[2]);
                float len = explicitDir.Length();
                if (float.IsFinite(len) && len > 1e-6f)
                    return explicitDir / len;
            }

            if (!isExternal && patches.TryGetValue(inletPatchName, out IReadOnlyList<StlTriangle>? inletTris) && inletTris.Count > 0)
            {
                Vector3 weightedNormal = Vector3.Zero;
                foreach (StlTriangle tri in inletTris)
                {
                    // Cross product magnitude is 2 * area and points along the outward triangle normal.
                    weightedNormal += Vector3.Cross(tri.B - tri.A, tri.C - tri.A);
                }

                if (weightedNormal.LengthSquared() > 1e-18f)
                {
                    Vector3 dir = -Vector3.Normalize(weightedNormal);
                    return new Vector3(
                        MathF.Abs(dir.X) < 1e-6f ? 0f : dir.X,
                        MathF.Abs(dir.Y) < 1e-6f ? 0f : dir.Y,
                        MathF.Abs(dir.Z) < 1e-6f ? 0f : dir.Z);
                }
            }

            if (isExternal && MathF.Abs(options.AngleOfAttackDeg) > 1e-6f)
            {
                float rad = options.AngleOfAttackDeg * (MathF.PI / 180.0f);
                return Vector3.Normalize(new Vector3(MathF.Cos(rad), 0f, MathF.Sin(rad)));
            }

            return Vector3.UnitX;
        }

        private static float ComputePatchArea(IReadOnlyDictionary<string, IReadOnlyList<StlTriangle>> patches, string patchName)
        {
            if (!patches.TryGetValue(patchName, out IReadOnlyList<StlTriangle>? tris) || tris.Count == 0)
                return 0f;

            double area = 0.0;
            foreach (StlTriangle tri in tris)
            {
                area += 0.5 * Vector3.Cross(tri.B - tri.A, tri.C - tri.A).Length();
            }
            return (float)area;
        }

        private static float ResolveReferenceAreaM2(
            OpenFoamOptions options,
            ModelRecord? record,
            Vector3 bodyMin,
            Vector3 bodyMax,
            SymmetryAxis symAxis)
        {
            if (options.ReferenceArea > 0f && float.IsFinite(options.ReferenceArea))
                return symAxis == SymmetryAxis.None ? options.ReferenceArea : options.ReferenceArea * 0.5f;

            if (record != null && !string.IsNullOrWhiteSpace(options.ReferenceAreaMetric) &&
                record.PassiveParameters.TryGetValue(options.ReferenceAreaMetric, out float mm2) &&
                float.IsFinite(mm2) && mm2 > 0f)
            {
                float areaM2 = mm2 * 1e-6f;
                return symAxis == SymmetryAxis.None ? areaM2 : areaM2 * 0.5f;
            }

            Vector3 size = bodyMax - bodyMin;
            float boxArea = MathF.Max(size.Y * size.Z, 1e-6f);
            return boxArea;
        }

        private static void WriteBlockMeshDict(
            string caseDir,
            OpenFoamOptions options,
            bool isExternal,
            SymmetryAxis symAxis,
            Vector3 bodyMin,
            Vector3 bodyMax,
            float diagM,
            float cellSizeM,
            string inletName,
            string outletName)
        {
            Vector3 boxMin;
            Vector3 boxMax;

            if (isExternal)
            {
                Vector3 size = bodyMax - bodyMin;
                float lenX = MathF.Max(size.X, 1e-3f);
                float latY = MathF.Max(size.Y, lenX * 0.25f) * MathF.Max(options.TunnelLateralFactor, 1.0f);
                float latZ = MathF.Max(size.Z, lenX * 0.25f) * MathF.Max(options.TunnelLateralFactor, 1.0f);

                boxMin = new Vector3(
                    bodyMin.X - MathF.Max(options.TunnelUpstreamFactor, 1.0f) * lenX,
                    symAxis == SymmetryAxis.Y0 ? 0.0f : bodyMin.Y - latY,
                    symAxis == SymmetryAxis.Z0 ? 0.0f : bodyMin.Z - latZ);
                boxMax = new Vector3(
                    bodyMax.X + MathF.Max(options.TunnelDownstreamFactor, 2.0f) * lenX,
                    bodyMax.Y + latY,
                    bodyMax.Z + latZ);
            }
            else
            {
                float pad = MathF.Max(2.5f * cellSizeM, 0.05f * diagM);
                boxMin = bodyMin - new Vector3(pad);
                boxMax = bodyMax + new Vector3(pad);
            }

            Vector3 span = boxMax - boxMin;
            int nx = Math.Max(4, (int)MathF.Ceiling(span.X / cellSizeM));
            int ny = Math.Max(4, (int)MathF.Ceiling(span.Y / cellSizeM));
            int nz = Math.Max(4, (int)MathF.Ceiling(span.Z / cellSizeM));

            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "blockMeshDict");
            sb.AppendLine("scale   1;");
            sb.AppendLine();
            sb.AppendLine("vertices");
            sb.AppendLine("(");
            sb.AppendLine($"    ({F(boxMin.X)} {F(boxMin.Y)} {F(boxMin.Z)})");
            sb.AppendLine($"    ({F(boxMax.X)} {F(boxMin.Y)} {F(boxMin.Z)})");
            sb.AppendLine($"    ({F(boxMax.X)} {F(boxMax.Y)} {F(boxMin.Z)})");
            sb.AppendLine($"    ({F(boxMin.X)} {F(boxMax.Y)} {F(boxMin.Z)})");
            sb.AppendLine($"    ({F(boxMin.X)} {F(boxMin.Y)} {F(boxMax.Z)})");
            sb.AppendLine($"    ({F(boxMax.X)} {F(boxMin.Y)} {F(boxMax.Z)})");
            sb.AppendLine($"    ({F(boxMax.X)} {F(boxMax.Y)} {F(boxMax.Z)})");
            sb.AppendLine($"    ({F(boxMin.X)} {F(boxMax.Y)} {F(boxMax.Z)})");
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine("blocks");
            sb.AppendLine("(");
            sb.AppendLine($"    hex (0 1 2 3 4 5 6 7) ({nx} {ny} {nz}) simpleGrading (1 1 1)");
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine("edges");
            sb.AppendLine("(");
            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine("boundary");
            sb.AppendLine("(");

            if (isExternal)
            {
                sb.AppendLine($"    {inletName}");
                sb.AppendLine("    {");
                sb.AppendLine("        type patch;");
                sb.AppendLine("        faces ( (0 4 7 3) );");
                sb.AppendLine("    }");
                sb.AppendLine($"    {outletName}");
                sb.AppendLine("    {");
                sb.AppendLine("        type patch;");
                sb.AppendLine("        faces ( (1 2 6 5) );");
                sb.AppendLine("    }");

                if (symAxis == SymmetryAxis.Y0)
                {
                    sb.AppendLine("    symmetry");
                    sb.AppendLine("    {");
                    sb.AppendLine("        type symmetry;");
                    sb.AppendLine("        faces ( (0 1 5 4) );");
                    sb.AppendLine("    }");
                    sb.AppendLine("    freestream");
                    sb.AppendLine("    {");
                    sb.AppendLine("        type patch;");
                    sb.AppendLine("        faces ( (3 7 6 2) (0 3 2 1) (4 5 6 7) );");
                    sb.AppendLine("    }");
                }
                else if (symAxis == SymmetryAxis.Z0)
                {
                    sb.AppendLine("    symmetry");
                    sb.AppendLine("    {");
                    sb.AppendLine("        type symmetry;");
                    sb.AppendLine("        faces ( (0 3 2 1) );");
                    sb.AppendLine("    }");
                    sb.AppendLine("    freestream");
                    sb.AppendLine("    {");
                    sb.AppendLine("        type patch;");
                    sb.AppendLine("        faces ( (4 5 6 7) (0 1 5 4) (3 7 6 2) );");
                    sb.AppendLine("    }");
                }
                else
                {
                    sb.AppendLine("    freestream");
                    sb.AppendLine("    {");
                    sb.AppendLine("        type patch;");
                    sb.AppendLine("        faces ( (0 1 5 4) (3 7 6 2) (0 3 2 1) (4 5 6 7) );");
                    sb.AppendLine("    }");
                }
            }
            else
            {
                sb.AppendLine("    background");
                sb.AppendLine("    {");
                sb.AppendLine("        type patch;");
                sb.AppendLine("        faces");
                sb.AppendLine("        (");
                sb.AppendLine("            (0 4 7 3)");
                sb.AppendLine("            (1 2 6 5)");
                sb.AppendLine("            (0 1 5 4)");
                sb.AppendLine("            (3 7 6 2)");
                sb.AppendLine("            (0 3 2 1)");
                sb.AppendLine("            (4 5 6 7)");
                sb.AppendLine("        );");
                sb.AppendLine("    }");
            }

            sb.AppendLine(");");
            sb.AppendLine();
            sb.AppendLine("mergePatchPairs");
            sb.AppendLine("(");
            sb.AppendLine(");");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "system/blockMeshDict", sb.ToString(), options);
        }

        private static void WriteZeroFields(
            string caseDir,
            OpenFoamOptions options,
            bool isExternal,
            SymmetryAxis symAxis,
            IReadOnlyList<string> patchNames,
            string inletName,
            string outletPrefix,
            Vector3 inletDirection,
            float inletAreaM2)
        {
            float speed = MathF.Max(options.InletVelocity, 1e-4f);
            if (inletAreaM2 > 1e-12f)
            {
                string cond = (options.InletCondition ?? "Velocity").Trim();
                if (cond.Equals("VolumetricFlowRate", StringComparison.OrdinalIgnoreCase) && options.InletVolumetricFlowRate > 0f)
                {
                    speed = options.InletVolumetricFlowRate / inletAreaM2;
                }
                else if (cond.Equals("MassFlowRate", StringComparison.OrdinalIgnoreCase) && options.InletMassFlowRate > 0f)
                {
                    speed = options.InletMassFlowRate / (MathF.Max(options.Density, 1e-3f) * inletAreaM2);
                }
            }

            Vector3 uVec = inletDirection * speed;
            string uVecStr = $"({F(uVec.X)} {F(uVec.Y)} {F(uVec.Z)})";

            WritePressureField(caseDir, options, isExternal, symAxis, patchNames, inletName, outletPrefix);
            WriteVelocityField(caseDir, options, isExternal, symAxis, patchNames, inletName, outletPrefix, uVecStr);

            string turb = (options.TurbulenceModel ?? "kOmegaSST").Trim();
            if (turb.Equals("laminar", StringComparison.OrdinalIgnoreCase))
                return;

            double dh = options.HydraulicDiameter > 0f
                ? options.HydraulicDiameter
                : (inletAreaM2 > 1e-12f ? Math.Sqrt(4.0 * inletAreaM2 / Math.PI) : Math.Max(options.ReferenceLength, 0.01));
            double intensity = Math.Clamp(options.TurbulenceIntensity, 0.001, 0.5);
            double kVal = Math.Max(1.5 * Math.Pow(speed * intensity, 2.0), 1e-8);
            double lt = Math.Max(0.07 * dh, 1e-5);
            const double cMu = 0.09;
            double omegaVal = Math.Sqrt(kVal) / (Math.Pow(cMu, 0.25) * lt);
            double epsilonVal = Math.Pow(cMu, 0.75) * Math.Pow(kVal, 1.5) / lt;
            double nuTildaVal = Math.Sqrt(1.5) * speed * intensity * lt;

            if (turb.Equals("SpalartAllmaras", StringComparison.OrdinalIgnoreCase))
            {
                WriteScalarTurbulenceField(
                    caseDir, options, "0/nuTilda", "nuTilda", "[0 2 -1 0 0 0 0]",
                    nuTildaVal, "fixedValue", $"uniform {F(0.0)}",
                    isExternal, symAxis, patchNames, inletName, outletPrefix);
            }
            else if (turb.Equals("kEpsilon", StringComparison.OrdinalIgnoreCase))
            {
                WriteScalarTurbulenceField(
                    caseDir, options, "0/k", "k", "[0 2 -2 0 0 0 0]",
                    kVal, "kqRWallFunction", $"uniform {F(kVal)}",
                    isExternal, symAxis, patchNames, inletName, outletPrefix);
                WriteScalarTurbulenceField(
                    caseDir, options, "0/epsilon", "epsilon", "[0 2 -3 0 0 0 0]",
                    epsilonVal, "epsilonWallFunction", $"uniform {F(epsilonVal)}",
                    isExternal, symAxis, patchNames, inletName, outletPrefix);
            }
            else
            {
                WriteScalarTurbulenceField(
                    caseDir, options, "0/k", "k", "[0 2 -2 0 0 0 0]",
                    kVal, "kqRWallFunction", $"uniform {F(kVal)}",
                    isExternal, symAxis, patchNames, inletName, outletPrefix);
                WriteScalarTurbulenceField(
                    caseDir, options, "0/omega", "omega", "[0 0 -1 0 0 0 0]",
                    omegaVal, "omegaWallFunction", $"uniform {F(omegaVal)}",
                    isExternal, symAxis, patchNames, inletName, outletPrefix);
            }

            WriteNutField(caseDir, options, turb, isExternal, symAxis, patchNames, inletName, outletPrefix);
        }

        private static void WritePressureField(
            string caseDir,
            OpenFoamOptions options,
            bool isExternal,
            SymmetryAxis symAxis,
            IReadOnlyList<string> patchNames,
            string inletName,
            string outletPrefix)
        {
            string pOut = F(options.OutletPressure);
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "volScalarField", "0", "p");
            sb.AppendLine("dimensions      [0 2 -2 0 0 0 0];");
            sb.AppendLine();
            sb.AppendLine($"internalField   uniform {pOut};");
            sb.AppendLine();
            sb.AppendLine("boundaryField");
            sb.AppendLine("{");

            foreach (string patch in patchNames)
            {
                sb.AppendLine($"    {patch}");
                sb.AppendLine("    {");
                if (patch.Equals(inletName, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        type            zeroGradient;");
                }
                else if (patch.StartsWith(outletPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        type            fixedValue;");
                    sb.AppendLine($"        value           uniform {pOut};");
                }
                else
                {
                    sb.AppendLine("        type            zeroGradient;");
                }
                sb.AppendLine("    }");
            }

            if (isExternal)
            {
                AppendExternalOuterBoundary(sb, "freestream", "zeroGradient", symAxis, "symmetry");
            }

            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "0/p", sb.ToString(), options);
        }

        private static void WriteVelocityField(
            string caseDir,
            OpenFoamOptions options,
            bool isExternal,
            SymmetryAxis symAxis,
            IReadOnlyList<string> patchNames,
            string inletName,
            string outletPrefix,
            string uVecStr)
        {
            string cond = (options.InletCondition ?? "Velocity").Trim();
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "volVectorField", "0", "U");
            sb.AppendLine("dimensions      [0 1 -1 0 0 0 0];");
            sb.AppendLine();
            sb.AppendLine($"internalField   uniform {uVecStr};");
            sb.AppendLine();
            sb.AppendLine("boundaryField");
            sb.AppendLine("{");

            foreach (string patch in patchNames)
            {
                sb.AppendLine($"    {patch}");
                sb.AppendLine("    {");
                if (patch.Equals(inletName, StringComparison.OrdinalIgnoreCase))
                {
                    if (cond.Equals("VolumetricFlowRate", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine("        type            flowRateInletVelocity;");
                        sb.AppendLine($"        volumetricFlowRate constant {F(options.InletVolumetricFlowRate)};");
                        sb.AppendLine("        value           uniform (0 0 0);");
                    }
                    else if (cond.Equals("MassFlowRate", StringComparison.OrdinalIgnoreCase))
                    {
                        sb.AppendLine("        type            flowRateInletVelocity;");
                        sb.AppendLine($"        massFlowRate    constant {F(options.InletMassFlowRate)};");
                        sb.AppendLine($"        rhoInlet        {F(options.Density)};");
                        sb.AppendLine("        value           uniform (0 0 0);");
                    }
                    else
                    {
                        sb.AppendLine("        type            fixedValue;");
                        sb.AppendLine($"        value           uniform {uVecStr};");
                    }
                }
                else if (patch.StartsWith(outletPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        type            inletOutlet;");
                    sb.AppendLine("        inletValue      uniform (0 0 0);");
                    sb.AppendLine("        value           uniform (0 0 0);");
                }
                else
                {
                    sb.AppendLine("        type            noSlip;");
                }
                sb.AppendLine("    }");
            }

            if (isExternal)
            {
                AppendExternalOuterBoundary(sb, "freestream", "slip", symAxis, "symmetry");
            }

            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "0/U", sb.ToString(), options);
        }

        private static void WriteScalarTurbulenceField(
            string caseDir,
            OpenFoamOptions options,
            string relPath,
            string fieldObject,
            string dimensions,
            double internalVal,
            string wallType,
            string wallValue,
            bool isExternal,
            SymmetryAxis symAxis,
            IReadOnlyList<string> patchNames,
            string inletName,
            string outletPrefix)
        {
            string valStr = F(internalVal);
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "volScalarField", "0", fieldObject);
            sb.AppendLine($"dimensions      {dimensions};");
            sb.AppendLine();
            sb.AppendLine($"internalField   uniform {valStr};");
            sb.AppendLine();
            sb.AppendLine("boundaryField");
            sb.AppendLine("{");

            foreach (string patch in patchNames)
            {
                sb.AppendLine($"    {patch}");
                sb.AppendLine("    {");
                if (patch.Equals(inletName, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        type            fixedValue;");
                    sb.AppendLine($"        value           uniform {valStr};");
                }
                else if (patch.StartsWith(outletPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        type            inletOutlet;");
                    sb.AppendLine($"        inletValue      uniform {valStr};");
                    sb.AppendLine($"        value           uniform {valStr};");
                }
                else
                {
                    sb.AppendLine($"        type            {wallType};");
                    sb.AppendLine($"        value           {wallValue};");
                }
                sb.AppendLine("    }");
            }

            if (isExternal)
            {
                AppendExternalOuterBoundary(sb, "freestream", "slip", symAxis, "symmetry");
            }

            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, relPath, sb.ToString(), options);
        }

        private static void WriteNutField(
            string caseDir,
            OpenFoamOptions options,
            string turbulenceModel,
            bool isExternal,
            SymmetryAxis symAxis,
            IReadOnlyList<string> patchNames,
            string inletName,
            string outletPrefix)
        {
            string wallType = turbulenceModel.Equals("SpalartAllmaras", StringComparison.OrdinalIgnoreCase)
                ? "nutUSpaldingWallFunction"
                : "nutkWallFunction";

            var sb = new StringBuilder();
            AppendFoamHeader(sb, "volScalarField", "0", "nut");
            sb.AppendLine("dimensions      [0 2 -1 0 0 0 0];");
            sb.AppendLine();
            sb.AppendLine("internalField   uniform 0;");
            sb.AppendLine();
            sb.AppendLine("boundaryField");
            sb.AppendLine("{");

            foreach (string patch in patchNames)
            {
                sb.AppendLine($"    {patch}");
                sb.AppendLine("    {");
                if (patch.Equals(inletName, StringComparison.OrdinalIgnoreCase) ||
                    patch.StartsWith(outletPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    sb.AppendLine("        type            calculated;");
                    sb.AppendLine("        value           uniform 0;");
                }
                else
                {
                    sb.AppendLine($"        type            {wallType};");
                    sb.AppendLine("        value           uniform 0;");
                }
                sb.AppendLine("    }");
            }

            if (isExternal)
            {
                AppendExternalOuterBoundary(sb, "freestream", "calculated;\n        value           uniform 0", symAxis, "symmetry");
            }

            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "0/nut", sb.ToString(), options);
        }

        private static void AppendExternalOuterBoundary(
            StringBuilder sb,
            string freestreamPatch,
            string freestreamBody,
            SymmetryAxis symAxis,
            string symmetryPatch)
        {
            sb.AppendLine($"    {freestreamPatch}");
            sb.AppendLine("    {");
            sb.AppendLine($"        type            {freestreamBody};");
            sb.AppendLine("    }");

            if (symAxis != SymmetryAxis.None)
            {
                sb.AppendLine($"    {symmetryPatch}");
                sb.AppendLine("    {");
                sb.AppendLine("        type            symmetry;");
                sb.AppendLine("    }");
            }
        }

        private static void WriteConstantDictionaries(string caseDir, OpenFoamOptions options)
        {
            double nu = options.DynamicViscosity / Math.Max(options.Density, 1e-6f);
            var tp = new StringBuilder();
            AppendFoamHeader(tp, "dictionary", "constant", "transportProperties");
            tp.AppendLine("transportModel  Newtonian;");
            tp.AppendLine();
            tp.AppendLine($"nu              [0 2 -1 0 0 0 0] {F(nu)};");
            AppendFoamFooter(tp);
            WriteDictionaryWithOverrides(caseDir, "constant/transportProperties", tp.ToString(), options);

            string turbModel = (options.TurbulenceModel ?? "kOmegaSST").Trim();
            var turb = new StringBuilder();
            AppendFoamHeader(turb, "dictionary", "constant", "turbulenceProperties");
            if (turbModel.Equals("laminar", StringComparison.OrdinalIgnoreCase))
            {
                turb.AppendLine("simulationType  laminar;");
            }
            else
            {
                turb.AppendLine("simulationType  RAS;");
                turb.AppendLine();
                turb.AppendLine("RAS");
                turb.AppendLine("{");
                turb.AppendLine($"    RASModel        {turbModel};");
                turb.AppendLine("    turbulence      on;");
                turb.AppendLine("    printCoeffs     on;");
                turb.AppendLine("}");
            }
            AppendFoamFooter(turb);
            WriteDictionaryWithOverrides(caseDir, "constant/turbulenceProperties", turb.ToString(), options);
        }

        private static void WriteSystemDictionaries(
            string caseDir,
            MeshRequest request,
            OpenFoamOptions options,
            bool isExternal,
            SymmetryAxis symAxis,
            IReadOnlyDictionary<string, IReadOnlyList<StlTriangle>> scaledPatches,
            string inletName,
            string outletPrefix,
            IReadOnlyList<string> outletPatches,
            string wallName,
            Vector3 locationInMesh,
            float cellSizeM,
            float referenceAreaM2,
            Vector3 inletDirection)
        {
            WriteControlDict(caseDir, request, options, isExternal, inletName, outletPatches, wallName, referenceAreaM2, inletDirection);
            WriteFvSchemes(caseDir, options);
            WriteFvSolution(caseDir, options);
            WriteSurfaceFeatureExtractDict(caseDir, options);
            WriteSnappyHexMeshDict(caseDir, options, isExternal, symAxis, scaledPatches, inletName, outletPrefix, wallName, locationInMesh);
            WriteCfMeshDict(caseDir, options, wallName, cellSizeM);
            WriteDecomposeParDict(caseDir, options, Math.Max(1, request.Config.MpiCores));
        }

        private static void WriteControlDict(
            string caseDir,
            MeshRequest request,
            OpenFoamOptions options,
            bool isExternal,
            string inletName,
            IReadOnlyList<string> outletPatches,
            string wallName,
            float referenceAreaM2,
            Vector3 inletDirection)
        {
            int configuredEndTime = Math.Max(1, options.EndTime);
            int endTime = request.MaxSolverIterations.HasValue && request.MaxSolverIterations.Value > 0
                ? Math.Min(configuredEndTime, request.MaxSolverIterations.Value)
                : configuredEndTime;
            int writeInterval = Math.Clamp(options.WriteInterval, 1, endTime);

            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "controlDict");
            sb.AppendLine($"application     {options.Solver};");
            sb.AppendLine("startFrom       startTime;");
            sb.AppendLine("startTime       0;");
            sb.AppendLine("stopAt          endTime;");
            sb.AppendLine($"endTime         {endTime};");
            sb.AppendLine("deltaT          1;");
            sb.AppendLine("writeControl    timeStep;");
            sb.AppendLine($"writeInterval   {writeInterval};");
            sb.AppendLine("purgeWrite      2;");
            sb.AppendLine("writeFormat     ascii;");
            sb.AppendLine("writePrecision  8;");
            sb.AppendLine("writeCompression off;");
            sb.AppendLine("timeFormat      general;");
            sb.AppendLine("timePrecision   6;");
            sb.AppendLine("runTimeModifiable true;");
            sb.AppendLine();
            sb.AppendLine("functions");
            sb.AppendLine("{");

            var monitoredPatches = new List<string> { inletName };
            foreach (string outlet in outletPatches)
            {
                if (!monitoredPatches.Contains(outlet, StringComparer.OrdinalIgnoreCase))
                    monitoredPatches.Add(outlet);
            }

            foreach (string patch in monitoredPatches)
            {
                sb.AppendLine($"    pressure_{patch}");
                sb.AppendLine("    {");
                sb.AppendLine("        type            surfaceFieldValue;");
                sb.AppendLine("        libs            (fieldFunctionObjects);");
                sb.AppendLine("        writeControl    timeStep;");
                sb.AppendLine("        writeInterval   1;");
                sb.AppendLine("        executeControl  timeStep;");
                sb.AppendLine("        executeInterval 1;");
                sb.AppendLine("        writeFields     false;");
                sb.AppendLine("        log             true;");
                sb.AppendLine("        operation       areaAverage;");
                sb.AppendLine("        regionType      patch;");
                sb.AppendLine($"        name            {patch};");
                sb.AppendLine("        fields          (p U);");
                sb.AppendLine("    }");
                sb.AppendLine();
                sb.AppendLine($"    flow_{patch}");
                sb.AppendLine("    {");
                sb.AppendLine("        type            surfaceFieldValue;");
                sb.AppendLine("        libs            (fieldFunctionObjects);");
                sb.AppendLine("        writeControl    timeStep;");
                sb.AppendLine("        writeInterval   1;");
                sb.AppendLine("        executeControl  timeStep;");
                sb.AppendLine("        executeInterval 1;");
                sb.AppendLine("        writeFields     false;");
                sb.AppendLine("        log             true;");
                sb.AppendLine("        operation       sum;");
                sb.AppendLine("        regionType      patch;");
                sb.AppendLine($"        name            {patch};");
                sb.AppendLine("        fields          (phi);");
                sb.AppendLine("    }");
                sb.AppendLine();
            }

            sb.AppendLine("    yPlus");
            sb.AppendLine("    {");
            sb.AppendLine("        type            yPlus;");
            sb.AppendLine("        libs            (fieldFunctionObjects);");
            sb.AppendLine("        writeControl    writeTime;");
            sb.AppendLine("        executeControl  writeTime;");
            sb.AppendLine("        writeFields     false;");
            sb.AppendLine("        log             true;");
            sb.AppendLine("    }");

            if (isExternal)
            {
                Vector3 dragDir = Vector3.Normalize(inletDirection);
                Vector3 liftDir = Vector3.Normalize(new Vector3(-dragDir.Z, 0f, dragDir.X));
                if (liftDir.LengthSquared() < 1e-6f)
                    liftDir = Vector3.UnitZ;

                sb.AppendLine();
                sb.AppendLine("    forceCoeffs");
                sb.AppendLine("    {");
                sb.AppendLine("        type            forceCoeffs;");
                sb.AppendLine("        libs            (forces);");
                sb.AppendLine("        writeControl    timeStep;");
                sb.AppendLine("        writeInterval   1;");
                sb.AppendLine("        executeControl  timeStep;");
                sb.AppendLine("        executeInterval 1;");
                sb.AppendLine("        log             true;");
                sb.AppendLine($"        patches         ({wallName});");
                sb.AppendLine("        rho             rhoInf;");
                sb.AppendLine($"        rhoInf          {F(options.Density)};");
                sb.AppendLine("        CofR            (0 0 0);");
                sb.AppendLine($"        liftDir         ({F(liftDir.X)} {F(liftDir.Y)} {F(liftDir.Z)});");
                sb.AppendLine($"        dragDir         ({F(dragDir.X)} {F(dragDir.Y)} {F(dragDir.Z)});");
                sb.AppendLine("        pitchAxis       (0 1 0);");
                sb.AppendLine($"        magUInf         {F(MathF.Max(options.InletVelocity, 1e-4f))};");
                sb.AppendLine($"        lRef            {F(MathF.Max(options.ReferenceLength, 1e-4f))};");
                sb.AppendLine($"        Aref            {F(MathF.Max(referenceAreaM2, 1e-6f))};");
                sb.AppendLine("    }");
            }

            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "system/controlDict", sb.ToString(), options);
        }

        private static void WriteFvSchemes(string caseDir, OpenFoamOptions options)
        {
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "fvSchemes");
            sb.AppendLine("ddtSchemes");
            sb.AppendLine("{");
            sb.AppendLine("    default         steadyState;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("gradSchemes");
            sb.AppendLine("{");
            sb.AppendLine("    default         Gauss linear;");
            sb.AppendLine("    grad(U)         cellLimited Gauss linear 1;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("divSchemes");
            sb.AppendLine("{");
            sb.AppendLine("    default         none;");
            sb.AppendLine("    div(phi,U)      bounded Gauss linearUpwindV grad(U);");
            sb.AppendLine("    div(phi,k)      bounded Gauss upwind;");
            sb.AppendLine("    div(phi,omega)  bounded Gauss upwind;");
            sb.AppendLine("    div(phi,epsilon) bounded Gauss upwind;");
            sb.AppendLine("    div(phi,nuTilda) bounded Gauss upwind;");
            sb.AppendLine("    div((nuEff*dev2(T(grad(U))))) Gauss linear;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("laplacianSchemes");
            sb.AppendLine("{");
            sb.AppendLine("    default         Gauss linear corrected;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("interpolationSchemes");
            sb.AppendLine("{");
            sb.AppendLine("    default         linear;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("snGradSchemes");
            sb.AppendLine("{");
            sb.AppendLine("    default         corrected;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("wallDist");
            sb.AppendLine("{");
            sb.AppendLine("    method          meshWave;");
            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "system/fvSchemes", sb.ToString(), options);
        }

        private static void WriteFvSolution(string caseDir, OpenFoamOptions options)
        {
            string res = F(MathF.Max(options.ResidualTarget, 1e-8f));
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "fvSolution");
            sb.AppendLine("solvers");
            sb.AppendLine("{");
            sb.AppendLine("    p");
            sb.AppendLine("    {");
            sb.AppendLine("        solver          GAMG;");
            sb.AppendLine("        smoother        GaussSeidel;");
            sb.AppendLine("        tolerance       1e-7;");
            sb.AppendLine("        relTol          0.01;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    Phi");
            sb.AppendLine("    {");
            sb.AppendLine("        $p;");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    \"(U|k|omega|epsilon|nuTilda)\"");
            sb.AppendLine("    {");
            sb.AppendLine("        solver          smoothSolver;");
            sb.AppendLine("        smoother        symGaussSeidel;");
            sb.AppendLine("        tolerance       1e-7;");
            sb.AppendLine("        relTol          0.1;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("SIMPLE");
            sb.AppendLine("{");
            sb.AppendLine("    nNonOrthogonalCorrectors 1;");
            sb.AppendLine("    consistent      yes;");
            sb.AppendLine();
            sb.AppendLine("    residualControl");
            sb.AppendLine("    {");
            sb.AppendLine($"        p               {res};");
            sb.AppendLine($"        U               {res};");
            sb.AppendLine($"        \"(k|omega|epsilon|nuTilda)\" {res};");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("relaxationFactors");
            sb.AppendLine("{");
            sb.AppendLine("    equations");
            sb.AppendLine("    {");
            sb.AppendLine("        U               0.7;");
            sb.AppendLine("        k               0.7;");
            sb.AppendLine("        omega           0.7;");
            sb.AppendLine("        epsilon         0.7;");
            sb.AppendLine("        nuTilda         0.7;");
            sb.AppendLine("    }");
            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "system/fvSolution", sb.ToString(), options);
        }

        private static void WriteSurfaceFeatureExtractDict(string caseDir, OpenFoamOptions options)
        {
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "surfaceFeatureExtractDict");
            sb.AppendLine("geometry.stl");
            sb.AppendLine("{");
            sb.AppendLine("    extractionMethod    extractFromSurface;");
            sb.AppendLine();
            sb.AppendLine("    extractFromSurfaceCoeffs");
            sb.AppendLine("    {");
            sb.AppendLine($"        includedAngle   {F(180.0f - Math.Clamp(options.FeatureAngleDeg, 5f, 175f))};");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    writeObj            yes;");
            sb.AppendLine("}");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "system/surfaceFeatureExtractDict", sb.ToString(), options);
        }

        private static void WriteSnappyHexMeshDict(
            string caseDir,
            OpenFoamOptions options,
            bool isExternal,
            SymmetryAxis symAxis,
            IReadOnlyDictionary<string, IReadOnlyList<StlTriangle>> scaledPatches,
            string inletName,
            string outletPrefix,
            string wallName,
            Vector3 locationInMesh)
        {
            int levelMin = Math.Max(0, options.SurfaceRefinementLevelMin);
            int levelMax = Math.Max(levelMin, options.SurfaceRefinementLevelMax);
            string addLayersBool = options.AddLayers ? "true" : "false";
            string relSizesBool = options.RelativeLayerSizes ? "true" : "false";

            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "snappyHexMeshDict");
            sb.AppendLine("castellatedMesh true;");
            sb.AppendLine("snap            true;");
            sb.AppendLine($"addLayers       {addLayersBool};");
            sb.AppendLine();
            sb.AppendLine("geometry");
            sb.AppendLine("{");
            sb.AppendLine("    geometry.stl");
            sb.AppendLine("    {");
            sb.AppendLine("        type triSurfaceMesh;");
            sb.AppendLine("        name geometry;");
            sb.AppendLine("        regions");
            sb.AppendLine("        {");
            foreach (string patch in scaledPatches.Keys)
            {
                sb.AppendLine($"            {patch} {{ name {patch}; }}");
            }
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine("};");
            sb.AppendLine();
            sb.AppendLine("castellatedMeshControls");
            sb.AppendLine("{");
            sb.AppendLine($"    maxLocalCells   {Math.Max(1000, options.MaxLocalCells)};");
            sb.AppendLine($"    maxGlobalCells  {Math.Max(1000, options.MaxGlobalCells)};");
            sb.AppendLine("    minRefinementCells 10;");
            sb.AppendLine("    maxLoadUnbalance 0.10;");
            sb.AppendLine("    nCellsBetweenLevels 3;");
            sb.AppendLine();
            sb.AppendLine("    features");
            sb.AppendLine("    (");
            sb.AppendLine("        {");
            sb.AppendLine("            file \"geometry.eMesh\";");
            sb.AppendLine($"            level {levelMin};");
            sb.AppendLine("        }");
            sb.AppendLine("    );");
            sb.AppendLine();
            sb.AppendLine("    refinementSurfaces");
            sb.AppendLine("    {");
            sb.AppendLine("        geometry");
            sb.AppendLine("        {");
            sb.AppendLine($"            level ({levelMin} {levelMax});");
            sb.AppendLine("            regions");
            sb.AppendLine("            {");
            foreach (string patch in scaledPatches.Keys)
            {
                string patchType = (!isExternal &&
                    (patch.Equals(inletName, StringComparison.OrdinalIgnoreCase) ||
                     patch.StartsWith(outletPrefix, StringComparison.OrdinalIgnoreCase)))
                    ? "patch"
                    : "wall";
                sb.AppendLine($"                {patch}");
                sb.AppendLine("                {");
                sb.AppendLine($"                    level ({levelMin} {levelMax});");
                sb.AppendLine("                    patchInfo");
                sb.AppendLine("                    {");
                sb.AppendLine($"                        type {patchType};");
                sb.AppendLine("                    }");
                sb.AppendLine("                }");
            }
            sb.AppendLine("            }");
            sb.AppendLine("            patchInfo");
            sb.AppendLine("            {");
            sb.AppendLine("                type wall;");
            sb.AppendLine("            }");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine($"    resolveFeatureAngle {F(options.FeatureAngleDeg)};");
            sb.AppendLine("    refinementRegions {}");
            sb.AppendLine($"    locationInMesh ({F(locationInMesh.X)} {F(locationInMesh.Y)} {F(locationInMesh.Z)});");
            sb.AppendLine("    allowFreeStandingZoneFaces true;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("snapControls");
            sb.AppendLine("{");
            sb.AppendLine("    nSmoothPatch    3;");
            sb.AppendLine("    tolerance       2.0;");
            sb.AppendLine("    nSolveIter      30;");
            sb.AppendLine("    nRelaxIter      5;");
            sb.AppendLine("    nFeatureSnapIter 10;");
            sb.AppendLine("    implicitFeatureSnap false;");
            sb.AppendLine("    explicitFeatureSnap true;");
            sb.AppendLine("    multiRegionFeatureSnap false;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("addLayersControls");
            sb.AppendLine("{");
            sb.AppendLine($"    relativeSizes   {relSizesBool};");
            sb.AppendLine("    layers");
            sb.AppendLine("    {");
            sb.AppendLine($"        \"{wallName}\"");
            sb.AppendLine("        {");
            sb.AppendLine($"            nSurfaceLayers {Math.Max(1, options.BoundaryLayerLayers)};");
            sb.AppendLine("        }");
            sb.AppendLine("    }");
            sb.AppendLine($"    expansionRatio  {F(MathF.Max(options.BoundaryLayerExpansionRatio, 1.01f))};");
            if (options.RelativeLayerSizes)
                sb.AppendLine($"    finalLayerThickness {F(options.FinalLayerThickness)};");
            else
                sb.AppendLine($"    firstLayerThickness {F(options.FirstLayerThickness)};");
            sb.AppendLine($"    minThickness    {F(options.MinLayerThickness)};");
            sb.AppendLine("    nGrow           0;");
            sb.AppendLine("    featureAngle    60;");
            sb.AppendLine("    nRelaxIter      5;");
            sb.AppendLine("    nSmoothSurfaceNormals 1;");
            sb.AppendLine("    nSmoothNormals  3;");
            sb.AppendLine("    nSmoothThickness 10;");
            sb.AppendLine("    maxFaceThicknessRatio 0.5;");
            sb.AppendLine("    maxThicknessToMedialRatio 0.3;");
            sb.AppendLine("    minMedialAxisAngle 90;");
            sb.AppendLine("    nBufferCellsNoExtrude 0;");
            sb.AppendLine("    nLayerIter      50;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("meshQualityControls");
            sb.AppendLine("{");
            sb.AppendLine($"    maxNonOrtho     {F(options.MaxNonOrthogonality)};");
            sb.AppendLine($"    maxBoundarySkewness {F(options.MaxSkewness)};");
            sb.AppendLine($"    maxInternalSkewness {F(options.MaxSkewness)};");
            sb.AppendLine("    maxConcave      80;");
            sb.AppendLine("    minVol          1e-13;");
            sb.AppendLine("    minTetQuality   -1e30;");
            sb.AppendLine("    minArea         -1;");
            sb.AppendLine("    minTwist        0.02;");
            sb.AppendLine("    minDeterminant  0.001;");
            sb.AppendLine("    minFaceWeight   0.05;");
            sb.AppendLine("    minVolRatio     0.01;");
            sb.AppendLine("    minTriangleTwist -1;");
            sb.AppendLine("    nSmoothScale    4;");
            sb.AppendLine("    errorReduction  0.75;");
            sb.AppendLine("}");
            sb.AppendLine();
            sb.AppendLine("mergeTolerance 1e-6;");
            AppendFoamFooter(sb);

            _ = symAxis; // Symmetry handled in blockMeshDict & STL clipping for External3D
            WriteDictionaryWithOverrides(caseDir, "system/snappyHexMeshDict", sb.ToString(), options);
        }

        private static void WriteCfMeshDict(string caseDir, OpenFoamOptions options, string wallName, float cellSizeM)
        {
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "meshDict");
            sb.AppendLine("surfaceFile     \"constant/triSurface/geometry.stl\";");
            sb.AppendLine();
            sb.AppendLine($"maxCellSize     {F(cellSizeM)};");
            sb.AppendLine($"boundaryCellSize {F(cellSizeM * 0.5f)};");
            if (options.AddLayers)
            {
                float firstLayerM = options.RelativeLayerSizes
                    ? cellSizeM * 0.1f
                    : MathF.Max(options.FirstLayerThickness, 1e-6f);
                sb.AppendLine();
                sb.AppendLine("boundaryLayers");
                sb.AppendLine("{");
                sb.AppendLine("    patchBoundaryLayers");
                sb.AppendLine("    {");
                sb.AppendLine($"        {wallName}");
                sb.AppendLine("        {");
                sb.AppendLine($"            nLayers         {Math.Max(1, options.BoundaryLayerLayers)};");
                sb.AppendLine($"            thicknessRatio  {F(MathF.Max(options.BoundaryLayerExpansionRatio, 1.01f))};");
                sb.AppendLine($"            maxFirstLayerThickness {F(firstLayerM)};");
                sb.AppendLine("        }");
                sb.AppendLine("    }");
                sb.AppendLine("}");
            }
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "system/meshDict", sb.ToString(), options);
        }

        private static void WriteDecomposeParDict(string caseDir, OpenFoamOptions options, int mpiCores)
        {
            var sb = new StringBuilder();
            AppendFoamHeader(sb, "dictionary", "system", "decomposeParDict");
            sb.AppendLine($"numberOfSubdomains {Math.Max(1, mpiCores)};");
            sb.AppendLine();
            sb.AppendLine("method          scotch;");
            AppendFoamFooter(sb);

            WriteDictionaryWithOverrides(caseDir, "system/decomposeParDict", sb.ToString(), options);
        }

        /// <summary>
        /// Writes a dictionary file after checking <see cref="OpenFoamOptions.CustomTemplateDirectory"/>
        /// and applying <see cref="OpenFoamOptions.CustomDictionaryOverrides"/>.
        /// </summary>
        internal static void WriteDictionaryWithOverrides(
            string caseDir,
            string relativePath,
            string generatedContent,
            OpenFoamOptions options)
        {
            string normalizedRel = relativePath.Replace('\\', '/').TrimStart('/');
            string content = generatedContent;

            if (!string.IsNullOrWhiteSpace(options.CustomTemplateDirectory))
            {
                string templateFile = Path.Combine(
                    options.CustomTemplateDirectory,
                    normalizedRel.Replace('/', Path.DirectorySeparatorChar));
                if (File.Exists(templateFile))
                {
                    content = File.ReadAllText(templateFile);
                }
            }

            if (options.CustomDictionaryOverrides != null && options.CustomDictionaryOverrides.Count > 0)
            {
                foreach (KeyValuePair<string, Dictionary<string, string>> entry in options.CustomDictionaryOverrides)
                {
                    string keyPath = entry.Key.Replace('\\', '/').TrimStart('/');
                    if (!keyPath.Equals(normalizedRel, StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (entry.Value != null)
                    {
                        content = ApplyOverrides(content, entry.Value);
                    }
                }
            }

            string targetPath = Path.Combine(caseDir, normalizedRel.Replace('/', Path.DirectorySeparatorChar));
            string? parent = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(parent))
                Directory.CreateDirectory(parent);

            File.WriteAllText(targetPath, content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static string ApplyOverrides(string content, IReadOnlyDictionary<string, string> overrides)
        {
            string updated = content;
            foreach (KeyValuePair<string, string> kvp in overrides)
            {
                string key = (kvp.Key ?? string.Empty).Trim();
                if (key.Length == 0) continue;
                string value = (kvp.Value ?? string.Empty).Trim().TrimEnd(';');

                var keyPattern = new Regex(
                    $@"(?m)^([ \t]*){Regex.Escape(key)}\b[ \t]+[^;\r\n]*;",
                    RegexOptions.CultureInvariant);

                if (keyPattern.IsMatch(updated))
                {
                    updated = keyPattern.Replace(updated, $"$1{key} {value};", count: 1);
                }
                else
                {
                    const string footerMarker = "// ************************************************************************* //";
                    string line = $"{key} {value};";
                    int footerIdx = updated.LastIndexOf(footerMarker, StringComparison.Ordinal);
                    if (footerIdx >= 0)
                    {
                        updated = updated.Insert(footerIdx, line + Environment.NewLine + Environment.NewLine);
                    }
                    else
                    {
                        updated = updated.TrimEnd() + Environment.NewLine + line + Environment.NewLine;
                    }
                }
            }
            return updated;
        }

        private static void AppendFoamHeader(StringBuilder sb, string foamClass, string location, string objectName)
        {
            sb.AppendLine("/*--------------------------------*- C++ -*----------------------------------*\\");
            sb.AppendLine("| =========                 |                                                 |");
            sb.AppendLine("| \\\\      /  F ield         | OpenFOAM: The Open Source CFD Toolbox           |");
            sb.AppendLine("|  \\\\    /   O peration     | Version:  v2506                                 |");
            sb.AppendLine("|   \\\\  /    A nd           | Website:  www.openfoam.com                      |");
            sb.AppendLine("|    \\\\/     M anipulation  |                                                 |");
            sb.AppendLine("\\*---------------------------------------------------------------------------*/");
            sb.AppendLine("FoamFile");
            sb.AppendLine("{");
            sb.AppendLine("    version     2.0;");
            sb.AppendLine("    format      ascii;");
            sb.AppendLine($"    class       {foamClass};");
            sb.AppendLine($"    location    \"{location}\";");
            sb.AppendLine($"    object      {objectName};");
            sb.AppendLine("}");
            sb.AppendLine("// * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * * //");
            sb.AppendLine();
        }

        private static void AppendFoamFooter(StringBuilder sb)
        {
            sb.AppendLine();
            sb.AppendLine("// ************************************************************************* //");
        }
    }
}
