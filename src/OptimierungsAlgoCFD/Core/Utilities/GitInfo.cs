using System;
using System.IO;
using System.Linq;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Liest Commit und Branch direkt aus den Dateien unter <c>.git</c> (TODO-50).
    ///
    /// <para>
    /// Bewusst ohne git-Programm: lokal liegt git nur im Ordner von GitHub Desktop und nicht im
    /// PATH, auf dem Server gibt es gar kein <c>.git</c>. Gelesen werden nur <c>HEAD</c>,
    /// <c>refs/heads/&lt;Branch&gt;</c> und <c>packed-refs</c>. Ob es nicht-committete
    /// Änderungen gibt, lässt sich so nicht sagen — das steht dann als „unbekannt" da.
    /// </para>
    /// </summary>
    public sealed class GitInfo
    {
        public GitInfo(string commit, string? branch)
        {
            Commit = commit;
            Branch = branch;
        }

        /// <summary>Volle Commit-Kennung (40 Hex-Zeichen).</summary>
        public string Commit { get; }

        /// <summary>Branch-Name, <c>null</c> bei abgekoppeltem HEAD.</summary>
        public string? Branch { get; }

        /// <summary>
        /// Liest den Stand des Repositorys in <paramref name="repositoryRoot"/>. <c>null</c>, wenn
        /// es dort kein lesbares <c>.git</c> gibt. Wirft nie.
        /// </summary>
        public static GitInfo? Read(string? repositoryRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(repositoryRoot)) return null;

                string? gitDirectory = ResolveGitDirectory(Path.Combine(repositoryRoot, ".git"));
                if (gitDirectory == null) return null;

                string headFile = Path.Combine(gitDirectory, "HEAD");
                if (!File.Exists(headFile)) return null;

                string head = File.ReadAllText(headFile).Trim();

                const string refPrefix = "ref:";
                if (!head.StartsWith(refPrefix, StringComparison.Ordinal))
                    return IsCommitId(head) ? new GitInfo(head, null) : null;

                string reference = head.Substring(refPrefix.Length).Trim();
                string branch = reference.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? reference.Substring("refs/heads/".Length)
                    : reference;

                string? commit = ReadReference(gitDirectory, reference);
                return commit == null ? null : new GitInfo(commit, branch);
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// <c>.git</c> ist normalerweise ein Ordner. Bei einem Worktree ist es eine Datei mit
        /// <c>gitdir: &lt;Pfad&gt;</c>.
        /// </summary>
        private static string? ResolveGitDirectory(string dotGit)
        {
            if (Directory.Exists(dotGit)) return dotGit;
            if (!File.Exists(dotGit)) return null;

            string content = File.ReadAllText(dotGit).Trim();
            const string prefix = "gitdir:";
            if (!content.StartsWith(prefix, StringComparison.Ordinal)) return null;

            string target = content.Substring(prefix.Length).Trim();
            if (!Path.IsPathRooted(target))
                target = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(dotGit)!, target));

            return Directory.Exists(target) ? target : null;
        }

        private static string? ReadReference(string gitDirectory, string reference)
        {
            string looseFile = Path.Combine(gitDirectory, reference.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(looseFile))
            {
                string loose = File.ReadAllText(looseFile).Trim();
                if (IsCommitId(loose)) return loose;
            }

            // Bei einem Worktree liegen die Branches im gemeinsamen Verzeichnis.
            string commonFile = Path.Combine(gitDirectory, "commondir");
            if (File.Exists(commonFile))
            {
                string common = Path.GetFullPath(Path.Combine(gitDirectory, File.ReadAllText(commonFile).Trim()));
                if (!string.Equals(common, gitDirectory, StringComparison.OrdinalIgnoreCase))
                    return ReadReference(common, reference);
            }

            string packed = Path.Combine(gitDirectory, "packed-refs");
            if (!File.Exists(packed)) return null;

            return File.ReadLines(packed)
                .Where(line => line.Length > 0 && line[0] != '#' && line[0] != '^')
                .Select(line => line.Split(' ', 2))
                .Where(parts => parts.Length == 2 && parts[1].Trim() == reference && IsCommitId(parts[0]))
                .Select(parts => parts[0])
                .FirstOrDefault();
        }

        private static bool IsCommitId(string text)
            => text.Length >= 40 && text.All(Uri.IsHexDigit);
    }
}
