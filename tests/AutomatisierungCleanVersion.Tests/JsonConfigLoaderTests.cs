using System;
using System.IO;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    public class JsonConfigLoaderTests : IDisposable
    {
        private readonly string _directory;

        public JsonConfigLoaderTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "cfgtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private string WriteFile(string name, string content)
        {
            string path = Path.Combine(_directory, name);
            File.WriteAllText(path, content);
            return path;
        }

        [Fact]
        public void Load_Without_Files_Keeps_Defaults()
        {
            var config = JsonConfigLoader.Load(
                SimulationConfig.CreateDefault(),
                Path.Combine(_directory, "gibtsnicht.json"));

            Assert.Equal(10, config.MaxIterations);
            Assert.Equal("gmsh", config.GmshPath);
        }

        [Fact]
        public void Load_Applies_Only_Keys_Present_In_File()
        {
            string path = WriteFile("simulation.json", @"{ ""MaxIterations"": 3 }");

            var config = JsonConfigLoader.Load(SimulationConfig.CreateDefault(), path);

            Assert.Equal(3, config.MaxIterations);
            // Nicht genannte Werte bleiben auf dem Standard
            Assert.Equal(10, config.VariantsPerIteration);
            Assert.Equal(0.2f, config.BouncerTolerance, 5);
        }

        [Fact]
        public void Local_File_Overlays_Base_File()
        {
            string basePath = WriteFile("simulation.json", @"{ ""GmshPath"": ""gmsh"", ""MpiCores"": 12 }");
            string localPath = WriteFile("simulation.local.json", @"{ ""GmshPath"": ""C:\\gmsh\\gmsh.exe"" }");

            var config = JsonConfigLoader.Load(SimulationConfig.CreateDefault(), basePath, localPath);

            Assert.Equal(@"C:\gmsh\gmsh.exe", config.GmshPath); // lokal gewinnt
            Assert.Equal(12, config.MpiCores);                  // aus der Basisdatei
        }

        [Fact]
        public void Invalid_Json_Throws_With_File_Name()
        {
            string path = WriteFile("simulation.json", "{ kaputt ");

            var ex = Assert.Throws<InvalidOperationException>(
                () => JsonConfigLoader.Load(SimulationConfig.CreateDefault(), path));

            Assert.Contains("simulation.json", ex.Message);
        }

        /// <summary>
        /// Rauchtest für die mitgelieferte src/projects/MantaAuv/simulation.json: sie muss
        /// gefunden werden (Suche nach oben, weil die Tests aus bin/Debug/net9.0 laufen),
        /// gültiges JSON sein und dieselben Werte liefern wie die einkompilierten Standardwerte.
        /// </summary>
        [Fact]
        public void Repository_Config_File_Is_Found_And_Matches_Defaults()
        {
            var fromFile = MantaProjectFiles.Simulation();
            var defaults = SimulationConfig.CreateDefault();

            Assert.Equal(defaults.MaxIterations, fromFile.MaxIterations);
            Assert.Equal(defaults.VariantsPerIteration, fromFile.VariantsPerIteration);
            // Die Programmpfade sind bewusst NICHT dabei: MantaAuv weicht dort vom
            // Code-Standard ab, weil der Hochschulserver sie nicht über den PATH findet.
            // Geprüft wird das in ProjectLayoutTests.
            Assert.Equal(defaults.BouncerTolerance, fromFile.BouncerTolerance, 5);
            Assert.Equal(defaults.RsmVirtualSimulations, fromFile.RsmVirtualSimulations);
            Assert.Equal(defaults.OptimizationAlgorithm, fromFile.OptimizationAlgorithm);
        }

        [Fact]
        public void Comments_And_Trailing_Commas_Are_Accepted()
        {
            string path = WriteFile("simulation.json", @"{
                // Kommentar
                ""MpiCores"": 4,
            }");

            var config = JsonConfigLoader.Load(SimulationConfig.CreateDefault(), path);

            Assert.Equal(4, config.MpiCores);
        }
    }
}
