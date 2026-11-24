using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using FluidForge.Cli;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Kernels.Planar2D;
using FluidForge.Projects.BenchmarkNaca0012;
using FluidForge.Projects.BenchmarkPipeBend90;
using FluidForge.Projects.BenchmarkSuboffOpenFoam;
using FluidForge.Projects.BenchmarkSuboffSu2;
using FluidForge.Projects.Naca2D;
using FluidForge.Solvers.OpenFoam;
using Xunit;

namespace FluidForge.Tests
{
    public sealed class Phase4BenchmarksTests : TempDirectoryTest
    {
        [Fact]
        public void DarpaSuboffHullBuilder_EvaluateRadiusFeet_Matches_Aff1_Key_Stations()
        {
            double rMax = DarpaSuboffHullBuilder.RMaxFeet;
            double rStern = DarpaSuboffHullBuilder.SternCapRatio * rMax;

            Assert.Equal(0.0, DarpaSuboffHullBuilder.EvaluateRadiusFeet(0.0), 6);
            Assert.Equal(rMax, DarpaSuboffHullBuilder.EvaluateRadiusFeet(10.0 / 3.0), 5);
            Assert.Equal(rMax, DarpaSuboffHullBuilder.EvaluateRadiusFeet(10.6458333333), 5);
            Assert.Equal(rStern, DarpaSuboffHullBuilder.EvaluateRadiusFeet(13.9791666667), 5);
            Assert.Equal(0.0, DarpaSuboffHullBuilder.EvaluateRadiusFeet(14.291667), 6);

            // Smoothness just inside forebody & afterbody
            double rMid = DarpaSuboffHullBuilder.EvaluateRadiusFeet(7.0);
            Assert.Equal(rMax, rMid, 6);

            double rAfterMid = DarpaSuboffHullBuilder.EvaluateRadiusFeet(12.3);
            Assert.InRange(rAfterMid, rStern, rMax);
        }

        [Fact]
        public void DarpaSuboffHullBuilder_BuildAndExport_Produces_Watertight_Mesh_With_Reference_Metrics_And_Parametric_Response()
        {
            string baselineDir = Sub("suboff_base");
            var baseParams = new Dictionary<string, float>
            {
                ["RadiusScale"] = 1.0f,
                ["MidbodyLengthScale"] = 1.0f
            };

            GeometryResult baseResult = DarpaSuboffHullBuilder.BuildAndExport(1, 1, baseParams, baselineDir);

            Assert.Equal(GeometryKind.SurfaceStl, baseResult.Kind);
            Assert.True(File.Exists(baseResult.StlPath));

            IReadOnlyList<StlTriangle> triangles = StlWriter.ReadTriangles(baseResult.StlPath);
            Assert.NotEmpty(triangles);

            (int openEdges, int nonManifoldEdges) = MeshMetrics.CountBoundaryAndNonManifoldEdges(triangles);
            Assert.Equal(0, openEdges);
            Assert.Equal(0, nonManifoldEdges);

            MeshMeasurement measurement = MeshMetrics.Measure(triangles);
            Assert.True(measurement.SignedVolume > 0f, "Expected outward triangle normals (positive signed volume).");

            Assert.InRange(baseResult.Metrics["Length"], 435.0f, 436.2f);
            Assert.InRange(baseResult.Metrics["Diameter"], 50.7f, 50.9f);
            Assert.InRange(baseResult.Metrics["WettedArea"], 59880f * 0.99f, 59880f * 1.01f);

            // RadiusScale = 1.05 increases diameter, wetted area, frontal area, and volume
            string widerDir = Sub("suboff_wider");
            GeometryResult widerResult = DarpaSuboffHullBuilder.BuildAndExport(
                1, 2,
                new Dictionary<string, float> { ["RadiusScale"] = 1.05f, ["MidbodyLengthScale"] = 1.0f },
                widerDir);

            Assert.True(widerResult.Metrics["Diameter"] > baseResult.Metrics["Diameter"] * 1.04f);
            Assert.True(widerResult.Metrics["FrontalArea"] > baseResult.Metrics["FrontalArea"] * 1.09f);
            Assert.True(widerResult.Metrics["WettedArea"] > baseResult.Metrics["WettedArea"] * 1.04f);
            Assert.True(widerResult.Metrics["Volume"] > baseResult.Metrics["Volume"] * 1.09f);

            // MidbodyLengthScale = 1.05 increases length, wetted area, and volume while preserving diameter
            string longerDir = Sub("suboff_longer");
            GeometryResult longerResult = DarpaSuboffHullBuilder.BuildAndExport(
                1, 3,
                new Dictionary<string, float> { ["RadiusScale"] = 1.0f, ["MidbodyLengthScale"] = 1.05f },
                longerDir);

            Assert.True(longerResult.Metrics["Length"] > baseResult.Metrics["Length"] + 10.0f);
            Assert.Equal(baseResult.Metrics["Diameter"], longerResult.Metrics["Diameter"], 2);
            Assert.True(longerResult.Metrics["WettedArea"] > baseResult.Metrics["WettedArea"]);
            Assert.True(longerResult.Metrics["Volume"] > baseResult.Metrics["Volume"]);

            // Twin projects must both run on DirectGeometryKernel (no PicoGK)
            Assert.IsType<DirectGeometryKernel>(new BenchmarkSuboffOpenFoamProject().CreateKernel());
            Assert.IsType<DirectGeometryKernel>(new BenchmarkSuboffSu2Project().CreateKernel());
        }

