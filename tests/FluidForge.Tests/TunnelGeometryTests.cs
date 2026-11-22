using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Xunit;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Since TODO-11, the wind tunnel no longer comes from PicoGK. These tests ensure
    /// that the default case remains the exact same box — if it changes, the simulation
    /// domain and therefore the measured drag changes.
    /// </summary>
    public class TunnelGeometryTests
    {
        /// <summary>
        /// The 12 triangles of <c>PicoGK.Utils.mshCreateCube(new Vector3(600,300,300), Vector3.Zero)</c>,
        /// captured from PicoGK output. Ordering and orientation must match so that Gmsh
        /// generates the same farfield topology as before the refactoring.
        /// </summary>
        private static readonly float[][] PicoGkCubeTriangles =
        {
            new[] { -300f, -150f, -150f, -300f, -150f,  150f, -300f,  150f,  150f },
            new[] { -300f, -150f, -150f, -300f,  150f,  150f, -300f,  150f, -150f },
            new[] {  300f, -150f, -150f,  300f,  150f, -150f,  300f,  150f,  150f },
            new[] {  300f, -150f, -150f,  300f,  150f,  150f,  300f, -150f,  150f },
            new[] { -300f, -150f, -150f, -300f,  150f, -150f,  300f,  150f, -150f },
            new[] { -300f, -150f, -150f,  300f,  150f, -150f,  300f, -150f, -150f },
            new[] { -300f, -150f,  150f,  300f, -150f,  150f,  300f,  150f,  150f },
            new[] { -300f, -150f,  150f,  300f,  150f,  150f, -300f,  150f,  150f },
            new[] { -300f,  150f, -150f, -300f,  150f,  150f,  300f,  150f,  150f },
            new[] { -300f,  150f, -150f,  300f,  150f,  150f,  300f,  150f, -150f },
            new[] { -300f, -150f, -150f,  300f, -150f, -150f,  300f, -150f,  150f },
            new[] { -300f, -150f, -150f,  300f, -150f,  150f, -300f, -150f,  150f }
        };

        [Fact]
        public void Box_Reproduces_The_PicoGk_Cube_Triangle_For_Triangle()
        {
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(
                new Vector3(600f, 300f, 300f), Vector3.Zero);

            Assert.Equal(PicoGkCubeTriangles.Length, box.Count);

            for (int i = 0; i < box.Count; i++)
            {
                float[] expected = PicoGkCubeTriangles[i];
                Assert.Equal(new Vector3(expected[0], expected[1], expected[2]), box[i].A);
                Assert.Equal(new Vector3(expected[3], expected[4], expected[5]), box[i].B);
                Assert.Equal(new Vector3(expected[6], expected[7], expected[8]), box[i].C);
            }
        }

        [Fact]
        public void Box_Is_Centered_On_The_Given_Point()
        {
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(
                new Vector3(600f, 300f, 300f), new Vector3(10f, -20f, 30f));

            (Vector3 min, Vector3 max) = BoundingBox(box);

            Assert.Equal(new Vector3(-290f, -170f, -120f), min);
            Assert.Equal(new Vector3(310f, 130f, 180f), max);
        }

        [Fact]
        public void Box_Normals_Point_Outwards()
        {
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(
                new Vector3(600f, 300f, 300f), Vector3.Zero);

            AssertNormalsPointAwayFrom(box, Vector3.Zero);
        }

        [Fact]
        public void Cylinder_Has_Four_Triangles_Per_Segment_And_The_Expected_Extent()
        {
            const int segments = 32;
            IReadOnlyList<StlTriangle> cylinder = TunnelGeometry.CreateCylinder(
                diameter: 300f, length: 600f, center: Vector3.Zero, segments: segments);

            Assert.Equal(4 * segments, cylinder.Count);

            (Vector3 min, Vector3 max) = BoundingBox(cylinder);

            Assert.Equal(-300f, min.X, 3);
            Assert.Equal(300f, max.X, 3);
            // The mantle is a polygon approximation: transverse to the axis it lies inside the circle,
            // but reaches the radius at the sample points.
            Assert.Equal(150f, max.Y, 3);
            Assert.Equal(-150f, min.Y, 3);
            Assert.True(max.Z <= 150.001f && max.Z > 149.0f, $"max.Z = {max.Z}");
            Assert.True(min.Z >= -150.001f && min.Z < -149.0f, $"min.Z = {min.Z}");
        }

        [Fact]
        public void Cylinder_Normals_Point_Outwards()
        {
            IReadOnlyList<StlTriangle> cylinder = TunnelGeometry.CreateCylinder(
                diameter: 300f, length: 600f, center: new Vector3(5f, -7f, 11f), segments: 24);

            AssertNormalsPointAwayFrom(cylinder, new Vector3(5f, -7f, 11f));
        }

        /// <summary>
        /// A closed hull uses each edge exactly twice, in opposite directions.
        /// If a cap is missing or a triangle is flipped, it fails here — and Gmsh
        /// would not be able to build a volume from the tunnel.
        /// </summary>
        [Theory]
        [InlineData(3)]
        [InlineData(8)]
        [InlineData(64)]
        public void Cylinder_Is_Watertight(int segments)
        {
            IReadOnlyList<StlTriangle> cylinder = TunnelGeometry.CreateCylinder(
                diameter: 300f, length: 600f, center: Vector3.Zero, segments: segments);

            AssertWatertight(cylinder);
        }

        [Fact]
        public void Box_Is_Watertight()
        {
            AssertWatertight(TunnelGeometry.CreateBox(new Vector3(600f, 300f, 300f), Vector3.Zero));
        }

        [Theory]
        [InlineData(2)]
        [InlineData(0)]
        [InlineData(-5)]
        public void Cylinder_Rejects_Too_Few_Segments(int segments)
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TunnelGeometry.CreateCylinder(300f, 600f, Vector3.Zero, segments));
        }

        [Fact]
        public void Cylinder_Rejects_Non_Positive_Dimensions()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TunnelGeometry.CreateCylinder(0f, 600f, Vector3.Zero, 16));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => TunnelGeometry.CreateCylinder(300f, -1f, Vector3.Zero, 16));
        }

        // --- Helper functions -----------------------------------------------

        internal static (Vector3 Min, Vector3 Max) BoundingBox(IReadOnlyList<StlTriangle> triangles)
        {
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach (StlTriangle t in triangles)
            {
                foreach (Vector3 v in new[] { t.A, t.B, t.C })
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }

            return (min, max);
        }

        private static void AssertNormalsPointAwayFrom(IReadOnlyList<StlTriangle> triangles, Vector3 center)
        {
            for (int i = 0; i < triangles.Count; i++)
            {
                StlTriangle t = triangles[i];
                Vector3 centroid = (t.A + t.B + t.C) / 3.0f;
                float outward = Vector3.Dot(t.Normal, centroid - center);

                Assert.True(outward > 0.0f, $"Triangle {i} points inward (dot product {outward}).");
            }
        }

        private static void AssertWatertight(IReadOnlyList<StlTriangle> triangles)
        {
            Dictionary<(Vector3, Vector3), int> edges = new Dictionary<(Vector3, Vector3), int>();

            void Add(Vector3 from, Vector3 to)
            {
                var key = (from, to);
                edges[key] = edges.TryGetValue(key, out int count) ? count + 1 : 1;
            }

            foreach (StlTriangle t in triangles)
            {
                Add(t.A, t.B);
                Add(t.B, t.C);
                Add(t.C, t.A);
            }

            foreach (KeyValuePair<(Vector3 From, Vector3 To), int> edge in edges)
            {
                Assert.Equal(1, edge.Value);
                Assert.True(edges.ContainsKey((edge.Key.To, edge.Key.From)),
                    $"Edge {edge.Key.From} → {edge.Key.To} has no opposite edge.");
            }
        }
    }
}
