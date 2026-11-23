using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;
using FluidForge.Solvers.OpenFoam;
using Moq;
using Xunit;

namespace FluidForge.Tests
{
    [Collection(ProcessWideStateCollection.Name)]
    public class MultiFidelityTests : TempDirectoryTest
    {
        private static ProjectConfig CreateProject() => new ProjectConfig
        {
            ProjectName = "MultiFidelityUnitTest",
            BaseParameters = new Dictionary<string, float> { ["X"] = 5.0f },
            MaxDeviations = new Dictionary<string, float> { ["X"] = 2.0f },
            ParameterBounds = new Dictionary<string, (float Min, float Max)> { ["X"] = (0.0f, 10.0f) }
        };

        private static ModelRecord MakeRecord(
            int variant,
            float fitness,
            bool feasible = true,
            bool failed = false,
            bool disqualified = false,
            EvaluationFailureStage failedStage = EvaluationFailureStage.None,
            string fidelityLevel = FidelityLevels.High) => new ModelRecord
        {
            Iteration = 1,
            Variant = variant,
            Fitness = fitness,
            Feasible = feasible,
            SimulationFailed = failed,
            Disqualified = disqualified,
            FailedStage = failedStage,
            FidelityLevel = fidelityLevel,
            ActiveParameters = new Dictionary<string, float> { ["X"] = variant }
        };

        // -----------------------------------------------------------------
        // 1. Config defaults, FidelityLevels, IsTwoStageEnabled validation
        // -----------------------------------------------------------------

        [Fact]
        public void Config_Defaults_And_FidelityLevels_Work_As_Expected()
        {
            var cfg = SimulationConfig.CreateDefault();
            Assert.NotNull(cfg.MultiFidelity);
            Assert.Equal("Off", cfg.MultiFidelity.Mode);
            Assert.Equal(1.5f, cfg.MultiFidelity.CoarseMeshFactor);
            Assert.Equal(0, cfg.MultiFidelity.CoarseMaxIterations);
            Assert.Equal(2, cfg.MultiFidelity.PromoteTopK);
            Assert.Equal(0.25f, cfg.MultiFidelity.PromoteTopFraction);
            Assert.False(MultiFidelitySelector.IsTwoStageEnabled(cfg.MultiFidelity));
            Assert.False(MultiFidelitySelector.IsTwoStageEnabled(null));

            Assert.True(FidelityLevels.IsCoarse("Coarse"));
            Assert.True(FidelityLevels.IsCoarse("fastconcept"));
            Assert.True(FidelityLevels.IsCoarse("LOW"));
            Assert.False(FidelityLevels.IsCoarse(FidelityLevels.High));
            Assert.False(FidelityLevels.IsCoarse(FidelityLevels.Verification));
            Assert.False(FidelityLevels.IsCoarse(null));

            Assert.Equal(0.5, FidelityLevels.ToNumeric(FidelityLevels.Coarse, 2.0f), 6);
            Assert.Equal(0.25, FidelityLevels.ToNumeric(FidelityLevels.Coarse, 4.0f), 6);
            Assert.Equal(0.1, FidelityLevels.ToNumeric(FidelityLevels.Coarse, 20.0f), 6);
            Assert.Equal(1.0, FidelityLevels.ToNumeric(FidelityLevels.High, 2.0f), 6);
            Assert.Equal(1.0, FidelityLevels.ToNumeric(FidelityLevels.Verification, 2.0f), 6);
        }

        [Fact]
        public void IsTwoStageEnabled_Validates_Invalid_Settings()
        {
            Assert.True(MultiFidelitySelector.IsTwoStageEnabled(new MultiFidelityConfig
            {
                Mode = "TwoStageFilter",
                CoarseMeshFactor = 2.0f,
                CoarseMaxIterations = 0,
                PromoteTopK = 0,
                PromoteTopFraction = 0.5f
            }));

            var unknownMode = Assert.Throws<InvalidOperationException>(() =>
                MultiFidelitySelector.IsTwoStageEnabled(new MultiFidelityConfig { Mode = "AdaptiveMagic" }));
            Assert.Contains(OutputConventions.ConfigTag, unknownMode.Message);

            var badFactor = Assert.Throws<InvalidOperationException>(() =>
                MultiFidelitySelector.IsTwoStageEnabled(new MultiFidelityConfig { Mode = "TwoStageFilter", CoarseMeshFactor = 1.0f }));
            Assert.Contains(OutputConventions.ConfigTag, badFactor.Message);

            var badTopK = Assert.Throws<InvalidOperationException>(() =>
                MultiFidelitySelector.IsTwoStageEnabled(new MultiFidelityConfig { Mode = "TwoStageFilter", PromoteTopK = -1 }));
            Assert.Contains(OutputConventions.ConfigTag, badTopK.Message);

            var badMaxIter = Assert.Throws<InvalidOperationException>(() =>
                MultiFidelitySelector.IsTwoStageEnabled(new MultiFidelityConfig { Mode = "TwoStageFilter", CoarseMaxIterations = -5 }));
            Assert.Contains(OutputConventions.ConfigTag, badMaxIter.Message);

            var badFraction = Assert.Throws<InvalidOperationException>(() =>
                MultiFidelitySelector.IsTwoStageEnabled(new MultiFidelityConfig { Mode = "TwoStageFilter", PromoteTopFraction = 1.5f }));
            Assert.Contains(OutputConventions.ConfigTag, badFraction.Message);
        }

