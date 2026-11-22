using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// A key that a machine-specific <c>*.local.json</c> actually changes compared to
    /// the committed configuration layers.
    /// </summary>
    /// <param name="File">Filename of the local override file.</param>
    /// <param name="Key">Key path, e.g., <c>MaxIterations</c> or <c>BaseParameters.Length</c>.</param>
    /// <param name="PreviousValue">Value that would have applied without this file.</param>
    /// <param name="NewValue">Effective value now in use.</param>
    public sealed record MachineOverride(string File, string Key, string PreviousValue, string NewValue);

    /// <summary>
    /// Records what happened while loading configuration: which file won each key,
    /// and which machine-specific file overrode project settings.
    ///
    /// <para>
    /// Stored as structured data rather than just a console side effect so that
    /// <c>effective-config.json</c> can preserve full provenance alongside results.
    /// </para>
    /// </summary>
    public sealed class ConfigLoadReport
    {
        /// <summary>Key path → file from which the effective value originated. Keys absent here use code defaults.</summary>
        public Dictionary<string, string> Origins { get; } = new();

        /// <summary>Keys changed by a <c>*.local.json</c> relative to committed layers.</summary>
        public List<MachineOverride> MachineOverrides { get; } = new();

        /// <summary>
        /// Existing <c>*.local.json</c> files that change no values. Mentioned briefly so
        /// nobody hunts for a divergence cause in a file that actually has no effect.
        /// </summary>
        public List<string> NeutralMachineFiles { get; } = new();

        /// <summary>
        /// Prominent startup warning block when <c>*.local.json</c> files override project settings.
        /// Keys with identical values are omitted to avoid noise.
        /// </summary>
        /// <param name="projectName">
        /// Used in the hint showing where project settings belong. Uses a placeholder if omitted.
        /// </param>
        public void PrintMachineOverrideWarning(string? projectName = null)
        {
            foreach (string file in NeutralMachineFiles)
                Console.WriteLine($"{OutputConventions.ConfigTag} {file}: present, but changes no values.");

            if (MachineOverrides.Count == 0) return;

            string target = string.IsNullOrWhiteSpace(projectName) ? "<Project>" : projectName.Trim();
            int keyWidth = Math.Min(28, Math.Max(20, MachineOverrides.Max(entry => entry.Key.Length) + 2));

            var files = MachineOverrides.GroupBy(entry => entry.File).ToList();

            Console.WriteLine("==================================================================");
            Console.WriteLine(files.Count == 1
                ? $"{OutputConventions.WarningTag} A machine-specific file overrides this project:"
                : $"{OutputConventions.WarningTag} {files.Count} machine-specific files override this project:");

            foreach (var group in files)
            {
                Console.WriteLine($"          {group.Key}");

                foreach (MachineOverride entry in group)
                {
                    Console.WriteLine($"          {entry.Key.PadRight(keyWidth)}"
                                      + $"Project: {entry.PreviousValue}   ->   local: {entry.NewValue}");
                }
            }

            Console.WriteLine(files.Count == 1
                ? "          Normally this file should not exist. If the values belong"
                : "          Normally these files should not exist. If the values belong");
            Console.WriteLine($"          to the project, move them to src/projects/{target}/");
            Console.WriteLine(files.Count == 1 ? "          and delete the local file." : "          and delete the local files.");
            Console.WriteLine("==================================================================");
        }

        /// <summary>
        /// Prints the winning file per key, grouped by file for readability.
        /// </summary>
        public void PrintOrigins()
        {
            if (Origins.Count == 0) return;

            Console.WriteLine($"{OutputConventions.ConfigTag} Origin of effective values (unlisted keys use code defaults):");

            foreach (var group in Origins.GroupBy(entry => entry.Value).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                string keys = string.Join(", ", group.Select(entry => entry.Key).OrderBy(key => key, StringComparer.Ordinal));
                Console.WriteLine($"           {group.Key}");
                Console.WriteLine($"             {keys}");
            }
        }
    }

    /// <summary>
    /// A loaded configuration file as recorded in <c>effective-config.json</c>.
    /// </summary>
    /// <param name="Name">Name relative to the project directory, e.g., <c>solvers/su2.json</c>.</param>
    /// <param name="Value">The effective configuration object used by the run.</param>
    /// <param name="Report">Provenance of each key.</param>
    public sealed record LoadedConfiguration(string Name, object Value, ConfigLoadReport Report);

    /// <summary>Result of loading a configuration: the effective value and its load report.</summary>
    public sealed class ConfigLoadResult<T> where T : class
    {
        public ConfigLoadResult(T value, ConfigLoadReport report)
        {
            Value = value;
            Report = report;
        }

        public T Value { get; }
        public ConfigLoadReport Report { get; }
    }
}
