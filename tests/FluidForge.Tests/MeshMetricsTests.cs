using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-29: Volume, surface area, and bounding box from the triangle mesh.
    ///
    /// <para>
    /// Verified against bodies whose dimensions can be checked by hand — the box from
    /// <see cref="TunnelGeometry"/> is already in the framework for this. That is the real
    /// benefit of this class: the calculation lives in one testable place instead of being
    /// reimplemented in every project.
    /// </para>
    /// </summary>
    public class MeshMetricsTests
    {
        private static readonly Vector3 BoxSize = new Vector3(600f, 300f, 300f);

        private static IReadOnlyList<StlTriangle> Box(Vector3? center = null)
            => TunnelGeometry.CreateBox(BoxSize, center ?? Vector3.Zero);

        /// <summary>600 x 300 x 300 = 54,000,000 mm³, with twelve triangles.</summary>
        [Fact]
        public void The_Volume_Of_A_Box_Is_Its_Product_Of_Edges()
        {
            MeshMeasurement measurement = MeshMetrics.Measure(Box());

            Assert.Equal(12, measurement.TriangleCount);
            Assert.Equal(600f * 300f * 300f, measurement.Volume, 0);
        }

        /// <summary>2 * (600*300 + 600*300 + 300*300) = 900,000 mm².</summary>
        [Fact]
        public void The_Surface_Of_A_Box_Is_The_Sum_Of_Its_Six_Faces()
        {
            float expected = 2f * (600f * 300f + 600f * 300f + 300f * 300f);

            Assert.Equal(expected, MeshMetrics.SurfaceArea(Box()), 0);
        }

        /// <summary>
        /// The bounding box of an origin-centered box spans from -size/2 to +size/2.
        /// The span across the flow (X) is therefore <c>Size.Y</c>.
        /// </summary>
        [Fact]
        public void The_Bounds_Cover_The_Box()
        {
            MeshBounds bounds = MeshMetrics.Bounds(Box());

            Assert.False(bounds.IsEmpty);
            Assert.Equal(-300f, bounds.Min.X, 3);
            Assert.Equal(300f, bounds.Max.X, 3);
            Assert.Equal(BoxSize, bounds.Size);
            Assert.Equal(Vector3.Zero, bounds.Center);
            Assert.Equal(300f, bounds.Size.Y, 3);
        }

        /// <summary>
        /// Volume does not depend on where the body is located — even though the calculation uses
        /// tetrahedra with their apex at the origin. That is the divergence theorem, and if it
        /// were wrong, all volumes of a shifted model would be plausibly wrong.
        /// </summary>
        [Fact]
        public void The_Volume_Does_Not_Depend_On_Where_The_Body_Sits()
        {
            float atOrigin = MeshMetrics.Volume(Box());
            float movedFarAway = MeshMetrics.Volume(Box(new Vector3(5000f, -2000f, 750f)));

            // One per-mille tolerance: coordinates are float, and far from the origin
            // large numbers cancel out in the sum.
            Assert.Equal(atOrigin, movedFarAway, 0.001f * atOrigin);
        }

        /// <summary>
        /// Surface area is completely independent of position — it only uses edge vectors.
        /// </summary>
        [Fact]
        public void The_Surface_Does_Not_Depend_On_Where_The_Body_Sits()
        {
            Assert.Equal(
                MeshMetrics.SurfaceArea(Box()),
                MeshMetrics.SurfaceArea(Box(new Vector3(5000f, -2000f, 750f))),
                0);
        }

        /// <summary>
        /// Inward-facing triangles yield a NEGATIVE volume. That is the purpose of the sign:
        /// Gmsh fails or miscomputes an inverted shell, and in magnitude the error is invisible.
        /// </summary>
        [Fact]
        public void Inverted_Triangles_Give_A_Negative_Signed_Volume()
        {
            var inverted = Box().Select(t => new StlTriangle(t.A, t.C, t.B)).ToList();

            Assert.True(MeshMetrics.SignedVolume(Box()) > 0f, "The default shell faces outward.");
            Assert.True(MeshMetrics.SignedVolume(inverted) < 0f);

            // As a metric, magnitude counts -- otherwise a fitness formula would get a negative
            // volume and thus a score worse than any failure.
            Assert.Equal(MeshMetrics.Volume(Box()), MeshMetrics.Volume(inverted), 0);
        }

        /// <summary>
        /// A cylinder with 64 segments is a 64-sided prism: its volume is systematically
        /// slightly BELOW that of the true cylinder (the polygon lies inside the circle),
        /// here by about 0.06%. Half a percent tolerance covers this and still catches a factor-of-2 error.
        /// </summary>
        [Fact]
        public void A_Cylinder_Comes_Close_To_The_Analytic_Volume()
        {
            IReadOnlyList<StlTriangle> cylinder =
                TunnelGeometry.CreateCylinder(300f, 600f, Vector3.Zero, 64);

            double expected = Math.PI / 4.0 * 300.0 * 300.0 * 600.0;

            Assert.Equal((float)expected, MeshMetrics.Volume(cylinder), (float)(0.005 * expected));
        }

        /// <summary>
        /// Lateral surface (π*d*l) plus two caps (2 * π/4*d²) — again slightly below because
        /// the polygon perimeter is shorter than the circle circumference.
        /// </summary>
        [Fact]
        public void A_Cylinder_Comes_Close_To_The_Analytic_Surface()
        {
            IReadOnlyList<StlTriangle> cylinder =
                TunnelGeometry.CreateCylinder(300f, 600f, Vector3.Zero, 64);

            double expected = Math.PI * 300.0 * 600.0 + 2.0 * (Math.PI / 4.0 * 300.0 * 300.0);

            Assert.Equal((float)expected, MeshMetrics.SurfaceArea(cylinder), (float)(0.005 * expected));
        }

        /// <summary>
        /// The frontal box area is the enclosing rectangle perpendicular to the X-axis.
        /// For an axis-aligned box, this bounding-box area matches the true projected frontal area exactly.
        /// </summary>
        [Fact]
        public void The_Frontal_Box_Area_Is_The_Bounding_Rectangle()
        {
            MeshMeasurement box = MeshMetrics.Measure(Box());

            Assert.Equal(300f * 300f, box.FrontalBoxArea, 0);
            Assert.Equal(300f * 300f, box.FrontalProjectedArea, 0);
        }

        /// <summary>
        /// A cylinder along X has a circular area in YZ projection (π/4 * d² ≈ 70,686 mm²),
        /// whereas the bounding rectangle d * d = 90,000 mm² is 27% too large.
        /// </summary>
        [Fact]
        public void A_Cylinder_Has_A_Circular_Projected_Frontal_Area()
        {
            IReadOnlyList<StlTriangle> cylinder =
                TunnelGeometry.CreateCylinder(300f, 600f, Vector3.Zero, 64);

            MeshMeasurement measurement = MeshMetrics.Measure(cylinder);
            double expectedCircleArea = Math.PI / 4.0 * 300.0 * 300.0;

            Assert.Equal(300f * 300f, measurement.FrontalBoxArea, 0);
            Assert.Equal((float)expectedCircleArea, measurement.FrontalProjectedArea, (float)(0.005 * expectedCircleArea));
        }

        /// <summary>
        /// A long thin rod (300 mm long, 10 mm wide) tilted at 45° in the YZ plane spans
        /// a huge bounding box (~219 * 219 ≈ 48,000 mm²), while its true frontal area is
        /// only 300 * 10 = 3,000 mm².
        /// </summary>
        [Fact]
        public void A_Diagonal_Thin_Rod_In_YZ_Gets_Its_True_Slender_Projected_Area()
        {
            // Thin rod: 20 mm deep in X, 300 mm long in Y, 10 mm thick in Z, rotated 45° around X-axis
            IReadOnlyList<StlTriangle> axisAlignedRod =
                TunnelGeometry.CreateBox(new Vector3(20f, 300f, 10f), Vector3.Zero);

            Quaternion rot45AroundX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4.0f);
            List<StlTriangle> diagonalRod = axisAlignedRod
                .Select(t => new StlTriangle(
                    Vector3.Transform(t.A, rot45AroundX),
                    Vector3.Transform(t.B, rot45AroundX),
                    Vector3.Transform(t.C, rot45AroundX)))
                .ToList();

            MeshMeasurement measurement = MeshMetrics.Measure(diagonalRod);
            float expectedTrueArea = 300f * 10f; // 3,000 mm²

            // Bounding box is ~(310 / √2)² ≈ 48,050 mm² (over 16x too large!)
            Assert.True(measurement.FrontalBoxArea > 45000f);

            // True projected silhouette hits 3,000 mm² within < 1%
            Assert.Equal(expectedTrueArea, measurement.FrontalProjectedArea, 0.01f * expectedTrueArea);

            // The flag switches between both methods
            Assert.Equal(measurement.FrontalProjectedArea, measurement.GetFrontalArea(useProjected: true));
            Assert.Equal(measurement.FrontalBoxArea, measurement.GetFrontalArea(useProjected: false));
        }

        /// <summary>
        /// Two tandem bodies along the flow direction (X) cast the same shadow in the YZ plane —
        /// the projected frontal area must not double.
        /// </summary>
        [Fact]
        public void Tandem_Bodies_Along_Flow_Direction_Do_Not_Double_Count_Projected_Area()
        {
            var frontBox = TunnelGeometry.CreateBox(new Vector3(100f, 200f, 150f), new Vector3(-200f, 0f, 0f));
            var rearBox = TunnelGeometry.CreateBox(new Vector3(100f, 200f, 150f), new Vector3(200f, 0f, 0f));
            var combined = frontBox.Concat(rearBox).ToList();

            float projected = MeshMetrics.FrontalProjectedArea(combined);

            Assert.Equal(200f * 150f, projected, 0);
        }

        /// <summary>
        /// An empty mesh is not a body of size 0, but none at all: the bounding box
        /// reports <c>IsEmpty</c>. Without this distinction, a lost mesh would look like
        /// a model shrunk to the origin.
        /// </summary>
        [Fact]
        public void An_Empty_Mesh_Has_No_Bounds()
        {
            MeshMeasurement measurement = MeshMetrics.Measure(Array.Empty<StlTriangle>());

            Assert.Equal(0, measurement.TriangleCount);
            Assert.Equal(0f, measurement.Volume);
            Assert.Equal(0f, measurement.SurfaceArea);
            Assert.True(measurement.Bounds.IsEmpty);
            Assert.Equal(Vector3.Zero, measurement.Bounds.Size);
        }

        /// <summary>A degenerate triangle has no area and no volume, but has a bounding box.</summary>
        [Fact]
        public void A_Degenerate_Triangle_Has_No_Area()
        {
            var degenerate = new[]
            {
                new StlTriangle(Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f))
            };

            MeshMeasurement measurement = MeshMetrics.Measure(degenerate);

            Assert.Equal(0f, measurement.SurfaceArea, 4);
            Assert.Equal(0f, measurement.Volume, 4);
            Assert.False(measurement.Bounds.IsEmpty);
            Assert.Equal(20f, measurement.Bounds.Size.X, 3);
        }

        /// <summary>
        /// <c>null</c> is a programming error, not an empty set — use
        /// <c>Array.Empty</c> for that.
        /// </summary>
        [Fact]
        public void Null_Is_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => MeshMetrics.Measure(null!));
        }
    }
}
