using System;
using System.Collections.Generic;
using Xunit;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
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
            
            // Without penalty: (1000 * 10) / (2.0^1) = 5000
            // With penalty: 5000 * 0.1 = 500

            // Act
            calculator.CalculateFitness(record, simConfig);

            // Assert
            Assert.Equal(500f, record.Fitness, 3);
        }

        /// <summary>
        /// TODO-26: the formula computes with Drag, Volume, and SensorDistance. If one is missing,
        /// the controller should abort after the first variant — for that, the calculator
        /// must declare them first. FrontalArea is intentionally excluded: it goes into the solver
        /// as REF_AREA, not into fitness.
        /// </summary>
        [Fact]
        public void Required_Metrics_Name_Exactly_What_The_Formula_Reads()
        {
            var calculator = new MantaFitnessCalculator(MantaProjectConfig.Create());

            Assert.Equal(
                new[] { "Drag", "Volume", "SensorDistance" },
                calculator.RequiredMetrics);
        }

        /// <summary>
        /// A calculator that declares nothing inherits an empty list — existing
        /// implementations did not need to be touched for TODO-26.
        /// </summary>
        [Fact]
        public void A_Calculator_That_Declares_Nothing_Requires_Nothing()
        {
            IFitnessCalculator silent = new SilentCalculator();

            Assert.Empty(silent.RequiredMetrics);
        }

        private class SilentCalculator : IFitnessCalculator
        {
            public void CalculateFitness(ModelRecord record, SimulationConfig config) => record.Fitness = 1f;
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
            Assert.Equal(0.0001f, record.Fitness, 5); // Disqualified
        }

        /// <summary>
        /// TODO-16: if a required target is missing, the error must occur at wiring time —
        /// not as a KeyNotFoundException mid-run. The message must name the missing
        /// key and the file where it should be defined.
        /// </summary>
        [Theory]
        [InlineData("MinimumAllowedVolume")]
        [InlineData("MaximumAllowedVolume")]
        [InlineData("DragBalanceFactor")]
        public void Missing_Target_Fails_At_Construction_With_A_Clear_Message(string missingKey)
        {
            var config = MantaProjectConfig.Create();
            config.OptimizationTargets.Remove(missingKey);

            var ex = Assert.Throws<InvalidOperationException>(() => new MantaFitnessCalculator(config));

            Assert.Contains(missingKey, ex.Message);
            // The message must state WHERE to add it — since TODO-24 this is
            // project.json in the project folder rather than config/projects/MantaAuv.json.
            Assert.Contains("src/projects/MantaAuv/project.json", ex.Message);
        }

        /// <summary>Without any targets, all three missing keys are reported at once.</summary>
        [Fact]
        public void Empty_Targets_Report_All_Missing_Keys_At_Once()
        {
            var config = MantaProjectConfig.Create();
            config.OptimizationTargets = new Dictionary<string, float>();

            var ex = Assert.Throws<InvalidOperationException>(() => new MantaFitnessCalculator(config));

            Assert.Contains("MinimumAllowedVolume", ex.Message);
            Assert.Contains("MaximumAllowedVolume", ex.Message);
            Assert.Contains("DragBalanceFactor", ex.Message);
        }

        /// <summary>The bundled project defaults must be complete.</summary>
        [Fact]
        public void MantaAuv_Defaults_Are_Complete()
        {
            var ex = Record.Exception(() => new MantaFitnessCalculator(MantaProjectConfig.Create()));

            Assert.Null(ex);
        }
    }
}

