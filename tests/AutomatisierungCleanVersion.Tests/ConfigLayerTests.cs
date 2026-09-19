using System;
using System.IO;
using System.Linq;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-22: die vier Schichten des Loaders, die Prüfung unbekannter Schlüssel und der
    /// Warnblock für maschinenspezifische Dateien.
    ///
    /// Gearbeitet wird auf einem temporären Projekt- und Konfigurationsordner — die
    /// mitgelieferten Dateien im Repo bleiben unangetastet.
    /// </summary>
    public class ConfigLayerTests : IDisposable
    {
        private readonly string _root;
        private readonly string _projectDirectory;
        private readonly string _configDirectory;

        public ConfigLayerTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "layertest_" + Guid.NewGuid().ToString("N"));
            _projectDirectory = Path.Combine(_root, "src", "projects", "Testprojekt");
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

        /// <summary>Das Etikett, unter dem der Bericht eine Datei führt: ein Pfad, kein blanker Name.</summary>
        private string ProjectLabel(string relativeName)
            => JsonConfigLoader.Label(Path.Combine(new[] { _projectDirectory }.Concat(relativeName.Split('/')).ToArray()));

        private string ConfigLabel(string relativeName)
            => JsonConfigLoader.Label(Path.Combine(new[] { _configDirectory }.Concat(relativeName.Split('/')).ToArray()));

        // -------------------------------------------------------------------
        // Schicht 2: der Projektordner
        // -------------------------------------------------------------------

        [Fact]
        public void A_Project_File_Overrides_The_Code_Defaults()
        {
            WriteProjectFile("simulation.json", @"{ ""MaxIterations"": 3 }");

            var result = LoadSimulation();

            Assert.Equal(3, result.Value.MaxIterations);
            // Nicht genannte Schlüssel bleiben auf dem Code-Standard.
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
            Assert.Equal(1025.0f, result.Value.Density);   // unverändert aus dem Code
        }

        // -------------------------------------------------------------------
        // Schichtreihenfolge: 3 schlägt 2, 4 schlägt 3
        // -------------------------------------------------------------------

        /// <summary>
        /// Schicht 3 gewinnt über Schicht 2, damit ein Projekt dem Rechner nicht seine
        /// Programmpfade umbiegen kann.
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

        /// <summary>Schicht 4 — dieses Projekt auf diesem Rechner — gewinnt über alles.</summary>
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

            Assert.Equal(5, result.Value.MaxIterations);   // Schicht 2
            Assert.Equal(4, result.Value.MpiCores);        // Schicht 3
            Assert.Equal(2, result.Value.GmshCores);       // Schicht 4
            Assert.Equal(10, result.Value.VariantsPerIteration); // Code-Standard
        }

        // -------------------------------------------------------------------
        // Entscheidung 8: ein unbekannter Schlüssel ist ein Fehler
        // -------------------------------------------------------------------

        [Fact]
        public void An_Unknown_Key_Aborts_With_File_Key_And_Suggestion()
        {
            WriteProjectFile("simulation.json", @"{ ""MachNumer"": 0.2 }");

            var error = Assert.Throws<InvalidOperationException>(() => LoadSimulation());

            Assert.Contains("simulation.json", error.Message);
            Assert.Contains("MachNumer", error.Message);
            Assert.Contains("MachNumber", error.Message);   // der Vorschlag
        }

        /// <summary>
        /// Gibt es keinen ähnlichen Namen, muss wenigstens die Liste der gültigen kommen —
        /// sonst steht der Nutzer vor einem Abbruch ohne Ausweg.
        /// </summary>
        [Fact]
        public void An_Unknown_Key_Without_A_Near_Match_Lists_Valid_Names()
        {
            WriteProjectFile("solvers/su2.json", @"{ ""Lieblingsfarbe"": ""blau"" }");

            var error = Assert.Throws<InvalidOperationException>(() => JsonConfigLoader.LoadForProject(
                new Su2SolverOptions(), "solvers/su2.json", _projectDirectory, _configDirectory));

            Assert.Contains("Lieblingsfarbe", error.Message);
            Assert.Contains("TurbulenceModel", error.Message);
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
        /// Die Kernregel aus der Abstimmung: ein Dictionary hat freie Schlüssel. "Length" unter
        /// BaseParameters ist ein Parametername, kein Property-Name — die Prüfung darf dort
        /// nicht absteigen, sonst zerlegt sie jede bestehende Projektdatei.
        /// </summary>
        [Fact]
        public void Free_Keys_Inside_Dictionaries_Are_Not_Checked()
        {
            WriteProjectFile("project.json", @"{
                ""BaseParameters"": { ""Fluegelspannweite"": 42.0 },
                ""OptimizationTargets"": { ""WasAuchImmer"": 1.0 },
                ""ParameterBounds"": { ""Fluegelspannweite"": { ""Min"": 1.0, ""Max"": 99.0 } },
                ""DimensionalParameters"": [ ""Fluegelspannweite"" ]
            }");

            var result = JsonConfigLoader.LoadForProject(
                new ProjectConfigDto(), "project.json", _projectDirectory, _configDirectory);

            var project = result.Value.ToProjectConfig();

            Assert.Equal(42.0f, project.BaseParameters["Fluegelspannweite"]);
            Assert.Equal(1.0f, project.OptimizationTargets["WasAuchImmer"]);
            Assert.Equal(99.0f, project.ParameterBounds["Fluegelspannweite"].Max);
            Assert.Contains("Fluegelspannweite", project.DimensionalParameters);
        }

        /// <summary>
        /// Auf oberster Ebene wird sehr wohl geprüft — auch in einer Datei, die weiter unten
        /// freie Schlüssel hat.
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

            // Drag kommt aus dem Code-Standard dazu — gewollt, siehe Kommentarkopf der su2.json.
            Assert.Equal("CD", result.Value.ResultMetrics["Drag"]);
            Assert.Equal("CL", result.Value.ResultMetrics["Lift"]);
            // Die Herkunft wird bis auf den einzelnen Schlüssel im verschachtelten Objekt geführt.
            Assert.Equal(ProjectLabel("solvers/su2.json"), result.Report.Origins["ResultMetrics.Lift"]);
            Assert.False(result.Report.Origins.ContainsKey("ResultMetrics.Drag"));
        }

        // -------------------------------------------------------------------
        // Warnblock für *.local.json
        // -------------------------------------------------------------------

        [Fact]
        public void A_Local_File_Is_Reported_With_Exactly_The_Keys_It_Changes()
        {
            WriteProjectFile("simulation.json", @"{ ""MaxIterations"": 10, ""MpiCores"": 12, ""GmshCores"": 12 }");
            WriteConfigFile("simulation.local.json", @"{ ""MaxIterations"": 2, ""MpiCores"": 4, ""GmshCores"": 12 }");

            var result = LoadSimulation();

            // GmshCores ist wertgleich und darf nicht auftauchen.
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
            // Unsichtbar soll sie trotzdem nicht sein.
            Assert.Contains(ConfigLabel("simulation.local.json"), result.Report.NeutralMachineFiles);
        }

        [Fact]
        public void A_Local_File_That_Only_Adds_To_The_Code_Default_Is_Reported_Too()
        {
            // Keine Projektdatei: der Vergleichswert ist der Code-Standard.
            WriteConfigFile("simulation.local.json", @"{ ""MaxIterations"": 2 }");

            var result = LoadSimulation();

            var entry = result.Report.MachineOverrides.Single();
            Assert.Equal("MaxIterations", entry.Key);
            Assert.Equal("10", entry.PreviousValue);
            Assert.Equal("2", entry.NewValue);
        }

        /// <summary>
        /// Beide local-Schichten heißen gleich — die Meldung muss trotzdem sagen, welche der
        /// beiden Dateien zu löschen ist. Deshalb führt der Bericht Pfade, keine blanken Namen.
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
        /// Eine Zeichenkette gehört ohne Anführungszeichen in den Block — er wird von Menschen
        /// gelesen, nicht geparst.
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
        /// Eine Datei unterhalb des Arbeitsverzeichnisses wird relativ benannt — sonst steht im
        /// Warnblock ein absoluter Pfad, den niemand mit dem Repo in Verbindung bringt.
        /// </summary>
        [Fact]
        public void A_File_Below_The_Working_Directory_Is_Labelled_Relative()
        {
            string inside = Path.Combine(Directory.GetCurrentDirectory(), "config", "simulation.local.json");

            Assert.Equal("config/simulation.local.json", JsonConfigLoader.Label(inside));
        }

        /// <summary>
        /// Der Block ist eine Nachricht an einen Menschen — deshalb wird hier ausnahmsweise die
        /// Konsolenausgabe selbst geprüft und nicht nur die Daten dahinter. Bewusst nur auf
        /// Vorhandensein: xUnit lässt andere Testklassen parallel laufen, die in denselben
        /// umgeleiteten Strom schreiben können.
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

            Assert.Contains("[WARNUNG] Eine maschinenspezifische Datei überschreibt dieses Projekt:", printed);
            Assert.Contains(ConfigLabel("simulation.local.json"), printed);
            Assert.Contains("Projekt: 10   ->   local: 2", printed);
            Assert.Contains("Projekt: 12   ->   local: 4", printed);
            // Der Block sagt auch, wohin die Werte gehören.
            Assert.Contains("src/projects/MantaAuv/", printed);
        }

        [Fact]
        public void The_Machine_Variant_Of_A_File_Is_Its_Local_Neighbour()
        {
            Assert.Equal(
                Path.Combine("irgendwo", "simulation.local.json"),
                JsonConfigLoader.MachineVariantOf(Path.Combine("irgendwo", "simulation.json")));

            // Schon eine local-Datei: bleibt, wie sie ist.
            Assert.Equal(
                Path.Combine("irgendwo", "su2.local.json"),
                JsonConfigLoader.MachineVariantOf(Path.Combine("irgendwo", "su2.local.json")));
        }
    }

    /// <summary>
    /// TODO-22: das Auflösen des Projektverzeichnisses. Ersetzt die frühere
    /// „sechs Ebenen hoch, suche config“-Heuristik.
    /// </summary>
    public class ProjectPathsTests : IDisposable
    {
        private readonly string _root;

        public ProjectPathsTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "pathtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_root, "src", "projects", "MantaAuv"));
            Directory.CreateDirectory(Path.Combine(_root, "src", "projects", "Propeller"));
            Directory.CreateDirectory(Path.Combine(_root, "src", "projects", "_Vorlage"));
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
        /// Ein falsch getippter Pfad darf nicht stillschweigend durch die Suche ersetzt werden —
        /// sonst rechnet der Lauf mit einem anderen Projekt als dem gemeinten.
        /// </summary>
        [Fact]
        public void A_Wrong_Explicit_Path_Yields_Null_Instead_Of_Searching()
        {
            Assert.Null(ProjectPaths.ResolveProjectsRoot(Path.Combine(_root, "gibtsnicht")));
        }

        [Fact]
        public void The_Folder_Name_Is_The_Project_Name()
        {
            string? directory = ProjectPaths.ResolveProjectDirectory("MantaAuv", ProjectsRoot);

            Assert.NotNull(directory);
            Assert.Equal("MantaAuv", Path.GetFileName(directory!));
        }

        /// <summary>Linux unterscheidet Groß-/Kleinschreibung, Windows nicht — der Lauf soll auf beiden dasselbe finden.</summary>
        [Fact]
        public void The_Project_Name_Is_Case_Insensitive()
        {
            Assert.NotNull(ProjectPaths.ResolveProjectDirectory("mantaauv", ProjectsRoot));
        }

        [Fact]
        public void An_Unknown_Project_Yields_Null()
        {
            Assert.Null(ProjectPaths.ResolveProjectDirectory("GibtsNicht", ProjectsRoot));
        }

        /// <summary>Die Vorlage ist kein Projekt — sie darf in keiner Auswahlliste auftauchen.</summary>
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
