using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using Moq;
using FluidForge.Core;
using Xunit;

namespace FluidForge.Tests
{
    [Collection(ProcessWideStateCollection.Name)]
    public class ObjectivesAndConstraintsTests : TempDirectoryTest
    {
        private sealed class MultiObjectiveTestEvaluator : ObjectiveEvaluatorBase
        {
            private readonly float _minLift;
            private readonly float _maxDrag;

            public MultiObjectiveTestEvaluator(float minLift = 0.4f, float maxDrag = 0.05f)
            {
                _minLift = minLift;
                _maxDrag = maxDrag;
            }

            public override IReadOnlyCollection<string> RequiredMetrics => new[] { "Drag", "Lift" };

            public override IReadOnlyCollection<string> RawComparisonMetrics => new[] { "Drag", "Lift" };

            public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
            {
                float drag = record.PassiveParameters.TryGetValue("Drag", out float d) ? d : float.NaN;
                float lift = record.PassiveParameters.TryGetValue("Lift", out float l) ? l : float.NaN;

                return new EvaluationOutcome
                {
                    Objectives =
                    {
                        new ObjectiveValue("Drag", drag, OptimizationDirection.Minimize, Weight: 2.0f),
                        new ObjectiveValue("Lift", lift, OptimizationDirection.Maximize, Weight: 1.0f)
                    },
                    Constraints =
                    {
                        ConstraintEvaluation.AtLeast("MinLift", lift, _minLift),
                        ConstraintEvaluation.AtMost("MaxDrag", drag, _maxDrag)
                    }
                };
            }
        }

        private sealed class ThicknessPreConstraintEvaluator : IGeometryConstraintEvaluator
        {
            private readonly float _minThickness;

            public ThicknessPreConstraintEvaluator(float minThickness) => _minThickness = minThickness;

            public IReadOnlyList<ConstraintEvaluation> EvaluateGeometryConstraints(ModelRecord record, ProjectConfig project)
            {
                float thickness = record.PassiveParameters.TryGetValue("Thickness", out float t)
                    ? t
                    : record.ActiveParameters.GetValueOrDefault("Thickness", 0f);
                return new[]
                {
                    ConstraintEvaluation.AtLeast("MinThickness", thickness, _minThickness, isPreSolve: true)
                };
            }
        }

