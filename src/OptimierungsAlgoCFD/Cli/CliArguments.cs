using System;
using System.Collections.Generic;
using System.Linq;
using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Cli
{
    /// <summary>
    /// Geparste CLI-Argumente für <see cref="Program"/>.
    /// </summary>
    internal sealed class CliArguments
    {
        /// <summary>Umgebungsvariable, die <c>scripts/sim-runner.sh</c> setzt.</summary>
        private const string ProjectVariable = "SIM_PROJECT";

        public const string ListProjectsFlag = "--list-projects";
        public const string PrintConfigFlag = "--print-config";
        public const string CalibrateFlag = "--calibrate";
        public const string RecheckFlag = "--recheck";
        public const string UnsteadyFlag = "--unsteady";
        public const string ResumeFlag = "--resume";

        public bool ListProjects { get; init; }
        public bool PrintConfig { get; init; }
        public bool Calibrate { get; init; }
        public bool Recheck { get; init; }
        public bool Unsteady { get; init; }
        public bool Resume { get; init; }
        public string? RecheckVariant { get; init; }
        public int RecheckFiner { get; init; } = 1;
        public string[] Positional { get; init; } = Array.Empty<string>();
        public string? ProjectDirectoryArgument => Positional.Length > 1 ? Positional[1] : null;

        public static CliArguments Parse(string[] args)
        {
            // Flags von Stellungsargumenten trennen, damit die Reihenfolge egal ist.
            var flags = args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal))
                            .Select(argument => argument.Trim().ToLowerInvariant())
                            .ToHashSet();

            bool isCalibrate = false;
            bool isRecheck = false;
            string? recheckVariant = null;
            int recheckFiner = 1;
            var positionalList = new List<string>();

            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i].Trim();
                if (a.Equals(CalibrateFlag, StringComparison.OrdinalIgnoreCase))
                {
                    isCalibrate = true;
                }
                else if (a.Equals(RecheckFlag, StringComparison.OrdinalIgnoreCase))
                {
                    isRecheck = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        recheckVariant = args[++i].Trim();
                    }
                }
                else if (a.StartsWith(RecheckFlag + "=", StringComparison.OrdinalIgnoreCase))
                {
                    isRecheck = true;
                    recheckVariant = a.Substring((RecheckFlag + "=").Length).Trim();
                }
                else if (a.Equals("--finer", StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && int.TryParse(args[i + 1].Trim(), out int fVal))
                    {
                        recheckFiner = fVal;
                        i++;
                    }
                }
                else if (a.StartsWith("--finer=", StringComparison.OrdinalIgnoreCase))
                {
                    int.TryParse(a.Substring("--finer=".Length).Trim(), out recheckFiner);
                }
                else if (!a.StartsWith("--", StringComparison.Ordinal))
                {
                    positionalList.Add(a);
                }
            }

            return new CliArguments
            {
                ListProjects = flags.Contains(ListProjectsFlag),
                PrintConfig = flags.Contains(PrintConfigFlag),
                Calibrate = isCalibrate,
                Recheck = isRecheck,
                Unsteady = flags.Contains(UnsteadyFlag),
                Resume = flags.Contains(ResumeFlag),
                RecheckVariant = recheckVariant,
                RecheckFiner = recheckFiner,
                Positional = positionalList.ToArray()
            };
        }

        /// <summary>
        /// Projektname aus dem ersten Argument oder der Umgebungsvariablen. <b>Kein stiller
        /// Standard mehr:</b> ein Vertipper im Startskript würde sonst klaglos das falsche
        /// Projekt rechnen — stundenlang, mit plausibel aussehenden Ergebnissen.
        /// </summary>
        public string ResolveProjectName(ProjectRegistry registry)
        {
            if (Positional.Length > 0 && !string.IsNullOrWhiteSpace(Positional[0]))
                return Positional[0].Trim();

            string? fromEnvironment = Environment.GetEnvironmentVariable(ProjectVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                Console.WriteLine($"[SYSTEM] Projektname aus {ProjectVariable}.");
                return fromEnvironment.Trim();
            }

            throw new InvalidOperationException(
                $"[PROJEKT] Es wurde kein Projekt angegeben (weder als Argument noch über {ProjectVariable}). "
                + registry.AvailableProjects()
                + " Aufruf:  .\\scripts\\sim.ps1 run -Project <Projektname>");
        }
    }
}
