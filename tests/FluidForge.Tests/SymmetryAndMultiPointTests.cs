using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.Json.Nodes;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Projects.Naca2D;
using FluidForge.Solvers.Cfd;
using Xunit;

namespace FluidForge.Tests
{
    [Collection(ProcessWideStateCollection.Name)]
    public class SymmetryAndMultiPointTests : TempDirectoryTest
    {
        [Fact]
        public void StlHalfClipper_Clips_FullBox_Preserves_Normals_And_Extracts_CutEdges()
        {
            IReadOnlyList<StlTriangle> fullBox = TunnelGeometry.CreateBox(new Vector3(20f, 10f, 6f), Vector3.Zero);

            HalfClipResult result = StlHalfClipper.ClipToHalfSpace(fullBox, SymmetryAxis.Y0);

            Assert.True(result.InputWasFullBody);
            Assert.NotEmpty(result.HalfTriangles);
            Assert.NotEmpty(result.CutEdges);
            Assert.All(result.HalfTriangles, t =>
            {
                Assert.True(t.A.Y >= 0f && t.B.Y >= 0f && t.C.Y >= 0f);
                // No triangle should lie completely on Y=0
                Assert.True(t.A.Y > 0f || t.B.Y > 0f || t.C.Y > 0f);
                // Outward normal on +Y face must still point +Y
                if (t.A.Y > 4.9f && t.B.Y > 4.9f && t.C.Y > 4.9f)
                {
                    Assert.True(t.Normal.Y > 0.9f);
                }
            });
            Assert.All(result.CutEdges, e =>
            {
                Assert.Equal(0f, e.A.Y);
                Assert.Equal(0f, e.B.Y);
            });
        }

        [Fact]
        public void StlHalfClipper_Detects_AlreadyHalf_And_Snaps_Sliver_Vertices()
        {
            // Box shifted so minY = -1e-5 (within eps = 1e-5 * 10 = 1e-4) -> snapped to 0 and recognized as already half
            IReadOnlyList<StlTriangle> alreadyHalf = TunnelGeometry.CreateBox(
                new Vector3(20f, 10f, 6f), new Vector3(0f, 5f - 1e-5f, 0f));

            HalfClipResult result = StlHalfClipper.ClipToHalfSpace(alreadyHalf, SymmetryAxis.Y0);

            Assert.False(result.InputWasFullBody);
            Assert.NotEmpty(result.HalfTriangles);
            Assert.NotEmpty(result.CutEdges);
            Assert.All(result.HalfTriangles, t => Assert.True(t.A.Y >= 0f && t.B.Y >= 0f && t.C.Y >= 0f));

            // Z0 clipping also works symmetrically
            IReadOnlyList<StlTriangle> fullZ = TunnelGeometry.CreateBox(new Vector3(20f, 10f, 8f), Vector3.Zero);
            HalfClipResult clipZ = StlHalfClipper.ClipToHalfSpace(fullZ, SymmetryAxis.Z0);
            Assert.True(clipZ.InputWasFullBody);
            Assert.All(clipZ.HalfTriangles, t => Assert.True(t.A.Z >= 0f && t.B.Z >= 0f && t.C.Z >= 0f));
        }

