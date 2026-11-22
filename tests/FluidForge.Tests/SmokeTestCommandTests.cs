using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FluidForge.Cli;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Projects.Naca2D;
using FluidForge.Solvers.Cfd;
using Xunit;

namespace FluidForge.Tests
{
    public class SmokeTestCommandTests : TempDirectoryTest
    {
        private sealed class Dummy2DGenerator : IGeometryGenerator
        {
            private readonly bool _ignoreSecondParam;

            public Dummy2DGenerator(bool ignoreSecondParam) => _ignoreSecondParam = ignoreSecondParam;

            public GeometryResult GenerateAndExport(
                int iteration, int variant, Dictionary<string, float> parameters, string outputDirectory, SimulationConfig config)
            {
                float length = parameters["Chord"];
                float thickness = _ignoreSecondParam ? 10.0f : parameters["Thickness"];

                var shape = new FluidForge.Kernels.Planar2D.Shape2D().AddPolygon(new List<Vector2>
                {
                    new(0f, -0.5f * thickness),
                    new(length, -0.5f * thickness),
                    new(length, 0.5f * thickness),
                    new(0f, 0.5f * thickness)
                });

                Directory.CreateDirectory(outputDirectory);
                string path = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.json");
                shape.SaveContour(path);

                var m = shape.Measure();
                var res = new GeometryResult { StlPath = path, Kind = GeometryKind.Contour2D };
                res.AddMetric("Area", m.Area, MetricScaling.Area);
                res.AddMetric("Chord", m.Chord, MetricScaling.Linear);
                return res;
            }
        }

        private sealed class DummyBoxStlGenerator : IGeometryGenerator
        {
            private readonly bool _dropFace;

            public DummyBoxStlGenerator(bool dropFace) => _dropFace = dropFace;

            public GeometryResult GenerateAndExport(
                int iteration, int variant, Dictionary<string, float> parameters, string outputDirectory, SimulationConfig config)
            {
                float sx = parameters["Length"];
                float sy = parameters["Width"];
                float sz = parameters["Height"];

                IReadOnlyList<StlTriangle> tris = TunnelGeometry.CreateBox(new Vector3(sx, sy, sz), Vector3.Zero);
                if (_dropFace)
                {
                    tris = tris.Take(tris.Count - 1).ToList();
                }

                Directory.CreateDirectory(outputDirectory);
                string path = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.stl");
                StlWriter.WriteBinary(path, tris);

                var m = MeshMetrics.Measure(tris);
                var res = new GeometryResult { StlPath = path, Kind = GeometryKind.SurfaceStl };
                res.AddMetric("Volume", m.Volume, MetricScaling.Volume);
                res.AddMetric("SurfaceArea", m.SurfaceArea, MetricScaling.Area);
                return res;
            }
        }

        private sealed class DummyProjectDefinition : IProjectDefinition
        {
            private readonly ProjectConfig _config;
            private readonly Func<IGeometryGenerator> _geomFactory;

            public DummyProjectDefinition(string name, ProjectConfig config, Func<IGeometryGenerator> geomFactory)
            {
                Name = name;
                _config = config;
                _geomFactory = geomFactory;
            }

            public string Name { get; }
            public ProjectConfig CreateDefaults() => _config;
            public IGeometryKernel CreateKernel() => new DirectGeometryKernel();
            public IGeometryGenerator CreateGeometry(ProjectContext context) => _geomFactory();
            public IFitnessCalculator CreateFitness(ProjectContext context) => new BayesHarness.SingleValueFitness(v => v);
            public SolverStage[] CreateStages(ProjectContext context) => Array.Empty<SolverStage>();
        }

        [Fact]
        public void SmokeTest_Passes_On_Real_Naca2D_Project()
        {
            var definition = new Naca2DProject();
            ProjectContext context = OptimizeCommand.LoadConfiguration(definition, projectDirectoryArgument: null);

            SmokeTestReport report = SmokeTestCommand.Execute(definition, context, Sub("naca2d_smoke"));

            Assert.True(report.Passed, report.Summary);
            Assert.Equal(6, report.Probes.Count);
            Assert.All(report.Probes, p =>
            {
                Assert.True(p.Passed, $"{p.Label}: {p.ErrorMessage}");
                Assert.True(p.FileSizeBytes > 0);
                Assert.True(p.ElementCount > 10);
            });

            var maxCamber = Assert.Single(report.Sensitivities, s => s.ParameterName == "MaxCamber");
            var camberPos = Assert.Single(report.Sensitivities, s => s.ParameterName == "CamberPosition");
            var maxThick = Assert.Single(report.Sensitivities, s => s.ParameterName == "MaxThickness");
            Assert.True(maxCamber.Effective);
            Assert.True(camberPos.Effective);
            Assert.True(maxThick.Effective);
        }

        [Fact]
        public void SmokeTest_Fails_And_Names_Ineffective_Parameter()
        {
            var project = new ProjectConfig
            {
                ProjectName = "DeadParamTest",
                BaseParameters = new Dictionary<string, float> { ["Chord"] = 50f, ["Thickness"] = 10f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    ["Chord"] = (30f, 80f),
                    ["Thickness"] = (5f, 20f)
                }
            };
            var definition = new DummyProjectDefinition("DeadParamTest", project, () => new Dummy2DGenerator(ignoreSecondParam: true));
            var context = new ProjectContext("DeadParamTest", null, null, SimulationConfig.CreateDefault(), project);

            SmokeTestReport report = SmokeTestCommand.Execute(definition, context, Sub("dead_param"));

            Assert.False(report.Passed);
            var thicknessSens = Assert.Single(report.Sensitivities, s => s.ParameterName == "Thickness");
            Assert.False(thicknessSens.Effective);
            Assert.Contains("DEAD", thicknessSens.Status);
        }

        [Fact]
        public void SmokeTest_Fails_On_Open_NonWatertight_3D_Stl_And_Passes_On_Closed_Box()
        {
            var project = new ProjectConfig
            {
                ProjectName = "StlWatertightTest",
                BaseParameters = new Dictionary<string, float> { ["Length"] = 100f, ["Width"] = 50f, ["Height"] = 30f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    ["Length"] = (60f, 140f),
                    ["Width"] = (30f, 80f),
                    ["Height"] = (20f, 60f)
                }
            };

            var openDef = new DummyProjectDefinition("OpenBox", project, () => new DummyBoxStlGenerator(dropFace: true));
            var openCtx = new ProjectContext("OpenBox", null, null, SimulationConfig.CreateDefault(), project);
            SmokeTestReport openReport = SmokeTestCommand.Execute(openDef, openCtx, Sub("open_stl"));

            Assert.False(openReport.Passed);
            Assert.All(openReport.Probes, p =>
            {
                Assert.False(p.Passed);
                Assert.True(p.OpenEdges > 0);
                Assert.Contains("Non-watertight", p.ErrorMessage);
            });

            var closedDef = new DummyProjectDefinition("ClosedBox", project, () => new DummyBoxStlGenerator(dropFace: false));
            var closedCtx = new ProjectContext("ClosedBox", null, null, SimulationConfig.CreateDefault(), project);
            SmokeTestReport closedReport = SmokeTestCommand.Execute(closedDef, closedCtx, Sub("closed_stl"));

            Assert.True(closedReport.Passed, closedReport.Summary);
            Assert.All(closedReport.Probes, p =>
            {
                Assert.True(p.Passed);
                Assert.Equal(0, p.OpenEdges);
                Assert.Equal(0, p.NonManifoldEdges);
            });
        }
    }
}
