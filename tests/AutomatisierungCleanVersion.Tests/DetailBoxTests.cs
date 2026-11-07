using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Xunit;

using MyPicoGkProject.Core;
using MyPicoGkProject.Kernels.Planar2D;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
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
        public void Three_Dimensional_Detail_Box_Is_Off_By_Default_And_Wired_When_On()
        {
            string path = Path.Combine(_dir, "m.geo");
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var config = SimulationConfig.CreateDefault();

            new GmshCfdMesher(new GmshMesherOptions(), flow).WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f);
            Assert.DoesNotContain(File.ReadAllLines(path), l => l.StartsWith("Field[5] = Box;"));

            var on = new GmshMesherOptions { DetailBox = true, DetailBoxCellSize = 2.0f, WakeRefinement = true };
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
    }
}
