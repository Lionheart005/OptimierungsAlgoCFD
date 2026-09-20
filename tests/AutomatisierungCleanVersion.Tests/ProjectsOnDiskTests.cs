using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Prüft <b>jedes</b> Projekt, das wirklich unter <c>src/projects/</c> liegt — nicht ein
    /// bestimmtes.
    ///
    /// <para>
    /// <b>Warum generisch:</b> die übrigen Projekt-Tests nennen <c>MantaAuv</c> beim Namen und
    /// sind damit wertlos für jedes zweite Projekt. Ein Test, der über die vorhandenen Ordner
    /// läuft, deckt ein neu angelegtes Projekt ab dem Moment mit ab, in dem es existiert — und
    /// verschwindet mit ihm wieder, ohne rot zu werden. Genau das ist der Fall, den dieses
    /// Framework können soll: Projekte kommen und gehen, das Gerüst bleibt.
    /// </para>
    /// </summary>
    public class ProjectsOnDiskTests
    {
        /// <summary>Die Projektnamen aus dem Dateisystem, als xUnit-Testdaten.</summary>
        public static IEnumerable<object[]> ProjectNames =>
            ProjectPaths.ListProjectNames().Select(name => new object[] { name });

        /// <summary>
        /// Der Abgleich, den auch <c>Program.cs</c> beim Start fährt — hier gegen das echte
        /// Repo. Ein neuer Ordner ohne Definition (oder mit abweichendem <c>Name</c>) fällt
        /// damit im Test auf und nicht erst beim ersten Lauf auf dem Server.
        /// </summary>
        [Fact]
        public void Every_Folder_Has_A_Definition_And_Every_Definition_A_Folder()
        {
            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);

            // Wirft mit einer Meldung, die beide Richtungen und den fehlenden Namen nennt.
            registry.AssertFoldersMatchDefinitions();
        }

        /// <summary>Zu jedem Ordner muss die Registry den Namen auch auflösen können.</summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Project_Can_Be_Resolved(string name)
        {
            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);

            Assert.Equal(name, registry.Resolve(name).Name, ignoreCase: true);
        }

        /// <summary>
        /// Die beiden Pflichtdateien müssen da sein und sich laden lassen. Ein unbekannter
        /// Schlüssel oder ein Syntaxfehler in der JSONC wirft dabei — das ist der eigentliche
        /// Zweck: ein Tippfehler in einer Projektdatei soll nicht erst auf dem Server auffallen.
        /// </summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Project_Loads_Its_Configuration(string name)
        {
            string directory = DirectoryOf(name);

            Assert.True(File.Exists(Path.Combine(directory, "simulation.json")),
                $"src/projects/{name}/simulation.json fehlt.");
            Assert.True(File.Exists(Path.Combine(directory, ProjectPaths.ManifestFileName)),
                $"src/projects/{name}/{ProjectPaths.ManifestFileName} fehlt.");

            var simulation = JsonConfigLoader.Load(
                SimulationConfig.CreateDefault(), Path.Combine(directory, "simulation.json"));

            // Plausibilität, keine Zahlenprüfung: ein Lauf mit 0 Varianten rechnet nichts.
            Assert.True(simulation.MaxIterations >= 1, $"{name}: MaxIterations muss mindestens 1 sein.");
            Assert.True(simulation.VariantsPerIteration >= 1, $"{name}: VariantsPerIteration muss mindestens 1 sein.");
            Assert.False(string.IsNullOrWhiteSpace(simulation.MpiRunPath), $"{name}: MpiRunPath ist leer.");
            Assert.False(string.IsNullOrWhiteSpace(simulation.Su2Path), $"{name}: Su2Path ist leer.");
            Assert.False(string.IsNullOrWhiteSpace(simulation.GmshPath), $"{name}: GmshPath ist leer.");
        }

        /// <summary>
        /// Die Solver-Dateien laufen über <see cref="ProjectContext.LoadSolverOptions{T}"/> —
        /// denselben Weg wie im Lauf. Eine Datei, die nur aus Kommentaren besteht, ist dabei
        /// ausdrücklich erlaubt: sie heißt „hier weicht nichts ab", und dann müssen die
        /// Code-Standards gelten.
        /// </summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Project_Loads_Its_Solver_Options(string name)
        {
            var context = new ProjectContext(
                name, DirectoryOf(name), null, SimulationConfig.CreateDefault(), new ProjectConfig());

            var su2 = context.LoadSolverOptions<Su2SolverOptions>("su2");
            var gmsh = context.LoadSolverOptions<GmshMesherOptions>("gmsh");

            // Ohne Referenzfläche wäre jeder Beiwert um Größenordnungen falsch (TODO-12),
            // und ohne eine einzige Ergebnisgröße käme aus dem Solver gar nichts an (TODO-26).
            Assert.False(string.IsNullOrWhiteSpace(su2.ReferenceAreaMetric), $"{name}: ReferenceAreaMetric ist leer.");
            Assert.NotEmpty(su2.ResultMetrics);
            Assert.NotEmpty(su2.HistoryOutput);

            // Ein Windkanal ohne Ausdehnung vernetzt nicht.
            Assert.True(gmsh.TunnelSizeX > 0f && gmsh.TunnelSizeY > 0f && gmsh.TunnelSizeZ > 0f,
                $"{name}: Der Windkanal hat keine Ausdehnung.");
        }

        /// <summary>
        /// Jeder Parameter braucht Startwert, Streuung und Grenzen — und der Startwert muss
        /// innerhalb der Grenzen liegen. Fehlt eine Streuung, mutiert der Algorithmus den
        /// Parameter nie; fehlen die Grenzen, verstellt er ihn beliebig weit. Beides fällt im
        /// Lauf nicht auf, es käme nur ein schlechtes Ergebnis heraus.
        /// </summary>
        [Theory]
        [MemberData(nameof(ProjectNames))]
        public void Every_Parameter_Is_Fully_Specified(string name)
        {
            var registry = ProjectRegistry.Discover(typeof(MantaProject).Assembly);
            IProjectDefinition definition = registry.Resolve(name);

            ProjectConfig project = JsonConfigLoader.Load(
                    ProjectConfigDto.FromProjectConfig(definition.CreateDefaults()),
                    Path.Combine(DirectoryOf(name), ProjectPaths.ManifestFileName))
                .ToProjectConfig();

            Assert.NotEmpty(project.BaseParameters);

            foreach (KeyValuePair<string, float> parameter in project.BaseParameters)
            {
                Assert.True(project.MaxDeviations.ContainsKey(parameter.Key),
                    $"{name}: MaxDeviations fehlt für '{parameter.Key}'.");
                Assert.True(project.ParameterBounds.ContainsKey(parameter.Key),
                    $"{name}: ParameterBounds fehlt für '{parameter.Key}'.");

                (float min, float max) = project.ParameterBounds[parameter.Key];

                Assert.True(min <= max, $"{name}: '{parameter.Key}' hat Min > Max.");
                Assert.InRange(parameter.Value, min, max);
            }

            // Ein dimensionsbehafteter Parameter, den es nicht gibt, ist ein Tippfehler: der
            // RubberBandScaler würde ihn stillschweigend ignorieren.
            foreach (string dimensional in project.DimensionalParameters)
            {
                Assert.True(project.BaseParameters.ContainsKey(dimensional),
                    $"{name}: DimensionalParameters nennt '{dimensional}', das es in "
                    + "BaseParameters nicht gibt.");
            }
        }

        private static string DirectoryOf(string name)
        {
            string? directory = ProjectPaths.ResolveProjectDirectory(name);

            Assert.True(directory != null, $"Der Ordner src/projects/{name}/ wurde nicht gefunden.");

            return directory!;
        }
    }
}
