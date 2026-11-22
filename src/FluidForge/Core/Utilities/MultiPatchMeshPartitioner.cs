using System;
using System.Collections.Generic;
using System.Numerics;

namespace FluidForge.Core
{
    /// <summary>
    /// Planar patch capture specification for partitioning a closed surface mesh into
    /// boundary-condition patches (e.g., <c>inlet</c>, <c>outlet</c>) and <c>wall</c>.
    /// </summary>
    public sealed record PatchPlaneSpec(
        string Name,
        Vector3 Center,
        Vector3 OutwardNormal,
        float CaptureRadius,
        float PlaneTolerance = 0.75f,
        float MinNormalDot = 0.85f);

    /// <summary>
    /// Disjoint partition of a closed triangle mesh into named patches and their surface areas (mm²).
    /// </summary>
    public sealed record PartitionResult(
        Dictionary<string, List<StlTriangle>> Patches,
        Dictionary<string, float> PatchAreas);

    /// <summary>
    /// Pure-Core geometric utilities for partitioning closed watertight triangle meshes into
    /// named boundary patches and checking point containment.
    /// </summary>
    public static class MultiPatchMeshPartitioner
    {
        private static readonly Vector3[] RayDirections =
        {
            Vector3.Normalize(new Vector3(1.0f, 0.37139067f, 0.21821789f)),
            Vector3.Normalize(new Vector3(0.41239305f, 1.0f, 0.19341217f)),
            Vector3.Normalize(new Vector3(0.27182818f, 0.31415927f, 1.0f))
        };