        [Fact]
        public void BenchmarkNaca0012_Generates_Closed_1000mm_Airfoil_And_Computes_Tmr_Errors()
        {
            var projectDef = new BenchmarkNaca0012Project();
            ProjectContext ctx = OptimizeCommand.LoadConfiguration(projectDef, null);

            string geomDir = Sub("naca_geom");
            IGeometryGenerator generator = projectDef.CreateGeometry(ctx);
            GeometryResult geom = generator.GenerateAndExport(1, 1, ctx.Project.BaseParameters, geomDir, ctx.Simulation);

            Assert.Equal(GeometryKind.Contour2D, geom.Kind);
            Assert.True(File.Exists(geom.StlPath));
            Assert.True(File.Exists(Path.Combine(geomDir, "Model_Gen1_Var1.svg")));

            Assert.InRange(geom.Metrics["Chord"], 999.0f, 1001.0f);
            Assert.InRange(geom.Metrics["Thickness"], 119.5f, 120.5f);
            Assert.Equal(geom.Metrics["Chord"], geom.Metrics["FrontalArea"]);

            ContourFile2D contour = Shape2D.LoadContour(geom.StlPath);
            ContourLoop2D loop = Assert.Single(contour.Contours);
            // Closed trailing edge reaches (1000, 0) without a blunt trailing-edge gap
            float[] te = loop.Points.MaxBy(pt => pt[0])!;
            Assert.InRange(te[0], 999.9f, 1000.1f);
            Assert.InRange(te[1], -1e-4f, 1e-4f);
            Assert.NotEmpty(loop.CornerIndices);

            // Fitness + TMR error calculation
            var fitness = new BenchmarkNaca0012FitnessCalculator(ctx.Project);
            float cdRef0 = Naca2DTmrBenchmark.ReferencePoints[0].CdRef;
            float clRef10 = Naca2DTmrBenchmark.ReferencePoints[10].ClRef;
            float cdRef10 = Naca2DTmrBenchmark.ReferencePoints[10].CdRef;

            var record = new ModelRecord
            {
                PassiveParameters =
                {
                    ["Drag"] = 0.5f * (cdRef0 * 1.02f + cdRef10 * 1.04f),
                    ["Lift"] = 0.5f * (0.0010f + clRef10 * 0.99f),
                    ["Drag_OP1"] = cdRef0 * 1.02f,
                    ["Lift_OP1"] = 0.0010f,
                    ["Drag_OP2"] = cdRef10 * 1.04f,
                    ["Lift_OP2"] = clRef10 * 0.99f
                }
            };

            fitness.CalculateFitness(record, ctx.Simulation);

            Assert.Equal(0.02f, record.PassiveParameters["Tmr_CdRelErr_Alpha0"], 4);
            Assert.Equal(0.0010f, record.PassiveParameters["Tmr_ClAbsErr_Alpha0"], 5);
            Assert.Equal(0.04f, record.PassiveParameters["Tmr_CdRelErr_Alpha10"], 4);
            Assert.Equal(0.01f, record.PassiveParameters["Tmr_ClRelErr_Alpha10"], 4);

            float expectedFitness = 1.0f / MathF.Max(MathF.Abs(record.PassiveParameters["Drag"]), 1e-4f);
            Assert.Equal(expectedFitness, record.Fitness, 3);
        }

        [Fact]
        public void BenchmarkPipeBend90FitnessCalculator_Computes_Idelchik_Zeta_Decomposition_And_Minimizes_PressureDrop()
        {
            var projectDef = new BenchmarkPipeBend90Project();
            ProjectContext ctx = OptimizeCommand.LoadConfiguration(projectDef, null);
            var calculator = new BenchmarkPipeBend90FitnessCalculator(ctx.Project);

            // q = 0.5 * 998.2 * 3.34^2 = 5567.75996 Pa
            double q = 0.5 * 998.2 * 3.34 * 3.34;
            float pressureDrop = (float)(0.50 * q); // TotalLossCoeff_Zeta = 0.50

            var record = new ModelRecord
            {
                ActiveParameters = { ["BendRadiusRatio"] = 3.0f },
                PassiveParameters = { ["PressureDrop"] = pressureDrop }
            };

            calculator.CalculateFitness(record, ctx.Simulation);

            double dM = 0.030;
            double re = (998.2 * 3.34 * dM) / 0.001002;
            double lambda = 0.3164 / Math.Pow(re, 0.25);
            double lOverD = 0.5 * (10.0 + 20.0 + 0.5 * Math.PI * 3.0);
            double expectedFriction = lambda * lOverD;
            double expectedBend = 0.50 - expectedFriction;

            Assert.Equal(0.50f, record.PassiveParameters["TotalLossCoeff_Zeta"], 4);
            Assert.Equal((float)expectedFriction, record.PassiveParameters["StraightFriction_Zeta"], 4);
            Assert.Equal((float)expectedBend, record.PassiveParameters["BendLossCoeff_Zeta"], 4);

            var higherDp = new ModelRecord
            {
                ActiveParameters = { ["BendRadiusRatio"] = 3.0f },
                PassiveParameters = { ["PressureDrop"] = pressureDrop * 1.5f }
            };
            calculator.CalculateFitness(higherDp, ctx.Simulation);
            Assert.True(record.Fitness > higherDp.Fitness);
        }

