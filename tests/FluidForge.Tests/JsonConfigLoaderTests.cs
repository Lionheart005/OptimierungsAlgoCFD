using System;
using System.IO;
using Xunit;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
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
                Path.Combine(_directory, "doesnotexist.json"));

            Assert.Equal(10, config.MaxIterations);
            Assert.Equal("gmsh", config.GmshPath);
        }

        [Fact]
        public void Load_Applies_Only_Keys_Present_In_File()
        {
            string path = WriteFile("simulation.json", @"{ ""MaxIterations"": 3 }");

            var config = JsonConfigLoader.Load(SimulationConfig.CreateDefault(), path);

            Assert.Equal(3, config.MaxIterations);
            // Unspecified values remain at their defaults
            Assert.Equal(10, config.VariantsPerIteration);
            Assert.Equal(0.2f, config.BouncerTolerance, 5);
        }

        [Fact]
        public void Local_File_Overlays_Base_File()
        {
            string basePath = WriteFile("simulation.json", @"{ ""GmshPath"": ""gmsh"", ""MpiCores"": 12 }");
            string localPath = WriteFile("simulation.local.json", @"{ ""GmshPath"": ""C:\\gmsh\\gmsh.exe"" }");

            var config = JsonConfigLoader.Load(SimulationConfig.CreateDefault(), basePath, localPath);

            Assert.Equal(@"C:\gmsh\gmsh.exe", config.GmshPath); // local wins
            Assert.Equal(12, config.MpiCores);                  // from base file
        }

        [Fact]
        public void Invalid_Json_Throws_With_File_Name()
        {
            string path = WriteFile("simulation.json", "{ broken ");

            var ex = Assert.Throws<InvalidOperationException>(
                () => JsonConfigLoader.Load(SimulationConfig.CreateDefault(), path));

            Assert.Contains("simulation.json", ex.Message);
        }

        /// <summary>
        /// Smoke test for the bundled src/projects/MantaAuv/simulation.json: it must be found
        /// (searching upwards, because tests run from bin/Debug/net9.0) and load cleanly (valid JSON,
        /// only known keys). The VALUES inside are trial settings and are not checked.
        /// </summary>
        [Fact]
        public void Repository_Config_File_Is_Found_And_Loads()
        {
            Assert.NotNull(MantaProjectFiles.Simulation());
        }

        [Fact]
        public void Comments_And_Trailing_Commas_Are_Accepted()
        {
            string path = WriteFile("simulation.json", @"{
                // Comment
                ""MpiCores"": 4,
            }");

            var config = JsonConfigLoader.Load(SimulationConfig.CreateDefault(), path);

            Assert.Equal(4, config.MpiCores);
        }
    }
}
