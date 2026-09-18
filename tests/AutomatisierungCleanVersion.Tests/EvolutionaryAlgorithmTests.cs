using System.Collections.Generic;
using System.Linq;
using Moq;
using Xunit;
using MyPicoGkProject;

namespace AutomatisierungCleanVersion.Tests
{
    public class EvolutionaryAlgorithmTests
    {
        [Fact]
        public void EvaluateAndSelectBest_Should_Delegate_To_FitnessCalculator()
        {
            // Arrange
            var mockFitness = new Mock<IFitnessCalculator>();
            
            // Simuliere, dass der Calculator der Variante 2 die beste Fitness gibt
            mockFitness.Setup(f => f.CalculateFitness(It.Is<ModelRecord>(r => r.Variant == 1), It.IsAny<SimulationConfig>()))
                .Callback<ModelRecord, SimulationConfig>((r, c) => r.Fitness = 10f);
            
            mockFitness.Setup(f => f.CalculateFitness(It.Is<ModelRecord>(r => r.Variant == 2), It.IsAny<SimulationConfig>()))
                .Callback<ModelRecord, SimulationConfig>((r, c) => r.Fitness = 50f);

            var algorithm = new EvolutionaryAlgorithm(mockFitness.Object);
            
            var records = new List<ModelRecord>
            {
                new ModelRecord { Variant = 1 },
                new ModelRecord { Variant = 2 }
            };

            var context = new SimulationContext(new SimulationConfig(), new ProjectConfig());

            // Act
            var best = algorithm.EvaluateAndSelectBest(records, context);

            // Assert
            Assert.Equal(2, best.Variant);
            Assert.Equal(50f, best.Fitness);
            
            // Prüfen, ob der Calculator für beide aufgerufen wurde
            mockFitness.Verify(f => f.CalculateFitness(It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>()), Times.Exactly(2));
        }

        /// <summary>
        /// TODO-19: die Zufallsquelle ist injizierbar. Mit demselben Seed muss dieselbe
        /// Mutation herauskommen — sonst lässt sich ein Lauf nicht nachstellen.
        /// </summary>
        [Fact]
        public void Same_Seed_Produces_The_Same_Mutation()
        {
            var fitness = new Mock<IFitnessCalculator>().Object;

            Dictionary<string, float> Mutate(int seed)
            {
                var algorithm = new EvolutionaryAlgorithm(fitness, new System.Random(seed));
                return algorithm.GenerateParameters(NewContext(), iteration: 1, variant: 2, maxVariants: 10);
            }

            var first = Mutate(1234);
            var second = Mutate(1234);
            var other = Mutate(4321);

            Assert.Equal(first, second);
            Assert.NotEqual(first, other);
        }

        /// <summary>Variante 1 ist der unveränderte Arbeitspunkt (Elite), nicht mutiert.</summary>
        [Fact]
        public void Variant_One_Is_The_Unchanged_Base_Point()
        {
            var algorithm = new EvolutionaryAlgorithm(new Mock<IFitnessCalculator>().Object, new System.Random(7));
            var context = NewContext();

            var parameters = algorithm.GenerateParameters(context, iteration: 3, variant: 1, maxVariants: 10);

            Assert.Equal(context.CurrentBaseParameters, parameters);
        }

        /// <summary>Mutierte Werte bleiben innerhalb der Parameter-Grenzen des Projekts.</summary>
        [Fact]
        public void Mutations_Stay_Within_The_Parameter_Bounds()
        {
            var algorithm = new EvolutionaryAlgorithm(new Mock<IFitnessCalculator>().Object, new System.Random(99));
            var context = NewContext();

            for (int variant = 2; variant <= 10; variant++)
            {
                var mutant = algorithm.GenerateParameters(context, 1, variant, 10);
                Assert.InRange(mutant["Length"], 30f, 100f);
                Assert.InRange(mutant["TailTaper"], 0.1f, 1.5f);
            }
        }

        private static SimulationContext NewContext()
        {
            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { { "Length", 50f }, { "TailTaper", 1f } },
                MaxDeviations = new Dictionary<string, float> { { "Length", 5f }, { "TailTaper", 0.1f } },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    { "Length", (30f, 100f) },
                    { "TailTaper", (0.1f, 1.5f) }
                }
            };

            return new SimulationContext(new SimulationConfig(), project);
        }
    }
}

