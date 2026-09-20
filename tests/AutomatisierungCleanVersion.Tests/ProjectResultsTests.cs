using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json.Nodes;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-25: Ergebnisse liegen je Projekt getrennt, und jeder Lauf legt daneben, womit er
    /// gerechnet hat.
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
        // Ergebnisse/<Projekt>/
        // -------------------------------------------------------------------

        [Fact]
        public void The_Working_Directory_Carries_The_Project_Name()
        {
            string directory = SimulationContext.DefaultWorkingDirectory(ProjectNamed("MantaAuv"));

            Assert.Equal("MantaAuv", Path.GetFileName(directory));
            Assert.Equal("Ergebnisse", Path.GetFileName(Path.GetDirectoryName(directory)));
        }

        /// <summary>
        /// Der eigentliche Punkt: zwei Projekte dürfen sich die Simulation_Results.csv nicht
        /// gegenseitig überschreiben.
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
        /// Ohne Projektnamen bleibt es beim gemeinsamen Ordner. Im Lauf kommt das nicht vor —
        /// die Registry erzwingt den Namen —, aber es soll nicht abstürzen.
        /// </summary>
        [Fact]
        public void Without_A_Project_Name_The_Shared_Folder_Is_Used()
        {
            string directory = SimulationContext.DefaultWorkingDirectory(new ProjectConfig());

            Assert.Equal("Ergebnisse", Path.GetFileName(directory));
        }

        [Fact]
        public void The_Csv_Lands_In_The_Project_Folder()
        {
            var project = ProjectNamed("MantaAuv");
            string working = Path.Combine(_root, "Ergebnisse", project.ProjectName);

            var context = new SimulationContext(SimulationConfig.CreateDefault(), project, working);
            context.EnsureDirectories();
            context.History.Add(new ModelRecord { Iteration = 1, Variant = 1, Fitness = 1.5f });
            context.ExportToCsv();

            Assert.True(File.Exists(Path.Combine(working, "Simulation_Results.csv")));
        }

        // -------------------------------------------------------------------
        // effective-config.json
        // -------------------------------------------------------------------

        /// <summary>Baut einen Kontext, wie ihn Program.cs nach dem Laden hätte.</summary>
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
            context.LoadSolverOptions<Su2SolverOptions>("su2");   // trägt sich selbst ein

            return context;
        }

        [Fact]
        public void The_Effective_Config_Records_Values_And_Their_Origin()
        {
            JsonObject content = EffectiveConfigWriter.Build(LoadedContext());

            Assert.Equal("MantaAuv", content["Projekt"]!.GetValue<string>());

            var sections = content["Abschnitte"]!.AsObject();
            Assert.True(sections.ContainsKey("simulation.json"));
            Assert.True(sections.ContainsKey("solvers/su2.json"));

            // Die Werte, mit denen gerechnet wurde ...
            var simulation = sections["simulation.json"]!.AsObject();
            Assert.Equal(3, simulation["Werte"]!["MaxIterations"]!.GetValue<int>());

            // ... und die Datei, aus der jeder einzelne stammt.
            string origin = simulation["HerkunftJeSchluessel"]!["MpiCores"]!.GetValue<string>();
            Assert.EndsWith("simulation.json", origin);

            // Was in keiner Datei stand, fehlt hier — das ist der Code-Standard.
            Assert.Null(simulation["HerkunftJeSchluessel"]!["BouncerJitter"]);
        }

        [Fact]
        public void The_Solver_Options_Are_Recorded_Too()
        {
            JsonObject content = EffectiveConfigWriter.Build(LoadedContext());

            var su2 = content["Abschnitte"]!["solvers/su2.json"]!.AsObject();

            Assert.Equal("SST", su2["Werte"]!["TurbulenceModel"]!.GetValue<string>());
            // Nicht genannt, also Code-Standard — und der muss trotzdem im Protokoll stehen.
            Assert.Equal(1025.0f, su2["Werte"]!["Density"]!.GetValue<float>());
        }

        [Fact]
        public void The_File_Is_Written_Beside_The_Results()
        {
            string working = Path.Combine(_root, "Ergebnisse", "MantaAuv");

            string path = EffectiveConfigWriter.Write(working, LoadedContext());

            Assert.Equal(Path.Combine(working, EffectiveConfigWriter.FileName), path);
            Assert.True(File.Exists(path));

            // Muss sich wieder lesen lassen — sonst nützt das Protokoll später nichts.
            JsonNode? reread = JsonNode.Parse(File.ReadAllText(path));
            Assert.Equal("MantaAuv", reread!["Projekt"]!.GetValue<string>());
        }

        /// <summary>
        /// Hat eine maschinenspezifische Datei mitgeredet, steht das in der Datei — sonst wäre
        /// später nicht erklärbar, warum ein alter Lauf von den hochgeladenen Werten abweicht.
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

            var overrides = EffectiveConfigWriter.Build(context)["MaschinenspezifischeUeberschreibungen"]!.AsArray();

            Assert.Single(overrides);
            Assert.Equal("MaxIterations", overrides[0]!["Schluessel"]!.GetValue<string>());
            Assert.Equal("10", overrides[0]!["Projektwert"]!.GetValue<string>());
            Assert.Equal("2", overrides[0]!["WirksamerWert"]!.GetValue<string>());
        }

        [Fact]
        public void Without_Machine_Files_The_Override_List_Is_Empty()
        {
            var overrides = EffectiveConfigWriter.Build(LoadedContext())["MaschinenspezifischeUeberschreibungen"]!.AsArray();

            Assert.Empty(overrides);
        }
    }
}
