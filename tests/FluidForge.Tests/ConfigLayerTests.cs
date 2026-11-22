using System;
using System.IO;
using System.Linq;
using Xunit;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-22: the four layers of the loader, unknown-key validation, and the
    /// warning block for machine-specific files.
    ///
    /// Operates on a temporary project and config directory — the bundled
    /// files in the repo remain untouched.
    ///
    /// The warning-block test captures console output via <c>Console.SetOut</c> — process-wide
    /// state, so never run in parallel with other classes (see <see cref="ProcessWideStateCollection"/>).
    /// </summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class ConfigLayerTests : IDisposable
    {
        private readonly string _root;
        private readonly string _projectDirectory;
        private readonly string _configDirectory;

        public ConfigLayerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "layertest_" + Guid.NewGuid().ToString("N"));
            _projectDirectory = Path.Combine(_root, "src", "projects", "TestProject");
            _configDirectory = Path.Combine(_root, "config");

            Directory.CreateDirectory(Path.Combine(_projectDirectory, "solvers"));
            Directory.CreateDirectory(Path.Combine(_configDirectory, "solvers"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private void WriteProjectFile(string relativeName, string content)
            => File.WriteAllText(Path.Combine(new[] { _projectDirectory }.Concat(relativeName.Split('/')).ToArray()), content);

        private void WriteConfigFile(string relativeName, string content)
            => File.WriteAllText(Path.Combine(new[] { _configDirectory }.Concat(relativeName.Split('/')).ToArray()), content);

        private ConfigLoadResult<SimulationConfig> LoadSimulation()
            => JsonConfigLoader.LoadForProject(
                SimulationConfig.CreateDefault(), "simulation.json", _projectDirectory, _configDirectory);

        /// <summary>The label under which the report lists a file: a path, not a bare filename.</summary>
        private string ProjectLabel(string relativeName)
            => JsonConfigLoader.Label(Path.Combine(new[] { _projectDirectory }.Concat(relativeName.Split('/')).ToArray()));

        private string ConfigLabel(string relativeName)
            => JsonConfigLoader.Label(Path.Combine(new[] { _configDirectory }.Concat(relativeName.Split('/')).ToArray()));

        // -------------------------------------------------------------------
        // Layer 2: the project directory
        // -------------------------------------------------------------------

        [Fact]
        public void A_Project_File_Overrides_The_Code_Defaults()
        {
            WriteProjectFile("simulation.json", @"{ ""MaxIterations"": 3 }");

            var result = LoadSimulation();

            Assert.Equal(3, result.Value.MaxIterations);
            // Unmentioned keys stay at the code default.
            Assert.Equal(10, result.Value.VariantsPerIteration);
            Assert.Equal(ProjectLabel("simulation.json"), result.Report.Origins["MaxIterations"]);
            Assert.False(result.Report.Origins.ContainsKey("VariantsPerIteration"));
        }

        [Fact]
        public void Without_Any_File_The_Code_Defaults_Apply()
        {
            var result = LoadSimulation();

            Assert.Equal(10, result.Value.MaxIterations);
            Assert.Empty(result.Report.Origins);
            Assert.Empty(result.Report.MachineOverrides);
        }

        [Fact]
        public void Solver_Options_Are_Found_In_The_Project_Subfolder()
        {
            WriteProjectFile("solvers/su2.json", @"{ ""TurbulenceModel"": ""SST"" }");

            var result = JsonConfigLoader.LoadForProject(
                new Su2SolverOptions(), "solvers/su2.json", _projectDirectory, _configDirectory);

            Assert.Equal("SST", result.Value.TurbulenceModel);
            Assert.Equal(1025.0f, result.Value.Density);   // unchanged from code
        }

        // -------------------------------------------------------------------
        // Layer order: 3 beats 2, 4 beats 3
        // -------------------------------------------------------------------

        /// <summary>
        /// Layer 3 beats layer 2 so a project cannot override a machine's own
        /// executable paths.
        /// </summary>
        [Fact]
        public void Layer_Three_Beats_Layer_Two()
        {
            WriteProjectFile("simulation.json", @"{ ""MpiCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MpiCores"": 4 }");

            var result = LoadSimulation();

            Assert.Equal(4, result.Value.MpiCores);
            Assert.Equal(ConfigLabel("simulation.local.json"), result.Report.Origins["MpiCores"]);
        }

        /// <summary>Layer 4 — this project on this machine — beats everything.</summary>
        [Fact]
        public void Layer_Four_Beats_Layer_Three()
        {
            WriteProjectFile("simulation.json", @"{ ""MpiCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MpiCores"": 4 }");
            WriteProjectFile("simulation.local.json", @"{ ""MpiCores"": 2 }");

            var result = LoadSimulation();

            Assert.Equal(2, result.Value.MpiCores);
        }

        [Fact]
        public void Every_Layer_Contributes_Only_What_It_Names()
        {
            WriteProjectFile("simulation.json", @"{ ""MaxIterations"": 5, ""MpiCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MpiCores"": 4 }");
            WriteProjectFile("simulation.local.json", @"{ ""GmshCores"": 2 }");

            var result = LoadSimulation();

            Assert.Equal(5, result.Value.MaxIterations);   // Layer 2
            Assert.Equal(4, result.Value.MpiCores);        // Layer 3
            Assert.Equal(2, result.Value.GmshCores);       // Layer 4
            Assert.Equal(10, result.Value.VariantsPerIteration); // Code default
        }

        // -------------------------------------------------------------------
        // Decision 8: an unknown key is an error
        // -------------------------------------------------------------------

        [Fact]
        public void An_Unknown_Key_Aborts_With_File_Key_And_Suggestion()
        {
            WriteProjectFile("simulation.json", @"{ ""MachNumer"": 0.2 }");

            var error = Assert.Throws<InvalidOperationException>(() => LoadSimulation());

            Assert.Contains("simulation.json", error.Message);
            Assert.Contains("MachNumer", error.Message);
            Assert.Contains("MachNumber", error.Message);   // the suggestion
        }

        /// <summary>
        /// If there is no similar name, at least the list of valid names must be shown —
        /// otherwise the user faces an abort with no way out.
        /// </summary>
        [Fact]
        public void An_Unknown_Key_Without_A_Near_Match_Lists_Valid_Names()
        {
            WriteProjectFile("solvers/su2.json", @"{ ""FavoriteColor"": ""blue"" }");

            var error = Assert.Throws<InvalidOperationException>(() => JsonConfigLoader.LoadForProject(
                new Su2SolverOptions(), "solvers/su2.json", _projectDirectory, _configDirectory));

            Assert.Contains("FavoriteColor", error.Message);
            Assert.Contains("TurbulenceModel", error.Message);

            // Complete, not abbreviated: every valid key appears in the message — including
            // the alphabetically last one, which would be dropped first in an abbreviation.
            foreach (var property in typeof(Su2SolverOptions).GetProperties())
                Assert.Contains(property.Name, error.Message);

            Assert.DoesNotContain("u.a.", error.Message);
        }

        [Fact]
        public void An_Unknown_Key_In_A_Local_File_Aborts_Too()
        {
            WriteConfigFile("simulation.local.json", @"{ ""MpiCoresX"": 4 }");

            var error = Assert.Throws<InvalidOperationException>(() => LoadSimulation());

            Assert.Contains("simulation.local.json", error.Message);
            Assert.Contains("MpiCores", error.Message);
        }

        /// <summary>
        /// Core rule from the alignment: a dictionary has free keys. "Length" under
        /// BaseParameters is a parameter name, not a property name — validation must not
        /// descend into it, otherwise it breaks every existing project file.
        /// </summary>
        [Fact]
        public void Free_Keys_Inside_Dictionaries_Are_Not_Checked()
        {
            WriteProjectFile("project.json", @"{
                ""BaseParameters"": { ""Wingspan"": 42.0 },
                ""OptimizationTargets"": { ""Whatever"": 1.0 },
                ""ParameterBounds"": { ""Wingspan"": { ""Min"": 1.0, ""Max"": 99.0 } },
                ""DimensionalParameters"": [ ""Wingspan"" ]
            }");

            var result = JsonConfigLoader.LoadForProject(
                new ProjectConfigDto(), "project.json", _projectDirectory, _configDirectory);

            var project = result.Value.ToProjectConfig();

            Assert.Equal(42.0f, project.BaseParameters["Wingspan"]);
            Assert.Equal(1.0f, project.OptimizationTargets["Whatever"]);
            Assert.Equal(99.0f, project.ParameterBounds["Wingspan"].Max);
            Assert.Contains("Wingspan", project.DimensionalParameters);
        }

        /// <summary>
        /// Top-level keys are still checked — even in a file that has free keys deeper down.
        /// </summary>
        [Fact]
        public void The_Top_Level_Of_A_Project_File_Is_Still_Checked()
        {
            WriteProjectFile("project.json", @"{
                ""BaseParameters"": { ""Length"": 50.0 },
                ""BaseParameter"": { ""Length"": 50.0 }
            }");

            var error = Assert.Throws<InvalidOperationException>(() => JsonConfigLoader.LoadForProject(
                new ProjectConfigDto(), "project.json", _projectDirectory, _configDirectory));

            Assert.Contains("BaseParameter'", error.Message);
            Assert.Contains("BaseParameters", error.Message);
        }

        [Fact]
        public void Nested_Objects_With_Free_Keys_Merge_Additively()
        {
            WriteProjectFile("solvers/su2.json", @"{ ""ResultMetrics"": { ""Lift"": ""CL"" } }");

            var result = JsonConfigLoader.LoadForProject(
                new Su2SolverOptions(), "solvers/su2.json", _projectDirectory, _configDirectory);

            // Drag is added from the code default — intentional, see header comment of su2.json.
            Assert.Equal("CD", result.Value.ResultMetrics["Drag"]);
            Assert.Equal("CL", result.Value.ResultMetrics["Lift"]);
            // Origin is tracked down to each individual key in the nested object.
            Assert.Equal(ProjectLabel("solvers/su2.json"), result.Report.Origins["ResultMetrics.Lift"]);
            Assert.False(result.Report.Origins.ContainsKey("ResultMetrics.Drag"));
        }

        // -------------------------------------------------------------------
        // Warning block for *.local.json
        // -------------------------------------------------------------------

        [Fact]
        public void A_Local_File_Is_Reported_With_Exactly_The_Keys_It_Changes()
        {
            WriteProjectFile("simulation.json", @"{ ""MaxIterations"": 10, ""MpiCores"": 12, ""GmshCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MaxIterations"": 2, ""MpiCores"": 4, ""GmshCores"": 12 }");

            var result = LoadSimulation();

            // GmshCores has the same value and must not appear.
            Assert.Equal(
                new[] { "MaxIterations", "MpiCores" },
                result.Report.MachineOverrides.Select(entry => entry.Key).OrderBy(key => key));

            var iterations = result.Report.MachineOverrides.Single(entry => entry.Key == "MaxIterations");
            Assert.Equal("10", iterations.PreviousValue);
            Assert.Equal("2", iterations.NewValue);
            Assert.Equal(ConfigLabel("simulation.local.json"), iterations.File);
        }

        [Fact]
        public void A_Value_Identical_Local_File_Produces_No_Warning()
        {
            WriteProjectFile("simulation.json", @"{ ""MpiCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MpiCores"": 12 }");

            var result = LoadSimulation();

            Assert.Empty(result.Report.MachineOverrides);
            // Still, it should not be invisible.
            Assert.Contains(ConfigLabel("simulation.local.json"), result.Report.NeutralMachineFiles);
        }

        [Fact]
        public void A_Local_File_That_Only_Adds_To_The_Code_Default_Is_Reported_Too()
        {
            // No project file: comparison value is the code default.
            WriteConfigFile("simulation.local.json", @"{ ""MaxIterations"": 2 }");

            var result = LoadSimulation();

            var entry = result.Report.MachineOverrides.Single();
            Assert.Equal("MaxIterations", entry.Key);
            Assert.Equal("10", entry.PreviousValue);
            Assert.Equal("2", entry.NewValue);
        }

        /// <summary>
        /// Both local layers share the same filename — the message must still tell which of the
        /// two files to delete. Therefore the report lists paths, not bare names.
        /// </summary>
        [Fact]
        public void Both_Local_Layers_Are_Reported_Under_Their_Own_Path()
        {
            WriteProjectFile("simulation.json", @"{ ""MpiCores"": 12, ""GmshCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MpiCores"": 4 }");
            WriteProjectFile("simulation.local.json", @"{ ""GmshCores"": 2 }");

            var result = LoadSimulation();

            Assert.Equal(2, result.Report.MachineOverrides.Count);

            Assert.Equal(
                ConfigLabel("simulation.local.json"),
                result.Report.MachineOverrides.Single(entry => entry.Key == "MpiCores").File);

            Assert.Equal(
                ProjectLabel("simulation.local.json"),
                result.Report.MachineOverrides.Single(entry => entry.Key == "GmshCores").File);
        }

        /// <summary>
        /// A string value belongs in the block without quotes — it is read by humans,
        /// not parsed.
        /// </summary>
        [Fact]
        public void String_Values_Are_Shown_Without_Quotes()
        {
            WriteProjectFile("simulation.json", @"{ ""GmshPath"": ""gmsh"" }");
            WriteConfigFile("simulation.local.json", @"{ ""GmshPath"": ""/opt/gmsh/bin/gmsh"" }");

            var entry = LoadSimulation().Report.MachineOverrides.Single();

            Assert.Equal("gmsh", entry.PreviousValue);
            Assert.Equal("/opt/gmsh/bin/gmsh", entry.NewValue);
        }

        /// <summary>
        /// A file below the working directory is labelled relatively — otherwise the
        /// warning block shows an absolute path that nobody associates with the repo.
        /// </summary>
        [Fact]
        public void A_File_Below_The_Working_Directory_Is_Labelled_Relative()
        {
            string inside = Path.Combine(Directory.GetCurrentDirectory(), "config", "simulation.local.json");

            Assert.Equal("config/simulation.local.json", JsonConfigLoader.Label(inside));
        }

        /// <summary>
        /// The block is a message to a human — therefore console output itself is checked here
        /// as an exception, not just the underlying data. Intentionally checking presence only:
        /// xUnit runs other test classes in parallel that may write to the same redirected stream.
        /// </summary>
        [Fact]
        public void The_Warning_Block_Names_File_Key_And_Both_Values()
        {
            WriteProjectFile("simulation.json", @"{ ""MaxIterations"": 10, ""MpiCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MaxIterations"": 2, ""MpiCores"": 4 }");

            ConfigLoadReport report = LoadSimulation().Report;

            var original = Console.Out;
            var buffer = new StringWriter();
            string printed;

            try
            {
                Console.SetOut(buffer);
                report.PrintMachineOverrideWarning("MantaAuv");
                printed = buffer.ToString();
            }
            finally
            {
                Console.SetOut(original);
            }

            Assert.Contains($"{OutputConventions.WarningTag} A machine-specific file overrides this project:", printed);
            Assert.Contains(ConfigLabel("simulation.local.json"), printed);
            Assert.Contains("Project: 10   ->   local: 2", printed);
            Assert.Contains("Project: 12   ->   local: 4", printed);
            // The block also tells where the values belong.
            Assert.Contains("src/projects/MantaAuv/", printed);
        }

        [Fact]
        public void The_Machine_Variant_Of_A_File_Is_Its_Local_Neighbour()
        {
            Assert.Equal(
                Path.Combine("somewhere", "simulation.local.json"),
                JsonConfigLoader.MachineVariantOf(Path.Combine("somewhere", "simulation.json")));

            // Already a local file: stays as is.
            Assert.Equal(
                Path.Combine("somewhere", "su2.local.json"),
                JsonConfigLoader.MachineVariantOf(Path.Combine("somewhere", "su2.local.json")));
        }
    }

    /// <summary>
    /// TODO-22: resolving the project directory. Replaces the previous
    /// "six levels up, search config" heuristic.
    ///
    /// One test here sets the environment variable <c>SIM_PROJECTS_DIR</c> — therefore this
    /// class never runs in parallel with others (see <see cref="ProcessWideStateCollection"/>).
    /// </summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class ProjectPathsTests : IDisposable
    {
        private readonly string _root;

        public ProjectPathsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "pathtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src", "projects", "MantaAuv"));
            Directory.CreateDirectory(Path.Combine(_root, "src", "projects", "Propeller"));
            Directory.CreateDirectory(Path.Combine(_root, "src", "projects", "_Template3D"));
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private string ProjectsRoot => Path.Combine(_root, "src", "projects");

        [Fact]
        public void An_Explicit_Path_Is_Used_As_Is()
        {
            Assert.Equal(
                Path.GetFullPath(ProjectsRoot),
                ProjectPaths.ResolveProjectsRoot(ProjectsRoot));
        }

        /// <summary>
        /// A mistyped path must not silently fall back to searching —
        /// otherwise the run uses a different project than intended.
        /// </summary>
        [Fact]
        public void A_Wrong_Explicit_Path_Yields_Null_Instead_Of_Searching()
        {
            Assert.Null(ProjectPaths.ResolveProjectsRoot(Path.Combine(_root, "doesnotexist")));
        }

        [Fact]
        public void The_Folder_Name_Is_The_Project_Name()
        {
            string? directory = ProjectPaths.ResolveProjectDirectory("MantaAuv", ProjectsRoot);

            Assert.NotNull(directory);
            Assert.Equal("MantaAuv", Path.GetFileName(directory!));
        }

        /// <summary>Linux is case-sensitive, Windows is not — the run should find the same on both.</summary>
        [Fact]
        public void The_Project_Name_Is_Case_Insensitive()
        {
            Assert.NotNull(ProjectPaths.ResolveProjectDirectory("mantaauv", ProjectsRoot));
        }

        [Fact]
        public void An_Unknown_Project_Yields_Null()
        {
            Assert.Null(ProjectPaths.ResolveProjectDirectory("DoesNotExist", ProjectsRoot));
        }

        /// <summary>The template is not a project — it must not appear in any selection list.</summary>
        [Fact]
        public void The_Template_Is_Not_Listed_As_A_Project()
        {
            var names = ProjectPaths.ListProjectNames(ProjectsRoot);

            Assert.Equal(new[] { "MantaAuv", "Propeller" }, names);
        }

        [Fact]
        public void The_Environment_Variable_Is_Used_When_No_Path_Is_Given()
        {
            string? before = Environment.GetEnvironmentVariable(ProjectPaths.ProjectsDirectoryVariable);
            try
            {
                Environment.SetEnvironmentVariable(ProjectPaths.ProjectsDirectoryVariable, ProjectsRoot);

                Assert.Equal(Path.GetFullPath(ProjectsRoot), ProjectPaths.ResolveProjectsRoot());
            }
            finally
            {
                Environment.SetEnvironmentVariable(ProjectPaths.ProjectsDirectoryVariable, before);
            }
        }
    }
}
