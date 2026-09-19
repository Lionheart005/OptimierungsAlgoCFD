using System;
using System.Collections.Generic;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

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

        /// <summary>
        /// TODO-26: die Formel rechnet mit Drag, Volume und SensorDistance. Fehlt eine davon,
        /// soll der Controller nach der ersten Variante abbrechen — dafür muss der Rechner
        /// sie überhaupt erst melden. FrontalArea gehört bewusst nicht dazu: die geht als
        /// REF_AREA in den Solver, nicht in die Fitness.
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
        /// Ein Rechner, der sich nicht festlegt, erbt eine leere Liste — bestehende
        /// Implementierungen mussten für TODO-26 nicht angefasst werden.
        /// </summary>
        [Fact]
        public void A_Calculator_That_Declares_Nothing_Requires_Nothing()
        {
            IFitnessCalculator schweigsam = new SchweigsamerRechner();

            Assert.Empty(schweigsam.RequiredMetrics);
        }

        private class SchweigsamerRechner : IFitnessCalculator
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
            Assert.Equal(0.0001f, record.Fitness, 5); // Disqualifiziert
        }

        /// <summary>
        /// TODO-16: fehlt ein Pflicht-Ziel, muss der Fehler beim Verdrahten kommen —
        /// nicht als KeyNotFoundException mitten im Lauf. Die Meldung muss den fehlenden
        /// Schlüssel und die Datei nennen, in der er stehen soll.
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
            Assert.Contains("MantaAuv.json", ex.Message);
        }

        /// <summary>Ohne jedes Ziel werden alle drei fehlenden Schlüssel auf einmal gemeldet.</summary>
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

        /// <summary>Die mitgelieferte Projekt-Vorgabe muss vollständig sein.</summary>
        [Fact]
        public void MantaAuv_Defaults_Are_Complete()
        {
            var ex = Record.Exception(() => new MantaFitnessCalculator(MantaProjectConfig.Create()));

            Assert.Null(ex);
        }
    }
}

