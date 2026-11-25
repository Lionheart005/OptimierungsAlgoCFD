using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;
using System.Text.Json;
using Clipper2Lib;
using FluidForge.Core;

namespace FluidForge.Kernels.Planar2D
{
    /// <summary>
    /// Serializable DTO for 2D contour data (compatible with 2D meshers such as Gmsh).
    /// </summary>
    public class ContourFile2D
    {
        public List<ContourLoop2D> Contours { get; set; } = new();
    }

    /// <summary>
    /// Single closed loop within a contour file (outer boundary or inner hole).
    /// </summary>
    public class ContourLoop2D
    {
        /// <summary>Control points of the loop as [x, y] coordinate pairs in mm.</summary>
        public List<float[]> Points { get; set; } = new();

        /// <summary>Indices of control points that form sharp corners.</summary>
        public List<int> CornerIndices { get; set; } = new();

        /// <summary>True if the loop represents an inner hole.</summary>
        public bool IsHole { get; set; }
    }

    /// <summary>
    /// Two-dimensional shape (set of closed contours with optional holes).
    /// Built on Clipper2 (PathsD) and provides boolean operations, rounding/offsetting,
    /// corner detection, and export (JSON/SVG).
    /// </summary>
    public class Shape2D
    {
        /// <summary>
        /// Decimal precision passed to Clipper2 (6 = 1e-6 mm = 1 nm).
        /// Prevents sub-micrometer rounding distortion on fine airfoil leading edges.
        /// </summary>
        public const int ClipperPrecision = 6;

        public PathsD Paths { get; private set; } = new();

        /// <summary>
        /// Threshold angle in degrees for detecting sharp corners (e.g., trailing edge).
        /// Default: 35 degrees turn angle.
        /// </summary>
        public float CornerAngleDeg { get; set; } = 35.0f;

        public Shape2D()
        {
        }

        public Shape2D(PathsD paths)
        {
            if (paths != null)
                Paths = new PathsD(paths);
        }

        public Shape2D(Lattice2D lattice)
        {
            if (lattice?.Paths != null)
                Paths = new PathsD(lattice.Paths);
        }

        #region Boolean Operations (mutating and fluent)

        /// <summary>
        /// Unites this shape with another shape (Union).
        /// </summary>
        public Shape2D BoolAdd(Shape2D other)
        {
            if (other?.Paths != null && other.Paths.Count > 0)
            {
                Paths = Paths.Count == 0
                    ? new PathsD(other.Paths)
                    : Clipper.Union(Paths, other.Paths, FillRule.NonZero, ClipperPrecision);
            }
            return this;
        }

        /// <summary>
        /// Subtracts another shape from this shape (Difference).
        /// </summary>
        public Shape2D BoolSubtract(Shape2D other)
        {
            if (other?.Paths != null && other.Paths.Count > 0 && Paths.Count > 0)
            {
                Paths = Clipper.Difference(Paths, other.Paths, FillRule.NonZero, ClipperPrecision);
            }
            return this;
        }

        /// <summary>
        /// Intersects this shape with another shape (Intersect).
        /// </summary>
        public Shape2D BoolIntersect(Shape2D other)
        {
            if (other?.Paths == null || other.Paths.Count == 0 || Paths.Count == 0)
            {
                Paths = new PathsD();
            }
            else
            {
                Paths = Clipper.Intersect(Paths, other.Paths, FillRule.NonZero, ClipperPrecision);
            }
            return this;
        }

        #endregion

        #region Offset and Rounding

        /// <summary>
        /// Offsets the contours outward (distanceMm > 0) or inward (distanceMm &lt; 0).
        /// Uses JoinType.Round for smooth edges.
        /// </summary>
        public Shape2D Offset(float distanceMm)
        {
            if (Paths.Count == 0 || Math.Abs(distanceMm) < 1e-6)
                return this;

            // Clipper.InflatePaths(PathsD, double delta, JoinType, EndType, double miterLimit, int precision, double arcTolerance)
            Paths = Clipper.InflatePaths(Paths, distanceMm, JoinType.Round, EndType.Polygon, 2.0, ClipperPrecision, 0.002);
            return this;
        }