        private SimulationContext CreateContext(int iterations = 1, int variants = 2)
        {
            var config = new SimulationConfig
            {
                MaxIterations = iterations,
                VariantsPerIteration = variants,
                UseRubberBandScaler = false,
                BouncerTolerance = 0.15f,
                BouncerJitter = 0.02f,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" }
            };
            var project = new ProjectConfig
            {
                ProjectName = "ObjConTest",
                BaseParameters = new Dictionary<string, float> { ["Thickness"] = 10f },
                MaxDeviations = new Dictionary<string, float> { ["Thickness"] = 2f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { ["Thickness"] = (5f, 20f) }
            };
            return new SimulationContext(config, project, Root);
        }

        [Fact]
        public void ConstraintEvaluation_Factories_Compute_Margins_And_Violations_Correctly()
        {
            ConstraintEvaluation atMostOk = ConstraintEvaluation.AtMost("MaxCd", 0.03f, 0.05f);
            Assert.True(atMostOk.IsSatisfied);
            Assert.Equal(0.02f, atMostOk.Margin, 5);
            Assert.Equal(-0.02f, atMostOk.Violation, 5);

            ConstraintEvaluation atMostFail = ConstraintEvaluation.AtMost("MaxCd", 0.07f, 0.05f);
            Assert.False(atMostFail.IsSatisfied);
            Assert.Equal(-0.02f, atMostFail.Margin, 5);
            Assert.Equal(0.02f, atMostFail.Violation, 5);

            ConstraintEvaluation atLeastNan = ConstraintEvaluation.AtLeast("MinCl", float.NaN, 0.2f, isPreSolve: true);
            Assert.False(atLeastNan.IsSatisfied);
            Assert.True(atLeastNan.IsPreSolve);

            IReadOnlyList<ConstraintEvaluation> range = ConstraintEvaluation.InRange("Volume", 150f, 100f, 200f, isPreSolve: true);
            Assert.Equal(2, range.Count);
            Assert.Equal("Volume_Min", range[0].Name);
            Assert.Equal(50f, range[0].Margin, 5);
            Assert.Equal("Volume_Max", range[1].Name);
            Assert.Equal(50f, range[1].Margin, 5);
            Assert.All(range, c => Assert.True(c.IsSatisfied && c.IsPreSolve));
        }

        [Fact]
        public void PreCfd_Constraint_Violation_ShortCircuits_Mesher_And_Solver_And_Skips_Estimator()
        {
            var geometry = new Mock<IGeometryGenerator>();
            geometry.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<Dictionary<string, float>>(), It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns((int i, int v, Dictionary<string, float> p, string d, SimulationConfig c) =>
                {
                    var res = new GeometryResult { StlPath = $"geom_{i}_{v}.stl" };
                    res.AddMetric("Thickness", p["Thickness"], MetricScaling.None);
                    return res;
                });

            var mesher = new Mock<IMeshGenerator>();
            var solver = new Mock<ISimulationSolver>();
            solver.SetupGet(s => s.Name).Returns("MockSolver");

            var evaluator = new MultiObjectiveTestEvaluator();
            var optimizer = new Mock<IOptimizationAlgorithm>();
            optimizer.SetupGet(o => o.Name).Returns("MockOpt");
            optimizer.Setup(o => o.GenerateParameters(It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new Dictionary<string, float> { ["Thickness"] = 6.5f }); // < min 8.0
            optimizer.Setup(o => o.EvaluateAndSelectBest(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationContext>()))
                .Returns((List<ModelRecord> recs, SimulationContext ctx) =>
                {
                    foreach (ModelRecord r in recs) evaluator.CalculateFitness(r, ctx.Config);
                    return WorkflowController.SelectBestRecord(recs)!;
                });

            var validator = new Mock<IModelValidator>();
            validator.Setup(v => v.ValidateChampion(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(), It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                .Returns((List<ModelRecord> c, SimulationConfig _, Func<Dictionary<string, float>, ModelRecord> _) => c[0]);

            SimulationContext context = CreateContext(iterations: 1, variants: 1);
            var controller = new WorkflowController(
                geometry.Object,
                new[] { new SolverStage(mesher.Object, solver.Object) },
                validator.Object,
                optimizer.Object,
                context,
                evaluator.RequiredMetrics,
                journal: null,
                geometryConstraints: new ThicknessPreConstraintEvaluator(minThickness: 8.0f));

            TextWriter previousOut = Console.Out;
            using var consoleCapture = new StringWriter();
            Console.SetOut(consoleCapture);
            var sw = Stopwatch.StartNew();
            try
            {
                controller.RunOptimization();
            }
            finally
            {
                sw.Stop();
                Console.SetOut(previousOut);
            }

            Assert.True(sw.Elapsed.TotalSeconds < 2.0, $"Expected < 2s abort, took {sw.Elapsed.TotalSeconds:F2}s");
            mesher.Verify(m => m.GenerateMesh(It.IsAny<MeshRequest>()), Times.Never());
            solver.Verify(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()), Times.Never());

            ModelRecord record = Assert.Single(context.History);
            Assert.False(record.Feasible);
            Assert.Equal(EvaluationFailureStage.PreConstraint, record.FailedStage);
            Assert.True(record.IsPreConstraintInfeasible);
            Assert.False(record.SimulationFailed);
            Assert.Equal(ObjectiveEvaluatorBase.DefaultFailedFitness, record.Fitness, 5);

            ConstraintEvaluation con = Assert.Single(record.Constraints);
            Assert.True(con.IsPreSolve);
            Assert.False(con.IsSatisfied);
            Assert.Equal(-1.5f, con.Margin, 4);
            Assert.Equal(1.5f, con.Violation, 4);

            // RunTimeEstimator must not receive samples from PreConstraint rejections -> no Forecast line printed.
            string output = consoleCapture.ToString();
            Assert.DoesNotContain("Forecast:", output);
            Assert.Contains("PreConstraint", output);
        }

        [Fact]
        public void PostCfd_Constraint_Violation_Keeps_Raw_Objectives_And_Marks_PostConstraint()
        {
            var geometry = new Mock<IGeometryGenerator>();
            geometry.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<Dictionary<string, float>>(), It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns(() =>
                {
                    var res = new GeometryResult { StlPath = "geom.stl" };
                    res.AddMetric("Thickness", 10.0f, MetricScaling.None);
                    return res;
                });

            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Returns("mesh.su2");

            var solver = new Mock<ISimulationSolver>();
            solver.SetupGet(s => s.Name).Returns("MockSolver");
            solver.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                .Callback((string _, ModelRecord r, SimulationConfig _, string _) =>
                {
                    r.PassiveParameters["Drag"] = 0.02f;
                    r.PassiveParameters["Lift"] = 0.30f; // violates MinLift = 0.40
                });

            var evaluator = new MultiObjectiveTestEvaluator(minLift: 0.40f, maxDrag: 0.05f);
            SimulationContext context = CreateContext(iterations: 1, variants: 1);

            var controller = new WorkflowController(
                geometry.Object,
                new[] { new SolverStage(mesher.Object, solver.Object) },
                new ChampionValidator(evaluator, new Random(1)),
                new IdwBaselineAlgorithm(evaluator, new Random(1)),
                context,
                evaluator.RequiredMetrics,
                journal: null,
                geometryConstraints: new ThicknessPreConstraintEvaluator(minThickness: 8.0f));

            controller.RunOptimization();

            solver.Verify(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()), Times.Once());

            ModelRecord record = Assert.Single(context.History);
            Assert.False(record.SimulationFailed);
            Assert.False(record.Feasible);
            Assert.Equal(EvaluationFailureStage.PostConstraint, record.FailedStage);
            Assert.Equal(2, record.Objectives.Count);
            Assert.Equal(0.02f, record.Objectives.Single(o => o.Name == "Drag").Value, 5);
            Assert.Equal(0.30f, record.Objectives.Single(o => o.Name == "Lift").Value, 5);

            // Pre-solve constraint was kept alongside the two post-solve constraints, even across repeated CalculateFitness calls.
            Assert.Equal(3, record.Constraints.Count);
            Assert.Single(record.Constraints, c => c.IsPreSolve && c.Name == "MinThickness" && c.IsSatisfied);
            Assert.Single(record.Constraints, c => !c.IsPreSolve && c.Name == "MinLift" && !c.IsSatisfied);
            Assert.Single(record.Constraints, c => !c.IsPreSolve && c.Name == "MaxDrag" && c.IsSatisfied);

            // Weighted scalarization without artificial 0.1 penalty cliff: (2 * (-0.02) + 1 * 0.30) / 3 = 0.26 / 3
            Assert.Equal((2.0f * -0.02f + 0.30f) / 3.0f, record.Fitness, 5);
        }

        [Fact]
        public void MultiObjective_RunJournal_Roundtrip_And_Csv_Export_Preserve_All_Fields()
        {
            var evaluator = new MultiObjectiveTestEvaluator(minLift: 0.40f, maxDrag: 0.05f);
            SimulationContext context = CreateContext(iterations: 1, variants: 2);

            var preInfeasible = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                ActiveParameters = new Dictionary<string, float> { ["Thickness"] = 7.0f },
                Feasible = false,
                FailedStage = EvaluationFailureStage.PreConstraint,
                Constraints = { ConstraintEvaluation.AtLeast("MinThickness", 7.0f, 8.0f, isPreSolve: true) }
            };
            evaluator.CalculateFitness(preInfeasible, context.Config);

            var feasible = new ModelRecord
            {
                Iteration = 1,
                Variant = 2,
                ActiveParameters = new Dictionary<string, float> { ["Thickness"] = 12.0f },
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.025f, ["Lift"] = 0.55f },
                Constraints = { ConstraintEvaluation.AtLeast("MinThickness", 12.0f, 8.0f, isPreSolve: true) }
            };
            evaluator.CalculateFitness(feasible, context.Config);

            // Journal roundtrip
            var header = new RunJournalHeader { Projekt = "ObjConTest", ZufallsSeed = 123 };
            RunJournal writerJournal = RunJournal.StartNew(Root, header);
            writerJournal.Append(1, 1, 1.0f, "", preInfeasible.ActiveParameters, preInfeasible, 0.25);
            writerJournal.Append(1, 2, 1.0f, "", feasible.ActiveParameters, feasible, 12.5);
            writerJournal.MarkCompleted();

            RunJournalContent content = RunJournal.Read(RunJournal.PathFor(Root))!;
            Assert.True(content.Completed);
            Assert.Equal(2, content.Entries.Count);

            RunJournal replayJournal = RunJournal.Resume(Root, content);
            Assert.True(replayJournal.TryReplay(1, 1, 1.0f, "", new Dictionary<string, float> { ["Thickness"] = 7.0f }, out ModelRecord? r1));
            Assert.True(replayJournal.TryReplay(1, 2, 1.0f, "", new Dictionary<string, float> { ["Thickness"] = 12.0f }, out ModelRecord? r2));

            Assert.NotNull(r1);
            Assert.False(r1!.Feasible);
            Assert.Equal(EvaluationFailureStage.PreConstraint, r1.FailedStage);
            Assert.Single(r1.Constraints);
            Assert.Equal(-1.0f, r1.Constraints[0].Margin, 5);

            Assert.NotNull(r2);
            Assert.True(r2!.Feasible);
            Assert.Equal(EvaluationFailureStage.None, r2.FailedStage);
            Assert.Equal(2, r2.Objectives.Count);
            Assert.Equal(OptimizationDirection.Minimize, r2.Objectives[0].Direction);
            Assert.Equal(OptimizationDirection.Maximize, r2.Objectives[1].Direction);
            Assert.Equal(3, r2.Constraints.Count);

            // CSV export
            context.History.Add(r1);
            context.History.Add(r2);
            context.ExportToCsv();

            string[] lines = File.ReadAllLines(Path.Combine(Root, OutputConventions.ResultsCsvFileName));
            Assert.Equal(3, lines.Length);

            string[] headers = lines[0].Split(';');
            Assert.Contains("Feasible", headers);
            Assert.Contains("FailedStage", headers);
            Assert.Contains("Obj_Drag", headers);
            Assert.Contains("Obj_Lift", headers);
            Assert.Contains("Con_MinThickness_Margin", headers);
            Assert.Contains("Con_MinLift_Margin", headers);
            Assert.Contains("Con_MaxDrag_Margin", headers);

            int idxFeasible = Array.IndexOf(headers, "Feasible");
            int idxStage = Array.IndexOf(headers, "FailedStage");
            int idxObjDrag = Array.IndexOf(headers, "Obj_Drag");
            int idxConThick = Array.IndexOf(headers, "Con_MinThickness_Margin");
            int idxConLift = Array.IndexOf(headers, "Con_MinLift_Margin");

            string[] row1 = lines[1].Split(';');
            Assert.Equal("0", row1[idxFeasible]);
            Assert.Equal("PreConstraint", row1[idxStage]);
            Assert.Equal("", row1[idxObjDrag]);
            Assert.Equal("-1.00000", row1[idxConThick]);
            Assert.Equal("", row1[idxConLift]);

            string[] row2 = lines[2].Split(';');
            Assert.Equal("1", row2[idxFeasible]);
            Assert.Equal("None", row2[idxStage]);
            Assert.Equal("0.02500", row2[idxObjDrag]);
            Assert.Equal("4.00000", row2[idxConThick]);
            Assert.Equal(0.15f, float.Parse(row2[idxConLift], CultureInfo.InvariantCulture), 4);
        }

