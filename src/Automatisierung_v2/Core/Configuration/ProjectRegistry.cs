using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Findet die Projekte und löst einen Namen in seine Definition auf (TODO-23).
    ///
    /// <para>
    /// Gesucht wird per Reflection nach allen nicht-abstrakten <see cref="IProjectDefinition"/>
    /// in der Assembly (Entscheidung 5) — nicht über Typnamen in einer JSON-Datei. Damit ist ein
    /// neues Projekt wirklich nur noch ein Ordner: keine Zeile in <c>Program.cs</c>, kein
    /// <c>case</c>, kein Eintrag in einer Liste.
    /// </para>
    /// </summary>
    public sealed class ProjectRegistry
    {
        private readonly List<IProjectDefinition> _definitions;

        private ProjectRegistry(List<IProjectDefinition> definitions)
        {
            _definitions = definitions;
        }

        /// <summary>Alle gefundenen Projektdefinitionen, alphabetisch nach Namen.</summary>
        public IReadOnlyList<IProjectDefinition> Definitions => _definitions;

        /// <summary>Die Namen der gefundenen Projekte, alphabetisch.</summary>
        public IReadOnlyList<string> Names =>
            _definitions.Select(definition => definition.Name).ToList();

        /// <summary>
        /// Sucht alle Projektdefinitionen in einer Assembly.
        /// </summary>
        /// <param name="assembly">
        /// Ohne Angabe die Assembly, in der dieser Kern liegt — dort landen auch die
        /// Projektdateien, weil die csproj <c>src/projects/**/*.cs</c> mit hineinzieht.
        /// </param>
        /// <exception cref="InvalidOperationException">Zwei Definitionen tragen denselben Namen.</exception>
        public static ProjectRegistry Discover(Assembly? assembly = null)
        {
            Assembly source = assembly ?? typeof(ProjectRegistry).Assembly;

            IEnumerable<Type> types;
            try
            {
                types = source.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                // Eine einzelne unladbare Klasse soll nicht den ganzen Start verhindern.
                types = ex.Types.Where(type => type != null).Select(type => type!);
            }

            return FromTypes(types);
        }

        /// <summary>
        /// Baut die Registry aus einer Typliste. Getrennt von <see cref="Discover"/>, damit
        /// Tests eigene Definitionen einspeisen können, ohne sie in die Assembly zu legen —
        /// eine Attrappe in der Test-Assembly wäre für einen Scan über den Kern unsichtbar.
        /// </summary>
        public static ProjectRegistry FromTypes(IEnumerable<Type> types)
        {
            var definitions = types
                .Where(type => typeof(IProjectDefinition).IsAssignableFrom(type))
                .Where(type => !type.IsAbstract && !type.IsInterface)
                // Ohne öffentlichen parameterlosen Konstruktor lässt sich nichts bauen. Das
                // still zu überspringen wäre falsch — es ist mit hoher Wahrscheinlichkeit ein
                // Versehen, und ein Projekt, das nicht auftaucht, sucht man lange.
                .Select(type => Create(type))
                .ToList();

            return FromDefinitions(definitions);
        }

        /// <summary>Baut die Registry aus fertigen Definitionen — der direkteste Weg für Tests.</summary>
        /// <exception cref="InvalidOperationException">Zwei Definitionen tragen denselben Namen.</exception>
        public static ProjectRegistry FromDefinitions(IEnumerable<IProjectDefinition> definitions)
        {
            var sorted = definitions
                .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AssertNamesAreUnique(sorted);

            return new ProjectRegistry(sorted);
        }

        /// <summary>
        /// Löst einen Projektnamen auf. Groß-/Kleinschreibung und Leerzeichen sind egal.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Kein Name angegeben oder keine Definition dazu — die Meldung nennt in beiden Fällen
        /// die vorhandenen Projekte, damit der Nutzer nicht raten muss.
        /// </exception>
        public IProjectDefinition Resolve(string? projectName)
        {
            string wanted = (projectName ?? string.Empty).Trim();

            if (wanted.Length == 0)
                throw new InvalidOperationException(
                    "[PROJEKT] Es wurde kein Projekt angegeben. " + AvailableProjects()
                    + " Aufruf:  dotnet run -- <Projektname>   oder  .\\scripts\\sim.ps1 run -Project <Projektname>");

            IProjectDefinition? found = _definitions.FirstOrDefault(
                definition => string.Equals(definition.Name, wanted, StringComparison.OrdinalIgnoreCase));

            if (found == null)
                throw new InvalidOperationException(
                    $"[PROJEKT] Unbekanntes Projekt '{wanted}'. " + AvailableProjects());

            return found;
        }

        /// <summary>
        /// Gleicht Definitionen und Ordner gegeneinander ab: zu jeder Definition muss ein Ordner
        /// <c>src/projects/&lt;Name&gt;/</c> existieren, und zu jedem Ordner genau eine Definition.
        ///
        /// <para>
        /// Beide Richtungen, weil beide Fehler still wären: ein Ordner ohne Definition wird beim
        /// Start nie angeboten, eine Definition ohne Ordner findet ihre JSONs nicht und rechnet
        /// klaglos mit den Code-Standards weiter.
        /// </para>
        ///
        /// <para>
        /// Gibt es überhaupt kein Projektverzeichnis, wird nichts geprüft und nur ein Hinweis
        /// ausgegeben — der Abgleich greift erst, wenn die Ordner mit TODO-24 angelegt sind.
        /// </para>
        /// </summary>
        /// <exception cref="InvalidOperationException">Ordner und Definitionen passen nicht zusammen.</exception>
        public void AssertFoldersMatchDefinitions(string? projectsRoot = null)
        {
            string? root = ProjectPaths.ResolveProjectsRoot(projectsRoot);

            if (root == null)
            {
                Console.WriteLine(
                    $"[PROJEKT] Kein Verzeichnis {ProjectPaths.SourceDirectoryName}/"
                    + $"{ProjectPaths.ProjectsDirectoryName}/ gefunden — der Abgleich zwischen "
                    + "Ordnern und Projektdefinitionen entfällt.");
                return;
            }

            var folders = ProjectPaths.ListProjectNames(root);
            var problems = new List<string>();

            foreach (IProjectDefinition definition in _definitions)
            {
                if (!folders.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"Zur Definition {definition.GetType().Name} (Name '{definition.Name}') fehlt der "
                        + $"Ordner {ProjectPaths.SourceDirectoryName}/{ProjectPaths.ProjectsDirectoryName}/{definition.Name}/. "
                        + "Ohne ihn findet das Projekt seine JSON-Dateien nicht und rechnet mit den Code-Standards.");
                }
            }

            foreach (string folder in folders)
            {
                int matches = _definitions.Count(
                    definition => string.Equals(definition.Name, folder, StringComparison.OrdinalIgnoreCase));

                if (matches == 0)
                {
                    problems.Add(
                        $"Zum Ordner {ProjectPaths.SourceDirectoryName}/{ProjectPaths.ProjectsDirectoryName}/{folder}/ "
                        + "gibt es keine IProjectDefinition. Das Projekt lässt sich nicht starten; "
                        + $"entweder fehlt die Klasse, oder ihr {nameof(IProjectDefinition.Name)} "
                        + "weicht vom Ordnernamen ab.");
                }
            }

            if (problems.Count == 0) return;

            throw new InvalidOperationException(
                "[PROJEKT] Ordner und Projektdefinitionen passen nicht zusammen:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "          " + problem)));
        }

        /// <summary>Der Satz für Fehlermeldungen: welche Projekte gibt es überhaupt?</summary>
        public string AvailableProjects()
        {
            return _definitions.Count == 0
                ? "Es ist überhaupt kein Projekt vorhanden."
                : "Vorhanden sind: " + string.Join(", ", Names) + ".";
        }

        private static IProjectDefinition Create(Type type)
        {
            try
            {
                return (IProjectDefinition)Activator.CreateInstance(type)!;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"[PROJEKT] Die Projektdefinition {type.FullName} lässt sich nicht erzeugen: {ex.Message} "
                    + "Sie braucht einen öffentlichen Konstruktor ohne Parameter — alles Weitere "
                    + "bekommt sie über den ProjectContext.", ex);
            }
        }

        private static void AssertNamesAreUnique(List<IProjectDefinition> definitions)
        {
            var duplicates = definitions
                .GroupBy(definition => definition.Name?.Trim() ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1)
                .ToList();

            if (duplicates.Count == 0) return;

            string detail = string.Join(Environment.NewLine, duplicates.Select(group =>
                $"          '{group.Key}' wird beansprucht von: "
                + string.Join(", ", group.Select(definition => definition.GetType().FullName))));

            throw new InvalidOperationException(
                "[PROJEKT] Zwei Projektdefinitionen tragen denselben Namen. Der Name ist der "
                + "Ordnername und muss eindeutig sein, sonst hinge es an der Reihenfolge der "
                + "Reflection, welches Projekt gerechnet wird:" + Environment.NewLine + detail);
        }
    }
}
