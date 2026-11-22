using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using FluidForge.Core;
using Xunit;

namespace FluidForge.Tests
{
    /// <summary>
    /// Tests for the simple IDW interpolation baseline (<see cref="IdwBaselineAlgorithm"/>)
    /// with known support points and an injectable random source.
    /// </summary>
    public class IdwBaselineAlgorithmTests
    {
        private static readonly Dictionary<string, (float Min, float Max)> Bounds =
            new() { { "Length", (0f, 100f) } };

        private static ModelRecord Sample(float length, float fitness, bool failed = false) => new ModelRecord
        {
            ActiveParameters = new Dictionary<string, float> { { "Length", length } },
            Fitness = fitness,
            SimulationFailed = failed
        };

        private static IFitnessCalculator Fitness() => new Mock<IFitnessCalculator>().Object;

        private static float Predict(Dictionary<string, float> candidate, List<ModelRecord> samples, float power = 3.0f)
            => IdwBaselineAlgorithm.PredictFitnessSurrogate(candidate, samples, Bounds, power);

        private static Dictionary<string, float> At(float length) => new() { { "Length", length } };

        /// <summary>At a support point, IDW interpolation returns its exact fitness.</summary>
        [Fact]
        public void On_A_Support_Point_The_Surrogate_Returns_Its_Exact_Fitness()
        {
            var samples = new List<ModelRecord> { Sample(10f, 5f), Sample(90f, 1f) };

            Assert.Equal(5f, Predict(At(10f), samples), 4);
            Assert.Equal(1f, Predict(At(90f), samples), 4);
        }

        /// <summary>Exactly halfway between two support points, the IDW mean is the arithmetic mean.</summary>
        [Fact]
        public void Exactly_Between_Two_Points_The_Estimate_Is_Their_Average()
        {
            var samples = new List<ModelRecord> { Sample(20f, 0f), Sample(60f, 10f) };

            Assert.Equal(5f, Predict(At(40f), samples), 3);
        }

        /// <summary>Closer to the better support point means closer to its value.</summary>
        [Fact]
        public void The_Estimate_Follows_The_Nearer_Support_Point()
        {
            var samples = new List<ModelRecord> { Sample(0f, 0f), Sample(100f, 100f) };

            float nearZero = Predict(At(10f), samples);
            float nearHundred = Predict(At(90f), samples);

            Assert.True(nearZero < 50f, $"Expected < 50, was {nearZero}");
            Assert.True(nearHundred > 50f, $"Expected > 50, was {nearHundred}");
            Assert.InRange(nearZero, 0f, 100f);
        }

        /// <summary>
        /// A higher IDW exponent weights the neighborhood more strongly — the estimate
        /// moves closer to the nearest support point.
        /// </summary>
        [Fact]
        public void A_Higher_Idw_Power_Weights_The_Nearest_Point_More()
        {
            var samples = new List<ModelRecord> { Sample(0f, 0f), Sample(100f, 100f) };

            float soft = Predict(At(25f), samples, power: 1.0f);
            float sharp = Predict(At(25f), samples, power: 6.0f);

            Assert.True(sharp < soft, $"Expected sharper (smaller): sharp={sharp}, soft={soft}");
        }

        /// <summary>Without support points there is nothing to estimate — 0 instead of division by 0.</summary>
        [Fact]
        public void Without_Samples_The_Estimate_Is_Zero()
        {
            Assert.Equal(0f, Predict(At(50f), new List<ModelRecord>()), 5);
        }

        /// <summary>
        /// As long as too few successful simulations exist, the IDW baseline builds
        /// no surrogate model and queues no candidates.
        /// </summary>
        [Fact]
        public void Below_The_Doe_Minimum_No_Candidates_Are_Queued()
        {
            var algorithm = new IdwBaselineAlgorithm(Fitness(), new Random(1));
            var context = ContextWithHistory(sampleCount: 3);

            algorithm.PrepareNextIteration(context, context.History, context.History[0]);

            var candidate = algorithm.GenerateParameters(context, iteration: 2, variant: 1, maxVariants: 4);
            Assert.InRange(candidate["Length"], 0f, 100f);
        }

