using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace FluidForge.Core
{
    /// <summary>
    /// Discovers projects and resolves a project name to its <see cref="IProjectDefinition"/>.
    ///
    /// <para>
    /// Discovers all non-abstract <see cref="IProjectDefinition"/> types in the assembly via reflection,
    /// so adding a new project requires only adding its folder under <c>src/projects/</c> without
    /// modifying <c>Program.cs</c> or central switch statements.
    /// </para>
    /// </summary>
    public sealed class ProjectRegistry
    {
        private readonly List<IProjectDefinition> _definitions;

        private ProjectRegistry(List<IProjectDefinition> definitions)
        {
            _definitions = definitions;
        }

        /// <summary>All discovered project definitions, sorted alphabetically by name.</summary>
        public IReadOnlyList<IProjectDefinition> Definitions => _definitions;

        /// <summary>Names of all discovered projects, sorted alphabetically.</summary>
        public IReadOnlyList<string> Names =>
            _definitions.Select(definition => definition.Name).ToList();

        /// <summary>
        /// Discovers all project definitions in an assembly (defaults to the Core assembly).
        /// </summary>
        /// <exception cref="InvalidOperationException">Two definitions share the same name.</exception>
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
                // A single unloadable type must not prevent startup.
                types = ex.Types.Where(type => type != null).Select(type => type!);
            }

            return FromTypes(types);
        }

        /// <summary>
        /// Builds the registry from an explicit list of types (useful for unit tests).
        /// </summary>
        public static ProjectRegistry FromTypes(IEnumerable<Type> types)
        {
            var definitions = types
                .Where(type => typeof(IProjectDefinition).IsAssignableFrom(type))
                .Where(type => !type.IsAbstract && !type.IsInterface)
                .Select(type => Create(type))
                .ToList();

            return FromDefinitions(definitions);
        }

        /// <summary>Builds the registry from instantiated definitions (for unit tests).</summary>
        /// <exception cref="InvalidOperationException">Two definitions share the same name.</exception>
        public static ProjectRegistry FromDefinitions(IEnumerable<IProjectDefinition> definitions)
        {
            var sorted = definitions
                .OrderBy(definition => definition.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();

            AssertNamesAreUnique(sorted);

            return new ProjectRegistry(sorted);
        }

        /// <summary>
        /// Resolves a project name (case-insensitive, trimmed).
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No name was provided or no matching definition exists.
        /// </exception>
        public IProjectDefinition Resolve(string? projectName)
        {
            string wanted = (projectName ?? string.Empty).Trim();

            if (wanted.Length == 0)
                throw new InvalidOperationException(
                    "[PROJECT] No project specified. " + AvailableProjects()
                    + " Usage:  dotnet run -- <ProjectName>   or  .\\scripts\\sim.ps1 run -Project <ProjectName>");

            IProjectDefinition? found = _definitions.FirstOrDefault(
                definition => string.Equals(definition.Name, wanted, StringComparison.OrdinalIgnoreCase));

            if (found == null)
                throw new InvalidOperationException(
                    $"[PROJECT] Unknown project '{wanted}'. " + AvailableProjects());

            return found;
        }

        /// <summary>
        /// Verifies that every definition has a matching <c>src/projects/&lt;Name&gt;/</c> folder
        /// and every project folder has a matching definition.
        /// </summary>
        /// <exception cref="InvalidOperationException">Folders and definitions do not match.</exception>
        public void AssertFoldersMatchDefinitions(string? projectsRoot = null)
        {
            string? root = ProjectPaths.ResolveProjectsRoot(projectsRoot);

            if (root == null)
            {
                Console.WriteLine(
                    $"[PROJECT] Directory {ProjectPaths.SourceDirectoryName}/"
                    + $"{ProjectPaths.ProjectsDirectoryName}/ not found — skipping folder/definition consistency check.");
                return;
            }

            var folders = ProjectPaths.ListProjectNames(root);
            var problems = new List<string>();

            foreach (IProjectDefinition definition in _definitions)
            {
                if (!folders.Contains(definition.Name, StringComparer.OrdinalIgnoreCase))
                {
                    problems.Add(
                        $"Definition {definition.GetType().Name} (Name '{definition.Name}') is missing its folder "
                        + $"{ProjectPaths.SourceDirectoryName}/{ProjectPaths.ProjectsDirectoryName}/{definition.Name}/. "
                        + "Without it, the project cannot locate its JSON files and would fall back to code defaults.");
                }
            }

            foreach (string folder in folders)
            {
                int matches = _definitions.Count(
                    definition => string.Equals(definition.Name, folder, StringComparison.OrdinalIgnoreCase));

                if (matches == 0)
                {
                    problems.Add(
                        $"Folder {ProjectPaths.SourceDirectoryName}/{ProjectPaths.ProjectsDirectoryName}/{folder}/ "
                        + "has no matching IProjectDefinition. The project cannot be started; "
                        + $"either the class is missing or its {nameof(IProjectDefinition.Name)} "
                        + "differs from the folder name.");
                }
            }

            if (problems.Count == 0) return;

            throw new InvalidOperationException(
                "[PROJECT] Folders and project definitions do not match:"
                + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "          " + problem)));
        }

        /// <summary>Human-readable list of available projects for error messages.</summary>
        public string AvailableProjects()
        {
            return _definitions.Count == 0
                ? "No projects are registered."
                : "Available: " + string.Join(", ", Names) + ".";
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
                    $"[PROJECT] Cannot instantiate project definition {type.FullName}: {ex.Message} "
                    + "It requires a public parameterless constructor — everything else "
                    + "is provided via ProjectContext.", ex);
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
                $"          '{group.Key}' is claimed by: "
                + string.Join(", ", group.Select(definition => definition.GetType().FullName))));

            throw new InvalidOperationException(
                "[PROJECT] Two project definitions share the same name. The name must match the "
                + "folder name and be unique:" + Environment.NewLine + detail);
        }
    }
}
