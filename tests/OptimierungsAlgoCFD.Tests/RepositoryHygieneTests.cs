using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// TODO-32: Es liegen keine Zugangsdaten im Repo — geprüft am 04.11.2026 über den
    /// Arbeitsstand und alle 71 Commits der Historie. Dieser Test sorgt dafür, dass es so bleibt.
    ///
    /// <para>
    /// Gesucht wird nach <b>Geheimnissen</b>, nicht nach Namen: Server-Benutzername und interne
    /// Adressen dürfen im Repo stehen (so entschieden am 04.11.2026). Ein privater Schlüssel,
    /// ein GitHub-Token oder ein eingetipptes Passwort dagegen nie — die wären nach einem Push
    /// nicht mehr zurückzuholen, auch nicht durch einen Commit, der sie wieder löscht.
    /// </para>
    /// </summary>
    public class RepositoryHygieneTests
    {
        /// <summary>Die Ordner, die ins Repo gehören und auf den Server deployt werden.</summary>
        private static readonly string[] ScannedFolders = { "src", "scripts", "config" };

        private static readonly string[] TextExtensions =
            { ".cs", ".json", ".ps1", ".sh", ".md", ".txt", ".py", ".geo", ".cfg", ".csproj", ".sln" };

        private static readonly (string Name, Regex Pattern)[] SecretPatterns =
        {
            ("privater Schlüssel",   new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
            ("GitHub-Token",         new Regex(@"\bghp_[A-Za-z0-9]{30,}")),
            ("GitHub-Token (fein)",  new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}")),
            ("AWS-Zugangsschlüssel", new Regex(@"\bAKIA[0-9A-Z]{16}\b")),
            ("API-Schlüssel (sk-)",  new Regex(@"\bsk-[A-Za-z0-9_-]{32,}")),
            // Eine Zuweisung mit einem echten Wert, nicht das Wort an sich: "Passwort eingeben"
            // in einer Anleitung ist harmlos, password = "abc123xyz" nicht.
            ("Passwort/Token im Klartext", new Regex(
                @"(?i)\b(password|passwort|passwd|secret|api[_-]?key|access[_-]?token)\b[""']?\s*[:=]\s*[""'][^""'\s]{6,}[""']")),
        };

        private static string RepoRoot
        {
            get
            {
                string? projectsRoot = ProjectPaths.ResolveProjectsRoot();
                Assert.True(projectsRoot != null, "Das Verzeichnis src/projects/ wurde nicht gefunden.");

                // src/projects -> src -> Repo-Wurzel
                return Path.GetDirectoryName(Path.GetDirectoryName(projectsRoot)!)!;
            }
        }

        private static IEnumerable<string> TextFiles()
        {
            string root = RepoRoot;

            foreach (string folder in ScannedFolders)
            {
                string full = Path.Combine(root, folder);
                if (!Directory.Exists(full)) continue;

                foreach (string file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
                {
                    string normalized = file.Replace('\\', '/');
                    if (normalized.Contains("/bin/") || normalized.Contains("/obj/")) continue;

                    // Maschinenspezifische Dateien sind gitignored und gehen nie ins Repo.
                    if (JsonConfigLoader.IsMachineSpecific(file)) continue;

                    if (TextExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                        yield return file;
                }
            }
        }

        [Fact]
        public void No_Secrets_Are_Checked_In()
        {
            var findings = new List<string>();

            foreach (string file in TextFiles())
            {
                string[] lines = File.ReadAllLines(file);

                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (var (name, pattern) in SecretPatterns)
                    {
                        if (pattern.IsMatch(lines[i]))
                            findings.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1} ({name})");
                    }
                }
            }

            Assert.True(findings.Count == 0,
                "Mögliche Zugangsdaten im Repo — vor dem nächsten Commit entfernen:\n  "
                + string.Join("\n  ", findings));
        }

        /// <summary>Der Test muss überhaupt etwas zu sehen bekommen, sonst ist er grün, ohne zu prüfen.</summary>
        [Fact]
        public void The_Scan_Actually_Sees_The_Sources()
        {
            var files = TextFiles().Select(Path.GetFileName).ToList();

            Assert.Contains("Program.cs", files);
            Assert.Contains("sim.ps1", files);
            Assert.Contains("sim-runner.sh", files);
        }

        /// <summary>Die Muster müssen echte Treffer auch erkennen — geprüft an erfundenen Werten.</summary>
        [Theory]
        [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
        [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
        [InlineData("\"Password\": \"geheim123\"")]
        [InlineData("api_key = 'abcdef123456'")]
        public void The_Patterns_Recognize_Typical_Secrets(string line)
        {
            Assert.Contains(SecretPatterns, entry => entry.Pattern.IsMatch(line));
        }

        /// <summary>Und sie schlagen bei normaler Doku nicht an.</summary>
        [Theory]
        [InlineData("müsstest du jedes Mal das Passwort eingeben")]
        [InlineData("Permission denied (publickey,password)")]
        [InlineData("\"MpiRunPath\": \"/home/lpleissner/miniconda/bin/mpirun\",")]
        public void The_Patterns_Ignore_Ordinary_Text(string line)
        {
            Assert.DoesNotContain(SecretPatterns, entry => entry.Pattern.IsMatch(line));
        }
    }
}
