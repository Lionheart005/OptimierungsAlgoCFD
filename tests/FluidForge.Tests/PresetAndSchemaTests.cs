using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluidForge.Cli;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;
using FluidForge.Solvers.OpenFoam;
using Xunit;

namespace FluidForge.Tests
{
    [Collection(ProcessWideStateCollection.Name)]
    public class PresetAndSchemaTests : IDisposable
    {
        private readonly string _tempRoot;

        public PresetAndSchemaTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "preset_schema_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
            {
                try { Directory.Delete(_tempRoot, recursive: true); } catch { }
            }
        }

        [Fact]
        public void Defaults_And_Internal3D_Presets_Disable_Scaler_And_StlSmoothing()
        {
            SimulationConfig defaults = SimulationConfig.CreateDefault();
            Assert.False(defaults.UseRubberBandScaler);
            Assert.False(defaults.UseStlSmoothing);

            foreach (string accuracy in new[] { "FastConcept", "StandardRans", "VerificationFine" })
            {
                PresetDefinition preset = SimulationPresets.Resolve($"Internal3D:{accuracy}")!;
                Assert.False(preset.SimulationOverrides["UseRubberBandScaler"]?.GetValue<bool>());
                Assert.False(preset.SimulationOverrides["UseStlSmoothing"]?.GetValue<bool>());
            }
        }

        [Fact]
        public void Preset_Matrix_Is_Complete_And_Validates_Against_Real_Types()
        {
            Assert.Equal(9, SimulationPresets.All.Count);

            var expectedDomains = new[] { "External2D", "External3D", "Internal3D" };
            var expectedAccuracies = new[] { "FastConcept", "StandardRans", "VerificationFine" };

            foreach (string domain in expectedDomains)
            {
                foreach (string accuracy in expectedAccuracies)
                {
                    string name = $"{domain}:{accuracy}";
                    Assert.True(SimulationPresets.TryGet(name, out PresetDefinition? preset), $"Missing preset {name}");
                    Assert.NotNull(preset);
                    Assert.Equal(domain, preset!.Domain);
                    Assert.Equal(accuracy, preset.Accuracy);
                    Assert.False(preset.IsPlaceholder);

                    // Validate SimulationOverrides against SimulationConfig
                    ConfigSchema.Validate(typeof(SimulationConfig), preset.SimulationOverrides, $"preset:{preset.Name}");

                    // Presets in Auto calibration mode must NOT set MeshRefinementFactor
                    string? calMode = preset.SimulationOverrides["MeshCalibration"]?["Mode"]?.GetValue<string>();
                    if (string.Equals(calMode, "Auto", StringComparison.OrdinalIgnoreCase))
                    {
                        Assert.Null(preset.SimulationOverrides["MeshRefinementFactor"]);
                    }

                    // Validate solver overrides
                    if (domain == "Internal3D")
                    {
                        JsonObject? openFoam = preset.GetSolverOverrides("solvers/openfoam.json");
                        Assert.NotNull(openFoam);
                        ConfigSchema.Validate(typeof(OpenFoamOptions), openFoam!, $"preset:{preset.Name}/openfoam");
                    }
                    else
                    {
                        JsonObject? su2 = preset.GetSolverOverrides("solvers/su2.json");
                        JsonObject? gmsh = preset.GetSolverOverrides("solvers/gmsh.json");
                        Assert.NotNull(su2);
                        Assert.NotNull(gmsh);

                        ConfigSchema.Validate(typeof(Su2SolverOptions), su2!, $"preset:{preset.Name}/su2");
                        Type gmshType = domain == "External2D" ? typeof(Gmsh2DMesherOptions) : typeof(GmshMesherOptions);
                        ConfigSchema.Validate(gmshType, gmsh!, $"preset:{preset.Name}/gmsh");

                        if (domain == "External3D")
                        {
                            JsonObject? openFoam = preset.GetSolverOverrides("solvers/openfoam.json");
                            Assert.NotNull(openFoam);
                            ConfigSchema.Validate(typeof(OpenFoamOptions), openFoam!, $"preset:{preset.Name}/openfoam");
                        }
                    }
                }
            }
        }

        [Fact]
        public void Preset_Overrides_Return_Fresh_DeepClones()
        {
            PresetDefinition preset = SimulationPresets.Resolve("External3D:StandardRans")!;
            JsonObject sim1 = preset.SimulationOverrides;
            sim1["MaxIterations"] = 999;
            JsonObject sim2 = preset.SimulationOverrides;
            Assert.Equal(10, sim2["MaxIterations"]!.GetValue<int>());

            JsonObject su2_1 = preset.GetSolverOverrides("SOLVERS\\SU2.JSON")!;
            su2_1["TargetYPlus"] = 99.0f;
            JsonObject su2_2 = preset.GetSolverOverrides("solvers/su2.json")!;
            Assert.Equal(1.5f, su2_2["TargetYPlus"]!.GetValue<float>());
        }