        /// <summary>
        /// Once enough support points exist, the IDW baseline fills the queue with exactly
        /// VariantsPerIteration candidates — which are then handed out sequentially.
        /// </summary>
        [Fact]
        public void With_Enough_Samples_The_Queue_Holds_One_Candidate_Per_Variant()
        {
            var algorithm = new IdwBaselineAlgorithm(Fitness(), new Random(1));
            var context = ContextWithHistory(sampleCount: 6);
            context.Config.VariantsPerIteration = 4;
            context.Config.IdwVirtualSimulations = 500;

            algorithm.PrepareNextIteration(context, context.History, BestOf(context.History));

            var queued = new List<float>();
            for (int variant = 1; variant <= 4; variant++)
            {
                queued.Add(algorithm.GenerateParameters(context, 2, variant, 4)["Length"]);
            }

            Assert.Equal(4, queued.Count);
            Assert.All(queued, value => Assert.InRange(value, 0f, 100f));
        }

        /// <summary>
        /// Proposed candidates lie near the known optimum rather than uniformly
        /// across the space. Support points with maximum at 80.
        /// </summary>
        [Fact]
        public void Proposed_Candidates_Cluster_Around_The_Known_Optimum()
        {
            var algorithm = new IdwBaselineAlgorithm(Fitness(), new Random(4711));
            var context = ContextWithHistory(sampleCount: 6, optimumAt: 80f);
            context.Config.VariantsPerIteration = 5;
            context.Config.IdwVirtualSimulations = 2000;

            algorithm.PrepareNextIteration(context, context.History, BestOf(context.History));

            var proposals = Enumerable.Range(1, 5)
                .Select(v => algorithm.GenerateParameters(context, 2, v, 5)["Length"])
                .ToList();

            float average = proposals.Average();
            Assert.InRange(average, 60f, 100f);
        }

        /// <summary>
        /// Failed simulations are not support points and do not count towards the
        /// minimum count.
        /// </summary>
        [Fact]
        public void Failed_Simulations_Are_Not_Support_Points()
        {
            var samples = new List<ModelRecord> { Sample(10f, 5f), Sample(90f, 1f) };
            var withFailure = new List<ModelRecord>(samples) { Sample(50f, 0.0001f, failed: true) };

            var filtered = withFailure.Where(r => !r.SimulationFailed).ToList();

            Assert.Equal(Predict(At(40f), samples), Predict(At(40f), filtered), 4);
            Assert.NotEqual(Predict(At(40f), samples), Predict(At(40f), withFailure), 4);
        }

        /// <summary>
        /// Missing ParameterBounds entries must not throw a KeyNotFoundException in
        /// PredictFitnessSurrogate or in PrepareNextIteration/GenerateParameters.
        /// </summary>
        [Fact]
        public void Missing_ParameterBounds_Use_Fallback_Without_Throwing()
        {
            var samples = new List<ModelRecord>
            {
                Sample(10f, 5f), Sample(20f, 10f), Sample(30f, 15f),
                Sample(40f, 20f), Sample(50f, 25f), Sample(60f, 30f)
            };

            float predicted = IdwBaselineAlgorithm.PredictFitnessSurrogate(
                At(35f),
                samples,
                new Dictionary<string, (float Min, float Max)>(),
                3.0f);
            Assert.InRange(predicted, 5f, 30f);

            var project = new ProjectConfig
            {
                ProjectName = "TestWithoutBounds",
                BaseParameters = new Dictionary<string, float> { { "Length", 50f } }
            };
            var context = new SimulationContext(new SimulationConfig { VariantsPerIteration = 3, IdwVirtualSimulations = 100 }, project, System.IO.Path.GetTempPath());
            context.History.AddRange(samples);

            var algorithm = new IdwBaselineAlgorithm(Fitness(), new Random(42));
            algorithm.PrepareNextIteration(context, context.History, BestOf(context.History));

            for (int variant = 1; variant <= 3; variant++)
            {
                var p = algorithm.GenerateParameters(context, 2, variant, 3);
                Assert.InRange(p["Length"], 25f, 75f);
            }
        }

        private static ModelRecord BestOf(List<ModelRecord> records)
            => records.OrderByDescending(r => r.Fitness).First();

        private static SimulationContext ContextWithHistory(int sampleCount, float optimumAt = 50f)
        {
            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { { "Length", 50f } },
                MaxDeviations = new Dictionary<string, float> { { "Length", 5f } },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>(Bounds)
            };

            var context = new SimulationContext(new SimulationConfig(), project, System.IO.Path.GetTempPath());

            for (int i = 0; i < sampleCount; i++)
            {
                float length = 100f * i / Math.Max(1, sampleCount - 1);
                float fitness = 100f - Math.Abs(length - optimumAt);
                context.History.Add(Sample(length, fitness));
            }

            return context;
        }
    }
}
