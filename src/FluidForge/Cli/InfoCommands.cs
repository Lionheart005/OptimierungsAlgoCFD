using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Cli
{
    /// <summary>
    /// Informational commands that do not start a simulation (<c>--list-projects</c>, <c>--print-config</c>).
    /// </summary>
    internal static class InfoCommands
    {
        /// <summary>
        /// Lists runnable projects as well as orphan folders that cannot be launched.
        /// A folder without a project definition is the most common mistake when creating a new project.
        /// </summary>
        public static void PrintProjects(ProjectRegistry registry, string? projectDirectoryArgument)
        {
            Console.WriteLine("Runnable projects:");

            if (registry.Definitions.Count == 0)
                Console.WriteLine("  (none)");

            foreach (IProjectDefinition definition in registry.Definitions)
                Console.WriteLine($"  {definition.Name}   ({definition.GetType().Name})");

            var folders = ProjectPaths.ListProjectNames(projectDirectoryArgument);
            var orphans = folders
                .Where(folder => !registry.Names.Contains(folder, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (orphans.Count == 0) return;

            Console.WriteLine();
            Console.WriteLine("Folders without a project definition (not runnable):");
            foreach (string folder in orphans)
                Console.WriteLine($"  {folder}");
        }

        /// <summary>
        /// Prints the effective framework configuration as <c>KEY=VALUE</c>, one entry per line.
        /// Deliberately flat instead of JSON so <c>sim-runner.sh</c> can extract values with
        /// <c>grep</c> without requiring a JSON parser.
        /// </summary>
        public static void PrintConfiguration(ProjectContext context)
        {
            bool autoMesh = string.Equals(context.Simulation.MeshCalibration.Mode?.Trim(), "Auto", StringComparison.OrdinalIgnoreCase);
            PrintProperties(context.Simulation, string.Empty, autoMesh);
        }

        /// <summary>
        /// Nested sections (<c>MeshCalibration</c>, <c>ControlRuns</c> ...) are printed in dotted
        /// notation, e.g. <c>MeshCalibration.Mode=Auto</c> — otherwise only the type name would appear.
        /// </summary>
        private static void PrintProperties(object instance, string prefix, bool autoMesh)
        {
            foreach (PropertyInfo property in instance.GetType()
                         .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                         .OrderBy(property => property.Name, StringComparer.Ordinal))
            {
                object? value = property.GetValue(instance);
                string name = prefix + property.Name;

                if (prefix.Length == 0 && property.Name == nameof(SimulationConfig.MeshRefinementFactor) && autoMesh)
                {
                    // In Auto mode the code default does not apply; the run calibrates the factor itself.
                    Console.WriteLine($"{name}=(calibrated at run start)");
                }
                else if (value != null && IsSection(property.PropertyType))
                {
                    PrintProperties(value, name + ".", autoMesh);
                }
                else
                {
                    Console.WriteLine($"{name}={Format(value)}");
                }
            }
        }

        /// <summary>Configuration section class defined by the application, not a string, number, or collection.</summary>
        private static bool IsSection(Type type) =>
            type.IsClass && type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

        /// <summary>Format culture-invariantly so localized machines do not output 0,5 instead of 0.5.</summary>
        private static string Format(object? value) => value switch
        {
            null => string.Empty,
            bool flag => flag ? "true" : "false",
            string text => text,
            System.Collections.IDictionary dictionary => "{" + string.Join(", ", dictionary.Keys.Cast<object>().Select(key => $"{key}={Format(dictionary[key])}")) + "}",
            System.Collections.IEnumerable list => "[" + string.Join(", ", list.Cast<object?>().Select(Format)) + "]",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty
        };
    }
}
