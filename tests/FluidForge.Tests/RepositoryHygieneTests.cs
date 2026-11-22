using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using FluidForge.Core;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-32: No credentials are in the repository — verified on 2026-11-04 across the
    /// working tree and all 71 commits in history. This test ensures it stays that way.
    ///
    /// <para>
    /// We scan for <b>secrets</b>, not names: server usernames and internal addresses may appear
    /// in the repository (decided on 2026-11-04). A private key, a GitHub token, or a typed
    /// password, however, must never be committed — after a push they could not be retracted,
    /// not even by a follow-up commit that deletes them.
    /// </para>
    /// </summary>
    public class RepositoryHygieneTests
    {
        /// <summary>The folders that belong in the repository and are deployed to the server.</summary>
        private static readonly string[] ScannedFolders = { "src", "scripts", "config" };

        private static readonly string[] TextExtensions =
            { ".cs", ".json", ".ps1", ".sh", ".md", ".txt", ".py", ".geo", ".cfg", ".csproj", ".sln" };

        private static readonly (string Name, Regex Pattern)[] SecretPatterns =
        {
            ("private key",                new Regex(@"-----BEGIN [A-Z ]*PRIVATE KEY-----")),
            ("GitHub token",               new Regex(@"\bghp_[A-Za-z0-9]{30,}")),
            ("GitHub token (fine-grained)", new Regex(@"\bgithub_pat_[A-Za-z0-9_]{20,}")),
            ("AWS access key",             new Regex(@"\bAKIA[0-9A-Z]{16}\b")),
            ("API key (sk-)",              new Regex(@"\bsk-[A-Za-z0-9_-]{32,}")),
            // An assignment with a real value, not the word itself: "enter password"
            // in documentation is harmless, password = "abc123xyz" is not.
            ("plaintext password/token",   new Regex(
                @"(?i)\b(password|passwort|passwd|secret|api[_-]?key|access[_-]?token)\b[""']?\s*[:=]\s*[""'][^""'\s]{6,}[""']")),
        };

        private static string RepoRoot
        {
            get
            {
                string? projectsRoot = ProjectPaths.ResolveProjectsRoot();
                Assert.True(projectsRoot != null, "Directory src/projects/ was not found.");

                // src/projects -> src -> repo root
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

                    // Machine-specific files are gitignored and never enter the repo.
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
                "Possible credentials in repository — remove before next commit:\n  "
                + string.Join("\n  ", findings));
        }

        /// <summary>The test must actually see source files; otherwise it passes without checking anything.</summary>
        [Fact]
        public void The_Scan_Actually_Sees_The_Sources()
        {
            var files = TextFiles().Select(Path.GetFileName).ToList();

            Assert.Contains("Program.cs", files);
            Assert.Contains("sim.ps1", files);
            Assert.Contains("sim-runner.sh", files);
        }

        /// <summary>The patterns must detect real hits — verified with fabricated values.</summary>
        [Theory]
        [InlineData("-----BEGIN OPENSSH PRIVATE KEY-----")]
        [InlineData("token: ghp_abcdefghijklmnopqrstuvwxyz0123456789")]
        [InlineData("\"Password\": \"secret123\"")]
        [InlineData("api_key = 'abcdef123456'")]
        public void The_Patterns_Recognize_Typical_Secrets(string line)
        {
            Assert.Contains(SecretPatterns, entry => entry.Pattern.IsMatch(line));
        }

        /// <summary>And they do not trigger on ordinary documentation.</summary>
        [Theory]
        [InlineData("you would have to enter the password every time")]
        [InlineData("Permission denied (publickey,password)")]
        [InlineData("\"MpiRunPath\": \"/home/exampleuser/miniconda/bin/mpirun\",")]
        public void The_Patterns_Ignore_Ordinary_Text(string line)
        {
            Assert.DoesNotContain(SecretPatterns, entry => entry.Pattern.IsMatch(line));
        }
    }
}
