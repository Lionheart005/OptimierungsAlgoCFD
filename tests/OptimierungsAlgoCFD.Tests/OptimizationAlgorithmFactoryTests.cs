using Moq;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Projects.MantaAuv;
using OptimierungsAlgoCFD.Solvers.Cfd;
using Xunit;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// TODO-10: die Algorithmuswahl kommt aus der Framework-Konfiguration
    /// (simulation.json des Projekts) statt aus auskommentiertem Code in Program.cs.
    /// </summary>
    public class OptimizationAlgorithmFactoryTests
    {
        private static IFitnessCalculator Fitness() => new Mock<IFitnessCalculator>().Object;

        [Theory]
        [InlineData("Rsm")]
        [InlineData("rsm")]
        [InlineData("  RSM  ")]
        public void Creates_Rsm_Regardless_Of_Spelling(string name)
        {
            Assert.IsType<RsmOptimizationAlgorithm>(
                OptimizationAlgorithmFactory.Create(name, Fitness()));
        }

        [Theory]
        [InlineData("Evolution")]
        [InlineData("evolution")]
        public void Creates_EvolutionaryAlgorithm(string name)
        {
            Assert.IsType<EvolutionaryAlgorithm>(
                OptimizationAlgorithmFactory.Create(name, Fitness()));
        }

        /// <summary>
        /// Ein Tippfehler in der JSON soll den Lauf nicht abbrechen (Entscheidung des
        /// Nutzers), sondern auf das Standardverfahren zurückfallen.
        /// </summary>
        [Theory]
        [InlineData("RMS")]
        [InlineData("Genetisch")]
        [InlineData("")]
        [InlineData(null)]
        public void Falls_Back_To_Default_For_Unknown_Name(string? name)
        {
            Assert.IsType<BayesOptimizationAlgorithm>(
                OptimizationAlgorithmFactory.Create(name, Fitness()));
        }

        /// <summary>
        /// Standard seit 08.11.2026: Bayes (10 × 10-Vergleich auf Naca2D, siehe .plans/vergleich_optimierer.md).
        /// </summary>
        [Fact]
        public void Framework_Defaults_To_Bayes()
        {
            Assert.Equal("Bayes", SimulationConfig.CreateDefault().OptimizationAlgorithm);
        }

        /// <summary>
        /// MantaAuv wählt "Rsm" ausdrücklich: Bayes ist im 3D-Fall (Modus "Metrics") noch nicht auf dem Server
        /// gemessen. Wird das nachgeholt, diesen Test bewusst ändern und den Eintrag in der simulation.json löschen.
        /// </summary>
        [Fact]
        public void Repository_Simulation_Json_Selects_Rsm()
        {
            Assert.Equal("Rsm", MantaProjectFiles.Simulation().OptimizationAlgorithm);
        }
    }
}
