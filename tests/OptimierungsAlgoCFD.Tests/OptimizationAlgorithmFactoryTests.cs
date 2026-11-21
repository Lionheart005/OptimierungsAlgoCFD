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
        [InlineData("IdwBaseline")]
        [InlineData("idwbaseline")]
        [InlineData("idw")]
        [InlineData("Rsm")]
        [InlineData("rsm")]
        [InlineData("  RSM  ")]
        public void Creates_IdwBaseline_Regardless_Of_Spelling_And_Legacy_Alias(string name)
        {
            Assert.IsType<IdwBaselineAlgorithm>(
                OptimizationAlgorithmFactory.Create(name, Fitness()));
        }

        /// <summary>
        /// Ein Tippfehler oder ein entferntes Verfahren (z. B. "Evolution") soll den Lauf
        /// nicht abbrechen, sondern auf das Standardverfahren zurückfallen.
        /// </summary>
        [Theory]
        [InlineData("Evolution")]
        [InlineData("evolution")]
        [InlineData("RMS")]
        [InlineData("Genetisch")]
        [InlineData("")]
        [InlineData(null)]
        public void Falls_Back_To_Default_For_Unknown_Or_Removed_Name(string? name)
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

    }
}
