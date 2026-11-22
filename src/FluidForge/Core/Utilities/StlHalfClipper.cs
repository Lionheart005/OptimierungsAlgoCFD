using System;
using System.Collections.Generic;
using System.Numerics;

namespace FluidForge.Core
{
    /// <summary>
    /// Symmetry plane axis for 3D half-model meshing and CFD boundary conditions (<c>MARKER_SYM</c>).
    /// </summary>
    public enum SymmetryAxis
    {
        None = 0,
        Y0 = 1,
        Z0 = 2
    }

    /// <summary>
    /// Result of clipping a 3D triangle mesh to the positive half-space (<c>Y &gt;= 0</c> or <c>Z &gt;= 0</c>).
    /// </summary>
    /// <param name="HalfTriangles">Triangles of the open half-shell on the positive side (triangles lying entirely on the symmetry plane are omitted).</param>
    /// <param name="InputWasFullBody">True if the input geometry spanned both negative and positive sides of the symmetry plane.</param>
    /// <param name="CutEdges">Open boundary edges of the half-shell lying on the symmetry plane.</param>
    public sealed record HalfClipResult(
        IReadOnlyList<StlTriangle> HalfTriangles,
        bool InputWasFullBody,
        IReadOnlyList<(Vector3 A, Vector3 B)> CutEdges);

    /// <summary>
    /// Pure half-space STL clipper for 3D symmetry models (<c>Y0</c> or <c>Z0</c>).
    /// Lives in <c>Core/</c> without external dependencies.
    /// </summary>
    public static class StlHalfClipper
    {
        /// <summary>
        /// Parses a symmetry plane string (<c>"None"</c>, <c>"Y0"</c>, <c>"Z0"</c>, case-insensitive).
        /// Returns <c>false</c> for null, empty, or unrecognized strings.
        /// </summary>
        public static bool TryParse(string? value, out SymmetryAxis axis)
        {
            axis = SymmetryAxis.None;
            if (string.IsNullOrWhiteSpace(value)) return false;

            switch (value.Trim().ToUpperInvariant())
            {
                case "NONE":
                    axis = SymmetryAxis.None;
                    return true;
                case "Y0":
                    axis = SymmetryAxis.Y0;
                    return true;
                case "Z0":
                    axis = SymmetryAxis.Z0;
                    return true;
                default:
                    return false;
            }
        }

        /// <summary>
        /// Parses a configured <c>SymmetryPlane</c> value, treating null/empty as <see cref="SymmetryAxis.None"/>
        /// and throwing a clear <c>[CONFIG]</c> exception for unknown values.
        /// </summary>
        public static SymmetryAxis ParseConfigValue(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return SymmetryAxis.None;
            if (TryParse(value, out SymmetryAxis axis)) return axis;

            throw new InvalidOperationException(
                $"{OutputConventions.ConfigTag} Unknown SymmetryPlane '{value}'. Allowed values are 'None', 'Y0', or 'Z0'.");
        }

