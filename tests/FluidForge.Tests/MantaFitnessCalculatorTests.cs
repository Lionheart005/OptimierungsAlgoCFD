using System;
using System.Collections.Generic;
using Xunit;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;

namespace FluidForge.Tests
{
    public class MantaFitnessCalculatorTests
    {
        [Fact]
        public void EvaluateObjectives_Computes_VolumetricDragCoeff_And_Efficiency_Without_Penalty_Cliff()
        {
            var config = MantaProjectConfig.Create();
            config.OptimizationTargets["MinimumAllowedVolume"] = 2000f;
            config.OptimizationTargets["MaximumAllowedVolume"] = 85000f;
            config.OptimizationTargets["DragBalanceFactor"] = 2.0f;

            var calculator = new MantaFitnessCalculator(config);
            var simConfig = new SimulationConfig();

            var record = new ModelRecord();
            record.PassiveParameters["Drag"] = 0.4f;
            record.PassiveParameters["FrontalArea"] = 400f; // mm^2
            record.PassiveParameters["Volume"] = 8000f;     // mm^3 -> V^(2/3) = 400 mm^2
            record.PassiveParameters["SensorDistance"] = 100f;

            // C_{D,V} = 0.4 * 400 / 8000^(2/3) = 0.4 * 400 / 400 = 0.4
            // HydrodynamicEfficiency = 100 / 0.4^2 = 625
            calculator.CalculateFitness(record, simConfig);

            Assert.True(record.Feasible);
            Assert.Equal(0.4f, record.PassiveParameters["VolumetricDragCoeff"], 4);
            Assert.Equal(625f, record.PassiveParameters["HydrodynamicEfficiency"], 3);
            Assert.Equal(625f, record.Fitness, 3);
            Assert.Single(record.Objectives);
            Assert.Equal("HydrodynamicEfficiency", record.Objectives[0].Name);
            Assert.Equal(OptimizationDirection.Maximize, record.Objectives[0].Direction);

            // Out-of-range volume does NOT apply a 0.1x cliff in the objective evaluator anymore;
            // volume limits are enforced pre-CFD by MantaGeometryConstraints.
            var lowVolRecord = new ModelRecord();
            lowVolRecord.PassiveParameters["Drag"] = 0.4f;
            lowVolRecord.PassiveParameters["FrontalArea"] = 400f;
            lowVolRecord.PassiveParameters["Volume"] = 1000f; // < 2000f
            lowVolRecord.PassiveParameters["SensorDistance"] = 100f;

            calculator.CalculateFitness(lowVolRecord, simConfig);
            float expectedCdV = 0.4f * 400f / MathF.Pow(1000f, 2f / 3f); // 1.6
            float expectedEff = 100f / (expectedCdV * expectedCdV);
            Assert.Equal(expectedCdV, lowVolRecord.PassiveParameters["VolumetricDragCoeff"], 4);
            Assert.Equal(expectedEff, lowVolRecord.Fitness, 3);
        }

        [Fact]
        public void GeometryConstraints_Enforces_Volume_InRange_PreSolve()
        {
            var config = MantaProjectConfig.Create();
            config.OptimizationTargets["MinimumAllowedVolume"] = 2000f;
            config.OptimizationTargets["MaximumAllowedVolume"] = 85000f;

            var constraints = new MantaGeometryConstraints();

            var tooSmall = new ModelRecord();
            tooSmall.PassiveParameters["Volume"] = 1500f;
            var resSmall = constraints.EvaluateGeometryConstraints(tooSmall, config);
            Assert.Equal(2, resSmall.Count);
            Assert.All(resSmall, c => Assert.True(c.IsPreSolve));
            Assert.False(resSmall[0].IsSatisfied);
            Assert.Equal(-500f, resSmall[0].Margin, 3);
            Assert.True(resSmall[1].IsSatisfied);

            var valid = new ModelRecord();
            valid.PassiveParameters["Volume"] = 12000f;
            Assert.All(constraints.EvaluateGeometryConstraints(valid, config), c => Assert.True(c.IsSatisfied));

            var tooLarge = new ModelRecord();
            tooLarge.PassiveParameters["Volume"] = 90000f;
            var resLarge = constraints.EvaluateGeometryConstraints(tooLarge, config);
            Assert.True(resLarge[0].IsSatisfied);
            Assert.False(resLarge[1].IsSatisfied);
            Assert.Equal(-5000f, resLarge[1].Margin, 3);
        }

        [Fact]
        public void Required_Metrics_Name_Exactly_What_The_Formula_Reads()
        {
            var calculator = new MantaFitnessCalculator(MantaProjectConfig.Create());

            Assert.Equal(
                new[] { "Drag", "FrontalArea", "Volume", "SensorDistance" },
                calculator.RequiredMetrics);
        }

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
            var calculator = new MantaFitnessCalculator(MantaProjectConfig.Create());
            var record = new ModelRecord();
            record.PassiveParameters["Drag"] = float.MaxValue;
            record.PassiveParameters["FrontalArea"] = 400f;
            record.PassiveParameters["Volume"] = 5000f;
            record.PassiveParameters["SensorDistance"] = 100f;

            calculator.CalculateFitness(record, new SimulationConfig());

            Assert.Equal(ObjectiveEvaluatorBase.DefaultFailedFitness, record.Fitness, 5);
        }

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
            Assert.Contains("src/projects/MantaAuv/project.json", ex.Message);
        }

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

        [Fact]
        public void MantaAuv_Defaults_Are_Complete()
        {
            var ex = Record.Exception(() => new MantaFitnessCalculator(MantaProjectConfig.Create()));

            Assert.Null(ex);
        }
    }
}
