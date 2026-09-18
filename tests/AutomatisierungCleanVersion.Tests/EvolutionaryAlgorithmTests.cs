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
    }
}

