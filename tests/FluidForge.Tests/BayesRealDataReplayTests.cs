using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Xunit;
using Xunit.Abstractions;

using FluidForge.Core;
using FluidForge.Projects.Naca2D;

namespace FluidForge.Tests
{
    /// <summary>
    /// Replays real runs from the server comparison on 2026-11-08 (Naca2D, 10 x 10, Bayes) and checks
    /// what the optimizer proposes afterwards.
    ///
    /// <para>
    /// Background: in that run, after very good results, Bayes computed entire rounds of nonsense (angle of attack 15 to 30°,
    /// thickness and camber at the bounds; the model itself predicted fitness 0 to 20 for them). The cause was the
    /// fallback rule "highest uncertainty in the entire space" as soon as expected improvement was zero everywhere.
    /// </para>
    /// </summary>
    public class BayesRealDataReplayTests
    {
        private readonly ITestOutputHelper _output;

        public BayesRealDataReplayTests(ITestOutputHelper output) => _output = output;

        /// <summary>All runs up to a given round; the optimizer receives them round by round, just like during a run.</summary>
        private sealed record Replay(List<ModelRecord> Records, int ThroughIteration, Naca2DFitnessCalculator Fitness)
        {
            public double BestFitness => Records.Where(r => !r.SimulationFailed && !r.Disqualified).Max(r => r.Fitness);
        }

        private static Replay Load(string csv, int throughIteration)
        {
            ProjectConfig project = new Naca2DProject().CreateDefaults();
            var config = new SimulationConfig { VariantsPerIteration = 10, MaxIterations = 10 };
            var fitness = new Naca2DFitnessCalculator(project);

            string[] names = { "MaxCamber", "CamberPosition", "MaxThickness", "AngleOfAttackDeg" };
            var records = new List<ModelRecord>();

            foreach (string line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] c = line.Split(';');
                int iteration = int.Parse(c[0], CultureInfo.InvariantCulture);
                if (iteration > throughIteration) continue;

                float F(int i) => float.Parse(c[i], CultureInfo.InvariantCulture);
                var record = new ModelRecord
                {
                    Iteration = iteration,
                    Variant = int.Parse(c[1], CultureInfo.InvariantCulture),
                    SimulationFailed = c[10] == "1",
                    Disqualified = c[11] == "1"
                };
                for (int i = 0; i < names.Length; i++) record.ActiveParameters[names[i]] = F(2 + i);
                record.PassiveParameters["Drag"] = F(6);
                record.PassiveParameters["Lift"] = F(7);
                record.PassiveParameters["Uncertainty_Drag"] = F(8);
                record.PassiveParameters["Uncertainty_Lift"] = F(9);
                fitness.CalculateFitness(record, config);
                records.Add(record);
            }

            return new Replay(records, throughIteration, fitness);
        }

        /// <summary>
        /// Replays rounds 1 through N (the optimizer keeps learning round by round as during a run, while its
        /// own proposals for those rounds are discarded) and returns the proposals for round N + 1
        /// along with the fitness predicted by the model.
        /// </summary>
        private static (List<double> Predicted, List<Dictionary<string, float>> Parameters, int Fallbacks) Propose(Replay replay, int optimizerSeed)
        {
            ProjectConfig project = new Naca2DProject().CreateDefaults();
            var config = new SimulationConfig { VariantsPerIteration = 10, MaxIterations = 10 };
            string dir = Path.Combine(Path.GetTempPath(), "bayes_replay_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var context = new SimulationContext(config, project, dir);

            var optimizer = new BayesOptimizationAlgorithm(replay.Fitness, random: new Random(optimizerSeed));
            optimizer.Preflight(context);

            for (int round = 1; round <= replay.ThroughIteration; round++)
            {
                List<ModelRecord> current = replay.Records.Where(r => r.Iteration == round).ToList();
                context.History.AddRange(current);
                ModelRecord best = context.History.Where(r => !r.SimulationFailed && !r.Disqualified).OrderByDescending(r => r.Fitness).First();
                optimizer.PrepareNextIteration(context, current, best);
            }

            int next = replay.ThroughIteration + 1;
            var proposals = new List<ModelRecord>();
            for (int v = 1; v <= 10; v++)
            {
                var record = new ModelRecord { Iteration = next, Variant = v };
                record.ActiveParameters = optimizer.GenerateParameters(context, next, v, 10);
                proposals.Add(record);
            }

            optimizer.EvaluateAndSelectBest(proposals, context);

            var predicted = proposals
                .Select(p => p.PassiveParameters.TryGetValue(BayesOptimizationAlgorithm.PredictedFitnessKey, out float f) ? (double)f : double.NaN)
                .ToList();
            return (predicted, proposals.Select(p => p.ActiveParameters).ToList(), optimizer.FallbackSelections);
        }

        public static IEnumerable<object[]> Situations()
        {
            // Right before these rounds, the model proposed nonsense during the run.
            yield return new object[] { BayesRealDataFixtures.S1, 5 };   // Seed 1, Round 6
            yield return new object[] { BayesRealDataFixtures.S1, 7 };   // Seed 1, Round 8
            yield return new object[] { BayesRealDataFixtures.S2, 8 };   // Seed 2, Round 9
            yield return new object[] { BayesRealDataFixtures.S3, 6 };   // Seed 3, Round 7
        }

        [Theory]
        [MemberData(nameof(Situations))]
        public void After_Good_Results_The_Next_Round_Is_Not_Filled_With_Nonsense(string csv, int throughIteration)
        {
            Replay replay = Load(csv, throughIteration);
            double best = replay.BestFitness;

            int garbageTotal = 0, fallbackTotal = 0;
            const int seeds = 4;
            for (int seed = 1; seed <= seeds; seed++)
            {
                var (predicted, parameters, fallbacks) = Propose(replay, seed);
                int garbage = predicted.Count(p => !(p >= 0.5 * best));
                garbageTotal += garbage;
                fallbackTotal += fallbacks;
                _output.WriteLine($"Seed {seed}: fallback {fallbacks}x | predicted: " + string.Join(" ", predicted.Select(p => p.ToString("0", CultureInfo.InvariantCulture))));
            }

            _output.WriteLine($"Best so far {best:0.0}; nonsense proposals (predicted < 50% of best): {garbageTotal} of {seeds * 10}; fallback {fallbackTotal}x");

            // At most the explorer slot per round may have a poor prediction.
            Assert.True(garbageTotal <= seeds * 2,
                $"{garbageTotal} of {seeds * 10} proposals are far below the best so far ({best:0.0}).");
        }
    }

