using System;
using System.IO;
using System.Linq;
using Xunit;
using FluidForge.Core;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-24: everything that must be configured for a project lives in a single folder.
    /// These tests lock down the structural decisions — they are cheap and fail before
    /// someone accidentally introduces a second configuration knob.
    /// </summary>
    public class ProjectLayoutTests
    {
        /// <summary>All four JSON files live in the project folder, none in config/ anymore.</summary>
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

            Assert.True(File.Exists(file), $"src/projects/MantaAuv/{relativeName} is missing.");
        }

        /// <summary>
        /// Geometry and fitness live right next to their numbers — that is the core of the
        /// restructuring: one folder, C# and JSON side by side.
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
                $"src/projects/MantaAuv/{fileName} is missing.");
        }

        /// <summary>
        /// The project name is ONLY in the folder name (Decision 1). A field in the file
        /// would be a second place that could drift out of sync with the folder.
        /// </summary>
        [Fact]
        public void The_Manifest_Does_Not_Carry_The_Project_Name()
        {
            string manifest = File.ReadAllText(
                Path.Combine(MantaProjectFiles.Directory, ProjectPaths.ManifestFileName));

            // The name may appear in comments — just not as a key.
            string withoutComments = string.Join('\n', manifest
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

            Assert.DoesNotContain("\"ProjectName\"", withoutComments);
        }

        /// <summary>
        /// The <b>code default</b> must stay PATH-relative. It applies to any project that does
        /// not specify paths — if a specific server's installation lived here, every external
        /// user would inherit it without noticing.
        ///
        /// <para>
        /// A single project may override this: MantaAuv specifies absolute paths in its
        /// simulation.json because the university server does not expose the tools on PATH.
        /// That is the exact division of responsibilities across layers — the exception lives
        /// with the project, not in the framework.
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
        /// A project that omits the three keys receives the PATH-relative code default.
        /// That is the path back for any other server: remove the three lines from
        /// simulation.json, done.
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
        /// The global base layer was removed without replacement (Decision 3). If a checked-in
        /// JSON remained in config/, it would have no effect — it is no longer read — and would
        /// thus be the worst kind of configuration: one that looks as if it applies.
        /// </summary>
        [Fact]
        public void The_Config_Folder_Holds_No_Checked_In_Json()
        {
            string? configDirectory = JsonConfigLoader.ResolveConfigDirectory(null);
            if (configDirectory == null) return;   // not present on a fresh machine

            var checkedIn = Directory
                .EnumerateFiles(configDirectory, "*.json", SearchOption.AllDirectories)
                .Where(file => !JsonConfigLoader.IsMachineSpecific(file))
                .Select(JsonConfigLoader.Label)
                .ToList();

            Assert.True(checkedIn.Count == 0,
                "config/ contains checked-in JSON files that nothing reads anymore: "
                + string.Join(", ", checkedIn)
                + ". Project settings belong in src/projects/<Name>/.");
        }
    }
}