        // -----------------------------------------------------------------
        // 2. ComputePromotionCount & ComputeCoarseFactor
        // -----------------------------------------------------------------

        [Fact]
        public void ComputePromotionCount_And_CoarseFactor_Behave_Correctly()
        {
            var mf = new MultiFidelityConfig { CoarseMeshFactor = 2.5f, PromoteTopK = 2, PromoteTopFraction = 0.25f };
            Assert.Equal(1.5f * 2.5f, MultiFidelitySelector.ComputeCoarseFactor(1.5f, mf), 4);

            Assert.Equal(0, MultiFidelitySelector.ComputePromotionCount(0, mf));
            Assert.Equal(1, MultiFidelitySelector.ComputePromotionCount(1, mf));
            Assert.Equal(2, MultiFidelitySelector.ComputePromotionCount(4, mf));
            Assert.Equal(3, MultiFidelitySelector.ComputePromotionCount(10, mf)); // ceil(2.5) = 3 > 2

            var zeroBoth = new MultiFidelityConfig { PromoteTopK = 0, PromoteTopFraction = 0f };
            Assert.Equal(1, MultiFidelitySelector.ComputePromotionCount(5, zeroBoth));
        }

        // -----------------------------------------------------------------
        // 3. SelectForPromotion (Single & Multi-Objective)
        // -----------------------------------------------------------------