        /// <summary>
        /// Clips <paramref name="triangles"/> to the positive half-space (<c>Y &gt;= 0</c> for <see cref="SymmetryAxis.Y0"/>,
        /// <c>Z &gt;= 0</c> for <see cref="SymmetryAxis.Z0"/>), snapping vertices within <c>1e-5 * span</c> onto the plane,
        /// splitting intersecting triangles cleanly with preserved outward normals, and extracting cut edges on the plane.
        /// </summary>
        public static HalfClipResult ClipToHalfSpace(IReadOnlyList<StlTriangle> triangles, SymmetryAxis axis)
        {
            ArgumentNullException.ThrowIfNull(triangles);

            if (axis == SymmetryAxis.None || triangles.Count == 0)
            {
                return new HalfClipResult(triangles, InputWasFullBody: false, Array.Empty<(Vector3, Vector3)>());
            }

            float minCoord = float.MaxValue;
            float maxCoord = float.MinValue;
            foreach (StlTriangle tri in triangles)
            {
                GrowMinMax(Coord(tri.A, axis), ref minCoord, ref maxCoord);
                GrowMinMax(Coord(tri.B, axis), ref minCoord, ref maxCoord);
                GrowMinMax(Coord(tri.C, axis), ref minCoord, ref maxCoord);
            }

            float span = MathF.Max(0f, maxCoord - minCoord);
            float eps = span > 0f ? 1e-5f * span : 1e-6f;
            float tol = MathF.Max(10f * eps, 1e-5f);
            bool inputWasFullBody = minCoord < -tol && maxCoord > tol;

            var halfTriangles = new List<StlTriangle>(triangles.Count);

            foreach (StlTriangle tri in triangles)
            {
                Vector3 a = SnapVertex(tri.A, axis, eps);
                Vector3 b = SnapVertex(tri.B, axis, eps);
                Vector3 c = SnapVertex(tri.C, axis, eps);

                float da = Coord(a, axis);
                float db = Coord(b, axis);
                float dc = Coord(c, axis);

                if (da <= 0f && db <= 0f && dc <= 0f)
                {
                    // Completely on the negative side or coplanar on the symmetry plane (cap face): omit.
                    continue;
                }

                if (da >= 0f && db >= 0f && dc >= 0f)
                {
                    // At least one vertex > 0: keep triangle as-is.
                    AddIfNonDegenerate(halfTriangles, a, b, c);
                    continue;
                }

                // Mixed signs: triangle crosses the symmetry plane.
                // Rotate cyclically so we handle all sign combinations with identical winding.
                ClipMixedTriangle(halfTriangles, a, b, c, da, db, dc, axis);
            }

            IReadOnlyList<(Vector3 A, Vector3 B)> cutEdges = ExtractBoundaryEdgesOnPlane(halfTriangles, axis);
            return new HalfClipResult(halfTriangles, inputWasFullBody, cutEdges);
        }

        private static void ClipMixedTriangle(
            List<StlTriangle> output,
            Vector3 a, Vector3 b, Vector3 c,
            float da, float db, float dc,
            SymmetryAxis axis)
        {
            int posCount = (da > 0f ? 1 : 0) + (db > 0f ? 1 : 0) + (dc > 0f ? 1 : 0);
            int negCount = (da < 0f ? 1 : 0) + (db < 0f ? 1 : 0) + (dc < 0f ? 1 : 0);

            if (posCount == 1 && negCount == 1)
            {
                // One positive, one zero, one negative: replace the negative vertex with edge intersection.
                if (da < 0f)
                {
                    Vector3 p = db > 0f ? Intersect(a, b, da, db, axis) : Intersect(a, c, da, dc, axis);
                    AddIfNonDegenerate(output, p, b, c);
                }
                else if (db < 0f)
                {
                    Vector3 p = dc > 0f ? Intersect(b, c, db, dc, axis) : Intersect(b, a, db, da, axis);
                    AddIfNonDegenerate(output, a, p, c);
                }
                else
                {
                    Vector3 p = da > 0f ? Intersect(c, a, dc, da, axis) : Intersect(c, b, dc, db, axis);
                    AddIfNonDegenerate(output, a, b, p);
                }
                return;
            }

            if (posCount == 1 && negCount == 2)
            {
                // Rotate cyclically so v0 is the single positive vertex.
                if (db > 0f)
                {
                    (a, b, c) = (b, c, a);
                    (da, db, dc) = (db, dc, da);
                }
                else if (dc > 0f)
                {
                    (a, b, c) = (c, a, b);
                    (da, db, dc) = (dc, da, db);
                }

                Vector3 p01 = Intersect(a, b, da, db, axis);
                Vector3 p02 = Intersect(a, c, da, dc, axis);
                AddIfNonDegenerate(output, a, p01, p02);
                return;
            }

            if (posCount == 2 && negCount == 1)
            {
                // Rotate cyclically so v0 is the single negative vertex.
                if (db < 0f)
                {
                    (a, b, c) = (b, c, a);
                    (da, db, dc) = (db, dc, da);
                }
                else if (dc < 0f)
                {
                    (a, b, c) = (c, a, b);
                    (da, db, dc) = (dc, da, db);
                }

                Vector3 p01 = Intersect(a, b, da, db, axis);
                Vector3 p02 = Intersect(a, c, da, dc, axis);
                AddIfNonDegenerate(output, p01, b, c);
                AddIfNonDegenerate(output, p01, c, p02);
            }
        }

