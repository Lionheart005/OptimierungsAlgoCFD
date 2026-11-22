using System;
using System.IO;
using System.Runtime.InteropServices;

namespace FluidForge.Core
{
    /// <summary>
    /// Resolves paths to external executables (SU2, Gmsh, MPI).
    /// Supports tilde expansion (~/), PATH lookup, and fallback to standard installation paths.
    /// </summary>
    public static class ExecutableResolver
    {
        /// <summary>
        /// Resolves a configured executable path or command name.
        /// </summary>
        /// <param name="configuredPath">The path or command name read from configuration.</param>
        /// <param name="toolKind">Optional tool kind ("gmsh", "su2", "mpi"). Derived from the name if null.</param>
        /// <param name="output">Optional writer for fallback log messages (defaults to Console.Out).</param>
        /// <param name="homeOverride">For tests only: overrides the user home directory.</param>
        /// <returns>The resolved executable path or command.</returns>
        public static string Resolve(string configuredPath, string? toolKind = null, TextWriter? output = null, string? homeOverride = null)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                return configuredPath;

            string expanded = ExpandHome(configuredPath);
            string home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // MPI: The official SU2 binary is built with MPICH; under OpenMPI each rank spawns an
            // independent serial copy. For bare "mpirun" or "mpiexec", prefer the MPICH environment
            // if present before falling back to PATH. Explicit paths are left untouched.
            if (IsBareMpiName(toolKind, expanded))
            {
                string mpich = MpichMpiexec(home);
                if (File.Exists(mpich))
                {
                    (output ?? Console.Out).WriteLine($"[RESOLVER] '{configuredPath}' -> MPICH environment: {mpich}");
                    return mpich;
                }
            }

            // 1. If the path contains directory separators and exists on disk -> return directly
            bool hasPathSeparators = expanded.Contains('/') || expanded.Contains('\\');
            if (hasPathSeparators && File.Exists(expanded))
            {
                return expanded;
            }

            // 2. If the command is found in PATH -> return expanded
            if (FindInPath(expanded) != null)
            {
                return expanded;
            }

            // 3. Otherwise check standard server fallback paths under ~
            string toolIdentifier = toolKind ?? configuredPath;
            string[]? candidates = GetToolCandidates(toolIdentifier, home);

            if (candidates != null)
            {
                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        var writer = output ?? Console.Out;
                        writer.WriteLine($"[RESOLVER] '{configuredPath}' not found in PATH -> using fallback: {candidate}");
                        return candidate;
                    }
                }
            }

            // 4. If none exists (e.g., on Windows or in unit tests with dummy names), return expanded unchanged
            return expanded;
        }

        /// <summary>
        /// Expands a leading ~/ or ~\ to the user's home directory.
        /// </summary>
        public static string ExpandHome(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;

            if (path == "~")
            {
                return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            if (path.StartsWith("~/") || path.StartsWith("~\\"))
            {
                string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                string rest = path.Substring(2)
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                return Path.Combine(home, rest);
            }

            return path;
        }

        /// <summary>
        /// Checks whether a command exists in any directory of the PATH environment variable.
        /// </summary>
        public static string? FindInPath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            // If a full path was already provided and exists:
            if ((command.Contains('/') || command.Contains('\\')) && File.Exists(command))
            {
                return command;
            }

            string filename = Path.GetFileName(command);
            if (string.IsNullOrEmpty(filename)) return null;

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (string.IsNullOrEmpty(pathEnv)) return null;

            string[] dirs = pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
            string[] extensions = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? (Environment.GetEnvironmentVariable("PATHEXT")?.Split(';', StringSplitOptions.RemoveEmptyEntries) ?? new[] { ".exe", ".cmd", ".bat" })
                : new[] { "" };

            foreach (string dir in dirs)
            {
                string baseCandidate = Path.Combine(dir.Trim(), filename);
                if (File.Exists(baseCandidate)) return baseCandidate;

                if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows) && !Path.HasExtension(filename))
                {
                    foreach (string ext in extensions)
                    {
                        string extWithDot = ext.StartsWith('.') ? ext : "." + ext;
                        string withExt = baseCandidate + extWithDot;
                        if (File.Exists(withExt)) return withExt;
                    }
                }
            }

            return null;
        }

        private static string MpichMpiexec(string home) => Path.Combine(home, "miniconda", "envs", "mpich", "bin", "mpiexec");

        private static bool IsBareMpiName(string? toolKind, string expanded)
        {
            if (!string.Equals(toolKind, "mpi", StringComparison.OrdinalIgnoreCase)) return false;
            return string.Equals(expanded, "mpirun", StringComparison.OrdinalIgnoreCase)
                   || string.Equals(expanded, "mpiexec", StringComparison.OrdinalIgnoreCase);
        }

        private static string[]? GetToolCandidates(string identifier, string home)
        {
            string idLower = identifier.ToLowerInvariant();
            if (idLower == "gmsh" || idLower.Contains("gmsh"))
            {
                return new[]
                {
                    Path.Combine(home, "software", "gmsh-4.11.1-Linux64", "bin", "gmsh"),
                    Path.Combine(home, "software", "bin", "gmsh"),
                    Path.Combine(home, "miniconda", "bin", "gmsh")
                };
            }
            if (idLower == "su2" || idLower.Contains("su2"))
            {
                return new[]
                {
                    Path.Combine(home, "software", "bin", "SU2_CFD"),
                    Path.Combine(home, "miniconda", "bin", "SU2_CFD")
                };
            }
            if (idLower == "mpi" || idLower.Contains("mpi"))
            {
                return new[]
                {
                    Path.Combine(home, "miniconda", "envs", "mpich", "bin", "mpiexec"),
                    Path.Combine(home, "miniconda", "bin", "mpirun"),
                    Path.Combine(home, "software", "bin", "mpirun")
                };
            }
            return null;
        }
    }
}