        [Theory]
        [InlineData("External2D", "External2D:StandardRans")]
        [InlineData("External3D", "External3D:StandardRans")]
        [InlineData("Internal3D", "Internal3D:StandardRans")]
        [InlineData("external3d:fastconcept", "External3D:FastConcept")]
        public void Shorthands_And_CaseInsensitive_Names_Resolve_Correctly(string input, string expectedCanonical)
        {
            PresetDefinition? resolved = SimulationPresets.Resolve(input);
            Assert.NotNull(resolved);
            Assert.Equal(expectedCanonical, resolved!.Name);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("Custom")]
        [InlineData("custom")]
        [InlineData(null)]
        public void Empty_Or_Custom_Preset_Resolves_To_Null(string? input)
        {
            Assert.Null(SimulationPresets.Resolve(input));
            Assert.False(SimulationPresets.TryGet(input, out _));
        }

        [Fact]
        public void Unknown_Preset_Throws_Config_Error_Listing_Valid_Presets()
        {
            var ex = Assert.Throws<InvalidOperationException>(() => SimulationPresets.Resolve("External3D:SuperFine"));
            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
            Assert.Contains("External3D:StandardRans", ex.Message);
            Assert.Contains("External2D:FastConcept", ex.Message);
        }

        [Fact]
        public void Layer_Priority_Respects_Default_Preset_Project_GlobalLocal_ProjectLocal()
        {
            string projectDir = Path.Combine(_tempRoot, "src", "projects", "Demo");
            string configDir = Path.Combine(_tempRoot, "config");
            Directory.CreateDirectory(projectDir);
            Directory.CreateDirectory(configDir);

            string projSim = Path.Combine(projectDir, "simulation.json");
            string globalLocal = Path.Combine(configDir, "simulation.local.json");
            string projLocal = Path.Combine(projectDir, "simulation.local.json");

            // Preset sets: MaxIterations=10, VariantsPerIteration=8, Su2MaxIterations=3000, CauchyTolerance="5e-5"
            File.WriteAllText(projSim, @"{
                ""$schema"": ""../../../schemas/simulation.schema.json"",
                ""Preset"": ""External3D:StandardRans"",
                ""VariantsPerIteration"": 4,
                ""MpiCores"": 8
            }");
            File.WriteAllText(globalLocal, @"{ ""MpiCores"": 6, ""GmshCores"": 6 }");
            File.WriteAllText(projLocal, @"{ ""GmshCores"": 2 }");

            string? peeked = JsonConfigLoader.PeekProjectStringKey("Preset", "simulation.json", projectDir, configDir);
            PresetDefinition preset = SimulationPresets.Resolve(peeked)!;

            var loaded = JsonConfigLoader.LoadForProjectWithPreset(
                SimulationConfig.CreateDefault(),
                "simulation.json",
                projectDir,
                preset.Name,
                preset.SimulationOverrides,
                configDir);

            // Code default: BouncerJitter = 0.01 (not in Origins)
            Assert.Equal(0.01f, loaded.Value.BouncerJitter);
            Assert.False(loaded.Report.Origins.ContainsKey("BouncerJitter"));

            // $schema must not fail validation and must not appear in Origins
            Assert.False(loaded.Report.Origins.ContainsKey("$schema"));

            // Preset (Layer 1.5)
            Assert.Equal("5e-5", loaded.Value.CauchyTolerance);
            Assert.Equal($"preset:{preset.Name}", loaded.Report.Origins["CauchyTolerance"]);

            // Project (Layer 2) beats Preset
            Assert.Equal(4, loaded.Value.VariantsPerIteration);
            Assert.Equal(JsonConfigLoader.Label(projSim), loaded.Report.Origins["VariantsPerIteration"]);

            // config/*.local.json (Layer 3) beats Project
            Assert.Equal(6, loaded.Value.MpiCores);
            Assert.Equal(JsonConfigLoader.Label(globalLocal), loaded.Report.Origins["MpiCores"]);

            // project/*.local.json (Layer 4) beats Layer 3
            Assert.Equal(2, loaded.Value.GmshCores);
            Assert.Equal(JsonConfigLoader.Label(projLocal), loaded.Report.Origins["GmshCores"]);
        }

