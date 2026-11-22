using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Checks <b>every</b> project that actually resides under <c>src/projects/</c> — not a
    /// specific one.
    ///
    /// <para>
    /// <b>Why generic:</b> the other project tests name <c>MantaAuv</c> explicitly and are
    /// therefore useless for any second project. A test that iterates over existing folders
    /// covers a newly created project from the moment it exists — and disappears with it
    /// without turning red. That is exactly what this framework is designed for: projects
    /// come and go, the scaffolding stays.
    /// </para>
    /// </summary>
    public class ProjectsOnDiskTests
    {
        /// <summary>Project names from the filesystem, as xUnit test data.</summary>
        public static IEnumerable<object[]> ProjectNames =>
            ProjectPaths.ListProjectNames().Select(name => new object[] { name });

        /// <summary>
        /// The same check that <c>Program.cs</c> runs at startup — here against the real
        /// repository. A new folder without a definition (or with a mismatched <c>Name</c>) is
        /// caught in the test rather than on the first server run.
        /// </summary>
        [Fact]
        public void Every_Folder_Has_A_Definition_And_Every_Definition_A_Folder()
        {
            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);

            // Throws with a message naming both directions and the missing name.
            registry.AssertFoldersMatchDefinitions();
        }

        /// <summary>For every folder, the registry must be able to resolve its name.</summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Project_Can_Be_Resolved(string name)
        {
            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);

            Assert.Equal(name, registry.Resolve(name).Name, ignoreCase: true);
        }

        /// <summary>
        /// Both required files must exist and load cleanly. An unknown key or syntax error in
        /// JSONC throws here — which is the actual purpose: a typo in a project file should not
        /// only show up on the server.
        /// </summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Project_Loads_Its_Configuration(string name)
        {
            string directory = DirectoryOf(name);

            Assert.True(File.Exists(Path.Combine(directory, "simulation.json")),
                $"src/projects/{name}/simulation.json is missing.");
            Assert.True(File.Exists(Path.Combine(directory, ProjectPaths.ManifestFileName)),
                $"src/projects/{name}/{ProjectPaths.ManifestFileName} is missing.");

            var simulation = JsonConfigLoader.Load(
                SimulationConfig.CreateDefault(), Path.Combine(directory, "simulation.json"));

            // Plausibility, not a numerical check: a run with 0 variants computes nothing.
            Assert.True(simulation.MaxIterations >= 1, $"{name}: MaxIterations must be at least 1.");
            Assert.True(simulation.VariantsPerIteration >= 1, $"{name}: VariantsPerIteration must be at least 1.");
            Assert.False(string.IsNullOrWhiteSpace(simulation.MpiRunPath), $"{name}: MpiRunPath is empty.");
            Assert.False(string.IsNullOrWhiteSpace(simulation.Su2Path), $"{name}: Su2Path is empty.");
            Assert.False(string.IsNullOrWhiteSpace(simulation.GmshPath), $"{name}: GmshPath is empty.");
        }

        /// <summary>
        /// Solver files go through <see cref="ProjectContext.LoadSolverOptions{T}"/> — the same
        /// path as at runtime. A file consisting solely of comments is explicitly allowed: it
        /// means "nothing deviates here", and code defaults must apply.
        /// </summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Project_Loads_Its_Solver_Options(string name)
        {
            var context = new ProjectContext(
                name, DirectoryOf(name), null, SimulationConfig.CreateDefault(), new ProjectConfig());

            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);
            IProjectDefinition definition = registry.Resolve(name);

            var su2 = context.LoadSolverOptions<Su2SolverOptions>("su2");


            // Without a reference area every coefficient would be off by orders of magnitude (TODO-12),
            // and without at least one result metric nothing would come back from the solver (TODO-26).
            Assert.False(string.IsNullOrWhiteSpace(su2.ReferenceAreaMetric), $"{name}: ReferenceAreaMetric is empty.");
            Assert.NotEmpty(su2.ResultMetrics);
            Assert.NotEmpty(su2.HistoryOutput);


            if (definition is Cfd2DProjectDefinition)
            {
                var gmsh2d = context.LoadSolverOptions<Gmsh2DMesherOptions>("gmsh");
                Assert.True(gmsh2d.FarfieldRadiusFactor > 0f, $"{name}: FarfieldRadiusFactor must be > 0.");
            }
            else
            {
                var gmsh = context.LoadSolverOptions<GmshMesherOptions>("gmsh");
                Assert.True(gmsh.TunnelSizeX > 0f && gmsh.TunnelSizeY > 0f && gmsh.TunnelSizeZ > 0f,
                    $"{name}: Wind tunnel has no extent.");
            }


        }

        /// <summary>
        /// Every parameter needs a base value, deviation, and bounds — and the base value must lie
        /// within the bounds. If deviation is missing, the algorithm never mutates the parameter;
        /// if bounds are missing, it can move it arbitrarily far. Neither fails loudly during a run,
        /// it would just produce poor results.
        /// </summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Parameter_Is_Fully_Specified(string name)
        {
            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);
            IProjectDefinition definition = registry.Resolve(name);

            ProjectConfig project = JsonConfigLoader.Load(
                    ProjectConfigDto.FromProjectConfig(definition.CreateDefaults()),
                    Path.Combine(DirectoryOf(name), ProjectPaths.ManifestFileName))
                .ToProjectConfig();

            Assert.NotEmpty(project.BaseParameters);

            foreach (KeyValuePair<string, float> parameter in project.BaseParameters)
            {
                Assert.True(project.MaxDeviations.ContainsKey(parameter.Key),
                    $"{name}: MaxDeviations missing for '{parameter.Key}'.");
                Assert.True(project.ParameterBounds.ContainsKey(parameter.Key),
                    $"{name}: ParameterBounds missing for '{parameter.Key}'.");

                (float min, float max) = project.ParameterBounds[parameter.Key];

                Assert.True(min <= max, $"{name}: '{parameter.Key}' has Min > Max.");
                Assert.InRange(parameter.Value, min, max);
            }

            // A dimensional parameter that does not exist is a typo: the RubberBandScaler
            // would silently ignore it.
            foreach (string dimensional in project.DimensionalParameters)
            {
                Assert.True(project.BaseParameters.ContainsKey(dimensional),
                    $"{name}: DimensionalParameters lists '{dimensional}', which does not exist in "
                    + "BaseParameters.");
            }
        }

        private static string DirectoryOf(string name)
        {
            string? directory = ProjectPaths.ResolveProjectDirectory(name);

            Assert.True(directory != null, $"Folder src/projects/{name}/ was not found.");

            return directory!;
        }
    }
}