        [Fact]
        public void BenchmarkSuboffOpenFoam_Generates_Reasonable_BlockMesh_Half_ReferenceArea_And_YPlus80()
        {
            var projectDef = new BenchmarkSuboffOpenFoamProject();
            ProjectContext ctx = OptimizeCommand.LoadConfiguration(projectDef, null);

            OpenFoamOptions openFoamOptions = ctx.LoadSolverOptions<OpenFoamOptions>("openfoam");
            Assert.Equal(80.0f, openFoamOptions.TargetYPlus, 3);
            Assert.Equal("Y0", openFoamOptions.SymmetryPlane);
            Assert.Equal("External3D", openFoamOptions.DomainMode);

            string geomDir = Sub("suboff_of_geom");
            GeometryResult geom = projectDef.CreateGeometry(ctx).GenerateAndExport(
                1, 1, ctx.Project.BaseParameters, geomDir, ctx.Simulation);

            string caseDir = Sub("suboff_of_case");
            var request = new MeshRequest(geom.StlPath, 1, 1, ctx.Simulation, 1.0f, Root, Geometry: geom);
            var record = new ModelRecord();
            foreach (var kvp in geom.Metrics)
            {
                record.PassiveParameters[kvp.Key] = kvp.Value;
            }

            OpenFoamCaseInfo caseInfo = OpenFoamCaseGenerator.Generate(caseDir, request, openFoamOptions, record);

            Assert.Equal(0.02994f, caseInfo.ReferenceAreaM2, 5);

            string controlDict = File.ReadAllText(Path.Combine(caseDir, "system", "controlDict"));
            Assert.Contains("Aref            0.02994;", controlDict);

            string blockMeshDict = File.ReadAllText(Path.Combine(caseDir, "system", "blockMeshDict"));
            Match match = Regex.Match(
                blockMeshDict,
                @"hex\s*\(0 1 2 3 4 5 6 7\)\s*\((\d+)\s+(\d+)\s+(\d+)\)",
                RegexOptions.CultureInvariant);
            Assert.True(match.Success, "Could not find blockMeshDict hex cell counts.");

            int nx = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            int ny = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
            int nz = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            long totalBackgroundCells = (long)nx * ny * nz;

            Assert.InRange(totalBackgroundCells, 100, 99_999);
        }

        [Theory]
        [InlineData("BenchmarkNaca0012")]
        [InlineData("BenchmarkPipeBend90")]
        [InlineData("BenchmarkSuboffOpenFoam")]
        [InlineData("BenchmarkSuboffSu2")]
        public void Benchmark_Readmes_Exist_In_English_With_Exactly_Two_Body_Paragraphs(string projectName)
        {
            string? projectDir = ProjectPaths.ResolveProjectDirectory(projectName);
            Assert.NotNull(projectDir);

            string readmePath = Path.Combine(projectDir!, "README.md");
            Assert.True(File.Exists(readmePath), $"Missing README.md for {projectName}.");

            string content = File.ReadAllText(readmePath).Trim();
            Assert.DoesNotContain("##", content, StringComparison.Ordinal);

            string[] blocks = Regex.Split(content.Replace("\r\n", "\n"), @"\n\s*\n")
                .Select(b => b.Trim())
                .Where(b => b.Length > 0)
                .ToArray();

            Assert.Equal(3, blocks.Length);
            Assert.Equal($"# {projectName}", blocks[0]);

            for (int i = 1; i <= 2; i++)
            {
                string paragraph = blocks[i];
                Assert.False(paragraph.StartsWith("#", StringComparison.Ordinal), "Subheadings are not allowed.");
                Assert.False(paragraph.StartsWith("-", StringComparison.Ordinal), "Bullet lists are not allowed.");
                Assert.False(paragraph.StartsWith("*", StringComparison.Ordinal), "Bullet lists are not allowed.");
                Assert.False(Regex.IsMatch(paragraph, @"^\d+\."), "Numbered lists are not allowed.");
                Assert.DoesNotContain("\n-", paragraph, StringComparison.Ordinal);
                Assert.DoesNotContain("\n*", paragraph, StringComparison.Ordinal);
            }

            // Basic English sanity check (no German umlauts / eszett)
            Assert.DoesNotMatch(@"[äöüÄÖÜß]", content);
        }
    }
}
