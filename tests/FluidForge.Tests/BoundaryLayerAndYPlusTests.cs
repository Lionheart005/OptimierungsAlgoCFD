using System;
using System.IO;
using System.Linq;
using System.Numerics;
using Xunit;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Tests for TODO-40 (structured boundary layer with BoundaryLayerCalculator and GmshCfdMesher)
    /// and TODO-41 (y+ evaluation and check after every CFD run).
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
            // NACA airfoil in air: rho = 1.225, mu = 1.789e-5, U = 68.64 m/s, c = 50 mm, y+ = 1.0, r = 1.2
            float density = 1.225f;
            float velocity = 68.64f;
            float dynamicViscosity = 1.789e-5f;
            float chordMm = 50.0f;

            var spec = BoundaryLayerCalculator.Calculate(
                density, velocity, dynamicViscosity, chordMm,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 4);

            // Re_L = 1.225 * 68.64 * 0.05 / 1.789e-5 = ~2.35e5
            Assert.InRange(spec.ReynoldsNumber, 2.34e5f, 2.36e5f);
            // First cell height y1 ≈ 0.0044 - 0.0046 mm
            Assert.InRange(spec.FirstLayerHeightMm, 0.0043f, 0.0047f);
            // Layer count N ≈ 24
            Assert.Equal(24, spec.LayerCount);
            // Boundary layer thickness delta ≈ 1.56 mm
            Assert.InRange(spec.BoundaryLayerThicknessMm, 1.5f, 1.7f);
            // Total thickness covers delta
            Assert.True(spec.TotalThicknessMm >= spec.BoundaryLayerThicknessMm);
        }

        [Fact]
        public void BoundaryLayerCalculator_MantaAuvBenchmark_Matches_Theory()
        {
            // MantaAuv in water: rho = 1025, mu = 1.001e-3, U = 2.0 m/s, L = 50 mm, y+ = 1.0, r = 1.2
            float density = 1025.0f;
            float velocity = 2.0f;
            float dynamicViscosity = 1.001e-3f;
            float lengthMm = 50.0f;

            var spec = BoundaryLayerCalculator.Calculate(
                density, velocity, dynamicViscosity, lengthMm,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 4);

            // Re_L = 1025 * 2 * 0.05 / 1.001e-3 = ~1.024e5
            Assert.InRange(spec.ReynoldsNumber, 1.02e5f, 1.03e5f);
            // First cell height y1 ≈ 0.0098 mm (≈ 0.010 mm)
            Assert.InRange(spec.FirstLayerHeightMm, 0.0095f, 0.0105f);
            // Layer count N ≈ 20-21
            Assert.InRange(spec.LayerCount, 20, 22);
            // Boundary layer thickness delta ≈ 1.84 mm
            Assert.InRange(spec.BoundaryLayerThicknessMm, 1.75f, 1.95f);
            Assert.True(spec.TotalThicknessMm >= spec.BoundaryLayerThicknessMm);
        }

        [Fact]
        public void ThreeDDefaults_Are_Moderately_Tightened_While_TwoDDefaults_Remain_Unchanged()
        {
            var gmsh3d = new GmshMesherOptions();
            var su2 = new Su2SolverOptions();
            var gmsh2d = new Gmsh2DMesherOptions();

            Assert.Equal(1.5f, gmsh3d.TargetYPlus);
            Assert.Equal(1.28f, gmsh3d.BoundaryLayerGrowthRate);
            Assert.Equal(1.5f, su2.TargetYPlus);

            Assert.Equal(1.0f, gmsh2d.TargetYPlus);
            Assert.Equal(1.2f, gmsh2d.BoundaryLayerGrowthRate);
        }

        [Fact]
        public void BoundaryLayerCalculator_MantaAuv_With_New3DDefaults_Produces_Moderate_LayerCount()
        {
            var defaults = new GmshMesherOptions();
            var spec = BoundaryLayerCalculator.Calculate(
                density: 1025.0f,
                velocity: 2.0f,
                dynamicViscosity: 0.001001f,
                characteristicLengthMm: 50.0f,
                targetYPlus: defaults.TargetYPlus,
                growthRate: defaults.BoundaryLayerGrowthRate,
                minLayers: defaults.BoundaryLayerMinLayers,
                fixedLayers: defaults.BoundaryLayerLayers,
                coverage: 1.0f);

            Assert.InRange(spec.LayerCount, 13, 17);
            Assert.Equal(15, spec.LayerCount);
            Assert.True(spec.TotalThicknessMm >= spec.BoundaryLayerThicknessMm);
            Assert.InRange(spec.FirstLayerHeightMm, 0.014f, 0.016f);
        }

        [Fact]
        public void BoundaryLayerCalculator_Enforces_MinLayers_And_FixedLayers()
        {
            // minLayers takes effect when automatic layers would be smaller
            var specMin = BoundaryLayerCalculator.Calculate(
                1025f, 2.0f, 0.001001f, 50.0f,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 30);
            Assert.Equal(30, specMin.LayerCount);

            // fixedLayers overrides automatic calculation
            var specFixed = BoundaryLayerCalculator.Calculate(
                1025f, 2.0f, 0.001001f, 50.0f,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 4, fixedLayers: 12);
            Assert.Equal(12, specFixed.LayerCount);

            // fixedLayers smaller than minLayers is raised to minLayers
            var specFixedMin = BoundaryLayerCalculator.Calculate(
                1025f, 2.0f, 0.001001f, 50.0f,
                targetYPlus: 1.0f, growthRate: 1.2f, minLayers: 8, fixedLayers: 3);
            Assert.Equal(8, specFixedMin.LayerCount);
        }

        [Theory]
        [InlineData(-1f, 1f, 1e-3f, 50f, 1f, 1.2f, 4, 0)]  // negative density
        [InlineData(0f, 1f, 1e-3f, 50f, 1f, 1.2f, 4, 0)]   // zero density
        [InlineData(1000f, -1f, 1e-3f, 50f, 1f, 1.2f, 4, 0)] // negative velocity
        [InlineData(1000f, 1f, 0f, 50f, 1f, 1.2f, 4, 0)]   // zero viscosity
        [InlineData(1000f, 1f, 1e-3f, 0f, 1f, 1.2f, 4, 0)]  // zero length
        [InlineData(1000f, 1f, 1e-3f, 50f, 0f, 1.2f, 4, 0)] // zero target y+
        [InlineData(1000f, 1f, 1e-3f, 50f, 1f, 1.0f, 4, 0)] // growthRate 1.0
        [InlineData(1000f, 1f, 1e-3f, 50f, 1f, 1.2f, 0, 0)] // minLayers 0
        [InlineData(1000f, 1f, 1e-3f, 50f, 1f, 1.2f, 4, -1)] // negative fixedLayers
        public void BoundaryLayerCalculator_Throws_ArgumentOutOfRangeException_On_Invalid_Inputs(
            float rho, float u, float mu, float l, float yPlus, float r, int minL, int fixedL)
        {
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BoundaryLayerCalculator.Calculate(rho, u, mu, l, yPlus, r, minL, fixedL));
        }

        // ===================================================================
        //  TODO-40: GmshCfdMesher .geo Generation for Hexa, Prism, Threshold
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

            // Two steps (server test 2026-11-06): wall surface, then wall layer + volume.
            string wallPath = GmshCfdMesher.WallScriptPath(path);
            Assert.Equal(Path.Combine(_workingDirectory, "Wall_hexa.geo"), wallPath);
            string[] wall = File.ReadAllLines(wallPath);
            string[] lines = File.ReadAllLines(path);

            // Step 1: classify, quads ONLY on the wall, save as one surface
            Assert.Contains(wall, l => l.Contains("ClassifySurfaces{"));
            Assert.Contains(wall, l => l.Contains("CreateGeometry;"));
            Assert.Contains(wall, l => l.Trim() == "Recombine Surface{wall[]};");
            Assert.Contains(wall, l => l.Trim() == "Mesh.Algorithm = 8; // Frontal-Delaunay for Quads");
            Assert.Contains(wall, l => l.Trim() == "Mesh.RecombinationAlgorithm = 2;");
            Assert.Contains(wall, l => l.Trim() == "Mesh.MeshSizeMax = 2.5;");
            Assert.Contains(wall, l => l.Trim() == "Physical Surface(1) = {wall[]};");
            Assert.Contains(wall, l => l.Trim() == "Mesh.SaveElementTagType = 2;");
            // RecombineAll would apply globally (including the tunnel) and overwrote each other in tests
            Assert.DoesNotContain(wall.Concat(lines), l => !l.TrimStart().StartsWith("//") && l.Contains("RecombineAll"));

            // Step 2: load wall, extrude, THEN tunnel, volume on the top faces
            int mergeWall = Array.FindIndex(lines, l => l.StartsWith("Merge \"") && l.Contains("Wall_hexa.mesh"));
            int extrude = Array.FindIndex(lines, l => l.Contains("Extrude { Surface{wall[]}; Layers{") && l.Contains("Recombine;"));
            int mergeTunnel = Array.FindIndex(lines, l => l.Trim() == "Merge \"tunnel.stl\";");
            Assert.True(mergeWall >= 0 && extrude > mergeWall && mergeTunnel > extrude,
                $"Order wall ({mergeWall}) < extrude ({extrude}) < tunnel ({mergeTunnel}) violated.");
            Assert.Contains(lines, l => l.Contains("Surface Loop(sl_bl) = {tops[]};"));
            Assert.Contains(lines, l => l.Trim() == "Field[2].SizeMin = 2.5;");
            // Global max = farfield size, NOT wall size (otherwise Gmsh runs endlessly)
            Assert.Contains(lines, l => l.Trim() == "Mesh.MeshSizeMax = 120;");
        }

        [Fact]
        public void WriteGeoScript_WallField_Is_Merged_First_And_Used_By_The_Extrusion()
        {
            // Wall field (direction x thickness per node): must be loaded as FIRST view (View[0]),
            // extrusion uses it, layer count stays as configured (no more layer loss).
            string path = Path.Combine(_workingDirectory, "Meshing_Gen1_Var1.geo");
            string field = GmshCfdMesher.WallFieldPath(path);
            var options = new GmshMesherOptions { BoundaryLayerMode = "Prism", TargetYPlus = 1.0f };
            var mesher = new GmshCfdMesher(options, new FlowConditions(2.0f, 1025f, 0.001001f, "test"));
            var config = SimulationConfig.CreateDefault();

            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f, 1.0f, field);
            string[] lines = File.ReadAllLines(path);
            string extrude = lines.Single(l => l.StartsWith("Extrude {"));

            Assert.Equal(Path.Combine(_workingDirectory, "Wall_Gen1_Var1.pos"), field);
            int mergeField = Array.FindIndex(lines, l => l.StartsWith("Merge \"") && l.Contains("Wall_Gen1_Var1.pos"));
            int mergeWall = Array.FindIndex(lines, l => l.StartsWith("Merge \"") && l.Contains("Wall_Gen1_Var1.mesh"));
            Assert.True(mergeField >= 0 && mergeField < mergeWall, "Wall field must be loaded BEFORE the wall (View[0]).");
            Assert.Contains("Using View[0]; Recombine;", extrude);

            // without wall field, Gmsh extrudes along raw normals (as before)
            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f, 1.0f);
            string plain = File.ReadAllLines(path).Single(l => l.StartsWith("Extrude {"));
            Assert.DoesNotContain("Using View", plain);
            Assert.Equal(extrude.Replace("Using View[0]; ", ""), plain);

            Assert.Equal(Path.Combine(_workingDirectory, "Wall_Gen1_Var1.geo"), GmshCfdMesher.WallScriptPath(path));
            Assert.Equal(Path.Combine(_workingDirectory, "Wall_Gen1_Var1.mesh"), GmshCfdMesher.WallMeshPath(path));
        }

        [Fact]
        public void WriteGeoScript_Layer_Count_Does_Not_Depend_On_The_Wall_Field()
        {
            // Plan wandschicht_zellzahl.md EZ1: identical layer count and heights for all variants and stages.
            string pathA = Path.Combine(_workingDirectory, "Meshing_A.geo");
            string pathB = Path.Combine(_workingDirectory, "Meshing_B.geo");
            var mesher = new GmshCfdMesher(new GmshMesherOptions { BoundaryLayerMode = "Prism" },
                new FlowConditions(2.0f, 1025f, 0.001001f, "test"));
            var config = SimulationConfig.CreateDefault();

            mesher.WriteGeoScript(pathA, "model.stl", "tunnel.stl", config, 1.0f, 1.0f);
            mesher.WriteGeoScript(pathB, "model.stl", "tunnel.stl", config, 1.0f, 1.0f, GmshCfdMesher.WallFieldPath(pathB));

            string a = File.ReadAllLines(pathA).Single(l => l.StartsWith("Extrude {"));
            string b = File.ReadAllLines(pathB).Single(l => l.StartsWith("Extrude {"));
            Assert.Equal(a.Replace("Using View[0]; ", ""), b.Replace("Using View[0]; ", ""));
        }

        [Fact]
        public void ExtractGmshError_Picks_The_Cause_And_Skips_The_Summary()
        {
            // Lines from the reproduction on 2026-11-09 (MantaAuv, Cal4, 21 layers)
            var lines = new[]
            {
                "Error   : PLC Error:  A segment and a facet intersect at point",
                "Error   : Invalid boundary mesh (segment-facet intersection) on surface 3, intersection (20.4584,17.4484,-2.67117)",
                "Error   : ------------------------------",
                "Error   : Mesh generation error summary",
                "Error   :     1 warning",
                "Error   :     2 errors",
                "Error   : Check the full log for details",
                "Error   : ------------------------------",
            };

            string? text = GmshCfdMesher.ExtractGmshError(lines);

            Assert.NotNull(text);
            Assert.StartsWith("PLC Error:", text);
            Assert.Contains("Invalid boundary mesh (segment-facet intersection)", text);
            Assert.DoesNotContain("summary", text);
            Assert.Null(GmshCfdMesher.ExtractGmshError(new[] { "Warning : 21 ill-shaped tets", "Info    : done" }));

            // Gmsh colors error lines (ESC[1m ESC[31m ... ESC[0m), even when output goes to a pipe
            string esc = ((char)27).ToString();
            string colored = esc + "[1m" + esc + "[31mError   : Invalid boundary mesh (segment-facet intersection)" + esc + "[0m";
            Assert.Equal("Invalid boundary mesh (segment-facet intersection)", GmshCfdMesher.ExtractGmshError(new[] { colored }));
            Assert.Null(GmshCfdMesher.ExtractGmshError(Array.Empty<string>()));
        }

        [Fact]
        public void Coverage_Makes_The_Layer_Reach_Beyond_The_Boundary_Layer()
        {
            var one = BoundaryLayerCalculator.Calculate(1025f, 2f, 0.001001f, 58f, 1f, 1.2f, 4, 0, 1.0f);
            var wide = BoundaryLayerCalculator.Calculate(1025f, 2f, 0.001001f, 58f, 1f, 1.2f, 4, 0, 1.5f);

            Assert.True(one.TotalThicknessMm >= one.BoundaryLayerThicknessMm);
            Assert.True(wide.TotalThicknessMm >= 1.5f * wide.BoundaryLayerThicknessMm);
            Assert.True(wide.LayerCount > one.LayerCount);
            Assert.Equal(one.FirstLayerHeightMm, wide.FirstLayerHeightMm, 6);
            Assert.Throws<ArgumentOutOfRangeException>(() =>
                BoundaryLayerCalculator.Calculate(1025f, 2f, 0.001001f, 58f, 1f, 1.2f, 4, 0, 0f));
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

            // Step 1: classify, but keep triangles (no quads)
            Assert.Contains(wall, l => l.Contains("ClassifySurfaces{"));
            Assert.DoesNotContain(wall, l => l.Contains("Recombine"));
            // Step 2: extrusion WITH "Recombine;" - for triangles this produces prisms instead of three
            // tetrahedra each; without this keyword Gmsh aborts ("Unable to subdivide extruded mesh").
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
        //  TODO-41: Su2SurfaceAnalyzer & y+ check
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
            // P95 for 5 values: index = 0.95 * 4 = 3.8 -> 2.0 * 0.2 + 4.0 * 0.8 = 3.6
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
                YPlusWarnFactor = 2.0f // Warning threshold = 2.0
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
                Assert.Contains($"{OutputConventions.WarningTag} y+", consoleLog);
                Assert.Contains("exceeds limit", consoleLog);
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
                Assert.DoesNotContain($"{OutputConventions.WarningTag} y+", consoleLog);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        [Fact]
        public void GenerateMesh_On_Gmsh_Failure_Copies_Geo_To_ErrorLogs()
        {
            // Threshold: a single .geo. With "Prism" (default), the wall-surface script fails first.
            var options = new GmshMesherOptions { BoundaryLayerMode = "Threshold" };
            var mesher = new GmshCfdMesher(options);
            var config = SimulationConfig.CreateDefault();
            config.GmshPath = "non_existent_gmsh_binary_12345";

            string stlPath = Path.Combine(_workingDirectory, "model_fail.stl");
            var triangles = new[] { new StlTriangle(Vector3.Zero, new Vector3(10, 0, 0), new Vector3(0, 10, 0)) };
            StlWriter.WriteBinary(stlPath, triangles);

            var request = new MeshRequest(stlPath, 1, 1, config, 1.0f, _workingDirectory);

            Assert.ThrowsAny<Exception>(() => mesher.GenerateMesh(request));

            string errorLogDir = Path.Combine(_workingDirectory, OutputConventions.ErrorLogsDirectory);
            Assert.True(Directory.Exists(errorLogDir), "Error logs directory was not created.");
            string copiedGeo = Path.Combine(errorLogDir, "Meshing_Gen1_Var1.geo");
            Assert.True(File.Exists(copiedGeo), ".geo script was not copied to error-logs on failure.");
        }

        [Fact]
        public void BuildClassifyAngleLadder_Deduplicates_And_Filters_Invalid_Angles()
        {
            Assert.Equal(new[] { 40f, 30f, 20f, 50f }, GmshCfdMesher.BuildClassifyAngleLadder(40f));
            Assert.Equal(new[] { 30f, 20f, 50f }, GmshCfdMesher.BuildClassifyAngleLadder(30f));
            Assert.Equal(new[] { 30f, 20f, 50f }, GmshCfdMesher.BuildClassifyAngleLadder(0f));
            Assert.Equal(new[] { 30f, 20f, 50f }, GmshCfdMesher.BuildClassifyAngleLadder(180f));
            Assert.Equal(new[] { 30f, 20f, 50f }, GmshCfdMesher.BuildClassifyAngleLadder(float.NaN));
        }

        [Fact]
        public void GenerateMesh_RetriesStep1WithClassifyAngleLadder_AndHealsWrongTopology()
        {
            string fakeGmshPath = CreateFakeGmshScriptForClassifyRetry();

            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                ClassifyAngleDeg = 40.0f
            };
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var mesher = new GmshCfdMesher(options, flow);

            var config = SimulationConfig.CreateDefault();
            config.GmshPath = fakeGmshPath;

            // Closed tetrahedron as model STL
            string stlPath = Path.Combine(_workingDirectory, "model_retry.stl");
            var p0 = Vector3.Zero;
            var p1 = new Vector3(10, 0, 0);
            var p2 = new Vector3(0, 10, 0);
            var p3 = new Vector3(0, 0, 10);
            var triangles = new[]
            {
                new StlTriangle(p0, p2, p1),
                new StlTriangle(p0, p1, p3),
                new StlTriangle(p1, p2, p3),
                new StlTriangle(p2, p0, p3)
            };
            StlWriter.WriteBinary(stlPath, triangles);

            var request = new MeshRequest(stlPath, 1, 1, config, 1.0f, _workingDirectory);
            string meshPath = mesher.GenerateMesh(request);

            Assert.True(File.Exists(meshPath));
            Assert.True(mesher.LastMeshMetrics.TryGetValue("BoundaryLayerClassifyAngleDeg", out float usedAngle));
            Assert.Equal(30.0f, usedAngle);
            // The final wall script contains the 30° angle (pi/6 ≈ 0.523599)
            string wallScript = GmshCfdMesher.WallScriptPath(Path.Combine(_workingDirectory, "Meshing_Gen1_Var1.geo"));
            Assert.Contains("0.52359", File.ReadAllText(wallScript));
        }

        private string CreateFakeGmshScriptForClassifyRetry()
        {
            string helperPyOrPs = Path.Combine(_workingDirectory, "fake_gmsh.ps1");
            string meditMesh = string.Join("\n", new[]
            {
                "MeshVersionFormatted 2",
                "Dimension",
                "3",
                "Vertices",
                "4",
                "0 0 0 1",
                "10 0 0 1",
                "0 10 0 1",
                "0 0 10 1",
                "Triangles",
                "4",
                "1 3 2 1",
                "1 2 4 1",
                "2 3 4 1",
                "3 1 4 1",
                "End"
            });
            string su2Mesh = "NDIME= 3\nNELEM= 1\n10 0 1 2 3 0\nNPOIN= 4\nNMARK= 0\n";

            if (OperatingSystem.IsWindows())
            {
                // PowerShell helper script invoked via .cmd wrapper (works reliably with quoted paths)
                File.WriteAllText(helperPyOrPs, $@"
$scriptPath = $args[0]
$outIdx = [Array]::IndexOf($args, '-o')
$outPath = $args[$outIdx + 1]
if ($args -contains '-2') {{
    $content = Get-Content -LiteralPath $scriptPath -Raw
    if ($content -match 'ClassifySurfaces\{{0\.698') {{
        Write-Output 'Error   : Wrong topology'
        exit 1
    }}
    Set-Content -LiteralPath $outPath -Value @'
{meditMesh}
'@ -Encoding ASCII
    exit 0
}}
Set-Content -LiteralPath $outPath -Value @'
{su2Mesh}
'@ -Encoding ASCII
exit 0
");
                string cmdPath = Path.Combine(_workingDirectory, "fake_gmsh.cmd");
                File.WriteAllText(cmdPath, $"@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"{helperPyOrPs}\" %*\r\n");
                return cmdPath;
            }
            else
            {
                string shPath = Path.Combine(_workingDirectory, "fake_gmsh.sh");
                File.WriteAllText(shPath, $@"#!/bin/sh
script=""$1""
out=""""
is2=0
prev=""""
for a in ""$@""; do
  if [ ""$a"" = ""-2"" ]; then is2=1; fi
  if [ ""$prev"" = ""-o"" ]; then out=""$a""; fi
  prev=""$a""
done
if [ ""$is2"" -eq 1 ]; then
  if grep -q 'ClassifySurfaces{{0\.698' ""$script""; then
    echo 'Error   : Wrong topology'
    exit 1
  fi
  cat <<'EOF' > ""$out""
{meditMesh}
EOF
  exit 0
fi
cat <<'EOF' > ""$out""
{su2Mesh}
EOF
exit 0
");
                File.SetUnixFileMode(shPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return shPath;
            }
        }
    }
}
