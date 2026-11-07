using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using Xunit;

using MyPicoGkProject.Composition;
using MyPicoGkProject.Core;
using MyPicoGkProject.Kernels.Planar2D;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Tests für TODO-45 (Gmsh2DMesher) und TODO-46 (SU2 in 2D, Anstellwinkel, Bezugslänge, Cfd2DProjectDefinition).
    /// </summary>
    public class Cfd2DMesherAndSolverTests
    {
        private static List<Vector2> CreateAirfoilContour(float chordMm = 50.0f)
        {
            // Typische NACA-ähnliche Profilkontur mit scharfem Knick an der Hinterkante
            var pts = new List<Vector2>
            {
                new(chordMm, 0f),                 // 0: Hinterkante (Knickpunkt)
                new(chordMm * 0.75f, -chordMm * 0.03f),
                new(chordMm * 0.5f, -chordMm * 0.05f),
                new(chordMm * 0.25f, -chordMm * 0.04f),
                new(0f, 0f),                      // 4: Vorderkante (rund)
                new(chordMm * 0.25f, chordMm * 0.06f),
                new(chordMm * 0.5f, chordMm * 0.08f),
                new(chordMm * 0.75f, chordMm * 0.05f)
            };
            return pts;
        }

        // =====================================================================
        // TODO-45: Gmsh2DMesher Tests
        // =====================================================================

        [Fact]
        public void Gmsh2DMesher_WriteGeoScript_Generates_Expected_2D_Elements()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gmsh2d_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string contourPath = Path.Combine(tempDir, "airfoil.json");
                string geoPath = Path.Combine(tempDir, "airfoil.geo");

                var airfoilPts = CreateAirfoilContour(50.0f);
                var shape = new Shape2D().AddPolygon(airfoilPts);
                shape.SaveContour(contourPath, cornerAngleDeg: 35.0f);

                var mesher = new Gmsh2DMesher(new Gmsh2DMesherOptions());
                var config = SimulationConfig.CreateDefault();

                mesher.WriteGeoScript(geoPath, contourPath, config, scaleFactor: 1.0f, refinementFactor: 1.0f);

                Assert.True(File.Exists(geoPath));
                string[] lines = File.ReadAllLines(geoPath);

                // 1. Point( und Spline( vorhanden
                Assert.Contains(lines, l => l.StartsWith("Point("));
                Assert.Contains(lines, l => l.StartsWith("Spline("));

                // 2. Spline an der scharfen Hinterkante getrennt -> mindestens 2 Splines
                int splineCount = lines.Count(l => l.StartsWith("Spline("));
                Assert.True(splineCount >= 2, $"Erwartet mindestens 2 Splines (getrennt an Kante), gefunden: {splineCount}");

                // 3. BoundaryLayer-Feld
                Assert.Contains(lines, l => l.Contains("Field[1] = BoundaryLayer;"));
                Assert.Contains(lines, l => l.Contains("Field[1].Quads = 1;"));
                Assert.Contains(lines, l => l.Contains("Field[1].FanPointsList"));
                Assert.Contains(lines, l => l.Contains("Field[1].FanNodesList"));
                // Ohne Thickness und ohne Anmeldung erzeugt Gmsh 4.11 keine bzw. nur eine Schicht
                // (Servertest 06.11.2026).
                Assert.Contains(lines, l => l.StartsWith("Field[1].Thickness = "));
                Assert.Contains(lines, l => l.Trim() == "BoundaryLayer Field = 1;");

                // 4. Physikalische Marker für SU2
                Assert.Contains(lines, l => l.Contains("Physical Curve(\"Wall\")"));
                Assert.Contains(lines, l => l.Contains("Physical Curve(\"Farfield\")"));
                Assert.Contains(lines, l => l.Contains("Physical Surface(\"Fluid\") = { 1 };"));

                // 5. Skalierung mm -> m
                Assert.Contains(lines, l => l.Contains("Mesh.ScalingFactor"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Gmsh2DMesher_RemoveMarker_Strips_Fluid_Block_And_Decrements_NMARK()
        {
            // Aufbau wie der SU2-Export von Gmsh 4.11 in 2D: das Fluidgebiet steht als dritter Marker drin.
            string path = Path.Combine(Path.GetTempPath(), "rm_marker_" + Guid.NewGuid().ToString("N") + ".su2");
            File.WriteAllLines(path, new[]
            {
                "NDIME= 2", "NELEM= 2", "5 0 1 2 0", "5 1 3 2 1",
                "NPOIN= 4", "0 0 0", "1 0 1", "1 1 2", "0 1 3",
                "NMARK= 3",
                "MARKER_TAG= Farfield", "MARKER_ELEMS= 1", "3 0 1",
                "MARKER_TAG= Wall", "MARKER_ELEMS= 1", "3 2 3",
                "MARKER_TAG= Fluid", "MARKER_ELEMS= 2", "5 0 1 2", "5 1 3 2",
            });

            try
            {
                Assert.True(Gmsh2DMesher.RemoveMarker(path, Gmsh2DMesher.FluidMarkerName));
                string[] lines = File.ReadAllLines(path);

                Assert.Contains("NMARK= 2", lines);
                Assert.DoesNotContain(lines, l => l.Contains("Fluid"));
                Assert.Equal("3 2 3", lines[^1]);
                Assert.Equal(16, lines.Length);

                // Zweiter Aufruf: nichts mehr zu tun, Datei bleibt gleich.
                Assert.False(Gmsh2DMesher.RemoveMarker(path, Gmsh2DMesher.FluidMarkerName));
                Assert.Equal(lines, File.ReadAllLines(path));
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void Gmsh2DMesher_RefinementFactor_Scales_CellSizes_Except_BoundaryLayer_Y1()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "gmsh2d_scale_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string contourPath = Path.Combine(tempDir, "airfoil.json");
                string geoPath1 = Path.Combine(tempDir, "airfoil_1.geo");
                string geoPath05 = Path.Combine(tempDir, "airfoil_05.geo");

                var airfoilPts = CreateAirfoilContour(50.0f);
                var shape = new Shape2D().AddPolygon(airfoilPts);
                shape.SaveContour(contourPath, cornerAngleDeg: 35.0f);

                var flow = new FlowConditions(68.64f, 1.225f, 1.789e-5f, "test");
                var mesher = new Gmsh2DMesher(new Gmsh2DMesherOptions(), flow);
                var config = SimulationConfig.CreateDefault();

                mesher.WriteGeoScript(geoPath1, contourPath, config, scaleFactor: 1.0f, refinementFactor: 1.0f);
                mesher.WriteGeoScript(geoPath05, contourPath, config, scaleFactor: 1.0f, refinementFactor: 0.5f);

                string[] lines1 = File.ReadAllLines(geoPath1);
                string[] lines05 = File.ReadAllLines(geoPath05);

                // Regel F4: y1 in Field[1].Size darf sich NICHT ändern!
                string y1Line1 = lines1.First(l => l.StartsWith("Field[1].Size ="));
                string y1Line05 = lines05.First(l => l.StartsWith("Field[1].Size ="));
                Assert.Equal(y1Line1, y1Line05);

                // Feldgrößen (Threshold SizeMin/SizeMax) müssen sich um Faktor 0.5 halbieren
                string sizeMin1 = lines1.First(l => l.StartsWith("Field[3].SizeMin ="));
                string sizeMin05 = lines05.First(l => l.StartsWith("Field[3].SizeMin ="));

                float val1 = float.Parse(sizeMin1.Split('=')[1].Trim().TrimEnd(';'), CultureInfo.InvariantCulture);
                float val05 = float.Parse(sizeMin05.Split('=')[1].Trim().TrimEnd(';'), CultureInfo.InvariantCulture);

                Assert.Equal(val1 * 0.5f, val05, 4);

                // Wake-Box Zellgröße muss sich ebenfalls halbieren
                string wakeVin1 = lines1.First(l => l.StartsWith("Field[4].VIn ="));
                string wakeVin05 = lines05.First(l => l.StartsWith("Field[4].VIn ="));

                float wakeVal1 = float.Parse(wakeVin1.Split('=')[1].Trim().TrimEnd(';'), CultureInfo.InvariantCulture);
                float wakeVal05 = float.Parse(wakeVin05.Split('=')[1].Trim().TrimEnd(';'), CultureInfo.InvariantCulture);

                Assert.Equal(wakeVal1 * 0.5f, wakeVal05, 4);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        // =====================================================================
        // TODO-46: SU2 in 2D & Anstellwinkel Tests
        // =====================================================================

        [Fact]
        public void Su2ConfigGenerator_ResolveVelocityVector_2D_And_3D()
        {
            float speed = 50.0f;
            float alpha = 10.0f;
            float rad = alpha * (MathF.PI / 180.0f);
            float expectedVx = speed * MathF.Cos(rad);
            float expectedVLift = speed * MathF.Sin(rad);

            // In 2D: Auftrieb auf +y -> (vx, vy, 0)
            var v2D = Su2ConfigGenerator.ResolveVelocityVector(speed, alpha, dimensions: 2);
            Assert.Equal(expectedVx, v2D.Vx, 4);
            Assert.Equal(expectedVLift, v2D.Vy, 4);
            Assert.Equal(0.0f, v2D.Vz);

            // In 3D: Auftrieb auf +z -> (vx, 0, vz)
            var v3D = Su2ConfigGenerator.ResolveVelocityVector(speed, alpha, dimensions: 3);
            Assert.Equal(expectedVx, v3D.Vx, 4);
            Assert.Equal(0.0f, v3D.Vy);
            Assert.Equal(expectedVLift, v3D.Vz, 4);

            // Bei alpha = 0: exakt (speed, 0, 0)
            var vZero = Su2ConfigGenerator.ResolveVelocityVector(speed, 0.0f, dimensions: 3);
            Assert.Equal(speed, vZero.Vx);
            Assert.Equal(0.0f, vZero.Vy);
            Assert.Equal(0.0f, vZero.Vz);
        }

        [Fact]
        public void Su2ConfigGenerator_ProjectForceToWindAxes_Calculates_Lift_And_Drag()
        {
            // Bei alpha = 0: Windachsen = Körperachsen (D = Fx, L = Fy)
            var fBody = new Vector2(10.0f, 100.0f);
            var wind0 = Su2ConfigGenerator.ProjectForceToWindAxes(fBody, 0.0f);
            Assert.Equal(10.0f, wind0.X, 4);
            Assert.Equal(100.0f, wind0.Y, 4);

            // Bei alpha = 90°: D = Fy, L = -Fx
            var wind90 = Su2ConfigGenerator.ProjectForceToWindAxes(fBody, 90.0f);
            Assert.Equal(100.0f, wind90.X, 4);
            Assert.Equal(-10.0f, wind90.Y, 4);

            // Bei alpha = 10°: D = Fx*cos(a) + Fy*sin(a), L = -Fx*sin(a) + Fy*cos(a)
            float rad = 10.0f * (MathF.PI / 180.0f);
            float expD = (10f * MathF.Cos(rad)) + (100f * MathF.Sin(rad));
            float expL = (-10f * MathF.Sin(rad)) + (100f * MathF.Cos(rad));
            var wind10 = Su2ConfigGenerator.ProjectForceToWindAxes(fBody, 10.0f);
            Assert.Equal(expD, wind10.X, 4);
            Assert.Equal(expL, wind10.Y, 4);
        }

        [Fact]
        public void Su2ConfigGenerator_Generates_Rotated_Inflow_In_2D_And_3D()
        {
            string path2D = Path.Combine(Path.GetTempPath(), "su2_2d_" + Guid.NewGuid().ToString("N") + ".cfg");
            string path3D = Path.Combine(Path.GetTempPath(), "su2_3d_" + Guid.NewGuid().ToString("N") + ".cfg");

            try
            {
                var config = SimulationConfig.CreateDefault();
                var options2D = new Su2SolverOptions
                {
                    Dimensions = 2,
                    FreestreamVelocity = 50.0f,
                    AngleOfAttackDeg = 5.0f
                };

                Su2ConfigGenerator.Generate(path2D, "/tmp/mesh.su2", 0.05f, config, options2D);
                string[] lines2D = File.ReadAllLines(path2D);

                // In 2D: Vy > 0, Vz == 0
                string velLine2D = lines2D.First(l => l.StartsWith("FREESTREAM_VELOCITY="));
                Assert.EndsWith(", 0 )", velLine2D); // Vz ist 0
                Assert.Contains("AOA= 5", lines2D);

                var options3D = new Su2SolverOptions
                {
                    Dimensions = 3,
                    FreestreamVelocity = 50.0f,
                    AngleOfAttackDeg = 5.0f
                };

                Su2ConfigGenerator.Generate(path3D, "/tmp/mesh.su2", 0.05f, config, options3D);
                string[] lines3D = File.ReadAllLines(path3D);

                // In 3D: Vy == 0, Vz > 0
                string velLine3D = lines3D.First(l => l.StartsWith("FREESTREAM_VELOCITY="));
                Assert.Contains(", 0, ", velLine3D); // Vy ist 0
                Assert.Contains("AOA= 5", lines3D);
            }
            finally
            {
                if (File.Exists(path2D)) File.Delete(path2D);
                if (File.Exists(path3D)) File.Delete(path3D);
            }
        }

        [Fact]
        public void Su2Solver_ResolveReferenceArea_In_2D_Converts_Mm_To_M()
        {
            var options2D = new Su2SolverOptions
            {
                Dimensions = 2,
                ReferenceAreaMetric = "Chord"
            };
            var solver2D = new Su2Solver(options2D);

            var record2D = new ModelRecord();
            record2D.PassiveParameters["Chord"] = 50.0f; // 50 mm Sehne

            // In 2D: REF_AREA = Sehne je Einheitsspannweite (1 m): 50 mm * 0.001 = 0.05 m²
            float refArea2D = solver2D.ResolveReferenceArea(record2D);
            Assert.Equal(0.05f, refArea2D, 5);

            // In 3D: REF_AREA = Fläche in mm² * 1e-6: 50 mm² * 1e-6 = 0.00005 m²
            var options3D = new Su2SolverOptions
            {
                Dimensions = 3,
                ReferenceAreaMetric = "FrontalArea"
            };
            var solver3D = new Su2Solver(options3D);

            var record3D = new ModelRecord();
            record3D.PassiveParameters["FrontalArea"] = 50.0f;

            float refArea3D = solver3D.ResolveReferenceArea(record3D);
            Assert.Equal(0.00005f, refArea3D, 7);
        }

        [Fact]
        public void Su2Solver_2D_Geometry_Loads_Bounds_From_Contour_Json()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "su2_2d_bounds_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string contourPath = Path.Combine(tempDir, "airfoil.json");
                string meshPath = Path.Combine(tempDir, "airfoil.su2");
                File.WriteAllText(meshPath, "mock mesh");

                var airfoilPts = CreateAirfoilContour(80.0f); // 80 mm Sehne
                var shape = new Shape2D().AddPolygon(airfoilPts);
                shape.SaveContour(contourPath, cornerAngleDeg: 35.0f);

                var record = new ModelRecord
                {
                    Iteration = 1,
                    Variant = 1,
                    StlPath = contourPath
                };
                record.PassiveParameters["Chord"] = 80.0f;

                var options = new Su2SolverOptions
                {
                    Dimensions = 2,
                    FreestreamVelocity = 50.0f,
                    Density = 1.225f,
                    DynamicViscosity = 1.789e-5f
                };

                var solver = new Su2Solver(options);
                var config = SimulationConfig.CreateDefault();
                config.MpiRunPath = "non_existent_binary";

                try
                {
                    solver.Solve(meshPath, record, config, tempDir);
                }
                catch
                {
                    // Erwarteter Abbruch beim Starten des MPI-Prozesses
                }

                Assert.True(record.PassiveParameters.ContainsKey("ReynoldsNumber"));
                // Re = rho * U * L / mu = 1.225 * 50 * 0.08 / 1.789e-5 = ~2.7389e5
                float re = record.PassiveParameters["ReynoldsNumber"];
                Assert.InRange(re, 2.7e5f, 2.8e5f);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Su2Solver_Active_AngleOfAttack_Overrides_Options_And_Records_PassiveParameter()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "su2_aoa_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                string meshPath = Path.Combine(tempDir, "airfoil.su2");
                File.WriteAllText(meshPath, "mock mesh");

                var record = new ModelRecord
                {
                    Iteration = 1,
                    Variant = 1,
                    ActiveParameters = new Dictionary<string, float>
                    {
                        { "AngleOfAttackDeg", 7.5f }
                    }
                };

                var options = new Su2SolverOptions
                {
                    Dimensions = 2,
                    AngleOfAttackDeg = 0.0f,
                    FreestreamVelocity = 50.0f
                };

                var solver = new Su2Solver(options);
                var config = SimulationConfig.CreateDefault();
                config.MpiRunPath = "non_existent_mpi";

                try
                {
                    solver.Solve(meshPath, record, config, tempDir);
                }
                catch
                {
                    // Erwarteter Abbruch beim Starten des MPI-Prozesses
                }

                // 1. Passive Parameter enthält den Anstellwinkel
                Assert.True(record.PassiveParameters.ContainsKey("AngleOfAttackDeg"));
                Assert.Equal(7.5f, record.PassiveParameters["AngleOfAttackDeg"]);

                // 2. Base Options wurden nicht dauerhaft mutiert
                Assert.Equal(0.0f, solver.Options.AngleOfAttackDeg);

                // 3. current_config.cfg wurde mit 7.5 erzeugt
                string cfgPath = Path.Combine(tempDir, "current_config.cfg");
                Assert.True(File.Exists(cfgPath));
                string[] cfgLines = File.ReadAllLines(cfgPath);
                Assert.Contains(cfgLines, l => l.Contains("AOA= 7.5") || l.Contains("AOA= 7,5"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        private class Test2DProject : Cfd2DProjectDefinition
        {
            public override string Name => "Test2D";
            public override ProjectConfig CreateDefaults() => new ProjectConfig();
            public override IGeometryGenerator CreateGeometry(ProjectContext context) => throw new NotImplementedException();
            public override IFitnessCalculator CreateFitness(ProjectContext context) => throw new NotImplementedException();
        }

        [Fact]
        public void Cfd2DProjectDefinition_Uses_DirectGeometryKernel_And_Wires_2D_Stages()
        {
            var project = new Test2DProject();

            // 1. Kernel muss DirectGeometryKernel sein (kein PicoGK, kein Xvfb!)
            var kernel = project.CreateKernel();
            Assert.IsType<DirectGeometryKernel>(kernel);

            // 2. CreateStages muss Gmsh2DMesher und Su2Solver mit Dimensions = 2 liefern
            var simConfig = SimulationConfig.CreateDefault();
            var projConfig = new ProjectConfig();
            var context = new ProjectContext("Test2D", null, null, simConfig, projConfig);

            var stages = project.CreateStages(context);
            Assert.Single(stages);

            var mesher = Assert.IsType<Gmsh2DMesher>(stages[0].Mesher);
            var solver = Assert.IsType<Su2Solver>(stages[0].Solver);

            Assert.Equal(2, solver.Options.Dimensions);
            Assert.NotNull(mesher.Flow);
            Assert.NotNull(solver.Flow);
            Assert.Equal(mesher.Flow.Velocity, solver.Flow.Velocity);
        }
    }
}
