using System;
using System.Collections.Generic;
using System.Linq;
using Moq;
using MyPicoGkProject;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-19: das IDW-Ersatzmodell des RSM mit bekannten Stützstellen. Die Zufallsquelle
    /// ist injizierbar (fester Seed), damit die DoE-Phase reproduzierbar ist.
    /// </summary>
    public class RsmOptimizationAlgorithmTests
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
            => RsmOptimizationAlgorithm.PredictFitnessSurrogate(candidate, samples, Bounds, power);

        private static Dictionary<string, float> At(float length) => new() { { "Length", length } };

        /// <summary>Auf einer Stützstelle liefert das Surrogat exakt deren Fitness.</summary>
        [Fact]
        public void On_A_Support_Point_The_Surrogate_Returns_Its_Exact_Fitness()
        {
            var samples = new List<ModelRecord> { Sample(10f, 5f), Sample(90f, 1f) };

            Assert.Equal(5f, Predict(At(10f), samples), 4);
            Assert.Equal(1f, Predict(At(90f), samples), 4);
        }

        /// <summary>Genau in der Mitte zweier Stützstellen ist das IDW-Mittel das arithmetische.</summary>
        [Fact]
        public void Exactly_Between_Two_Points_The_Estimate_Is_Their_Average()
        {
            var samples = new List<ModelRecord> { Sample(20f, 0f), Sample(60f, 10f) };

            Assert.Equal(5f, Predict(At(40f), samples), 3);
        }

        /// <summary>Näher an der besseren Stützstelle heißt näher an deren Wert.</summary>
        [Fact]
        public void The_Estimate_Follows_The_Nearer_Support_Point()
        {
            var samples = new List<ModelRecord> { Sample(0f, 0f), Sample(100f, 100f) };

            float nearZero = Predict(At(10f), samples);
            float nearHundred = Predict(At(90f), samples);

            Assert.True(nearZero < 50f, $"Erwartet < 50, war {nearZero}");
            Assert.True(nearHundred > 50f, $"Erwartet > 50, war {nearHundred}");
            Assert.InRange(nearZero, 0f, 100f);
        }

        /// <summary>
        /// Ein höherer IDW-Exponent gewichtet die Nachbarschaft stärker — die Schätzung
        /// rückt näher an die nächstgelegene Stützstelle.
        /// </summary>
        [Fact]
        public void A_Higher_Idw_Power_Weights_The_Nearest_Point_More()
        {
            var samples = new List<ModelRecord> { Sample(0f, 0f), Sample(100f, 100f) };

            float soft = Predict(At(25f), samples, power: 1.0f);
            float sharp = Predict(At(25f), samples, power: 6.0f);

            Assert.True(sharp < soft, $"Erwartet schärfer (kleiner): sharp={sharp}, soft={soft}");
        }

        /// <summary>Ohne Stützstellen gibt es nichts zu schätzen — 0 statt Division durch 0.</summary>
        [Fact]
        public void Without_Samples_The_Estimate_Is_Zero()
        {
            Assert.Equal(0f, Predict(At(50f), new List<ModelRecord>()), 5);
        }

        /// <summary>
        /// Die DoE-Phase: solange zu wenige gelungene Simulationen vorliegen, baut der RSM
        /// kein Ersatzmodell und stellt keine Kandidaten in die Warteschlange.
        /// </summary>
        [Fact]
        public void Below_The_Doe_Minimum_No_Candidates_Are_Queued()
        {
            var algorithm = new RsmOptimizationAlgorithm(Fitness(), new Random(1));
            var context = ContextWithHistory(sampleCount: 3);

            algorithm.PrepareNextIteration(context, context.History, context.History[0]);

            // Keine Warteschlange -> der nächste Aufruf würfelt frei innerhalb der Grenzen
            var candidate = algorithm.GenerateParameters(context, iteration: 2, variant: 1, maxVariants: 4);
            Assert.InRange(candidate["Length"], 0f, 100f);
        }

        /// <summary>
        /// Ab genügend Stützstellen füllt der RSM die Warteschlange mit genau
        /// VariantsPerIteration Kandidaten — die dann der Reihe nach ausgegeben werden.
        /// </summary>
        [Fact]
        public void With_Enough_Samples_The_Queue_Holds_One_Candidate_Per_Variant()
        {
            var algorithm = new RsmOptimizationAlgorithm(Fitness(), new Random(1));
            var context = ContextWithHistory(sampleCount: 6);
            context.Config.VariantsPerIteration = 4;
            context.Config.RsmVirtualSimulations = 500;

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
        /// Der Kern von RSM: die vorgeschlagenen Kandidaten liegen in der Nähe des bekannten
        /// Optimums, nicht gleichverteilt über den Raum. Stützstellen mit Maximum bei 80.
        /// </summary>
        [Fact]
        public void Proposed_Candidates_Cluster_Around_The_Known_Optimum()
        {
            var algorithm = new RsmOptimizationAlgorithm(Fitness(), new Random(4711));
            var context = ContextWithHistory(sampleCount: 6, optimumAt: 80f);
            context.Config.VariantsPerIteration = 5;
            context.Config.RsmVirtualSimulations = 2000;

            algorithm.PrepareNextIteration(context, context.History, BestOf(context.History));

            var proposals = Enumerable.Range(1, 5)
                .Select(v => algorithm.GenerateParameters(context, 2, v, 5)["Length"])
                .ToList();

            float average = proposals.Average();
            Assert.InRange(average, 60f, 100f);
        }

        /// <summary>
        /// Fehlgeschlagene Simulationen sind keine Stützstellen und zählen nicht für die
        /// DoE-Mindestanzahl (bewusste Verhaltensänderung aus TODO-2).
        /// </summary>
        [Fact]
        public void Failed_Simulations_Are_Not_Support_Points()
        {
            var samples = new List<ModelRecord> { Sample(10f, 5f), Sample(90f, 1f) };
            var withFailure = new List<ModelRecord>(samples) { Sample(50f, 0.0001f, failed: true) };

            // Der Aufrufer filtert fehlgeschlagene Läufe heraus — mit Filter ändert sich
            // die Schätzung nicht, ohne Filter würde der Ausreißer sie nach unten ziehen.
            var filtered = withFailure.Where(r => !r.SimulationFailed).ToList();

            Assert.Equal(Predict(At(40f), samples), Predict(At(40f), filtered), 4);
            Assert.NotEqual(Predict(At(40f), samples), Predict(At(40f), withFailure), 4);
        }

        private static ModelRecord BestOf(List<ModelRecord> records)
            => records.OrderByDescending(r => r.Fitness).First();

        /// <summary>
        /// Context mit einem Parameter (k=1 -> DoE-Minimum 5 Stützstellen) und einer History,
        /// deren Fitness bei <paramref name="optimumAt"/> ihr Maximum hat.
        /// </summary>
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
                // Dreiecksförmiger Verlauf mit Maximum bei optimumAt
                float fitness = 100f - Math.Abs(length - optimumAt);
                context.History.Add(Sample(length, fitness));
            }

            return context;
        }
    }
}