        [Fact]
        public void SelectForPromotion_SingleObjective_Filters_And_Orders_Correctly()
        {
            var mf = new MultiFidelityConfig { PromoteTopK = 2, PromoteTopFraction = 0f };
            var records = new List<ModelRecord>
            {
                MakeRecord(1, 999f, feasible: true, failed: true, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(2, 998f, feasible: false, failed: false, failedStage: EvaluationFailureStage.PreConstraint, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(3, 997f, feasible: true, disqualified: true, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(4, 50f, feasible: false, fidelityLevel: FidelityLevels.Coarse), // infeasible post-solve
                MakeRecord(5, 10f, feasible: true, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(6, 20f, feasible: true, fidelityLevel: FidelityLevels.Coarse)
            };

            IReadOnlyList<ModelRecord> promoted = MultiFidelitySelector.SelectForPromotion(records, mf);
            Assert.Equal(new[] { 6, 5 }, promoted.Select(r => r.Variant));

            // When only 1 feasible exists and K=2, second slot takes best infeasible eligible
            var oneFeasible = new List<ModelRecord>
            {
                MakeRecord(4, 50f, feasible: false, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(5, 10f, feasible: true, fidelityLevel: FidelityLevels.Coarse)
            };
            IReadOnlyList<ModelRecord> promoted2 = MultiFidelitySelector.SelectForPromotion(oneFeasible, mf);
            Assert.Equal(new[] { 5, 4 }, promoted2.Select(r => r.Variant));
        }

        [Fact]
        public void SelectForPromotion_MultiObjective_Prefers_Pareto_Optimal_Candidates()
        {
            var mf = new MultiFidelityConfig { PromoteTopK = 2, PromoteTopFraction = 0f };

            ModelRecord paretoA = MakeRecord(1, 5f, feasible: true, fidelityLevel: FidelityLevels.Coarse);
            paretoA.Objectives.Add(new ObjectiveValue("Drag", 1.0f, OptimizationDirection.Minimize));
            paretoA.Objectives.Add(new ObjectiveValue("Lift", 10.0f, OptimizationDirection.Maximize));

            ModelRecord dominatedHighScalar = MakeRecord(2, 99f, feasible: true, fidelityLevel: FidelityLevels.Coarse);
            dominatedHighScalar.Objectives.Add(new ObjectiveValue("Drag", 2.0f, OptimizationDirection.Minimize));
            dominatedHighScalar.Objectives.Add(new ObjectiveValue("Lift", 5.0f, OptimizationDirection.Maximize));

            ModelRecord paretoB = MakeRecord(3, 4f, feasible: true, fidelityLevel: FidelityLevels.Coarse);
            paretoB.Objectives.Add(new ObjectiveValue("Drag", 0.5f, OptimizationDirection.Minimize));
            paretoB.Objectives.Add(new ObjectiveValue("Lift", 6.0f, OptimizationDirection.Maximize));

            IReadOnlyList<ModelRecord> promoted = MultiFidelitySelector.SelectForPromotion(
                new[] { paretoA, dominatedHighScalar, paretoB }, mf);

            Assert.Equal(new[] { 1, 3 }, promoted.Select(r => r.Variant));
        }

        // -----------------------------------------------------------------
        // 4. WorkflowController TwoStageFilter End-to-End (Mocked)
        // -----------------------------------------------------------------

        [Fact]
        public void WorkflowController_TwoStageFilter_Screens_Coarse_And_Promotes_Top_Candidates()
        {
            var config = new SimulationConfig
            {
                MaxIterations = 1,
                VariantsPerIteration = 4,
                UseRubberBandScaler = false,
                MeshRefinementFactor = 1.0f,
                Su2MaxIterations = 3000,
                ChampionVerification = new ChampionVerificationConfig { Mode = "FinalTopN", TopN = 2 },
                MultiFidelity = new MultiFidelityConfig
                {
                    Mode = "TwoStageFilter",
                    CoarseMeshFactor = 2.0f,
                    CoarseMaxIterations = 400,
                    PromoteTopK = 2,
                    PromoteTopFraction = 0.25f
                }
            };

            var context = new SimulationContext(config, CreateProject(), Root);
            context.EnsureDirectories();

            var meshRequests = new List<MeshRequest>();
            var solverCalls = new List<(string Tag, float Factor, int Su2MaxIter, string Fidelity)>();

            var geoMock = new Mock<IGeometryGenerator>();
            geoMock.Setup(g => g.GenerateAndExport(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(), It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                   .Returns((int it, int v, Dictionary<string, float> p, string dir, SimulationConfig c) =>
                       new GeometryResult { StlPath = Path.Combine(dir, $"g_{it}_{v}.stl") });

            var mesherMock = new Mock<IMeshGenerator>();
            mesherMock.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                      .Returns((MeshRequest req) =>
                      {
                          meshRequests.Add(req);
                          return Path.Combine(req.WorkingDirectory, $"mesh_{req.BaseSuffix}.su2");
                      });

            var solverMock = new Mock<ISimulationSolver>();
            solverMock.Setup(s => s.Name).Returns("MockCFD");
            solverMock.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                      .Callback((string mesh, ModelRecord rec, SimulationConfig cfg, string dir) =>
                      {
                          solverCalls.Add((context.CurrentRunTag, rec.PassiveParameters["MeshRefinementFactor"], cfg.Su2MaxIterations, rec.FidelityLevel));
                          float x = rec.ActiveParameters["X"];
                          // Variants 4 (x=4) and 3 (x=3) score highest on coarse (10 - x -> lower drag)
                          float drag = FidelityLevels.IsCoarse(rec.FidelityLevel)
                              ? (10f - x)
                              : (rec.Variant == 3 ? 9.0f : 1.0f);
                          rec.PassiveParameters["Drag"] = drag;
                      });

            var fitnessMock = new Mock<IFitnessCalculator>();
            fitnessMock.Setup(f => f.RequiredMetrics).Returns(new[] { "Drag" });
            fitnessMock.Setup(f => f.CalculateFitness(It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>()))
                       .Callback((ModelRecord rec, SimulationConfig cfg) =>
                       {
                           rec.Fitness = 100f - rec.PassiveParameters["Drag"];
                       });

            var validatorMock = new Mock<IModelValidator>();
            validatorMock.Setup(v => v.ValidateChampion(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(), It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                         .Returns((List<ModelRecord> pool, SimulationConfig c, Func<Dictionary<string, float>, ModelRecord> _) =>
                             WorkflowController.SelectBestRecord(pool)!);

            int nextVar = 1;
            var optimizerMock = new Mock<IOptimizationAlgorithm>();
            optimizerMock.Setup(o => o.Name).Returns("MockOpt");
            optimizerMock.Setup(o => o.GenerateParameters(It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                         .Returns(() => new Dictionary<string, float> { ["X"] = nextVar++ });
            optimizerMock.Setup(o => o.EvaluateAndSelectBest(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationContext>()))
                         .Returns((List<ModelRecord> recs, SimulationContext ctx) =>
                         {
                             foreach (var r in recs) fitnessMock.Object.CalculateFitness(r, ctx.Config);
                             return WorkflowController.SelectBestRecord(recs)!;
                         });

            var controller = new WorkflowController(
                geoMock.Object,
                new[] { new SolverStage(mesherMock.Object, solverMock.Object) },
                validatorMock.Object,
                optimizerMock.Object,
                context,
                requiredMetrics: new[] { "Drag" });

            TextWriter originalOut = Console.Out;
            var captured = new StringWriter();
            try
            {
                Console.SetOut(captured);
                controller.RunOptimization();
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            string logOutput = captured.ToString();
            Assert.Contains("[MULTI-FIDELITY] Stage-1 vs Stage-2 delta:", logOutput);

            // 4 Coarse + 2 Promoted Stage2 + 2 FinalTopN Verification = 8 pipeline evaluations
            Assert.Equal(8, meshRequests.Count);

            var coarseReqs = meshRequests.Take(4).ToList();
            Assert.All(coarseReqs, r =>
            {
                Assert.Equal(2.0f, r.RefinementFactor, 4);
                Assert.Equal("Coarse", r.Tag);
                Assert.Equal(400, r.MaxSolverIterations);
                Assert.Equal(400, r.Config.Su2MaxIterations);
            });

            var fineReqs = meshRequests.Skip(4).Take(2).ToList();
            Assert.Equal(new[] { 4, 3 }, fineReqs.Select(r => r.Variant));
            Assert.All(fineReqs, r =>
            {
                Assert.Equal(1.0f, r.RefinementFactor, 4);
                Assert.Equal("Stage2", r.Tag);
                Assert.Null(r.MaxSolverIterations);
                Assert.Equal(3000, r.Config.Su2MaxIterations);
            });

            var verifReqs = meshRequests.Skip(6).ToList();
            Assert.Equal(2, verifReqs.Count);
            // Only non-coarse (High) variants 4 and 3 were verified
            Assert.Equal(new[] { "Fine1_Gen1_Var4", "Fine2_Gen1_Var3" }, verifReqs.Select(r => r.Tag));
            Assert.All(verifReqs, r => Assert.Equal(99, r.Variant));

            Assert.Equal(4, solverCalls.Count(c => c.Fidelity == FidelityLevels.Coarse));
            Assert.Equal(2, solverCalls.Count(c => c.Fidelity == FidelityLevels.High));
            Assert.Equal(2, solverCalls.Count(c => c.Fidelity == FidelityLevels.Verification));

            // History contains 4 Coarse + 2 High
            Assert.Equal(6, context.History.Count);
            Assert.Equal(4, context.History.Count(r => r.FidelityLevel == FidelityLevels.Coarse));
            Assert.Equal(2, context.History.Count(r => r.FidelityLevel == FidelityLevels.High));

            // SelectBestRecord prefers High over higher-scoring Coarse
            var highLowScore = MakeRecord(1, 10f, feasible: true, fidelityLevel: FidelityLevels.High);
            var coarseHighScore = MakeRecord(2, 999f, feasible: true, fidelityLevel: FidelityLevels.Coarse);
            ModelRecord? selected = WorkflowController.SelectBestRecord(new[] { coarseHighScore, highLowScore });
            Assert.Same(highLowScore, selected);

            // results.csv contains FidelityLevel column with both Coarse and High
            string[] csvLines = File.ReadAllLines(Path.Combine(Root, OutputConventions.ResultsCsvFileName));
            string[] headers = csvLines[0].Split(';');
            int idxFidelity = Array.IndexOf(headers, "FidelityLevel");
            Assert.True(idxFidelity >= 0);
            var exportedLevels = csvLines.Skip(1).Select(l => l.Split(';')[idxFidelity]).ToList();
            Assert.Contains("Coarse", exportedLevels);
            Assert.Contains("High", exportedLevels);
        }

        // -----------------------------------------------------------------
        // 5. RunJournal Roundtrip & Replay with TwoStageFilter
        // -----------------------------------------------------------------

        [Fact]
        public void RunJournal_Roundtrips_And_Replays_TwoStage_Fidelity_And_Detects_Mismatch()
        {
            string journalDir = Sub("journal_test");
            var header = new RunJournalHeader { Projekt = "MFTest", ZufallsSeed = 123 };
            RunJournal writer = RunJournal.StartNew(journalDir, header);

            var p1 = new Dictionary<string, float> { ["X"] = 1.0f };
            var rCoarse = MakeRecord(1, 10f, fidelityLevel: FidelityLevels.Coarse);
            var rFine = MakeRecord(1, 25f, fidelityLevel: FidelityLevels.High);

            writer.Append(1, 1, 2.0f, "Coarse", p1, rCoarse, 1.2, FidelityLevels.Coarse);
            writer.Append(1, 1, 1.0f, "Stage2", p1, rFine, 4.5, FidelityLevels.High);
            writer.MarkCompleted();

            RunJournalContent? content = RunJournal.Read(RunJournal.PathFor(journalDir));
            Assert.NotNull(content);
            Assert.Equal(2, content!.Entries.Count);
            Assert.Equal(FidelityLevels.Coarse, content.Entries[0].FidelityLevel);
            Assert.Equal(FidelityLevels.High, content.Entries[1].FidelityLevel);

            RunJournal replay = RunJournal.Resume(journalDir, content);
            Assert.True(replay.TryReplay(1, 1, 2.0f, "Coarse", p1, out ModelRecord? outCoarse, FidelityLevels.Coarse));
            Assert.Equal(FidelityLevels.Coarse, outCoarse!.FidelityLevel);

            Assert.True(replay.TryReplay(1, 1, 1.0f, "Stage2", p1, out ModelRecord? outFine, FidelityLevels.High));
            Assert.Equal(FidelityLevels.High, outFine!.FidelityLevel);

            // Fidelity mismatch triggers deviation stop
            string mismatchDir = Sub("journal_mismatch");
            RunJournal writer2 = RunJournal.StartNew(mismatchDir, header);
            writer2.Append(1, 1, 2.0f, "Coarse", p1, rCoarse, 1.2, FidelityLevels.Coarse);
            RunJournal replay2 = RunJournal.Resume(mismatchDir, RunJournal.Read(RunJournal.PathFor(mismatchDir))!);

            Assert.False(replay2.TryReplay(1, 1, 2.0f, "Coarse", p1, out _, FidelityLevels.High));
            Assert.Single(Directory.GetFiles(RunJournal.DirectoryFor(mismatchDir), "journal_deviation_*.jsonl"));
        }

        // -----------------------------------------------------------------
        // 6. ExternalProcessOptimizer Ask-Request & Pareto Filtering
        // -----------------------------------------------------------------

        private sealed class CapturingTransport : IExternalOptimizerTransport
        {
            public ExternalOptimizerRequest? LastRequest { get; private set; }

            public bool CheckAvailable(SimulationContext context, out string details)
            {
                details = "mock";
                return true;
            }

            public ExternalOptimizerResponse Ask(
                ExternalOptimizerRequest request,
                SimulationContext context,
                string requestFilePath,
                string responseFilePath)
            {
                LastRequest = request;
                return new ExternalOptimizerResponse
                {
                    Worker = "mock",
                    Candidates =
                    {
                        new ExternalCandidate
                        {
                            Parameters = new Dictionary<string, double> { ["X"] = 3.0 }
                        }
                    }
                };
            }
        }

        [Fact]
        public void ExternalProcessOptimizer_Populates_MultiFidelity_Fields_And_Prefers_NonCoarse_Pareto()
        {
            var config = new SimulationConfig
            {
                MultiFidelity = new MultiFidelityConfig
                {
                    Mode = "TwoStageFilter",
                    CoarseMeshFactor = 2.0f,
                    PromoteTopK = 1
                }
            };
            var context = new SimulationContext(config, CreateProject(), Sub("ext_opt"));
            context.EnsureDirectories();

            var coarseRec = MakeRecord(1, 5f, fidelityLevel: FidelityLevels.Coarse);
            coarseRec.Objectives.Add(new ObjectiveValue("ObjA", 0.1f, OptimizationDirection.Minimize));
            coarseRec.Objectives.Add(new ObjectiveValue("ObjB", 10.0f, OptimizationDirection.Maximize));

            var fineRec = MakeRecord(2, 8f, fidelityLevel: FidelityLevels.High);
            fineRec.Objectives.Add(new ObjectiveValue("ObjA", 0.5f, OptimizationDirection.Minimize));
            fineRec.Objectives.Add(new ObjectiveValue("ObjB", 8.0f, OptimizationDirection.Maximize));

            context.History.Add(coarseRec);
            context.History.Add(fineRec);

            var transport = new CapturingTransport();
            var fitnessMock = new Mock<IFitnessCalculator>();
            var optimizer = new ExternalProcessOptimizer(fitnessMock.Object, "BoTorch", transport, new Random(1));

            optimizer.GenerateParameters(context, iteration: 2, variant: 1, maxVariants: 1);

            ExternalOptimizerRequest? req = transport.LastRequest;
            Assert.NotNull(req);
            Assert.True(req!.MultiFidelity);
            Assert.Contains(0.5, req.AvailableFidelities);
            Assert.Contains(1.0, req.AvailableFidelities);
            Assert.Equal(2, req.History.Count);
            Assert.Equal(0.5, req.History[0].Fidelity, 4);
            Assert.Equal(FidelityLevels.Coarse, req.History[0].FidelityLevel);
            Assert.Equal(1.0, req.History[1].Fidelity, 4);
            Assert.Equal(FidelityLevels.High, req.History[1].FidelityLevel);

            // EvaluateAndSelectBest exports Pareto summary from non-coarse records when available
            optimizer.EvaluateAndSelectBest(new List<ModelRecord> { fineRec }, context);
            string paretoJson = File.ReadAllText(Path.Combine(context.WorkingDirectory, "pareto_front.json"));
            ParetoSummary? summary = System.Text.Json.JsonSerializer.Deserialize<ParetoSummary>(
                paretoJson, ExternalOptimizerProtocol.JsonOptions);
            Assert.NotNull(summary);
            Assert.Single(summary!.Points);
            Assert.Equal(2, summary.Points[0].Variant);
        }

        // -----------------------------------------------------------------
        // 7. OpenFoamCaseGenerator respects MeshRequest.MaxSolverIterations
        // -----------------------------------------------------------------

        [Fact]
        public void OpenFoamCaseGenerator_Respects_MeshRequest_MaxSolverIterations_In_ControlDict()
        {
            string stlPath = Path.Combine(Root, "box.stl");
            File.WriteAllText(stlPath,
                "solid wall\n"
                + "  facet normal 0 0 1\n"
                + "    outer loop\n"
                + "      vertex 0 0 0\n"
                + "      vertex 10 0 0\n"
                + "      vertex 0 10 0\n"
                + "    endloop\n"
                + "  endfacet\n"
                + "endsolid wall\n");

            var options = new OpenFoamOptions
            {
                DomainMode = "External3D",
                EndTime = 1500,
                WriteInterval = 500
            };

            string coarseCase = Sub("of_coarse");
            var coarseReq = new MeshRequest(
                stlPath, 1, 1, SimulationConfig.CreateDefault(), 1.0f, Root,
                RefinementFactor: 2.0f, Tag: "Coarse", MaxSolverIterations: 300);
            OpenFoamCaseGenerator.Generate(coarseCase, coarseReq, options);

            string coarseControlDict = File.ReadAllText(Path.Combine(coarseCase, "system", "controlDict"));
            Assert.Contains("endTime         300;", coarseControlDict);
            Assert.Contains("writeInterval   300;", coarseControlDict);

            string fineCase = Sub("of_fine");
            var fineReq = new MeshRequest(
                stlPath, 1, 1, SimulationConfig.CreateDefault(), 1.0f, Root,
                RefinementFactor: 1.0f, Tag: "Fine", MaxSolverIterations: null);
            OpenFoamCaseGenerator.Generate(fineCase, fineReq, options);

            string fineControlDict = File.ReadAllText(Path.Combine(fineCase, "system", "controlDict"));
            Assert.Contains("endTime         1500;", fineControlDict);
            Assert.Contains("writeInterval   500;", fineControlDict);
        }

        // -----------------------------------------------------------------
        // 8. Hardened Stage2 tag, startup warnings & Stage-1 vs Stage-2 diagnostics
        // -----------------------------------------------------------------

        [Fact]
        public void Stage2_Tag_Does_Not_Trigger_Su2_ControlRunProfile()
        {
            Assert.False(Su2Solver.IsControlRunTag("Stage2"));
            Assert.False(Su2Solver.IsControlRunTag("Coarse"));
            Assert.True(Su2Solver.IsControlRunTag("Fine"));
            Assert.True(Su2Solver.IsControlRunTag("Fine1_Gen1_Var4"));
            Assert.True(Su2Solver.IsControlRunTag("Cal1"));
            Assert.True(Su2Solver.IsControlRunTag("Recheck"));

            var config = new SimulationConfig
            {
                MaxIterations = 1,
                VariantsPerIteration = 2,
                UseRubberBandScaler = false,
                MeshRefinementFactor = 1.0f,
                Su2MaxIterations = 3000,
                ControlRuns = new ControlRunConfig { Su2MaxIterations = 20000 },
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" },
                MultiFidelity = new MultiFidelityConfig
                {
                    Mode = "TwoStageFilter",
                    CoarseMeshFactor = 1.5f,
                    CoarseMaxIterations = 0,
                    PromoteTopK = 1
                }
            };

            var context = new SimulationContext(config, CreateProject(), Sub("stage2_budget"));
            context.EnsureDirectories();

            var stage2SolverBudgets = new List<int>();
            var stage2Tags = new List<string>();

            var geoMock = new Mock<IGeometryGenerator>();
            geoMock.Setup(g => g.GenerateAndExport(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(), It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                   .Returns((int it, int v, Dictionary<string, float> p, string dir, SimulationConfig c) =>
                       new GeometryResult { StlPath = Path.Combine(dir, $"g_{it}_{v}.stl") });

            var mesherMock = new Mock<IMeshGenerator>();
            mesherMock.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                      .Returns((MeshRequest req) =>
                      {
                          if (req.Tag == "Stage2") stage2Tags.Add(req.Tag);
                          return Path.Combine(req.WorkingDirectory, $"Model_{req.BaseSuffix}.su2");
                      });

            var solverMock = new Mock<ISimulationSolver>();
            solverMock.Setup(s => s.Name).Returns("SU2 CFD Solver");
            solverMock.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                      .Callback((string mesh, ModelRecord rec, SimulationConfig cfg, string dir) =>
                      {
                          string tag = Su2Solver.ExtractTag(mesh, rec);
                          SimulationConfig effective = Su2Solver.IsControlRunTag(tag)
                              ? cfg.WithControlRunProfile()
                              : cfg;
                          if (tag == "Stage2") stage2SolverBudgets.Add(effective.Su2MaxIterations);
                          rec.PassiveParameters["Drag"] = 1.0f / rec.Variant;
                      });

            var validatorMock = new Mock<IModelValidator>();
            validatorMock.Setup(v => v.ValidateChampion(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(), It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                         .Returns((List<ModelRecord> pool, SimulationConfig c, Func<Dictionary<string, float>, ModelRecord> _) =>
                             WorkflowController.SelectBestRecord(pool)!);

            int nextVar = 1;
            var optimizerMock = new Mock<IOptimizationAlgorithm>();
            optimizerMock.Setup(o => o.Name).Returns("External Optimizer (BoTorch)");
            optimizerMock.Setup(o => o.GenerateParameters(It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                         .Returns(() => new Dictionary<string, float> { ["X"] = nextVar++ });
            optimizerMock.Setup(o => o.EvaluateAndSelectBest(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationContext>()))
                         .Returns((List<ModelRecord> recs, SimulationContext ctx) =>
                         {
                             foreach (var r in recs) r.Fitness = 10f - r.PassiveParameters["Drag"];
                             return WorkflowController.SelectBestRecord(recs)!;
                         });

            var controller = new WorkflowController(
                geoMock.Object,
                new[] { new SolverStage(mesherMock.Object, solverMock.Object) },
                validatorMock.Object,
                optimizerMock.Object,
                context,
                requiredMetrics: new[] { "Drag" });

            controller.RunOptimization();

            Assert.Single(stage2Tags);
            Assert.Equal("Stage2", stage2Tags[0]);
            Assert.Single(stage2SolverBudgets);
            Assert.Equal(3000, stage2SolverBudgets[0]);
        }

        [Fact]
        public void GetStartupWarnings_Detects_Tight_SU2_Budget_And_BuiltIn_Bayes()
        {
            var offConfig = new SimulationConfig
            {
                MultiFidelity = new MultiFidelityConfig { Mode = "Off", CoarseMaxIterations = 500 }
            };
            Assert.Empty(MultiFidelitySelector.GetStartupWarnings(offConfig, new[] { "SU2 CFD Solver" }, "Bayes"));

            var tightSu2AndBayes = new SimulationConfig
            {
                CauchyElements = 300,
                OptimizationAlgorithm = "Bayes",
                MultiFidelity = new MultiFidelityConfig
                {
                    Mode = "TwoStageFilter",
                    CoarseMeshFactor = 1.5f,
                    CoarseMaxIterations = 500
                }
            };

            IReadOnlyList<string> bothWarnings = MultiFidelitySelector.GetStartupWarnings(
                tightSu2AndBayes,
                new[] { "SU2 CFD Solver" },
                "Bayesian Optimization (Gaussian Process, Expected Improvement)");

            Assert.Equal(2, bothWarnings.Count);
            Assert.Contains(bothWarnings, w => w.Contains("500") && w.Contains("300") && w.Contains("TreatUnconvergedAsFailed"));
            Assert.Contains(bothWarnings, w => w.Contains("Built-in C# Bayes") && w.Contains("BoTorch"));

            // Uncapped SU2 (CoarseMaxIterations = 0) and BoTorch produce 0 warnings
            var cleanSu2BoTorch = new SimulationConfig
            {
                CauchyElements = 300,
                OptimizationAlgorithm = "BoTorch",
                MultiFidelity = new MultiFidelityConfig
                {
                    Mode = "TwoStageFilter",
                    CoarseMeshFactor = 1.5f,
                    CoarseMaxIterations = 0
                }
            };
            Assert.Empty(MultiFidelitySelector.GetStartupWarnings(
                cleanSu2BoTorch,
                new[] { "SU2 CFD Solver" },
                "External Optimizer (BoTorch)"));

            // OpenFOAM with CoarseMaxIterations = 500 does not trigger the SU2 warning
            Assert.Empty(MultiFidelitySelector.GetStartupWarnings(
                tightSu2AndBayes,
                new[] { "OpenFOAM simpleFoam" },
                "External Optimizer (BoTorch)"));
        }

        [Fact]
        public void CompareStage1AndStage2_Detects_Normal_Deltas_RankReversal_And_HighDiscrepancy()
        {
            // 1. Small consistent deltas -> no warning
            var coarseOk = new[]
            {
                MakeRecord(4, 96f, feasible: true, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(3, 88f, feasible: true, fidelityLevel: FidelityLevels.Coarse)
            };
            var highOk = new[]
            {
                MakeRecord(4, 99f, feasible: true, fidelityLevel: FidelityLevels.High),
                MakeRecord(3, 90f, feasible: true, fidelityLevel: FidelityLevels.High)
            };

            StageComparisonReport okReport = MultiFidelitySelector.CompareStage1AndStage2(coarseOk, highOk);
            Assert.False(okReport.RankReversed);
            Assert.False(okReport.HighDiscrepancy);
            Assert.Null(okReport.WarningLine);
            Assert.Contains("[MULTI-FIDELITY] Stage-1 vs Stage-2 delta:", okReport.SummaryLine);
            Assert.Contains("Var4 (coarse=96.0000 -> high=99.0000", okReport.SummaryLine);
            Assert.True(okReport.MaxRelativeDelta < 0.05f);

            // 2. Rank reversal (Var4 beats Var3 in Stage 1, loses to Var3 in Stage 2)
            var coarseRev = new[]
            {
                MakeRecord(4, 95f, feasible: true, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(3, 90f, feasible: true, fidelityLevel: FidelityLevels.Coarse)
            };
            var highRev = new[]
            {
                MakeRecord(4, 85f, feasible: true, fidelityLevel: FidelityLevels.High),
                MakeRecord(3, 92f, feasible: true, fidelityLevel: FidelityLevels.High)
            };

            StageComparisonReport revReport = MultiFidelitySelector.CompareStage1AndStage2(coarseRev, highRev);
            Assert.True(revReport.RankReversed);
            Assert.False(revReport.HighDiscrepancy);
            Assert.NotNull(revReport.WarningLine);
            Assert.Contains("[MULTI-FIDELITY]", revReport.WarningLine);
            Assert.Contains("rank reversal", revReport.WarningLine);

            // 3. > 25% discrepancy without rank reversal
            var coarseDisc = new[]
            {
                MakeRecord(4, 140f, feasible: true, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(3, 80f, feasible: true, fidelityLevel: FidelityLevels.Coarse)
            };
            var highDisc = new[]
            {
                MakeRecord(4, 100f, feasible: true, fidelityLevel: FidelityLevels.High),
                MakeRecord(3, 75f, feasible: true, fidelityLevel: FidelityLevels.High)
            };

            StageComparisonReport discReport = MultiFidelitySelector.CompareStage1AndStage2(coarseDisc, highDisc);
            Assert.False(discReport.RankReversed);
            Assert.True(discReport.HighDiscrepancy);
            Assert.True(discReport.MaxRelativeDelta > 0.25f);
            Assert.NotNull(discReport.WarningLine);
            Assert.Contains("max delta > 25%", discReport.WarningLine);

            // 4. Feasibility reversal + Stage-2 failure
            var coarseFeas = new[]
            {
                MakeRecord(4, 95f, feasible: true, fidelityLevel: FidelityLevels.Coarse),
                MakeRecord(3, 90f, feasible: true, fidelityLevel: FidelityLevels.Coarse)
            };
            var highFeas = new[]
            {
                MakeRecord(4, 96f, feasible: false, fidelityLevel: FidelityLevels.High),
                MakeRecord(3, 91f, feasible: true, fidelityLevel: FidelityLevels.High)
            };
            StageComparisonReport feasRevReport = MultiFidelitySelector.CompareStage1AndStage2(coarseFeas, highFeas);
            Assert.True(feasRevReport.RankReversed);
            Assert.NotNull(feasRevReport.WarningLine);

            var highFailed = new[]
            {
                MakeRecord(4, 0f, feasible: false, failed: true, fidelityLevel: FidelityLevels.High),
                MakeRecord(3, 91f, feasible: true, fidelityLevel: FidelityLevels.High)
            };
            StageComparisonReport failReport = MultiFidelitySelector.CompareStage1AndStage2(coarseFeas, highFailed);
            Assert.Contains("high=FAILED", failReport.SummaryLine);
            Assert.NotNull(failReport.WarningLine);
            Assert.Contains("Stage-2 failure", failReport.WarningLine);
        }
    }
}
