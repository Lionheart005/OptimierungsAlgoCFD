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
        /// Der <b>Code-Standard</b> muss PATH-relativ bleiben. Er gilt für jedes Projekt, das
        /// nichts einträgt — stünde hier die Installation eines bestimmten Servers, erbte sie
        /// jeder fremde Nutzer, ohne es zu merken.
        ///
        /// <para>
        /// Ein einzelnes Projekt darf davon abweichen: MantaAuv trägt in seiner
        /// simulation.json absolute Pfade, weil der Hochschulserver die Programme nicht über
        /// den PATH findet. Das ist genau die Aufgabenteilung der Schichten — die Ausnahme
        /// steht beim Projekt, nicht im Framework.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Code_Default_Program_Paths_Stay_Path_Relative()
        {
            var defaults = SimulationConfig.CreateDefault();

            Assert.Equal("mpirun", defaults.MpiRunPath);
            Assert.Equal("SU2_CFD", defaults.Su2Path);
            Assert.Equal("gmsh", defaults.GmshPath);
        }

        /// <summary>
        /// Ein Projekt, das die drei Schlüssel nicht nennt, bekommt den PATH-relativen
        /// Code-Standard. Das ist der Weg zurück für jeden anderen Server: die drei Zeilen aus
        /// der simulation.json löschen, fertig.
        /// </summary>
        [Fact]
        public void A_Project_Without_Path_Keys_Falls_Back_To_The_Relative_Default()
        {
            string directory = Path.Combine(Path.GetTempPath(), "pathtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                File.WriteAllText(
                    Path.Combine(directory, "simulation.json"),
                    @"{ ""MaxIterations"": 3 }");

                var loaded = JsonConfigLoader.LoadForProject(
                    SimulationConfig.CreateDefault(), "simulation.json", directory).Value;

                Assert.Equal("mpirun", loaded.MpiRunPath);
                Assert.Equal("SU2_CFD", loaded.Su2Path);
                Assert.Equal("gmsh", loaded.GmshPath);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        /// <summary>
        /// MantaAuv nennt alle drei Pfade ausdrücklich. Stünde nur einer davon in der Datei,
        /// wäre für die anderen beiden stillschweigend die PATH-Suche zuständig — und genau
        /// die schlägt auf dem Hochschulserver fehl.
        /// </summary>
        [Fact]
        public void MantaAuv_Names_All_Three_Program_Paths_Explicitly()
        {
            var loaded = MantaProjectFiles.Simulation();
            var defaults = SimulationConfig.CreateDefault();

            Assert.NotEqual(defaults.MpiRunPath, loaded.MpiRunPath);
            Assert.NotEqual(defaults.Su2Path, loaded.Su2Path);
            Assert.NotEqual(defaults.GmshPath, loaded.GmshPath);

            Assert.StartsWith("/", loaded.MpiRunPath);
            Assert.StartsWith("/", loaded.Su2Path);
            Assert.StartsWith("/", loaded.GmshPath);
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
