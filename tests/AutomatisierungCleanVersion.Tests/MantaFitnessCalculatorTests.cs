using System.Collections.Generic;
using Xunit;
using MyPicoGkProject;

namespace AutomatisierungCleanVersion.Tests
{
    public class MantaFitnessCalculatorTests
    {
        [Fact]
        public void CalculateFitness_Should_Apply_Penalty_If_Volume_Below_Min()
        {
            // Arrange
            var config = MantaProjectConfig.Create();
            config.OptimizationTargets["MinimumAllowedVolume"] = 2000f;
            config.OptimizationTargets["MaximumAllowedVolume"] = 85000f;
            config.OptimizationTargets["DragBalanceFactor"] = 1f;

            var calculator = new MantaFitnessCalculator(config);
            var simConfig = new SimulationConfig(); // Framework config
            
            var record = new ModelRecord();
            record.PassiveParameters["Drag"] = 2.0f;
            record.PassiveParameters["Volume"] = 1000f; // < 2000f -> Penalty * 0.1
            record.PassiveParameters["SensorDistance"] = 10f;
            
            // Ohne Penalty: (1000 * 10) / (2.0^1) = 5000
            // Mit Penalty: 5000 * 0.1 = 500

            // Act
            calculator.CalculateFitness(record, simConfig);

            // Assert
            Assert.Equal(500f, record.Fitness, 3);
        }

        [Fact]
        public void CalculateFitness_Should_Disqualify_If_Drag_Max()
        {
            // Arrange
            var calculator = new MantaFitnessCalculator(MantaProjectConfig.Create());
            var record = new ModelRecord();
            record.PassiveParameters["Drag"] = float.MaxValue;
            record.PassiveParameters["Volume"] = 5000f;
            
            // Act
            calculator.CalculateFitness(record, new SimulationConfig());

            // Assert
            Assert.Equal(0.0001f, record.Fitness, 5); // Disqualifiziert
        }
    }
}

