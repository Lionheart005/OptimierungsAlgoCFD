using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using FluidForge.Core;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace FluidForge.Tests
{
    public sealed class PythonOptFactAttribute : FactAttribute
    {
        private static readonly Lazy<string?> SkipReason = new(ProbeUnavailableReason);

        public PythonOptFactAttribute()
        {
            if (SkipReason.Value != null)
            {
                Skip = SkipReason.Value;
            }
        }

        private static string? ProbeUnavailableReason()
        {
            string? py = ProcessExternalOptimizerTransport.ResolvePythonExecutable(new ExternalOptimizerConfig());
            if (string.IsNullOrWhiteSpace(py) || !File.Exists(py))
                return "Python opt-py interpreter not found on this machine.";

            string? botorchWorker = ProcessExternalOptimizerTransport.ResolveWorkerScript(new ExternalOptimizerConfig(), "BoTorch");
            if (string.IsNullOrWhiteSpace(botorchWorker) || !File.Exists(botorchWorker))
                return "botorch_worker.py not found.";

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = py,
                    Arguments = $"\"{botorchWorker}\" --check",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                };
                psi.Environment["PYTHONWARNINGS"] = "ignore";
                using var proc = Process.Start(psi);
                if (proc == null) return "Failed to start Python --check process.";
                if (!proc.WaitForExit(15000))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    return "Python opt-py --check timed out.";
                }
                return proc.ExitCode == 0 ? null : $"Python opt-py --check failed (exit {proc.ExitCode}).";
            }
            catch (Exception ex)
            {
                return $"Python opt-py probe failed: {ex.Message}";
            }
        }
    }

    public class ExternalOptimizerTests : TempDirectoryTest
    {
        private readonly ITestOutputHelper _output;

        public ExternalOptimizerTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private sealed class SyntheticTwoObjectiveEvaluator : ObjectiveEvaluatorBase
        {
            public override IReadOnlyCollection<string> RequiredMetrics => new[] { "F1", "F2" };

            public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
            {
                float x1 = record.ActiveParameters["X1"];
                float x2 = record.ActiveParameters["X2"];
                float f1 = x1 * x1 + (x2 - 0.5f) * (x2 - 0.5f);
                float f2 = (x1 - 1.0f) * (x1 - 1.0f) + (x2 - 0.5f) * (x2 - 0.5f);
                float dist2 = (x1 - 0.5f) * (x1 - 0.5f) + (x2 - 0.5f) * (x2 - 0.5f);

                record.PassiveParameters["F1"] = f1;
                record.PassiveParameters["F2"] = f2;

                return new EvaluationOutcome
                {
                    Objectives =
                    {
                        new ObjectiveValue("F1", f1, OptimizationDirection.Minimize, Weight: 1.0f),
                        new ObjectiveValue("F2", f2, OptimizationDirection.Minimize, Weight: 1.0f)
                    },
                    Constraints =
                    {
                        ConstraintEvaluation.AtMost("CircleLimit", dist2, 0.20f)
                    }
                };
            }
        }

        private static SimulationContext Create2DContext(
            string workDir,
            string algorithm = "BoTorch",
            string acquisition = "Auto",
            int iterations = 3,
            int variants = 4,
            bool fallback = true)
        {
            var config = new SimulationConfig
            {
                MaxIterations = iterations,
                VariantsPerIteration = variants,
                OptimizationAlgorithm = algorithm,
                UseRubberBandScaler = false,
                BouncerTolerance = 0.25f,
                BouncerJitter = 0.01f,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" },
                ExternalOptimizer = new ExternalOptimizerConfig
                {
                    Acquisition = acquisition,
                    TimeoutSeconds = 120,
                    FallbackToBuiltInBayes = fallback,
                    KeepExchangeFiles = true
                }
            };

            var project = new ProjectConfig
            {
                ProjectName = "ParetoBench2D",
                BaseParameters = new Dictionary<string, float> { ["X1"] = 0.5f, ["X2"] = 0.5f },
                MaxDeviations = new Dictionary<string, float> { ["X1"] = 0.1f, ["X2"] = 0.1f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    ["X1"] = (0.0f, 1.0f),
                    ["X2"] = (0.0f, 1.0f)
                }
            };

            var ctx = new SimulationContext(config, project, workDir);
            ctx.EnsureDirectories();
            return ctx;
        }

        [Fact]
        public void Protocol_Roundtrip_Preserves_Version_Constraints_And_Named_Floats()
        {
            Assert.Equal("1.0", ExternalOptimizerProtocol.CurrentProtocolVersion);

            var request = new ExternalOptimizerRequest
            {
                Algorithm = "BoTorch",
                Acquisition = "qNEHVI",
                Iteration = 2,
                BatchSize = 4,
                Seed = 123,
                MultiFidelity = true,
                AvailableFidelities = new[] { 0.5, 1.0 },
                Parameters =
                {
                    new ExternalParameterSpec { Name = "X1", Min = 0.0, Max = 1.0, BaseValue = 0.5 }
                },
                Objectives =
                {
                    new ExternalObjectiveSpec { Name = "Drag", Direction = "Minimize", ReferencePoint = 0.1 }
                },
                Constraints =
                {
                    new ExternalConstraintSpec { Name = "MinLift", IsPreSolve = false }
                },
                History =
                {
                    new ExternalObservation
                    {
                        Iteration = 1,
                        Variant = 1,
                        Status = "Feasible",
                        Fidelity = 0.5,
                        Fitness = double.NaN,
                        Parameters = new Dictionary<string, double> { ["X1"] = 0.4 },
                        Objectives = new Dictionary<string, double> { ["Drag"] = 0.025 },
                        ConstraintViolations = new Dictionary<string, double> { ["MinLift"] = -0.05 }
                    }
                }
            };

            string json = ExternalOptimizerProtocol.SerializeRequest(request);
            ExternalOptimizerRequest? parsed = ExternalOptimizerProtocol.DeserializeRequest(json);

            Assert.NotNull(parsed);
            Assert.Equal("1.0", parsed!.ProtocolVersion);
            Assert.Equal("BoTorch", parsed.Algorithm);
            Assert.True(parsed.MultiFidelity);
            Assert.Equal(new[] { 0.5, 1.0 }, parsed.AvailableFidelities);
            Assert.Single(parsed.History);
            Assert.True(double.IsNaN(parsed.History[0].Fitness!.Value));
            Assert.Equal(-0.05, parsed.History[0].ConstraintViolations["MinLift"], 6);

            string respJson = @"{
                ""protocolVersion"": ""1.0"",
                ""worker"": ""botorch_worker"",
                ""backendVersion"": ""0.18.1"",
                ""candidates"": [{ ""parameters"": { ""X1"": 0.62 }, ""fidelity"": 1.0 }],
                ""ardImportances"": { ""X1"": 1.0 },
                ""paretoMask"": [true],
                ""hypervolume"": 0.123
            }";
            ExternalOptimizerResponse? resp = ExternalOptimizerProtocol.DeserializeResponse(respJson);
            Assert.NotNull(resp);
            Assert.Equal("botorch_worker", resp!.Worker);
            Assert.Equal(0.62, resp.Candidates[0].Parameters["X1"], 6);
            Assert.Equal(0.123, resp.Hypervolume!.Value, 6);
        }

        [Fact]
        public void ParetoFrontCalculator_Computes_Mixed_Directions_And_Excludes_Invalid_Records()
        {
            var records = new List<ModelRecord>
            {
                // 0: Pareto-optimal (low drag, moderate lift)
                new()
                {
                    Iteration = 1, Variant = 1, Feasible = true,
                    Objectives = { new("Drag", 0.02f, OptimizationDirection.Minimize), new("Lift", 0.50f, OptimizationDirection.Maximize) }
                },
                // 1: Dominated by 0 (higher drag, lower lift)
                new()
                {
                    Iteration = 1, Variant = 2, Feasible = true,
                    Objectives = { new("Drag", 0.03f, OptimizationDirection.Minimize), new("Lift", 0.45f, OptimizationDirection.Maximize) }
                },
                // 2: Pareto-optimal (higher drag, higher lift)
                new()
                {
                    Iteration = 1, Variant = 3, Feasible = true,
                    Objectives = { new("Drag", 0.04f, OptimizationDirection.Minimize), new("Lift", 0.80f, OptimizationDirection.Maximize) }
                },
                // 3: Infeasible -> excluded even though dominates both
                new()
                {
                    Iteration = 1, Variant = 4, Feasible = false,
                    Objectives = { new("Drag", 0.01f, OptimizationDirection.Minimize), new("Lift", 0.95f, OptimizationDirection.Maximize) }
                },
                // 4: Disqualified -> excluded
                new()
                {
                    Iteration = 1, Variant = 5, Feasible = true, Disqualified = true,
                    Objectives = { new("Drag", 0.01f, OptimizationDirection.Minimize), new("Lift", 0.90f, OptimizationDirection.Maximize) }
                },
                // 5: SimulationFailed -> excluded
                new()
                {
                    Iteration = 1, Variant = 6, Feasible = true, SimulationFailed = true,
                    Objectives = { new("Drag", 0.01f, OptimizationDirection.Minimize), new("Lift", 0.90f, OptimizationDirection.Maximize) }
                }
            };

            bool[] mask = ParetoFrontCalculator.ComputeParetoMask(records);
            Assert.Equal(new[] { true, false, true, false, false, false }, mask);
        }

        [Fact]
        public void ParetoFrontCalculator_ComputeHypervolume2D_Is_Exact()
        {
            var points = new List<(double F1, double F2)>
            {
                (1.0, 4.0),
                (2.0, 2.0),
                (4.0, 1.0),
                (3.0, 3.0), // dominated by (2, 2)
                (6.0, 0.5)  // outside reference point (5, 5)
            };

            double hv = ParetoFrontCalculator.ComputeHypervolume2D(points, (5.0, 5.0));
            // Rectangles: [1,2]x[4,5]=1 + [2,4]x[2,5]=6 + [4,5]x[1,5]=4 => 11.0
            Assert.Equal(11.0, hv, 8);
        }

        [Fact]
        public void ParetoFrontCalculator_ExportParetoSummary_Writes_Json_And_Copies_Only_Pareto_Stls()
        {
            string stl1 = Path.Combine(Root, "geom_1_1.stl");
            string stl2 = Path.Combine(Root, "geom_1_2.stl");
            string stl3 = Path.Combine(Root, "geom_1_3.stl");
            File.WriteAllText(stl1, "solid s1\nendsolid s1\n");
            File.WriteAllText(stl2, "solid s2\nendsolid s2\n");
            File.WriteAllText(stl3, "solid s3\nendsolid s3\n");

            var history = new List<ModelRecord>
            {
                new()
                {
                    Iteration = 1, Variant = 1, Feasible = true, StlPath = stl1,
                    ActiveParameters = new() { ["X1"] = 0.2f },
                    Objectives = { new("F1", 0.1f, OptimizationDirection.Minimize), new("F2", 0.9f, OptimizationDirection.Minimize) }
                },
                new()
                {
                    Iteration = 1, Variant = 2, Feasible = true, StlPath = stl2,
                    ActiveParameters = new() { ["X1"] = 0.5f },
                    Objectives = { new("F1", 0.5f, OptimizationDirection.Minimize), new("F2", 0.95f, OptimizationDirection.Minimize) } // dominated by var 1
                },
                new()
                {
                    Iteration = 1, Variant = 3, Feasible = true, StlPath = stl3,
                    ActiveParameters = new() { ["X1"] = 0.8f },
                    Objectives = { new("F1", 0.8f, OptimizationDirection.Minimize), new("F2", 0.2f, OptimizationDirection.Minimize) }
                }
            };

            ParetoSummary summary = ParetoFrontCalculator.ExportParetoSummary(Root, history, copyStls: true);

            Assert.Equal(2, summary.ParetoCount);
            Assert.True(summary.Hypervolume > 0);
            Assert.True(File.Exists(Path.Combine(Root, "pareto_front.json")));
            Assert.True(File.Exists(Path.Combine(Root, "pareto", "pareto_Gen1_Var1.stl")));
            Assert.False(File.Exists(Path.Combine(Root, "pareto", "pareto_Gen1_Var2.stl")));
            Assert.True(File.Exists(Path.Combine(Root, "pareto", "pareto_Gen1_Var3.stl")));
        }

        [Theory]
        [InlineData("BoTorch")]
        [InlineData("botorch")]
        [InlineData("Pymoo")]
        [InlineData("pymoo")]
        [InlineData("ExternalPython")]
        [InlineData("  externalpython  ")]
        public void OptimizationAlgorithmFactory_Resolves_External_Algorithms_With_3Arg_Signature(string name)
        {
            var fitness = new Mock<IFitnessCalculator>().Object;
            IOptimizationAlgorithm algo = OptimizationAlgorithmFactory.Create(name, fitness, new Random(7));
            Assert.IsType<ExternalProcessOptimizer>(algo);
            Assert.Contains("BoTorch", OptimizationAlgorithmFactory.SupportedAlgorithms);
            Assert.Contains("Pymoo", OptimizationAlgorithmFactory.SupportedAlgorithms);
            Assert.Contains("ExternalPython", OptimizationAlgorithmFactory.SupportedAlgorithms);
        }

        [Fact]
        public void ExternalProcessOptimizer_Preflight_FallsBack_To_Bayes_When_Unavailable()
        {
            var transport = new Mock<IExternalOptimizerTransport>();
            string details = "opt-py missing";
            transport.Setup(t => t.CheckAvailable(It.IsAny<SimulationContext>(), out details)).Returns(false);

            SimulationContext ctx = Create2DContext(Sub("fallback_on"), fallback: true);
            var optimizer = new ExternalProcessOptimizer(new SyntheticTwoObjectiveEvaluator(), "BoTorch", transport.Object, new Random(1));

            optimizer.Preflight(ctx);
            Assert.True(optimizer.IsUsingFallback);

            Dictionary<string, float> p = optimizer.GenerateParameters(ctx, 1, 1, 4);
            Assert.Equal(0.5f, p["X1"], 5);
            Assert.Equal(0.5f, p["X2"], 5);
            transport.Verify(t => t.Ask(It.IsAny<ExternalOptimizerRequest>(), It.IsAny<SimulationContext>(), It.IsAny<string>(), It.IsAny<string>()), Times.Never());
        }

        [Fact]
        public void ExternalProcessOptimizer_Preflight_Throws_When_Unavailable_And_FallbackDisabled()
        {
            var transport = new Mock<IExternalOptimizerTransport>();
            string details = "opt-py missing";
            transport.Setup(t => t.CheckAvailable(It.IsAny<SimulationContext>(), out details)).Returns(false);

            SimulationContext ctx = Create2DContext(Sub("fallback_off"), fallback: false);
            var optimizer = new ExternalProcessOptimizer(new SyntheticTwoObjectiveEvaluator(), "BoTorch", transport.Object, new Random(1));

            var ex = Assert.Throws<InvalidOperationException>(() => optimizer.Preflight(ctx));
            Assert.Contains("environment_opt.yml", ex.Message);
        }

        [Fact]
        public void ExternalProcessOptimizer_Runtime_Error_FallsBack_Cleanly_When_Enabled()
        {
            var transport = new Mock<IExternalOptimizerTransport>();
            string details = "ok";
            transport.Setup(t => t.CheckAvailable(It.IsAny<SimulationContext>(), out details)).Returns(true);
            transport.Setup(t => t.Ask(It.IsAny<ExternalOptimizerRequest>(), It.IsAny<SimulationContext>(), It.IsAny<string>(), It.IsAny<string>()))
                .Throws(new InvalidOperationException("worker crashed"));

            SimulationContext ctx = Create2DContext(Sub("runtime_fallback"), fallback: true);
            var optimizer = new ExternalProcessOptimizer(new SyntheticTwoObjectiveEvaluator(), "BoTorch", transport.Object, new Random(1));

            optimizer.Preflight(ctx);
            Assert.False(optimizer.IsUsingFallback);

            Dictionary<string, float> p = optimizer.GenerateParameters(ctx, 1, 1, 4);
            Assert.True(optimizer.IsUsingFallback);
            Assert.InRange(p["X1"], 0f, 1f);
        }

        [Fact]
        public void ExternalProcessOptimizer_Batches_Ask_Clamps_Bounds_And_Exports_Pareto()
        {
            var transport = new Mock<IExternalOptimizerTransport>();
            string details = "mock ready";
            transport.Setup(t => t.CheckAvailable(It.IsAny<SimulationContext>(), out details)).Returns(true);
            transport.Setup(t => t.Ask(It.IsAny<ExternalOptimizerRequest>(), It.IsAny<SimulationContext>(), It.IsAny<string>(), It.IsAny<string>()))
                .Returns((ExternalOptimizerRequest req, SimulationContext _, string _, string _) => new ExternalOptimizerResponse
                {
                    ProtocolVersion = "1.0",
                    Worker = "mock_worker",
                    BackendVersion = "1.0",
                    ArdImportances = new Dictionary<string, double> { ["X1"] = 0.7, ["X2"] = 0.3 },
                    ParetoMask = Enumerable.Repeat(true, req.History.Count).ToList(),
                    Hypervolume = 0.42,
                    Candidates = new List<ExternalCandidate>
                    {
                        new() { Parameters = new() { ["X1"] = -0.5, ["X2"] = 0.5 } },  // out of lower bound -> clamp to 0.0
                        new() { Parameters = new() { ["X1"] = 1.8, ["X2"] = 0.5 } },   // out of upper bound -> clamp to 1.0
                        new() { Parameters = new() { ["X1"] = 0.4, ["X2"] = 0.5 } }
                    }
                });

            SimulationContext ctx = Create2DContext(Sub("batch_clamp"), variants: 3, fallback: false);
            ctx.Project.BaseParameters["FixedParam"] = 42.0f;
            ctx.Project.ParameterBounds["FixedParam"] = (42.0f, 42.0f);
            ctx.ResetRuntimeState();

            var evaluator = new SyntheticTwoObjectiveEvaluator();
            var optimizer = new ExternalProcessOptimizer(evaluator, "BoTorch", transport.Object, new Random(9));
            optimizer.Preflight(ctx);

            var p1 = optimizer.GenerateParameters(ctx, 1, 1, 3);
            var p2 = optimizer.GenerateParameters(ctx, 1, 2, 3);
            var p3 = optimizer.GenerateParameters(ctx, 1, 3, 3);

            // Ask called exactly once for the 3-variant batch
            transport.Verify(t => t.Ask(
                It.Is<ExternalOptimizerRequest>(r => r.BatchSize == 3 && r.Parameters.Count == 2),
                It.IsAny<SimulationContext>(),
                It.IsAny<string>(),
                It.IsAny<string>()), Times.Once());

            Assert.Equal(0.0f, p1["X1"], 5);
            Assert.Equal(1.0f, p2["X1"], 5);
            Assert.Equal(0.4f, p3["X1"], 5);
            Assert.Equal(42.0f, p1["FixedParam"], 5);
            Assert.Equal(0.7, optimizer.LastArdImportances["X1"], 5);

            var records = new List<ModelRecord>
            {
                new() { Iteration = 1, Variant = 1, ActiveParameters = p1 },
                new() { Iteration = 1, Variant = 2, ActiveParameters = p2 },
                new() { Iteration = 1, Variant = 3, ActiveParameters = p3 }
            };
            ctx.History.AddRange(records);
            optimizer.EvaluateAndSelectBest(records, ctx);

            Assert.True(File.Exists(Path.Combine(ctx.WorkingDirectory, "pareto_front.json")));
        }

        [PythonOptFact]
        [Trait("Category", "Integration")]
        [Trait("Category", "PythonOpt")]
        public void BoTorch_qNEHVI_2Obj_Constrained_Pareto_Live()
        {
            var sw = Stopwatch.StartNew();
            SimulationContext ctx = Create2DContext(Sub("live_botorch"), algorithm: "BoTorch", acquisition: "qNEHVI", iterations: 5, variants: 4, fallback: false);
            var evaluator = new SyntheticTwoObjectiveEvaluator();
            var optimizer = new ExternalProcessOptimizer(evaluator, "BoTorch", random: new Random(42));

            optimizer.Preflight(ctx);
            Assert.False(optimizer.IsUsingFallback);

            for (int iter = 1; iter <= ctx.Config.MaxIterations; iter++)
            {
                var iterRecords = new List<ModelRecord>();
                for (int varIdx = 1; varIdx <= ctx.Config.VariantsPerIteration; varIdx++)
                {
                    Dictionary<string, float> active = optimizer.GenerateParameters(ctx, iter, varIdx, ctx.Config.VariantsPerIteration);
                    string stl = Path.Combine(ctx.WorkingDirectory, $"model_g{iter}_v{varIdx}.stl");
                    File.WriteAllText(stl, $"solid g{iter}_v{varIdx}\nendsolid g{iter}_v{varIdx}\n");

                    var rec = new ModelRecord
                    {
                        Iteration = iter,
                        Variant = varIdx,
                        ActiveParameters = active,
                        StlPath = stl
                    };
                    iterRecords.Add(rec);
                    ctx.History.Add(rec);
                }

                ModelRecord best = optimizer.EvaluateAndSelectBest(iterRecords, ctx);
                ctx.ExportToCsv();
                if (iter < ctx.Config.MaxIterations)
                {
                    optimizer.PrepareNextIteration(ctx, iterRecords, best);
                }
            }
            sw.Stop();

            string paretoJson = Path.Combine(ctx.WorkingDirectory, "pareto_front.json");
            Assert.True(File.Exists(paretoJson), "pareto_front.json was not created.");

            ParetoSummary summary = ParetoFrontCalculator.ExportParetoSummary(ctx.WorkingDirectory, ctx.History, copyStls: true);
            bool[] mask = ParetoFrontCalculator.ComputeParetoMask(ctx.History);
            var paretoRecords = ctx.History.Where((_, i) => mask[i]).ToList();

            _output.WriteLine(
                $"BoTorch qNEHVI live: Total={ctx.History.Count}, Feasible={summary.FeasibleCount}, " +
                $"Pareto={paretoRecords.Count}, HV={summary.Hypervolume:F5}, Elapsed={sw.Elapsed.TotalSeconds:F2}s");

            Assert.True(paretoRecords.Count >= 3, $"Expected >= 3 Pareto points, got {paretoRecords.Count}.");
            Assert.All(paretoRecords, r => Assert.True(r.Feasible));
            Assert.True(summary.Hypervolume.HasValue && summary.Hypervolume.Value > 0.0, $"Expected positive Hypervolume, got {summary.Hypervolume}.");
            Assert.NotNull(optimizer.LastResponse);
            Assert.True(optimizer.LastArdImportances.ContainsKey("X1") && optimizer.LastArdImportances.ContainsKey("X2"),
                "Expected ArdImportances to contain X1 and X2.");
            Assert.True(Directory.GetFiles(Path.Combine(ctx.WorkingDirectory, "pareto"), "*.stl").Length >= 3);
        }

        [PythonOptFact]
        [Trait("Category", "Integration")]
        [Trait("Category", "PythonOpt")]
        public void Pymoo_NSGA2_2Obj_Constrained_Pareto_Live()
        {
            var sw = Stopwatch.StartNew();
            SimulationContext ctx = Create2DContext(Sub("live_pymoo"), algorithm: "Pymoo", acquisition: "NSGA2", iterations: 5, variants: 6, fallback: false);
            var evaluator = new SyntheticTwoObjectiveEvaluator();
            var optimizer = new ExternalProcessOptimizer(evaluator, "Pymoo", random: new Random(42));

            optimizer.Preflight(ctx);
            Assert.False(optimizer.IsUsingFallback);

            for (int iter = 1; iter <= ctx.Config.MaxIterations; iter++)
            {
                var iterRecords = new List<ModelRecord>();
                for (int varIdx = 1; varIdx <= ctx.Config.VariantsPerIteration; varIdx++)
                {
                    Dictionary<string, float> active = optimizer.GenerateParameters(ctx, iter, varIdx, ctx.Config.VariantsPerIteration);
                    var rec = new ModelRecord
                    {
                        Iteration = iter,
                        Variant = varIdx,
                        ActiveParameters = active
                    };
                    iterRecords.Add(rec);
                    ctx.History.Add(rec);
                }

                ModelRecord best = optimizer.EvaluateAndSelectBest(iterRecords, ctx);
                ctx.ExportToCsv();
                if (iter < ctx.Config.MaxIterations)
                {
                    optimizer.PrepareNextIteration(ctx, iterRecords, best);
                }
            }
            sw.Stop();

            ParetoSummary summary = ParetoFrontCalculator.ExportParetoSummary(ctx.WorkingDirectory, ctx.History, copyStls: false);
            bool[] mask = ParetoFrontCalculator.ComputeParetoMask(ctx.History);
            var paretoRecords = ctx.History.Where((_, i) => mask[i]).ToList();

            _output.WriteLine(
                $"pymoo NSGA2 live: Total={ctx.History.Count}, Feasible={summary.FeasibleCount}, " +
                $"Pareto={paretoRecords.Count}, HV={summary.Hypervolume:F5}, Elapsed={sw.Elapsed.TotalSeconds:F2}s");

            Assert.True(paretoRecords.Count >= 3, $"Expected >= 3 Pareto points, got {paretoRecords.Count}.");
            Assert.All(paretoRecords, r => Assert.True(r.Feasible));
            Assert.True(summary.Hypervolume.HasValue && summary.Hypervolume.Value > 0.0);
        }

        [PythonOptFact]
        [Trait("Category", "Integration")]
        [Trait("Category", "PythonOpt")]
        public void PlotPareto_Generates_Plots_And_Exports_Stls_Live()
        {
            string workDir = Sub("live_plot_pareto");
            string plotsDir = Path.Combine(workDir, "plots");
            string stlExportDir = Path.Combine(workDir, "exported_pareto_stls");

            var eval = new SyntheticTwoObjectiveEvaluator();
            var cfg = new SimulationConfig();
            var ctx = Create2DContext(workDir);

            var samples = new (int Iter, int Var, float X1, float X2)[]
            {
                (1, 1, 0.20f, 0.50f),
                (1, 2, 0.50f, 0.50f),
                (1, 3, 0.90f, 0.95f), // infeasible (outside circle)
                (2, 1, 0.80f, 0.50f),
                (2, 2, 0.35f, 0.45f),
                (2, 3, 0.65f, 0.50f)
            };

            foreach (var s in samples)
            {
                string stl = Path.Combine(workDir, $"geom_{s.Iter}_{s.Var}.stl");
                File.WriteAllText(stl, $"solid g{s.Iter}_v{s.Var}\nendsolid g{s.Iter}_v{s.Var}\n");
                var rec = new ModelRecord
                {
                    Iteration = s.Iter,
                    Variant = s.Var,
                    ActiveParameters = new() { ["X1"] = s.X1, ["X2"] = s.X2 },
                    StlPath = stl
                };
                eval.CalculateFitness(rec, cfg);
                ctx.History.Add(rec);
            }

            ctx.ExportToCsv();
            ParetoSummary summary = ParetoFrontCalculator.ExportParetoSummary(workDir, ctx.History, copyStls: true);

            string? py = ProcessExternalOptimizerTransport.ResolvePythonExecutable(new ExternalOptimizerConfig());
            string? repoRoot = ProjectPaths.ResolveRepositoryRoot();
            Assert.NotNull(py);
            Assert.NotNull(repoRoot);
            string plotScript = Path.Combine(repoRoot!, "scripts", "plot_pareto.py");
            Assert.True(File.Exists(plotScript), $"Missing script: {plotScript}");

            var psi = new ProcessStartInfo
            {
                FileName = py!,
                Arguments = $"\"{plotScript}\" \"{workDir}\" --out \"{plotsDir}\" --export-stls \"{stlExportDir}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            psi.Environment["PYTHONWARNINGS"] = "ignore";
            using var proc = Process.Start(psi)!;
            string stdout = proc.StandardOutput.ReadToEnd();
            string stderr = proc.StandardError.ReadToEnd();
            Assert.True(proc.WaitForExit(30000), "plot_pareto.py timed out.");
            Assert.True(proc.ExitCode == 0, $"plot_pareto.py failed (exit {proc.ExitCode}): {stderr}\n{stdout}");

            string paretoPng = Path.Combine(plotsDir, "pareto_front.png");
            string hvPng = Path.Combine(plotsDir, "hypervolume_progress.png");
            string exportedSummary = Path.Combine(stlExportDir, "pareto_summary.json");

            Assert.True(File.Exists(paretoPng) && new FileInfo(paretoPng).Length > 1000, "pareto_front.png missing or empty.");
            Assert.True(File.Exists(hvPng) && new FileInfo(hvPng).Length > 1000, "hypervolume_progress.png missing or empty.");
            Assert.True(File.Exists(exportedSummary), "pareto_summary.json missing.");

            string[] exportedStls = Directory.GetFiles(stlExportDir, "*.stl");
            Assert.Equal(summary.ParetoCount, exportedStls.Length);
            _output.WriteLine($"plot_pareto.py live OK: Pareto={summary.ParetoCount}, STLs={exportedStls.Length}, stdout={stdout.Trim()}");
        }
    }
}
