using System.IO;
using Xunit;
using MyPicoGkProject.Core;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Zugriff auf die mitgelieferten JSON-Dateien von <c>src/projects/MantaAuv/</c> (TODO-24).
    ///
    /// <para>
    /// <b>Warum dieser Helfer existiert:</b> <c>JsonConfigLoader</c> überspringt eine fehlende
    /// Datei kommentarlos — das ist im Lauf richtig, im Test aber die schlimmste Sorte Fehler.
    /// Ein Test, der eine nicht gefundene Datei lädt, vergleicht die Code-Standards mit sich
    /// selbst und ist grün, ohne irgendetwas geprüft zu haben. Deshalb wird hier vorher hart
    /// auf Existenz geprüft.
    /// </para>
    /// </summary>
    internal static class MantaProjectFiles
    {
        public const string ProjectName = "MantaAuv";

        /// <summary>
        /// Der Ordner <c>src/projects/MantaAuv/</c>. Gesucht wird aufwärts vom
        /// Arbeitsverzeichnis, weil die Tests aus <c>bin/Debug/net9.0</c> laufen.
        /// </summary>
        public static string Directory
        {
            get
            {
                string? directory = ProjectPaths.ResolveProjectDirectory(ProjectName);

                Assert.True(directory != null,
                    $"Der Ordner src/projects/{ProjectName}/ wurde nicht gefunden — "
                    + "ohne ihn prüfen die folgenden Tests nichts.");

                return directory!;
            }
        }

        /// <summary>
        /// Lädt eine Datei des Projekts über die Schichten des Loaders und stellt sicher, dass
        /// sie auch wirklich existiert.
        /// </summary>
        /// <param name="relativeName">z.B. <c>simulation.json</c> oder <c>solvers/su2.json</c>.</param>
        public static T Load<T>(T defaults, string relativeName) where T : class
        {
            string directory = Directory;
            string file = Path.Combine(directory, relativeName.Replace('/', Path.DirectorySeparatorChar));

            Assert.True(File.Exists(file),
                $"src/projects/{ProjectName}/{relativeName} fehlt — ohne die Datei vergleicht "
                + "dieser Test die Code-Standards mit sich selbst.");

            return JsonConfigLoader.LoadForProject(defaults, relativeName, directory).Value;
        }

        /// <summary>Die <c>simulation.json</c> des Projekts.</summary>
        public static SimulationConfig Simulation()
            => Load(SimulationConfig.CreateDefault(), "simulation.json");

        /// <summary>Die <c>project.json</c> des Projekts, samt Projektname aus dem Ordner.</summary>
        public static ProjectConfig Project(ProjectConfig defaults)
        {
            ProjectConfig loaded = Load(ProjectConfigDto.FromProjectConfig(defaults), ProjectPaths.ManifestFileName)
                .ToProjectConfig();

            // Der Ordnername ist der Projektname (Entscheidung 1) — genau wie in Program.cs.
            loaded.ProjectName = ProjectName;

            return loaded;
        }
    }
}
