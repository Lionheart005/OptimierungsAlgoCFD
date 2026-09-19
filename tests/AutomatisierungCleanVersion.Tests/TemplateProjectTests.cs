using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;

using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Projects.MeinProjekt;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Zugriff auf <c>src/projects/_Vorlage/</c> (TODO-27).
    ///
    /// <para>
    /// Wie bei <see cref="MantaProjectFiles"/> wird vor jedem Laden hart auf Existenz geprüft:
    /// der <see cref="JsonConfigLoader"/> überspringt eine fehlende Datei kommentarlos, und ein
    /// Test, der eine nicht gefundene Datei lädt, vergleicht die Code-Standards mit sich selbst.
    /// </para>
    /// </summary>
    internal static class TemplateProjectFiles
    {
        /// <summary>Das Wort, das <c>scripts/new-project.ps1</c> durch den Projektnamen ersetzt.</summary>
        public const string Placeholder = "MeinProjekt";

        public static string Directory
        {
            get
            {
                string? root = ProjectPaths.ResolveProjectsRoot();

                Assert.True(root != null, "Das Verzeichnis src/projects/ wurde nicht gefunden.");

                string template = Path.Combine(root!, ProjectPaths.TemplateDirectoryName);

                Assert.True(System.IO.Directory.Exists(template),
                    $"Der Ordner src/projects/{ProjectPaths.TemplateDirectoryName}/ fehlt — "
                    + "ohne ihn prüfen die folgenden Tests nichts.");

                return template;
            }
        }

        /// <summary>Voller Pfad einer Vorlagendatei; bricht ab, wenn sie fehlt.</summary>
        public static string File(string relativeName)
        {
            string file = Path.Combine(
                Directory, relativeName.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(System.IO.File.Exists(file),
                $"src/projects/{ProjectPaths.TemplateDirectoryName}/{relativeName} fehlt.");

            return file;
        }

        /// <summary>
        /// Lädt eine Vorlagendatei über <b>nur</b> diese eine Datei — bewusst nicht über
        /// <c>LoadForProject</c>: das würde auf diesem Rechner vorhandene <c>*.local.json</c>
        /// mit einbeziehen und den Vergleich gegen die Code-Standards verfälschen.
        /// </summary>
        public static T Load<T>(T defaults, string relativeName) where T : class
            => JsonConfigLoader.Load(defaults, File(relativeName));

        /// <summary>Die Schlüssel der obersten Ebene einer Vorlagendatei, in Dateireihenfolge.</summary>
        public static IReadOnlyList<string> TopLevelKeys(string relativeName)
        {
            JsonNode? node = JsonNode.Parse(
                System.IO.File.ReadAllText(File(relativeName)),
                documentOptions: new JsonDocumentOptions
                {
                    CommentHandling = JsonCommentHandling.Skip,
                    AllowTrailingCommas = true
                });

            Assert.True(node is JsonObject, $"{relativeName} ist kein JSON-Objekt.");

            return ((JsonObject)node!).Select(entry => entry.Key).ToList();
        }

        /// <summary>
        /// Ein Wert, wie ihn ein Vergleich braucht. Über JSON, weil <c>Assert.Equal(object, object)</c>
        /// bei <c>Dictionary</c> und <c>string[]</c> auf Referenzgleichheit prüfen würde — und
        /// <c>ResultMetrics</c> sowie <c>HistoryOutput</c> sind genau das.
        /// </summary>
        public static string Describe(object? value)
            => value == null ? "(null)" : JsonSerializer.Serialize(value);

        /// <summary>Alle öffentlichen Properties eines Konfigurationstyps, alphabetisch.</summary>
        public static PropertyInfo[] PropertiesOf(Type type)
            => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                   .OrderBy(property => property.Name, StringComparer.Ordinal)
                   .ToArray();
    }

    /// <summary>
    /// TODO-27: die Vorlage ist der Startpunkt für ein neues Projekt — und damit eine Zusage.
    /// Diese Tests halten die drei Eigenschaften fest, ohne die sie nichts wert ist:
    /// sie ist <b>vollständig</b> (jeder Schlüssel steht drin), sie ist <b>aktuell</b> (die Werte
    /// sind die Code-Standards) und sie ist <b>kein Projekt</b> (sie taucht nirgends als startbar
    /// auf).
    ///
    /// <para>
    /// Die vierte Eigenschaft — dass sie überhaupt compiliert — prüft kein Test, sondern der
    /// Build dieses Testprojekts: die csproj zieht <c>src/projects/_Vorlage/**/*.cs</c> mit
    /// herein. Deshalb stehen diese Tests hier und nicht bei den Projekt-Tests.
    /// </para>
    /// </summary>
    public class TemplateProjectTests
    {
        // ------------------------------------------------------------------
        //  Vollständigkeit: woraus ein Projekt besteht
        // ------------------------------------------------------------------

        /// <summary>
        /// Vier JSONs und drei Klassen — mehr ist ein Projekt nicht, und weniger darf die
        /// Vorlage nicht zeigen. Die README erklärt, warum der Ordner nicht startbar ist.
        /// </summary>
        [Theory]
        [InlineData("project.json")]
        [InlineData("simulation.json")]
        [InlineData("solvers/su2.json")]
        [InlineData("solvers/gmsh.json")]
        [InlineData("MeinProjektProject.cs")]
        [InlineData("MeinProjektGeometryGenerator.cs")]
        [InlineData("MeinProjektFitnessCalculator.cs")]
        [InlineData("README.md")]
        public void The_Template_Is_Complete(string relativeName)
        {
            // File(...) prüft die Existenz und nennt die Datei im Fehlerfall.
            Assert.True(File.Exists(TemplateProjectFiles.File(relativeName)));
        }

        /// <summary>
        /// Die JSONs müssen Kommentare tragen. Über <c>JsonConfigLoader.Save</c> erzeugte Dateien
        /// haben keine — und die Erklärungen sind bei einer Vorlage der ganze Inhalt.
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
        /// Der Projektname steht NUR im Ordnernamen (Entscheidung 1) — auch in der Vorlage nicht
        /// als Feld, sonst kopiert ein neues Projekt sich die zweite Stelle mit.
        /// </summary>
        [Fact]
        public void The_Template_Manifest_Does_Not_Carry_The_Project_Name()
        {
            Assert.DoesNotContain(
                "ProjectName",
                TemplateProjectFiles.TopLevelKeys("project.json"));
        }

        // ------------------------------------------------------------------
        //  Aktualität: die Werte sind die Code-Standards
        // ------------------------------------------------------------------

        /// <summary>
        /// Die <c>simulation.json</c> der Vorlage ist ein Nachschlagewerk: sie muss JEDEN
        /// Framework-Schlüssel nennen. Ein neuer Schlüssel in <see cref="SimulationConfig"/>, der
        /// hier fehlt, wäre für einen neuen Nutzer unsichtbar.
        /// </summary>
        [Fact]
        public void The_Template_Simulation_Json_Names_Every_Framework_Key()
        {
            var keys = TemplateProjectFiles.TopLevelKeys("simulation.json");

            var missing = TemplateProjectFiles.PropertiesOf(typeof(SimulationConfig))
                .Select(property => property.Name)
                .Where(name => !keys.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                "In src/projects/_Vorlage/simulation.json fehlt/fehlen: "
                + string.Join(", ", missing)
                + ". Die Vorlage soll jeden Schlüssel zeigen, den es gibt.");
        }

        /// <summary>
        /// Und die Werte müssen die Code-Standards sein — sonst erbt jedes neue Projekt eine
        /// Zahl, die irgendwann einmal aktuell war.
        ///
        /// <para>
        /// <b>Zwei bewusste Ausnahmen:</b> <c>MaxIterations</c> und <c>VariantsPerIteration</c>
        /// stehen auf 1 und 2 statt auf 10 und 10. Der erste Lauf eines neuen Projekts soll ein
        /// Rauchtest sein („läuft die Kette durch?“) und keine Nachtschicht mit 100 Varianten.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Template_Simulation_Json_Holds_The_Code_Defaults_Except_The_Smoke_Test_Size()
        {
            var defaults = SimulationConfig.CreateDefault();
            var loaded = TemplateProjectFiles.Load(SimulationConfig.CreateDefault(), "simulation.json");

            Assert.Equal(1, loaded.MaxIterations);
            Assert.Equal(2, loaded.VariantsPerIteration);

            var deviations = new[]
            {
                nameof(SimulationConfig.MaxIterations),
                nameof(SimulationConfig.VariantsPerIteration)
            };

            foreach (PropertyInfo property in TemplateProjectFiles.PropertiesOf(typeof(SimulationConfig)))
            {
                if (deviations.Contains(property.Name)) continue;

                Assert.Equal(
                    TemplateProjectFiles.Describe(property.GetValue(defaults)),
                    TemplateProjectFiles.Describe(property.GetValue(loaded)));
            }
        }

        /// <summary>
        /// Dasselbe für die Solver-Dateien: jeder Schlüssel genannt, jeder Wert der
        /// Code-Standard. Hier ohne Ausnahme — eine Fluideigenschaft hat keinen Grund,
        /// in der Vorlage anders zu stehen als im Code.
        /// </summary>
        [Fact]
        public void The_Template_Su2_Json_Mirrors_The_Code_Defaults()
        {
            AssertMirrorsDefaults<Su2SolverOptions>("solvers/su2.json");
        }

        /// <inheritdoc cref="The_Template_Su2_Json_Mirrors_The_Code_Defaults"/>
        [Fact]
        public void The_Template_Gmsh_Json_Mirrors_The_Code_Defaults()
        {
            AssertMirrorsDefaults<GmshMesherOptions>("solvers/gmsh.json");
        }

        /// <summary>
        /// Die <c>project.json</c> und <c>CreateDefaults()</c> führen dieselben Zahlen — das sagt
        /// die Vorlage in ihren Kommentaren zu, und ein neues Projekt erbt diese Zusage. Läufen
        /// sie auseinander, rechnet ein Projekt ohne <c>project.json</c> anders als eines mit.
        /// </summary>
        [Fact]
        public void The_Template_Manifest_Matches_Its_Code_Defaults()
        {
            ProjectConfig code = new MeinProjektProject().CreateDefaults();

            ProjectConfig file = TemplateProjectFiles
                .Load(ProjectConfigDto.FromProjectConfig(new MeinProjektProject().CreateDefaults()), "project.json")
                .ToProjectConfig();

            Assert.Equal(TemplateProjectFiles.Describe(code.BaseParameters), TemplateProjectFiles.Describe(file.BaseParameters));
            Assert.Equal(TemplateProjectFiles.Describe(code.MaxDeviations), TemplateProjectFiles.Describe(file.MaxDeviations));
            Assert.Equal(TemplateProjectFiles.Describe(code.OptimizationTargets), TemplateProjectFiles.Describe(file.OptimizationTargets));

            Assert.Equal(code.ParameterBounds.Count, file.ParameterBounds.Count);
            foreach (var bound in code.ParameterBounds)
            {
                Assert.True(file.ParameterBounds.ContainsKey(bound.Key), $"ParameterBounds: '{bound.Key}' fehlt in project.json.");
                Assert.Equal(bound.Value, file.ParameterBounds[bound.Key]);
            }

            Assert.Equal(
                code.DimensionalParameters.OrderBy(name => name, StringComparer.Ordinal),
                file.DimensionalParameters.OrderBy(name => name, StringComparer.Ordinal));
        }

        /// <summary>
        /// Jeder Parameter braucht alle drei Einträge. Fehlte ein Startwert, liefe der Lauf gegen
        /// die Abbruchmeldung des Generators; fehlten die Grenzen, könnte der Algorithmus ihn
        /// beliebig weit verstellen.
        /// </summary>
        [Fact]
        public void Every_Template_Parameter_Has_A_Start_Value_A_Deviation_And_Bounds()
        {
            ProjectConfig config = new MeinProjektProject().CreateDefaults();

            Assert.NotEmpty(config.BaseParameters);

            foreach (string name in config.BaseParameters.Keys)
            {
                Assert.True(config.MaxDeviations.ContainsKey(name), $"MaxDeviations fehlt für '{name}'.");
                Assert.True(config.ParameterBounds.ContainsKey(name), $"ParameterBounds fehlt für '{name}'.");

                (float min, float max) = config.ParameterBounds[name];

                Assert.True(min < max, $"ParameterBounds für '{name}': Min muss kleiner als Max sein.");
                Assert.InRange(config.BaseParameters[name], min, max);
            }
        }

        // ------------------------------------------------------------------
        //  Kein Projekt: die Vorlage ist nicht startbar
        // ------------------------------------------------------------------

        /// <summary>
        /// <c>_Vorlage</c> darf nicht als Projektordner gelten — sonst verlangte die
        /// <see cref="ProjectRegistry"/> eine Definition dazu und bräche beim Start ab.
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
        /// Und die Definition der Vorlage darf nicht im Programm landen: sie heißt
        /// <c>MeinProjekt</c>, und zu diesem Namen gibt es keinen Ordner.
        /// <c>Automatisierung_v2.csproj</c> schließt <c>_Vorlage</c> deshalb aus.
        ///
        /// <para>
        /// Dass die Klasse hier trotzdem benutzbar ist, ist der Beweis für die andere Hälfte des
        /// Aufbaus: das Testprojekt compiliert die Vorlage, damit eine geänderte
        /// Framework-Schnittstelle als Build-Fehler auffällt und nicht Monate später.
        /// </para>
        /// </summary>
        [Fact]
        public void The_Template_Definition_Is_Compiled_But_Not_Part_Of_The_Program()
        {
            Assembly program = typeof(MantaProject).Assembly;

            Assert.NotEqual(program, typeof(MeinProjektProject).Assembly);
            Assert.Equal(GetType().Assembly, typeof(MeinProjektProject).Assembly);

            var registry = ProjectRegistry.Discover(program);

            Assert.DoesNotContain(
                new MeinProjektProject().Name,
                registry.Names,
                StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Der Platzhalter muss der sein, den <c>scripts/new-project.ps1</c> ersetzt. Liefen die
        /// beiden auseinander, entstünde ein Projekt, dessen Klassenname nicht zum Ordner passt —
        /// und das fiele erst beim Build auf.
        /// </summary>
        [Fact]
        public void The_Placeholder_Name_Is_The_One_The_Script_Replaces()
        {
            Assert.Equal(TemplateProjectFiles.Placeholder, new MeinProjektProject().Name);

            // _Vorlage -> projects -> src -> Repo-Wurzel
            string projectsRoot = Path.GetDirectoryName(TemplateProjectFiles.Directory)!;
            string sourceRoot = Path.GetDirectoryName(projectsRoot)!;
            string repoRoot = Path.GetDirectoryName(sourceRoot)!;

            string script = Path.Combine(repoRoot, "scripts", "new-project.ps1");

            Assert.True(File.Exists(script),
                "scripts/new-project.ps1 fehlt — ohne das Skript ist die Vorlage Handarbeit.");

            Assert.Contains(
                $"$Placeholder = '{TemplateProjectFiles.Placeholder}'",
                File.ReadAllText(script));
        }

        // ------------------------------------------------------------------
        //  Die Fitness der Vorlage
        // ------------------------------------------------------------------

        /// <summary>
        /// Weniger Widerstand = höhere Fitness. Das ist die eine Aussage, die die
        /// Vorlagen-Formel macht — und die Richtung, in die ein neuer Nutzer sie erweitert.
        /// </summary>
        [Fact]
        public void The_Template_Fitness_Rewards_Less_Drag()
        {
            var calculator = new MeinProjektFitnessCalculator(new MeinProjektProject().CreateDefaults());

            ModelRecord low = RecordWithDrag(0.5f);
            ModelRecord high = RecordWithDrag(2.0f);

            calculator.CalculateFitness(low, SimulationConfig.CreateDefault());
            calculator.CalculateFitness(high, SimulationConfig.CreateDefault());

            Assert.Equal(2.0f, low.Fitness, 3);
            Assert.Equal(0.5f, high.Fitness, 3);
            Assert.True(low.Fitness > high.Fitness);
        }

        /// <summary>Nur „Drag" — die Vorlage bewertet nichts anderes (TODO-26).</summary>
        [Fact]
        public void The_Template_Fitness_Requires_Only_Drag()
        {
            var calculator = new MeinProjektFitnessCalculator(new MeinProjektProject().CreateDefaults());

            Assert.Equal(new[] { "Drag" }, calculator.RequiredMetrics);
        }

        /// <summary>
        /// Abgebrochene Simulation: das Flag wird ZUERST geprüft (TODO-2). Die
        /// <c>PassiveParameters</c> eines fehlgeschlagenen Modells sind unvollständig.
        /// </summary>
        [Fact]
        public void A_Failed_Simulation_Gets_The_Failure_Fitness()
        {
            var calculator = new MeinProjektFitnessCalculator(new MeinProjektProject().CreateDefaults());

            ModelRecord record = RecordWithDrag(0.5f);
            record.SimulationFailed = true;

            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(0.0001f, record.Fitness, 6);
        }

        /// <summary>Ohne Widerstand ist nichts zu bewerten — und keine Division zu wagen.</summary>
        [Fact]
        public void A_Record_Without_Drag_Gets_The_Failure_Fitness()
        {
            var calculator = new MeinProjektFitnessCalculator(new MeinProjektProject().CreateDefaults());

            var record = new ModelRecord();
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(0.0001f, record.Fitness, 6);
        }

        /// <summary>
        /// Ein Widerstand von (fast) Null darf keine Fitness in Millionenhöhe geben: ein einziger
        /// solcher Ausreißer würde den Rest des Laufs dominieren. Die Untergrenze 0.001 deckelt
        /// die Fitness bei 1000.
        /// </summary>
        [Fact]
        public void A_Drag_Near_Zero_Is_Clamped()
        {
            var calculator = new MeinProjektFitnessCalculator(new MeinProjektProject().CreateDefaults());

            ModelRecord record = RecordWithDrag(0.0f);
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(1000.0f, record.Fitness, 3);
        }

        /// <summary>
        /// Ein negativer Beiwert wird über den Betrag bewertet — je nach Anströmrichtung kann CD
        /// mit umgekehrtem Vorzeichen herauskommen, und ein negativer Nenner hätte eine negative
        /// Fitness ergeben: schlechter als jedes fehlgeschlagene Modell.
        /// </summary>
        [Fact]
        public void A_Negative_Drag_Is_Judged_By_Its_Magnitude()
        {
            var calculator = new MeinProjektFitnessCalculator(new MeinProjektProject().CreateDefaults());

            ModelRecord record = RecordWithDrag(-0.5f);
            calculator.CalculateFitness(record, SimulationConfig.CreateDefault());

            Assert.Equal(2.0f, record.Fitness, 3);
        }

        // ------------------------------------------------------------------
        //  Hilfsmittel
        // ------------------------------------------------------------------

        private static ModelRecord RecordWithDrag(float drag)
        {
            var record = new ModelRecord();
            record.PassiveParameters["Drag"] = drag;
            return record;
        }

        /// <summary>
        /// Prüft für eine Solver-Datei beides auf einmal: jeder Schlüssel des Typs steht in der
        /// Datei, und jeder Wert ist der Code-Standard.
        /// </summary>
        private static void AssertMirrorsDefaults<T>(string relativeName) where T : class, new()
        {
            var defaults = new T();

            var keys = TemplateProjectFiles.TopLevelKeys(relativeName);

            var missing = TemplateProjectFiles.PropertiesOf(typeof(T))
                .Select(property => property.Name)
                .Where(name => !keys.Contains(name, StringComparer.OrdinalIgnoreCase))
                .ToList();

            Assert.True(missing.Count == 0,
                $"In src/projects/_Vorlage/{relativeName} fehlt/fehlen: " + string.Join(", ", missing)
                + ". Die Vorlage soll jeden Schlüssel zeigen, den es gibt.");

            T loaded = TemplateProjectFiles.Load(new T(), relativeName);

            foreach (PropertyInfo property in TemplateProjectFiles.PropertiesOf(typeof(T)))
            {
                Assert.Equal(
                    TemplateProjectFiles.Describe(property.GetValue(defaults)),
                    TemplateProjectFiles.Describe(property.GetValue(loaded)));
            }
        }
    }
}
