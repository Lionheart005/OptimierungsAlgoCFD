using System;
using System.IO;
using System.Numerics;
using Xunit;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Tests für TODO-40 (Strukturierte Grenzschicht mit BoundaryLayerCalculator und GmshCfdMesher)
    /// und TODO-41 (y+-Auswertung und -Prüfung nach jeder CFD-Rechnung).
    /// </summary>
    public class BoundaryLayerAndYPlusTests : IDisposable
    {
        private readonly string _workingDirectory;

        public BoundaryLayerAndYPlusTests()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), "bl_yplus_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDirectory))
            {
                try { Directory.Delete(_workingDirectory, recursive: true); } catch { }
            }
        }

        // ===================================================================
        //  TODO-40: BoundaryLayerCalculator
        // ===================================================================

        [Fact]
        public void BoundaryLayerCalculator_NacaBenchmark_Matches_Theory()
        {
            // NACA-Profil in Luft: rho = 1.225, mu = 1.789e-5, U = 68.64 m/s, c = 50 mm, y+ = 1.0, r = 1.2
            float density = 1.225f;
            float velocity = 68.64f;
            float dynamicViscosity = 1.789e-5f;
            float chordMm = 50.0f;

            var spec = BoundaryLayerCalculator.Calculate(
                density, velocity, dynamicViscosity, chordMm,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 4);

            // Re_L = 1.225 * 68.64 * 0.05 / 1.789e-5 = ~2.35e5
            Assert.InRange(spec.ReynoldsNumber, 2.34e5f, 2.36e5f);
            // Erste Zellhöhe y1 ≈ 0.0044 - 0.0046 mm
            Assert.InRange(spec.FirstLayerHeightMm, 0.0043f, 0.0047f);
            // Schichtanzahl N ≈ 24
            Assert.Equal(24, spec.LayerCount);
            // Grenzschichtdicke delta ≈ 1.56 mm
            Assert.InRange(spec.BoundaryLayerThicknessMm, 1.5f, 1.7f);
            // Gesamtdicke deckt delta ab
            Assert.True(spec.TotalThicknessMm >= spec.BoundaryLayerThicknessMm);
        }

        [Fact]
        public void BoundaryLayerCalculator_MantaAuvBenchmark_Matches_Theory()
        {
            // MantaAuv in Wasser: rho = 1025, mu = 1.001e-3, U = 2.0 m/s, L = 50 mm, y+ = 1.0, r = 1.2
            float density = 1025.0f;
            float velocity = 2.0f;
            float dynamicViscosity = 1.001e-3f;
            float lengthMm = 50.0f;

            var spec = BoundaryLayerCalculator.Calculate(
                density, velocity, dynamicViscosity, lengthMm,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 4);

            // Re_L = 1025 * 2 * 0.05 / 1.001e-3 = ~1.024e5
            Assert.InRange(spec.ReynoldsNumber, 1.02e5f, 1.03e5f);
            // Erste Zellhöhe y1 ≈ 0.0098 mm (≈ 0.010 mm)
            Assert.InRange(spec.FirstLayerHeightMm, 0.0095f, 0.0105f);
            // Schichtanzahl N ≈ 20-21
            Assert.InRange(spec.LayerCount, 20, 22);
            // Grenzschichtdicke delta ≈ 1.84 mm
            Assert.InRange(spec.BoundaryLayerThicknessMm, 1.75f, 1.95f);
            Assert.True(spec.TotalThicknessMm >= spec.BoundaryLayerThicknessMm);
        }

        [Fact]
        public void BoundaryLayerCalculator_Enforces_MinLayers_And_FixedLayers()
        {
            // minLayers greift, wenn automatische Schichten kleiner wären
            var specMin = BoundaryLayerCalculator.Calculate(
                1025f, 2.0f, 0.001001f, 50.0f,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 30);
            Assert.Equal(30, specMin.LayerCount);

            // fixedLayers überschreibt automatische Berechnung
            var specFixed = BoundaryLayerCalculator.Calculate(
                1025f, 2.0f, 0.001001f, 50.0f,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 4, fixedLayers: 12);
            Assert.Equal(12, specFixed.LayerCount);

            // fixedLayers kleiner als minLayers wird auf minLayers angehoben
            var specFixedMin = BoundaryLayerCalculator.Calculate(
                1025f, 2.0f, 0.001001f, 50.0f,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 8, fixedLayers: 3);
            Assert.Equal(8, specFixedMin.LayerCount);
        }

        [Theory]
        [InlineData(-1f, 1f, 1e-3f, 50f, 1f, 1.2f, 4, 0)]  // Dichte negativ
        [InlineData(0f, 1f, 1e-3f, 50f, 1f, 1.2f, 4, 0)]   // Dichte 0
        [InlineData(1000f, -1f, 1e-3f, 50f, 1f, 1.2f, 4, 0)] // Geschwindigkeit negativ
        [InlineData(1000f, 1f, 0f, 50f, 1f, 1.2f, 4, 0)]   // Viskosität 0
        [InlineData(1000f, 1f, 1e-3f, 0f, 1f, 1.2f, 4, 0)]  // Länge 0
        [InlineData(1000f, 1f, 1e-3f, 50f, 0f, 1.2f, 4, 0)] // Ziel-y+ 0
        [InlineData(1000f, 1f, 1e-3f, 50f, 1f, 1.0f, 4, 0)] // growthRate 1.0
        [InlineData(1000f, 1f, 1e-3f, 50f, 1f, 1.2f, 0, 0)] // minLayers 0
        [InlineData(1000f, 1f, 1e-3f, 50f, 1f, 1.2f, 4, -1)] // fixedLayers negativ
        public void BoundaryLayerCalculator_Throws_ArgumentOutOfRangeException_On_Invalid_Inputs(
            float rho, float u, float mu, float l, float yPlus, float r, int minL, int fixedL)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BoundaryLayerCalculator.Calculate(rho, u, mu, l, yPlus, r, minL, fixedL));
        }

        // ===================================================================
        //  TODO-40: GmshCfdMesher .geo Generation für Hexa, Prism, Threshold
        // ===================================================================

        [Fact]
        public void WriteGeoScript_HexaMode_Generates_ClassifySurfaces_And_RecombineExtrude()
        {
            string path = Path.Combine(_workingDirectory, "hexa.geo");
            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = "Hexa",
                TargetYPlus = 1.0f,
                BoundaryLayerMinLayers = 5,
                ClassifyAngleDeg = 45.0f,
                SurfaceCellSize = 2.5f
            };
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var mesher = new GmshCfdMesher(options, flow);
            var config = SimulationConfig.CreateDefault();

            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, scaleFactor: 1.0f);

            // Zwei Schritte (Servertest 06.11.2026): Wandoberfläche, dann Wandschicht + Volumen.
            string wallPath = GmshCfdMesher.WallScriptPath(path);
            Assert.Equal(Path.Combine(_workingDirectory, "Wall_hexa.geo"), wallPath);
            string[] wall = File.ReadAllLines(wallPath);
            string[] lines = File.ReadAllLines(path);

            // Schritt 1: klassifizieren, Vierecke NUR auf der Wand, als eine Fläche speichern
            Assert.Contains(wall, l => l.Contains("ClassifySurfaces{"));
            Assert.Contains(wall, l => l.Contains("CreateGeometry;"));
            Assert.Contains(wall, l => l.Trim() == "Recombine Surface{wall[]};");
            Assert.Contains(wall, l => l.Trim() == "Mesh.Algorithm = 8; // Frontal-Delaunay for Quads");
            Assert.Contains(wall, l => l.Trim() == "Mesh.RecombinationAlgorithm = 2;");
            Assert.Contains(wall, l => l.Trim() == "Mesh.MeshSizeMax = 2.5;");
            Assert.Contains(wall, l => l.Trim() == "Physical Surface(1) = {wall[]};");
            Assert.Contains(wall, l => l.Trim() == "Mesh.SaveElementTagType = 2;");
            // RecombineAll gälte global (auch für den Kanal) und überschrieb sich in Tests gegenseitig
            Assert.DoesNotContain(wall.Concat(lines), l => !l.TrimStart().StartsWith("//") && l.Contains("RecombineAll"));

            // Schritt 2: Wand einlesen, extrudieren, DANACH den Kanal, Volumen auf den Oberseiten
            int mergeWall = Array.FindIndex(lines, l => l.StartsWith("Merge \"") && l.Contains("Wall_hexa.mesh"));
            int extrude = Array.FindIndex(lines, l => l.Contains("Extrude { Surface{wall[]}; Layers{") && l.Contains("Recombine;"));
            int mergeTunnel = Array.FindIndex(lines, l => l.Trim() == "Merge \"tunnel.stl\";");
            Assert.True(mergeWall >= 0 && extrude > mergeWall && mergeTunnel > extrude,
                $"Reihenfolge Wand ({mergeWall}) < Extrusion ({extrude}) < Kanal ({mergeTunnel}) verletzt.");
            Assert.Contains(lines, l => l.Contains("Surface Loop(sl_bl) = {tops[]};"));
            Assert.Contains(lines, l => l.Trim() == "Field[2].SizeMin = 2.5;");
            // Globales Maximum = Fernfeldgröße, NICHT die Wandgröße (sonst rechnet Gmsh endlos)
            Assert.Contains(lines, l => l.Trim() == "Mesh.MeshSizeMax = 120;");
        }

        [Fact]
        public void WriteGeoScript_LayerOverride_Drops_Outer_Layers()
        {
            string path = Path.Combine(_workingDirectory, "Meshing_Gen1_Var1.geo");
            var options = new GmshMesherOptions { BoundaryLayerMode = "Prism", TargetYPlus = 1.0f };
            var mesher = new GmshCfdMesher(options, new FlowConditions(2.0f, 1025f, 0.001001f, "test"));
            var config = SimulationConfig.CreateDefault();

            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f, 1.0f, layerCountOverride: 5);

            string extrude = File.ReadAllLines(path).Single(l => l.StartsWith("Extrude {"));
            Assert.Contains("Layers{ {1, 1, 1, 1, 1}, {", extrude);
            Assert.Equal(Path.Combine(_workingDirectory, "Wall_Gen1_Var1.geo"), GmshCfdMesher.WallScriptPath(path));
            Assert.Equal(Path.Combine(_workingDirectory, "Wall_Gen1_Var1.mesh"), GmshCfdMesher.WallMeshPath(path));
        }

        [Fact]
        public void NextLayerCount_Drops_A_Quarter_Each_Time()
        {
            // Manta (Servertest 06.11.2026): 21 und 16 Schichten durchdringen sich, 12 gehen.
            var seq = new List<int> { 21 };
            while (seq[^1] > 4) seq.Add(GmshCfdMesher.NextLayerCount(seq[^1]));
            Assert.Equal(new[] { 21, 16, 12, 9, 7, 6, 5, 4 }, seq);
        }

        [Fact]
        public void WriteGeoScript_PrismMode_Generates_Extrude_Without_RecombineAll()
        {
            string path = Path.Combine(_workingDirectory, "prism.geo");
            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                TargetYPlus = 1.0f
            };
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var mesher = new GmshCfdMesher(options, flow);
            var config = SimulationConfig.CreateDefault();

            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, scaleFactor: 1.0f);

            string[] wall = File.ReadAllLines(GmshCfdMesher.WallScriptPath(path));
            string[] lines = File.ReadAllLines(path);

            // Schritt 1: klassifizieren, aber Dreiecke behalten (keine Vierecke)
            Assert.Contains(wall, l => l.Contains("ClassifySurfaces{"));
            Assert.DoesNotContain(wall, l => l.Contains("Recombine"));
            // Schritt 2: Extrusion MIT "Recombine;" – bei Dreiecken heißt das Prismen statt je drei
            // Tetraedern; ohne das Schlüsselwort bricht Gmsh ab ("Unable to subdivide extruded mesh").
            Assert.Contains(lines, l => l.Contains("Extrude { Surface{wall[]}; Layers{") && l.Contains("Recombine;"));
            Assert.DoesNotContain(lines, l => l.Contains("RecombineAll"));
        }

        [Fact]
        public void WriteGeoScript_UnknownMode_Throws_InvalidOperationException()
        {
            string path = Path.Combine(_workingDirectory, "invalid.geo");
            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = "UnknownMode"
            };
            var mesher = new GmshCfdMesher(options);
            var config = SimulationConfig.CreateDefault();

            Assert.Throws<InvalidOperationException>(() =>
                mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, scaleFactor: 1.0f));
        }

        [Fact]
        public void ReadSu2BoundaryLayerHexShare_Computes_Correct_Share()
        {
            string meshPath = Path.Combine(_workingDirectory, "sample.su2");
            // SU2 format sample: 3 Hexa (12), 1 Prism (13), 2 Tetra (10)
            string content = @"NDIME= 3
NELEM= 6
10 0 1 2 3
12 0 1 2 3 4 5 6 7
12 1 2 3 4 5 6 7 8
12 2 3 4 5 6 7 8 9
13 0 1 2 3 4 5
10 4 5 6 7
NPOIN= 10
";
            File.WriteAllText(meshPath, content);

            float share = GmshCfdMesher.ReadSu2BoundaryLayerHexShare(meshPath);

            // 3 Hexa / (3 Hexa + 1 Prism) = 0.75 (75 %)
            Assert.Equal(0.75f, share, precision: 3);
        }

        [Fact]
        public void ReadSu2BoundaryLayerHexShare_Returns_NaN_When_No_Boundary_Elements()
        {
            string meshPath = Path.Combine(_workingDirectory, "tetra_only.su2");
            string content = @"NDIME= 3
NELEM= 2
10 0 1 2 3
10 1 2 3 4
NPOIN= 5
";
            File.WriteAllText(meshPath, content);

            float share = GmshCfdMesher.ReadSu2BoundaryLayerHexShare(meshPath);
            Assert.True(float.IsNaN(share));
        }

        // ===================================================================
        //  TODO-41: Su2SurfaceAnalyzer & y+-Prüfung
        // ===================================================================

        [Fact]
        public void EvaluateSurfaceCsv_Computes_Accurate_Statistics()
        {
            string[] lines = new[]
            {
                "\"PointID\", \"x\", \"y\", \"z\", \"Y_Plus\", \"Pressure\"",
                "0, 0.0, 0.0, 0.0, 0.5, 101325.0",
                "1, 0.1, 0.0, 0.0, 1.0, 101320.0",
                "2, 0.2, 0.0, 0.0, 1.5, 101315.0",
                "3, 0.3, 0.0, 0.0, 2.0, 101310.0",
                "4, 0.4, 0.0, 0.0, 4.0, 101300.0"
            };

            var stats = Su2SurfaceAnalyzer.EvaluateSurfaceCsv(lines);

            Assert.NotNull(stats);
            Assert.Equal(5, stats.SampleCount);
            Assert.Equal(0.5f, stats.Min, 3);
            Assert.Equal(1.5f, stats.Median, 3);
            Assert.Equal(4.0f, stats.Max, 3);
            // P95 bei 5 Werten: index = 0.95 * 4 = 3.8 -> 2.0 * 0.2 + 4.0 * 0.8 = 3.6
            Assert.InRange(stats.P95, 3.5f, 3.7f);
        }

        [Fact]
        public void EvaluateSurfaceCsv_Returns_Null_When_Column_Missing_Or_No_Data()
        {
            string[] linesWithoutYPlus = new[]
            {
                "\"PointID\", \"x\", \"y\", \"z\", \"Pressure\"",
                "0, 0.0, 0.0, 0.0, 101325.0"
            };
            Assert.Null(Su2SurfaceAnalyzer.EvaluateSurfaceCsv(linesWithoutYPlus));

            string[] emptyLines = Array.Empty<string>();
            Assert.Null(Su2SurfaceAnalyzer.EvaluateSurfaceCsv(emptyLines));
        }

        [Fact]
        public void EvaluateSurfaceVtu_Parses_Ascii_PointData_Array()
        {
            string vtuPath = Path.Combine(_workingDirectory, "sample_surface.vtu");
            string content = @"<?xml version=""1.0""?>
<VTKFile type=""PolyData"" version=""0.1"" byte_order=""LittleEndian"">
  <PolyData>
    <Piece NumberOfPoints=""3"" NumberOfPolys=""1"">
      <PointData>
        <DataArray type=""Float32"" Name=""Y_Plus"" format=""ascii"">
          0.8 1.2 2.4
        </DataArray>
      </PointData>
    </Piece>
  </PolyData>
</VTKFile>";
            File.WriteAllText(vtuPath, content);

            var stats = Su2SurfaceAnalyzer.EvaluateSurfaceVtu(vtuPath);

            Assert.NotNull(stats);
            Assert.Equal(3, stats.SampleCount);
            Assert.Equal(0.8f, stats.Min, 2);
            Assert.Equal(1.2f, stats.Median, 2);
            Assert.Equal(2.4f, stats.Max, 2);
        }

        [Fact]
        public void Su2Solver_EvaluateSurfaceYPlus_Records_PassiveParameters_And_Warns_On_High_YPlus()
        {
            string csvPath = Path.Combine(_workingDirectory, "surface.csv");
            string[] csvLines = new[]
            {
                "\"PointID\", \"Y_Plus\"",
                "0, 1.0",
                "1, 1.5",
                "2, 2.8" // P95 > 2.0
            };
            File.WriteAllLines(csvPath, csvLines);

            var options = new Su2SolverOptions
            {
                TargetYPlus = 1.0f,
                YPlusWarnFactor = 2.0f // Warnschwelle = 2.0
            };
            var solver = new Su2Solver(options);
            var record = new ModelRecord();

            using var sw = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(sw);
            try
            {
                var stats = solver.EvaluateSurfaceYPlus(csvPath, record);

                Assert.NotNull(stats);
                Assert.True(record.PassiveParameters.ContainsKey("YPlus_Median"));
                Assert.True(record.PassiveParameters.ContainsKey("YPlus_P95"));
                Assert.True(record.PassiveParameters.ContainsKey("YPlus_Max"));

                Assert.Equal(1.5f, record.PassiveParameters["YPlus_Median"]);
                Assert.Equal(2.8f, record.PassiveParameters["YPlus_Max"]);

                string consoleLog = sw.ToString();
                Assert.Contains("[WARNUNG] y+", consoleLog);
                Assert.Contains("überschreitet das Limit", consoleLog);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Fact]
        public void Su2Solver_EvaluateSurfaceYPlus_Silent_When_YPlus_Within_Limit()
        {
            string csvPath = Path.Combine(_workingDirectory, "surface_ok.csv");
            string[] csvLines = new[]
            {
                "\"PointID\", \"Y_Plus\"",
                "0, 0.9",
                "1, 1.1",
                "2, 1.3" // P95 < 2.0
            };
            File.WriteAllLines(csvPath, csvLines);

            var options = new Su2SolverOptions
            {
                TargetYPlus = 1.0f,
                YPlusWarnFactor = 2.0f
            };
            var solver = new Su2Solver(options);
            var record = new ModelRecord();

            using var sw = new StringWriter();
            var originalOut = Console.Out;
            Console.SetOut(sw);
            try
            {
                solver.EvaluateSurfaceYPlus(csvPath, record);
                string consoleLog = sw.ToString();
                Assert.DoesNotContain("[WARNUNG] y+", consoleLog);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Fact]
        public void GenerateMesh_On_Gmsh_Failure_Copies_Geo_To_FehlerLogs()
        {
            // Threshold: ein einziges .geo. Bei "Prism" (Standard) scheitert zuerst das Wandflächen-Skript.
            var options = new GmshMesherOptions { BoundaryLayerMode = "Threshold" };
            var mesher = new GmshCfdMesher(options);
            var config = SimulationConfig.CreateDefault();
            config.GmshPath = "non_existent_gmsh_binary_12345";

            string stlPath = Path.Combine(_workingDirectory, "model_fail.stl");
            var triangles = new[] { new StlTriangle(Vector3.Zero, new Vector3(10, 0, 0), new Vector3(0, 10, 0)) };
            StlWriter.WriteBinary(stlPath, triangles);

            var request = new MeshRequest(stlPath, 1, 1, config, 1.0f, _workingDirectory);

            Assert.ThrowsAny<Exception>(() => mesher.GenerateMesh(request));

            string errorLogDir = Path.Combine(_workingDirectory, "FehlerLogs");
            Assert.True(Directory.Exists(errorLogDir), "FehlerLogs-Ordner wurde nicht erstellt.");
            string copiedGeo = Path.Combine(errorLogDir, "Meshing_Gen1_Var1.geo");
            Assert.True(File.Exists(copiedGeo), ".geo-Skript wurde bei Fehlschlag nicht nach FehlerLogs kopiert.");
        }
    }
}
