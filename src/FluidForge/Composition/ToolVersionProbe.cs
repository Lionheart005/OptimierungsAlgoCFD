using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using FluidForge.Core;

namespace FluidForge.Composition
{
    /// <summary>
    /// Queries the versions of external tools for the flight recorder
    /// <c>run-info.json</c> (TODO-50). Lives in <c>Composition</c> because it knows solvers
    /// and kernels by name — <c>Core</c> must not depend on them.
    ///
    /// <para>
    /// <b>A missing or unresponsive program never aborts the run.</b> Every invocation has a
    /// timeout and is wrapped in <c>try/catch</c>; anything that fails is recorded as
    /// <see cref="Unknown"/>.
    /// </para>
    /// </summary>
    public static class ToolVersionProbe
    {
        public const string Unknown = "unknown";

        /// <summary>Timeout per tool invocation.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        private static readonly Regex AnyVersion = new Regex(@"(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

        // SU2 has no reliable version flag. With no arguments it prints its banner
        // ("SU2 v7.5.1 \"Blackbird\"") and then complains about the missing cfg file.
        private static readonly Regex Su2Version = new Regex(@"SU2.*?v?(\d+\.\d+\.\d+)", RegexOptions.Compiled);

        // Identifies which MPI flavor is behind MpiRunPath. The official SU2 binary is built with MPICH;
        // under OpenMPI "-n 12" launches twelve independent serial copies that overwrite each other's files
        // (server test 06.11.2026) -- without any error message.
        private static readonly Regex MpiFlavor = new Regex(@"(Open MPI|OpenRTE|PRRTE|HYDRA|MPICH)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public const string OpenMpi = "OpenMPI";
        public const string Mpich = "MPICH";

        /// <summary>Maps tool name to version for all tools used in the run.</summary>
        public static Dictionary<string, string> Probe(SimulationConfig config)
        {
            // Uses the exact same resolution as execution (tilde, MPICH environment, fallbacks),
            // otherwise a different binary might be probed than actually launched.
            string mpi = ExecutableResolver.Resolve(config.MpiRunPath, "mpi", TextWriter.Null);

            return new Dictionary<string, string>
            {
                ["SU2"] = Query(config.Su2Path, "", Su2Version, DefaultTimeout),
                ["Gmsh"] = Query(config.GmshPath, "--version", AnyVersion, DefaultTimeout),
                ["mpirun"] = Query(mpi, "--version", AnyVersion, DefaultTimeout),
                ["MPI-Variant"] = MpiVariant(mpi),
                ["PicoGK"] = PicoGkVersion()
            };
        }

        /// <summary>"OpenMPI", "MPICH", or <see cref="Unknown"/>.</summary>
        public static string MpiVariant(string? mpiRunPath)
        {
            string found = Query(mpiRunPath, "--version", MpiFlavor, DefaultTimeout);
            if (found == Unknown) return Unknown;
            return found.Contains("Open", StringComparison.OrdinalIgnoreCase)
                   || found.Contains("PRRTE", StringComparison.OrdinalIgnoreCase)
                ? OpenMpi
                : Mpich;
        }

        /// <summary>Warning message if the MPI variant does not match the SU2 binary; otherwise <c>null</c>.</summary>
        public static string? MpiWarning(IReadOnlyDictionary<string, string> versions, string mpiRunPath)
        {
            if (!versions.TryGetValue("MPI-Variant", out string? variant) || variant != OpenMpi) return null;

            return $"MpiRunPath '{mpiRunPath}' is OpenMPI. The official SU2 binary is built with MPICH: under OpenMPI "
                   + "each compute rank runs as a separate serial copy and overwrites the other ranks' files. Results "
                   + "would be unusable. Set 'MpiRunPath' in simulation.json to the MPICH environment's mpiexec "
                   + "(server: ~/miniconda/envs/mpich/bin/mpiexec, see scripts/README.md for setup).";
        }

        /// <summary>
        /// Launches <paramref name="program"/> with <paramref name="arguments"/> in an empty temp
        /// directory and searches stdout and stderr for <paramref name="pattern"/> (first capture group).
        /// </summary>
        public static string Query(string? program, string arguments, Regex pattern, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(program)) return Unknown;

            string sandbox = Path.Combine(Path.GetTempPath(), "toolprobe_" + Guid.NewGuid().ToString("N"));

            try
            {
                // Dedicated empty working directory: SU2 without arguments looks for a default.cfg
                // in the current directory — ensure it never finds one there.
                Directory.CreateDirectory(sandbox);

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = program,
                        Arguments = arguments,
                        WorkingDirectory = sandbox,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        RedirectStandardInput = true,
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };

                if (!process.Start()) return Unknown;
                process.StandardInput.Close();

                Task<string> stdout = process.StandardOutput.ReadToEndAsync();
                Task<string> stderr = process.StandardError.ReadToEndAsync();

                if (!process.WaitForExit((int)timeout.TotalMilliseconds))
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { }
                }

                var output = new StringBuilder();
                if (stdout.Wait(TimeSpan.FromSeconds(2))) output.AppendLine(stdout.Result);
                if (stderr.Wait(TimeSpan.FromSeconds(2))) output.AppendLine(stderr.Result);

                Match match = pattern.Match(output.ToString());
                return match.Success ? match.Groups[1].Value : Unknown;
            }
            catch (Exception)
            {
                return Unknown;
            }
            finally
            {
                try { if (Directory.Exists(sandbox)) Directory.Delete(sandbox, recursive: true); } catch (Exception) { }
            }
        }

        private static string PicoGkVersion()
        {
            try
            {
                Version? version = typeof(PicoGK.Library).Assembly.GetName().Version;
                return version == null ? Unknown : $"{version.Major}.{version.Minor}.{version.Build}";
            }
            catch (Exception)
            {
                return Unknown;
            }
        }
    }
}