        private static IReadOnlyList<(Vector3 A, Vector3 B)> ExtractBoundaryEdgesOnPlane(
            List<StlTriangle> triangles, SymmetryAxis axis)
        {
            var edgeCounts = new Dictionary<UndirectedEdgeKey, ((Vector3 A, Vector3 B) Segment, int Count)>();

            foreach (StlTriangle tri in triangles)
            {
                TryAddPlaneEdge(edgeCounts, tri.A, tri.B, axis);
                TryAddPlaneEdge(edgeCounts, tri.B, tri.C, axis);
                TryAddPlaneEdge(edgeCounts, tri.C, tri.A, axis);
            }

            var cutEdges = new List<(Vector3 A, Vector3 B)>();
            foreach (var entry in edgeCounts.Values)
            {
                if (entry.Count == 1)
                    cutEdges.Add(entry.Segment);
            }
            return cutEdges;
        }

        private static void TryAddPlaneEdge(
            Dictionary<UndirectedEdgeKey, ((Vector3 A, Vector3 B) Segment, int Count)> counts,
            Vector3 p, Vector3 q, SymmetryAxis axis)
        {
            if (Coord(p, axis) != 0f || Coord(q, axis) != 0f) return;

            var vp = NormalizeZero(p);
            var vq = NormalizeZero(q);
            if (vp.Equals(vq)) return;

            var key = CompareVertex(vp, vq) <= 0
                ? new UndirectedEdgeKey(vp, vq)
                : new UndirectedEdgeKey(vq, vp);

            if (counts.TryGetValue(key, out var existing))
                counts[key] = (existing.Segment, existing.Count + 1);
            else
                counts[key] = ((p, q), 1);
        }

        private static Vector3 Intersect(Vector3 p, Vector3 q, float dp, float dq, SymmetryAxis axis)
        {
            // Order consistently from negative to positive so shared edges yield bit-identical points.
            Vector3 neg = dp < 0f ? p : q;
            Vector3 pos = dp < 0f ? q : p;
            float dNeg = dp < 0f ? dp : dq;
            float dPos = dp < 0f ? dq : dp;

            float t = dNeg / (dNeg - dPos);
            t = Math.Clamp(t, 0f, 1f);
            Vector3 point = neg + t * (pos - neg);
            return WithCoord(point, axis, 0f);
        }

        private static void AddIfNonDegenerate(List<StlTriangle> output, Vector3 a, Vector3 b, Vector3 c)
        {
            if (Vector3.Cross(b - a, c - a).LengthSquared() > 1e-18f)
                output.Add(new StlTriangle(a, b, c));
        }

        private static Vector3 SnapVertex(Vector3 v, SymmetryAxis axis, float eps)
        {
            float c = Coord(v, axis);
            if (MathF.Abs(c) <= eps) return WithCoord(v, axis, 0f);
            return v;
        }

        private static float Coord(Vector3 v, SymmetryAxis axis)
            => axis == SymmetryAxis.Y0 ? v.Y : v.Z;

        private static Vector3 WithCoord(Vector3 v, SymmetryAxis axis, float value)
            => axis == SymmetryAxis.Y0 ? new Vector3(v.X, value, v.Z) : new Vector3(v.X, v.Y, value);

        private static void GrowMinMax(float value, ref float min, ref float max)
        {
            if (value < min) min = value;
            if (value > max) max = value;
        }

        private static (float X, float Y, float Z) NormalizeZero(Vector3 v)
            => (v.X == 0f ? 0f : v.X, v.Y == 0f ? 0f : v.Y, v.Z == 0f ? 0f : v.Z);

        private static int CompareVertex((float X, float Y, float Z) a, (float X, float Y, float Z) b)
        {
            int cmp = a.X.CompareTo(b.X);
            if (cmp != 0) return cmp;
            cmp = a.Y.CompareTo(b.Y);
            return cmp != 0 ? cmp : a.Z.CompareTo(b.Z);
        }

        private readonly record struct UndirectedEdgeKey((float X, float Y, float Z) A, (float X, float Y, float Z) B);
    }
}
