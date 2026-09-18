using System;
using System.IO;
using Xunit;
using MyPicoGkProject;

namespace AutomatisierungCleanVersion.Tests
{
    public class SolverOptionsTests
    {
        /// <summary>
        /// config/solvers/su2.json muss dieselben Werte enthalten wie die Code-Standards —
        /// sonst rechnet SU2 mit anderen Fluideigenschaften als vor dem Umzug nach JSON.
        /// </summary>
        [Fact]
        public void Su2_Json_Matches_Code_Defaults()
        {
            var defaults = new Su2SolverOptions();
            var loaded = JsonConfigLoader.LoadSolverOptions<Su2SolverOptions>("su2");

            Assert.Equal(defaults.Density, loaded.Density);
            Assert.Equal(defaults.DynamicViscosity, loaded.DynamicViscosity);
            Assert.Equal(defaults.TurbulenceModel, loaded.TurbulenceModel);
            Assert.Equal(defaults.SpeedOfSound, loaded.SpeedOfSound);
            Assert.Equal(defaults.ReferenceLength, loaded.ReferenceLength);
            Assert.Equal(defaults.CflNumber, loaded.CflNumber);
            Assert.Equal(defaults.CflAdapt, loaded.CflAdapt);
            Assert.Equal(defaults.CflAdaptFactorDown, loaded.CflAdaptFactorDown);
            Assert.Equal(defaults.CflAdaptFactorUp, loaded.CflAdaptFactorUp);
            Assert.Equal(defaults.CflAdaptMin, loaded.CflAdaptMin);
            Assert.Equal(defaults.CflAdaptMax, loaded.CflAdaptMax);
        }

        [Fact]
        public void Gmsh_Json_Matches_Code_Defaults()
        {
            var defaults = new GmshMesherOptions();
            var loaded = JsonConfigLoader.LoadSolverOptions<GmshMesherOptions>("gmsh");

            Assert.Equal(defaults.TunnelShape, loaded.TunnelShape);
            Assert.Equal(defaults.TunnelSizeX, loaded.TunnelSizeX);
            Assert.Equal(defaults.TunnelSizeY, loaded.TunnelSizeY);
            Assert.Equal(defaults.TunnelSizeZ, loaded.TunnelSizeZ);
            Assert.Equal(defaults.TunnelCenterX, loaded.TunnelCenterX);
            Assert.Equal(defaults.TunnelCenterY, loaded.TunnelCenterY);
            Assert.Equal(defaults.TunnelCenterZ, loaded.TunnelCenterZ);
            Assert.Equal(defaults.TunnelDiameter, loaded.TunnelDiameter);
            Assert.Equal(defaults.TunnelLength, loaded.TunnelLength);
            Assert.Equal(defaults.TunnelSegments, loaded.TunnelSegments);
            Assert.Equal(defaults.BoundaryLayerSizeMin, loaded.BoundaryLayerSizeMin);
            Assert.Equal(defaults.BoundaryLayerSizeMax, loaded.BoundaryLayerSizeMax);
            Assert.Equal(defaults.BoundaryLayerDistMin, loaded.BoundaryLayerDistMin);
            Assert.Equal(defaults.BoundaryLayerDistMax, loaded.BoundaryLayerDistMax);
        }

        /// <summary>
        /// Die SU2-Konfigurationsdatei muss mit den Standard-Optionen physikalisch dasselbe
        /// beschreiben wie die frühere hardcodierte Fassung.
        /// </summary>
        [Fact]
        public void Generated_Su2_Config_Keeps_The_Previous_Values()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path,
                    "/tmp/mesh.su2",
                    refArea: 0.001f,
                    config: SimulationConfig.CreateDefault(),
                    options: new Su2SolverOptions());

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("KIND_TURB_MODEL= SA", lines);
                Assert.Contains("FREESTREAM_DENSITY= 1025", lines);
                Assert.Contains("INC_DENSITY_INIT= 1025", lines);
                Assert.Contains("MU_CONSTANT= 0.001001", lines);
                Assert.Contains("REF_LENGTH= 0.01", lines);
                Assert.Contains("CFL_NUMBER= 5", lines);
                Assert.Contains("CFL_ADAPT= YES", lines);
                Assert.Contains("CFL_ADAPT_PARAM= ( 0.5, 1.2, 1, 50 )", lines);
                Assert.Contains("MESH_FILENAME= mesh.su2", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Options_Reach_The_Generated_Su2_Config()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path,
                    "/tmp/mesh.su2",
                    refArea: 0.001f,
                    config: SimulationConfig.CreateDefault(),
                    options: new Su2SolverOptions
                    {
                        Density = 998.0f,
                        TurbulenceModel = "SST",
                        CflAdapt = false
                    });

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("FREESTREAM_DENSITY= 998", lines);
                Assert.Contains("KIND_TURB_MODEL= SST", lines);
                Assert.Contains("CFL_ADAPT= NO", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
