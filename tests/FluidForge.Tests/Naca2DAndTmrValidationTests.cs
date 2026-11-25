using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using FluidForge.Core;
using FluidForge.Kernels.Planar2D;
using FluidForge.Projects.Naca2D;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Tests for TODO-47: 2D NACA airfoil project Naca2D and validation against NASA TMR reference data.
    /// </summary>
    public class Naca2DAndTmrValidationTests
    {
        [Fact]
        public void Naca2DGeometryGenerator_GenerateAndExport_Creates_Contour_And_Svg_Without_PicoGK()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "naca2d_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var generator = new Naca2DGeometryGenerator(closedTrailingEdge: true, chordLength: 50.0f);
                var parameters = new Dictionary<string, float>
                {
                    { "MaxCamber", 0.0f },
                    { "CamberPosition", 40.0f },
                    { "MaxThickness", 12.0f }
                };

                var config = SimulationConfig.CreateDefault();
                var result = generator.GenerateAndExport(1, 1, parameters, tempDir, config);

                // 1. Result kind and paths
                Assert.Equal(GeometryKind.Contour2D, result.Kind);
                Assert.True(File.Exists(result.StlPath), $"Contour file not found: {result.StlPath}");
                Assert.EndsWith(".json", result.StlPath, StringComparison.OrdinalIgnoreCase);

                string svgPath = Path.Combine(tempDir, "Model_Gen1_Var1.svg");
                Assert.True(File.Exists(svgPath), $"SVG file not found: {svgPath}");

                // 2. Geometric metrics for NACA 0012 at c = 50 mm, t = 12 %
                // Chord = 50 mm, thickness = 0.12 * 50 = 6.0 mm, area ~ 0.685 * 0.12 * 2500 = 205.5 mm²
                Assert.True(result.Metrics.ContainsKey("Chord"));
                Assert.True(result.Metrics.ContainsKey("Thickness"));
                Assert.True(result.Metrics.ContainsKey("Area"));
                Assert.True(result.Metrics.ContainsKey("Perimeter"));
                Assert.True(result.Metrics.ContainsKey("FrontalArea"));

                float chord = result.Metrics["Chord"];
                float thickness = result.Metrics["Thickness"];
                float area = result.Metrics["Area"];
                float frontalArea = result.Metrics["FrontalArea"];

                Assert.InRange(chord, 49.8f, 50.2f);
                Assert.InRange(thickness, 5.9f, 6.1f);
                Assert.InRange(area, 200.0f, 210.0f);
                Assert.Equal(chord, frontalArea);

                // 3. Load contour file and verify corner detection
                ContourFile2D contourFile = Shape2D.LoadContour(result.StlPath);
                Assert.NotNull(contourFile.Contours);
                Assert.Single(contourFile.Contours);

                ContourLoop2D loop = contourFile.Contours[0];
                Assert.NotNull(loop.Points);
                Assert.True(loop.Points.Count > 100, $"Expected > 100 points, found: {loop.Points.Count}");

                // At the sharp trailing edge at least 1 corner point must be detected
                Assert.NotNull(loop.CornerIndices);
                Assert.NotEmpty(loop.CornerIndices);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Gmsh2DMesher_WriteGeoScript_On_Naca2D_Contour_Produces_Valid_Script()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "naca2d_gmsh_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var generator = new Naca2DGeometryGenerator(closedTrailingEdge: true, chordLength: 50.0f);
                var parameters = new Dictionary<string, float>
                {
                    { "MaxCamber", 0.0f },
                    { "CamberPosition", 40.0f },
                    { "MaxThickness", 12.0f }
                };

                var config = SimulationConfig.CreateDefault();
                var geomResult = generator.GenerateAndExport(1, 1, parameters, tempDir, config);

                string geoPath = Path.Combine(tempDir, "naca2d.geo");
                var mesher = new Gmsh2DMesher(new Gmsh2DMesherOptions());

                mesher.WriteGeoScript(geoPath, geomResult.StlPath, config, scaleFactor: 1.0f, refinementFactor: 1.0f);

                Assert.True(File.Exists(geoPath));
                string[] lines = File.ReadAllLines(geoPath);

                // Single closed smooth spline starting and ending at the sharp trailing edge
                int splineCount = lines.Count(l => l.StartsWith("Spline("));
                Assert.Equal(1, splineCount);
                string splineLine = lines.First(l => l.StartsWith("Spline(1) = {"));
                string[] splineIds = splineLine.Split('{')[1].Split('}')[0]
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                Assert.Equal(splineIds[0], splineIds[^1]);
                string fanLine = lines.First(l => l.StartsWith("Field[1].FanPointsList = {"));
                string fanId = fanLine.Split('{')[1].Split('}')[0].Trim();
                Assert.Equal(fanId, splineIds[0]);

                // BoundaryLayer field present
                Assert.Contains(lines, l => l.Contains("Field[1] = BoundaryLayer;"));
                Assert.Contains(lines, l => l.Contains("Field[1].Quads = 1;"));

                // SU2 marker names present
                Assert.Contains(lines, l => l.Contains("Physical Curve(\"Wall\")"));
                Assert.Contains(lines, l => l.Contains("Physical Curve(\"Farfield\")"));
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Fact]
        public void Su2ConfigGenerator_Generate_On_Naca2D_Includes_2D_Aoa_And_Markers()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "su2_cfg_test_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var options = new Su2SolverOptions
                {
                    Dimensions = 2,
                    AngleOfAttackDeg = 5.0f,
                    ReferenceLength = 0.05f,
                    ReferenceAreaMetric = "Chord",
                    FreestreamVelocity = 50.0f,
                    TurbulenceModel = "SA"
                };

                var simConfig = SimulationConfig.CreateDefault();
                simConfig.Su2MaxIterations = 250;

                string cfgPath = Path.Combine(tempDir, "config.cfg");
                float refArea = 0.05f; // 50 mm = 0.05 m chord * 1 m span in 2D

                Su2ConfigGenerator.Generate(
                    cfgPath,
                    "mesh.su2",
                    refArea,
                    simConfig,
                    options,
                    FlowConditions.From(simConfig, options));

                Assert.True(File.Exists(cfgPath));
                string configContent = File.ReadAllText(cfgPath);

                // AOA and velocity vector in 2D
                Assert.Contains("AOA= 5", configContent);
                Assert.Contains("FREESTREAM_VELOCITY", configContent);

                // Wall and Farfield markers
                Assert.Contains("MARKER_HEATFLUX= ( Wall, 0.0 )", configContent);
                Assert.Contains("MARKER_FAR= ( Farfield )", configContent);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, true);
            }
        }

        [Theory]
        [InlineData(0.0f, 0.0005f, 0.00825f, true)]   // alpha=0: CL near 0, CD error ~0.73% -> PASSED
        [InlineData(0.0f, 0.0080f, 0.00825f, false)]  // alpha=0: CL = 0.008 > 0.005 abs tolerance -> FAILED
        [InlineData(0.0f, 0.0000f, 0.00900f, false)]  // alpha=0: CD error 9.9% > 5% -> FAILED
        [InlineData(10.0f, 1.1000f, 0.01250f, true)]  // alpha=10: CL error 0.83% <= 2%, CD error 1.54% <= 5% -> PASSED
        [InlineData(10.0f, 1.1200f, 0.01250f, false)] // alpha=10: CL error 2.67% > 2% -> FAILED
        [InlineData(10.0f, 1.0909f, 0.01350f, false)] // alpha=10: CD error 9.67% > 5% -> FAILED
        [InlineData(15.0f, 1.5500f, 0.02150f, true)]  // alpha=15: CL error 0.25% <= 2%, CD error 1.22% <= 5% -> PASSED
        [InlineData(15.0f, 1.5850f, 0.02124f, false)] // alpha=15: CL error 2.52% > 2% -> FAILED
        [InlineData(15.0f, 1.5461f, 0.02300f, false)] // alpha=15: CD error 8.29% > 5% -> FAILED
        public void Naca2DTmrBenchmark_EvaluateAgainstTmr_Validates_Within_Tolerances(
            float alpha, float clSim, float cdSim, bool expectedPass)
        {
            var result = Naca2DTmrBenchmark.EvaluateAgainstTmr(alpha, clSim, cdSim);

            Assert.Equal(expectedPass, result.Passed);
            Assert.Equal(alpha, result.AlphaDeg);
            Assert.Contains(expectedPass ? "PASSED" : "FAILED", result.Summary);
        }

        [Fact]
        public void Naca2DTmrBenchmark_EvaluateAgainstTmr_Throws_On_Unsupported_Alpha()
        {
            Assert.Throws<ArgumentException>(() =>
                Naca2DTmrBenchmark.EvaluateAgainstTmr(5.0f, 0.5f, 0.01f));
        }

        [Fact]
        public void Naca2DTmrBenchmark_CreateTmrFlowAndSolverOptions_Configures_Re_6e6()
        {
            var options = Naca2DTmrBenchmark.CreateTmrFlowAndSolverOptions(alphaDeg: 10.0f, chordMeters: 1.0f);

            Assert.Equal(2, options.Dimensions);
            Assert.Equal(10.0f, options.AngleOfAttackDeg);
            Assert.Equal("SA", options.TurbulenceModel);

            // Re = (rho * v * c) / mu
            float reynolds = (options.Density * options.FreestreamVelocity * options.ReferenceLength) / options.DynamicViscosity;

            // Re must be exactly 6e6 (with numerical tolerance < 0.1 %)
            Assert.InRange(reynolds, 5.99e6f, 6.01e6f);
        }

        [Fact]
        public void Naca2DFitnessCalculator_Calculates_GlideRatio()
        {
            var calculator = new Naca2DFitnessCalculator(new Naca2DProject().CreateDefaults());
            var simConfig = SimulationConfig.CreateDefault();

            // Normal case: CL = 1.0909, CD = 0.01231
            var record = new ModelRecord();
            record.PassiveParameters["Lift"] = 1.0909f;
            record.PassiveParameters["Drag"] = 0.01231f;

            calculator.CalculateFitness(record, simConfig);

            float expectedRatio = 1.0909f / 0.01231f;
            Assert.Equal(expectedRatio, record.Fitness, precision: 2);
            Assert.True(record.PassiveParameters.ContainsKey("GlideRatio"));

            // Failed simulation
            var failed = new ModelRecord { SimulationFailed = true };
            calculator.CalculateFitness(failed, simConfig);
            Assert.Equal(0.0001f, failed.Fitness, precision: 4);
        }
    }
}