        [Theory]
        [InlineData("None", SymmetryAxis.None, true)]
        [InlineData("y0", SymmetryAxis.Y0, true)]
        [InlineData("Z0", SymmetryAxis.Z0, true)]
        [InlineData("X0", SymmetryAxis.None, false)]
        [InlineData("", SymmetryAxis.None, false)]
        public void StlHalfClipper_TryParse_And_ParseConfigValue_Validate_Cleanly(
            string input, SymmetryAxis expected, bool validTryParse)
        {
            Assert.Equal(validTryParse, StlHalfClipper.TryParse(input, out SymmetryAxis parsed));
            if (validTryParse)
            {
                Assert.Equal(expected, parsed);
                Assert.Equal(expected, StlHalfClipper.ParseConfigValue(input));
            }
            else if (!string.IsNullOrWhiteSpace(input))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => StlHalfClipper.ParseConfigValue(input));
                Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
            }
        }

        [Fact]
        public void GmshTunnelBuilder_SymmetryY0_Starts_At_Zero_With_Equal_Blockage_And_Cylinder_Fallback()
        {
            var fullOptions = new GmshMesherOptions
            {
                SymmetryPlane = "None",
                TunnelShape = "Box",
                TunnelSizeX = 1000f,
                TunnelSizeY = 300f,
                TunnelSizeZ = 300f,
                TunnelCenterX = 200f,
                TunnelCenterY = 0f,
                TunnelCenterZ = 0f,
                TunnelMaxBlockagePercent = 0.3f
            };
            var halfOptions = new GmshMesherOptions
            {
                SymmetryPlane = "Y0",
                TunnelShape = "Cylinder",
                TunnelDiameter = 300f,
                TunnelLength = 1000f,
                TunnelCenterX = 200f,
                TunnelMaxBlockagePercent = 0.3f
            };

            var fullBounds = new MeshBounds(new Vector3(-25f, -20f, -6f), new Vector3(25f, 20f, 6f));
            float fullArea = 300f;

            TunnelSpec fullSpec = new GmshTunnelBuilder(fullOptions).ResolveEffectiveTunnel(fullBounds, 1.0f, fullArea);
            TunnelSpec halfSpec = new GmshTunnelBuilder(halfOptions).ResolveEffectiveTunnel(fullBounds, 1.0f, fullArea);

            Assert.False(halfSpec.IsCylinder); // Cylinder falls back to half-box
            Assert.Equal(fullSpec.BlockagePercent, halfSpec.BlockagePercent, 3);
            Assert.Equal(halfSpec.SizeY * 0.5f, halfSpec.CenterY, 3); // minY == 0 => CenterY == SizeY / 2
        }

        [Fact]
        public void BoundaryLayerWallField_Projects_SymmetryPlane_Vertex_Normals_Onto_Plane()
        {
            // Open half-wedge surface with two vertices on Y=0 and two on Y=5
            double[] points =
            {
                -10, 0,  0,   // 0 (on Y=0)
                 10, 0,  0,   // 1 (on Y=0)
                  0, 0,  5,   // 2 (on Y=0)
                 -5, 5,  2,   // 3 (Y > 0)
                  5, 5,  2    // 4 (Y > 0)
            };
            int[] triangles =
            {
                0, 2, 3,
                2, 1, 4,
                2, 4, 3
            };
            var wall = new WallSurface(points, triangles);

            WallFieldResult field = BoundaryLayerWallField.Build(
                wall, thickness: 1.0, BoundaryLayerWallField.Ladder[0], SymmetryAxis.Y0);

            // Vertices 0, 1, 2 lie on Y=0 -> Y-component of extrusion vector must be strictly 0
            Assert.Equal(0.0, field.Vectors[3 * 0 + 1], 12);
            Assert.Equal(0.0, field.Vectors[3 * 1 + 1], 12);
            Assert.Equal(0.0, field.Vectors[3 * 2 + 1], 12);
        }

        [Fact]
        public void Gmsh3DGeoScriptBuilder_Emits_Symmetry_Marker_In_Prism_And_Threshold_Modes()
        {
            string prismGeo = Path.Combine(Root, "Meshing_PrismSym.geo");
            string threshGeo = Path.Combine(Root, "Meshing_ThreshSym.geo");

            var prismOpts = new GmshMesherOptions { SymmetryPlane = "Y0", BoundaryLayerMode = "Prism" };
            var threshOpts = new GmshMesherOptions { SymmetryPlane = "Z0", BoundaryLayerMode = "Threshold" };
            var config = SimulationConfig.CreateDefault();

            Gmsh3DGeoScriptBuilder.WriteGeoScript(
                prismOpts, FlowConditions.From(config, new Su2SolverOptions()),
                prismGeo, "model_half.stl", "tunnel.stl", config, 1.0f, 1.0f, "wall.pos");
            Gmsh3DGeoScriptBuilder.WriteGeoScript(
                threshOpts, FlowConditions.From(config, new Su2SolverOptions()),
                threshGeo, "model_half.stl", "tunnel.stl", config, 1.0f, 1.0f, null);

            string prismContent = File.ReadAllText(prismGeo);
            string threshContent = File.ReadAllText(threshGeo);

            Assert.Contains("Physical Surface(\"Symmetry\")", prismContent);
            Assert.Contains("bl_laterals[]", prismContent);
            Assert.Contains("Physical Surface(\"Wall\")", prismContent);
            Assert.Contains("Physical Surface(\"Farfield\")", prismContent);

            Assert.Contains("Physical Surface(\"Symmetry\")", threshContent);
            Assert.Contains("Physical Surface(\"Wall\")", threshContent);
            Assert.Contains("Physical Surface(\"Farfield\")", threshContent);
        }

        [Fact]
        public void Su2ConfigGenerator_Handles_Symmetry_SST_And_Rejects_Unknown_TurbulenceModel()
        {
            string cfgPath = Path.Combine(Root, "sym_sst.cfg");
            var options = new Su2SolverOptions
            {
                SymmetryPlane = "Y0",
                TurbulenceModel = "sst"
            };

            Su2ConfigGenerator.Generate(cfgPath, "mesh.su2", 0.002f, SimulationConfig.CreateDefault(), options);
            string content = File.ReadAllText(cfgPath);

            Assert.Contains("KIND_TURB_MODEL= SST", content);
            Assert.Contains("MARKER_SYM= ( Symmetry )", content);

            var badTurb = new Su2SolverOptions { TurbulenceModel = "kOmegaFake" };
            var ex = Assert.Throws<InvalidOperationException>(() =>
                Su2ConfigGenerator.Generate(cfgPath, "mesh.su2", 0.002f, SimulationConfig.CreateDefault(), badTurb));
            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
        }

        private sealed class Fake3DSymmetryProject : CfdProjectDefinition
        {
            public override string Name => "FakeSym";
            public override IGeometryKernel CreateKernel() => new DirectGeometryKernel();
            public override IGeometryGenerator CreateGeometry(ProjectContext context) => throw new NotImplementedException();
            public override IFitnessCalculator CreateFitness(ProjectContext context) => throw new NotImplementedException();
        }

        [Fact]
        public void CfdProjectDefinitionBase_Synchronizes_And_Detects_Conflicting_SymmetryPlane()
        {
            string projDir = Sub("sym_sync");
            Directory.CreateDirectory(Path.Combine(projDir, "solvers"));
            File.WriteAllText(Path.Combine(projDir, "solvers", "gmsh.json"), @"{ ""SymmetryPlane"": ""Y0"" }");
            File.WriteAllText(Path.Combine(projDir, "solvers", "su2.json"), @"{ }");

            var def = new Fake3DSymmetryProject();
            var ctx = new ProjectContext("FakeSym", projDir, null, SimulationConfig.CreateDefault(), new ProjectConfig());
            SolverStage[] stages = def.CreateStages(ctx);

            var mesher = Assert.IsType<GmshCfdMesher>(stages[0].Mesher);
            var solver = Assert.IsType<Su2Solver>(stages[0].Solver);
            Assert.Equal("Y0", mesher.Options.SymmetryPlane);
            Assert.Equal("Y0", solver.Options.SymmetryPlane);

            // Conflict: Y0 vs Z0 must throw [CONFIG]
            File.WriteAllText(Path.Combine(projDir, "solvers", "su2.json"), @"{ ""SymmetryPlane"": ""Z0"" }");
            var ex = Assert.Throws<InvalidOperationException>(() => def.CreateStages(ctx));
            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
            Assert.Contains("SymmetryPlane", ex.Message);
        }

        [Fact]
        public void ConfigSchema_Validates_Elements_Inside_OperatingPoints_Array()
        {
            JsonObject overlay = JsonNode.Parse(@"{
                ""OperatingPoints"": [
                    { ""Name"": ""Cruise"", ""AngleOfAttckDeg"": 4.0, ""Weight"": 0.5 }
                ]
            }")!.AsObject();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                ConfigSchema.Validate(typeof(Su2SolverOptions), overlay, "solvers/su2.json"));
            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
            Assert.Contains("OperatingPoints[0].AngleOfAttckDeg", ex.Message);
            Assert.Contains("AngleOfAttackDeg", ex.Message);
        }

        [Fact]
        public void FlowConditions_Uses_Max_Velocity_Across_OperatingPoints()
        {
            var options = new Su2SolverOptions
            {
                FreestreamVelocity = 10.0f,
                OperatingPoints = new List<OperatingPointOptions>
                {
                    new() { Name = "Slow", Velocity = 5.0f, AngleOfAttackDeg = 2.0f, Weight = 0.3f },
                    new() { Name = "Nominal", Velocity = null, AngleOfAttackDeg = 4.0f, Weight = 0.4f },
                    new() { Name = "Sprint", Velocity = 25.0f, AngleOfAttackDeg = 0.0f, Weight = 0.3f }
                }
            };

            FlowConditions flow = FlowConditions.From(SimulationConfig.CreateDefault(), options);
            Assert.Equal(25.0f, flow.Velocity, 4);
        }

        [Fact]
        public void Su2Solver_MultiPoint_Reuses_Mesh_Aggregates_Weighted_Metrics_And_Archives_Per_OP()
        {
            string workDir = Sub("mp_run");
            string meshPath = Path.Combine(workDir, "Model_Gen1_Var1.su2");
            File.WriteAllText(meshPath, "NDIME= 2\nNELEM= 0\nNPOIN= 0\nNMARK= 0\n");

            var options = new Su2SolverOptions
            {
                Dimensions = 2,
                Density = 1.225f,
                DynamicViscosity = 1.81e-5f,
                FreestreamVelocity = 20.0f,
                ReferenceLength = 0.05f,
                ReferenceAreaMetric = "Chord",
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "CD", ["Lift"] = "CL" },
                AutoRestartOnDrift = false,
                TreatUnconvergedAsFailed = false,
                OperatingPoints = new List<OperatingPointOptions>
                {
                    new() { Name = "Low", AngleOfAttackDeg = 2.0f, Weight = 1.0f },
                    new() { Name = "High", AngleOfAttackDeg = 8.0f, Velocity = 40.0f, Weight = 3.0f }
                }
            };

            int callCount = 0;
            var solver = new Su2Solver(options)
            {
                RunSu2Override = (dir, cores) =>
                {
                    callCount++;
                    double cd = callCount == 1 ? 0.010 : 0.030;
                    double cl = callCount == 1 ? 0.200 : 0.800;

                    var csvLines = new List<string> { "\"Inner_Iter\",\"rms[P]\",\"CD\",\"CL\"" };
                    for (int i = 0; i < 30; i++)
                    {
                        csvLines.Add($"{i}, -5.0, {cd.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {cl.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                    }
                    File.WriteAllLines(Path.Combine(dir, "history.csv"), csvLines);
                    File.WriteAllText(Path.Combine(dir, "surface.csv"),
                        $"\"PointID\",\"Y_Plus\"\n0,{(callCount == 1 ? "0.8" : "1.6")}\n1,{(callCount == 1 ? "1.1" : "2.2")}\n");
                    return (0, "SU2 v8.5.0", "");
                }
            };

            var record = new ModelRecord { Iteration = 1, Variant = 1, MeshPath = meshPath };
            record.PassiveParameters["Chord"] = 50.0f;

            var simConfig = SimulationConfig.CreateDefault();
            simConfig.Su2MaxIterations = 100;
            solver.Solve(meshPath, record, simConfig, workDir);

            Assert.Equal(2, callCount);
            Assert.False(record.SimulationFailed);
            Assert.Equal(2f, record.PassiveParameters["OperatingPointCount"]);

            // Normalized weights: 0.25 and 0.75
            Assert.Equal(0.25f * 0.010f + 0.75f * 0.030f, record.PassiveParameters["Drag"], 5);
            Assert.Equal(0.25f * 0.200f + 0.75f * 0.800f, record.PassiveParameters["Lift"], 5);

            Assert.Equal(0.010f, record.PassiveParameters["Drag_OP1"], 5);
            Assert.Equal(0.030f, record.PassiveParameters["Drag_OP2"], 5);
            Assert.Equal(2.0f, record.PassiveParameters["AngleOfAttackDeg_OP1"], 4);
            Assert.Equal(8.0f, record.PassiveParameters["AngleOfAttackDeg_OP2"], 4);
            Assert.Equal(20.0f, record.PassiveParameters["Velocity_OP1"], 4);
            Assert.Equal(40.0f, record.PassiveParameters["Velocity_OP2"], 4);
            Assert.Equal(60f, record.PassiveParameters["Su2_Iterations"]);
            Assert.Equal(1f, record.PassiveParameters["Su2_Converged"]);
            Assert.True(record.PassiveParameters["YPlus_P95"] > 2.0f);

            string historiesDir = Path.Combine(workDir, OutputConventions.AnalysisDirectory, OutputConventions.HistoriesSubDirectory);
            Assert.True(File.Exists(Path.Combine(historiesDir, "History_Gen1_Var1_OP1.csv")));
            Assert.True(File.Exists(Path.Combine(historiesDir, "History_Gen1_Var1_OP2.csv")));
        }

        [Fact]
        public void Su2Solver_MultiPoint_Aborts_On_Failure_And_Rejects_NonPositive_Weights()
        {
            string workDir = Sub("mp_fail");
            string meshPath = Path.Combine(workDir, "Model_Gen1_Var1.su2");
            File.WriteAllText(meshPath, "NDIME= 2\nNELEM= 0\nNPOIN= 0\nNMARK= 0\n");

            var badWeightSolver = new Su2Solver(new Su2SolverOptions
            {
                OperatingPoints = new List<OperatingPointOptions>
                {
                    new() { Name = "Bad", Weight = 0f }
                }
            });
            var recBad = new ModelRecord();
            recBad.PassiveParameters["FrontalArea"] = 100f;
            var ex = Assert.Throws<InvalidOperationException>(() =>
                badWeightSolver.Solve(meshPath, recBad, SimulationConfig.CreateDefault(), workDir));
            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);

            var failSolver = new Su2Solver(new Su2SolverOptions
            {
                Dimensions = 2,
                ReferenceAreaMetric = "Chord",
                OperatingPoints = new List<OperatingPointOptions>
                {
                    new() { Name = "OP1", AngleOfAttackDeg = 2f, Weight = 0.5f },
                    new() { Name = "OP2", AngleOfAttackDeg = 8f, Weight = 0.5f }
                }
            })
            {
                RunSu2Override = (dir, _) => (1, "", "Crash") // fails immediately on OP1 (no history.csv)
            };

            var record = new ModelRecord { Iteration = 1, Variant = 1, MeshPath = meshPath };
            record.PassiveParameters["Chord"] = 50f;
            failSolver.Solve(meshPath, record, SimulationConfig.CreateDefault(), workDir);

            Assert.True(record.SimulationFailed);
            Assert.Equal(EvaluationFailureStage.Solver, record.FailedStage);
            Assert.False(record.Feasible);
        }

        [Fact]
        public void Su2Solver_Symmetry_Halves_RefArea_And_Computes_Full_DragForceN()
        {
            string workDir = Sub("sym_refarea");
            string meshPath = Path.Combine(workDir, "Model_Gen1_Var1.su2");
            File.WriteAllText(meshPath, "NDIME= 3\nNELEM= 0\nNPOIN= 0\nNMARK= 0\n");

            var options = new Su2SolverOptions
            {
                Dimensions = 3,
                SymmetryPlane = "Y0",
                Density = 1000f,
                FreestreamVelocity = 2.0f,
                ReferenceAreaMetric = "FrontalArea",
                AutoRestartOnDrift = false,
                TreatUnconvergedAsFailed = false
            };

            string? capturedCfg = null;
            var solver = new Su2Solver(options)
            {
                RunSu2Override = (dir, _) =>
                {
                    capturedCfg = File.ReadAllText(Path.Combine(dir, "current_config.cfg"));
                    var lines = new List<string> { "\"Inner_Iter\",\"rms[P]\",\"CD\"" };
                    for (int i = 0; i < 30; i++) lines.Add($"{i}, -5.0, 0.25");
                    File.WriteAllLines(Path.Combine(dir, "history.csv"), lines);
                    return (0, "SU2 v8.5.0", "");
                }
            };

            var record = new ModelRecord { Iteration = 1, Variant = 1, MeshPath = meshPath };
            record.PassiveParameters["FrontalArea"] = 4000f; // 4000 mm^2 = 0.004 m^2 full
            record.PassiveParameters["SymmetryClippedFromFull"] = 1f;

            solver.Solve(meshPath, record, SimulationConfig.CreateDefault(), workDir);

            Assert.NotNull(capturedCfg);
            Assert.Contains("REF_AREA= 0.002", capturedCfg);
            Assert.Contains("MARKER_SYM= ( Symmetry )", capturedCfg);
            Assert.Equal(2.0f, record.PassiveParameters["SymmetryFactor"]);
            Assert.Equal(0.25f, record.PassiveParameters["Drag"], 4);

            // DragForceN = 0.5 * 1000 * 2^2 * 0.25 * 0.004 = 2.0 N
            Assert.Equal(2.0f, record.PassiveParameters["DragForceN"], 4);
        }

        [Fact]
        public void Su2RegimeAdvisor_Warns_Only_Below_Transitional_Threshold()
        {
            Assert.Null(Su2RegimeAdvisor.CheckLowReynoldsWarning(0f, "SA"));
            Assert.Null(Su2RegimeAdvisor.CheckLowReynoldsWarning(5.0e5f, "SA"));
            Assert.Null(Su2RegimeAdvisor.CheckLowReynoldsWarning(6.0e6f, "SST"));

            string? warn = Su2RegimeAdvisor.CheckLowReynoldsWarning(1.2e5f, "sst");
            Assert.NotNull(warn);
            Assert.Contains("SST", warn);
            Assert.Contains("laminar separation bubbles", warn);
        }

        [Fact]
        public void Naca2D_MultiPoint_Fitness_And_PreCfd_Thickness_Constraint_Work_Together()
        {
            ProjectConfig project = new Naca2DProject().CreateDefaults();
            var constraints = new Naca2DGeometryConstraints();
            var fitness = new Naca2DFitnessCalculator(project, new[] { 0.3f, 0.4f, 0.3f });

            var thinRecord = new ModelRecord();
            thinRecord.PassiveParameters["Thickness"] = 3.0f; // 6% of 50 mm < 4.5 mm
            var pre = constraints.EvaluateGeometryConstraints(thinRecord, project);
            Assert.Single(pre);
            Assert.True(pre[0].IsPreSolve);
            Assert.False(pre[0].IsSatisfied);

            var validRecord = new ModelRecord();
            validRecord.PassiveParameters["Drag"] = 0.02f;
            validRecord.PassiveParameters["Lift"] = 0.6f;
            validRecord.PassiveParameters["Drag_OP1"] = 0.01f;
            validRecord.PassiveParameters["Lift_OP1"] = 0.2f; // E1 = 20
            validRecord.PassiveParameters["Drag_OP2"] = 0.02f;
            validRecord.PassiveParameters["Lift_OP2"] = 0.6f; // E2 = 30
            validRecord.PassiveParameters["Drag_OP3"] = 0.04f;
            validRecord.PassiveParameters["Lift_OP3"] = 0.8f; // E3 = 20

            fitness.CalculateFitness(validRecord, SimulationConfig.CreateDefault());

            float expected = 0.3f * 20f + 0.4f * 30f + 0.3f * 20f; // 24
            Assert.True(validRecord.Feasible);
            Assert.Equal(expected, validRecord.PassiveParameters["GlideRatio"], 4);
            Assert.Equal(expected, validRecord.Fitness, 4);
        }
    }
}