    /// <summary>
    /// Same situation as in the server run, but controllable: a Naca2D-like problem with Drag and Lift
    /// ("Metrics" mode), whose optimum lies on a parameter bound and which has no noise. After a few
    /// rounds, no improvement is expected anywhere. Until 2026-11-08, selection then fell back to
    /// "highest uncertainty in the entire space" and wasted entire rounds at the bounds.
    /// </summary>
    public class BayesConvergedPhaseTests : TempDirectoryTest
    {
        private readonly ITestOutputHelper _output;

        public BayesConvergedPhaseTests(ITestOutputHelper output) => _output = output;

        private static BayesHarness.Problem NacaLike() => new()
        {
            Names = new[] { "MaxCamber", "CamberPosition", "MaxThickness", "AngleOfAttackDeg" },
            Bounds = new[] { (0f, 8f), (15f, 75f), (6f, 24f), (-10f, 30f) },
            Start = new[] { 0f, 40f, 12f, 0f },
            Solve = r =>
            {
                float c = r.ActiveParameters["MaxCamber"], p = r.ActiveParameters["CamberPosition"];
                float t = r.ActiveParameters["MaxThickness"], a = r.ActiveParameters["AngleOfAttackDeg"];

                float lift = 0.11f * a + 0.12f * c;
                if (a > 12f) lift *= MathF.Exp(-0.35f * (a - 12f));            // Stall
                float drag = 0.006f + 0.0004f * t + 0.0000005f * (p - 40f) * (p - 40f) + 0.0006f * a * a
                             + (a > 12f ? 0.02f * (a - 12f) : 0f);

                r.PassiveParameters["Drag"] = drag;
                r.PassiveParameters["Lift"] = lift;
            },
            Fitness = () => new Naca2DFitnessCalculator(new ProjectConfig { ProjectName = "NacaLike" }),
            Iterations = 10,
            Variants = 10
        };

        [Fact]
        public void When_Nothing_Can_Improve_Any_More_The_Rounds_Stay_Near_The_Best()
        {
            int total = 0, near = 0, fallbacks = 0;

            for (int seed = 1; seed <= 4; seed++)
            {
                var (context, optimizer) = BayesHarness.Run(NacaLike(), "Bayes", seed, Sub("s" + seed));
                fallbacks += ((BayesOptimizationAlgorithm)optimizer).FallbackSelections;

                double best = 0.0;
                foreach (var round in context.History.Where(r => r.Variant != 99).GroupBy(r => r.Iteration).OrderBy(g => g.Key))
                {
                    if (round.Key >= 5)
                    {
                        foreach (ModelRecord r in round)
                        {
                            total++;
                            if (!r.SimulationFailed && r.Fitness >= 0.5 * best) near++;
                        }
                    }
                    best = Math.Max(best, round.Where(r => !r.SimulationFailed).Max(r => r.Fitness));
                }
            }

            double share = (double)near / total;
            _output.WriteLine($"Share of variants from round 5 onwards with at least 50% of best so far: {share:P0} ({near} of {total}); fallback {fallbacks}x");
            Assert.True(share >= 0.7, $"Only {share:P0} of late variants are near the best.");
        }
    }
}