        /// <summary>
        /// Applies two consecutive offsets.
        /// </summary>
        public Shape2D DoubleOffset(float d1Mm, float d2Mm)
        {
            Offset(d1Mm);
            Offset(d2Mm);
            return this;
        }

        /// <summary>
        /// Triple offset (+r, -2r, +r) analogous to PicoGK's TripleOffset:
        /// uniformly rounds both inner and outer corners with radius r.
        /// </summary>
        public Shape2D TripleOffset(float radiusMm)
        {
            if (radiusMm <= 0f) return this;
            Offset(radiusMm);
            Offset(-2.0f * radiusMm);
            Offset(radiusMm);
            return this;
        }

        #endregion

        #region Primitives and Curves

        /// <summary>
        /// Adds a closed polygon and unites it with the existing shape.
        /// </summary>
        public Shape2D AddPolygon(IReadOnlyList<Vector2> points)
        {
            if (points == null || points.Count < 3)
                return this;

            var path = new PathD(points.Count);
            for (int i = 0; i < points.Count; i++)
            {
                path.Add(new PointD(points[i].X, points[i].Y));
            }

            var newPaths = new PathsD { path };
            Paths = Clipper.Union(Paths, newPaths, FillRule.NonZero, ClipperPrecision);

            return this;
        }

        /// <summary>
        /// Adds a circle as a polygon and unites it with the existing shape.
        /// </summary>
        public Shape2D AddCircle(Vector2 center, float radius, int segments = 64)
        {
            if (radius <= 0f) return this;
            if (segments < 3) segments = 3;

            var path = new PathD(segments);
            double step = 2.0 * Math.PI / segments;
            for (int i = 0; i < segments; i++)
            {
                double angle = i * step;
                path.Add(new PointD(
                    center.X + (radius * Math.Cos(angle)),
                    center.Y + (radius * Math.Sin(angle))));
            }

            var newPaths = new PathsD { path };
            Paths = Clipper.Union(Paths, newPaths, FillRule.NonZero, ClipperPrecision);

            return this;
        }

        /// <summary>
        /// Generates a closed or open Catmull-Rom spline through the given points
        /// and unites it as a closed polygon with the existing shape.
        /// </summary>
        public Shape2D AddSpline(IReadOnlyList<Vector2> controlOrThroughPoints, int samplesPerSegment = 16, bool closed = true)
        {
            if (controlOrThroughPoints == null || controlOrThroughPoints.Count < 3)
                return this;

            int n = controlOrThroughPoints.Count;
            int samples = Math.Max(2, samplesPerSegment);
            var sampled = new List<Vector2>();

            if (closed)
            {
                for (int i = 0; i < n; i++)
                {
                    Vector2 p0 = controlOrThroughPoints[(i - 1 + n) % n];
                    Vector2 p1 = controlOrThroughPoints[i];
                    Vector2 p2 = controlOrThroughPoints[(i + 1) % n];
                    Vector2 p3 = controlOrThroughPoints[(i + 2) % n];

                    for (int s = 0; s < samples; s++)
                    {
                        float t = (float)s / samples;
                        float t2 = t * t;
                        float t3 = t2 * t;

                        Vector2 pt = 0.5f * (
                            (2.0f * p1) +
                            ((-p0 + p2) * t) +
                            (((2.0f * p0) - (5.0f * p1) + (4.0f * p2) - p3) * t2) +
                            ((-p0 + (3.0f * p1) - (3.0f * p2) + p3) * t3)
                        );
                        sampled.Add(pt);
                    }
                }
            }
            else
            {
                for (int i = 0; i < n - 1; i++)
                {
                    Vector2 p0 = i > 0 ? controlOrThroughPoints[i - 1] : controlOrThroughPoints[0];
                    Vector2 p1 = controlOrThroughPoints[i];
                    Vector2 p2 = controlOrThroughPoints[i + 1];
                    Vector2 p3 = (i + 2 < n) ? controlOrThroughPoints[i + 2] : controlOrThroughPoints[n - 1];

                    for (int s = 0; s < samples; s++)
                    {
                        float t = (float)s / samples;
                        float t2 = t * t;
                        float t3 = t2 * t;

                        Vector2 pt = 0.5f * (
                            (2.0f * p1) +
                            ((-p0 + p2) * t) +
                            (((2.0f * p0) - (5.0f * p1) + (4.0f * p2) - p3) * t2) +
                            ((-p0 + (3.0f * p1) - (3.0f * p2) + p3) * t3)
                        );
                        sampled.Add(pt);
                    }
                }
                sampled.Add(controlOrThroughPoints[n - 1]);
            }

            return AddPolygon(sampled);
        }

