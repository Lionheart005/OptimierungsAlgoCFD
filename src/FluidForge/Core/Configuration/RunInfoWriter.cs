using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace FluidForge.Core
{
    /// <summary>
    /// Writes and maintains the flight-recorder <c>results/&lt;Project&gt;/run-info.json</c>
    /// and creates the project source snapshot under <c>source-snapshot/</c>.
    ///
    /// <para>
    /// Nothing here may abort a run: maintenance calls (<see cref="Finish"/>,
    /// <see cref="AddResume"/>) catch every exception and only log a warning.
    /// </para>
    /// </summary>
    public static class RunInfoWriter
    {
        public const string FileName = OutputConventions.RunInfoFileName;

        /// <summary>Written by <c>sim.ps1</c> into the server repository root after a successful build.</summary>
        public const string DeployInfoFileName = "deploy-info.json";

        /// <summary>Fingerprint of the uploaded code.</summary>
        public const string DeployHashFileName = ".deploy_hash";

        public const string SourceSnapshotDirectoryName = OutputConventions.SourceSnapshotDirectory;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Timestamp with timezone offset, as recorded in the file.</summary>
        public static string Now()
            => DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        /// <summary>Creates the initial flight-recorder state.</summary>
        public static RunInfo Create(
            string project,
            int seed,
            IEnumerable<string> arguments,
            RunInfoSource source,
            IReadOnlyDictionary<string, string> tools,
            string configurationHash)
        {
            return new RunInfo
            {
                Project = project,
                StartedAt = Now(),
                Status = RunInfo.StatusRunning,
                CommandLine = arguments.ToList(),
                RandomSeed = seed,
                Host = new RunInfoMachine
                {
                    Name = Environment.MachineName,
                    OperatingSystem = RuntimeInformation.OSDescription,
                    ProcessorCount = Environment.ProcessorCount,
                    DotNet = Environment.Version.ToString()
                },
                Source = source,
                Tools = new Dictionary<string, string>(tools),
                ConfigHash = configurationHash
            };
        }

        public static string PathIn(string workingDirectory) => Path.Combine(workingDirectory, FileName);

        /// <summary>Writes the file and returns its path.</summary>
        public static string Write(string workingDirectory, RunInfo info)
        {
            Directory.CreateDirectory(workingDirectory);
            string path = PathIn(workingDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(info, Options));
            return path;
        }

        /// <summary>Reads the file, returning <c>null</c> if missing or unreadable.</summary>
        public static RunInfo? Read(string workingDirectory)
        {
            try
            {
                string path = PathIn(workingDirectory);
                return File.Exists(path) ? JsonSerializer.Deserialize<RunInfo>(File.ReadAllText(path), Options) : null;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>Sets <c>Status</c> and <c>FinishedAt</c> at the end of a run. Never throws.</summary>
        public static void Finish(string workingDirectory, string status)
        {
            Update(workingDirectory, info =>
            {
                info.Status = status;
                info.FinishedAt = Now();
            });
        }

        /// <summary>
        /// Updates a tool's version under <c>Tools</c> if it is missing or <c>"unknown"</c>
        /// (e.g., SU2 has no version flag; its version only appears in the banner of the first run).
        /// Does nothing if <c>run-info.json</c> is missing. Never throws.
        /// </summary>
        public static void SetProgramVersion(string workingDirectory, string program, string version)
        {
            try
            {
                RunInfo? info = Read(workingDirectory);
                if (info == null) return;

                if (info.Tools.TryGetValue(program, out string? known)
                    && !string.IsNullOrWhiteSpace(known)
                    && !known.Equals("unknown", StringComparison.OrdinalIgnoreCase))
                    return;

                info.Tools[program] = version;
                Write(workingDirectory, info);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{OutputConventions.WarningTag} {FileName}: failed to record version of {program}: {exception.Message}");
            }
        }

        /// <summary>
        /// Records a resume event: appends an entry under <c>Resumes</c> and resets
        /// <c>Status</c> back to <c>running</c>. Never throws.
        /// </summary>
        public static void AddResume(string workingDirectory, int storedSimulations)
        {
            Update(workingDirectory, info =>
            {
                info.Resumes.Add(new RunInfoResume { Timestamp = Now(), AdoptedEvaluations = storedSimulations });
                info.Status = RunInfo.StatusRunning;
                info.FinishedAt = null;
            });
        }

        private static void Update(string workingDirectory, Action<RunInfo> change)
        {
            try
            {
                RunInfo? info = Read(workingDirectory);
                if (info == null)
                {
                    Console.WriteLine($"{OutputConventions.WarningTag} {FileName} is missing or unreadable — skipping update.");
                    return;
                }

                change(info);
                Write(workingDirectory, info);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{OutputConventions.WarningTag} {FileName} could not be updated: {exception.Message}");
            }
        }

        /// <summary>
        /// Reads source state: <c>deploy-info.json</c> takes precedence (server, where <c>.git</c>
        /// is absent). Otherwise reads commit and branch from <c>.git</c> and, if present,
        /// the contents of <c>.deploy_hash</c>. Never throws.
        /// </summary>
        public static RunInfoSource ReadSourceState(string? repositoryRoot)
        {
            var source = new RunInfoSource();
            if (string.IsNullOrWhiteSpace(repositoryRoot)) return source;

            try
            {
                string deployInfo = Path.Combine(repositoryRoot, DeployInfoFileName);
                if (File.Exists(deployInfo))
                {
                    var fromDeploy = JsonSerializer.Deserialize<DeployInfo>(File.ReadAllText(deployInfo), Options);
                    if (fromDeploy != null)
                    {
                        source.Commit = Blank(fromDeploy.Commit);
                        source.Branch = Blank(fromDeploy.Branch);
                        source.DeployHash = Blank(fromDeploy.DeployHash);
                        source.DeployedAt = Blank(fromDeploy.DeployedAt);
                        source.UncommittedChanges = Blank(fromDeploy.UncommittedChanges) ?? "unknown";
                        return source;
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{OutputConventions.WarningTag} {DeployInfoFileName} is unreadable: {exception.Message}");
            }

            GitInfo? git = GitInfo.Read(repositoryRoot);
            source.Commit = git?.Commit;
            source.Branch = git?.Branch;

            try
            {
                string hashFile = Path.Combine(repositoryRoot, DeployHashFileName);
                if (File.Exists(hashFile)) source.DeployHash = Blank(File.ReadAllText(hashFile).Trim());
            }
            catch (Exception)
            {
                // informational only
            }

            return source;
        }

        /// <summary>
        /// Copies <c>*.cs</c> and <c>*.json</c> from the project directory (excluding <c>bin/</c> and
        /// <c>obj/</c>) into <c>&lt;results&gt;/source-snapshot/</c>. The folder is recreated per run.
        /// Returns the number of copied files. Never throws.
        /// </summary>
        public static int CopyProjectSources(string? projectDirectory, string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(projectDirectory) || !Directory.Exists(projectDirectory)) return 0;

            try
            {
                string target = Path.Combine(workingDirectory, SourceSnapshotDirectoryName);
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(target);

                int copied = 0;
                string root = Path.GetFullPath(projectDirectory);

                foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                {
                    string extension = Path.GetExtension(file);
                    if (!extension.Equals(".cs", StringComparison.OrdinalIgnoreCase)
                        && !extension.Equals(".json", StringComparison.OrdinalIgnoreCase)) continue;

                    string relative = Path.GetRelativePath(root, file);
                    string[] parts = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                    if (parts.Take(parts.Length - 1).Any(part =>
                            part.Equals("bin", StringComparison.OrdinalIgnoreCase)
                            || part.Equals("obj", StringComparison.OrdinalIgnoreCase))) continue;

                    string destination = Path.Combine(target, relative);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(file, destination, overwrite: true);
                    copied++;
                }

                return copied;
            }
            catch (Exception exception)
            {
                Console.WriteLine($"{OutputConventions.WarningTag} Failed to copy project source snapshot: {exception.Message}");
                return 0;
            }
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>Schema of <c>deploy-info.json</c>.</summary>
        private sealed class DeployInfo
        {
            public string? Commit { get; set; }
            public string? Branch { get; set; }
            public string? DeployHash { get; set; }
            public string? DeployedAt { get; set; }
            public string? UncommittedChanges { get; set; }
        }
    }
}
