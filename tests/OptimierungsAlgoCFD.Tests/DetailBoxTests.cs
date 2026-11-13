using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Xunit;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.Planar2D;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>TODO-W6: Detailbox um und hinter dem Modell, feinere Kanten, Fernfeld 500 c mit Wachstum.</summary>
    public class DetailBoxTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "detailbox_" + Guid.NewGuid().ToString("N"));

        public DetailBoxTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); }

        private static List<Vector2> Airfoil(float c = 50f)
        {
            var pts = new List<Vector2>
            {
                new(c, 0f),
                new(c * 0.75f, -c * 0.03f),
                new(c * 0.5f, -c * 0.05f),
                new(c * 0.25f, -c * 0.04f),
                new(c * 0.10f, -c * 0.025f),
            };

            // Abgerundete Nase (Kreisbogen r = 1 mm), damit die Vorderkante kein Knickpunkt (> 35°) ist
            float rNose = 0.02f * c;
            int noseSteps = 8;
            for (int i = 0; i <= noseSteps; i++)
            {
                float angle = -MathF.PI * 0.5f + (MathF.PI * i / noseSteps);
                float x = rNose * (1.0f - MathF.Cos(angle));
                float y = rNose * MathF.Sin(angle);
                pts.Add(new Vector2(x, y));
            }

            pts.Add(new(c * 0.10f, c * 0.035f));
            pts.Add(new(c * 0.25f, c * 0.06f));
            pts.Add(new(c * 0.5f, c * 0.08f));
            pts.Add(new(c * 0.75f, c * 0.05f));
            return pts;
        }

        private string[] Geo2D(Gmsh2DMesherOptions options, float factor = 1.0f)
        {
            string contour = Path.Combine(_dir, "airfoil.json");
            new Shape2D().AddPolygon(Airfoil()).SaveContour(contour, cornerAngleDeg: 35.0f);
            string geo = Path.Combine(_dir, "a.geo");
            new Gmsh2DMesher(options).WriteGeoScript(geo, contour, SimulationConfig.CreateDefault(), 1.0f, factor);
            return File.ReadAllLines(geo);
        }

        private static float Value(string[] lines, string key)
            => float.Parse(lines.First(l => l.StartsWith(key + " =")).Split('=')[1].Trim().TrimEnd(';'), CultureInfo.InvariantCulture);

        [Fact]
        public void New_2D_Defaults_Follow_The_NASA_Grid()
        {
            var o = new Gmsh2DMesherOptions();
            Assert.Equal(500f, o.FarfieldRadiusFactor);
            Assert.Equal(25f, o.FarfieldCellSizeFactor);
            Assert.True(o.DetailBox);
            Assert.Equal(0.25f, o.EdgeRefinementFactor);
        }

        [Fact]
        public void Detail_Box_Reaches_One_Chord_Behind_And_Scales_With_The_Factor()
        {
            var lines1 = Geo2D(new Gmsh2DMesherOptions(), 1.0f);
            var lines05 = Geo2D(new Gmsh2DMesherOptions(), 0.5f);

            Assert.Contains(lines1, l => l.StartsWith("Field[6] = Box;"));
            // Profil: x 0..50, y -2,5..4 (Sehne 50): von -5 bis 100 (1 L hinter der Hinterkante)
            Assert.Equal(-5f, Value(lines1, "Field[6].XMin"), 1);
            Assert.Equal(100f, Value(lines1, "Field[6].XMax"), 1);
            // quer: Profilhöhe plus 0,25 L = 12,5 je Seite
            Assert.True(Value(lines1, "Field[6].YMax") - Value(lines1, "Field[6].YMin") > 25f);
            // Zellgröße L/100 = 0,5 mm, halbiert bei Faktor 0,5
            Assert.Equal(0.5f, Value(lines1, "Field[6].VIn"), 3);
            Assert.Equal(0.25f, Value(lines05, "Field[6].VIn"), 3);
            // eingebunden ins Minimum der Hintergrundfelder
            Assert.Contains(lines1, l => l.StartsWith("Field[5].FieldsList") && l.Contains("6"));
        }

        [Fact]
        public void Detail_Box_And_Edge_Refinement_Can_Be_Switched_Off()
        {
            var lines = Geo2D(new Gmsh2DMesherOptions { DetailBox = false, EdgeRefinementFactor = 1.0f, WakeRefinement = false });

            Assert.DoesNotContain(lines, l => l.StartsWith("Field[6]"));
            Assert.Contains(lines, l => l.Trim() == "Background Field = 3;");
            // Ohne Kantenverfeinerung haben alle Konturpunkte dieselbe Größe.
            var sizes = lines.Where(l => l.StartsWith("Point(") && l.Contains("}")).Select(l => l.Split(',').Last()).Distinct().ToList();
            Assert.True(sizes.Count <= 4, "Konturpunkte sollten (bis auf das Fernfeld) eine Größe haben.");
        }

        [Fact]
        public void Leading_And_Trailing_Edge_Points_Get_Finer_Cells_Without_Extra_Fields()
        {
            var lines = Geo2D(new Gmsh2DMesherOptions());

            // Gmsh 4.11 rechnet mit Distanz-/Ball-Feldern unter der Wandzellgröße bei aktiver Grenzschicht
            // endlos (Servertest 06.11.2026): die Kantenverfeinerung läuft deshalb nur über Punktgrößen.
            Assert.DoesNotContain(lines, l => l.StartsWith("Field[7]") || l.StartsWith("Field[8]"));

            var contourPoints = lines.Where(l => l.StartsWith("Point(") && l.Contains(", 0,")).ToList();
            float Size(string l) => float.Parse(l.Split(',').Last().Replace("};", "").Replace("}", "").Trim(), CultureInfo.InvariantCulture);
            float min = contourPoints.Min(Size), max = contourPoints.Max(Size);
            // Konturzellgröße c/100 = 0,5 mm; an den Kanten ein Viertel davon.
            Assert.Equal(0.125f, min, 3);
            Assert.True(max > 0.4f);
        }

        [Fact]
        public void Contour_Is_Split_Near_Both_Edges_So_The_Refinement_Stays_Local()
        {
            var on = Geo2D(new Gmsh2DMesherOptions());
            var off = Geo2D(new Gmsh2DMesherOptions { EdgeRefinementFactor = 1.0f });

            int splinesOn = on.Count(l => l.StartsWith("Spline("));
            int splinesOff = off.Count(l => l.StartsWith("Spline("));
            // Teilungspunkte 0,1 L hinter der Vorderkante und vor der Hinterkante, oben und unten.
            Assert.True(splinesOn > splinesOff, $"{splinesOn} Splines mit, {splinesOff} ohne Kantenverfeinerung.");
        }

        [Fact]
        public void Cell_Size_Grows_Gently_Out_To_A_Far_Field_Of_500_Chords()
        {
            var lines = Geo2D(new Gmsh2DMesherOptions());

            float distMax = Value(lines, "Field[3].DistMax");
            float sizeMax = Value(lines, "Field[3].SizeMax");
            // 0,4 · 500 c = 200 c = 10 000 mm Wachstumsstrecke bis zur Fernfeldzelle 25 c = 1 250 mm
            Assert.Equal(10000f, distMax, 0);
            Assert.Equal(1250f, sizeMax, 0);
            // Steigung der Zellgröße über den Abstand: ~0,12 statt früher ~3,4
            float slope = (sizeMax - Value(lines, "Field[3].SizeMin")) / (distMax - Value(lines, "Field[3].DistMin"));
            Assert.InRange(slope, 0.05f, 0.25f);
        }

        [Fact]
        public void Explicit_Growth_Distance_Overrides_The_Automatic_Value()
        {
            var lines = Geo2D(new Gmsh2DMesherOptions { NearfieldGrowthDistanceFactor = 20f });
            Assert.Equal(1000f, Value(lines, "Field[3].DistMax"), 0);
        }

        // ------------------------------------------------------------------ 3D

        [Fact]
        public void Three_Dimensional_Detail_Box_Is_On_By_Default_And_Absent_When_Off()
        {
            string path = Path.Combine(_dir, "m.geo");
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var config = SimulationConfig.CreateDefault();

            Assert.True(new GmshMesherOptions().DetailBox); // Standard seit 08.11.2026

            var off = new GmshMesherOptions { DetailBox = false, BoundaryLayerMode = "Threshold" };
            new GmshCfdMesher(off, flow).WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f);
            Assert.DoesNotContain(File.ReadAllLines(path), l => l.StartsWith("Field[5] = Box;"));

            var on = new GmshMesherOptions { DetailBox = true, DetailBoxCellSize = 2.0f, WakeRefinement = true, BoundaryLayerMode = "Threshold" };
            new GmshCfdMesher(on, flow).WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f, 0.5f);
            var lines = File.ReadAllLines(path);

            Assert.Contains(lines, l => l.StartsWith("Field[5] = Box;"));
            Assert.Equal(1.0f, Value(lines, "Field[5].VIn"), 3); // 2 mm × Faktor 0,5
            Assert.Contains(lines, l => l.StartsWith("Field[4].FieldsList") && l.Contains("2") && l.Contains("3") && l.Contains("5"));
        }

        [Fact]
        public void Detail_Box_Also_Works_With_The_Structured_Wall_Layer()
        {
            string path = Path.Combine(_dir, "Meshing_X.geo");
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var options = new GmshMesherOptions { BoundaryLayerMode = "Prism", DetailBox = true };

            new GmshCfdMesher(options, flow).WriteGeoScript(path, "model.stl", "tunnel.stl", SimulationConfig.CreateDefault(), 1.0f);

            var lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.Contains("BoundingBox Surface {wall[]}") && l.StartsWith("dbbox"));
            Assert.Contains(lines, l => l.StartsWith("Field[4].FieldsList") && l.Contains("5"));
        }

        [Fact]
        public void Three_Dimensional_Mesher_Defaults_And_Scheme_Version_Match_AP5()
        {
            var options = new GmshMesherOptions();
            Assert.Equal(1.5f, options.DetailBoxLengthFactor);
            Assert.Equal(0.15f, options.DetailBoxMinHeightLengthFactor);
            Assert.True(options.MeshSizeFromCurvature);
            Assert.Equal(0.5f, options.CurvatureMinSizeFactor);
            Assert.Equal(20, options.CurvatureElements);
            Assert.Equal("tunnel-scaled-curvature-2", MeshCalibration.MesherSchemeVersion);
        }

        [Fact]
        public void Three_Dimensional_Detail_Box_Extends_1_5L_And_Decouples_Z_Padding_With_Length_Floor()
        {
            string pathDefault = Path.Combine(_dir, "detail_default.geo");
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var config = SimulationConfig.CreateDefault();

            new GmshCfdMesher(new GmshMesherOptions { BoundaryLayerMode = "Threshold" }, flow)
                .WriteGeoScript(pathDefault, "model.stl", "tunnel.stl", config, 1.0f);

            var linesDefault = File.ReadAllLines(pathDefault);
            Assert.Contains(linesDefault, l => l.Trim() == "Field[5].XMax = dmaxX + (1.5 * dlenX);");
            Assert.Contains(linesDefault, l => l.Trim() == "dz_width = 0.25 * dlenZ;");
            Assert.Contains(linesDefault, l => l.Trim() == "dz_min_len = 0.15 * dlenX;");
            Assert.Contains(linesDefault, l => l.Trim() == "dz_pad = (dz_width > dz_min_len) ? dz_width : dz_min_len;");
            Assert.Contains(linesDefault, l => l.Trim() == "Field[5].ZMin = dminZ - dz_pad;");
            Assert.Contains(linesDefault, l => l.Trim() == "Field[5].ZMax = dmaxZ + dz_pad;");

            // Deaktivierbar über 0 (und <= 0)
            string pathZero = Path.Combine(_dir, "detail_zero_floor.geo");
            new GmshCfdMesher(new GmshMesherOptions
            {
                BoundaryLayerMode = "Threshold",
                DetailBoxMinHeightLengthFactor = 0f
            }, flow).WriteGeoScript(pathZero, "model.stl", "tunnel.stl", config, 1.0f);

            var linesZero = File.ReadAllLines(pathZero);
            Assert.Contains(linesZero, l => l.Trim() == "dz_min_len = 0 * dlenX;");

            string pathNegative = Path.Combine(_dir, "detail_neg_floor.geo");
            new GmshCfdMesher(new GmshMesherOptions
            {
                BoundaryLayerMode = "Threshold",
                DetailBoxMinHeightLengthFactor = -0.2f
            }, flow).WriteGeoScript(pathNegative, "model.stl", "tunnel.stl", config, 1.0f);

            var linesNegative = File.ReadAllLines(pathNegative);
            Assert.Contains(linesNegative, l => l.Trim() == "dz_min_len = 0 * dlenX;");
        }

        [Theory]
        [InlineData(0.01f, 0.1f, 1.0f)]
        [InlineData(0.35f, 0.35f, 0.5f)]
        [InlineData(2.5f, 1.0f, 0.4f)]
        public void CurvatureMinSizeFactor_Is_Clamped_And_Scaled_In_Prism_And_Threshold(
            float configuredFactor, float expectedClampedFactor, float scaleFactor)
        {
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var config = SimulationConfig.CreateDefault();
            float refinementFactor = 0.5f;

            // Prism: Schritt 1 (Wall_*.geo)
            var prismOptions = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                SurfaceCellSize = 2.0f,
                BoundaryLayerSizeMax = 100.0f,
                MeshSizeFromCurvature = true,
                CurvatureMinSizeFactor = configuredFactor
            };
            string prismGeo = Path.Combine(_dir, $"Meshing_Prism_{configuredFactor.ToString(CultureInfo.InvariantCulture)}.geo");
            new GmshCfdMesher(prismOptions, flow).WriteGeoScript(prismGeo, "model.stl", "tunnel.stl", config, scaleFactor, refinementFactor);

            string[] wallLines = File.ReadAllLines(GmshCfdMesher.WallScriptPath(prismGeo));
            float expectedSurfaceGeo = 2.0f * refinementFactor * scaleFactor;
            Assert.Equal(expectedSurfaceGeo, Value(wallLines, "Mesh.MeshSizeMax"), 4);
            Assert.Equal(expectedSurfaceGeo * expectedClampedFactor, Value(wallLines, "Mesh.MeshSizeMin"), 4);
            Assert.Contains(wallLines, l => l.Trim() == "Mesh.MeshSizeFromCurvature = 20;");

            // Threshold
            var thresholdOptions = new GmshMesherOptions
            {
                BoundaryLayerMode = "Threshold",
                BoundaryLayerSizeMin = 1.6f,
                BoundaryLayerSizeMax = 100.0f,
                MeshSizeFromCurvature = true,
                CurvatureMinSizeFactor = configuredFactor
            };
            string thresholdGeo = Path.Combine(_dir, $"Meshing_Thresh_{configuredFactor.ToString(CultureInfo.InvariantCulture)}.geo");
            new GmshCfdMesher(thresholdOptions, flow).WriteGeoScript(thresholdGeo, "model.stl", "tunnel.stl", config, scaleFactor, refinementFactor);

            string[] thresholdLines = File.ReadAllLines(thresholdGeo);
            float expectedSizeMinGeo = 1.6f * refinementFactor * scaleFactor;
            float expectedSizeMaxGeo = 100.0f * refinementFactor * scaleFactor;
            Assert.Contains(thresholdLines, l => l.Trim() == "Mesh.MeshSizeFromCurvature = 1;");
            Assert.Equal(expectedSizeMinGeo * expectedClampedFactor, Value(thresholdLines, "Mesh.MeshSizeMin"), 4);
            Assert.Equal(expectedSizeMaxGeo, Value(thresholdLines, "Mesh.MeshSizeMax"), 3);
        }

        [Fact]
        public void SurfaceCellSize_DetailBoxCellSize_And_BoundaryLayerSizeMax_Are_Decoupled()
        {
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var config = SimulationConfig.CreateDefault();

            string[] WriteAndRead(GmshMesherOptions opts, string tag, out string[] wallLines)
            {
                string geo = Path.Combine(_dir, $"Meshing_{tag}.geo");
                new GmshCfdMesher(opts, flow).WriteGeoScript(geo, "model.stl", "tunnel.stl", config, 1.0f);
                wallLines = File.ReadAllLines(GmshCfdMesher.WallScriptPath(geo));
                return File.ReadAllLines(geo);
            }

            var baseline = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                SurfaceCellSize = 1.0f,
                DetailBoxCellSize = 3.0f,
                BoundaryLayerSizeMax = 120.0f
            };
            string[] baseGeo = WriteAndRead(baseline, "base", out string[] baseWall);

            // 1. DetailBoxCellSize ändern -> Wall_*.geo bleibt exakt identisch
            var changedDetail = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                SurfaceCellSize = 1.0f,
                DetailBoxCellSize = 7.5f,
                BoundaryLayerSizeMax = 120.0f
            };
            string[] detailGeo = WriteAndRead(changedDetail, "detail", out string[] detailWall);
            Assert.Equal(baseWall, detailWall);
            Assert.Equal(7.5f, Value(detailGeo, "Field[5].VIn"), 3);

            // 2. BoundaryLayerSizeMax ändern -> Wall_*.geo bleibt exakt identisch; nur Schritt 2 / VOut ändert sich
            var changedSizeMax = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                SurfaceCellSize = 1.0f,
                DetailBoxCellSize = 3.0f,
                BoundaryLayerSizeMax = 240.0f
            };
            string[] sizeMaxGeo = WriteAndRead(changedSizeMax, "sizemax", out string[] sizeMaxWall);
            Assert.Equal(baseWall, sizeMaxWall);
            Assert.Equal(240.0f, Value(sizeMaxGeo, "Field[5].VOut"), 3);
            Assert.Equal(240.0f, Value(sizeMaxGeo, "Mesh.MeshSizeMax"), 3);
            Assert.Equal(3.0f, Value(sizeMaxGeo, "Field[5].VIn"), 3);

            // 3. SurfaceCellSize ändern -> Field[5].VOut bleibt unverändert
            var changedSurface = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                SurfaceCellSize = 0.4f,
                DetailBoxCellSize = 3.0f,
                BoundaryLayerSizeMax = 120.0f
            };
            string[] surfaceGeo = WriteAndRead(changedSurface, "surface", out string[] surfaceWall);
            Assert.Equal(0.4f, Value(surfaceWall, "Mesh.MeshSizeMax"), 3);
            Assert.Equal(Value(baseGeo, "Field[5].VOut"), Value(surfaceGeo, "Field[5].VOut"), 3);
        }
    }
}
