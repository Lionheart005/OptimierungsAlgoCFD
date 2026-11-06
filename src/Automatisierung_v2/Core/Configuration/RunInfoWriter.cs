using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Schreibt und pflegt den Fahrtenschreiber <c>Ergebnisse/&lt;Projekt&gt;/run-info.json</c>
    /// und legt die Kopie des Projektcodes unter <c>Quellstand/</c> an (TODO-50, F24).
    ///
    /// <para>
    /// Nichts hier darf einen Lauf abbrechen: Pflege-Aufrufe (<see cref="Finish"/>,
    /// <see cref="AddResume"/>) fangen jeden Fehler ab und melden ihn nur.
    /// </para>
    /// </summary>
    public static class RunInfoWriter
    {
        public const string FileName = "run-info.json";

        /// <summary>Schreibt <c>sim.ps1</c> nach erfolgreichem Build ins Repo-Verzeichnis des Servers.</summary>
        public const string DeployInfoFileName = "deploy-info.json";

        /// <summary>Fingerabdruck des hochgeladenen Codes (gibt es seit TODO-28).</summary>
        public const string DeployHashFileName = ".deploy_hash";

        public const string SourceSnapshotDirectoryName = "Quellstand";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Zeitstempel mit Zeitzone, wie er in der Datei steht.</summary>
        public static string Now()
            => DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture);

        /// <summary>Baut den Anfangsstand des Fahrtenschreibers.</summary>
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
                Projekt = project,
                Start = Now(),
                Status = RunInfo.StatusRunning,
                Aufruf = arguments.ToList(),
                ZufallsSeed = seed,
                Rechner = new RunInfoMachine
                {
                    Name = Environment.MachineName,
                    Betriebssystem = RuntimeInformation.OSDescription,
                    Prozessorkerne = Environment.ProcessorCount,
                    DotNet = Environment.Version.ToString()
                },
                Quellstand = source,
                Programme = new Dictionary<string, string>(tools),
                KonfigurationsHash = configurationHash
            };
        }

        public static string PathIn(string workingDirectory) => Path.Combine(workingDirectory, FileName);

        /// <summary>Schreibt die Datei und gibt ihren Pfad zurück.</summary>
        public static string Write(string workingDirectory, RunInfo info)
        {
            Directory.CreateDirectory(workingDirectory);
            string path = PathIn(workingDirectory);
            File.WriteAllText(path, JsonSerializer.Serialize(info, Options));
            return path;
        }

        /// <summary>Liest die Datei, <c>null</c> wenn sie fehlt oder unlesbar ist.</summary>
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

        /// <summary>Setzt am Ende <c>Status</c> und <c>Ende</c>. Wirft nie.</summary>
        public static void Finish(string workingDirectory, string status)
        {
            Update(workingDirectory, info =>
            {
                info.Status = status;
                info.Ende = Now();
            });
        }

        /// <summary>
        /// Vermerkt eine Wiederaufnahme (TODO-52): Eintrag unter <c>Wiederaufnahmen</c>,
        /// <c>Status</c> wieder auf <c>laeuft</c>. Wirft nie.
        /// </summary>
        public static void AddResume(string workingDirectory, int storedSimulations)
        {
            Update(workingDirectory, info =>
            {
                info.Wiederaufnahmen.Add(new RunInfoResume { Zeit = Now(), UebernommeneRechnungen = storedSimulations });
                info.Status = RunInfo.StatusRunning;
                info.Ende = null;
            });
        }

        private static void Update(string workingDirectory, Action<RunInfo> change)
        {
            try
            {
                RunInfo? info = Read(workingDirectory);
                if (info == null)
                {
                    Console.WriteLine($"[WARNUNG] {FileName} fehlt oder ist unlesbar — Eintrag übersprungen.");
                    return;
                }

                change(info);
                Write(workingDirectory, info);
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[WARNUNG] {FileName} konnte nicht aktualisiert werden: {exception.Message}");
            }
        }

        /// <summary>
        /// Code-Stand: <c>deploy-info.json</c> hat Vorrang (Server, dort gibt es kein
        /// <c>.git</c>). Sonst Commit und Branch aus <c>.git</c> und, falls vorhanden, der
        /// Inhalt von <c>.deploy_hash</c>. Wirft nie.
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
                        source.DeployZeit = Blank(fromDeploy.DeployZeit);
                        source.UncommittedChanges = Blank(fromDeploy.UncommittedChanges) ?? "unbekannt";
                        return source;
                    }
                }
            }
            catch (Exception exception)
            {
                Console.WriteLine($"[WARNUNG] {DeployInfoFileName} ist unlesbar: {exception.Message}");
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
                // nur Information
            }

            return source;
        }

        /// <summary>
        /// Kopiert <c>*.cs</c> und <c>*.json</c> des Projektordners (ohne <c>bin/</c> und
        /// <c>obj/</c>) nach <c>&lt;Ergebnisse&gt;/Quellstand/</c>. Der Ordner wird je Lauf neu
        /// angelegt. Gibt die Zahl der kopierten Dateien zurück. Wirft nie.
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
                Console.WriteLine($"[WARNUNG] Kopie des Quellstands fehlgeschlagen: {exception.Message}");
                return 0;
            }
        }

        private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        /// <summary>Aufbau von <c>deploy-info.json</c>.</summary>
        private sealed class DeployInfo
        {
            public string? Commit { get; set; }
            public string? Branch { get; set; }
            public string? DeployHash { get; set; }
            public string? DeployZeit { get; set; }
            public string? UncommittedChanges { get; set; }
        }
    }
}
