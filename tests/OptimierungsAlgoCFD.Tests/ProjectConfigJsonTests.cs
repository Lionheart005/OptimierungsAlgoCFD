using System;
using System.IO;
using Xunit;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Projects.MantaAuv;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    public class ProjectConfigJsonTests
    {
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
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, ProjectPaths.ManifestFileName),
                    @"{ ""BaseParameters"": { ""Length"": 77.0 } }");

                var defaults = MantaProjectConfig.Create();
                var loaded = JsonConfigLoader.LoadForProject(
                        ProjectConfigDto.FromProjectConfig(defaults),
                        ProjectPaths.ManifestFileName, directory)
                    .Value.ToProjectConfig();

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
