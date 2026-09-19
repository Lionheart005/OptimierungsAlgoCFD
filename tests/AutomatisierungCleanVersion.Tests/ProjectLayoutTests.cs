using System;
using System.IO;
using System.Linq;
using Xunit;
using MyPicoGkProject.Core;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-24: alles, was für ein Projekt eingestellt werden muss, liegt in genau einem Ordner.
    /// Diese Tests halten die Struktur-Entscheidungen fest — sie sind billig und schlagen an,
    /// bevor jemand aus Versehen ein zweites Stellrad einbaut.
    /// </summary>
    public class ProjectLayoutTests
    {
        /// <summary>Alle vier JSON-Dateien liegen im Projektordner, keine mehr in config/.</summary>
        [Theory]
        [InlineData("project.json")]
        [InlineData("simulation.json")]
        [InlineData("solvers/su2.json")]
        [InlineData("solvers/gmsh.json")]
        public void Every_Configuration_File_Lives_In_The_Project_Folder(string relativeName)
        {
            string file = Path.Combine(
                MantaProjectFiles.Directory,
                relativeName.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(file), $"src/projects/MantaAuv/{relativeName} fehlt.");
        }

        /// <summary>
        /// Die Geometrie und die Fitness liegen neben ihren Zahlen — das ist der Kern des
        /// Umbaus: ein Ordner, C# und JSON nebeneinander.
        /// </summary>
        [Theory]
        [InlineData("MantaProject.cs")]
        [InlineData("MantaGeometryGenerator.cs")]
        [InlineData("MantaFitnessCalculator.cs")]
        [InlineData("MantaProjectConfig.cs")]
        public void The_Project_Code_Lives_Beside_Its_Numbers(string fileName)
        {
            Assert.True(
                File.Exists(Path.Combine(MantaProjectFiles.Directory, fileName)),
                $"src/projects/MantaAuv/{fileName} fehlt.");
        }

        /// <summary>
        /// Der Projektname steht NUR im Ordnernamen (Entscheidung 1). Ein Feld in der Datei
        /// wäre eine zweite Stelle, die mit dem Ordner auseinanderlaufen kann.
        /// </summary>
        [Fact]
        public void The_Manifest_Does_Not_Carry_The_Project_Name()
        {
            string manifest = File.ReadAllText(
                Path.Combine(MantaProjectFiles.Directory, ProjectPaths.ManifestFileName));

            // In den Kommentaren darf der Name vorkommen — als Schlüssel nicht.
            string withoutComments = string.Join('\n', manifest
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotContain("\"ProjectName\"", withoutComments);
        }

        /// <summary>
        /// Die Programmpfade müssen PATH-relativ sein. Ein absoluter Pfad im Code-Standard band
        /// jeden fremden Nutzer an die Installation eines ganz bestimmten Servers.
        /// </summary>
        [Fact]
        public void The_Program_Paths_Are_Path_Relative()
        {
            var defaults = SimulationConfig.CreateDefault();

            Assert.Equal("mpirun", defaults.MpiRunPath);
            Assert.Equal("SU2_CFD", defaults.Su2Path);
            Assert.Equal("gmsh", defaults.GmshPath);
        }

        /// <summary>Dasselbe für die mitgelieferte Projektdatei — dort stand der Serverpfad.</summary>
        [Fact]
        public void The_Shipped_Simulation_Json_Uses_Path_Relative_Programs()
        {
            var loaded = MantaProjectFiles.Simulation();

            Assert.DoesNotContain("/", loaded.MpiRunPath);
            Assert.DoesNotContain("/", loaded.Su2Path);
            Assert.DoesNotContain("/", loaded.GmshPath);
        }

        /// <summary>
        /// Die globale Basis-Schicht ist ersatzlos entfallen (Entscheidung 3). Läge in config/
        /// wieder eine eingecheckte JSON, hätte sie keinerlei Wirkung — sie wird gar nicht mehr
        /// gelesen — und wäre damit die schlimmste Sorte Konfiguration: eine, die aussieht,
        /// als würde sie gelten.
        /// </summary>
        [Fact]
        public void The_Config_Folder_Holds_No_Checked_In_Json()
        {
            string? configDirectory = JsonConfigLoader.ResolveConfigDirectory(null);
            if (configDirectory == null) return;   // auf einem frischen Rechner gar nicht vorhanden

            var checkedIn = Directory
                .EnumerateFiles(configDirectory, "*.json", SearchOption.AllDirectories)
                .Where(file => !JsonConfigLoader.IsMachineSpecific(file))
                .Select(JsonConfigLoader.Label)
                .ToList();

            Assert.True(checkedIn.Count == 0,
                "config/ enthält eingecheckte JSON-Dateien, die niemand mehr liest: "
                + string.Join(", ", checkedIn)
                + ". Projekteinstellungen gehören nach src/projects/<Name>/.");
        }
    }
}
