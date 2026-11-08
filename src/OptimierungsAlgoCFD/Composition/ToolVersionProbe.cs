using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Composition
{
    /// <summary>
    /// Fragt die Versionen der externen Programme ab, für den Fahrtenschreiber
    /// <c>run-info.json</c> (TODO-50). Liegt in <c>Composition</c>, weil es Solver und Kernel
    /// beim Namen kennt — <c>Core</c> darf das nicht.
    ///
    /// <para>
    /// <b>Ein fehlendes oder stummes Programm bricht nie den Lauf ab.</b> Jeder Aufruf hat eine
    /// Zeitgrenze und ist in <c>try/catch</c>; was nicht klappt, steht als
    /// <see cref="Unknown"/> da.
    /// </para>
    /// </summary>
    public static class ToolVersionProbe
    {
        public const string Unknown = "unbekannt";

        /// <summary>Zeitgrenze je Programmaufruf.</summary>
        public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

        private static readonly Regex AnyVersion = new Regex(@"(\d+\.\d+(?:\.\d+)?)", RegexOptions.Compiled);

        // SU2 kennt keinen verlässlichen Versionsschalter. Ohne Argumente gibt es das Banner aus
        // ("SU2 v7.5.1 \"Blackbird\"") und beschwert sich dann über die fehlende cfg-Datei.
        private static readonly Regex Su2Version = new Regex(@"SU2.*?v?(\d+\.\d+\.\d+)", RegexOptions.Compiled);

        // Welche MPI-Variante hinter MpiRunPath steckt. Das offizielle SU2-Binary ist mit MPICH gebaut; unter
        // OpenMPI startet "-n 12" zwölf unabhängige serielle Kopien, die sich gegenseitig die Dateien überschreiben
        // (Servertest 06.11.2026) -- ohne jede Fehlermeldung.
        private static readonly Regex MpiFlavor = new Regex(@"(Open MPI|OpenRTE|PRRTE|HYDRA|MPICH)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

        public const string OpenMpi = "OpenMPI";
        public const string Mpich = "MPICH";

        /// <summary>Name → Version für alle Programme des Laufs.</summary>
        public static Dictionary<string, string> Probe(SimulationConfig config)
        {
            return new Dictionary<string, string>
            {
                ["SU2"] = Query(config.Su2Path, "", Su2Version, DefaultTimeout),
                ["Gmsh"] = Query(config.GmshPath, "--version", AnyVersion, DefaultTimeout),
                ["mpirun"] = Query(config.MpiRunPath, "--version", AnyVersion, DefaultTimeout),
                ["MPI-Variante"] = MpiVariant(config.MpiRunPath),
                ["PicoGK"] = PicoGkVersion()
            };
        }

        /// <summary>"OpenMPI", "MPICH" oder <see cref="Unknown"/>.</summary>
        public static string MpiVariant(string? mpiRunPath)
        {
            string found = Query(mpiRunPath, "--version", MpiFlavor, DefaultTimeout);
            if (found == Unknown) return Unknown;
            return found.Contains("Open", StringComparison.OrdinalIgnoreCase)
                   || found.Contains("PRRTE", StringComparison.OrdinalIgnoreCase)
                ? OpenMpi
                : Mpich;
        }

        /// <summary>Meldungstext, wenn die MPI-Variante nicht zum SU2-Binary passt; sonst <c>null</c>.</summary>
        public static string? MpiWarning(IReadOnlyDictionary<string, string> versions, string mpiRunPath)
        {
            if (!versions.TryGetValue("MPI-Variante", out string? variant) || variant != OpenMpi) return null;

            return $"MpiRunPath '{mpiRunPath}' ist OpenMPI. Das offizielle SU2-Binary ist mit MPICH gebaut: unter OpenMPI läuft "
                   + "jeder Rechenkern als eigene serielle Kopie und überschreibt die Dateien der anderen. Die Ergebnisse "
                   + "wären unbrauchbar. In simulation.json 'MpiRunPath' auf das mpiexec des MPICH-Environments setzen "
                   + "(Server: ~/miniconda/envs/mpich/bin/mpiexec, Einrichtung siehe scripts/README.md).";
        }

        /// <summary>
        /// Startet <paramref name="program"/> mit <paramref name="arguments"/> in einem leeren
        /// Temp-Ordner und sucht in stdout und stderr nach <paramref name="pattern"/> (erste Gruppe).
        /// </summary>
        public static string Query(string? program, string arguments, Regex pattern, TimeSpan timeout)
        {
            if (string.IsNullOrWhiteSpace(program)) return Unknown;

            string sandbox = Path.Combine(Path.GetTempPath(), "toolprobe_" + Guid.NewGuid().ToString("N"));

            try
            {
                // Eigener, leerer Arbeitsordner: SU2 ohne Argumente sucht eine default.cfg im
                // aktuellen Ordner — die soll es dort sicher nicht finden.
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
