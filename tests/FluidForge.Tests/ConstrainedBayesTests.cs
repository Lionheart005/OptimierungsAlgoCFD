using System;
using System.Collections.Generic;
using System.Linq;
using FluidForge.Core;
using Moq;
using Xunit;

namespace FluidForge.Tests
{
    public class ConstrainedBayesTests : TempDirectoryTest
    {
        private sealed class BenchmarkObjective : ObjectiveEvaluatorBase
        {
            public override IReadOnlyCollection<string> RequiredMetrics => new[] { "Score" };

            public override EvaluationOutcome EvaluateObjectives(ModelRecord record, SimulationConfig config)
            {
                float score = record.PassiveParameters.TryGetValue("Score", out float s) ? s : float.NaN;
                float x1 = record.ActiveParameters["X1"];
                float x2 = record.ActiveParameters["X2"];
                // Post-CFD constraint: x2 >= 0.35
                return new EvaluationOutcome
                {
                    Objectives = { new ObjectiveValue("Score", score, OptimizationDirection.Maximize) },
                    Constraints = { ConstraintEvaluation.AtLeast("MinX2", x2, 0.35f) }
                };
            }
        }

        private sealed class CirclePreConstraint : IGeometryConstraintEvaluator
        {
            private readonly float _cx;
            private readonly float _cy;
            private readonly float _radius;

            public CirclePreConstraint(float cx, float cy, float radius)
            {
                _cx = cx;
                _cy = cy;
                _radius = radius;
            }

            public IReadOnlyList<ConstraintEvaluation> EvaluateGeometryConstraints(ModelRecord record, ProjectConfig project)
            {
                float x1 = record.ActiveParameters["X1"];
                float x2 = record.ActiveParameters["X2"];
                float dist2 = (x1 - _cx) * (x1 - _cx) + (x2 - _cy) * (x2 - _cy);
                return new[]
                {
                    // Feasible inside circle (x1 - cx)^2 + (x2 - cy)^2 <= radius^2
                    ConstraintEvaluation.AtMost("CircleLimit", dist2, _radius * _radius, isPreSolve: true)
                };
            }
        }

