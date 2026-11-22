using System;
using System.Collections.Generic;
using System.Numerics;
using PicoGK;

using FluidForge.Core;

namespace FluidForge.Kernels.PicoGk
{
    /// <summary>
    /// Bridge between a PicoGK <see cref="Mesh"/> and <see cref="MeshMetrics"/>.
    ///
    /// <para>
    /// <b>Why this class is separated from <see cref="MeshMetrics"/>:</b> it requires
    /// <c>using PicoGK</c>, whereas <c>Core/</c> must remain PicoGK-free. The geometric
    /// calculation therefore lives in Core while the mesh conversion stays in the kernel layer.
    /// For a project, usage is a single line:
    /// </para>
    ///
    /// <code>
    /// MeshMeasurement measurement = PicoGkMeshMetrics.Measure(new Mesh(voxels));
    /// </code>
    ///
    /// <para>
    /// <b>Not unit-tested directly</b> — every call requires a running PicoGK runtime
    /// (<c>Library.Go</c>) and the native <c>libpicogk</c> library. The underlying
    /// math is tested in <see cref="MeshMetrics"/>; this class only loops over
    /// <c>nTriangleCount</c>/<c>GetTriangle</c>.
    /// </para>
    /// </summary>
    public static class PicoGkMeshMetrics
    {
        /// <summary>Volume, surface area, bounding box, and projected frontal area of a PicoGK mesh.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <c>null</c>.</exception>
        public static MeshMeasurement Measure(Mesh mesh) => MeshMetrics.Measure(ToTriangles(mesh));

        /// <summary>True projected frontal area (mm²) of a PicoGK mesh onto the YZ plane perpendicular to the X axis.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <c>null</c>.</exception>
        public static float FrontalProjectedArea(Mesh mesh) => MeshMetrics.FrontalProjectedArea(ToTriangles(mesh));

        /// <summary>
        /// Converts a PicoGK mesh into a framework-native list of triangles so that a mesh
        /// can also be written via <see cref="StlWriter"/> or inspected in tests without
        /// propagating PicoGK types to callers.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="mesh"/> is <c>null</c>.</exception>
        public static IReadOnlyList<StlTriangle> ToTriangles(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            int count = mesh.nTriangleCount();
            var triangles = new List<StlTriangle>(count);

            for (int triangle = 0; triangle < count; triangle++)
            {
                mesh.GetTriangle(triangle, out Vector3 a, out Vector3 b, out Vector3 c);
                triangles.Add(new StlTriangle(a, b, c));
            }

            return triangles;
        }
    }
}
