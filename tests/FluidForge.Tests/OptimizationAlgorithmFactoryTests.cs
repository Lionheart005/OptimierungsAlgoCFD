using Moq;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;
using Xunit;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-10: algorithm selection comes from the framework configuration
    /// (project simulation.json) rather than commented-out code in Program.cs.
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
        /// A typo or a removed algorithm (e.g. "Evolution") must not abort the run,
        /// but fall back to the default algorithm.
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
        /// Default since 2026-11-08: Bayes (10 × 10 comparison on Naca2D, see .plans/vergleich_optimierer.md).
        /// </summary>
        [Fact]
        public void Framework_Defaults_To_Bayes()
        {
            Assert.Equal("Bayes", SimulationConfig.CreateDefault().OptimizationAlgorithm);
        }

    }
}
