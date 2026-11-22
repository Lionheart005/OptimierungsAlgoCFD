using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Locates the projects root directory (<c>src/projects/</c>) and individual project folders.
    ///
    /// <para>Searches in the following order:</para>
    /// <list type="number">
    ///   <item>an explicitly passed path (CLI argument)</item>
    ///   <item>the <c>SIM_PROJECTS_DIR</c> environment variable</item>
    ///   <item>upward from the current working directory until <c>src/projects/</c> is found</item>
    /// </list>
    ///
    /// <para>
    /// JSON config files are intentionally not copied to <c>bin/</c> via <c>CopyToOutputDirectory</c>,
    /// so edits to source config files take effect immediately without requiring a rebuild.
    /// </para>
    /// </summary>
    public static class ProjectPaths
    {
        /// <summary>Source directory in the repository root.</summary>
        public const string SourceDirectoryName = "src";

        /// <summary>Subdirectory under <see cref="SourceDirectoryName"/> containing project folders.</summary>
        public const string ProjectsDirectoryName = "projects";

        /// <summary>Environment variable that overrides the projects root directory.</summary>
        public const string ProjectsDirectoryVariable = "SIM_PROJECTS_DIR";

        /// <summary>
        /// 3D template for new projects. Not counted as a project (has no <c>IProjectDefinition</c>).
        /// </summary>
        public const string TemplateDirectoryName = "_Template3D";

        /// <summary>
        /// 2D template for new projects. Not counted as a project.
        /// </summary>
        public const string Template2DDirectoryName = "_Template2D";

        /// <summary>
        /// 3D internal-flow (OpenFOAM) template for new projects. Not counted as a project.
        /// </summary>
        public const string TemplateInternal3DDirectoryName = "_TemplateInternal3D";

        /// <summary>Any folder starting with this prefix is a template, not a runnable project.</summary>
        public const string TemplatePrefix = "_Template";

        /// <summary>
        /// Project manifest filename. Identical across all projects to avoid confusion with the project name.
        /// </summary>
        public const string ManifestFileName = "project.json";

        /// <summary>Maximum parent directory levels to search upward.</summary>
        private const int MaxParentLevelsToSearch = 6;

        /// <summary>Directories skipped when listing projects.</summary>
        private static readonly string[] IgnoredDirectories = { "bin", "obj" };

        /// <summary>
        /// Returns the directory containing project folders, or <c>null</c> if not found.
        /// </summary>
        /// <param name="explicitPath">
        /// An explicitly provided path. If it does not exist, <c>null</c> is returned immediately
        /// rather than falling back to directory search.
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
        /// Repository root: the parent of <c>src/projects/</c>, containing <c>.git</c> (local)
        /// or <c>deploy-info.json</c> and <c>.deploy_hash</c> (server). Returns <c>null</c> if not found.
        /// </summary>
        public static string? ResolveRepositoryRoot(string? explicitPath = null)
        {
            string? projectsRoot = ResolveProjectsRoot(explicitPath);
            if (projectsRoot == null) return null;

            DirectoryInfo? source = Directory.GetParent(projectsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            return source?.Parent?.FullName;
        }

        /// <summary>
        /// Returns a project's directory (the folder name <b>is</b> the project name),
        /// or <c>null</c> if the projects root or folder does not exist.
        /// </summary>
        public static string? ResolveProjectDirectory(string projectName, string? explicitPath = null)
        {
            if (string.IsNullOrWhiteSpace(projectName)) return null;

            string? root = ResolveProjectsRoot(explicitPath);
            if (root == null) return null;

            // Case-insensitive match so Linux and Windows resolve the same project consistently.
            return Directory.EnumerateDirectories(root)
                .FirstOrDefault(directory => string.Equals(
                    Path.GetFileName(directory), projectName.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// Lists all project names present as folders — sorted alphabetically, excluding templates
        /// and build artifacts.
        /// </summary>
        public static IReadOnlyList<string> ListProjectNames(string? explicitPath = null)
        {
            string? root = ResolveProjectsRoot(explicitPath);
            if (root == null) return Array.Empty<string>();

            return Directory.EnumerateDirectories(root)
                .Select(Path.GetFileName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Select(name => name!)
                .Where(name => !name.StartsWith(TemplatePrefix, StringComparison.OrdinalIgnoreCase))
                .Where(name => !IgnoredDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                .Where(name => !name.StartsWith(".", StringComparison.Ordinal))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
