using System;
using System.IO;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    public class ProjectConfigJsonTests
    {
        /// <summary>
        /// Die mitgelieferte config/projects/MantaAuv.json muss dieselben Zahlen enthalten
        /// wie die Code-Vorgabe. Weicht eine ab, rechnet die Pipeline anders als vor dem
        /// Umzug nach JSON — genau das soll dieser Test verhindern.
        /// </summary>
        [Fact]
        public void MantaAuv_Json_Matches_Code_Defaults()
        {
            string? directory = JsonConfigLoader.ResolveConfigDirectory(null);
            Assert.NotNull(directory);
            Assert.True(File.Exists(Path.Combine(directory!, "projects", "MantaAuv.json")),
                "config/projects/MantaAuv.json fehlt.");

            var expected = MantaProjectConfig.Create();
            var actual = JsonConfigLoader.LoadProjectConfig("MantaAuv", expected);

            Assert.Equal(expected.ProjectName, actual.ProjectName);
            Assert.Equal(expected.BaseParameters, actual.BaseParameters);
            Assert.Equal(expected.MaxDeviations, actual.MaxDeviations);
            Assert.Equal(expected.OptimizationTargets, actual.OptimizationTargets);
            Assert.Equal(expected.DimensionalParameters, actual.DimensionalParameters);
            Assert.Equal(expected.ParameterBounds, actual.ParameterBounds);
        }

        [Fact]
        public void Dto_Roundtrip_Preserves_ParameterBounds()
        {
            var original = MantaProjectConfig.Create();

            var roundtripped = ProjectConfigDto.FromProjectConfig(original).ToProjectConfig();

            Assert.Equal(original.ParameterBounds, roundtripped.ParameterBounds);
            Assert.Equal(30.0f, roundtripped.ParameterBounds["Length"].Min);
            Assert.Equal(100.0f, roundtripped.ParameterBounds["Length"].Max);
        }

        [Fact]
        public void Json_Overrides_Only_What_It_Names()
        {
            string directory = Path.Combine(Path.GetTempPath(), "projtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(directory, "projects"));
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "projects", "MantaAuv.json"),
                    @"{ ""BaseParameters"": { ""Length"": 77.0 } }");

                var defaults = MantaProjectConfig.Create();
                var loaded = JsonConfigLoader.LoadProjectConfig("MantaAuv", defaults, directory);

                Assert.Equal(77.0f, loaded.BaseParameters["Length"]);
                // Nicht genannte Werte bleiben auf der Code-Vorgabe
                Assert.Equal(defaults.BaseParameters["Width"], loaded.BaseParameters["Width"]);
                Assert.Equal(defaults.ParameterBounds["Length"], loaded.ParameterBounds["Length"]);
                Assert.Equal(defaults.DimensionalParameters, loaded.DimensionalParameters);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
