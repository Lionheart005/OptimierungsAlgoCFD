using System;
using System.IO;
using System.Runtime.InteropServices;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Löst Pfade zu externen Werkzeugen (SU2, Gmsh, MPI) auf.
    /// Unterstützt Tilde-Expansion (~/), Prüfung gegen PATH und Fallback auf Standard-Installationspfade.
    /// </summary>
    public static class ExecutableResolver
    {
        /// <summary>
        /// Löst einen konfigurierten Programmpfad auf.
        /// </summary>
        /// <param name="configuredPath">Der aus der Konfiguration gelesene Pfad oder Befehlsname.</param>
        /// <param name="toolKind">Optional: Werkzeug-Art ("gmsh", "su2", "mpi"). Wenn null, wird sie aus dem Namen abgeleitet.</param>
        /// <param name="output">Optional: Ausgabe-Writer für Log-Meldungen bei Fallbacks (Standard: Console.Out).</param>
        /// <param name="homeOverride">Nur für Tests: Ersatz für das Benutzerverzeichnis.</param>
        /// <returns>Der aufgelöste Pfad oder Befehl.</returns>
        public static string Resolve(string configuredPath, string? toolKind = null, TextWriter? output = null, string? homeOverride = null)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
                return configuredPath;

            string expanded = ExpandHome(configuredPath);
            string home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            // MPI: Das offizielle SU2-Binary ist mit MPICH gebaut, unter OpenMPI startet jeder Kern eine eigene serielle
            // Kopie. Heißt das Programm nur "mpirun" oder "mpiexec", gewinnt deshalb das MPICH-Environment, wenn es
            // existiert (legt setup_server.sh an), noch vor dem PATH, in dem auf dem Server das OpenMPI liegt.
            // Ein ausdrücklich eingetragener Pfad bleibt unangetastet.
            if (IsBareMpiName(toolKind, expanded))
            {
                string mpich = MpichMpiexec(home);
                if (File.Exists(mpich))
                {
                    (output ?? Console.Out).WriteLine($"[RESOLVER] '{configuredPath}' -> MPICH-Environment: {mpich}");
                    return mpich;
                }
            }

            // 1. Wenn der Pfad Pfadtrenner enthält und File.Exists(expanded) wahr ist -> direkt zurückgeben
            bool hasPathSeparators = expanded.Contains('/') || expanded.Contains('\\');
            if (hasPathSeparators && File.Exists(expanded))
            {
                return expanded;
            }

            // 2. Wenn der Befehl über PATH gefunden wird -> gib expanded zurück
            if (FindInPath(expanded) != null)
            {
                return expanded;
            }

            // 3. Andernfalls Standard-Server-Fallback-Pfade unter ~ prüfen
            string toolIdentifier = toolKind ?? configuredPath;
            string[]? candidates = GetToolCandidates(toolIdentifier, home);

            if (candidates != null)
            {
                foreach (string candidate in candidates)
                {
                    if (File.Exists(candidate))
                    {
                        var writer = output ?? Console.Out;
                        writer.WriteLine($"[RESOLVER] '{configuredPath}' nicht im PATH gefunden -> verwende Fallback: {candidate}");
                        return candidate;
                    }
                }
            }

            // 4. Wenn keiner existiert (z.B. unter Windows oder in Unit-Tests mit Dummy-Namen), expanded unverändert zurückgeben
            return expanded;
        }

        /// <summary>
        /// Expandiert führendes ~/ oder ~\ zum Benutzerverzeichnis.
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
        /// Prüft, ob ein Befehl in den Verzeichnissen der PATH-Umgebungsvariable liegt.
        /// </summary>
        public static string? FindInPath(string command)
        {
            if (string.IsNullOrWhiteSpace(command)) return null;

            // Falls bereits ein vollständiger Pfad übergeben wurde und existiert:
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