        [Fact]
        public void ChampionValidator_Decoupled_Confirms_Stable_Champion_Even_When_Jitter_Crosses_PostConstraint()
        {
            var evaluator = new MultiObjectiveTestEvaluator(minLift: 0.40f, maxDrag: 0.05f);
            var candidate = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                ActiveParameters = new Dictionary<string, float> { ["Thickness"] = 10f },
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.020f, ["Lift"] = 0.401f }
            };
            evaluator.CalculateFitness(candidate, new SimulationConfig());
            Assert.True(candidate.Feasible);

            var validator = new ChampionValidator(evaluator, new Random(1));
            var config = new SimulationConfig { BouncerTolerance = 0.10f, BouncerJitter = 0.01f };

            ModelRecord winner = validator.ValidateChampion(
                new List<ModelRecord> { candidate },
                config,
                _ => new ModelRecord
                {
                    // Raw objectives vary only ~1%, even though Lift dipped below minLift=0.40
                    PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.0202f, ["Lift"] = 0.397f }
                });

            Assert.Same(candidate, winner);
            Assert.False(candidate.Disqualified);
        }

        [Fact]
        public void ChampionValidator_Decoupled_Retries_Opposite_Jitter_On_PreConstraint_Boundary()
        {
            var evaluator = new MultiObjectiveTestEvaluator(minLift: 0.40f, maxDrag: 0.05f);
            var candidate = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                ActiveParameters = new Dictionary<string, float> { ["Thickness"] = 8.05f },
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.020f, ["Lift"] = 0.50f }
            };
            evaluator.CalculateFitness(candidate, new SimulationConfig());

            var bounds = new Dictionary<string, (float Min, float Max)> { ["Thickness"] = (5f, 20f) };
            var validator = new ChampionValidator(evaluator, new Random(1), bounds);
            var config = new SimulationConfig { BouncerTolerance = 0.10f, BouncerJitter = 0.02f }; // delta = +/-0.30

            var triedThickness = new List<float>();
            ModelRecord winner = validator.ValidateChampion(
                new List<ModelRecord> { candidate },
                config,
                p =>
                {
                    float t = p["Thickness"];
                    triedThickness.Add(t);
                    if (t < 8.0f)
                    {
                        return new ModelRecord
                        {
                            Feasible = false,
                            FailedStage = EvaluationFailureStage.PreConstraint,
                            Constraints = { ConstraintEvaluation.AtLeast("MinThickness", t, 8.0f, isPreSolve: true) }
                        };
                    }

                    return new ModelRecord
                    {
                        PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.0201f, ["Lift"] = 0.502f }
                    };
                });

            // Regardless of which sign Random(1) picked first, either 1 call succeeded or 2 calls retried the opposite side.
            // Force a seed that triggers the < 8.0 side first if Random(1) happened to go positive:
            if (triedThickness.Count == 1)
            {
                // Re-run with opposite seed to deterministically exercise the 2-call retry branch!
                bool exercisedRetry = false;
                for (int seed = 0; seed < 30; seed++)
                {
                    var calls = new List<float>();
                    var v = new ChampionValidator(evaluator, new Random(seed), bounds);
                    var candCopy = new ModelRecord
                    {
                        Iteration = 1,
                        Variant = 1,
                        ActiveParameters = new Dictionary<string, float> { ["Thickness"] = 8.05f },
                        PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.020f, ["Lift"] = 0.50f }
                    };
                    evaluator.CalculateFitness(candCopy, config);

                    ModelRecord w = v.ValidateChampion(
                        new List<ModelRecord> { candCopy },
                        config,
                        p =>
                        {
                            float t = p["Thickness"];
                            calls.Add(t);
                            if (t < 8.0f)
                            {
                                return new ModelRecord
                                {
                                    Feasible = false,
                                    FailedStage = EvaluationFailureStage.PreConstraint,
                                    Constraints = { ConstraintEvaluation.AtLeast("MinThickness", t, 8.0f, isPreSolve: true) }
                                };
                            }
                            return new ModelRecord
                            {
                                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.0201f, ["Lift"] = 0.502f }
                            };
                        });

                    if (calls.Count == 2)
                    {
                        Assert.True(calls[0] < 8.0f);
                        Assert.True(calls[1] > 8.05f);
                        Assert.False(candCopy.Disqualified);
                        Assert.Same(candCopy, w);
                        exercisedRetry = true;
                        break;
                    }
                }
                Assert.True(exercisedRetry, "Expected at least one seed to hit the negative jitter direction and trigger opposite retry.");
            }
            else
            {
                Assert.Equal(2, triedThickness.Count);
                Assert.False(candidate.Disqualified);
                Assert.Same(candidate, winner);
            }
        }

        [Fact]
        public void ChampionValidator_Decoupled_Disqualifies_Unstable_Raw_Objective()
        {
            var evaluator = new MultiObjectiveTestEvaluator(minLift: 0.40f, maxDrag: 0.05f);
            var unstable = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                ActiveParameters = new Dictionary<string, float> { ["Thickness"] = 10f },
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.020f, ["Lift"] = 0.50f }
            };
            evaluator.CalculateFitness(unstable, new SimulationConfig());

            var stable = new ModelRecord
            {
                Iteration = 1,
                Variant = 2,
                ActiveParameters = new Dictionary<string, float> { ["Thickness"] = 12f },
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.030f, ["Lift"] = 0.45f }
            };
            evaluator.CalculateFitness(stable, new SimulationConfig());

            var validator = new ChampionValidator(evaluator, new Random(1));
            var config = new SimulationConfig { BouncerTolerance = 0.10f, BouncerJitter = 0.01f };

            int call = 0;
            ModelRecord winner = validator.ValidateChampion(
                new List<ModelRecord> { unstable, stable },
                config,
                _ =>
                {
                    call++;
                    return call == 1
                        ? new ModelRecord { PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.035f, ["Lift"] = 0.50f } } // +75% drag jump
                        : new ModelRecord { PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.0305f, ["Lift"] = 0.452f } };
                });

            Assert.True(unstable.Disqualified);
            Assert.Equal(0.0001f, unstable.Fitness, 5);
            Assert.Same(stable, winner);
            Assert.False(stable.Disqualified);
        }

        [Fact]
        public void SelectBestRecord_And_Algorithm_Selection_Prefer_Feasible_Over_Higher_Raw_Infeasible()
        {
            var infeasibleHigh = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                Feasible = false,
                FailedStage = EvaluationFailureStage.PostConstraint,
                Fitness = 100.0f
            };
            var feasibleLower = new ModelRecord
            {
                Iteration = 1,
                Variant = 2,
                Feasible = true,
                FailedStage = EvaluationFailureStage.None,
                Fitness = 12.0f
            };
            var disqualifiedHigh = new ModelRecord
            {
                Iteration = 1,
                Variant = 3,
                Feasible = true,
                Disqualified = true,
                Fitness = 200.0f
            };

            ModelRecord? best = WorkflowController.SelectBestRecord(new[] { infeasibleHigh, disqualifiedHigh, feasibleLower });
            Assert.Same(feasibleLower, best);

            // Fallback when no feasible record exists: non-failed & non-disqualified beats failed/disqualified
            ModelRecord? fallback = WorkflowController.SelectBestRecord(new[] { disqualifiedHigh, infeasibleHigh });
            Assert.Same(infeasibleHigh, fallback);

            // Also verify Bayes and IdwBaseline EvaluateAndSelectBest prefer feasible records
            var evaluator = new MultiObjectiveTestEvaluator(minLift: 0.40f, maxDrag: 0.05f);
            SimulationContext ctx = CreateContext();
            var rInfeas = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.01f, ["Lift"] = 0.35f } // high lift/drag but < minLift
            };
            var rFeas = new ModelRecord
            {
                Iteration = 1,
                Variant = 2,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.04f, ["Lift"] = 0.41f } // lower fitness than rInfeas, but feasible
            };

            var idw = new IdwBaselineAlgorithm(evaluator, new Random(1));
            ModelRecord idwBest = idw.EvaluateAndSelectBest(new List<ModelRecord> { rInfeas, rFeas }, ctx);
            Assert.Same(rFeas, idwBest);

            var bayes = new BayesOptimizationAlgorithm(evaluator, random: new Random(1));
            ModelRecord bayesBest = bayes.EvaluateAndSelectBest(new List<ModelRecord> { rInfeas, rFeas }, ctx);
            Assert.Same(rFeas, bayesBest);
        }

        [Fact]
        public void WorkflowController_Records_Mesh_And_Solver_FailedStages()
        {
            var geom = new Mock<IGeometryGenerator>();
            geom.Setup(g => g.GenerateAndExport(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(), It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns(new GeometryResult { StlPath = "g.stl" });

            var meshGenExMesher = new Mock<IMeshGenerator>();
            meshGenExMesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                .Throws(new MeshGenerationException("mesh blew up"));

            var solver = new Mock<ISimulationSolver>();
            solver.SetupGet(s => s.Name).Returns("S");

            var opt = new Mock<IOptimizationAlgorithm>();
            opt.SetupGet(o => o.Name).Returns("O");
            opt.Setup(o => o.GenerateParameters(It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new Dictionary<string, float> { ["Thickness"] = 10f });
            opt.Setup(o => o.EvaluateAndSelectBest(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationContext>()))
                .Returns((List<ModelRecord> r, SimulationContext _) => r[0]);

            var val = new Mock<IModelValidator>();
            val.Setup(v => v.ValidateChampion(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(), It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                .Returns((List<ModelRecord> r, SimulationConfig _, Func<Dictionary<string, float>, ModelRecord> _) => r[0]);

            SimulationContext ctxMesh = CreateContext(1, 1);
            new WorkflowController(geom.Object, new[] { new SolverStage(meshGenExMesher.Object, solver.Object) }, val.Object, opt.Object, ctxMesh)
                .RunOptimization();

            Assert.Equal(EvaluationFailureStage.Mesh, ctxMesh.History.Single().FailedStage);
            Assert.False(ctxMesh.History.Single().Feasible);
            Assert.True(ctxMesh.History.Single().SimulationFailed);

            var okMesher = new Mock<IMeshGenerator>();
            okMesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Returns("m.su2");
            var failingSolver = new Mock<ISimulationSolver>();
            failingSolver.SetupGet(s => s.Name).Returns("S");
            failingSolver.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                .Callback((string _, ModelRecord r, SimulationConfig _, string _) => r.SimulationFailed = true);

            SimulationContext ctxSolver = CreateContext(1, 1);
            new WorkflowController(geom.Object, new[] { new SolverStage(okMesher.Object, failingSolver.Object) }, val.Object, opt.Object, ctxSolver)
                .RunOptimization();

            Assert.Equal(EvaluationFailureStage.Solver, ctxSolver.History.Single().FailedStage);
            Assert.False(ctxSolver.History.Single().Feasible);
            Assert.True(ctxSolver.History.Single().SimulationFailed);
        }

        [Fact]
        public void SimulationLog_Reports_PreConstraint_And_PostConstraint_Infeasible_Clearly()
        {
            var preRecord = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                Feasible = false,
                FailedStage = EvaluationFailureStage.PreConstraint,
                Fitness = 0.0001f,
                Constraints = { ConstraintEvaluation.AtLeast("MinThickness", 6.5f, 8.0f, isPreSolve: true) }
            };

            var postRecord = new ModelRecord
            {
                Iteration = 1,
                Variant = 2,
                Feasible = false,
                FailedStage = EvaluationFailureStage.PostConstraint,
                Fitness = 0.12f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.02f, ["Lift"] = 0.30f },
                Constraints = { ConstraintEvaluation.AtLeast("MinLift", 0.30f, 0.40f, isPreSolve: false) }
            };

            var okRecord = new ModelRecord
            {
                Iteration = 1,
                Variant = 3,
                Feasible = true,
                FailedStage = EvaluationFailureStage.None,
                Fitness = 0.50f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 0.015f, ["Lift"] = 0.55f }
            };

            TextWriter previousOut = Console.Out;
            using var buffer = new StringWriter();
            Console.SetOut(buffer);
            try
            {
                SimulationLog.PrintVariantResult(preRecord);
                SimulationLog.PrintVariantResult(postRecord);
                SimulationLog.PrintVariantResult(okRecord);
                SimulationLog.PrintIterationSummary(1, 2, new List<ModelRecord> { preRecord, postRecord, okRecord }, okRecord);
            }
            finally
            {
                Console.SetOut(previousOut);
            }

            string output = buffer.ToString();
            Assert.Contains("INFEASIBLE (PreConstraint: MinThickness", output);
            Assert.Contains("INFEASIBLE (PostConstraint: MinLift", output);
            Assert.Contains("⚠ PRE-CON", output);
            Assert.Contains("⚠ INFEAS", output);
            Assert.Contains("★ CHAMPION", output);
        }
    }
}
