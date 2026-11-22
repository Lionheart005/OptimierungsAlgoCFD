using System;
using System.IO;
using System.Linq;
using Xunit;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-23: the <c>switch</c> in Program.cs is gone. A project name is resolved via
    /// <see cref="ProjectRegistry"/>, which discovers its definitions via reflection.
    /// </summary>
    public class ProjectRegistryTests
    {
        /// <summary>Stub: a project that does nothing, but has a name.</summary>
        private sealed class FakeProject : IProjectDefinition
        {
            public FakeProject(string name) => Name = name;

            public string Name { get; }

            public ProjectConfig CreateDefaults() => new ProjectConfig { ProjectName = Name };
            public IGeometryKernel CreateKernel() => new DirectGeometryKernel();
            public IGeometryGenerator CreateGeometry(ProjectContext context) => throw new NotSupportedException();
            public IFitnessCalculator CreateFitness(ProjectContext context) => throw new NotSupportedException();
            public SolverStage[] CreateStages(ProjectContext context) => Array.Empty<SolverStage>();
        }

        // -------------------------------------------------------------------
        // Resolution
        // -------------------------------------------------------------------

        /// <summary>
        /// Replacement for <c>case "MantaAuv":</c> — the project must be found without a single
        /// line in Program.cs.
        /// </summary>
        [Fact]
        public void MantaAuv_Is_Discovered_In_The_Assembly()
        {
            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);

            IProjectDefinition definition = registry.Resolve("MantaAuv");

            Assert.IsType<MantaProject>(definition);
            Assert.Equal("MantaAuv", definition.Name);
        }

        [Fact]
        public void The_Name_Is_Resolved_Regardless_Of_Case_And_Spaces()
        {
            var registry = ProjectRegistry.FromDefinitions(new[] { new FakeProject("Propeller") });

            Assert.Equal("Propeller", registry.Resolve("  propeller ").Name);
        }

        /// <summary>
        /// An unknown name must tell the user what exists instead — otherwise they have to guess.
        /// </summary>
        [Fact]
        public void An_Unknown_Name_Names_The_Available_Projects()
        {
            var registry = ProjectRegistry.FromDefinitions(new[]
            {
                new FakeProject("MantaAuv"),
                new FakeProject("Propeller")
            });

            var error = Assert.Throws<InvalidOperationException>(() => registry.Resolve("HeatExchanger"));

            Assert.Contains("HeatExchanger", error.Message);
            Assert.Contains("MantaAuv", error.Message);
            Assert.Contains("Propeller", error.Message);
        }

        /// <summary>No name is no longer a silent default, but an abort listing the projects.</summary>
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void A_Missing_Name_Aborts_With_The_Available_Projects(string? name)
        {
            var registry = ProjectRegistry.FromDefinitions(new[] { new FakeProject("MantaAuv") });

            var error = Assert.Throws<InvalidOperationException>(() => registry.Resolve(name));

            Assert.Contains("MantaAuv", error.Message);
        }

        // -------------------------------------------------------------------
        // Duplicate names
        // -------------------------------------------------------------------

        /// <summary>
        /// Two definitions with the same name must be caught at startup: otherwise which one
        /// wins would depend on reflection order.
        /// </summary>
        [Fact]
        public void Duplicate_Names_Are_Rejected_At_Startup()
        {
            var error = Assert.Throws<InvalidOperationException>(() => ProjectRegistry.FromDefinitions(new[]
            {
                new FakeProject("Propeller"),
                new FakeProject("Propeller")
            }));

            Assert.Contains("Propeller", error.Message);
            Assert.Contains(nameof(FakeProject), error.Message);
        }

        /// <summary>The folder name decides, and filesystems differ in case sensitivity.</summary>
        [Fact]
        public void Duplicate_Names_Are_Detected_Ignoring_Case()
        {
            Assert.Throws<InvalidOperationException>(() => ProjectRegistry.FromDefinitions(new[]
            {
                new FakeProject("Propeller"),
                new FakeProject("propeller")
            }));
        }

        [Fact]
        public void Definitions_Are_Listed_Alphabetically()
        {
            var registry = ProjectRegistry.FromDefinitions(new[]
            {
                new FakeProject("Propeller"),
                new FakeProject("Auv"),
                new FakeProject("Waermetauscher")
            });

            Assert.Equal(new[] { "Auv", "Propeller", "Waermetauscher" }, registry.Names);
        }

        // -------------------------------------------------------------------
        // Matching against folders
        // -------------------------------------------------------------------

        [Fact]
        public void Matching_Folders_And_Definitions_Pass()
        {
            using var repository = new FakeRepository("MantaAuv", "Propeller");

            var registry = ProjectRegistry.FromDefinitions(new[]
            {
                new FakeProject("MantaAuv"),
                new FakeProject("Propeller")
            });

            registry.AssertFoldersMatchDefinitions(repository.ProjectsRoot);
        }

        /// <summary>
        /// A definition without a folder cannot find its JSON files and would silently continue
        /// with code defaults — the bug would go unnoticed.
        /// </summary>
        [Fact]
        public void A_Definition_Without_A_Folder_Is_Rejected()
        {
            using var repository = new FakeRepository("MantaAuv");

            var registry = ProjectRegistry.FromDefinitions(new[]
            {
                new FakeProject("MantaAuv"),
                new FakeProject("Propeller")
            });

            var error = Assert.Throws<InvalidOperationException>(
                () => registry.AssertFoldersMatchDefinitions(repository.ProjectsRoot));

            Assert.Contains("Propeller", error.Message);
        }

        /// <summary>A folder without a definition is never offered at startup — also silent.</summary>
        [Fact]
        public void A_Folder_Without_A_Definition_Is_Rejected()
        {
            using var repository = new FakeRepository("MantaAuv", "Propeller");

            var registry = ProjectRegistry.FromDefinitions(new[] { new FakeProject("MantaAuv") });

            var error = Assert.Throws<InvalidOperationException>(
                () => registry.AssertFoldersMatchDefinitions(repository.ProjectsRoot));

            Assert.Contains("Propeller", error.Message);
            Assert.Contains("IProjectDefinition", error.Message);
        }

        /// <summary>The template is not a project and must not trigger the mismatch check.</summary>
        [Fact]
        public void The_Template_Folder_Does_Not_Need_A_Definition()
        {
            using var repository = new FakeRepository("MantaAuv", ProjectPaths.TemplateDirectoryName);

            var registry = ProjectRegistry.FromDefinitions(new[] { new FakeProject("MantaAuv") });

            registry.AssertFoldersMatchDefinitions(repository.ProjectsRoot);
        }

        /// <summary>
        /// Without an existing src/projects/ directory, the check is skipped — until TODO-24
        /// creates the folders, there is simply nothing to match against.
        /// </summary>
        [Fact]
        public void Without_A_Projects_Directory_Nothing_Is_Checked()
        {
            var registry = ProjectRegistry.FromDefinitions(new[] { new FakeProject("Propeller") });

            registry.AssertFoldersMatchDefinitions(Path.Combine(Path.GetTempPath(), "nonexistent_" + Guid.NewGuid()));
        }

        // -------------------------------------------------------------------
        // Manta project wiring
        // -------------------------------------------------------------------

        /// <summary>
        /// What the <c>case</c> in Program.cs used to wire together: PicoGK, Gmsh, SU2, and the
        /// Manta classes. The kernel is not constructed — PicoGK is not installed on the
        /// development machine.
        /// </summary>
        [Fact]
        public void MantaProject_Wires_Geometry_Fitness_And_The_Cfd_Chain()
        {
            var definition = new MantaProject();
            var context = new ProjectContext(
                "MantaAuv", null, null, SimulationConfig.CreateDefault(), definition.CreateDefaults());

            Assert.IsType<MantaGeometryGenerator>(definition.CreateGeometry(context));
            Assert.IsType<MantaFitnessCalculator>(definition.CreateFitness(context));

            SolverStage[] stages = definition.CreateStages(context);

            Assert.Single(stages);
            Assert.IsType<GmshCfdMesher>(stages[0].Mesher);
            Assert.IsType<Su2Solver>(stages[0].Solver);
        }

        [Fact]
        public void MantaProject_Defaults_Match_The_Previous_Code_Configuration()
        {
            var fromDefinition = new MantaProject().CreateDefaults();
            var fromConfigClass = MantaProjectConfig.Create();

            Assert.Equal(fromConfigClass.BaseParameters, fromDefinition.BaseParameters);
            Assert.Equal(fromConfigClass.OptimizationTargets, fromDefinition.OptimizationTargets);
            Assert.Equal(fromConfigClass.DimensionalParameters, fromDefinition.DimensionalParameters);
        }

        /// <summary>
        /// A temporary repo scaffold with <c>src/projects/&lt;Name&gt;/</c> folders.
        /// </summary>
        private sealed class FakeRepository : IDisposable
        {
            private readonly string _root;

            public FakeRepository(params string[] projectFolders)
            {
                _root = Path.Combine(Path.GetTempPath(), "registrytest_" + Guid.NewGuid().ToString("N"));
                ProjectsRoot = Path.Combine(_root, ProjectPaths.SourceDirectoryName, ProjectPaths.ProjectsDirectoryName);

                foreach (string folder in projectFolders)
                    Directory.CreateDirectory(Path.Combine(ProjectsRoot, folder));
            }

            public string ProjectsRoot { get; }

            public void Dispose()
            {
                if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
            }
        }
    }
}
