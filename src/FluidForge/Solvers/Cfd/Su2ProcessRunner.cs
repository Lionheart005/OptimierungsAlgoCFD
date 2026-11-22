using System;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Startet den SU2-Prozess (wahlweise per MPI oder direkt im seriellen/OpenMP-Modus)
    /// und liest die SU2-Version aus der Konsolenausgabe.
    /// </summary>
    internal static class Su2ProcessRunner
    {
        private static readonly Regex VersionBanner = new(
            @"Release\s+(\d+\.\d+(?:\.\d+)?)|SU2\s+v(\d+\.\d+(?:\.\d+)?)",
            RegexOptions.Compiled);

        /// <summary>
        /// Bestimmt Programmname, Argumente und ob MPI verwendet wird.
        /// Wenn <see cref="SimulationConfig.MpiRunPath"/> leer/whitespace/"none" oder
        /// <see cref="SimulationConfig.MpiCores"/> &lt;= 1 ist, wird SU2 direkt gestartet.
        /// </summary>
        internal static (string FileName, string Arguments, bool UsesMpi) BuildExecutionCommand(
            SimulationConfig config,
            string configFileName = "current_config.cfg")
        {
            if (config == null) throw new ArgumentNullException(nameof(config));

            string resolvedSu2 = ExecutableResolver.Resolve(config.Su2Path, "su2");
            bool directMode = string.IsNullOrWhiteSpace(config.MpiRunPath)
                || string.Equals(config.MpiRunPath.Trim(), "none", StringComparison.OrdinalIgnoreCase)
                || config.MpiCores <= 1;

            if (directMode)
            {
                return (resolvedSu2, configFileName, false);
            }

            string resolvedMpi = ExecutableResolver.Resolve(config.MpiRunPath, "mpi");
            return (resolvedMpi, $"-n {config.MpiCores} {resolvedSu2} {configFileName}", true);
        }

        /// <summary>
        /// Erstellt die <see cref="ProcessStartInfo"/> für SU2 und setzt im OpenMP-Direct-Mode
        /// (<c>UsesMpi == false</c> und <c>MpiCores &gt; 1</c>) <c>OMP_NUM_THREADS</c>,
        /// sofern diese Umgebungsvariable noch nicht gesetzt ist.
        /// </summary>
        internal static ProcessStartInfo CreateStartInfo(
            string workingDirectory,
            SimulationConfig config,
            string configFileName = "current_config.cfg")
        {
            var (fileName, arguments, usesMpi) = BuildExecutionCommand(config, configFileName);

            var startInfo = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };

            if (!usesMpi && config.MpiCores > 1)
            {
                if (!startInfo.Environment.TryGetValue("OMP_NUM_THREADS", out string? existing)
                    || string.IsNullOrWhiteSpace(existing))
                {
                    startInfo.Environment["OMP_NUM_THREADS"] = config.MpiCores.ToString(CultureInfo.InvariantCulture);
                }
            }

            return startInfo;
        }

        internal static (int ExitCode, string CombinedOutput) ExecuteSu2Process(
            string workingDirectory,
            SimulationConfig config,
            Func<string, int, (int ExitCode, string StdOut, string StdErr)>? runSu2Override = null)
        {
            if (runSu2Override != null)
            {
                var (exitCode, stdOut, stdErr) = runSu2Override(workingDirectory, config.MpiCores);
                string combined = string.Concat(
                    stdOut ?? string.Empty,
                    !string.IsNullOrEmpty(stdOut) && !string.IsNullOrEmpty(stdErr) ? Environment.NewLine : string.Empty,
                    stdErr ?? string.Empty);
                return (exitCode, combined);
            }

            using Process su2Process = new Process
            {
                StartInfo = CreateStartInfo(workingDirectory, config)
            };

            var su2Output = new StringBuilder();
            object outputLock = new object();

            su2Process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) lock (outputLock) { su2Output.AppendLine(e.Data); }
            };
            su2Process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) lock (outputLock) { su2Output.AppendLine(e.Data); }
            };

            su2Process.Start();
            su2Process.BeginOutputReadLine();
            su2Process.BeginErrorReadLine();
            su2Process.WaitForExit();

            return (su2Process.ExitCode, su2Output.ToString());
        }

        /// <summary>
        /// Liest die SU2-Version aus dem Startbanner der Konsolenausgabe
        /// (<c>Release 7.5.1 "Blackbird"</c> bzw. <c>SU2 v7.5.1</c>); <c>null</c>, wenn keines vorkommt.
        /// </summary>
        public static string? TryParseVersion(string? consoleOutput)
        {
            if (string.IsNullOrEmpty(consoleOutput)) return null;
            var match = VersionBanner.Match(consoleOutput);
            if (!match.Success) return null;
            return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }
    }
}
