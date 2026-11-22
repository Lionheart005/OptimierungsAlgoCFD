using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Cli
{
    /// <summary>
    /// Informational and transparency commands that do not start a simulation
    /// (<c>--list-projects</c>, <c>--print-config</c>, <c>--dump-preset</c>, <c>--export-schemas</c>).
    /// </summary>
    internal static class InfoCommands
    {
        private static readonly JsonSerializerOptions PrettyJsonOptions = new()
        {
            WriteIndented = true
        };

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
        /// Prints the effective framework configuration as <c>KEY=VALUE</c>, one entry per line
        /// (compatible with <c>sim-runner.sh</c> <c>grep</c>/<c>sed</c>), followed by a commented
        /// provenance and solver-options summary.
        /// </summary>
        public static void PrintConfiguration(ProjectContext context)
        {
            bool autoMesh = string.Equals(context.Simulation.MeshCalibration.Mode?.Trim(), "Auto", StringComparison.OrdinalIgnoreCase);
            PrintProperties(context.Simulation, string.Empty, autoMesh, linePrefix: string.Empty);

            Console.WriteLine("#");
            Console.WriteLine("# --- Provenance & Solver Options ---");

            foreach (LoadedConfiguration loaded in context.Loaded)
            {
                Console.WriteLine($"# [{loaded.Name}]");

                if (loaded.Report.Origins.Count == 0)
                {
                    Console.WriteLine("#   (all keys from code defaults)");
                }
                else
                {
                    foreach (KeyValuePair<string, string> origin in loaded.Report.Origins.OrderBy(e => e.Key, StringComparer.Ordinal))
                    {
                        Console.WriteLine($"#   {origin.Key} <- {origin.Value}");
                    }
                }

                if (loaded.Name.StartsWith("solvers/", StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"# Effective values for {loaded.Name}:");
                    PrintProperties(loaded.Value, string.Empty, autoMesh: false, linePrefix: "#   ");
                }
            }
        }

        /// <summary>
        /// Lists all curated presets or dumps the complete effective JSONs for a single preset
        /// to stdout or into <paramref name="outDirectory"/>.
        /// </summary>
        public static void DumpPreset(string? presetName, string? outDirectory)
        {
            if (string.IsNullOrWhiteSpace(presetName)
                || presetName.Trim().Equals("list", StringComparison.OrdinalIgnoreCase))
            {
                Console.WriteLine("Available presets (Domain:Accuracy):");
                Console.WriteLine($"  {"Name",-28} {"Domain",-12} {"Accuracy",-18} {"Status",-20} Description");
                Console.WriteLine($"  {new string('-', 28)} {new string('-', 12)} {new string('-', 18)} {new string('-', 20)} {new string('-', 40)}");
                foreach (PresetDefinition item in SimulationPresets.All)
                {
                    string status = item.IsPlaceholder ? "Phase 3 placeholder" : "Ready";
                    Console.WriteLine($"  {item.Name,-28} {item.Domain,-12} {item.Accuracy,-18} {status,-20} {item.Description}");
                }
                Console.WriteLine();
                Console.WriteLine("Shorthands: 'External2D', 'External3D', 'Internal3D' -> ':StandardRans'.");
                Console.WriteLine("Dump full JSONs: --dump-preset <PresetName> [--out <Directory>]");
                return;
            }

            PresetDefinition? preset = SimulationPresets.Resolve(presetName);
            if (preset == null)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} 'Custom' is not a preset. Specify a preset name or run '--dump-preset' to list all presets.");
            }

            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            var simDefaults = SimulationConfig.CreateDefault();
            simDefaults.Preset = preset.Name;
            SimulationConfig effectiveSim = JsonConfigLoader.LoadWithPresetCore(
                simDefaults, preset.Name, preset.SimulationOverrides, printOrigins: false).Value;
            files["simulation.json"] = JsonSerializer.Serialize(effectiveSim, PrettyJsonOptions) + Environment.NewLine;

            if (preset.Domain.Equals("External2D", StringComparison.OrdinalIgnoreCase))
            {
                Su2SolverOptions su2 = JsonConfigLoader.LoadWithPresetCore(
                    new Su2SolverOptions(), preset.Name, preset.GetSolverOverrides("solvers/su2.json"), printOrigins: false).Value;
                Gmsh2DMesherOptions gmsh2d = JsonConfigLoader.LoadWithPresetCore(
                    new Gmsh2DMesherOptions(), preset.Name, preset.GetSolverOverrides("solvers/gmsh.json"), printOrigins: false).Value;

                files["solvers/su2.json"] = JsonSerializer.Serialize(su2, PrettyJsonOptions) + Environment.NewLine;
                files["solvers/gmsh.json"] = JsonSerializer.Serialize(gmsh2d, PrettyJsonOptions) + Environment.NewLine;
            }
            else if (preset.Domain.Equals("External3D", StringComparison.OrdinalIgnoreCase))
            {
                Su2SolverOptions su2 = JsonConfigLoader.LoadWithPresetCore(
                    new Su2SolverOptions(), preset.Name, preset.GetSolverOverrides("solvers/su2.json"), printOrigins: false).Value;
                GmshMesherOptions gmsh3d = JsonConfigLoader.LoadWithPresetCore(
                    new GmshMesherOptions(), preset.Name, preset.GetSolverOverrides("solvers/gmsh.json"), printOrigins: false).Value;

                files["solvers/su2.json"] = JsonSerializer.Serialize(su2, PrettyJsonOptions) + Environment.NewLine;
                files["solvers/gmsh.json"] = JsonSerializer.Serialize(gmsh3d, PrettyJsonOptions) + Environment.NewLine;
            }

            if (!string.IsNullOrWhiteSpace(outDirectory))
            {
                string targetRoot = Path.GetFullPath(outDirectory);
                foreach (KeyValuePair<string, string> entry in files)
                {
                    string filePath = Path.Combine(targetRoot, entry.Key.Replace('/', Path.DirectorySeparatorChar));
                    string? parent = Path.GetDirectoryName(filePath);
                    if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                    File.WriteAllText(filePath, entry.Value);
                    Console.WriteLine($"{OutputConventions.ConfigTag} Wrote {JsonConfigLoader.Label(filePath)}");
                }
                return;
            }

            Console.WriteLine($"=== Preset: {preset.Name} ===");
            if (preset.IsPlaceholder)
                Console.WriteLine($"Note: {preset.Description}");

            foreach (KeyValuePair<string, string> entry in files)
            {
                Console.WriteLine();
                Console.WriteLine($"--- {entry.Key} ---");
                Console.Write(entry.Value);
            }
        }

        /// <summary>
        /// Generates all JSON Schema files into <paramref name="directory"/> (default <c>schemas/</c>).
        /// </summary>
        public static void ExportSchemas(string directory)
        {
            string targetDir = Path.GetFullPath(string.IsNullOrWhiteSpace(directory) ? "schemas" : directory);
            Directory.CreateDirectory(targetDir);

            var targets = new (string FileName, Type Type, string Title, string Description)[]
            {
                ("simulation.schema.json", typeof(SimulationConfig), "FluidForge SimulationConfig", "Framework & optimization settings (simulation.json)."),
                ("project.schema.json", typeof(ProjectConfigDto), "FluidForge ProjectConfig", "Project parameters, bounds, targets, and dimensional keys (project.json)."),
                ("su2.schema.json", typeof(Su2SolverOptions), "FluidForge Su2SolverOptions", "SU2 CFD solver settings (solvers/su2.json)."),
                ("gmsh.schema.json", typeof(GmshMesherOptions), "FluidForge GmshMesherOptions", "Gmsh 3D mesher settings (solvers/gmsh.json)."),
                ("gmsh2d.schema.json", typeof(Gmsh2DMesherOptions), "FluidForge Gmsh2DMesherOptions", "Gmsh 2D mesher settings (solvers/gmsh.json for 2D projects).")
            };

            foreach (var (fileName, type, title, description) in targets)
            {
                string path = Path.Combine(targetDir, fileName);
                File.WriteAllText(path, JsonSchemaGenerator.GenerateJson(type, title, description));
                Console.WriteLine($"{OutputConventions.ConfigTag} Exported schema: {JsonConfigLoader.Label(path)}");
            }
        }

        /// <summary>
        /// Nested sections (<c>MeshCalibration</c>, <c>ControlRuns</c> ...) are printed in dotted
        /// notation, e.g. <c>MeshCalibration.Mode=Auto</c> — otherwise only the type name would appear.
        /// </summary>
        private static void PrintProperties(object instance, string prefix, bool autoMesh, string linePrefix)
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
                    Console.WriteLine($"{linePrefix}{name}=(calibrated at run start)");
                }
                else if (value != null && IsSection(property.PropertyType))
                {
                    PrintProperties(value, name + ".", autoMesh, linePrefix);
                }
                else
                {
                    Console.WriteLine($"{linePrefix}{name}={Format(value)}");
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
