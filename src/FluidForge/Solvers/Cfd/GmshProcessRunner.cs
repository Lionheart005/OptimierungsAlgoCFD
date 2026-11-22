using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>Result of a Gmsh invocation: exit code and extracted error line(s) from output.</summary>
    internal sealed record GmshRunResult(int ExitCode, string? FirstError)
    {
        /// <summary>For log messages: " (Gmsh: ...)" or empty.</summary>
        public string ErrorSuffix => string.IsNullOrEmpty(FirstError) ? string.Empty : $" (Gmsh: {FirstError})";
    }

    /// <summary>
    /// Unified Gmsh process execution for 2D and 3D including error-log archiving.
    /// </summary>
    internal static class GmshProcessRunner
    {
        /// <summary>
        /// Launches Gmsh with the specified arguments and, when <paramref name="captureOutput"/> is true,
        /// captures the first meaningful error lines for diagnostics.
        /// </summary>
        public static GmshRunResult Run(
            SimulationConfig config,
            string arguments,
            string workingDirectory,
            string scriptForErrorLog,
            bool captureOutput = true)
        {
            using Process mesher = new Process();
            mesher.StartInfo.FileName = ExecutableResolver.Resolve(config.GmshPath, "gmsh");
            mesher.StartInfo.Arguments = arguments;
            mesher.StartInfo.UseShellExecute = false;
            mesher.StartInfo.CreateNoWindow = true;

            var errorLines = new List<string>();
            if (captureOutput)
            {
                mesher.StartInfo.RedirectStandardOutput = true;
                mesher.StartInfo.RedirectStandardError = true;
                DataReceivedEventHandler collect = (_, e) =>
                {
                    if (e.Data == null) return;
                    string clean = GmshErrorParser.StripAnsi(e.Data);
                    if (clean.StartsWith("Error", StringComparison.Ordinal))
                    {
                        lock (errorLines) errorLines.Add(clean);
                    }
                };
                mesher.OutputDataReceived += collect;
                mesher.ErrorDataReceived += collect;
            }

            try
            {
                mesher.Start();
                if (captureOutput)
                {
                    mesher.BeginOutputReadLine();
                    mesher.BeginErrorReadLine();
                }

                mesher.WaitForExit();
                if (!captureOutput)
                    return new GmshRunResult(mesher.ExitCode, null);

                string[] lines;
                lock (errorLines) lines = errorLines.ToArray();
                return new GmshRunResult(mesher.ExitCode, GmshErrorParser.ExtractGmshError(lines));
            }
            catch (Exception ex)
            {
                CopyGeoToErrorLogs(scriptForErrorLog, workingDirectory, "LaunchError");
                throw new Exception($"Failed to start Gmsh: {ex.Message}. .geo script copied to {OutputConventions.ErrorLogsDirectory}.", ex);
            }
        }

        /// <summary>
        /// Defensively copies a failing <c>.geo</c> or <c>.pos</c> script into <c>error-logs/</c>.
        /// </summary>
        public static void CopyGeoToErrorLogs(string geoPath, string workingDirectory, string errorLabel = "")
        {
            try
            {
                string errorLogDir = Path.Combine(workingDirectory, OutputConventions.ErrorLogsDirectory);
                Directory.CreateDirectory(errorLogDir);
                string destGeoPath = Path.Combine(errorLogDir, Path.GetFileName(geoPath));
                if (File.Exists(geoPath))
                {
                    File.Copy(geoPath, destGeoPath, overwrite: true);
                    if (!string.IsNullOrWhiteSpace(errorLabel))
                    {
                        SimulationLog.Detail($"{OutputConventions.ErrorTag} {errorLabel}: copied {Path.GetFileName(geoPath)} to {OutputConventions.ErrorLogsDirectory}.");
                    }
                }
            }
            catch { }
        }
    }
}
