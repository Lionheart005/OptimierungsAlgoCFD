using System;
using System.IO;
using System.Linq;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-23: der <c>switch</c> in Program.cs ist weg. Ein Projektname wird über die
    /// <see cref="ProjectRegistry"/> aufgelöst, die ihre Definitionen per Reflection findet.
    /// </summary>
    public class ProjectRegistryTests
    {
        /// <summary>Attrappe: ein Projekt, das nichts tut, aber einen Namen hat.</summary>
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
        // Auflösen
        // -------------------------------------------------------------------

        /// <summary>
        /// Der Ersatz für <c>case "MantaAuv":</c> — das Projekt muss ohne eine Zeile in
        /// Program.cs gefunden werden.
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
        /// Ein unbekannter Name muss sagen, was es stattdessen gibt — sonst rät der Nutzer.
        /// </summary>
        [Fact]
        public void An_Unknown_Name_Names_The_Available_Projects()
        {
            var registry = ProjectRegistry.FromDefinitions(new[]
            {
                new FakeProject("MantaAuv"),
                new FakeProject("Propeller")
            });

            var error = Assert.Throws<InvalidOperationException>(() => registry.Resolve("Wärmetauscher"));

            Assert.Contains("Wärmetauscher", error.Message);
            Assert.Contains("MantaAuv", error.Message);
            Assert.Contains("Propeller", error.Message);
        }

        /// <summary>Kein Name ist kein stiller Standard mehr, sondern ein Abbruch mit der Liste.</summary>
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
        // Doppelte Namen
        // -------------------------------------------------------------------

        /// <summary>
        /// Zwei Definitionen mit demselben Namen müssen beim Start auffallen: welche gewinnt,
        /// hinge sonst an der Reihenfolge der Reflection.
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

        /// <summary>Der Ordnername entscheidet, und Dateisysteme unterscheiden sich in der Groß-/Kleinschreibung.</summary>
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
        // Abgleich mit den Ordnern
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
        /// Eine Definition ohne Ordner findet ihre JSON-Dateien nicht und rechnet klaglos mit
        /// den Code-Standards weiter — der Fehler wäre also still.
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

        /// <summary>Ein Ordner ohne Definition wird beim Start nie angeboten — ebenfalls still.</summary>
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

        /// <summary>Die Vorlage ist kein Projekt und darf den Abgleich nicht auslösen.</summary>
        [Fact]
        public void The_Template_Folder_Does_Not_Need_A_Definition()
        {
            using var repository = new FakeRepository("MantaAuv", ProjectPaths.TemplateDirectoryName);

            var registry = ProjectRegistry.FromDefinitions(new[] { new FakeProject("MantaAuv") });

            registry.AssertFoldersMatchDefinitions(repository.ProjectsRoot);
        }

        /// <summary>
        /// Ohne vorhandenes src/projects/ entfällt der Abgleich — bis TODO-24 die Ordner
        /// angelegt hat, gibt es schlicht nichts abzugleichen.
        /// </summary>
        [Fact]
        public void Without_A_Projects_Directory_Nothing_Is_Checked()
        {
            var registry = ProjectRegistry.FromDefinitions(new[] { new FakeProject("Propeller") });

            registry.AssertFoldersMatchDefinitions(Path.Combine(Path.GetTempPath(), "gibtsnicht_" + Guid.NewGuid()));
        }

        // -------------------------------------------------------------------
        // Die Verdrahtung des Manta-Projekts
        // -------------------------------------------------------------------

        /// <summary>
        /// Was vorher der <c>case</c> in Program.cs zusammensteckte: PicoGK, Gmsh, SU2 und die
        /// Manta-Klassen. Der Kernel wird nicht gebaut — PicoGK gibt es auf dem
        /// Entwicklungsrechner nicht.
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
        /// Ein temporäres Repo-Gerüst mit <c>src/projects/&lt;Name&gt;/</c>-Ordnern.
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