        /// <summary>
        /// Partitions every triangle in <paramref name="triangles"/> into the first matching
        /// <see cref="PatchPlaneSpec"/> or <paramref name="wallPatchName"/>, guaranteeing a 100%
        /// disjoint cover of the input triangles and non-empty patches for all declared ports and walls.
        /// </summary>
        public static PartitionResult PartitionClosedMesh(
            IReadOnlyList<StlTriangle> triangles,
            IReadOnlyList<PatchPlaneSpec> ports,
            string wallPatchName = "wall")
        {
            ArgumentNullException.ThrowIfNull(triangles);
            ArgumentNullException.ThrowIfNull(ports);
            ArgumentException.ThrowIfNullOrWhiteSpace(wallPatchName);

            string wallName = wallPatchName.Trim();
            var normalizedPorts = new List<(PatchPlaneSpec Spec, Vector3 UnitNormal)>(ports.Count);
            var patches = new Dictionary<string, List<StlTriangle>>(StringComparer.OrdinalIgnoreCase);
            var areaAccumulators = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            foreach (PatchPlaneSpec port in ports)
            {
                if (port == null) throw new ArgumentNullException(nameof(ports), "Port specification must not be null.");
                ArgumentException.ThrowIfNullOrWhiteSpace(port.Name);
                if (port.CaptureRadius <= 0f)
                    throw new ArgumentOutOfRangeException(nameof(ports), $"CaptureRadius for patch '{port.Name}' must be positive.");
                if (port.PlaneTolerance < 0f)
                    throw new ArgumentOutOfRangeException(nameof(ports), $"PlaneTolerance for patch '{port.Name}' must be non-negative.");

                float normalLen = port.OutwardNormal.Length();
                if (normalLen <= 1e-6f)
                    throw new ArgumentException($"OutwardNormal for patch '{port.Name}' must be non-zero.", nameof(ports));

                string portName = port.Name.Trim();
                if (portName.Equals(wallName, StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException($"Port name '{portName}' cannot collide with wall patch name '{wallName}'.", nameof(ports));
                if (patches.ContainsKey(portName))
                    throw new ArgumentException($"Duplicate port patch name '{portName}'.", nameof(ports));

                normalizedPorts.Add((port with { Name = portName }, port.OutwardNormal / normalLen));
                patches[portName] = new List<StlTriangle>();
                areaAccumulators[portName] = 0.0;
            }

            patches[wallName] = new List<StlTriangle>();
            areaAccumulators[wallName] = 0.0;

            foreach (StlTriangle tri in triangles)
            {
                Vector3 centroid = (tri.A + tri.B + tri.C) / 3.0f;
                Vector3 triNormal = tri.Normal;

                string targetPatch = wallName;
                foreach (var (spec, unitNormal) in normalizedPorts)
                {
                    if (triNormal == Vector3.Zero)
                        continue;

                    if (Vector3.Dot(triNormal, unitNormal) < spec.MinNormalDot)
                        continue;

                    Vector3 offset = centroid - spec.Center;
                    float signedPlaneDist = Vector3.Dot(offset, unitNormal);
                    if (MathF.Abs(signedPlaneDist) > spec.PlaneTolerance)
                        continue;

                    Vector3 inPlaneOffset = offset - signedPlaneDist * unitNormal;
                    if (inPlaneOffset.Length() <= spec.CaptureRadius)
                    {
                        targetPatch = spec.Name;
                        break;
                    }
                }

                patches[targetPatch].Add(tri);
                areaAccumulators[targetPatch] += ComputeTriangleArea(tri);
            }

            var emptyPatches = new List<string>();
            foreach (var (spec, _) in normalizedPorts)
            {
                if (patches[spec.Name].Count == 0)
                    emptyPatches.Add(spec.Name);
            }

            if (patches[wallName].Count == 0)
                emptyPatches.Add(wallName);

            if (emptyPatches.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Multi-patch mesh partitioning failed: patch(es) [{string.Join(", ", emptyPatches)}] received 0 triangles out of {triangles.Count} total.");
            }

            var patchAreas = new Dictionary<string, float>(areaAccumulators.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in areaAccumulators)
            {
                patchAreas[kvp.Key] = (float)kvp.Value;
            }

            return new PartitionResult(patches, patchAreas);
        }

        /// <summary>
        /// Determines whether <paramref name="point"/> lies strictly inside a closed triangle mesh
        /// using Möller–Trumbore ray-triangle intersection along a non-axis-aligned ray.
        /// Falls back to a 3-ray majority vote if a ray grazes a triangle edge or vertex.
        /// </summary>
        public static bool IsPointInsideClosedMesh(IReadOnlyList<StlTriangle> triangles, Vector3 point)
        {
            ArgumentNullException.ThrowIfNull(triangles);
            if (triangles.Count == 0) return false;

            (int hits0, bool grazed0) = CastRay(triangles, point, RayDirections[0]);
            if (!grazed0)
                return (hits0 & 1) == 1;

            int insideVotes = (hits0 & 1) == 1 ? 1 : 0;
            for (int r = 1; r < RayDirections.Length; r++)
            {
                (int hits, _) = CastRay(triangles, point, RayDirections[r]);
                if ((hits & 1) == 1) insideVotes++;
            }

            return insideVotes >= 2;
        }

        private static (int Hits, bool EdgeGrazed) CastRay(
            IReadOnlyList<StlTriangle> triangles,
            Vector3 origin,
            Vector3 direction)
        {
            const double Epsilon = 1e-9;
            const double EdgeGrazeTol = 1e-6;

            double ox = origin.X, oy = origin.Y, oz = origin.Z;
            double dx = direction.X, dy = direction.Y, dz = direction.Z;

            int hits = 0;
            bool grazed = false;

            for (int i = 0; i < triangles.Count; i++)
            {
                StlTriangle tri = triangles[i];

                double e1x = (double)tri.B.X - tri.A.X;
                double e1y = (double)tri.B.Y - tri.A.Y;
                double e1z = (double)tri.B.Z - tri.A.Z;

                double e2x = (double)tri.C.X - tri.A.X;
                double e2y = (double)tri.C.Y - tri.A.Y;
                double e2z = (double)tri.C.Z - tri.A.Z;

                // pvec = cross(dir, e2)
                double px = dy * e2z - dz * e2y;
                double py = dz * e2x - dx * e2z;
                double pz = dx * e2y - dy * e2x;

                double det = e1x * px + e1y * py + e1z * pz;
                if (Math.Abs(det) < Epsilon)
                    continue;

                double invDet = 1.0 / det;

                double tx = ox - tri.A.X;
                double ty = oy - tri.A.Y;
                double tz = oz - tri.A.Z;

                double u = (tx * px + ty * py + tz * pz) * invDet;
                if (u < -Epsilon || u > 1.0 + Epsilon)
                    continue;

                // qvec = cross(tvec, e1)
                double qx = ty * e1z - tz * e1y;
                double qy = tz * e1x - tx * e1z;
                double qz = tx * e1y - ty * e1x;

                double v = (dx * qx + dy * qy + dz * qz) * invDet;
                if (v < -Epsilon || u + v > 1.0 + Epsilon)
                    continue;

                double t = (e2x * qx + e2y * qy + e2z * qz) * invDet;
                if (t > Epsilon)
                {
                    double w = 1.0 - u - v;
                    if (u < EdgeGrazeTol || v < EdgeGrazeTol || w < EdgeGrazeTol)
                        grazed = true;

                    if (u >= 0.0 && v >= 0.0 && u + v <= 1.0)
                        hits++;
                }
            }

            return (hits, grazed);
        }

        private static double ComputeTriangleArea(StlTriangle tri)
        {
            Vector3 u = tri.B - tri.A;
            Vector3 v = tri.C - tri.A;

            double x = (double)u.Y * v.Z - (double)u.Z * v.Y;
            double y = (double)u.Z * v.X - (double)u.X * v.Z;
            double z = (double)u.X * v.Y - (double)u.Y * v.X;

            return 0.5 * Math.Sqrt(x * x + y * y + z * z);
        }
    }
}
