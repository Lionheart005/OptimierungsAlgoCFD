using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace FluidForge.Core
{
    /// <summary>
    /// Axis-aligned bounding box of a triangle mesh (mm). <see cref="Size"/> provides
    /// overall extents: flow is along +X, so spanwise extent is <c>Size.Y</c>.
    /// </summary>
    public readonly struct MeshBounds
    {
        private readonly bool _hasPoints;

        public MeshBounds(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
            _hasPoints = true;
        }

        /// <summary>Minimum corner.</summary>
        public Vector3 Min { get; }

        /// <summary>Maximum corner.</summary>
        public Vector3 Max { get; }

        /// <summary>
        /// True when the mesh had zero points (distinct from a degenerate zero-size mesh
        /// where <see cref="Min"/> and <see cref="Max"/> happen to coincide).
        /// </summary>
        public bool IsEmpty => !_hasPoints;

        /// <summary>Edge lengths of the bounding box.</summary>
        public Vector3 Size => _hasPoints ? Max - Min : Vector3.Zero;

        /// <summary>Center point of the bounding box.</summary>
        public Vector3 Center => _hasPoints ? (Min + Max) * 0.5f : Vector3.Zero;
    }

    /// <summary>
    /// Single-pass geometric measurements of a triangle mesh.
    /// </summary>
    public readonly struct MeshMeasurement
    {
        public MeshMeasurement(
            float signedVolume,
            float surfaceArea,
            MeshBounds bounds,
            int triangleCount,
            float frontalProjectedArea = 0f)
        {
            SignedVolume = signedVolume;
            SurfaceArea = surfaceArea;
            Bounds = bounds;
            TriangleCount = triangleCount;
            FrontalProjectedArea = frontalProjectedArea;
        }

        /// <summary>
        /// Signed volume (mm³). <b>Negative means triangle normals point inward.</b>
        /// An inverted shell will cause Gmsh to fail or produce invalid meshes.
        /// </summary>
        public float SignedVolume { get; }

        /// <summary>Unsigned volume (mm³) — the value reported as a metric.</summary>
        public float Volume => Math.Abs(SignedVolume);

        /// <summary>Surface area (mm²).</summary>
        public float SurfaceArea { get; }

        /// <summary>Axis-aligned bounding box.</summary>
        public MeshBounds Bounds { get; }

        /// <summary>Triangle count.</summary>
        public int TriangleCount { get; }

        /// <summary>
        /// Bounding-box frontal area (mm²) perpendicular to the X axis (<c>Size.Y × Size.Z</c>).
        /// Simple rectangular approximation rather than the true projected silhouette.
        /// </summary>
        public float FrontalBoxArea => Bounds.Size.Y * Bounds.Size.Z;

        /// <summary>
        /// True projected frontal area / silhouette (mm²) in the YZ plane perpendicular to
        /// flow direction (X axis).
        /// <para>
        /// Unlike <see cref="FrontalBoxArea"/>, this computes the actual 2D shadow area
        /// of the mesh so swept struts, wing-body combinations, or tandem parts get their
        /// true cross-sectional area rather than the enclosing rectangle.
        /// </para>
        /// </summary>
        public float FrontalProjectedArea { get; }

        /// <summary>
        /// Returns either the true projected frontal area (<see cref="FrontalProjectedArea"/>, default)
        /// or the bounding-box rectangle (<see cref="FrontalBoxArea"/>).
        /// </summary>
        public float GetFrontalArea(bool useProjected = true)
            => useProjected ? FrontalProjectedArea : FrontalBoxArea;
    }

    /// <summary>
    /// Breakdown of an SU2 mesh file by volume and wall-surface element classes.
    /// </summary>
    public sealed record Su2CellBreakdown(
        int TotalVolumeCells,
        int Tetrahedra,
        int Hexahedra,
        int Prisms,
        int Pyramids,
        int SurfaceTriangles,
        int SurfaceQuads,
        int SurfaceLines);

    /// <summary>
    /// Measurements of a triangle mesh: volume, surface area, bounding box, and projected frontal area.
    ///
    /// <para>
    /// Operates purely on <see cref="StlTriangle"/> so <c>Core/</c> has no dependency on any
    /// geometry kernel. All accumulations use <c>double</c> precision before converting to <c>float</c>.
    /// </para>
    /// </summary>
    public static class MeshMetrics
    {
        /// <summary>
        /// Computes all mesh metrics in a single pass.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="triangles"/> is <c>null</c>.</exception>
        public static MeshMeasurement Measure(IReadOnlyList<StlTriangle> triangles)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));

            if (triangles.Count == 0)
                return new MeshMeasurement(0f, 0f, default, 0);

            double volume = 0.0;
            double area = 0.0;

            Vector3 min = triangles[0].A;
            Vector3 max = triangles[0].A;

            foreach (StlTriangle triangle in triangles)
            {
                volume += TetrahedronVolume(triangle);
                area += TriangleArea(triangle);

                Grow(ref min, ref max, triangle.A);
                Grow(ref min, ref max, triangle.B);
                Grow(ref min, ref max, triangle.C);
            }

            MeshBounds bounds = new MeshBounds(min, max);
            float frontalProjectedArea = ComputeFrontalProjectedArea(triangles, bounds);

            return new MeshMeasurement(
                (float)volume, (float)area, bounds, triangles.Count, frontalProjectedArea);
        }

        /// <summary>Surface area (mm²) as sum of triangle areas.</summary>
        public static float SurfaceArea(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).SurfaceArea;

        /// <summary>
        /// Unsigned volume (mm³). Assumes a <b>closed</b> watertight shell.
        /// </summary>
        public static float Volume(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).Volume;

        /// <summary>
        /// Signed volume (mm³) — negative if triangle normals point inward.
        /// </summary>
        public static float SignedVolume(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).SignedVolume;

        /// <summary>Bounding box of the mesh.</summary>
        public static MeshBounds Bounds(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).Bounds;

        /// <summary>
        /// True projected frontal area (mm²) in the YZ plane perpendicular to the X axis.
        /// </summary>
        public static float FrontalProjectedArea(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).FrontalProjectedArea;

        /// <summary>
        /// Reads the number of volume elements (cells) from the header of an SU2 mesh file (.su2)
        /// by locating the first <c>NELEM=</c> line. Returns 0 if missing or unreadable.
        /// </summary>
        public static int ReadSu2CellCount(string su2MeshPath)
        {
            if (string.IsNullOrWhiteSpace(su2MeshPath) || !File.Exists(su2MeshPath))
                return 0;

            try
            {
                foreach (string line in File.ReadLines(su2MeshPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("NELEM", StringComparison.OrdinalIgnoreCase))
                    {
                        int eqIdx = trimmed.IndexOf('=');
                        if (eqIdx >= 0 && trimmed.Substring(0, eqIdx).Trim().Equals("NELEM", StringComparison.OrdinalIgnoreCase))
                        {
                            string valuePart = trimmed.Substring(eqIdx + 1).Trim();
                            if (int.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                            {
                                return count;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Return 0 defensively on I/O or lock errors
                return 0;
            }

            return 0;
        }

        /// <summary>
        /// Parses <c>NELEM=</c> (volume/2D element types) and <c>NMARK=</c> (all wall markers except
        /// <c>Farfield</c> and <c>Fluid</c>, case-insensitive) from an ASCII <c>.su2</c> file.
        /// </summary>
        public static Su2CellBreakdown? ReadSu2CellBreakdown(string su2MeshPath)
        {
            if (string.IsNullOrWhiteSpace(su2MeshPath) || !File.Exists(su2MeshPath))
                return null;

            try
            {
                using var reader = new StreamReader(su2MeshPath);
                int totalVolumeCells = 0;
                int tetrahedra = 0;
                int hexahedra = 0;
                int prisms = 0;
                int pyramids = 0;
                int surfaceTriangles = 0;
                int surfaceQuads = 0;
                int surfaceLines = 0;
                bool foundNelem = false;

                string? line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.Trim();
                    if (trimmed.Length == 0 || trimmed.StartsWith('%'))
                        continue;

                    if (!foundNelem && TryParseSu2HeaderInt(trimmed, "NELEM", out int nelemCount))
                    {
                        foundNelem = true;
                        totalVolumeCells = Math.Max(0, nelemCount);

                        for (int i = 0; i < nelemCount; i++)
                        {
                            string? elemLine = ReadNextNonCommentLine(reader);
                            if (elemLine == null) break;
                            if (TryReadFirstIntToken(elemLine, out int vtkType))
                            {
                                switch (vtkType)
                                {
                                    case 10: tetrahedra++; break;
                                    case 12: hexahedra++; break;
                                    case 13: prisms++; break;
                                    case 14: pyramids++; break;
                                    case 5:
                                    case 9:
                                        // 2D volume cells (triangles/quads) are included in TotalVolumeCells.
                                        break;
                                }
                            }
                        }
                        continue;
                    }

                    if (TryParseSu2HeaderInt(trimmed, "NMARK", out int markerCount))
                    {
                        for (int m = 0; m < markerCount; m++)
                        {
                            string? tagLine = ReadNextNonCommentLine(reader);
                            if (tagLine == null) break;

                            string markerTag = TryParseSu2HeaderValue(tagLine, "MARKER_TAG", out string? parsedTag)
                                ? parsedTag
                                : string.Empty;

                            string? elemsHeader = ReadNextNonCommentLine(reader);
                            if (elemsHeader == null) break;

                            if (!TryParseSu2HeaderInt(elemsHeader, "MARKER_ELEMS", out int markerElems))
                                break;

                            bool skipMarker = markerTag.Equals("Farfield", StringComparison.OrdinalIgnoreCase)
                                              || markerTag.Equals("Fluid", StringComparison.OrdinalIgnoreCase);

                            for (int e = 0; e < markerElems; e++)
                            {
                                string? elemLine = ReadNextNonCommentLine(reader);
                                if (elemLine == null) break;
                                if (skipMarker) continue;

                                if (TryReadFirstIntToken(elemLine, out int vtkType))
                                {
                                    switch (vtkType)
                                    {
                                        case 5: surfaceTriangles++; break;
                                        case 9: surfaceQuads++; break;
                                        case 3: surfaceLines++; break;
                                    }
                                }
                            }
                        }
                        break;
                    }
                }

                return foundNelem
                    ? new Su2CellBreakdown(
                        totalVolumeCells,
                        tetrahedra,
                        hexahedra,
                        prisms,
                        pyramids,
                        surfaceTriangles,
                        surfaceQuads,
                        surfaceLines)
                    : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads total and element-class cell counts from an SU2 mesh file and writes them into
        /// <paramref name="target"/> (<c>MeshCells</c>, <c>MeshCells_Tri</c>, <c>MeshCells_Quad</c>,
        /// <c>MeshCells_Prism</c>, <c>MeshCells_Hexa</c>, <c>MeshCells_Tet</c>, <c>MeshCells_Pyramid</c>).
        /// </summary>
        public static void PopulateSu2CellMetrics(
            IDictionary<string, float> target,
            string su2MeshPath,
            bool overwriteExisting = true)
        {
            ArgumentNullException.ThrowIfNull(target);
            if (string.IsNullOrWhiteSpace(su2MeshPath) || !File.Exists(su2MeshPath))
                return;

            int cellCount = ReadSu2CellCount(su2MeshPath);
            if (cellCount > 0 && (overwriteExisting || !target.ContainsKey("MeshCells")))
            {
                target["MeshCells"] = cellCount;
            }

            Su2CellBreakdown? breakdown = ReadSu2CellBreakdown(su2MeshPath);
            if (breakdown == null)
                return;

            void SetMetric(string key, float value)
            {
                if (overwriteExisting || !target.ContainsKey(key))
                    target[key] = value;
            }

            SetMetric("MeshCells_Tri", breakdown.SurfaceTriangles);
            SetMetric("MeshCells_Quad", breakdown.SurfaceQuads);
            SetMetric("MeshCells_Prism", breakdown.Prisms);
            SetMetric("MeshCells_Hexa", breakdown.Hexahedra);
            SetMetric("MeshCells_Tet", breakdown.Tetrahedra);
            SetMetric("MeshCells_Pyramid", breakdown.Pyramids);
        }

        private static string? ReadNextNonCommentLine(StreamReader reader)
        {
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith('%'))
                    return trimmed;
            }
            return null;
        }

        private static bool TryParseSu2HeaderValue(string trimmedLine, string key, out string value)
        {
            value = string.Empty;
            int eqIdx = trimmedLine.IndexOf('=');
            if (eqIdx <= 0) return false;

            if (!trimmedLine.Substring(0, eqIdx).Trim().Equals(key, StringComparison.OrdinalIgnoreCase))
                return false;

            value = trimmedLine.Substring(eqIdx + 1).Trim();
            return true;
        }

        private static bool TryParseSu2HeaderInt(string trimmedLine, string key, out int value)
        {
            value = 0;
            return TryParseSu2HeaderValue(trimmedLine, key, out string raw)
                   && int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        private static bool TryReadFirstIntToken(string trimmedLine, out int value)
        {
            int sep = trimmedLine.IndexOfAny(new[] { ' ', '\t' });
            string token = sep > 0 ? trimmedLine.Substring(0, sep) : trimmedLine;
            return int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
        }

        /// <summary>
        /// Signed volume of the tetrahedron Origin–A–B–C.
        /// Summed over all triangles of a closed shell, exterior contributions cancel out
        /// by the divergence theorem.
        /// </summary>
        private static double TetrahedronVolume(StlTriangle triangle)
        {
            Vector3 a = triangle.A, b = triangle.B, c = triangle.C;

            double x = (double)b.Y * c.Z - (double)b.Z * c.Y;
            double y = (double)b.Z * c.X - (double)b.X * c.Z;
            double z = (double)b.X * c.Y - (double)b.Y * c.X;

            return (a.X * x + a.Y * y + a.Z * z) / 6.0;
        }

        /// <summary>Area of a triangle: half the magnitude of the cross product of two edge vectors.</summary>
        private static double TriangleArea(StlTriangle triangle)
        {
            Vector3 u = triangle.B - triangle.A;
            Vector3 v = triangle.C - triangle.A;

            double x = (double)u.Y * v.Z - (double)u.Z * v.Y;
            double y = (double)u.Z * v.X - (double)u.X * v.Z;
            double z = (double)u.X * v.Y - (double)u.Y * v.X;

            return Math.Sqrt(x * x + y * y + z * z) * 0.5;
        }

        private static void Grow(ref Vector3 min, ref Vector3 max, Vector3 point)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }

        /// <summary>
        /// Computes the true projected frontal area (silhouette in the YZ plane perpendicular to X)
        /// via a fast, gap-free 2D scanline rasterization on a 512×512 bitgrid.
        /// <para>
        /// Using a bitgrid instead of summing normal projections (<c>0.5 * Σ |n_x| A_i</c>) avoids
        /// double-counting occluded surfaces on non-convex bodies or tandem components, while fitting
        /// in 32 KB of L1 cache.
        /// </para>
        /// </summary>
        private static float ComputeFrontalProjectedArea(IReadOnlyList<StlTriangle> triangles, MeshBounds bounds)
        {
            if (triangles.Count == 0 || bounds.IsEmpty)
                return 0f;

            double minY = bounds.Min.Y;
            double minZ = bounds.Min.Z;
            double sizeY = (double)bounds.Max.Y - minY;
            double sizeZ = (double)bounds.Max.Z - minZ;

            if (sizeY <= 1e-9 || sizeZ <= 1e-9)
                return 0f;

            const int GridResolution = 512;
            const int WordsPerRow = GridResolution / 64; // 8 ulongs per row
            const double Eps = 1e-9;

            ulong[] grid = new ulong[GridResolution * WordsPerRow];

            double scaleY = GridResolution / sizeY;
            double scaleZ = GridResolution / sizeZ;

            foreach (StlTriangle tri in triangles)
            {
                double u0 = (tri.A.Y - minY) * scaleY;
                double v0 = (tri.A.Z - minZ) * scaleZ;
                double u1 = (tri.B.Y - minY) * scaleY;
                double v1 = (tri.B.Z - minZ) * scaleZ;
                double u2 = (tri.C.Y - minY) * scaleY;
                double v2 = (tri.C.Z - minZ) * scaleZ;

                // Skip degenerate 2D projections (e.g., faces parallel to the X axis)
                double cross2D = (u1 - u0) * (v2 - v0) - (v1 - v0) * (u2 - u0);
                if (Math.Abs(cross2D) <= 1e-12)
                    continue;

                double vMin = Math.Min(v0, Math.Min(v1, v2));
                double vMax = Math.Max(v0, Math.Max(v1, v2));

                int ivStart = Math.Max(0, (int)Math.Ceiling(vMin - 0.5 - Eps));
                int ivEnd = Math.Min(GridResolution - 1, (int)Math.Floor(vMax - 0.5 + Eps));

                if (ivStart > ivEnd)
                    continue;

                for (int iv = ivStart; iv <= ivEnd; iv++)
                {
                    double vc = iv + 0.5;
                    double uLeft = double.PositiveInfinity;
                    double uRight = double.NegativeInfinity;

                    IntersectEdge(u0, v0, u1, v1, vc, Eps, ref uLeft, ref uRight);
                    IntersectEdge(u1, v1, u2, v2, vc, Eps, ref uLeft, ref uRight);
                    IntersectEdge(u2, v2, u0, v0, vc, Eps, ref uLeft, ref uRight);

                    if (uLeft > uRight)
                        continue;

                    int iuStart = Math.Max(0, (int)Math.Ceiling(uLeft - 0.5 - Eps));
                    int iuEnd = Math.Min(GridResolution - 1, (int)Math.Floor(uRight - 0.5 + Eps));

                    if (iuStart > iuEnd)
                        continue;

                    int rowOffset = iv * WordsPerRow;
                    int startWord = iuStart >> 6;
                    int endWord = iuEnd >> 6;
                    ulong startMask = ulong.MaxValue << (iuStart & 63);
                    ulong endMask = ulong.MaxValue >> (63 - (iuEnd & 63));

                    if (startWord == endWord)
                    {
                        grid[rowOffset + startWord] |= (startMask & endMask);
                    }
                    else
                    {
                        grid[rowOffset + startWord] |= startMask;
                        for (int w = startWord + 1; w < endWord; w++)
                            grid[rowOffset + w] = ulong.MaxValue;
                        grid[rowOffset + endWord] |= endMask;
                    }
                }
            }

            long coveredCells = 0;
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] != 0UL)
                    coveredCells += BitOperations.PopCount(grid[i]);
            }

            double cellArea = (sizeY * sizeZ) / ((double)GridResolution * GridResolution);
            return (float)(coveredCells * cellArea);
        }

        private static void IntersectEdge(
            double ua, double va,
            double ub, double vb,
            double vc, double eps,
            ref double uLeft, ref double uRight)
        {
            double vMin = Math.Min(va, vb);
            double vMax = Math.Max(va, vb);
            if (vc < vMin - eps || vc > vMax + eps)
                return;

            double dv = vb - va;
            if (Math.Abs(dv) <= eps)
            {
                double uMin = Math.Min(ua, ub);
                double uMax = Math.Max(ua, ub);
                if (uMin < uLeft) uLeft = uMin;
                if (uMax > uRight) uRight = uMax;
                return;
            }

            double t = Math.Clamp((vc - va) / dv, 0.0, 1.0);
            double uInt = ua + t * (ub - ua);
            if (uInt < uLeft) uLeft = uInt;
            if (uInt > uRight) uRight = uInt;
        }
    }
}