        #endregion

        #region Measurement and Evaluation

        /// <summary>
        /// Converts all loops into lists of Vector2 points.
        /// </summary>
        public List<List<Vector2>> ToContourPoints()
        {
            var result = new List<List<Vector2>>(Paths.Count);
            foreach (var path in Paths)
            {
                var loop = new List<Vector2>(path.Count);
                foreach (var pt in path)
                {
                    loop.Add(new Vector2((float)pt.x, (float)pt.y));
                }
                result.Add(loop);
            }
            return result;
        }

        /// <summary>
        /// Measures the 2D shape (area, perimeter, chord in X, thickness in Y, point count).
        /// </summary>
        public ContourMeasurement2D Measure()
        {
            return MeshMetrics2D.Measure(ToContourPoints());
        }

        /// <summary>
        /// Computes area and bounding box of the shape.
        /// </summary>
        public void CalculateProperties(out float area, out BBox2 boundingBox)
        {
            var m = Measure();
            area = m.Area;
            boundingBox = m.Bounds;
        }

        /// <summary>
        /// Total perimeter of all contour loops in mm.
        /// </summary>
        public float Perimeter => Measure().Perimeter;

        #endregion

        #region Corner Detection and Export

        /// <summary>
        /// Detects corners (sharp edges/vertices) in a closed loop where the
        /// directional turn angle exceeds cornerAngleDeg.
        /// </summary>
        public static IReadOnlyList<int> DetectCorners(IReadOnlyList<Vector2> loop, float cornerAngleDeg = 35.0f)
        {
            var corners = new List<int>();
            if (loop == null || loop.Count < 3)
                return corners;

            int n = loop.Count;
            for (int i = 0; i < n; i++)
            {
                Vector2 prev = loop[(i - 1 + n) % n];
                Vector2 curr = loop[i];
                Vector2 next = loop[(i + 1) % n];

                Vector2 vIn = curr - prev;
                Vector2 vOut = next - curr;

                float lenIn = vIn.Length();
                float lenOut = vOut.Length();

                if (lenIn < 1e-6f || lenOut < 1e-6f)
                    continue;

                float dot = Vector2.Dot(vIn, vOut) / (lenIn * lenOut);
                dot = Math.Clamp(dot, -1.0f, 1.0f);

                // Turn angle: 0° = straight ahead, 90° = right angle, ~180° = sharp tip/trailing edge
                float turnRad = MathF.Acos(dot);
                float turnDeg = turnRad * (180.0f / MathF.PI);

                if (turnDeg > cornerAngleDeg)
                {
                    corners.Add(i);
                }
            }

            return corners;
        }

        /// <summary>
        /// Saves the contours including detected corners and hole flags as a JSON file.
        /// </summary>
        public void SaveContour(string path, float? cornerAngleDeg = null)
        {
            float angleThreshold = cornerAngleDeg ?? CornerAngleDeg;
            var fileDto = new ContourFile2D();

            foreach (var pathD in Paths)
            {
                var loop = new List<Vector2>(pathD.Count);
                var pointsList = new List<float[]>(pathD.Count);
                foreach (var pt in pathD)
                {
                    float x = (float)pt.x;
                    float y = (float)pt.y;
                    loop.Add(new Vector2(x, y));
                    pointsList.Add(new float[] { x, y });
                }

                var corners = (List<int>)DetectCorners(loop, angleThreshold);
                bool isHole = !Clipper.IsPositive(pathD);

                fileDto.Contours.Add(new ContourLoop2D
                {
                    Points = pointsList,
                    CornerIndices = corners,
                    IsHole = isHole
                });
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            string json = JsonSerializer.Serialize(fileDto, options);
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, json, Encoding.UTF8);
        }

