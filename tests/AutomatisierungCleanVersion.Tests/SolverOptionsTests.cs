using System;
using System.IO;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    public class SolverOptionsTests
    {
        /// <summary>
        /// src/projects/MantaAuv/solvers/su2.json muss dieselben Werte enthalten wie die
        /// Code-Standards — sonst rechnet SU2 mit anderen Fluideigenschaften als vor dem
        /// Umzug nach JSON. Einzige bewusste Ausnahme: <c>FreestreamVelocity</c>, siehe
        /// <see cref="MantaAuv_Sets_A_Realistic_Velocity_In_Water"/>.
        /// </summary>
        [Fact]
        public void Su2_Json_Matches_Code_Defaults()
        {
            var defaults = new Su2SolverOptions();
            var loaded = MantaProjectFiles.Load(new Su2SolverOptions(), "solvers/su2.json");

            Assert.Equal(defaults.Density, loaded.Density);
            Assert.Equal(defaults.DynamicViscosity, loaded.DynamicViscosity);
            Assert.Equal(defaults.TurbulenceModel, loaded.TurbulenceModel);
            Assert.Equal(defaults.SpeedOfSound, loaded.SpeedOfSound);
            Assert.Equal(defaults.ReferenceLength, loaded.ReferenceLength);
            Assert.Equal(defaults.ReferenceAreaMetric, loaded.ReferenceAreaMetric);
            // TODO-26: die Zuordnung der Ergebnisgrößen muss in der Datei stehen, damit sie
            // ein neuer Nutzer überhaupt findet — der Code-Standard allein ist unsichtbar.
            Assert.Equal(defaults.ResultMetrics, loaded.ResultMetrics);
            Assert.Equal(defaults.ConvergenceField, loaded.ConvergenceField);
            Assert.Equal(defaults.HistoryOutput, loaded.HistoryOutput);
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
            var loaded = MantaProjectFiles.Load(new GmshMesherOptions(), "solvers/gmsh.json");

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

        // -------------------------------------------------------------------
        // TODO-38: Anströmung direkt in m/s
        // -------------------------------------------------------------------

        /// <summary>
        /// Ohne <c>FreestreamVelocity</c> rechnet jedes Projekt exakt wie vor TODO-38:
        /// Geschwindigkeit = MachNumber × SpeedOfSound.
        /// </summary>
        [Fact]
        public void Without_A_Velocity_The_Old_Mach_Path_Still_Applies()
        {
            var config = SimulationConfig.CreateDefault();
            var options = new Su2SolverOptions();

            float velocity = Su2ConfigGenerator.ResolveVelocity(config, options, out string source);

            Assert.Equal(0.0f, options.FreestreamVelocity);
            Assert.Equal(config.MachNumber * options.SpeedOfSound, velocity);
            Assert.Contains("MachNumber", source);
        }

        /// <summary>
        /// Eine gesetzte Geschwindigkeit gewinnt — und erreicht beide Stellen der cfg, an denen
        /// SU2 sie braucht: Freistrom (Randbedingung) und Startfeld.
        /// </summary>
        [Fact]
        public void A_Set_Velocity_Wins_Over_The_Mach_Number_And_Reaches_The_Config()
        {
            var config = SimulationConfig.CreateDefault();
            config.MachNumber = 0.3f; // darf keine Rolle mehr spielen

            var options = new Su2SolverOptions { FreestreamVelocity = 2.0f };

            Assert.Equal(2.0f, Su2ConfigGenerator.ResolveVelocity(config, options, out string source));
            Assert.Contains("FreestreamVelocity", source);

            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", refArea: 0.001f, config: config, options: options);

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("FREESTREAM_VELOCITY= ( 2, 0.0, 0.0 )", lines);
                Assert.Contains("INC_VELOCITY_INIT= ( 2, 0.0, 0.0 )", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// MantaAuv weicht hier bewusst vom Code-Standard ab: 2 m/s statt der 34,3 m/s, die
        /// Mach 0.1 mit der Schallgeschwindigkeit von Luft ergab (Entscheidung E4,
        /// <c>.plans/anmerkungen_umsetzung.md</c>). Ändert jemand den Wert, soll das bewusst
        /// passieren — die Ergebnisse alter und neuer Läufe sind dann nicht vergleichbar.
        /// </summary>
        [Fact]
        public void MantaAuv_Sets_A_Realistic_Velocity_In_Water()
        {
            var loaded = MantaProjectFiles.Load(new Su2SolverOptions(), "solvers/su2.json");

            Assert.Equal(2.0f, loaded.FreestreamVelocity);
            Assert.Equal(2.0f, Su2ConfigGenerator.ResolveVelocity(MantaProjectFiles.Simulation(), loaded, out _));
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