        [Fact]
        public void Constrained_EI_Finds_Feasible_Optimum_With_PreCfd_Constraint_And_Saves_Solver_Calls()
        {
            // Unconstrained optimum sits at (0.1, 0.1) with fitness ~10, OUTSIDE feasible circle centered at (0.7, 0.7) radius 0.25.
            // Feasible optimum inside circle is near (0.55, 0.55).
            int solverCalls = 0;

            SimulationContext RunBayes(int seed)
            {
                var config = new SimulationConfig
                {
                    MaxIterations = 7,
                    VariantsPerIteration = 4,
                    UseRubberBandScaler = false,
                    ChampionVerification = new ChampionVerificationConfig { Mode = "Off" },
                    Bayes = new BayesConfig { InitialSamples = 6 }
                };
                var project = new ProjectConfig
                {
                    ProjectName = "ConstrainedBench",
                    BaseParameters = new Dictionary<string, float> { ["X1"] = 0.70f, ["X2"] = 0.70f },
                    MaxDeviations = new Dictionary<string, float> { ["X1"] = 0.1f, ["X2"] = 0.1f },
                    ParameterBounds = new Dictionary<string, (float Min, float Max)>
                    {
                        ["X1"] = (0.0f, 1.0f),
                        ["X2"] = (0.0f, 1.0f)
                    }
                };
                var context = new SimulationContext(config, project, Sub($"bayes_{seed}"));

                var geometry = new Mock<IGeometryGenerator>();
                geometry.Setup(g => g.GenerateAndExport(
                        It.IsAny<int>(), It.IsAny<int>(),
                        It.IsAny<Dictionary<string, float>>(), It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                    .Returns((int i, int v, Dictionary<string, float> p, string d, SimulationConfig c) =>
                        new GeometryResult { StlPath = $"geom_{i}_{v}.stl" });

                var mesher = new Mock<IMeshGenerator>();
                mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Returns("mesh.su2");

                var solver = new Mock<ISimulationSolver>();
                solver.SetupGet(s => s.Name).Returns("MockSolver");
                solver.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                    .Callback((string _, ModelRecord r, SimulationConfig _, string _) =>
                    {
                        solverCalls++;
                        float x1 = r.ActiveParameters["X1"];
                        float x2 = r.ActiveParameters["X2"];
                        // Decreasing away from (0.1, 0.1): higher closer to (0.1, 0.1)
                        float d2 = (x1 - 0.1f) * (x1 - 0.1f) + (x2 - 0.1f) * (x2 - 0.1f);
                        r.PassiveParameters["Score"] = 10.0f - 5.0f * d2;
                    });

                var fitness = new BenchmarkObjective();
                var optimizer = new BayesOptimizationAlgorithm(fitness, random: RunSeed.ForOptimizer(seed));
                optimizer.Preflight(context);

                var controller = new WorkflowController(
                    geometry.Object,
                    new[] { new SolverStage(mesher.Object, solver.Object) },
                    new ChampionValidator(fitness, RunSeed.ForValidator(seed), project.ParameterBounds),
                    optimizer,
                    context,
                    fitness.RequiredMetrics,
                    journal: null,
                    geometryConstraints: new CirclePreConstraint(0.7f, 0.7f, 0.25f));

                controller.RunOptimization();
                return context;
            }

            // Evaluate 3 seeds of Bayes vs pure random baseline with the same budget (28 evaluations)
            double bayesSum = 0.0;
            double randomSum = 0.0;
            foreach (int seed in new[] { 1, 2, 3 })
            {
                SimulationContext ctx = RunBayes(seed);
                var feasible = ctx.History.Where(r => r.Feasible && !r.SimulationFailed && !r.Disqualified).ToList();
                Assert.NotEmpty(feasible);
                double bestFeasible = feasible.Max(r => r.Fitness);
                bayesSum += bestFeasible;

                // Theoretical feasible optimum inside circle (0.7,0.7), r=0.25 closest to (0.1,0.1):
                // point = (0.7 - 0.25/sqrt(2), 0.7 - 0.25/sqrt(2)) ≈ (0.5232, 0.5232) => Score ≈ 8.208
                Assert.True(bestFeasible >= 7.7, $"Seed {seed}: expected feasible optimum >= 7.7, got {bestFeasible:F3}");

                // Random DoE baseline with same 28-sample budget
                var rng = new Random(100 + seed);
                double bestRand = 0.0;
                for (int k = 0; k < 28; k++)
                {
                    float x1 = (float)rng.NextDouble();
                    float x2 = (float)rng.NextDouble();
                    bool ok = (x1 - 0.7f) * (x1 - 0.7f) + (x2 - 0.7f) * (x2 - 0.7f) <= 0.25f * 0.25f && x2 >= 0.35f;
                    if (ok)
                    {
                        float d2 = (x1 - 0.1f) * (x1 - 0.1f) + (x2 - 0.1f) * (x2 - 0.1f);
                        bestRand = Math.Max(bestRand, 10.0f - 5.0f * d2);
                    }
                }
                randomSum += bestRand;
            }

            // Pre-CFD constraint short-circuited infeasible evaluations so solverCalls < 3 * 28 = 84
            Assert.True(solverCalls < 84, $"Expected pre-CFD short-circuits to save solver calls, got {solverCalls}/84");
            Assert.True(bayesSum > randomSum, $"Bayes sum {bayesSum:F3} did not beat random baseline {randomSum:F3}");
        }

        [Fact]
        public void Feasibility_First_Navigates_Into_Feasible_Region_When_All_Initial_Points_Are_Infeasible()
        {
            // Small feasible island around (0.8, 0.8), radius 0.18 -> initial DoE starting at (0.1, 0.1) is 100% infeasible!
            var config = new SimulationConfig
            {
                MaxIterations = 5,
                VariantsPerIteration = 5,
                UseRubberBandScaler = false,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" },
                Bayes = new BayesConfig { InitialSamples = 5 }
            };
            var project = new ProjectConfig
            {
                ProjectName = "FeasibilityFirst",
                BaseParameters = new Dictionary<string, float> { ["X1"] = 0.1f, ["X2"] = 0.1f },
                MaxDeviations = new Dictionary<string, float> { ["X1"] = 0.1f, ["X2"] = 0.1f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    ["X1"] = (0.0f, 1.0f),
                    ["X2"] = (0.0f, 1.0f)
                }
            };

            var context = new SimulationContext(config, project, Sub("feas_first"));
            var fitness = new BenchmarkObjective();
            var optimizer = new BayesOptimizationAlgorithm(fitness, random: new Random(7));
            optimizer.Preflight(context);

            var preConstraint = new CirclePreConstraint(0.8f, 0.8f, 0.18f);

            // Round 1: force all 5 initial points to be outside the feasible circle (PreConstraint infeasible)
            var round1 = new List<ModelRecord>();
            for (int v = 1; v <= 5; v++)
            {
                var p = new Dictionary<string, float>
                {
                    ["X1"] = 0.05f + 0.08f * v,
                    ["X2"] = 0.05f + 0.06f * (5 - v)
                };
                var rec = new ModelRecord
                {
                    Iteration = 1,
                    Variant = v,
                    ActiveParameters = p,
                    Feasible = false,
                    FailedStage = EvaluationFailureStage.PreConstraint,
                    Fitness = ObjectiveEvaluatorBase.DefaultFailedFitness
                };
                rec.Constraints.AddRange(preConstraint.EvaluateGeometryConstraints(rec, project));
                context.History.Add(rec);
                round1.Add(rec);
            }

            // Prepare round 2: 0 CFD-evaluated records exist, 5 PreConstraint records exist -> FitConstraintOnly path!
            optimizer.PrepareNextIteration(context, round1, round1[0]);

            // Run rounds 2..5 via manual loop evaluating PreConstraint + mock solver
            bool foundFeasible = false;
            for (int iter = 2; iter <= 5; iter++)
            {
                var roundRecords = new List<ModelRecord>();
                for (int v = 1; v <= 5; v++)
                {
                    Dictionary<string, float> p = optimizer.GenerateParameters(context, iter, v, 5);
                    var rec = new ModelRecord { Iteration = iter, Variant = v, ActiveParameters = p };
                    var cons = preConstraint.EvaluateGeometryConstraints(rec, project);
                    rec.Constraints.AddRange(cons);

                    if (cons.All(c => c.IsSatisfied))
                    {
                        rec.PassiveParameters["Score"] = 5.0f + p["X1"] + p["X2"];
                        fitness.CalculateFitness(rec, config);
                        if (rec.Feasible) foundFeasible = true;
                    }
                    else
                    {
                        rec.Feasible = false;
                        rec.FailedStage = EvaluationFailureStage.PreConstraint;
                        rec.Fitness = ObjectiveEvaluatorBase.DefaultFailedFitness;
                    }

                    context.History.Add(rec);
                    roundRecords.Add(rec);
                }

                ModelRecord best = optimizer.EvaluateAndSelectBest(roundRecords, context);
                if (iter < 5) optimizer.PrepareNextIteration(context, roundRecords, best);
            }

            Assert.True(foundFeasible, "Constrained Bayes failed to locate the feasible island from 0 feasible starts.");
        }
    }
}
