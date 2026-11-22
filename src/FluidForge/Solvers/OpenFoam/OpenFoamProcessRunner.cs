using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluidForge.Core;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Output from an OpenFOAM command invocation.
    /// </summary>
    public sealed record OpenFoamCommandResult(int ExitCode, string StdOut, string StdErr)
    {
        /// <summary>Combined stdout and stderr output.</summary>
        public string CombinedOutput => string.Concat(
            StdOut ?? string.Empty,
            !string.IsNullOrEmpty(StdOut) && !string.IsNullOrEmpty(StdErr) ? Environment.NewLine : string.Empty,
            StdErr ?? string.Empty);
    }

    /// <summary>
    /// Abstraction over OpenFOAM shell execution for unit testing without an OpenFOAM installation.
    /// </summary>
    public interface IOpenFoamCommandRunner
    {
        OpenFoamCommandResult Run(string workingDirectory, string command, OpenFoamOptions options, int mpiCores = 1);
    }

    /// <summary>
    /// Executes OpenFOAM tools in an isolated bash environment stripped of Conda/MPICH entries
    /// so system OpenMPI links cleanly with ESI OpenFOAM (<c>openfoam2506</c>).
    /// </summary>
    public sealed class OpenFoamProcessRunner : IOpenFoamCommandRunner
    {
        private static readonly string[] ForbiddenPathFragments = { "miniconda", "anaconda", "conda", "mpich" };

        private static readonly Regex VersionRegex = new(
            @"(?:^|\s)(v?\d{4}|\d+\.\d+(?:\.\d+)?)(?:\s|$)",
            RegexOptions.Compiled);

        /// <summary>
        /// Removes any colon-separated PATH/LD_LIBRARY_PATH entries containing Conda or MPICH fragments.
        /// </summary>
        public static string StripCondaMpiFromPath(string? pathValue)
        {
            if (string.IsNullOrWhiteSpace(pathValue))
                return string.Empty;

            IEnumerable<string> kept = pathValue
                .Split(':', StringSplitOptions.RemoveEmptyEntries)
                .Select(segment => segment.Trim())
                .Where(segment => segment.Length > 0 && !ContainsForbiddenMpiFragment(segment));

            return string.Join(":", kept);
        }

        private static bool ContainsForbiddenMpiFragment(string segment)
        {
            foreach (string fragment in ForbiddenPathFragments)
            {
                if (segment.Contains(fragment, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        /// <summary>
        /// Resolves the OpenFOAM <c>etc/bashrc</c> path: explicit option first, then standard
        /// <c>/usr/lib/openfoam/openfoam2506/etc/bashrc</c>, then wildcard <c>openfoam*</c>,
        /// then <c>$HOME/miniconda/envs/openfoam/etc/bashrc</c>.
        /// </summary>
        public static string ResolveEffectiveBashrcPath(
            OpenFoamOptions options,
            string? homeOverride = null,
            Func<string, bool>? fileExists = null,
            Func<string, IEnumerable<string>>? directoryEnumerator = null)
        {
            ArgumentNullException.ThrowIfNull(options);
            Func<string, bool> exists = fileExists ?? File.Exists;
            Func<string, IEnumerable<string>> enumDirs = directoryEnumerator ?? (dir =>
                Directory.Exists(dir) ? Directory.EnumerateDirectories(dir, "openfoam*") : Array.Empty<string>());

            if (!string.IsNullOrWhiteSpace(options.OpenFoamBashrcPath))
            {
                return ExpandUnixHome(options.OpenFoamBashrcPath.Trim(), homeOverride);
            }

            const string primary = "/usr/lib/openfoam/openfoam2506/etc/bashrc";
            if (exists(primary))
                return primary;

            foreach (string dir in enumDirs("/usr/lib/openfoam").OrderByDescending(d => d, StringComparer.Ordinal))
            {
                string candidate = $"{dir.TrimEnd('/')}/etc/bashrc";
                if (exists(candidate))
                    return candidate;
            }

            string home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(home))
            {
                string condaOpenFoam = $"{home.TrimEnd('/', '\\').Replace('\\', '/')}/miniconda/envs/openfoam/etc/bashrc";
                if (exists(condaOpenFoam))
                    return condaOpenFoam;
            }

            return primary;
        }

        /// <summary>
        /// Resolves the MPI launcher path for OpenFOAM (always avoiding Conda/MPICH).
        /// </summary>
        public static string ResolveMpiRunPath(OpenFoamOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            string configured = (options.MpiRunPath ?? string.Empty).Trim();
            if (configured.Length == 0 || ContainsForbiddenMpiFragment(configured))
                return "/usr/bin/mpirun";
            return configured;
        }

        /// <summary>
        /// Builds the serial or <c>mpirun -np &lt;cores&gt; ... -parallel</c> invocation string.
        /// </summary>
        public static string BuildParallelToolCommand(
            string tool,
            string? arguments,
            OpenFoamOptions options,
            int mpiCores)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(tool);
            ArgumentNullException.ThrowIfNull(options);

            string trimmedTool = tool.Trim();
            string trimmedArgs = string.IsNullOrWhiteSpace(arguments) ? string.Empty : " " + arguments.Trim();

            if (mpiCores <= 1)
                return $"{trimmedTool}{trimmedArgs}";

            string mpiRun = ResolveMpiRunPath(options);
            return string.Create(
                CultureInfo.InvariantCulture,
                $"\"{mpiRun}\" -np {mpiCores} {trimmedTool}{trimmedArgs} -parallel");
        }

        /// <summary>
        /// Builds a self-contained bash script that strips Conda/MPICH from <c>PATH</c> and
        /// <c>LD_LIBRARY_PATH</c>, ensures standard system paths, sources OpenFOAM's <c>etc/bashrc</c>,
        /// sets OpenMPI/hwloc guards, and executes <paramref name="command"/>.
        /// </summary>
        public static string BuildIsolatedBashScript(
            string command,
            OpenFoamOptions options,
            int mpiCores = 1,
            string? homeOverride = null,
            Func<string, bool>? fileExists = null,
            Func<string, IEnumerable<string>>? directoryEnumerator = null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ArgumentNullException.ThrowIfNull(options);

            string bashrcPath = ResolveEffectiveBashrcPath(options, homeOverride, fileExists, directoryEnumerator);
            string sanitizedCommand = command.Trim();

            // Strip Conda/MPICH, restore system paths, source OpenFOAM bashrc with set +u/set -u, and set OpenMPI guards.
            var sb = new StringBuilder();
            sb.AppendLine("export PATH=\"$(printf '%s' \"${PATH:-}\" | tr ':' '\\n' | grep -viE '(miniconda|anaconda|conda|mpich)' | paste -sd: -)\"");
            sb.AppendLine("export LD_LIBRARY_PATH=\"$(printf '%s' \"${LD_LIBRARY_PATH:-}\" | tr ':' '\\n' | grep -viE '(miniconda|anaconda|conda|mpich)' | paste -sd: -)\"");
            sb.AppendLine("export PATH=\"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH\"");
            sb.AppendLine("set +u");
            sb.AppendLine($"source \"{EscapeDoubleQuoted(bashrcPath)}\"");
            sb.AppendLine("set -u");
            sb.AppendLine("export HWLOC_HIDE_ERRORS=1");
            sb.AppendLine("export OMPI_MCA_btl_vader_single_copy_mechanism=none");
            sb.Append(sanitizedCommand);
            return sb.ToString();
        }

        /// <summary>
        /// Probes the active OpenFOAM installation's <c>WM_PROJECT_VERSION</c>, returning <c>null</c> on failure.
        /// </summary>
        public static string? DetectOpenFoamVersion(
            IOpenFoamCommandRunner runner,
            OpenFoamOptions options,
            string workingDirectory)
        {
            ArgumentNullException.ThrowIfNull(runner);
            ArgumentNullException.ThrowIfNull(options);

            try
            {
                OpenFoamCommandResult result = runner.Run(
                    workingDirectory,
                    "printf '%s' \"${WM_PROJECT_VERSION:-}\"",
                    options,
                    mpiCores: 1);

                if (result.ExitCode != 0)
                    return null;

                string output = result.StdOut?.Trim() ?? string.Empty;
                if (output.Length == 0)
                    return null;

                Match m = VersionRegex.Match(output);
                return m.Success ? m.Groups[1].Value : output.Split('\n')[0].Trim();
            }
            catch
            {
                return null;
            }
        }

        /// <inheritdoc />
        public OpenFoamCommandResult Run(string workingDirectory, string command, OpenFoamOptions options, int mpiCores = 1)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
            ArgumentException.ThrowIfNullOrWhiteSpace(command);
            ArgumentNullException.ThrowIfNull(options);

            string script = BuildIsolatedBashScript(command, options, mpiCores);

            var startInfo = new ProcessStartInfo
            {
                FileName = "bash",
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-c");
            startInfo.ArgumentList.Add(script);

            if (startInfo.Environment.TryGetValue("PATH", out string? pathEnv))
                startInfo.Environment["PATH"] = StripCondaMpiFromPath(pathEnv);
            if (startInfo.Environment.TryGetValue("LD_LIBRARY_PATH", out string? ldEnv))
                startInfo.Environment["LD_LIBRARY_PATH"] = StripCondaMpiFromPath(ldEnv);

            using var process = new Process { StartInfo = startInfo };
            var stdout = new StringBuilder();
            var stderr = new StringBuilder();
            object sync = new object();

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (sync) { stdout.AppendLine(e.Data); }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data != null) lock (sync) { stderr.AppendLine(e.Data); }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            process.WaitForExit();

            return new OpenFoamCommandResult(process.ExitCode, stdout.ToString(), stderr.ToString());
        }

        private static string ExpandUnixHome(string path, string? homeOverride)
        {
            if (path.StartsWith("~/", StringComparison.Ordinal))
            {
                string home = homeOverride ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrWhiteSpace(home))
                    return $"{home.TrimEnd('/', '\\').Replace('\\', '/')}/{path.Substring(2)}";
            }
            return path;
        }

        private static string EscapeDoubleQuoted(string value)
            => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
    }
}
