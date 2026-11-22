using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Projects.MyProject;
using FluidForge.Projects.MyProject2D;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Access to <c>src/projects/_Template3D/</c> (TODO-27).
    ///
    /// <para>
    /// As with <see cref="MantaProjectFiles"/>, existence is strictly checked before every load:
    /// <see cref="JsonConfigLoader"/> silently skips a missing file, and a test loading a
    /// non-existent file would end up comparing code defaults against themselves.
    /// </para>
    /// </summary>
    internal static class TemplateProjectFiles
    {
        /// <summary>The token that <c>scripts/new-project.ps1</c> replaces with the project name.</summary>
        public const string Placeholder = "MyProject";

        public static string Directory
        {
            get
            {
                string? root = ProjectPaths.ResolveProjectsRoot();

                Assert.True(root != null, "Directory src/projects/ was not found.");

                string template = Path.Combine(root!, ProjectPaths.TemplateDirectoryName);

                Assert.True(System.IO.Directory.Exists(template),
                    $"The folder src/projects/{ProjectPaths.TemplateDirectoryName}/ is missing — "
                    + "without it, the following tests check nothing.");

                return template;
            }
        }

        /// <summary>Full path to a template file; fails if missing.</summary>
        public static string File(string relativeName)
        {
            string file = Path.Combine(
                Directory, relativeName.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(System.IO.File.Exists(file),
                $"src/projects/{ProjectPaths.TemplateDirectoryName}/{relativeName} is missing.");

            return file;
        }

        /// <summary>
        /// Loads a template file from <b>only</b> that single file — intentionally not via
        /// <c>LoadForProject</c>: that would pick up any <c>*.local.json</c> present on this
        /// machine and skew the comparison against code defaults.
        /// </summary>
        public static T Load<T>(T defaults, string relativeName) where T : class
            => JsonConfigLoader.Load(defaults, File(relativeName));

        /// <summary>Top-level keys of a template file, in file order.</summary>
        public static IReadOnlyList<string> TopLevelKeys(string relativeName)
        {
            JsonNode? node = JsonNode.Parse(
                System.IO.File.ReadAllText(File(relativeName)),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            Assert.True(node is JsonObject, $"{relativeName} is not a JSON object.");

            return ((JsonObject)node!).Select(entry => entry.Key).ToList();
        }

        /// <summary>
        /// A value formatted for comparison. Uses JSON because <c>Assert.Equal(object, object)</c>
        /// checks reference equality on <c>Dictionary</c> and <c>string[]</c> — which
        /// <c>ResultMetrics</c> and <c>HistoryOutput</c> happen to be.
        /// </summary>
        public static string Describe(object? value)
            => value == null ? "(null)" : JsonSerializer.Serialize(value);

        /// <summary>All public properties of a configuration type, sorted alphabetically.</summary>
        public static PropertyInfo[] PropertiesOf(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .OrderBy(property => property.Name, StringComparer.Ordinal)
                   .ToArray();
    }

    /// <summary>
    /// Access to <c>src/projects/_Template2D/</c> (TODO-47).
    /// </summary>
    internal static class Template2DProjectFiles
    {
        public const string Placeholder = "MyProject2D";

        public static string Directory
        {
            get
            {
                string? root = ProjectPaths.ResolveProjectsRoot();
                Assert.True(root != null, "Directory src/projects/ was not found.");

                string template = Path.Combine(root!, ProjectPaths.Template2DDirectoryName);
                Assert.True(System.IO.Directory.Exists(template),
                    $"The folder src/projects/{ProjectPaths.Template2DDirectoryName}/ is missing — "
                    + "without it, the following tests check nothing.");

                return template;
            }
        }

        public static string File(string relativeName)
        {
            string file = Path.Combine(
                Directory, relativeName.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(System.IO.File.Exists(file),
                $"src/projects/{ProjectPaths.Template2DDirectoryName}/{relativeName} is missing.");

            return file;
        }

        public static T Load<T>(T defaults, string relativeName) where T : class
            => JsonConfigLoader.Load(defaults, File(relativeName));

        public static IReadOnlyList<string> TopLevelKeys(string relativeName)
        {
            JsonNode? node = JsonNode.Parse(
                System.IO.File.ReadAllText(File(relativeName)),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            Assert.True(node is JsonObject, $"{relativeName} is not a JSON object.");

            return ((JsonObject)node!).Select(entry => entry.Key).ToList();
        }
    }

    /// <summary>
    /// TODO-27: the template is the starting point for a new project — and therefore a contract.
    /// These tests lock down the three properties without which it is worthless:
    /// it is <b>complete</b> (every key is present), it is <b>up to date</b> (values match
    /// code defaults), and it is <b>not a project</b> (it never appears as runnable).
    ///
    /// <para>
    /// The fourth property — that it compiles at all — is verified not by a test, but by
    /// building this test project: the csproj pulls in <c>src/projects/_Template3D/**/*.cs</c>.
    /// That is why these tests live here rather than with the project tests.
    /// </para>
    /// </summary>
    public class TemplateProjectTests
    {
        // ------------------------------------------------------------------
        //  Completeness: what a project consists of
        // ------------------------------------------------------------------

        /// <summary>
        /// Four JSONs and three classes — a project is nothing more, and the template must not
        /// show less. The README explains why the folder is not runnable.
        /// </summary>
        [Theory]
        [InlineData("project.json")]
        [InlineData("simulation.json")]
        [InlineData("solvers/su2.json")]
        [InlineData("solvers/gmsh.json")]
        [InlineData("MyProjectProject.cs")]
        [InlineData("MyProjectGeometryGenerator.cs")]
        [InlineData("MyProjectFitnessCalculator.cs")]
        [InlineData("README.md")]
        public void The_Template_Is_Complete(string relativeName)
        {
            // File(...) checks existence and names the file on failure.
            Assert.True(File.Exists(TemplateProjectFiles.File(relativeName)));
        }

        /// <summary>
        /// The JSONs must carry comments. Files written via <c>JsonConfigLoader.Save</c>
        /// have none — and explanations are the entire point of a template.
        /// </summary>
        [Theory]
        [InlineData("project.json")]
        [InlineData("simulation.json")]
        [InlineData("solvers/su2.json")]
        [InlineData("solvers/gmsh.json")]
        public void The_Template_Json_Files_Are_Commented(string relativeName)
        {
            string text = File.ReadAllText(TemplateProjectFiles.File(relativeName));

            Assert.Contains("//", text);
        }

        /// <summary>
        /// The project name lives ONLY in the folder name (Decision 1) — not even in the
        /// template as a field, otherwise a new project would copy the duplicate location.
        /// </summary>
        [Fact]
        public void The_Template_Manifest_Does_Not_Carry_The_Project_Name()
        {
            Assert.DoesNotContain(
                "ProjectName",
                TemplateProjectFiles.TopLevelKeys("project.json"));
        }

        // ------------------------------------------------------------------
        //  Currency: values match code defaults
        // ------------------------------------------------------------------

        /// <summary>
        /// The template's <c>simulation.json</c> is a reference: it must list EVERY
        /// framework key. A new key in <see cref="SimulationConfig"/> missing here would
        /// be invisible to a new user.
        /// </summary>
        /// <summary>
        /// Intentionally omitted from the templates (decision 2026-11-08): the "Bayes" settings.
        /// The template mentions them in a comment referencing the manual.
        /// </summary>
        internal static readonly HashSet<string> OptionalAlgorithmKeys = new(StringComparer.OrdinalIgnoreCase)
        {
            "Bayes"
        };

        /// <summary>
        /// Intentionally omitted from the templates (decision 2026-11-08): in Auto mesh calibration
        /// mode (default), a specified <c>MeshRefinementFactor</c> aborts startup
        /// (<c>MeshCalibration.ValidateConfig</c>). The template explains it in a comment.
        /// </summary>
        internal const string AutoCalibratedKey = nameof(SimulationConfig.MeshRefinementFactor);

        [Fact]
        public void The_Templates_Calibrate_The_Mesh_Automatically_And_Leave_Out_The_Factor()
        {
            foreach (string folder in new[] { "_Template3D", "_Template2D" })
            {
                string? root = ProjectPaths.ResolveProjectsRoot();
                string file = System.IO.Path.Combine(root!, folder, "simulation.json");
                // The mode itself is a tunable setting; only the rule that no
                // MeshRefinementFactor is set is checked (in Auto mode it would abort startup).
                JsonNode? node = JsonNode.Parse(System.IO.File.ReadAllText(file),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                Assert.DoesNotContain(AutoCalibratedKey, ((JsonObject)node!).Select(entry => entry.Key));
            }
        }

        [Fact]
        public void The_Template_Comment_Points_To_The_Optional_Algorithm_Settings()
        {
            foreach (string folder in new[] { "_Template3D", "_Template2D" })
            {
                string? root = ProjectPaths.ResolveProjectsRoot();
                string text = System.IO.File.ReadAllText(System.IO.Path.Combine(root!, folder, "simulation.json"));
                foreach (string key in OptionalAlgorithmKeys)
                    Assert.Contains(key, text);
                Assert.Contains("BEDIENHANDBUCH.md", text);
            }
        }

        [Fact]
        public void The_Template_Simulation_Json_Names_Every_Framework_Key()
        {
            var keys = TemplateProjectFiles.TopLevelKeys("simulation.json");

            var missing = TemplateProjectFiles.PropertiesOf(typeof(SimulationConfig))
                .Select(property => property.Name)
                .Where(name => !name.Equals("MachNumber", StringComparison.OrdinalIgnoreCase))
                .Where(name => !OptionalAlgorithmKeys.Contains(name))
                .Where(name => name != AutoCalibratedKey)
                .Where(name => !keys.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                "Missing in src/projects/_Template3D/simulation.json: "
                + string.Join(", ", missing)
                + ". The template should show every available key.");
        }

        /// <summary>
        /// Every key of the solver files is present in the template so a new user can discover it.
        /// VALUES are tunable settings and are intentionally not checked here.
        /// </summary>
        [Fact]
        public void The_Template_Su2_Json_Names_Every_Key()
        {
            AssertNamesEveryKey<Su2SolverOptions>("solvers/su2.json");
        }

        /// <inheritdoc cref="The_Template_Su2_Json_Names_Every_Key"/>
        [Fact]
        public void The_Template_Gmsh_Json_Names_Every_Key()
        {
            AssertNamesEveryKey<GmshMesherOptions>("solvers/gmsh.json");
        }

        /// <summary>
        /// Every parameter needs all three entries. If a base value were missing, the run would
        /// hit the generator's abort message; if bounds were missing, the algorithm could move
        /// it arbitrarily far.
        /// </summary>
        [Fact]
        public void Every_Template_Parameter_Has_A_Start_Value_A_Deviation_And_Bounds()
        {
            ProjectConfig config = new MyProjectProject().CreateDefaults();

            Assert.NotEmpty(config.BaseParameters);

            foreach (string name in config.BaseParameters.Keys)
            {
                Assert.True(config.MaxDeviations.ContainsKey(name), $"MaxDeviations missing for '{name}'.");
                Assert.True(config.ParameterBounds.ContainsKey(name), $"ParameterBounds missing for '{name}'.");

                (float min, float max) = config.ParameterBounds[name];

                Assert.True(min < max, $"ParameterBounds for '{name}': Min must be less than Max.");
                Assert.InRange(config.BaseParameters[name], min, max);
            }
        }

        // ------------------------------------------------------------------
        //  Not a project: the template is not runnable
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>_Template3D</c> must not count as a project folder — otherwise
        /// <see cref="ProjectRegistry"/> would require a definition for it and abort at startup.
        /// </summary>
        [Fact]
        public void The_Template_Folder_Is_Not_Listed_As_A_Project()
        {
            Assert.DoesNotContain(
                ProjectPaths.TemplateDirectoryName,
                ProjectPaths.ListProjectNames(),
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// And the template's definition must not end up in the program: it is named
        /// <c>MyProject</c>, and there is no folder for that name.
        /// <c>FluidForge.csproj</c> excludes <c>_Template3D</c> for this reason.
        ///
        /// <para>
        /// That the class is still usable here proves the other half of the setup:
        /// the test project compiles the template so that a changed framework interface
        /// fails as a build error rather than months later.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Template_Definition_Is_Compiled_But_Not_Part_Of_The_Program()
        {
            Assembly program = typeof(MantaProject).Assembly;

            Assert.NotEqual(program, typeof(MyProjectProject).Assembly);
            Assert.Equal(GetType().Assembly, typeof(MyProjectProject).Assembly);

            var registry = ProjectRegistry.Discover(program);

            Assert.DoesNotContain(
                new MyProjectProject().Name,
                registry.Names,
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// The placeholder must match what <c>scripts/new-project.ps1</c> replaces. If the two
        /// diverged, a generated project would have a class name that does not match its folder —
        /// and that would only fail at build time.
        /// </summary>
        [Fact]
        public void The_Placeholder_Name_Is_The_One_The_Script_Replaces()
        {
            Assert.Equal(TemplateProjectFiles.Placeholder, new MyProjectProject().Name);

            // _Template3D -> projects -> src -> repo root
            string projectsRoot = Path.GetDirectoryName(TemplateProjectFiles.Directory)!;
            string sourceRoot = Path.GetDirectoryName(projectsRoot)!;
            string repoRoot = Path.GetDirectoryName(sourceRoot)!;

            string script = Path.Combine(repoRoot, "scripts", "new-project.ps1");

            Assert.True(File.Exists(script),
                "scripts/new-project.ps1 is missing — without the script, using the template is manual work.");

            Assert.Contains(
                $"$Placeholder = '{TemplateProjectFiles.Placeholder}'",
                File.ReadAllText(script));
        }

        // ------------------------------------------------------------------
        //  Template fitness
        // ------------------------------------------------------------------

        /// <summary>
        /// Less drag = higher fitness. That is the single statement the template formula
        /// makes — and the direction in which a new user extends it.
        /// </summary>
        [Fact]
        public void The_Template_Fitness_Rewards_Less_Drag()
        {
            var calculator = new MyProjectFitnessCalculator(new MyProjectProject().CreateDefaults());

            ModelRecord low = RecordWithDrag(0.5f);
            ModelRecord high = RecordWithDrag(2.0f);

            calculator.CalculateFitness(low, SimulationConfig.CreateDefault());
            calculator.CalculateFitness(high, SimulationConfig.CreateDefault());

            Assert.Equal(2.0f, low.Fitness, 3);
            Assert.Equal(0.5f, high.Fitness, 3);
            Assert.True(low.Fitness > high.Fitness);
        }

        /// <summary>Only "Drag" — the template evaluates nothing else (TODO-26).</summary>
        [Fact]
        public void The_Template_Fitness_Requires_Only_Drag()
        {
            var calculator = new MyProjectFitnessCalculator(new MyProjectProject().CreateDefaults());

            Assert.Equal(new[] { "Drag" }, calculator.RequiredMetrics);
        }

        /// <summary>
        /// Aborted simulation: the flag is checked FIRST (TODO-2). The
        /// <c>PassiveParameters</c> of a failed model are incomplete.
        /// </summary>
        [Fact]
        public void A_Failed_Simulation_Gets_The_Failure_Fitness()
        {
            var calculator = new MyProjectFitnessCalculator(new MyProjectProject().CreateDefaults());

            ModelRecord record = RecordWithDrag(0.5f);
            record.SimulationFailed = true;

            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(0.0001f, record.Fitness, 6);
        }

        /// <summary>Without drag there is nothing to score — and no division to attempt.</summary>
        [Fact]
        public void A_Record_Without_Drag_Gets_The_Failure_Fitness()
        {
            var calculator = new MyProjectFitnessCalculator(new MyProjectProject().CreateDefaults());

            var record = new ModelRecord();
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(0.0001f, record.Fitness, 6);
        }

        /// <summary>
        /// A drag near zero must not yield fitness in the millions: a single such outlier
        /// would dominate the rest of the run. The 0.001 floor caps fitness at 1000.
        /// </summary>
        [Fact]
        public void A_Drag_Near_Zero_Is_Clamped()
        {
            var calculator = new MyProjectFitnessCalculator(new MyProjectProject().CreateDefaults());

            ModelRecord record = RecordWithDrag(0.0f);
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(1000.0f, record.Fitness, 3);
        }

        /// <summary>
        /// A negative coefficient is evaluated by magnitude — depending on inflow direction,
        /// CD can come out with a flipped sign, and a negative denominator would have yielded
        /// negative fitness: worse than any failed model.
        /// </summary>
        [Fact]
        public void A_Negative_Drag_Is_Judged_By_Its_Magnitude()
        {
            var calculator = new MyProjectFitnessCalculator(new MyProjectProject().CreateDefaults());

            ModelRecord record = RecordWithDrag(-0.5f);
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(2.0f, record.Fitness, 3);
        }

        // ------------------------------------------------------------------
        //  Helpers
        // ------------------------------------------------------------------

        private static ModelRecord RecordWithDrag(float drag)
        {
            var record = new ModelRecord();
            record.PassiveParameters["Drag"] = drag;
            return record;
        }

        // ------------------------------------------------------------------
        //  2D template tests (TODO-47)
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("project.json")]
        [InlineData("simulation.json")]
        [InlineData("solvers/su2.json")]
        [InlineData("solvers/gmsh.json")]
        [InlineData("MyProject2DProject.cs")]
        [InlineData("MyProject2DGeometryGenerator.cs")]
        [InlineData("MyProject2DFitnessCalculator.cs")]
        [InlineData("README.md")]
        public void The_2D_Template_Is_Complete(string relativeName)
        {
            Assert.True(File.Exists(Template2DProjectFiles.File(relativeName)));
        }

        [Theory]
        [InlineData("project.json")]
        [InlineData("simulation.json")]
        [InlineData("solvers/su2.json")]
        [InlineData("solvers/gmsh.json")]
        public void The_2D_Template_Json_Files_Are_Commented(string relativeName)
        {
            string text = File.ReadAllText(Template2DProjectFiles.File(relativeName));
            Assert.Contains("//", text);
        }

        [Fact]
        public void The_2D_Template_Manifest_Does_Not_Carry_The_Project_Name()
        {
            Assert.DoesNotContain(
                "ProjectName",
                Template2DProjectFiles.TopLevelKeys("project.json"));
        }

        [Fact]
        public void The_2D_Template_Simulation_Json_Names_Every_Framework_Key()
        {
            var keys = Template2DProjectFiles.TopLevelKeys("simulation.json");

            var omittedIn2D = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "MachNumber",
                "BaseVoxelResolution",
                "TargetPicoGkSize",
                "VoxelSmoothingIterations",
                "VoxelSmoothingPremeltingSteps",
                "UseRubberBandScaler",
                "UseStlSmoothing"
            };

            var missing = TemplateProjectFiles.PropertiesOf(typeof(SimulationConfig))
                .Select(property => property.Name)
                .Where(name => !omittedIn2D.Contains(name))
                .Where(name => !OptionalAlgorithmKeys.Contains(name))
                .Where(name => name != AutoCalibratedKey)
                .Where(name => !keys.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                "Missing in src/projects/_Template2D/simulation.json: "
                + string.Join(", ", missing));
        }

        /// <summary>Every key of the 2D solver files is present in the template; values are tunable settings and are not checked.</summary>
        [Fact]
        public void The_2D_Template_Su2_Json_Names_Every_Key()
        {
            AssertNamesEvery2DKey<Su2SolverOptions>("solvers/su2.json");
        }

        /// <inheritdoc cref="The_2D_Template_Su2_Json_Names_Every_Key"/>
        [Fact]
        public void The_2D_Template_Gmsh_Json_Names_Every_Key()
        {
            AssertNamesEvery2DKey<Gmsh2DMesherOptions>("solvers/gmsh.json");
        }

        [Fact]
        public void The_2D_Template_Folder_Is_Not_Listed_As_A_Project()
        {
            Assert.DoesNotContain(
                ProjectPaths.Template2DDirectoryName,
                ProjectPaths.ListProjectNames(),
                StringComparer.OrdinalIgnoreCase);
        }

        [Fact]
        public void The_2D_Template_Definition_Is_Compiled_But_Not_Part_Of_The_Program()
        {
            Assembly program = typeof(MantaProject).Assembly;

            Assert.NotEqual(program, typeof(MyProject2DProject).Assembly);
            Assert.Equal(GetType().Assembly, typeof(MyProject2DProject).Assembly);

            var registry = ProjectRegistry.Discover(program);

            Assert.DoesNotContain(
                new MyProject2DProject().Name,
                registry.Names,
                StringComparer.OrdinalIgnoreCase);
        }

        private static void AssertNamesEvery2DKey<T>(string relativeName) where T : class, new()
        {
            var keys = Template2DProjectFiles.TopLevelKeys(relativeName);

            var missing = TemplateProjectFiles.PropertiesOf(typeof(T))
                .Select(property => property.Name)
                .Where(name => !keys.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                $"Missing in src/projects/_Template2D/{relativeName}: " + string.Join(", ", missing)
                + ". The template should show every available key.");
        }

        private static void AssertNamesEveryKey<T>(string relativeName) where T : class, new()
        {
            var keys = TemplateProjectFiles.TopLevelKeys(relativeName);

            var missing = TemplateProjectFiles.PropertiesOf(typeof(T))
                .Select(property => property.Name)
                .Where(name => !keys.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                $"Missing in src/projects/_Template3D/{relativeName}: " + string.Join(", ", missing)
                + ". The template should show every available key.");
        }
    }
}
