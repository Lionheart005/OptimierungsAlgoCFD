using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Findet das Verzeichnis, in dem die Projekte liegen (<c>src/projects/</c>), und darin den
    /// Ordner eines einzelnen Projekts.
    ///
    /// <para>
    /// Ersetzt die frühere „sechs Ebenen hoch, suche <c>config</c>“-Heuristik durch eine
    /// benannte Stelle (TODO-22). Gesucht wird in dieser Reihenfolge:
    /// </para>
    /// <list type="number">
    ///   <item>ein explizit übergebener Pfad (Aufrufargument)</item>
    ///   <item>die Umgebungsvariable <c>SIM_PROJECTS_DIR</c></item>
    ///   <item>aufwärts vom Arbeitsverzeichnis, bis ein <c>src/projects/</c> auftaucht</item>
    /// </list>
    ///
    /// <para>
    /// Punkt 3 ist der Normalfall und der Grund, warum es diese Klasse gibt: <c>dotnet run</c>
    /// startet im Projektverzeichnis, die Projektordner liegen aber im Repo-Root.
    /// </para>
    ///
    /// <para>
    /// Bewusst <b>kein</b> <c>CopyToOutputDirectory</c> in der csproj: die JSONs lägen dann in
    /// <c>bin/</c>, und eine Änderung an der Quelldatei würde erst nach einem Rebuild wirken.
    /// Diese Sorte Verwirrung will man bei einem mehrstündigen Lauf nicht.
    /// </para>
    /// </summary>
    public static class ProjectPaths
    {
        /// <summary>Quellverzeichnis im Repo-Root.</summary>
        public const string SourceDirectoryName = "src";

        /// <summary>Verzeichnis unterhalb von <see cref="SourceDirectoryName"/>, in dem die Projekte liegen.</summary>
        public const string ProjectsDirectoryName = "projects";

        /// <summary>Umgebungsvariable, die das Projektverzeichnis direkt setzt.</summary>
        public const string ProjectsDirectoryVariable = "SIM_PROJECTS_DIR";

        /// <summary>
        /// Vorlage für neue Projekte. Zählt nicht als Projekt — sie hat keine
        /// <c>IProjectDefinition</c> im Code und soll auch keine haben (TODO-23).
        /// </summary>
        public const string TemplateDirectoryName = "_Vorlage";

        /// <summary>
        /// Manifestdatei eines Projekts. Heißt in <b>jedem</b> Projekt gleich (Entscheidung 2):
        /// ein Dateiname, der überall derselbe ist, kann nicht mit dem Projektnamen verwechselt
        /// werden — <c>config/projects/MantaAuv.json</c> konnte das.
        /// </summary>
        public const string ManifestFileName = "project.json";

        /// <summary>Wie viele Ebenen nach oben gesucht wird.</summary>
        private const int MaxParentLevelsToSearch = 6;

        /// <summary>Verzeichnisse, die beim Auflisten von Projekten übersprungen werden.</summary>
        private static readonly string[] IgnoredDirectories = { "bin", "obj" };

        /// <summary>
        /// Liefert das Verzeichnis, in dem die Projektordner liegen, oder <c>null</c>, wenn keines
        /// gefunden wurde.
        /// </summary>
        /// <param name="explicitPath">
        /// Ein direkt übergebener Pfad, z.B. aus dem zweiten Aufrufargument. Existiert er nicht,
        /// wird <c>null</c> geliefert — ein falsch getippter Pfad soll nicht stillschweigend
        /// durch die Suche ersetzt werden.
        /// </param>
        public static string? ResolveProjectsRoot(string? explicitPath = null)
        {
            if (!string.IsNullOrWhiteSpace(explicitPath))
                return Directory.Exists(explicitPath) ? Path.GetFullPath(explicitPath) : null;

            string? fromEnvironment = Environment.GetEnvironmentVariable(ProjectsDirectoryVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
                return Directory.Exists(fromEnvironment) ? Path.GetFullPath(fromEnvironment) : null;

            DirectoryInfo? current = new DirectoryInfo(Directory.GetCurrentDirectory());

            for (int level = 0; level <= MaxParentLevelsToSearch && current != null; level++)
            {
                string candidate = Path.Combine(current.FullName, SourceDirectoryName, ProjectsDirectoryName);
                if (Directory.Exists(candidate)) return candidate;
                current = current.Parent;
            }

            return null;
        }

        /// <summary>
        /// Liefert den Ordner eines Projekts. Der Ordnername <b>ist</b> der Projektname
        /// (Entscheidung 1) — es gibt keine zweite Stelle, an der er stünde.
        /// Gibt <c>null</c> zurück, wenn es das Projektverzeichnis oder den Ordner nicht gibt.
        /// </summary>
        public static string? ResolveProjectDirectory(string projectName, string? explicitPath = null)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return null;

            string? root = ResolveProjectsRoot(explicitPath);
            if (root == null) return null;

            // Groß-/Kleinschreibung ignorieren: Linux unterscheidet sie, Windows nicht, und ein
            // Lauf soll auf beiden Rechnern dasselbe Projekt finden.
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(directory => string.Equals(
                    Path.GetFileName(directory), projectName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Alle Projektnamen, die als Ordner vorliegen — alphabetisch, ohne die Vorlage und ohne
        /// Build-Artefakte. Grundlage für die Startmeldung „unbekanntes Projekt, vorhanden sind …“.
        /// </summary>
        public static IReadOnlyList<string> ListProjectNames(string? explicitPath = null)
        {
            string? root = ResolveProjectsRoot(explicitPath);
            if (root == null) return Array.Empty<string>();

            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Where(name => !string.Equals(name, TemplateDirectoryName, StringComparison.OrdinalIgnoreCase))
                .Where(name => !IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Where(name => !name.StartsWith(".", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
