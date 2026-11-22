using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;

using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Wall field for boundary-layer extrusion (Plan wandschicht_zellzahl.md, WZ2): smoothed directions,
    /// exact folding check, pinch gaps, fallback ladder, file formats.
    /// </summary>
    public class BoundaryLayerWallFieldTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "wallfield_" + Guid.NewGuid().ToString("N"));

        public BoundaryLayerWallFieldTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        /// <summary>
        /// Grid surface z = height(y) over x ∈ [0, lengthX], y ∈ [yMin, yMax]. <paramref name="up"/>: normals point to +z
        /// (otherwise −z). The winding order determines which way the layer grows.
        /// </summary>
        private static WallSurface Sheet(Func<double, double> height, double yMin, double yMax, double h, bool up = true, double lengthX = 6.0, double zOffset = 0.0)
        {
            int nx = (int)Math.Round(lengthX / h) + 1, ny = (int)Math.Round((yMax - yMin) / h) + 1;
            var pts = new List<double>();
            for (int i = 0; i < nx; i++)
            for (int j = 0; j < ny; j++)
            {
                double y = yMin + (yMax - yMin) * j / (ny - 1);
                pts.Add(lengthX * i / (nx - 1)); pts.Add(y); pts.Add(height(y) + zOffset);
            }

            var tris = new List<int>();
            for (int i = 0; i < nx - 1; i++)
            for (int j = 0; j < ny - 1; j++)
            {
                int a = i * ny + j, b = (i + 1) * ny + j, c = (i + 1) * ny + j + 1, d = i * ny + j + 1;
                // (a,b,c): e1 = +x, e2 = +x+y -> normal +z
                if (up) { tris.AddRange(new[] { a, b, c, a, c, d }); }
                else { tris.AddRange(new[] { a, c, b, a, d, c }); }
            }

            return new WallSurface(pts.ToArray(), tris.ToArray());
        }

        // ------------------------------------------------------------------ Basics

        [Fact]
        public void Flat_Sheet_Needs_No_Smoothing_And_No_Thinning()
        {
            var wall = Sheet(_ => 0.0, -3, 3, 0.5);

            var field = BoundaryLayerWallField.Build(wall, 2.0, BoundaryLayerWallField.Ladder.Last());

            Assert.Equal(0.0, field.MaxTiltDeg, 6);
            Assert.Equal(0, field.FoldedTriangles);
            // Ladder.Last() thins globally to 25% (fallback mesh): uniform everywhere, no local effect
            Assert.Equal(1.0, field.ThinnedShare, 9);
            Assert.All(field.Scale, s => Assert.Equal(0.25, s, 9));

            var a = BoundaryLayerWallField.Build(wall, 2.0, BoundaryLayerWallField.Ladder[0]);
            Assert.All(a.Scale, s => Assert.Equal(1.0, s, 9));
            Assert.Equal(1.0, a.MinScale, 9);
        }

        [Fact]
        public void Raw_Normals_Of_A_Sheet_Point_Along_The_Winding()
        {
            var up = Sheet(_ => 0.0, -2, 2, 1.0, up: true);
            var down = Sheet(_ => 0.0, -2, 2, 1.0, up: false);

            Assert.Equal(1.0, up.RawNormals[2], 9);
            Assert.Equal(-1.0, down.RawNormals[2], 9);
        }

        [Theory]
        [InlineData(1.0, -3.0, 2.0, 1.0)]   // T² − 3T + 2 -> roots 1 and 2 -> smallest positive 1
        [InlineData(0.0, -2.0, 4.0, 2.0)]   // linear: −2T + 4 = 0
        [InlineData(0.0, 2.0, 4.0, double.PositiveInfinity)]   // only grows
        [InlineData(1.0, 0.0, 1.0, double.PositiveInfinity)]   // no real root
        public void SmallestPositiveRoot_Works(double a, double b, double c, double expected)
        {
            double root = BoundaryLayerWallField.SmallestPositiveRoot(a, b, c);
            if (double.IsInfinity(expected)) Assert.True(double.IsPositiveInfinity(root));
            else Assert.Equal(expected, root, 9);
        }

        // ------------------------------------------------------------------ Valley

        /// <summary>
        /// Crease valley: two surfaces meet at angle <paramref name="slopeDeg"/> along a line (like sensor sphere
        /// on the wing: curvature sits on one or two mesh rows). Normals converge above the valley.
        /// </summary>
        private static WallSurface Valley(double slopeDeg, double h)
        {
            double k = Math.Tan(slopeDeg * Math.PI / 180.0);
            return Sheet(y => k * Math.Abs(y), -3.0, 3.0, h, lengthX: 4.0);
        }

        [Fact]
        public void Raw_Normals_Fold_In_A_Valley_And_Smoothing_Delays_It()
        {
            var wall = Valley(40.0, 0.25);

            double[] rawCrit = BoundaryLayerWallField.CriticalTriangleThickness(wall, wall.RawNormals);
            double rawMin = rawCrit.Min();

            double[] smooth = BoundaryLayerWallField.SmoothDirections(wall, thickness: 2.0, radiusFactor: 0.8, maxTiltDeg: 30);
            double smoothMin = BoundaryLayerWallField.CriticalTriangleThickness(wall, smooth).Min();

            // Raw folds already at fractions of the mesh size; smoothed only at significantly larger thickness
            Assert.True(rawMin < 1.0, $"Raw normals must fold early in the valley (raw {rawMin:0.00}).");
            Assert.True(smoothMin > 2.0 * rawMin, $"Smoothed directions must fold later (raw {rawMin:0.00}, smoothed {smoothMin:0.00}).");
        }

        [Fact]
        public void Smoothed_Directions_Stay_Unit_Length_And_Within_The_Tilt_Limit()
        {
            var wall = Valley(40.0, 0.25);
            const double maxTilt = 20.0;

            double[] dir = BoundaryLayerWallField.SmoothDirections(wall, 3.0, 1.6, maxTilt);

            for (int v = 0; v < wall.VertexCount; v++)
            {
                double len = Math.Sqrt(dir[3 * v] * dir[3 * v] + dir[3 * v + 1] * dir[3 * v + 1] + dir[3 * v + 2] * dir[3 * v + 2]);
                Assert.Equal(1.0, len, 6);
                double c = dir[3 * v] * wall.RawNormals[3 * v] + dir[3 * v + 1] * wall.RawNormals[3 * v + 1] + dir[3 * v + 2] * wall.RawNormals[3 * v + 2];
                Assert.True(Math.Acos(Math.Clamp(c, -1, 1)) * 180 / Math.PI <= maxTilt + 1e-6, "Tilt above limit.");
            }
        }

        [Fact]
        public void Smoothing_Does_Not_Touch_A_Flat_Sheet_Or_A_Convex_Edge()
        {
            // Convex edge (roof): normals diverge, nothing to fold. A flat sheet stays exact.
            var roof = Sheet(y => -0.8 * Math.Abs(y), -3.0, 3.0, 0.25, lengthX: 4.0);
            double[] crit = BoundaryLayerWallField.CriticalTriangleThickness(roof, roof.RawNormals);

            Assert.All(crit, c => Assert.True(c > 1e3, $"Convex edge must not fold (critical thickness {c:0.##})."));   // infinite or numerically huge
        }

        [Fact]
        public void Local_Thinning_Makes_The_Layer_Fold_Free_Where_Smoothing_Alone_Does_Not()
        {
            var wall = Valley(40.0, 0.25);
            const double thickness = 4.0;   // far beyond what the valley tolerates even when smoothed

            var stageA = BoundaryLayerWallField.Build(wall, thickness, BoundaryLayerWallField.Ladder[0]);
            var stageB = BoundaryLayerWallField.Build(wall, thickness, BoundaryLayerWallField.Ladder[1]);

            Assert.True(stageA.FoldedTriangles > 0, "Thickness is chosen so that stage A still folds.");
            Assert.Equal(1.0, stageA.MinScale, 9);

            Assert.True(stageB.MinScale < 0.99 && stageB.MinScale >= 0.3 - 1e-9, $"Stage B must thin (smallest factor {stageB.MinScale:0.00}).");
            Assert.True(stageB.ThinnedShare > 0.0 && stageB.ThinnedShare < 1.0, $"Thinning is local, not everywhere ({stageB.ThinnedShare:0.00}).");
            Assert.Equal(2, stageB.Settings.Stage);

            // Actual goal: with direction * factor as offset, nothing folds within the target thickness.
            double minCrit = BoundaryLayerWallField.CriticalTriangleThickness(wall, stageB.Vectors).Min();
            Assert.True(minCrit > thickness, $"Even thinned it folds at {minCrit:0.00} < {thickness}.");
        }

        [Fact]
        public void A_Wide_Valley_Needs_No_Thinning_At_A_Thin_Layer()
        {
            var wall = Valley(40.0, 0.25);
            const double thickness = 0.3;   // smaller than mesh size: nothing to do

            var stageB = BoundaryLayerWallField.Build(wall, thickness, BoundaryLayerWallField.Ladder[1]);

            Assert.Equal(0, stageB.FoldedTriangles);
            Assert.Equal(1.0, stageB.MinScale, 9);
            Assert.Equal(0.0, stageB.ThinnedShare, 9);
        }

        // ------------------------------------------------------------------ Pinch gaps

        [Fact]
        public void Pinch_Distances_Find_The_Opposite_Plate()
        {
            const double gap = 1.0;
            // Plate A (normals pointing up) at z = 0, plate B (normals pointing down) at z = gap, same area
            var a = Sheet(_ => 0.0, -3, 3, 0.5, up: true);
            var b = Sheet(_ => 0.0, -3, 3, 0.5, up: false, zOffset: gap);
            var wall = Merge(a, b);

            double[] dir = wall.RawNormals;
            double[] pinch = BoundaryLayerWallField.PinchDistances(wall, dir, 3.0);

            // Interior nodes of plate A: distance to plate B = gap
            int checkedNodes = 0;
            for (int v = 0; v < a.VertexCount; v++)
            {
                double x = wall.Points[3 * v], y = wall.Points[3 * v + 1];
                if (x < 1 || x > 5 || Math.Abs(y) > 2) continue;
                Assert.Equal(gap, pinch[v], 6);
                checkedNodes++;
            }
            Assert.True(checkedNodes > 10);

            // Further apart (out of range): no pinch
            var far = Merge(a, Sheet(_ => 0.0, -3, 3, 0.5, up: false, zOffset: 10.0));
            double[] pinchFar = BoundaryLayerWallField.PinchDistances(far, far.RawNormals, 3.0);
            Assert.All(pinchFar.Take(a.VertexCount), d => Assert.True(double.IsPositiveInfinity(d)));
        }

        [Fact]
        public void Parallel_Sheets_Far_Apart_Or_Facing_Away_Are_Not_A_Pinch()
        {
            // Same plates, but facing away from each other: A down at z = 0, B up at z = 1
            var a = Sheet(_ => 0.0, -3, 3, 0.5, up: false);
            var b = Sheet(_ => 0.0, -3, 3, 0.5, up: true, zOffset: 1.0);
            var wall = Merge(a, b);

            double[] pinch = BoundaryLayerWallField.PinchDistances(wall, wall.RawNormals, 3.0);

            Assert.All(pinch, d => Assert.True(double.IsPositiveInfinity(d)));
        }

        [Fact]
        public void Pinch_Thins_Both_Layers_To_Fit_The_Gap()
        {
            const double gap = 1.0;
            var wall = Merge(Sheet(_ => 0.0, -3, 3, 0.5, up: true), Sheet(_ => 0.0, -3, 3, 0.5, up: false, zOffset: gap));
            double thickness = 1.0;   // two layers of 1.0 each do not fit into a gap of 1.0

            var b1 = BoundaryLayerWallField.Build(wall, thickness, BoundaryLayerWallField.Ladder[1]);

            Assert.True(b1.PinchedNodes > 0);
            // gap / (2.2 * T) = 0.4545 -> layer is thinned to around 45% (boundary nodes may have fewer hits)
            Assert.InRange(b1.MinScale, 0.3, 0.5);
            Assert.True(b1.ThinnedShare > 0.3);
        }

        private static WallSurface Merge(WallSurface a, WallSurface b)
        {
            var pts = a.Points.Concat(b.Points).ToArray();
            int offset = a.VertexCount;
            var tris = a.Triangles.Concat(b.Triangles.Select(t => t + offset)).ToArray();
            return new WallSurface(pts, tris);
        }

        // ------------------------------------------------------------------ Fallback ladder

        [Fact]
        public void Ladder_Goes_From_Standard_To_Fallback_And_Never_Changes_The_Layers()
        {
            var ladder = BoundaryLayerWallField.Ladder;

            Assert.Equal(1, ladder[0].Stage);
            Assert.False(ladder[0].LocalThickness);
            Assert.Equal(1.0, ladder[0].GlobalScale);
            Assert.Equal(3, ladder[^1].Stage);
            Assert.True(ladder[^1].GlobalScale < 1.0);

            // strictly non-decreasing stages, only becoming more conservative
            for (int i = 1; i < ladder.Count; i++)
            {
                Assert.True(ladder[i].Stage >= ladder[i - 1].Stage);
                Assert.True(ladder[i].Safety <= ladder[i - 1].Safety);
                Assert.True(ladder[i].GlobalScale <= ladder[i - 1].GlobalScale);
            }

            // The field knows neither layer count nor first cell height: it cannot change anything there.
            Assert.DoesNotContain(typeof(WallFieldSettings).GetProperties(), p => p.Name.Contains("Layer", StringComparison.OrdinalIgnoreCase));
        }

        // ------------------------------------------------------------------ Files

        [Fact]
        public void ReadMedit_Reads_Vertices_And_Triangles_And_Skips_Other_Sections()
        {
            string path = Path.Combine(_dir, "wall.mesh");
            File.WriteAllText(path,
                " MeshVersionFormatted 2\n Dimension\n 3\n Vertices\n 4\n"
                + "  0 0 0 1\n  1 0 0 1\n  1 1 0 1\n  0 1 0 1\n"
                + " Edges\n 2\n 1 2 0\n 2 3 0\n"
                + " Triangles\n 2\n 1 2 3 1\n 1 3 4 1\n End\n");

            var wall = WallSurface.ReadMedit(path);

            Assert.Equal(4, wall.VertexCount);
            Assert.Equal(2, wall.TriangleCount);
            Assert.Equal(new[] { 0, 1, 2, 0, 2, 3 }, wall.Triangles);
            Assert.Equal(1.0, wall.TriangleNormals[2], 9);       // counter-clockwise -> +z
            Assert.Equal(0.5, wall.TriangleAreas[0], 9);
        }

        [Fact]
        public void ReadMedit_Rejects_A_File_Without_Triangles()
        {
            string path = Path.Combine(_dir, "empty.mesh");
            File.WriteAllText(path, " MeshVersionFormatted 2\n Dimension\n 3\n Vertices\n 1\n 0 0 0 1\n End\n");

            Assert.Throws<InvalidDataException>(() => WallSurface.ReadMedit(path));
        }

        [Fact]
        public void WritePos_Writes_One_Vector_Triple_Per_Triangle_In_Gmsh_Format()
        {
            var wall = Sheet(_ => 0.0, -1, 1, 1.0, lengthX: 2.0);
            var field = BoundaryLayerWallField.Build(wall, 1.0, BoundaryLayerWallField.Ladder[0]);
            string path = Path.Combine(_dir, "Wall_X.pos");

            BoundaryLayerWallField.WritePos(path, wall, field);

            string[] lines = File.ReadAllLines(path);
            Assert.StartsWith("View \"", lines[0]);
            Assert.Equal("};", lines[^1]);
            var entries = lines.Where(l => l.StartsWith("VT(")).ToList();
            Assert.Equal(wall.TriangleCount, entries.Count);

            // one entry: VT(9 numbers){9 numbers};  — direction (0,0,1) times factor 1
            string e = entries[0];
            string[] pos = e.Substring(3, e.IndexOf(')') - 3).Split(',');
            string[] vec = e.Substring(e.IndexOf('{') + 1, e.IndexOf('}') - e.IndexOf('{') - 1).Split(',');
            Assert.Equal(9, pos.Length);
            Assert.Equal(9, vec.Length);
            Assert.Equal(1.0, double.Parse(vec[2], CultureInfo.InvariantCulture), 6);
            Assert.Equal(0.0, double.Parse(vec[0], CultureInfo.InvariantCulture), 6);
        }

        [Fact]
        public void Vectors_Carry_The_Thickness_Factor_As_Their_Length()
        {
            var wall = Sheet(_ => 0.0, -3, 3, 0.5);
            var field = BoundaryLayerWallField.Build(wall, 2.0, BoundaryLayerWallField.Ladder[^2]); // C1: global 0,5

            for (int v = 0; v < wall.VertexCount; v++)
            {
                double len = Math.Sqrt(field.Vectors[3 * v] * field.Vectors[3 * v] + field.Vectors[3 * v + 1] * field.Vectors[3 * v + 1] + field.Vectors[3 * v + 2] * field.Vectors[3 * v + 2]);
                Assert.Equal(field.Scale[v], len, 9);
                Assert.Equal(0.5, len, 9);
            }
        }
    }
}
