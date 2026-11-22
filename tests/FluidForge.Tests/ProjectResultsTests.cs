using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-25: Results are stored per project, and each run records what it computed with.
    /// </summary>
    public class ProjectResultsTests : IDisposable
    {
        private readonly string _root;

        public ProjectResultsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "resulttest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }

        private static ProjectConfig ProjectNamed(string name) => new ProjectConfig { ProjectName = name };

        // -------------------------------------------------------------------
        // results/<Project>/
        // -------------------------------------------------------------------

        [Fact]
        public void The_Working_Directory_Carries_The_Project_Name()
        {
            string directory = SimulationContext.DefaultWorkingDirectory(ProjectNamed("MantaAuv"));

            Assert.Equal("MantaAuv", Path.GetFileName(directory));
            Assert.Equal(OutputConventions.ResultsRootDirectory, Path.GetFileName(Path.GetDirectoryName(directory)));
        }

        /// <summary>
        /// The main point: two projects must not overwrite each other's results.csv.
        /// </summary>
        [Fact]
        public void Two_Projects_Do_Not_Share_A_Results_Folder()
        {
            var first = new SimulationContext(SimulationConfig.CreateDefault(), ProjectNamed("MantaAuv"));
            var second = new SimulationContext(SimulationConfig.CreateDefault(), ProjectNamed("Propeller"));

            Assert.NotEqual(first.WorkingDirectory, second.WorkingDirectory);
        }

        [Fact]
        public void An_Explicit_Working_Directory_Still_Wins()
        {
            var context = new SimulationContext(
                SimulationConfig.CreateDefault(), ProjectNamed("MantaAuv"), _root);

            Assert.Equal(_root, context.WorkingDirectory);
        }

        /// <summary>
        /// Without a project name, the shared folder is used. This does not happen in a real
        /// run — the registry enforces the name —, but it should not crash.
        /// </summary>
        [Fact]
        public void Without_A_Project_Name_The_Shared_Folder_Is_Used()
        {
            string directory = SimulationContext.DefaultWorkingDirectory(new ProjectConfig());

            Assert.Equal(OutputConventions.ResultsRootDirectory, Path.GetFileName(directory));
        }

        [Fact]
        public void The_Csv_Lands_In_The_Project_Folder()
        {
            var project = ProjectNamed("MantaAuv");
            string working = Path.Combine(_root, OutputConventions.ResultsRootDirectory, project.ProjectName);

            var context = new SimulationContext(SimulationConfig.CreateDefault(), project, working);
            context.EnsureDirectories();
            context.History.Add(new ModelRecord { Iteration = 1, Variant = 1, Fitness = 1.5f });
            context.ExportToCsv();

            Assert.True(File.Exists(Path.Combine(working, OutputConventions.ResultsCsvFileName)));
        }

        // -------------------------------------------------------------------
        // effective-config.json
        // -------------------------------------------------------------------

        /// <summary>Builds a context as Program.cs would have after loading.</summary>
        private ProjectContext LoadedContext()
        {
            string projectDirectory = Path.Combine(_root, "src", "projects", "MantaAuv");
            Directory.CreateDirectory(Path.Combine(projectDirectory, "solvers"));

            File.WriteAllText(
                Path.Combine(projectDirectory, "simulation.json"),
                @"{ ""MaxIterations"": 3, ""MpiCores"": 4 }");
            File.WriteAllText(
                Path.Combine(projectDirectory, "solvers", "su2.json"),
                @"{ ""TurbulenceModel"": ""SST"" }");

            var simulation = JsonConfigLoader.LoadForProject(
                SimulationConfig.CreateDefault(), "simulation.json", projectDirectory);

            var context = new ProjectContext(
                "MantaAuv", projectDirectory, null, simulation.Value, ProjectNamed("MantaAuv"));

            context.Record("simulation.json", simulation);
            context.LoadSolverOptions<Su2SolverOptions>("su2");   // registers itself

            return context;
        }

        [Fact]
        public void The_Effective_Config_Records_Values_And_Their_Origin()
        {
            JsonObject content = EffectiveConfigWriter.Build(LoadedContext());

            Assert.Equal("MantaAuv", content["Project"]!.GetValue<string>());

            var sections = content["Sections"]!.AsObject();
            Assert.True(sections.ContainsKey("simulation.json"));
            Assert.True(sections.ContainsKey("solvers/su2.json"));

            // The values used for the computation ...
            var simulation = sections["simulation.json"]!.AsObject();
            Assert.Equal(3, simulation["Values"]!["MaxIterations"]!.GetValue<int>());

            // ... and the file each value originated from.
            string origin = simulation["OriginByKey"]!["MpiCores"]!.GetValue<string>();
            Assert.EndsWith("simulation.json", origin);

            // Whatever was not in any file is absent here — that is the code default.
            Assert.Null(simulation["OriginByKey"]!["BouncerJitter"]);
        }

        [Fact]
        public void The_Solver_Options_Are_Recorded_Too()
        {
            JsonObject content = EffectiveConfigWriter.Build(LoadedContext());

            var su2 = content["Sections"]!["solvers/su2.json"]!.AsObject();

            Assert.Equal("SST", su2["Values"]!["TurbulenceModel"]!.GetValue<string>());
            // Not specified, so code default — and that must still appear in the log.
            Assert.Equal(1025.0f, su2["Values"]!["Density"]!.GetValue<float>());
        }

        [Fact]
        public void The_File_Is_Written_Beside_The_Results()
        {
            string working = Path.Combine(_root, OutputConventions.ResultsRootDirectory, "MantaAuv");

            string path = EffectiveConfigWriter.Write(working, LoadedContext());

            Assert.Equal(Path.Combine(working, EffectiveConfigWriter.FileName), path);
            Assert.True(File.Exists(path));

            // Must be readable again — otherwise the log is useless later.
            JsonNode? reread = JsonNode.Parse(File.ReadAllText(path));
            Assert.Equal("MantaAuv", reread!["Project"]!.GetValue<string>());
        }

        /// <summary>
        /// If a machine-specific file contributed, it is recorded in the file — otherwise it
        /// would be impossible to explain later why an old run differs from the uploaded values.
        /// </summary>
        [Fact]
        public void Machine_Overrides_Are_Part_Of_The_Record()
        {
            string projectDirectory = Path.Combine(_root, "src", "projects", "MantaAuv");
            Directory.CreateDirectory(projectDirectory);

            File.WriteAllText(Path.Combine(projectDirectory, "simulation.json"), @"{ ""MaxIterations"": 10 }");
            File.WriteAllText(Path.Combine(projectDirectory, "simulation.local.json"), @"{ ""MaxIterations"": 2 }");

            var simulation = JsonConfigLoader.LoadForProject(
                SimulationConfig.CreateDefault(), "simulation.json", projectDirectory);

            var context = new ProjectContext(
                "MantaAuv", projectDirectory, null, simulation.Value, ProjectNamed("MantaAuv"));
            context.Record("simulation.json", simulation);

            var overrides = EffectiveConfigWriter.Build(context)["MachineSpecificOverrides"]!.AsArray();

            Assert.Single(overrides);
            Assert.Equal("MaxIterations", overrides[0]!["Key"]!.GetValue<string>());
            Assert.Equal("10", overrides[0]!["ProjectValue"]!.GetValue<string>());
            Assert.Equal("2", overrides[0]!["EffectiveValue"]!.GetValue<string>());
        }

        [Fact]
        public void Without_Machine_Files_The_Override_List_Is_Empty()
        {
            var overrides = EffectiveConfigWriter.Build(LoadedContext())["MachineSpecificOverrides"]!.AsArray();

            Assert.Empty(overrides);
        }
    }
}
