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
using FluidForge.Projects.MyInternalProject;
using FluidForge.Projects.MyProject;
using FluidForge.Projects.MyProject2D;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Access to <c>src/projects/_Template3D/</c> (TODO-27).
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
                    $"The folder src/projects/{ProjectPaths.TemplateDirectoryName}/ is missing.");

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

        public static JsonObject ParseJson(string relativeName)
        {
            JsonNode? node = JsonNode.Parse(
                System.IO.File.ReadAllText(File(relativeName)),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            Assert.True(node is JsonObject, $"{relativeName} is not a JSON object.");
            return (JsonObject)node!;
        }

        /// <summary>Top-level keys of a template file, in file order.</summary>
        public static IReadOnlyList<string> TopLevelKeys(string relativeName)
            => ParseJson(relativeName).Select(entry => entry.Key).ToList();

        public static ProjectConfig LoadProjectConfig()
            => JsonConfigLoader.Load(
                ProjectConfigDto.FromProjectConfig(new MyProjectProject().CreateDefaults()),
                File("project.json"))
                .ToProjectConfig();
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
                    $"The folder src/projects/{ProjectPaths.Template2DDirectoryName}/ is missing.");

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

        public static JsonObject ParseJson(string relativeName)
        {
            JsonNode? node = JsonNode.Parse(
                System.IO.File.ReadAllText(File(relativeName)),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            Assert.True(node is JsonObject, $"{relativeName} is not a JSON object.");
            return (JsonObject)node!;
        }

        public static IReadOnlyList<string> TopLevelKeys(string relativeName)
            => ParseJson(relativeName).Select(entry => entry.Key).ToList();

        public static ProjectConfig LoadProjectConfig()
            => JsonConfigLoader.Load(
                ProjectConfigDto.FromProjectConfig(new MyProject2DProject().CreateDefaults()),
                File("project.json"))
                .ToProjectConfig();
    }

    /// <summary>
    /// Contract tests for <c>_Template3D</c> and <c>_Template2D</c>:
    /// slim preset-based configuration, Single Source of Truth in <c>project.json</c>,
    /// valid <c>$schema</c> links, and isolation from the main application registry.
    /// </summary>
    public class TemplateProjectTests
    {
        private static readonly string[] TemplateJsonFiles =
            { "project.json", "simulation.json", "solvers/su2.json", "solvers/gmsh.json" };

        // ------------------------------------------------------------------
        //  Completeness: what a project consists of
        // ------------------------------------------------------------------

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
            Assert.True(File.Exists(TemplateProjectFiles.File(relativeName)));
        }

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

        [Fact]
        public void The_Template_Manifest_Does_Not_Carry_The_Project_Name()
        {
            Assert.DoesNotContain(
                "ProjectName",
                TemplateProjectFiles.TopLevelKeys("project.json"));
        }

        [Fact]
        public void The_Templates_Calibrate_The_Mesh_Automatically_And_Leave_Out_The_Factor()
        {
            foreach (string folder in new[] { "_Template3D", "_Template2D" })
            {
                string? root = ProjectPaths.ResolveProjectsRoot();
                string file = Path.Combine(root!, folder, "simulation.json");
                JsonNode? node = JsonNode.Parse(File.ReadAllText(file),
                    documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
                var obj = (JsonObject)node!;
                var keys = obj.Select(entry => entry.Key).ToList();
                Assert.DoesNotContain(nameof(SimulationConfig.MeshRefinementFactor), keys);

                string? presetName = obj["Preset"]?.GetValue<string>();
                Assert.False(string.IsNullOrWhiteSpace(presetName));
                PresetDefinition? preset = SimulationPresets.Resolve(presetName);
                Assert.NotNull(preset);
                Assert.Equal("Auto", preset!.SimulationOverrides["MeshCalibration"]?["Mode"]?.GetValue<string>());
            }
        }

        [Fact]
        public void The_Template_Comments_Reference_Modular_Docs()
        {
            foreach (string folder in new[] { "_Template3D", "_Template2D" })
            {
                string? root = ProjectPaths.ResolveProjectsRoot();
                string simText = File.ReadAllText(Path.Combine(root!, folder, "simulation.json"));
                Assert.Contains("docs/physics-presets.md", simText);
                Assert.Contains("docs/config-reference.md", simText);
            }
        }

        [Fact]
        public void Both_Templates_Are_Slim_And_Use_Valid_Schema_References()
        {
            foreach (string folder in new[] { "_Template3D", "_Template2D" })
            {
                string? root = ProjectPaths.ResolveProjectsRoot();
                Assert.NotNull(root);
                string templateDir = Path.Combine(root!, folder);

                int totalKeys = 0;
                foreach (string rel in TemplateJsonFiles)
                {
                    string filePath = Path.Combine(templateDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    var obj = JsonNode.Parse(File.ReadAllText(filePath),
                        documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject;
                    Assert.NotNull(obj);

                    string? schemaRel = obj!["$schema"]?.GetValue<string>();
                    Assert.False(string.IsNullOrWhiteSpace(schemaRel));
                    string resolvedSchema = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filePath)!, schemaRel!));
                    Assert.True(File.Exists(resolvedSchema), $"{folder}/{rel} $schema target missing: {resolvedSchema}");

                    totalKeys += obj.Count(entry => !entry.Key.Equals("$schema", StringComparison.OrdinalIgnoreCase));
                }

                Assert.True(totalKeys <= 15, $"{folder} has {totalKeys} top-level keys (expected <= 15 for slim templates).");
            }
        }

        [Fact]
        public void Every_Template_Parameter_Has_A_Start_Value_A_Deviation_And_Bounds()
        {
            foreach (ProjectConfig config in new[]
            {
                TemplateProjectFiles.LoadProjectConfig(),
                Template2DProjectFiles.LoadProjectConfig()
            })
            {
                Assert.NotEmpty(config.BaseParameters);

                foreach (string name in config.BaseParameters.Keys)
                {
                    Assert.True(config.MaxDeviations.ContainsKey(name), $"MaxDeviations missing for '{name}'.");
                    Assert.True(config.ParameterBounds.ContainsKey(name), $"ParameterBounds missing for '{name}'.");

                    (float min, float max) = config.ParameterBounds[name];

                    Assert.True(min < max, $"ParameterBounds for '{name}': Min must be less than Max.");
                    Assert.InRange(config.BaseParameters[name], min, max);
                    Assert.True(config.MaxDeviations[name] > 0f, $"MaxDeviations for '{name}' must be > 0.");
                }
            }
        }

        // ------------------------------------------------------------------
        //  Not a project: the template is not runnable
        // ------------------------------------------------------------------

        [Fact]
        public void The_Template_Folder_Is_Not_Listed_As_A_Project()
        {
            Assert.DoesNotContain(
                ProjectPaths.TemplateDirectoryName,
                ProjectPaths.ListProjectNames(),
                StringComparer.OrdinalIgnoreCase);
        }

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

        [Fact]
        public void The_Placeholder_Name_Is_The_One_The_Script_Replaces()
        {
            Assert.Equal(TemplateProjectFiles.Placeholder, new MyProjectProject().Name);

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

        [Fact]
        public void The_Template_Fitness_Rewards_Less_Drag()
        {
            var calculator = new MyProjectFitnessCalculator(TemplateProjectFiles.LoadProjectConfig());

            ModelRecord low = RecordWithDrag(0.5f);
            ModelRecord high = RecordWithDrag(2.0f);

            calculator.CalculateFitness(low, SimulationConfig.CreateDefault());
            calculator.CalculateFitness(high, SimulationConfig.CreateDefault());

            Assert.Equal(2.0f, low.Fitness, 3);
            Assert.Equal(0.5f, high.Fitness, 3);
            Assert.True(low.Fitness > high.Fitness);
        }

        [Fact]
        public void The_Template_Fitness_Requires_Only_Drag()
        {
            var calculator = new MyProjectFitnessCalculator(TemplateProjectFiles.LoadProjectConfig());
            Assert.Equal(new[] { "Drag" }, calculator.RequiredMetrics);
        }

        [Fact]
        public void A_Failed_Simulation_Gets_The_Failure_Fitness()
        {
            var calculator = new MyProjectFitnessCalculator(TemplateProjectFiles.LoadProjectConfig());

            ModelRecord record = RecordWithDrag(0.5f);
            record.SimulationFailed = true;

            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(0.0001f, record.Fitness, 6);
        }

        [Fact]
        public void A_Record_Without_Drag_Gets_The_Failure_Fitness()
        {
            var calculator = new MyProjectFitnessCalculator(TemplateProjectFiles.LoadProjectConfig());

            var record = new ModelRecord();
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(0.0001f, record.Fitness, 6);
        }

        [Fact]
        public void A_Drag_Near_Zero_Is_Clamped()
        {
            var calculator = new MyProjectFitnessCalculator(TemplateProjectFiles.LoadProjectConfig());

            ModelRecord record = RecordWithDrag(0.0f);
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(1000.0f, record.Fitness, 3);
        }

        [Fact]
        public void A_Negative_Drag_Is_Judged_By_Its_Magnitude()
        {
            var calculator = new MyProjectFitnessCalculator(TemplateProjectFiles.LoadProjectConfig());

            ModelRecord record = RecordWithDrag(-0.5f);
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(2.0f, record.Fitness, 3);
        }

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

        // ------------------------------------------------------------------
        //  Internal3D template tests
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("project.json")]
        [InlineData("simulation.json")]
        [InlineData("solvers/openfoam.json")]
        [InlineData("MyInternalProjectProject.cs")]
        [InlineData("MyInternalProjectGeometryGenerator.cs")]
        [InlineData("MyInternalProjectFitnessCalculator.cs")]
        [InlineData("README.md")]
        public void The_Internal3D_Template_Is_Complete(string relativeName)
        {
            string root = ProjectPaths.ResolveProjectsRoot()!;
            string path = Path.Combine(root, ProjectPaths.TemplateInternal3DDirectoryName, relativeName.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(path), $"Missing {path}");
        }

        [Fact]
        public void The_Internal3D_Template_Is_Slim_Commented_And_Isolated_From_Program()
        {
            string root = ProjectPaths.ResolveProjectsRoot()!;
            string templateDir = Path.Combine(root, ProjectPaths.TemplateInternal3DDirectoryName);
            string[] jsonFiles = { "project.json", "simulation.json", "solvers/openfoam.json" };

            int totalKeys = 0;
            foreach (string rel in jsonFiles)
            {
                string filePath = Path.Combine(templateDir, rel.Replace('/', Path.DirectorySeparatorChar));
                string text = File.ReadAllText(filePath);
                Assert.Contains("//", text);

                var obj = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                }) as JsonObject;
                Assert.NotNull(obj);

                string? schemaRel = obj!["$schema"]?.GetValue<string>();
                Assert.False(string.IsNullOrWhiteSpace(schemaRel));
                string resolvedSchema = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(filePath)!, schemaRel!));
                Assert.True(File.Exists(resolvedSchema), $"_TemplateInternal3D/{rel} $schema target missing: {resolvedSchema}");

                totalKeys += obj.Count(e => !e.Key.Equals("$schema", StringComparison.OrdinalIgnoreCase));
            }

            Assert.True(totalKeys <= 15, $"_TemplateInternal3D has {totalKeys} keys (expected <= 15).");
            Assert.DoesNotContain(ProjectPaths.TemplateInternal3DDirectoryName, ProjectPaths.ListProjectNames(), StringComparer.OrdinalIgnoreCase);

            ProjectConfig tpl3d = TemplateProjectFiles.LoadProjectConfig();
            Assert.Empty(tpl3d.DimensionalParameters);
            Assert.Equal(100.0f, tpl3d.OptimizationTargets["ReferenceLengthMm"]);

            string internalProjectJson = Path.Combine(templateDir, "project.json");
            ProjectConfig tplInternal = JsonConfigLoader.Load(
                ProjectConfigDto.FromProjectConfig(new MyInternalProjectProject().CreateDefaults()),
                internalProjectJson).ToProjectConfig();
            Assert.Empty(tplInternal.DimensionalParameters);
            Assert.Equal(10.0f, tplInternal.OptimizationTargets["InletRadiusMm"]);

            Assembly program = typeof(MantaProject).Assembly;
            Assert.NotEqual(program, typeof(MyInternalProjectProject).Assembly);
            Assert.DoesNotContain(new MyInternalProjectProject().Name, ProjectRegistry.Discover(program).Names, StringComparer.OrdinalIgnoreCase);

            var fitness = new MyInternalProjectFitnessCalculator(new ProjectConfig());
            var rec = new ModelRecord();
            rec.PassiveParameters["PressureDrop"] = 120f;
            fitness.CalculateFitness(rec, SimulationConfig.CreateDefault());
            Assert.Equal(-120f, rec.Fitness, 3);
        }
    }
}
