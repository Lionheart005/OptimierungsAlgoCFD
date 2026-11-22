using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;
using Xunit;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-15: the "OVERALL CHAMPION" at the end of the run must be the best record
    /// in the entire history — previously it was the winner of the last iteration.
    /// </summary>
    public class WorkflowControllerChampionTests
    {
        private static ModelRecord Record(int iteration, int variant, float fitness) => new ModelRecord
        {
            Iteration = iteration,
            Variant = variant,
            Fitness = fitness
        };

        [Fact]
        public void Picks_Best_Fitness_Not_Last_Entry()
        {
            var history = new List<ModelRecord>
            {
                Record(1, 1, 5.0f),
                Record(1, 2, 42.0f),   // best run, but not at the end
                Record(2, 1, 7.0f),
                Record(2, 2, 9.0f)     // winner of the last iteration
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.NotNull(best);
            Assert.Equal(1, best!.Iteration);
            Assert.Equal(2, best.Variant);
            Assert.Equal(42.0f, best.Fitness);
        }

        /// <summary>
        /// Disqualified (bouncer) and failed models carry a very small fitness —
        /// they must never win as long as a valid model exists.
        /// </summary>
        [Fact]
        public void Ignores_Disqualified_And_Failed_Records()
        {
            var failed = Record(1, 1, 0.0001f);
            failed.SimulationFailed = true;

            var history = new List<ModelRecord>
            {
                failed,
                Record(1, 2, 0.0001f),  // disqualified by ChampionValidator
                Record(1, 3, 1.5f)
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.Equal(3, best!.Variant);
        }

        /// <summary>On a tie, the earlier entry wins — otherwise selection would be arbitrary.</summary>
        [Fact]
        public void Ties_Go_To_The_Earlier_Record()
        {
            var history = new List<ModelRecord>
            {
                Record(1, 1, 3.0f),
                Record(2, 1, 3.0f)
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.Equal(1, best!.Iteration);
        }

        [Fact]
        public void Empty_History_Yields_Null()
        {
            Assert.Null(WorkflowController.SelectBestRecord(new List<ModelRecord>()));
        }
    }

    /// <summary>
    /// TODO-19: Controller orchestration using exclusively mocked interfaces —
    /// without PicoGK, Gmsh, or SU2. Tests what the controller itself owns:
    /// solver-chain ordering, metric adoption, mesh caching, and behavior on
    /// meshing or solver failures.
    /// </summary>
    public class WorkflowControllerOrchestrationTests : IDisposable
    {
        private readonly string _workingDirectory;

        public WorkflowControllerOrchestrationTests()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), "wfctest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, recursive: true);
        }

        private SimulationContext Context(int iterations = 1, int variants = 2, bool useScaler = false)
        {
            var config = new SimulationConfig
            {
                MaxIterations = iterations,
                VariantsPerIteration = variants,
                UseRubberBandScaler = useScaler,
                TargetPicoGkSize = 50f,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" }
            };

            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { { "Length", 100f } },
                MaxDeviations = new Dictionary<string, float> { { "Length", 5f } },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { { "Length", (30f, 200f) } },
                DimensionalParameters = new HashSet<string> { "Length" }
            };

            return new SimulationContext(config, project, _workingDirectory);
        }

        /// <summary>Geometry stub: writes nothing, returns fixed metrics.</summary>
        private static IGeometryGenerator Geometry(params (string Name, float Value, MetricScaling Scaling)[] metrics)
        {
            var mock = new Mock<IGeometryGenerator>();
            mock.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns((int iter, int var_, Dictionary<string, float> p, string dir, SimulationConfig c) =>
                {
                    var result = new GeometryResult { StlPath = $"model_{iter}_{var_}.stl" };
                    foreach (var metric in metrics) result.AddMetric(metric.Name, metric.Value, metric.Scaling);
                    return result;
                });
            return mock.Object;
        }

        /// <summary>Optimizer stub: returns base parameters and scores by variant number.</summary>
        private static IOptimizationAlgorithm Optimizer()
        {
            var mock = new Mock<IOptimizationAlgorithm>();
            mock.SetupGet(o => o.Name).Returns("Test-Optimizer");
            mock.Setup(o => o.GenerateParameters(
                    It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns((SimulationContext ctx, int iter, int var_, int max) =>
                    new Dictionary<string, float> { { "Length", 100f + var_ } });
            mock.Setup(o => o.EvaluateAndSelectBest(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationContext>()))
                .Returns((List<ModelRecord> records, SimulationContext ctx) =>
                {
                    foreach (var record in records) record.Fitness = record.Variant;
                    return records.OrderByDescending(r => r.Fitness).First();
                });
            return mock.Object;
        }

        /// <summary>Validator stub: always confirms the candidate with the highest fitness.</summary>
        private static IModelValidator PassthroughValidator()
        {
            var mock = new Mock<IModelValidator>();
            mock.Setup(v => v.ValidateChampion(
                    It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(),
                    It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                .Returns((List<ModelRecord> c, SimulationConfig cfg, Func<Dictionary<string, float>, ModelRecord> re)
                    => c.OrderByDescending(r => r.Fitness).First());
            return mock.Object;
        }

        private static IMeshGenerator Mesher(List<string> log, string name, bool throws = false)
        {
            var mock = new Mock<IMeshGenerator>();
            mock.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                .Returns((MeshRequest req) =>
                {
                    log.Add(name);
                    if (throws) throw new InvalidOperationException($"{name} cannot mesh this model");
                    return $"{name}_{req.Iteration}_{req.Variant}.su2";
                });
            return mock.Object;
        }

        private static ISimulationSolver Solver(List<string> log, string name, bool throws = false)
        {
            var mock = new Mock<ISimulationSolver>();
            mock.SetupGet(s => s.Name).Returns(name);
            mock.Setup(s => s.Solve(
                    It.IsAny<string>(), It.IsAny<ModelRecord>(),
                    It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                .Callback((string mesh, ModelRecord record, SimulationConfig c, string dir) =>
                {
                    log.Add(name);
                    if (throws) throw new InvalidOperationException($"{name} crashed");
                    record.PassiveParameters[name + "Result"] = 1f;
                });
            return mock.Object;
        }

        [Fact]
        public void Solver_Chain_Runs_In_Order_For_Every_Variant()
        {
            var log = new List<string>();
            var stages = new[]
            {
                new SolverStage(Mesher(log, "MesherA"), Solver(log, "SolverA")),
                new SolverStage(Mesher(log, "MesherB"), Solver(log, "SolverB"))
            };

            var context = Context(iterations: 1, variants: 1);
            new WorkflowController(Geometry(), stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(new[] { "MesherA", "SolverA", "MesherB", "SolverB" }, log);
        }

        /// <summary>
        /// Stages sharing a mesher INSTANCE mesh only once (TODO-4).
        /// The record carries the path from the first stage.
        /// </summary>
        [Fact]
        public void Shared_Mesher_Instance_Meshes_Only_Once()
        {
            var log = new List<string>();
            var shared = Mesher(log, "Shared");
            var stages = new[]
            {
                new SolverStage(shared, Solver(log, "SolverA")),
                new SolverStage(shared, Solver(log, "SolverB"))
            };

            var context = Context(iterations: 1, variants: 1);
            new WorkflowController(Geometry(), stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(new[] { "Shared", "SolverA", "SolverB" }, log);
            Assert.Equal("Shared_1_1.su2", context.History[0].MeshPath);
        }

        /// <summary>
        /// Metrics declared by the project land in the record — including rescaling.
        /// With the scaler disabled, rescaling is the identity.
        /// </summary>
        [Fact]
        public void Geometry_Metrics_Land_In_The_Record()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var geometry = Geometry(
                ("Volume", 4200f, MetricScaling.Volume),
                ("FrontalArea", 850f, MetricScaling.Area),
                ("TailTaper", 1.25f, MetricScaling.None));

            var context = Context(iterations: 1, variants: 1);
            new WorkflowController(geometry, stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            var record = context.History.Single();
            Assert.Equal(4200f, record.PassiveParameters["Volume"], 3);
            Assert.Equal(850f, record.PassiveParameters["FrontalArea"], 3);
            Assert.Equal(1.25f, record.PassiveParameters["TailTaper"], 3);
            Assert.Equal(1f, record.PassiveParameters["ScaleFactor"], 5);   // scaler off
            Assert.Equal("model_1_1.stl", record.StlPath);
            Assert.Equal(1f, record.PassiveParameters["SResult"], 5);        // written by solver
            Assert.False(record.SimulationFailed);
        }

        /// <summary>
        /// Scaler enabled: the controller shrinks parameters for geometry generation
        /// and rescales metrics dimension-aware back to full size.
        /// </summary>
        [Fact]
        public void Metrics_Are_Restored_Dimension_Aware_When_The_Scaler_Runs()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            // Length = 101 (variant 1), target size 50 -> ShrinkFactor ~0.495
            var geometry = Geometry(
                ("Volume", 1f, MetricScaling.Volume),
                ("Area", 1f, MetricScaling.Area),
                ("Length", 1f, MetricScaling.Linear),
                ("Ratio", 1f, MetricScaling.None));

            var context = Context(iterations: 1, variants: 1, useScaler: true);
            new WorkflowController(geometry, stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            var record = context.History.Single();
            float shrink = record.PassiveParameters["ScaleFactor"];
            Assert.Equal(50f / 101f, shrink, 5);

            Assert.Equal(MathF.Pow(1f / shrink, 3), record.PassiveParameters["Volume"], 3);
            Assert.Equal(MathF.Pow(1f / shrink, 2), record.PassiveParameters["Area"], 3);
            Assert.Equal(1f / shrink, record.PassiveParameters["Length"], 3);
            Assert.Equal(1f, record.PassiveParameters["Ratio"], 5);   // dimensionless
        }

        /// <summary>
        /// A mesher failure must not abort the entire run: the record is marked
        /// failed and the loop continues (TODO-2).
        /// </summary>
        [Fact]
        public void Failing_Mesher_Marks_The_Record_But_Does_Not_Abort_The_Run()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M", throws: true), Solver(log, "S")) };

            var context = Context(iterations: 2, variants: 2);
            new WorkflowController(Geometry(("Volume", 10f, MetricScaling.Volume)), stages,
                    PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(4, context.History.Count);
            Assert.All(context.History, r => Assert.True(r.SimulationFailed));
            Assert.DoesNotContain("S", log);                        // solver never ran
            Assert.All(context.History, r => Assert.Equal(10f, r.PassiveParameters["Volume"], 3));
        }

        [Fact]
        public void Failing_Solver_Marks_The_Record_But_Does_Not_Abort_The_Run()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S", throws: true)) };

            var context = Context(iterations: 2, variants: 2);
            new WorkflowController(Geometry(("Volume", 10f, MetricScaling.Volume)), stages,
                    PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(4, context.History.Count);
            Assert.All(context.History, r => Assert.True(r.SimulationFailed));
        }

        /// <summary>All variants of all iterations are recorded in History and exported to CSV.</summary>
        [Fact]
        public void Every_Variant_Is_Recorded_And_Exported()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var context = Context(iterations: 3, variants: 4);
            new WorkflowController(Geometry(("Volume", 1f, MetricScaling.Volume)), stages,
                    PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(12, context.History.Count);
            Assert.Equal(new[] { 1, 2, 3 }, context.History.Select(r => r.Iteration).Distinct());
            Assert.Equal(new[] { 1, 2, 3, 4 }, context.History.Select(r => r.Variant).Distinct());

            string csv = Path.Combine(_workingDirectory, OutputConventions.ResultsCsvFileName);
            Assert.True(File.Exists(csv), $"{OutputConventions.ResultsCsvFileName} was not written.");
            Assert.Equal(13, File.ReadAllLines(csv).Length);   // header + 12 records
        }

        /// <summary>
        /// The validator receives only the records of the current iteration — not the full history —
        /// and its re-simulation callback runs the same pipeline under variant 99.
        /// </summary>
        [Fact]
        public void Validator_Sees_The_Current_Iteration_And_Can_Resimulate()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var seenCounts = new List<int>();
            ModelRecord? resimulated = null;

            var validator = new Mock<IModelValidator>();
            validator.Setup(v => v.ValidateChampion(
                    It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(),
                    It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                .Returns((List<ModelRecord> c, SimulationConfig cfg, Func<Dictionary<string, float>, ModelRecord> re) =>
                {
                    seenCounts.Add(c.Count);
                    resimulated = re(new Dictionary<string, float> { { "Length", 60f } });
                    return c.OrderByDescending(r => r.Fitness).First();
                });

            var context = Context(iterations: 2, variants: 3);
            new WorkflowController(Geometry(), stages, validator.Object, Optimizer(), context)
                .RunOptimization();

            Assert.Equal(new[] { 3, 3 }, seenCounts);              // per iteration only its variants
            Assert.NotNull(resimulated);
            Assert.Equal(99, resimulated!.Variant);                // ValidationVariantNumber
            Assert.Equal(6, context.History.Count);                // validation runs do not count
        }

        // -------------------------------------------------------------------
        // TODO-26: Required fitness metrics
        // -------------------------------------------------------------------

        /// <summary>
        /// If a metric required by the fitness formula is missing, the run aborts after the first
        /// variant — instead of running for hours with a fallback fitness.
        /// </summary>
        [Fact]
        public void A_Missing_Required_Metric_Stops_The_Run_After_The_First_Variant()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            // Geometry reports Volume, but no SensorDistance.
            var geometry = Geometry(("Volume", 4200f, MetricScaling.Volume));
            var context = Context(iterations: 5, variants: 4);

            var controller = new WorkflowController(
                geometry, stages, PassthroughValidator(), Optimizer(), context,
                new[] { "Volume", "SensorDistance" });

            var error = Assert.Throws<InvalidOperationException>(() => controller.RunOptimization());

            Assert.Contains("SensorDistance", error.Message);
            Assert.Contains("Present:", error.Message);
            // Exactly one variant computed, not 5 x 4 = 20.
            Assert.Equal(1, log.Count(entry => entry == "S"));
        }

        [Fact]
        public void Complete_Required_Metrics_Let_The_Run_Pass()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var geometry = Geometry(
                ("Volume", 4200f, MetricScaling.Volume),
                ("SensorDistance", 12f, MetricScaling.Area));

            var context = Context(iterations: 1, variants: 2);

            new WorkflowController(
                    geometry, stages, PassthroughValidator(), Optimizer(), context,
                    new[] { "Volume", "SensorDistance" })
                .RunOptimization();

            Assert.Equal(2, context.History.Count);
        }

        /// <summary>
        /// Without specification nothing is checked — existing wiring remains unchanged.
        /// </summary>
        [Fact]
        public void Without_Required_Metrics_Nothing_Is_Checked()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var context = Context(iterations: 1, variants: 2);
            new WorkflowController(Geometry(), stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(2, context.History.Count);
        }

        /// <summary>
        /// A crashed solver is not a configuration error: the check waits for the first
        /// variant that actually succeeded. Otherwise a single SU2 crash in variant 1
        /// would be reported as a missing metric.
        /// </summary>
        [Fact]
        public void A_Failed_First_Variant_Does_Not_Trigger_The_Check()
        {
            var log = new List<string>();

            // The solver throws only on the very first call.
            int calls = 0;
            var solver = new Mock<ISimulationSolver>();
            solver.SetupGet(s => s.Name).Returns("Flaky");
            solver.Setup(s => s.Solve(
                    It.IsAny<string>(), It.IsAny<ModelRecord>(),
                    It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                .Callback((string mesh, ModelRecord record, SimulationConfig c, string dir) =>
                {
                    calls++;
                    log.Add("S");
                    if (calls == 1) throw new InvalidOperationException("SU2 crashed");
                    record.PassiveParameters["Drag"] = 0.5f;
                });

            var stages = new[] { new SolverStage(Mesher(log, "M"), solver.Object) };
            var context = Context(iterations: 1, variants: 3);

            new WorkflowController(
                    Geometry(), stages, PassthroughValidator(), Optimizer(), context,
                    new[] { "Drag" })
                .RunOptimization();

            // Variant 1 failed and was skipped; variant 2 provides Drag.
            Assert.True(context.History[0].SimulationFailed);
            Assert.Equal(3, context.History.Count);
        }

        [Fact]
        public void WorkflowController_Records_MeshCells_When_Su2_Has_Nelem()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "meshcells_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                string dummyMesh = Path.Combine(tempDir, "sample.su2");
                File.WriteAllLines(dummyMesh, new[]
                {
                    "NDIME= 3",
                    "NELEM= 48120",
                    "NPOIN= 9200"
                });

                var mesherMock = new Mock<IMeshGenerator>();
                mesherMock.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                    .Returns(dummyMesh);

                var stages = new[] { new SolverStage(mesherMock.Object, Solver(new List<string>(), "S")) };
                var context = Context(iterations: 1, variants: 1);
                context.WorkingDirectory = tempDir;

                new WorkflowController(Geometry(), stages, PassthroughValidator(), Optimizer(), context)
                    .RunOptimization();

                var record = context.History.Single();
                Assert.True(record.PassiveParameters.ContainsKey("MeshCells"));
                Assert.Equal(48120f, record.PassiveParameters["MeshCells"]);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        [Theory]
        [InlineData(0f, "0")]
        [InlineData(50f, "50")]
        [InlineData(50.5f, "50.5")]
        [InlineData(50.555f, "50.56")]
        [InlineData(0.02f, "0.02")]
        [InlineData(0.0025f, "0.0025")]
        [InlineData(-0.1234f, "-0.1234")]
        [InlineData(-10.5f, "-10.5")]
        public void FormatParameterValue_Formats_Correctly(float value, string expected)
        {
            string actual = WorkflowController.FormatParameterValue(value);
            Assert.Equal(expected, actual);
        }
    }
}