        [Fact]
        public void ProjectContext_LoadSolverOptions_Applies_Preset_Even_Without_ProjectDirectory()
        {
            var sim = new SimulationConfig { Preset = "External2D:VerificationFine" };
            var context = new ProjectContext("Standalone", null, null, sim, new ProjectConfig());

            var su2 = context.LoadSolverOptions<Su2SolverOptions>("su2");
            var gmsh2d = context.LoadSolverOptions<Gmsh2DMesherOptions>("gmsh");

            Assert.Equal(2, su2.Dimensions);
            Assert.Equal(0.8f, su2.TargetYPlus);
            Assert.Equal(0.8f, gmsh2d.TargetYPlus);
            Assert.Equal(500.0f, gmsh2d.FarfieldRadiusFactor);
        }

        [Fact]
        public void FastConcept_Overridden_To_Auto_Calibration_Does_Not_Abort_On_Preset_MeshRefinementFactor()
        {
            string projectDir = Path.Combine(_tempRoot, "FastPlusAuto");
            Directory.CreateDirectory(projectDir);
            File.WriteAllText(Path.Combine(projectDir, "simulation.json"), @"{
                ""Preset"": ""External3D:FastConcept"",
                ""MeshCalibration"": { ""Mode"": ""Auto"" }
            }");

            PresetDefinition preset = SimulationPresets.Resolve("External3D:FastConcept")!;
            var result = JsonConfigLoader.LoadForProjectWithPreset(
                SimulationConfig.CreateDefault(), "simulation.json", projectDir, preset.Name, preset.SimulationOverrides);

            Assert.Equal("Auto", result.Value.MeshCalibration.Mode);
            Assert.Equal($"preset:{preset.Name}", result.Report.Origins["MeshRefinementFactor"]);

            // Must NOT throw
            MeshCalibration.ValidateConfig(result.Value, result.Report, "FastPlusAuto");

            // Now add explicit MeshRefinementFactor in project JSON -> MUST throw
            File.WriteAllText(Path.Combine(projectDir, "simulation.json"), @"{
                ""Preset"": ""External3D:FastConcept"",
                ""MeshCalibration"": { ""Mode"": ""Auto"" },
                ""MeshRefinementFactor"": 1.2
            }");

            var invalid = JsonConfigLoader.LoadForProjectWithPreset(
                SimulationConfig.CreateDefault(), "simulation.json", projectDir, preset.Name, preset.SimulationOverrides);

            var ex = Assert.Throws<InvalidOperationException>(
                () => MeshCalibration.ValidateConfig(invalid.Value, invalid.Report, "FastPlusAuto"));
            Assert.Contains("MeshRefinementFactor", ex.Message);
        }

        [Fact]
        public void Calibration_Fingerprint_Includes_Preset()
        {
            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var ctx1 = new ProjectContext("A", null, null, new SimulationConfig { Preset = "External3D:FastConcept" }, project);
            var ctx2 = new ProjectContext("A", null, null, new SimulationConfig { Preset = "External3D:StandardRans" }, project);

            Assert.NotEqual(MeshCalibration.ComputeFingerprint(ctx1), MeshCalibration.ComputeFingerprint(ctx2));
        }

        [Fact]
        public void ProjectConfigDto_AutoDerives_Missing_MaxDeviations_From_Bounds()
        {
            var dto = new ProjectConfigDto
            {
                BaseParameters = new Dictionary<string, float> { ["Length"] = 50f, ["Fixed"] = 10f },
                ParameterBounds = new Dictionary<string, ParameterBoundsDto>
                {
                    ["Length"] = new() { Min = 20f, Max = 100f },
                    ["Fixed"] = new() { Min = 10f, Max = 10f }
                }
            };

            ProjectConfig config = dto.ToProjectConfig();

            Assert.Equal(8.0f, config.MaxDeviations["Length"], 4);
            Assert.Equal(1e-4f, config.MaxDeviations["Fixed"], 6);
        }

