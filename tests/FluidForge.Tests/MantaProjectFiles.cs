using System.IO;
using Xunit;
using FluidForge.Core;

namespace FluidForge.Tests
{
    /// <summary>
    /// Access to the bundled JSON files of <c>src/projects/MantaAuv/</c> (TODO-24).
    ///
    /// <para>
    /// <b>Why this helper exists:</b> <c>JsonConfigLoader</c> silently skips a missing
    /// file — that is right during a run, but the worst kind of bug in a test.
    /// A test that loads a non-existent file compares code defaults against themselves
    /// and passes without having tested anything. Therefore existence is strictly checked here first.
    /// </para>
    /// </summary>
    internal static class MantaProjectFiles
    {
        public const string ProjectName = "MantaAuv";

        /// <summary>
        /// The folder <c>src/projects/MantaAuv/</c>. Searched upwards from the
        /// working directory because tests run from <c>bin/Debug/net9.0</c>.
        /// </summary>
        public static string Directory
        {
            get
            {
                string? directory = ProjectPaths.ResolveProjectDirectory(ProjectName);

                Assert.True(directory != null,
                    $"The folder src/projects/{ProjectName}/ was not found — "
                    + "without it, the following tests verify nothing.");

                return directory!;
            }
        }

        /// <summary>
        /// Loads a project file through the loader layers and ensures that
        /// it actually exists.
        /// </summary>
        /// <param name="relativeName">e.g. <c>simulation.json</c> or <c>solvers/su2.json</c>.</param>
        public static T Load<T>(T defaults, string relativeName) where T : class
        {
            string directory = Directory;
            string file = Path.Combine(directory, relativeName.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(file),
                $"src/projects/{ProjectName}/{relativeName} is missing — without this file, "
                + "this test compares code defaults against themselves.");

            return JsonConfigLoader.LoadForProject(defaults, relativeName, directory).Value;
        }

        /// <summary>The <c>simulation.json</c> of the project.</summary>
        public static SimulationConfig Simulation()
            => Load(SimulationConfig.CreateDefault(), "simulation.json");

        /// <summary>The <c>project.json</c> of the project, including project name from the folder.</summary>
        public static ProjectConfig Project(ProjectConfig defaults)
        {
            ProjectConfig loaded = Load(ProjectConfigDto.FromProjectConfig(defaults), ProjectPaths.ManifestFileName)
                .ToProjectConfig();

            // The folder name is the project name (Decision 1) — just like in Program.cs.
            loaded.ProjectName = ProjectName;

            return loaded;
        }
    }
}
