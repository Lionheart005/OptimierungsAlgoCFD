using System;
using System.Globalization;
using System.Linq;
using System.Reflection;
using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Cli
{
    /// <summary>
    /// Informations-Befehle ohne Simulationsstart (<c>--list-projects</c>, <c>--print-config</c>).
    /// </summary>
    internal static class InfoCommands
    {
        /// <summary>
        /// Listet auf, was startbar ist — und was daneben liegt, ohne es zu sein. Ein Ordner ohne
        /// Definition ist der häufigste Fall beim Anlegen eines neuen Projekts.
        /// </summary>
        public static void PrintProjects(ProjectRegistry registry, string? projectDirectoryArgument)
        {
            Console.WriteLine("Startbare Projekte:");

            if (registry.Definitions.Count == 0)
                Console.WriteLine("  (keine)");

            foreach (IProjectDefinition definition in registry.Definitions)
                Console.WriteLine($"  {definition.Name}   ({definition.GetType().Name})");

            var folders = ProjectPaths.ListProjectNames(projectDirectoryArgument);
            var orphans = folders
                .Where(folder => !registry.Names.Contains(folder, StringComparer.OrdinalIgnoreCase))
                .ToList();

            if (orphans.Count == 0) return;

            Console.WriteLine();
            Console.WriteLine("Ordner ohne Projektdefinition (nicht startbar):");
            foreach (string folder in orphans)
                Console.WriteLine($"  {folder}");
        }

        /// <summary>
        /// Gibt die wirksame Framework-Konfiguration als <c>SCHLÜSSEL=WERT</c> aus, eine Zeile je
        /// Eintrag. Bewusst dieses stumpfe Format und kein JSON: <c>sim-runner.sh</c> soll den
        /// Wert mit <c>grep</c> herausziehen können, ohne einen JSON-Parser zu brauchen.
        /// </summary>
        public static void PrintConfiguration(ProjectContext context)
        {
            bool autoMesh = string.Equals(context.Simulation.MeshCalibration.Mode?.Trim(), "Auto", StringComparison.OrdinalIgnoreCase);
            PrintProperties(context.Simulation, string.Empty, autoMesh);
        }

        /// <summary>
        /// Verschachtelte Abschnitte (<c>MeshCalibration</c>, <c>ControlRuns</c> …) erscheinen mit Punkt-Schreibweise,
        /// z. B. <c>MeshCalibration.Mode=Auto</c> — sonst stünde dort nur der Typname.
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
                    // Im Auto-Modus gilt der Wert aus dem Code-Standard nicht; der Lauf kalibriert den Faktor selbst.
                    Console.WriteLine($"{name}=(wird am Laufanfang kalibriert)");
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

        /// <summary>Eigener Konfigurationsabschnitt (Klasse des Programms), kein Text, keine Zahl, keine Liste.</summary>
        private static bool IsSection(Type type) =>
            type.IsClass && type != typeof(string) && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type);

        /// <summary>Kultur-unabhängig formatieren — sonst steht auf einem deutschen Rechner 0,5 statt 0.5.</summary>
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