        [Fact]
        public void DumpPreset_Lists_And_Writes_Files_With_Out_Directory()
        {
            // 1. List stdout
            var originalOut = Console.Out;
            var listBuffer = new StringWriter();
            try
            {
                Console.SetOut(listBuffer);
                InfoCommands.DumpPreset(null, null);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            string listOutput = listBuffer.ToString();
            foreach (PresetDefinition preset in SimulationPresets.All)
                Assert.Contains(preset.Name, listOutput);

            // 2. Dump 2D preset to directory
            string out2d = Path.Combine(_tempRoot, "dump2d");
            InfoCommands.DumpPreset("External2D:StandardRans", out2d);
            Assert.True(File.Exists(Path.Combine(out2d, "simulation.json")));
            Assert.True(File.Exists(Path.Combine(out2d, "solvers", "su2.json")));
            Assert.True(File.Exists(Path.Combine(out2d, "solvers", "gmsh.json")));

            // 3. Dump Internal3D preset -> simulation.json + solvers/openfoam.json
            string outInternal = Path.Combine(_tempRoot, "dumpInternal");
            InfoCommands.DumpPreset("Internal3D:StandardRans", outInternal);
            Assert.True(File.Exists(Path.Combine(outInternal, "simulation.json")));
            Assert.True(File.Exists(Path.Combine(outInternal, "solvers", "openfoam.json")));
            Assert.False(File.Exists(Path.Combine(outInternal, "solvers", "su2.json")));

            // 4. Dump External3D preset -> includes solvers/openfoam.json alongside su2/gmsh
            string out3d = Path.Combine(_tempRoot, "dump3d");
            InfoCommands.DumpPreset("External3D:StandardRans", out3d);
            Assert.True(File.Exists(Path.Combine(out3d, "solvers", "su2.json")));
            Assert.True(File.Exists(Path.Combine(out3d, "solvers", "gmsh.json")));
            Assert.True(File.Exists(Path.Combine(out3d, "solvers", "openfoam.json")));
        }

        [Fact]
        public void PrintConfig_Preserves_KeyValue_Contract_And_Includes_Provenance_And_Solver_Options()
        {
            string? nacaDir = ProjectPaths.ResolveProjectDirectory("Naca2D");
            Assert.NotNull(nacaDir);

            IProjectDefinition definition = ProjectRegistry.Discover(typeof(Program).Assembly).Resolve("Naca2D");
            ProjectContext context = OptimizeCommand.LoadConfiguration(definition, null);
            definition.CreateStages(context);

            var originalOut = Console.Out;
            var buffer = new StringWriter();
            try
            {
                Console.SetOut(buffer);
                InfoCommands.PrintConfiguration(context);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            string output = buffer.ToString();
            Assert.Contains("MpiRunPath=", output);
            Assert.Contains("Preset=", output);
            Assert.Contains("# --- Provenance & Solver Options ---", output);
            Assert.Contains("# [simulation.json]", output);
            Assert.Contains("# [solvers/su2.json]", output);
            Assert.Contains("# [solvers/gmsh.json]", output);
        }

        [Fact]
        public void Committed_Schemas_Are_Valid_And_Cover_All_Target_Properties()
        {
            string? repoRoot = ProjectPaths.ResolveRepositoryRoot();
            Assert.NotNull(repoRoot);
            string schemasDir = Path.Combine(repoRoot!, "schemas");
            Assert.True(Directory.Exists(schemasDir), "schemas/ directory is missing.");

            var expected = new (string File, Type Type)[]
            {
                ("simulation.schema.json", typeof(SimulationConfig)),
                ("project.schema.json", typeof(ProjectConfigDto)),
                ("su2.schema.json", typeof(Su2SolverOptions)),
                ("gmsh.schema.json", typeof(GmshMesherOptions)),
                ("gmsh2d.schema.json", typeof(Gmsh2DMesherOptions)),
                ("openfoam.schema.json", typeof(OpenFoamOptions))
            };

            foreach (var (file, type) in expected)
            {
                string path = Path.Combine(schemasDir, file);
                Assert.True(File.Exists(path), $"Schema file missing: schemas/{file}");

                JsonObject? schema = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
                Assert.NotNull(schema);
                Assert.Equal(JsonSchemaGenerator.SchemaDraft202012, schema!["$schema"]?.GetValue<string>());

                JsonObject? properties = schema["properties"] as JsonObject;
                Assert.NotNull(properties);
                Assert.True(properties!.ContainsKey("$schema"), $"schemas/{file} should allow $schema.");

                foreach (PropertyInfo prop in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanWrite))
                {
                    Assert.True(properties.ContainsKey(prop.Name), $"schemas/{file} is missing property {prop.Name}.");
                }
            }

            // Check SimulationConfig.Preset enum coverage
            var simSchema = JsonNode.Parse(File.ReadAllText(Path.Combine(schemasDir, "simulation.schema.json")))!.AsObject();
            var presetEnum = simSchema["properties"]!["Preset"]!["enum"]!.AsArray()
                .Select(n => n!.GetValue<string>())
                .ToHashSet(StringComparer.Ordinal);

            Assert.Contains("", presetEnum);
            Assert.Contains("Custom", presetEnum);
            foreach (PresetDefinition p in SimulationPresets.All)
                Assert.Contains(p.Name, presetEnum);

            // Also verify --export-schemas writes cleanly to a temp directory
            string tempSchemas = Path.Combine(_tempRoot, "exported-schemas");
            InfoCommands.ExportSchemas(tempSchemas);
            foreach (var (file, _) in expected)
                Assert.True(File.Exists(Path.Combine(tempSchemas, file)));
        }
    }
}
