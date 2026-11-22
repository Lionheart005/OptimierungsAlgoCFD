using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Remeshed wall surface (Medit file from step 1 of the boundary-layer workflow) as a
    /// triangle mesh. Pure computational object without Gmsh dependencies so fold checks and smoothing are unit-testable.
    /// </summary>
    public sealed class WallSurface
    {
        /// <summary>Vertex coordinates, interleaved x, y, z per vertex (length 3*n).</summary>
        public double[] Points { get; }

        /// <summary>Zero-based vertex indices, three per triangle (length 3*m).</summary>
        public int[] Triangles { get; }

        public int VertexCount => Points.Length / 3;
        public int TriangleCount => Triangles.Length / 3;

        /// <summary>Unit normals of the triangles (length 3*m), derived from winding order.</summary>
        public double[] TriangleNormals { get; }

        /// <summary>Triangle areas (length m).</summary>
        public double[] TriangleAreas { get; }

        /// <summary>Raw vertex normals: average of unit normals of adjacent triangles (length 3*n).</summary>
        public double[] RawNormals { get; }

        /// <summary>Mean edge length.</summary>
        public double MeanEdgeLength { get; }

        /// <summary>Maximum edge length.</summary>
        public double MaxEdgeLength { get; }

        /// <summary>Neighbor vertices (CSR): neighbors of vertex i are stored in <c>NeighborList[NeighborStart[i] .. NeighborStart[i+1])</c>.</summary>
        public int[] NeighborStart { get; }
        public int[] NeighborList { get; }

        public WallSurface(double[] points, int[] triangles)
        {
            if (points.Length % 3 != 0) throw new ArgumentException("Coordinates must be a multiple of 3.", nameof(points));
            if (triangles.Length % 3 != 0) throw new ArgumentException("Triangles must be a multiple of 3.", nameof(triangles));

            Points = points;
            Triangles = triangles;
            int n = VertexCount, m = TriangleCount;
            foreach (int t in triangles)
            {
                if (t < 0 || t >= n) throw new ArgumentException("Triangle references a non-existent vertex.", nameof(triangles));
            }

            TriangleNormals = new double[3 * m];
            TriangleAreas = new double[m];
            RawNormals = new double[3 * n];
            double edgeSum = 0, edgeMax = 0;
            int edgeCount = 0;

            for (int t = 0; t < m; t++)
            {
                int a = triangles[3 * t], b = triangles[3 * t + 1], c = triangles[3 * t + 2];
                (double ex, double ey, double ez) = Sub(points, b, a);
                (double fx, double fy, double fz) = Sub(points, c, a);
                double nx = ey * fz - ez * fy, ny = ez * fx - ex * fz, nz = ex * fy - ey * fx;
                double len = Math.Sqrt(nx * nx + ny * ny + nz * nz);
                TriangleAreas[t] = 0.5 * len;
                if (len > 1e-300) { nx /= len; ny /= len; nz /= len; }
                TriangleNormals[3 * t] = nx; TriangleNormals[3 * t + 1] = ny; TriangleNormals[3 * t + 2] = nz;
                foreach (int v in new[] { a, b, c })
                {
                    RawNormals[3 * v] += nx; RawNormals[3 * v + 1] += ny; RawNormals[3 * v + 2] += nz;
                }
                foreach ((int p, int q) in new[] { (a, b), (b, c), (c, a) })
                {
                    (double dx, double dy, double dz) = Sub(points, p, q);
                    double e = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                    edgeSum += e; edgeCount++;
                    if (e > edgeMax) edgeMax = e;
                }
            }

            for (int v = 0; v < n; v++) Normalize(RawNormals, v);

            MeanEdgeLength = edgeCount > 0 ? edgeSum / edgeCount : 1.0;
            MaxEdgeLength = edgeMax;

            // Adjacency as CSR (each edge in both directions; interior edges count twice, uniformly).
            var degree = new int[n + 1];
            for (int t = 0; t < m; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    degree[triangles[3 * t + k] + 1] += 2;
                }
            }
            for (int v = 0; v < n; v++) degree[v + 1] += degree[v];
            NeighborStart = degree;
            NeighborList = new int[NeighborStart[n]];
            var fill = new int[n];
            for (int t = 0; t < m; t++)
            {
                for (int k = 0; k < 3; k++)
                {
                    int v = triangles[3 * t + k];
                    int w1 = triangles[3 * t + (k + 1) % 3];
                    int w2 = triangles[3 * t + (k + 2) % 3];
                    NeighborList[NeighborStart[v] + fill[v]++] = w1;
                    NeighborList[NeighborStart[v] + fill[v]++] = w2;
                }
            }
        }

        private static (double, double, double) Sub(double[] p, int a, int b)
            => (p[3 * a] - p[3 * b], p[3 * a + 1] - p[3 * b + 1], p[3 * a + 2] - p[3 * b + 2]);

        internal static void Normalize(double[] v, int i)
        {
            double x = v[3 * i], y = v[3 * i + 1], z = v[3 * i + 2];
            double len = Math.Sqrt(x * x + y * y + z * z);
            if (len < 1e-300) { v[3 * i] = 0; v[3 * i + 1] = 0; v[3 * i + 2] = 1; return; }
            v[3 * i] = x / len; v[3 * i + 1] = y / len; v[3 * i + 2] = z / len;
        }

        /// <summary>
        /// Reads <c>Vertices</c> and <c>Triangles</c> from a Medit file (<c>.mesh</c>, as written by
        /// <c>Wall_&lt;variant&gt;.geo</c>). Other sections are skipped.
        /// </summary>
        public static WallSurface ReadMedit(string path)
        {
            var points = new List<double>();
            var tris = new List<int>();
            using var reader = new StreamReader(path);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string key = line.Trim();
                if (key.Length == 0 || key.StartsWith("#", StringComparison.Ordinal)) continue;

                if (key.StartsWith("MeshVersionFormatted", StringComparison.OrdinalIgnoreCase)) continue;
                if (key.Equals("Dimension", StringComparison.OrdinalIgnoreCase)) { reader.ReadLine(); continue; }
                if (key.Equals("End", StringComparison.OrdinalIgnoreCase)) break;

                bool isVertices = key.Equals("Vertices", StringComparison.OrdinalIgnoreCase);
                bool isTriangles = key.Equals("Triangles", StringComparison.OrdinalIgnoreCase);

                // Every subsequent section: keyword, count, then count lines.
                string? countLine = reader.ReadLine();
                if (countLine == null || !int.TryParse(countLine.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                    throw new InvalidDataException($"{Path.GetFileName(path)}: missing count after '{key}'.");

                for (int i = 0; i < count; i++)
                {
                    string? row = reader.ReadLine();
                    if (row == null) throw new InvalidDataException($"{Path.GetFileName(path)}: section '{key}' is incomplete.");
                    if (!isVertices && !isTriangles) continue;

                    string[] tok = row.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                    if (isVertices)
                    {
                        if (tok.Length < 3) throw new InvalidDataException($"{Path.GetFileName(path)}: invalid vertex line '{row}'.");
                        for (int k = 0; k < 3; k++) points.Add(double.Parse(tok[k], CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        if (tok.Length < 3) throw new InvalidDataException($"{Path.GetFileName(path)}: invalid triangle line '{row}'.");
                        for (int k = 0; k < 3; k++) tris.Add(int.Parse(tok[k], CultureInfo.InvariantCulture) - 1); // Medit is 1-based
                    }
                }
            }

            if (points.Count == 0 || tris.Count == 0)
                throw new InvalidDataException($"{Path.GetFileName(path)}: no vertices or triangles found.");

            return new WallSurface(points.ToArray(), tris.ToArray());
        }
    }

    /// <summary>
    /// One step of the fallback ladder for the boundary-layer wall field.
    /// Layer count and first cell height remain unchanged at EVERY step; only direction and local thickness vary.
    /// </summary>
    /// <param name="Label">Display label for logs.</param>
    /// <param name="Stage">1 = A (smoothed directions only), 2 = B (plus local thickness), 3 = C (fallback mesh).</param>
    /// <param name="SmoothingRadiusFactor">Smoothing radius as a multiple of the target thickness.</param>
    /// <param name="MaxTiltDeg">Maximum angular deviation from the raw vertex normal.</param>
    /// <param name="LocalThickness">Reduce thickness locally where the layer folds or clearance is tight.</param>
    /// <param name="Safety">Fraction of critical thickness used locally.</param>
    /// <param name="MinScale">Lower bound for the local thickness scale factor.</param>
    /// <param name="GlobalScale">Additional global scale factor (fallback mesh only).</param>
    public sealed record WallFieldSettings(
        string Label,
        int Stage,
        double SmoothingRadiusFactor,
        double MaxTiltDeg,
        bool LocalThickness,
        double Safety,
        double MinScale,
        double GlobalScale);

    /// <summary>Result of a wall-field computation: directions * thickness scale per vertex plus log/CSV diagnostics.</summary>
    public sealed class WallFieldResult
    {
        /// <summary>Vector per vertex (length 3*n): unit direction * thickness scale. Gmsh scales layer thickness accordingly.</summary>
        public double[] Vectors { get; init; } = Array.Empty<double>();

        /// <summary>Thickness scale factor per vertex (length n), including global scale.</summary>
        public double[] Scale { get; init; } = Array.Empty<double>();

        public double MedianTiltDeg { get; init; }
        public double MaxTiltDeg { get; init; }

        /// <summary>Smallest thickness scale factor (1 = unthinned everywhere).</summary>
        public double MinScale { get; init; } = 1.0;

        /// <summary>Fraction of wall surface area where thickness is below 90% of the target thickness.</summary>
        public double ThinnedShare { get; init; }

        /// <summary>Triangles predicted to fold at full thickness despite smoothed directions.</summary>
        public int FoldedTriangles { get; init; }

        /// <summary>Vertices with an opposing wall closer than twice the layer thickness (when checked).</summary>
        public int PinchedNodes { get; init; }

        public WallFieldSettings Settings { get; init; } = BoundaryLayerWallField.Ladder[0];
    }

    /// <summary>
    /// Computes a vector field on the wall for boundary-layer extrusion (Gmsh <c>Extrude ... Using View</c>):
    /// smoothed extrusion directions instead of raw vertex normals, with locally reduced thickness if needed.
    ///
    /// <para>
    /// Background: Gmsh extrudes along raw normals. In concave fillets of radius R they converge and the layer
    /// folds once thickness ≈ R, causing core tetrahedral meshing to fail. Smoothed directions preserve full
    /// thickness, while local thickness handles tight clearances. Layer count and first cell height stay constant.
    /// </para>
    /// </summary>
    public static class BoundaryLayerWallField
    {
        /// <summary>Thickness multiplier below which an opposing wall is treated as a pinch (two layers plus clearance).</summary>
        public const double PinchClearance = 2.2;

        /// <summary>Maximum number of smoothing steps.</summary>
        private const int MaxSmoothingSteps = 60;

        /// <summary>
        /// Fallback ladder: A (full thickness), B (locally thinned, progressively more conservative),
        /// C (fallback mesh with additional global scaling). Evaluated step by step until Gmsh succeeds.
        /// </summary>
        public static readonly IReadOnlyList<WallFieldSettings> Ladder = new[]
        {
            new WallFieldSettings("A: smoothed directions, full thickness",    1, 0.8, 30.0, false, 1.00, 1.0, 1.0),
            new WallFieldSettings("B1: locally thinned (safety 0.80)",         2, 0.8, 30.0, true,  0.80, 0.3, 1.0),
            new WallFieldSettings("B2: stronger smoothing, safety 0.60",       2, 1.6, 30.0, true,  0.60, 0.3, 1.0),
            new WallFieldSettings("B3: safety 0.45",                           2, 1.6, 30.0, true,  0.45, 0.3, 1.0),
            new WallFieldSettings("C1: fallback mesh, half thickness",         3, 1.6, 30.0, true,  0.45, 0.3, 0.5),
            new WallFieldSettings("C2: fallback mesh, quarter thickness",      3, 1.6, 30.0, true,  0.45, 0.3, 0.25),
        };

        /// <summary>Computes the field for target <paramref name="thickness"/> (in geometry units).</summary>
        public static WallFieldResult Build(WallSurface wall, double thickness, WallFieldSettings settings)
        {
            if (thickness <= 0) throw new ArgumentOutOfRangeException(nameof(thickness), "Layer thickness must be positive.");

            int n = wall.VertexCount;
            double[] raw = wall.RawNormals;
            double[] dir = SmoothDirections(wall, thickness, settings.SmoothingRadiusFactor, settings.MaxTiltDeg);

            // Prediction: how many triangles still fold at full thickness with these directions?
            double[] triCrit = CriticalTriangleThickness(wall, dir);
            int folded = 0;
            for (int t = 0; t < triCrit.Length; t++) if (triCrit[t] < thickness) folded++;

            var scale = new double[n];
            Array.Fill(scale, 1.0);
            int pinched = 0;

            if (settings.LocalThickness)
            {
                var req = new double[n];
                Array.Fill(req, 1.0);

                var nodeCrit = new double[n];
                Array.Fill(nodeCrit, double.PositiveInfinity);
                for (int t = 0; t < triCrit.Length; t++)
                {
                    for (int k = 0; k < 3; k++)
                    {
                        int v = wall.Triangles[3 * t + k];
                        if (triCrit[t] < nodeCrit[v]) nodeCrit[v] = triCrit[t];
                    }
                }

                double[] gap = PinchDistances(wall, dir, PinchClearance * thickness);
                for (int v = 0; v < n; v++)
                {
                    double r = 1.0;
                    if (!double.IsInfinity(nodeCrit[v])) r = Math.Min(r, settings.Safety * nodeCrit[v] / thickness);
                    if (!double.IsInfinity(gap[v]))
                    {
                        pinched++;
                        r = Math.Min(r, gap[v] / (PinchClearance * thickness));
                    }
                    req[v] = Math.Max(settings.MinScale, Math.Min(1.0, r));
                }

                scale = SmoothScale(wall, req, 15);

                // Re-check with the thinned field itself: thickness was scaled from the critical thickness of the UNTHINNED
                // directions; varying scale per vertex alters the layer geometry. Wherever it still folds, tighten the
                // scale factor until the layer is numerically fold-free (or hits MinScale).
                for (int round = 0; round < 8; round++)
                {
                    var scaledDir = new double[3 * n];
                    for (int v = 0; v < n; v++)
                    {
                        scaledDir[3 * v] = dir[3 * v] * scale[v];
                        scaledDir[3 * v + 1] = dir[3 * v + 1] * scale[v];
                        scaledDir[3 * v + 2] = dir[3 * v + 2] * scale[v];
                    }

                    double[] crit = CriticalTriangleThickness(wall, scaledDir);
                    bool tightened = false;
                    for (int t = 0; t < crit.Length; t++)
                    {
                        if (crit[t] >= thickness) continue;

                        double ratio = 0.9 * crit[t] / thickness;
                        for (int k = 0; k < 3; k++)
                        {
                            int v = wall.Triangles[3 * t + k];
                            double wanted = Math.Max(settings.MinScale, scale[v] * ratio);
                            if (wanted < req[v] - 1e-12) { req[v] = wanted; tightened = true; }
                        }
                    }

                    if (!tightened) break;
                    scale = SmoothScale(wall, req, 15);
                }
            }

            if (settings.GlobalScale != 1.0)
            {
                for (int v = 0; v < n; v++) scale[v] *= settings.GlobalScale;
            }

            // Diagnostics
            var vectors = new double[3 * n];
            var tilt = new double[n];
            double minScale = 1.0;
            for (int v = 0; v < n; v++)
            {
                vectors[3 * v] = dir[3 * v] * scale[v];
                vectors[3 * v + 1] = dir[3 * v + 1] * scale[v];
                vectors[3 * v + 2] = dir[3 * v + 2] * scale[v];
                double c = dir[3 * v] * raw[3 * v] + dir[3 * v + 1] * raw[3 * v + 1] + dir[3 * v + 2] * raw[3 * v + 2];
                tilt[v] = Math.Acos(Math.Clamp(c, -1.0, 1.0)) * 180.0 / Math.PI;
                if (scale[v] < minScale) minScale = scale[v];
            }

            double thinnedArea = 0, totalArea = 0;
            for (int t = 0; t < wall.TriangleCount; t++)
            {
                double s = (scale[wall.Triangles[3 * t]] + scale[wall.Triangles[3 * t + 1]] + scale[wall.Triangles[3 * t + 2]]) / 3.0;
                totalArea += wall.TriangleAreas[t];
                if (s < 0.9) thinnedArea += wall.TriangleAreas[t];
            }

            var sortedTilt = (double[])tilt.Clone();
            Array.Sort(sortedTilt);

            return new WallFieldResult
            {
                Vectors = vectors,
                Scale = scale,
                MedianTiltDeg = sortedTilt.Length > 0 ? sortedTilt[sortedTilt.Length / 2] : 0.0,
                MaxTiltDeg = sortedTilt.Length > 0 ? sortedTilt[^1] : 0.0,
                MinScale = minScale,
                ThinnedShare = totalArea > 0 ? thinnedArea / totalArea : 0.0,
                FoldedTriangles = folded,
                PinchedNodes = pinched,
                Settings = settings,
            };
        }

        /// <summary>
        /// Smooths vertex normals by repeated neighbor averaging. The step count follows from the target
        /// radius (diffusion reach ≈ √steps * edgeLength), keeping smoothing reach consistent across mesh
        /// factors. Directions deviate at most <paramref name="maxTiltDeg"/> from the raw normal.
        /// </summary>
        public static double[] SmoothDirections(WallSurface wall, double thickness, double radiusFactor, double maxTiltDeg)
        {
            int n = wall.VertexCount;
            double[] raw = wall.RawNormals;
            double[] cur = (double[])raw.Clone();
            double[] next = new double[3 * n];

            int steps = (int)Math.Round(Math.Pow(radiusFactor * thickness / Math.Max(wall.MeanEdgeLength, 1e-12), 2.0));
            steps = Math.Clamp(steps, 3, MaxSmoothingSteps);

            double cosMax = Math.Cos(maxTiltDeg * Math.PI / 180.0);
            double sinMax = Math.Sin(maxTiltDeg * Math.PI / 180.0);

            for (int it = 0; it < steps; it++)
            {
                for (int v = 0; v < n; v++)
                {
                    double x = cur[3 * v], y = cur[3 * v + 1], z = cur[3 * v + 2];
                    for (int k = wall.NeighborStart[v]; k < wall.NeighborStart[v + 1]; k++)
                    {
                        int w = wall.NeighborList[k];
                        x += cur[3 * w]; y += cur[3 * w + 1]; z += cur[3 * w + 2];
                    }
                    next[3 * v] = x; next[3 * v + 1] = y; next[3 * v + 2] = z;
                    WallSurface.Normalize(next, v);

                    // At most maxTiltDeg from raw normal to avoid overly skewed prisms
                    double c = next[3 * v] * raw[3 * v] + next[3 * v + 1] * raw[3 * v + 1] + next[3 * v + 2] * raw[3 * v + 2];
                    if (c < cosMax)
                    {
                        double px = next[3 * v] - c * raw[3 * v];
                        double py = next[3 * v + 1] - c * raw[3 * v + 1];
                        double pz = next[3 * v + 2] - c * raw[3 * v + 2];
                        double pl = Math.Sqrt(px * px + py * py + pz * pz);
                        if (pl > 1e-12)
                        {
                            next[3 * v] = cosMax * raw[3 * v] + sinMax * px / pl;
                            next[3 * v + 1] = cosMax * raw[3 * v + 1] + sinMax * py / pl;
                            next[3 * v + 2] = cosMax * raw[3 * v + 2] + sinMax * pz / pl;
                        }
                        else
                        {
                            next[3 * v] = raw[3 * v]; next[3 * v + 1] = raw[3 * v + 1]; next[3 * v + 2] = raw[3 * v + 2];
                        }
                    }
                }
                (cur, next) = (next, cur);
            }

            return cur;
        }

        /// <summary>
        /// Smallest positive thickness per triangle at which the triangle offset by <c>T * dir</c>
        /// collapses to 10% of its area or flips. Exact analytic root: the offset triangle normal is
        /// quadratic in T, and its projection onto the original normal yields the smallest positive root
        /// (infinity if the triangle never folds).
        /// </summary>
        public static double[] CriticalTriangleThickness(WallSurface wall, double[] dir)
        {
            int m = wall.TriangleCount;
            var result = new double[m];
            double[] p = wall.Points;

            for (int t = 0; t < m; t++)
            {
                int a = wall.Triangles[3 * t], b = wall.Triangles[3 * t + 1], c = wall.Triangles[3 * t + 2];
                double[] e1 = { p[3 * b] - p[3 * a], p[3 * b + 1] - p[3 * a + 1], p[3 * b + 2] - p[3 * a + 2] };
                double[] e2 = { p[3 * c] - p[3 * a], p[3 * c + 1] - p[3 * a + 1], p[3 * c + 2] - p[3 * a + 2] };
                double[] d1 = { dir[3 * b] - dir[3 * a], dir[3 * b + 1] - dir[3 * a + 1], dir[3 * b + 2] - dir[3 * a + 2] };
                double[] d2 = { dir[3 * c] - dir[3 * a], dir[3 * c + 1] - dir[3 * a + 1], dir[3 * c + 2] - dir[3 * a + 2] };

                double[] n0 = Cross(e1, e2);
                double[] x1 = Cross(e1, d2);
                double[] x2 = Cross(d1, e2);
                double[] n1 = { x1[0] + x2[0], x1[1] + x2[1], x1[2] + x2[2] };
                double[] n2 = Cross(d1, d2);

                double qa = Dot(n0, n2);        // T²
                double qb = Dot(n0, n1);        // T
                double qc = 0.9 * Dot(n0, n0);  // Remaining: area drops to 10%

                result[t] = SmallestPositiveRoot(qa, qb, qc);
            }

            return result;
        }

        public static double SmallestPositiveRoot(double a, double b, double c)
        {
            // a*T² + b*T + c = 0 with c > 0 (at T = 0 the triangle is intact).
            if (c <= 0) return 0.0;
            if (Math.Abs(a) < 1e-14 * Math.Max(Math.Abs(b), c))
            {
                return b < 0 ? -c / b : double.PositiveInfinity;
            }

            double disc = b * b - 4.0 * a * c;
            if (disc < 0) return double.PositiveInfinity;

            double sq = Math.Sqrt(disc);
            double r1 = (-b - sq) / (2.0 * a);
            double r2 = (-b + sq) / (2.0 * a);
            double best = double.PositiveInfinity;
            if (r1 > 0 && r1 < best) best = r1;
            if (r2 > 0 && r2 < best) best = r2;
            return best;
        }

        /// <summary>
        /// Distance per vertex to the nearest opposing wall along the extrusion direction (pinch gaps, e.g.,
        /// between adjacent body parts). Counts only hits on faces nearly opposing the ray (angle under ~32°);
        /// measures perpendicular distance. Infinity if nothing is within <paramref name="maxDistance"/>.
        /// </summary>
        public static double[] PinchDistances(WallSurface wall, double[] dir, double maxDistance)
        {
            int n = wall.VertexCount, m = wall.TriangleCount;
            var result = new double[n];
            Array.Fill(result, double.PositiveInfinity);
            double[] p = wall.Points;

            // Spatial hash grid over triangle bounds; cell size = ray reach
            double cell = Math.Max(maxDistance, wall.MaxEdgeLength * 2.0);
            var grid = new Dictionary<long, List<int>>();
            for (int t = 0; t < m; t++)
            {
                GetTriangleBox(wall, t, out double x0, out double y0, out double z0, out double x1, out double y1, out double z1);
                ForEachCell(x0, y0, z0, x1, y1, z1, cell, key =>
                {
                    if (!grid.TryGetValue(key, out var list)) grid[key] = list = new List<int>();
                    list.Add(t);
                });
            }

            var stamp = new int[m];
            int stampId = 0;

            for (int v = 0; v < n; v++)
            {
                double ox = p[3 * v], oy = p[3 * v + 1], oz = p[3 * v + 2];
                double dx = dir[3 * v], dy = dir[3 * v + 1], dz = dir[3 * v + 2];
                double ex = ox + dx * maxDistance, ey = oy + dy * maxDistance, ez = oz + dz * maxDistance;

                stampId++;
                double bestGap = double.PositiveInfinity;
                ForEachCell(
                    Math.Min(ox, ex), Math.Min(oy, ey), Math.Min(oz, ez),
                    Math.Max(ox, ex), Math.Max(oy, ey), Math.Max(oz, ez), cell,
                    key =>
                    {
                        if (!grid.TryGetValue(key, out var list)) return;
                        foreach (int t in list)
                        {
                            if (stamp[t] == stampId) continue;
                            stamp[t] = stampId;

                            int a = wall.Triangles[3 * t], b = wall.Triangles[3 * t + 1], c = wall.Triangles[3 * t + 2];
                            if (a == v || b == v || c == v) continue;

                            double cosFacing = dx * wall.TriangleNormals[3 * t] + dy * wall.TriangleNormals[3 * t + 1] + dz * wall.TriangleNormals[3 * t + 2];
                            if (cosFacing > -0.85) continue; // only faces opposing the ray

                            double hit = RayTriangle(p, a, b, c, ox, oy, oz, dx, dy, dz);
                            if (hit <= 1e-9 || hit > maxDistance) continue;

                            double gap = hit * -cosFacing;
                            if (gap < bestGap) bestGap = gap;
                        }
                    });
                result[v] = bestGap;
            }

            return result;
        }

        /// <summary>Möller–Trumbore: distance t along the ray to the triangle, or -1.</summary>
        private static double RayTriangle(double[] p, int a, int b, int c, double ox, double oy, double oz, double dx, double dy, double dz)
        {
            double e1x = p[3 * b] - p[3 * a], e1y = p[3 * b + 1] - p[3 * a + 1], e1z = p[3 * b + 2] - p[3 * a + 2];
            double e2x = p[3 * c] - p[3 * a], e2y = p[3 * c + 1] - p[3 * a + 1], e2z = p[3 * c + 2] - p[3 * a + 2];
            double px = dy * e2z - dz * e2y, py = dz * e2x - dx * e2z, pz = dx * e2y - dy * e2x;
            double det = e1x * px + e1y * py + e1z * pz;
            if (Math.Abs(det) < 1e-14) return -1.0;
            double inv = 1.0 / det;
            double tx = ox - p[3 * a], ty = oy - p[3 * a + 1], tz = oz - p[3 * a + 2];
            double u = (tx * px + ty * py + tz * pz) * inv;
            const double eps = 1e-9; // hits directly on vertex/edge count (rounding tolerance)
            if (u < -eps || u > 1.0 + eps) return -1.0;
            double qx = ty * e1z - tz * e1y, qy = tz * e1x - tx * e1z, qz = tx * e1y - ty * e1x;
            double w = (dx * qx + dy * qy + dz * qz) * inv;
            if (w < -eps || u + w > 1.0 + eps) return -1.0;
            return (e2x * qx + e2y * qy + e2z * qz) * inv;
        }

        private static void GetTriangleBox(WallSurface wall, int t, out double x0, out double y0, out double z0, out double x1, out double y1, out double z1)
        {
            double[] p = wall.Points;
            x0 = y0 = z0 = double.PositiveInfinity;
            x1 = y1 = z1 = double.NegativeInfinity;
            for (int k = 0; k < 3; k++)
            {
                int v = wall.Triangles[3 * t + k];
                x0 = Math.Min(x0, p[3 * v]); x1 = Math.Max(x1, p[3 * v]);
                y0 = Math.Min(y0, p[3 * v + 1]); y1 = Math.Max(y1, p[3 * v + 1]);
                z0 = Math.Min(z0, p[3 * v + 2]); z1 = Math.Max(z1, p[3 * v + 2]);
            }
        }

        private static void ForEachCell(double x0, double y0, double z0, double x1, double y1, double z1, double cell, Action<long> action)
        {
            int ix0 = (int)Math.Floor(x0 / cell), ix1 = (int)Math.Floor(x1 / cell);
            int iy0 = (int)Math.Floor(y0 / cell), iy1 = (int)Math.Floor(y1 / cell);
            int iz0 = (int)Math.Floor(z0 / cell), iz1 = (int)Math.Floor(z1 / cell);
            for (int ix = ix0; ix <= ix1; ix++)
            for (int iy = iy0; iy <= iy1; iy++)
            for (int iz = iz0; iz <= iz1; iz++)
            {
                action(((long)(ix & 0x1FFFFF) << 42) | ((long)(iy & 0x1FFFFF) << 21) | (long)(iz & 0x1FFFFF));
            }
        }

        /// <summary>
        /// Smooths the thickness scale factor without undercutting the requirement: after each step
        /// <c>s = min(target, 0.5 * s + 0.5 * neighborAvg)</c>, creating a smooth transition instead of sharp jumps.
        /// </summary>
        private static double[] SmoothScale(WallSurface wall, double[] required, int steps)
        {
            int n = wall.VertexCount;
            var cur = (double[])required.Clone();
            var next = new double[n];
            for (int it = 0; it < steps; it++)
            {
                for (int v = 0; v < n; v++)
                {
                    int from = wall.NeighborStart[v], to = wall.NeighborStart[v + 1];
                    double avg = cur[v];
                    if (to > from)
                    {
                        double sum = 0;
                        for (int k = from; k < to; k++) sum += cur[wall.NeighborList[k]];
                        avg = sum / (to - from);
                    }
                    next[v] = Math.Min(required[v], 0.5 * cur[v] + 0.5 * avg);
                }
                (cur, next) = (next, cur);
            }
            return cur;
        }

        /// <summary>
        /// Writes the field as a Gmsh <c>.pos</c> file: one <c>VT</c> entry with three vectors per wall triangle.
        /// ALL triangles are included so every extrusion start vertex is covered by the field.
        /// </summary>
        public static void WritePos(string path, WallSurface wall, WallFieldResult field)
        {
            var sb = new StringBuilder(wall.TriangleCount * 200);
            sb.Append("View \"wallfield\" {\n");
            double[] p = wall.Points;
            for (int t = 0; t < wall.TriangleCount; t++)
            {
                sb.Append("VT(");
                for (int k = 0; k < 3; k++)
                {
                    int v = wall.Triangles[3 * t + k];
                    if (k > 0) sb.Append(',');
                    sb.Append(F(p[3 * v])).Append(',').Append(F(p[3 * v + 1])).Append(',').Append(F(p[3 * v + 2]));
                }
                sb.Append("){");
                for (int k = 0; k < 3; k++)
                {
                    int v = wall.Triangles[3 * t + k];
                    if (k > 0) sb.Append(',');
                    sb.Append(F(field.Vectors[3 * v])).Append(',').Append(F(field.Vectors[3 * v + 1])).Append(',').Append(F(field.Vectors[3 * v + 2]));
                }
                sb.Append("};\n");
            }
            sb.Append("};\n");
            File.WriteAllText(path, sb.ToString(), new UTF8Encoding(false));
        }

        private static string F(double value) => value.ToString("0.#######", CultureInfo.InvariantCulture);

        private static double[] Cross(double[] a, double[] b)
            => new[] { a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0] };

        private static double Dot(double[] a, double[] b) => a[0] * b[0] + a[1] * b[1] + a[2] * b[2];
    }
}