        /// <summary>
        /// Loads a ContourFile2D from a JSON file.
        /// </summary>
        public static ContourFile2D LoadContour(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException($"Contour file not found: {path}", path);

            string json = File.ReadAllText(path, Encoding.UTF8);
            var fileDto = JsonSerializer.Deserialize<ContourFile2D>(json);
            return fileDto ?? new ContourFile2D();
        }

        /// <summary>
        /// Creates a Shape2D from a previously saved ContourFile2D JSON file.
        /// </summary>
        public static Shape2D FromContourFile(string path)
        {
            var fileDto = LoadContour(path);
            var paths = new PathsD();

            foreach (var contour in fileDto.Contours)
            {
                if (contour.Points == null || contour.Points.Count == 0)
                    continue;

                var pathD = new PathD(contour.Points.Count);
                foreach (var p in contour.Points)
                {
                    if (p != null && p.Length >= 2)
                        pathD.Add(new PointD(p[0], p[1]));
                }

                if (contour.IsHole)
                {
                    if (Clipper.IsPositive(pathD))
                        pathD.Reverse();
                }
                else
                {
                    if (!Clipper.IsPositive(pathD))
                        pathD.Reverse();
                }

                paths.Add(pathD);
            }

            return new Shape2D(paths);
        }

        /// <summary>
        /// Exports the 2D shape as an SVG vector graphic with bounding box and highlighted corners.
        /// </summary>
        public void SaveSvg(string path, float strokeWidth = 0.2f)
        {
            var m = Measure();
            BBox2 bounds = m.Bounds;

            float minX = bounds.IsEmpty ? 0f : bounds.Min.X;
            float minY = bounds.IsEmpty ? 0f : -bounds.Max.Y;
            float width = bounds.IsEmpty ? 100f : MathF.Max(bounds.Size.X, 1.0f);
            float height = bounds.IsEmpty ? 100f : MathF.Max(bounds.Size.Y, 1.0f);

            float margin = MathF.Max(MathF.Max(width, height) * 0.05f, 1.0f);
            float vbX = minX - margin;
            float vbY = minY - margin;
            float vbW = width + (2.0f * margin);
            float vbH = height + (2.0f * margin);

            var sb = new StringBuilder();
            sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            sb.AppendLine($"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"{vbX.ToString("F3", CultureInfo.InvariantCulture)} {vbY.ToString("F3", CultureInfo.InvariantCulture)} {vbW.ToString("F3", CultureInfo.InvariantCulture)} {vbH.ToString("F3", CultureInfo.InvariantCulture)}\">");

            var pathData = new StringBuilder();
            var cornerPoints = new List<Vector2>();

            foreach (var pathD in Paths)
            {
                if (pathD.Count == 0) continue;

                var loop = new List<Vector2>(pathD.Count);
                for (int i = 0; i < pathD.Count; i++)
                {
                    var p = pathD[i];
                    loop.Add(new Vector2((float)p.x, (float)p.y));
                    string cmd = (i == 0) ? "M" : "L";
                    pathData.Append(CultureInfo.InvariantCulture, $"{cmd} {p.x:F4} {-p.y:F4} ");
                }
                pathData.Append("Z ");

                var cornerIdxs = DetectCorners(loop, CornerAngleDeg);
                foreach (int idx in cornerIdxs)
                {
                    cornerPoints.Add(loop[idx]);
                }
            }

            sb.AppendLine(CultureInfo.InvariantCulture, $"  <path d=\"{pathData.ToString().Trim()}\" fill=\"#3b82f6\" fill-opacity=\"0.3\" fill-rule=\"evenodd\" stroke=\"#1d4ed8\" stroke-width=\"{strokeWidth:F3}\" />");

            float cornerRadius = Math.Clamp(strokeWidth * 1.2f, 0.05f, MathF.Max(width * 0.004f, 0.1f));
            foreach (var cp in cornerPoints)
            {
                sb.AppendLine(CultureInfo.InvariantCulture, $"  <circle cx=\"{cp.X:F4}\" cy=\"{-cp.Y:F4}\" r=\"{cornerRadius:F4}\" fill=\"#ef4444\" stroke=\"#991b1b\" stroke-width=\"{(strokeWidth * 0.5f):F4}\" />");
            }

            sb.AppendLine("</svg>");

            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);
            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
        }

        #endregion
    }
}
