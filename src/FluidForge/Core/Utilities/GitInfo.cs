using System;
using System.IO;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Reads commit and branch directly from the files under <c>.git</c>.
    ///
    /// <para>
    /// Intentionally avoids spawning a git process: locally git may not be in PATH, and on the
    /// server <c>.git</c> does not exist at all. Reads only <c>HEAD</c>,
    /// <c>refs/heads/&lt;Branch&gt;</c>, and <c>packed-refs</c>. Uncommitted changes cannot
    /// be determined this way and are therefore reported as "unknown".
    /// </para>
    /// </summary>
    public sealed class GitInfo
    {
        public GitInfo(string commit, string? branch)
        {
            Commit = commit;
            Branch = branch;
        }

        /// <summary>Full commit hash (40 hex characters).</summary>
        public string Commit { get; }

        /// <summary>Branch name, or <c>null</c> for a detached HEAD.</summary>
        public string? Branch { get; }

        /// <summary>
        /// Reads repository state in <paramref name="repositoryRoot"/>. Returns <c>null</c> if
        /// no readable <c>.git</c> exists there. Never throws.
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
        /// <c>.git</c> is normally a directory; in a worktree it is a file containing
        /// <c>gitdir: &lt;path&gt;</c>.
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

            // In a worktree, refs live in the common git directory.
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
