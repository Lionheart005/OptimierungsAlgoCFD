using System;
using System.Collections.Generic;
using System.Linq;
using FluidForge.Core;

namespace FluidForge.Cli
{
    /// <summary>
    /// Parsed CLI arguments for <see cref="Program"/>.
    /// </summary>
    internal sealed class CliArguments
    {
        /// <summary>Environment variable set by <c>scripts/sim-runner.sh</c>.</summary>
        private const string ProjectVariable = "SIM_PROJECT";

        public const string ListProjectsFlag = "--list-projects";
        public const string PrintConfigFlag = "--print-config";
        public const string OriginsFlag = "--origins";
        public const string DumpPresetFlag = "--dump-preset";
        public const string ExportSchemasFlag = "--export-schemas";
        public const string OutFlag = "--out";
        public const string CalibrateFlag = "--calibrate";
        public const string RecheckFlag = "--recheck";
        public const string UnsteadyFlag = "--unsteady";
        public const string ResumeFlag = "--resume";
        public const string SmokeTestFlag = "--smoke-test";

        public bool ListProjects { get; init; }
        public bool PrintConfig { get; init; }
        public bool Origins { get; init; }
        public bool DumpPreset { get; init; }
        public string? DumpPresetName { get; init; }
        public bool ExportSchemas { get; init; }
        public string ExportSchemasDirectory { get; init; } = "schemas";
        public string? OutDirectory { get; init; }
        public bool Calibrate { get; init; }
        public bool Recheck { get; init; }
        public bool Unsteady { get; init; }
        public bool Resume { get; init; }
        public bool SmokeTest { get; init; }
        public string? RecheckVariant { get; init; }
        public int RecheckFiner { get; init; } = 1;
        public string[] Positional { get; init; } = Array.Empty<string>();
        public string? ProjectDirectoryArgument => Positional.Length > 1 ? Positional[1] : null;

        public static CliArguments Parse(string[] args)
        {
            // Separate flags from positional arguments so argument order does not matter.
            var flags = args.Where(argument => argument.StartsWith("--", StringComparison.Ordinal))
                            .Select(argument => argument.Trim().ToLowerInvariant())
                            .ToHashSet();

            bool isCalibrate = false;
            bool isRecheck = false;
            bool isDumpPreset = false;
            string? dumpPresetName = null;
            bool isExportSchemas = false;
            string exportSchemasDir = "schemas";
            string? outDirectory = null;
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
                else if (a.Equals(DumpPresetFlag, StringComparison.OrdinalIgnoreCase))
                {
                    isDumpPreset = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        dumpPresetName = args[++i].Trim();
                    }
                }
                else if (a.StartsWith(DumpPresetFlag + "=", StringComparison.OrdinalIgnoreCase))
                {
                    isDumpPreset = true;
                    dumpPresetName = a.Substring((DumpPresetFlag + "=").Length).Trim();
                }
                else if (a.Equals(ExportSchemasFlag, StringComparison.OrdinalIgnoreCase))
                {
                    isExportSchemas = true;
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        exportSchemasDir = args[++i].Trim();
                    }
                }
                else if (a.StartsWith(ExportSchemasFlag + "=", StringComparison.OrdinalIgnoreCase))
                {
                    isExportSchemas = true;
                    string dirVal = a.Substring((ExportSchemasFlag + "=").Length).Trim();
                    if (!string.IsNullOrEmpty(dirVal)) exportSchemasDir = dirVal;
                }
                else if (a.Equals(OutFlag, StringComparison.OrdinalIgnoreCase))
                {
                    if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                    {
                        outDirectory = args[++i].Trim();
                    }
                }
                else if (a.StartsWith(OutFlag + "=", StringComparison.OrdinalIgnoreCase))
                {
                    outDirectory = a.Substring((OutFlag + "=").Length).Trim();
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
                Origins = flags.Contains(OriginsFlag),
                DumpPreset = isDumpPreset,
                DumpPresetName = dumpPresetName,
                ExportSchemas = isExportSchemas,
                ExportSchemasDirectory = exportSchemasDir,
                OutDirectory = outDirectory,
                Calibrate = isCalibrate,
                Recheck = isRecheck,
                Unsteady = flags.Contains(UnsteadyFlag),
                Resume = flags.Contains(ResumeFlag),
                SmokeTest = flags.Contains(SmokeTestFlag),
                RecheckVariant = recheckVariant,
                RecheckFiner = recheckFiner,
                Positional = positionalList.ToArray()
            };
        }

        /// <summary>
        /// Resolves the project name from the first positional argument or environment variable.
        /// <b>No silent default:</b> a typo in the launch script would otherwise silently run the
        /// wrong project for hours with plausible-looking results.
        /// </summary>
        public string ResolveProjectName(ProjectRegistry registry)
        {
            if (Positional.Length > 0 && !string.IsNullOrWhiteSpace(Positional[0]))
                return Positional[0].Trim();

            string? fromEnvironment = Environment.GetEnvironmentVariable(ProjectVariable);
            if (!string.IsNullOrWhiteSpace(fromEnvironment))
            {
                Console.WriteLine($"{OutputConventions.SystemTag} Project name from {ProjectVariable}.");
                return fromEnvironment.Trim();
            }

            throw new InvalidOperationException(
                $"[PROJECT] No project specified (neither as an argument nor via {ProjectVariable}). "
                + registry.AvailableProjects()
                + " Usage:  .\\scripts\\sim.ps1 run -Project <ProjectName>");
        }
    }
}
