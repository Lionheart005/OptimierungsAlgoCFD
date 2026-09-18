using Moq;
using MyPicoGkProject;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-10: die Algorithmuswahl kommt aus der Framework-Konfiguration
    /// (config/simulation.json) statt aus auskommentiertem Code in Program.cs.
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
            Assert.IsType<RsmOptimizationAlgorithm>(
                OptimizationAlgorithmFactory.Create(name, Fitness()));
        }

        /// <summary>
        /// Die Code-Vorgabe muss das Verfahren benennen, das vor TODO-10 fest in
        /// Program.cs stand — sonst rechnet der nächste Lauf anders als bisher.
        /// </summary>
        [Fact]
        public void Framework_Defaults_To_Rsm()
        {
            Assert.Equal("Rsm", SimulationConfig.CreateDefault().OptimizationAlgorithm);
        }

        /// <summary>
        /// Die mitgelieferte config/simulation.json muss dieselbe Wahl treffen wie die
        /// Code-Vorgabe — der Schlüssel liegt seit der Korrektur dort und nicht mehr
        /// in der Projekt-JSON.
        /// </summary>
        [Fact]
        public void Repository_Simulation_Json_Selects_Rsm()
        {
            Assert.Equal("Rsm", JsonConfigLoader.LoadSimulationConfig().OptimizationAlgorithm);
        }
    }
}
