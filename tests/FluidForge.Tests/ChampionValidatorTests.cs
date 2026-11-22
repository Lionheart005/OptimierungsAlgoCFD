using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;
using Xunit;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-19 / TODO-3: the bouncer validates fitness (no longer "Drag") and is
    /// part of the framework. Tested: confirmation, disqualification, fallback
    /// when no candidate is stable, and that no more re-simulations than necessary run.
    /// </summary>
    public class ChampionValidatorTests
    {
        private static ModelRecord Candidate(int variant, float fitness) => new ModelRecord
        {
            Iteration = 1,
            Variant = variant,
            Fitness = fitness,
            ActiveParameters = new Dictionary<string, float> { { "Length", 50f + variant } }
        };

        /// <summary>
        /// Fitness calculator that assigns a fixed value to every re-simulation. The validator
        /// calls it for the re-simulation result — not for the candidates.
        /// </summary>
        private static IFitnessCalculator FitnessReturning(float value)
        {
            var mock = new Mock<IFitnessCalculator>();
            mock.Setup(f => f.CalculateFitness(It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>()))
                .Callback<ModelRecord, SimulationConfig>((r, c) => r.Fitness = value);
            return mock.Object;
        }

        private static SimulationConfig Config(float tolerance = 0.2f) => new SimulationConfig
        {
            BouncerTolerance = tolerance,
            BouncerJitter = 0.01f
        };

        [Fact]
        public void Stable_Candidate_Is_Confirmed()
        {
            // Re-simulation yields 9.5 vs reference 10.0 -> 5% deviation, within 20%
            var validator = new ChampionValidator(FitnessReturning(9.5f), new Random(1));
            var candidates = new List<ModelRecord> { Candidate(1, 8f), Candidate(2, 10f) };

            var winner = validator.ValidateChampion(candidates, Config(), _ => new ModelRecord());

            Assert.Equal(2, winner.Variant);
            Assert.Equal(10f, winner.Fitness);   // unchanged, not disqualified
        }

        /// <summary>
        /// If fitness deviates too much, the candidate is disqualified (fitness 0.0001)
        /// and the next best one is tested.
        /// </summary>
        [Fact]
        public void Unstable_Candidate_Is_Disqualified_And_The_Next_One_Is_Tested()
        {
            var best = Candidate(1, 100f);
            var second = Candidate(2, 10f);

            // Re-simulation is always 10.0: for 100 it is 90% deviation (out),
            // for 10 it is 0% (confirmed).
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { best, second }, Config(), _ => new ModelRecord());

            Assert.Equal(2, winner.Variant);
            Assert.Equal(0.0001f, best.Fitness, 6);   // disqualified
            Assert.True(best.Disqualified);           // and flagged (TODO-53)
            Assert.False(second.Disqualified);
        }

        /// <summary>TODO-53: the "re-simulation throws" branch also flags the candidate.</summary>
        [Fact]
        public void A_Failing_Resimulation_Also_Sets_The_Disqualified_Flag()
        {
            var candidate = Candidate(1, 10f);
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(1));

            validator.ValidateChampion(
                new List<ModelRecord> { candidate }, Config(),
                _ => throw new InvalidOperationException("Mesh failed"));

            Assert.True(candidate.Disqualified);
            Assert.Equal(0.0001f, candidate.Fitness, 6);
        }

        /// <summary>
        /// If no candidate is stable, the run does not abort — the best of the rest wins.
        /// </summary>
        [Fact]
        public void Falls_Back_To_The_Best_Of_The_Rest_When_Nothing_Is_Stable()
        {
            var best = Candidate(1, 100f);
            var second = Candidate(2, 50f);
            var validator = new ChampionValidator(FitnessReturning(1f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { second, best }, Config(), _ => new ModelRecord());

            // sorted descending by fitness -> 100 was first
            Assert.Equal(1, winner.Variant);
            Assert.Equal(0.0001f, best.Fitness, 6);
            Assert.Equal(0.0001f, second.Fitness, 6);
        }

        /// <summary>If re-simulation aborts, the candidate counts as unstable — no exception escapes.</summary>
        [Fact]
        public void Failing_Resimulation_Disqualifies_Instead_Of_Throwing()
        {
            var best = Candidate(1, 100f);
            var second = Candidate(2, 10f);
            int calls = 0;

            var validator = new ChampionValidator(FitnessReturning(10f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { best, second },
                Config(),
                _ =>
                {
                    calls++;
                    if (calls == 1) throw new InvalidOperationException("Solver crashed");
                    return new ModelRecord();
                });

            Assert.Equal(2, winner.Variant);
            Assert.Equal(0.0001f, best.Fitness, 6);
        }

        /// <summary>
        /// Candidates below the test threshold (0.1) are not re-simulated — otherwise
        /// the framework would spend compute time on obviously unusable models.
        /// </summary>
        [Fact]
        public void Candidates_Below_The_Test_Threshold_Are_Not_Resimulated()
        {
            int calls = 0;
            var validator = new ChampionValidator(FitnessReturning(1f), new Random(1));

            var winner = validator.ValidateChampion(
                new List<ModelRecord> { Candidate(1, 0.05f), Candidate(2, 0.0001f) },
                Config(),
                _ => { calls++; return new ModelRecord(); });

            Assert.Equal(0, calls);
            Assert.Equal(1, winner.Variant);   // Best of the rest
        }

        /// <summary>The confirmed champion costs exactly one re-simulation.</summary>
        [Fact]
        public void Confirmed_Champion_Costs_Exactly_One_Resimulation()
        {
            int calls = 0;
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(1));

            validator.ValidateChampion(
                new List<ModelRecord> { Candidate(1, 10f), Candidate(2, 9f) },
                Config(),
                _ => { calls++; return new ModelRecord(); });

            Assert.Equal(1, calls);
        }

        /// <summary>Jitter perturbs exactly one parameter by the exact configured amount.</summary>
        [Fact]
        public void Jitter_Changes_Exactly_One_Parameter_By_The_Configured_Amount()
        {
            var candidate = new ModelRecord
            {
                Variant = 1,
                Fitness = 10f,
                ActiveParameters = new Dictionary<string, float>
                {
                    { "Length", 50f }, { "Width", 40f }, { "TailTaper", 1f }
                }
            };

            Dictionary<string, float>? seen = null;
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(42));

            validator.ValidateChampion(
                new List<ModelRecord> { candidate },
                Config(),
                p => { seen = p; return new ModelRecord(); });

            Assert.NotNull(seen);
            var changed = seen!.Where(kvp => candidate.ActiveParameters[kvp.Key] != kvp.Value).ToList();
            Assert.Single(changed);
            Assert.Equal(0.01f, Math.Abs(changed[0].Value - candidate.ActiveParameters[changed[0].Key]), 5);

            // The candidate itself remains untouched — jitter runs on a copy.
            Assert.Equal(50f, candidate.ActiveParameters["Length"]);
        }

        [Fact]
        public void Empty_Candidate_List_Throws()
        {
            var validator = new ChampionValidator(FitnessReturning(1f), new Random(1));

            Assert.Throws<InvalidOperationException>(
                () => validator.ValidateChampion(new List<ModelRecord>(), Config(), _ => new ModelRecord()));
        }

        [Fact]
        public void Jitter_With_Bounds_Scales_Relatively_To_Parameter_Range()
        {
            var candidate = new ModelRecord
            {
                Variant = 1,
                Fitness = 10f,
                ActiveParameters = new Dictionary<string, float> { { "Length", 100f } }
            };

            var bounds = new Dictionary<string, (float Min, float Max)>
            {
                { "Length", (50f, 250f) } // Range = 200f
            };

            Dictionary<string, float>? seen = null;
            // Random with fixed seed
            var validator = new ChampionValidator(FitnessReturning(10f), new Random(42), bounds);

            var config = new SimulationConfig
            {
                BouncerTolerance = 0.2f,
                BouncerJitter = 0.05f // 5% of span 200f = 10.0f
            };

            validator.ValidateChampion(
                new List<ModelRecord> { candidate },
                config,
                p => { seen = p; return new ModelRecord(); });

            Assert.NotNull(seen);
            float diff = Math.Abs(seen!["Length"] - 100f);
            Assert.Equal(10.0f, diff, 4);
        }

        [Fact]
        public void Jitter_With_Bounds_Clamps_To_Min_And_Max()
        {
            var candidateAtMin = new ModelRecord
            {
                Variant = 1,
                Fitness = 10f,
                ActiveParameters = new Dictionary<string, float> { { "Camber", 0.0f } }
            };

            var bounds = new Dictionary<string, (float Min, float Max)>
            {
                { "Camber", (0.0f, 0.1f) } // Min = 0.0
            };

            var config = new SimulationConfig
            {
                BouncerTolerance = 0.2f,
                BouncerJitter = 0.5f // Large jitter
            };

            // Test across multiple runs that the value never falls below Min or exceeds Max
            for (int seed = 0; seed < 20; seed++)
            {
                Dictionary<string, float>? seen = null;
                var validator = new ChampionValidator(FitnessReturning(10f), new Random(seed), bounds);

                validator.ValidateChampion(
                    new List<ModelRecord> { candidateAtMin },
                    config,
                    p => { seen = p; return new ModelRecord(); });

                Assert.NotNull(seen);
                Assert.InRange(seen!["Camber"], 0.0f, 0.1f);
            }
        }
    }
}
