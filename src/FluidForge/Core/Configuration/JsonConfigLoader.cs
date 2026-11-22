using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluidForge.Core
{
    /// <summary>
    /// Loads configuration from JSON files and overlays them onto compiled-in defaults.
    ///
    /// <para>
    /// Each layer overrides only the keys it explicitly defines. A missing file is not an error —
    /// values from earlier layers remain in effect. An <b>unknown</b> key, however, aborts startup
    /// so typos are caught immediately.
    /// </para>
    ///
    /// <para>Layers, from general to specific:</para>
    /// <list type="number">
    ///   <item>Code defaults (<c>CreateDefault()</c> / <c>new T()</c>) — apply to all runs</item>
    ///   <item><c>src/projects/&lt;Name&gt;/…json</c> — this project, all machines</item>
    ///   <item><c>config/…local.json</c> — all projects, this machine <i>(escape hatch)</i></item>
    ///   <item><c>src/projects/&lt;Name&gt;/…local.json</c> — this project, this machine <i>(escape hatch)</i></item>
    /// </list>
    ///
    /// <para>
    /// Rule of thumb: <b>more specific wins — and gitignored beats committed.</b>
    /// Ideally layers 3 and 4 do not exist; any present <c>*.local.json</c> is reported at startup.
    /// </para>
    /// </summary>
    public static class JsonConfigLoader
    {
        /// <summary>Default config directory name relative to the repository root.</summary>
        public const string DefaultConfigDirectoryName = "config";

        /// <summary>
        /// Suffix for machine-specific override files (excluded via <c>.gitignore</c> and deploy filters).
        /// </summary>
        public const string MachineSpecificSuffix = ".local.json";

        /// <summary>Maximum parent directory levels to search upward for the config directory.</summary>
        private const int MaxParentLevelsToSearch = 6;

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        /// <summary>
        /// Overlays the specified JSON files in order onto <paramref name="defaults"/>.
        /// Missing files are skipped.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// An existing file is invalid JSON or contains an unknown key.
        /// </exception>
        public static T Load<T>(T defaults, params string[] jsonFilePaths) where T : class
            => LoadWithReport(defaults, jsonFilePaths).Value;

        /// <summary>
        /// Like <see cref="Load{T}"/>, but also returns the <see cref="ConfigLoadReport"/>
        /// detailing key origins and machine-specific overrides.
        /// </summary>
        public static ConfigLoadResult<T> LoadWithReport<T>(T defaults, params string[] jsonFilePaths) where T : class
            => LoadWithPreset(defaults, presetName: null, presetOverlay: null, jsonFilePaths);

        /// <summary>
        /// Overlays an optional Layer 1.5 <paramref name="presetOverlay"/> onto <paramref name="defaults"/>
        /// and then applies <paramref name="jsonFilePaths"/> in order, returning the effective value
        /// and its <see cref="ConfigLoadReport"/>.
        /// </summary>
        public static ConfigLoadResult<T> LoadWithPreset<T>(
            T defaults,
            string? presetName,
            JsonObject? presetOverlay,
            params string[] jsonFilePaths) where T : class
            => LoadWithPresetCore(defaults, presetName, presetOverlay, printOrigins: true, jsonFilePaths);

        internal static ConfigLoadResult<T> LoadWithPresetCore<T>(
            T defaults,
            string? presetName,
            JsonObject? presetOverlay,
            bool printOrigins,
            params string[] jsonFilePaths) where T : class
        {
            var report = new ConfigLoadReport();

            JsonNode merged = JsonSerializer.SerializeToNode(defaults, SerializerOptions)
                              ?? new JsonObject();

            if (!string.IsNullOrWhiteSpace(presetName) && presetOverlay != null && presetOverlay.Count > 0 && merged is JsonObject presetTarget)
            {
                string presetLabel = $"preset:{presetName}";
                ConfigSchema.Validate(typeof(T), presetOverlay, presetLabel);
                Merge(presetTarget, presetOverlay, prefix: string.Empty, presetLabel, report, machineSpecific: false);
            }

            foreach (string path in jsonFilePaths)
            {
                string label = Label(path);

                if (!File.Exists(path))
                {
                    if (printOrigins)
                        Console.WriteLine($"{OutputConventions.ConfigTag} {label}: not found, skipped.");
                    continue;
                }

                JsonNode? overlay;
                try
                {
                    overlay = JsonNode.Parse(
                        File.ReadAllText(path),
                        documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true
                        });
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"{OutputConventions.ConfigTag} '{path}' is not valid JSON: {ex.Message}", ex);
                }

                if (merged is not JsonObject target || overlay is not JsonObject source) continue;

                // Validate before merging so typos fail immediately.
                ConfigSchema.Validate(typeof(T), source, path);

                bool machineSpecific = IsMachineSpecific(path);
                int overridesBefore = report.MachineOverrides.Count;

                Merge(target, source, prefix: string.Empty, label, report, machineSpecific);

                if (machineSpecific && report.MachineOverrides.Count == overridesBefore)
                    report.NeutralMachineFiles.Add(label);
            }

            T? result = merged.Deserialize<T>(SerializerOptions);
            if (result == null)
                throw new InvalidOperationException($"{OutputConventions.ConfigTag} Failed to deserialize configuration of type {typeof(T).Name}.");

            if (printOrigins)
                report.PrintOrigins();

            return new ConfigLoadResult<T>(result, report);
        }

        /// <summary>
        /// Formats a file path for logs and warnings: relative to the current working directory
        /// if underneath it, otherwise absolute. Always uses forward slashes.
        /// </summary>
        public static string Label(string path)
        {
            string full = Path.GetFullPath(path);
            string current = Directory.GetCurrentDirectory().TrimEnd(Path.DirectorySeparatorChar);

            if (full.Length > current.Length
                && full.StartsWith(current, StringComparison.OrdinalIgnoreCase)
                && (full[current.Length] == Path.DirectorySeparatorChar || full[current.Length] == Path.AltDirectorySeparatorChar))
            {
                full = full.Substring(current.Length + 1);
            }

            return full.Replace('\\', '/');
        }

        /// <summary>
        /// Returns whether the path refers to a machine-specific override file based on its suffix.
        /// </summary>
        public static bool IsMachineSpecific(string path)
            => Path.GetFileName(path).EndsWith(MachineSpecificSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Returns the machine-specific sibling path for a config file:
        /// <c>simulation.json</c> → <c>simulation.local.json</c>.
        /// </summary>
        public static string MachineVariantOf(string jsonFilePath)
        {
            if (IsMachineSpecific(jsonFilePath)) return jsonFilePath;

            string? directory = Path.GetDirectoryName(jsonFilePath);
            string name = Path.GetFileNameWithoutExtension(jsonFilePath) + MachineSpecificSuffix;

            return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
        }

        /// <summary>
        /// Loads a project configuration file across all four layers.
        /// </summary>
        /// <param name="defaults">Layer 1 — code defaults.</param>
        /// <param name="relativeName">
        /// Filename relative to the project folder, e.g., <c>project.json</c> or
        /// <c>solvers/su2.json</c>. Forward slashes are supported.
        /// </param>
        /// <param name="projectDirectory">Project folder (layers 2 and 4).</param>
        /// <param name="configDirectory">
        /// The <c>config/</c> directory for layer 3. <c>null</c> searches upward; if none is
        /// found, layer 3 is skipped.
        /// </param>
        public static ConfigLoadResult<T> LoadForProject<T>(
            T defaults,
            string relativeName,
            string projectDirectory,
            string? configDirectory = null) where T : class
            => LoadForProjectWithPreset(defaults, relativeName, projectDirectory, presetName: null, presetOverlay: null, configDirectory);

        /// <summary>
        /// Loads a project configuration file across all layers, inserting an optional Layer 1.5
        /// <paramref name="presetOverlay"/> between code defaults and project JSON files.
        /// </summary>
        public static ConfigLoadResult<T> LoadForProjectWithPreset<T>(
            T defaults,
            string relativeName,
            string projectDirectory,
            string? presetName,
            JsonObject? presetOverlay,
            string? configDirectory = null) where T : class
        {
            string[] layers = BuildProjectLayers(relativeName, projectDirectory, configDirectory);
            return LoadWithPreset(defaults, presetName, presetOverlay, layers);
        }

        /// <summary>
        /// Reads the last occurring string value for <paramref name="keyName"/> across existing
        /// JSON files in <paramref name="jsonFilePaths"/> (case-insensitive). Returns <c>null</c>
        /// if no file defines the key.
        /// </summary>
        public static string? PeekStringKey(string keyName, params string[] jsonFilePaths)
        {
            string? found = null;

            foreach (string path in jsonFilePaths)
            {
                if (!File.Exists(path)) continue;

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(
                        File.ReadAllText(path),
                        documentOptions: new JsonDocumentOptions
                        {
                            CommentHandling = JsonCommentHandling.Skip,
                            AllowTrailingCommas = true
                        });
                }
                catch (JsonException ex)
                {
                    throw new InvalidOperationException($"{OutputConventions.ConfigTag} '{path}' is not valid JSON: {ex.Message}", ex);
                }

                if (node is not JsonObject obj) continue;

                foreach (KeyValuePair<string, JsonNode?> entry in obj)
                {
                    if (!string.Equals(entry.Key, keyName, StringComparison.OrdinalIgnoreCase)) continue;
                    if (entry.Value is JsonValue val && val.TryGetValue(out string? text))
                        found = text;
                }
            }

            return found;
        }

        /// <summary>
        /// Reads the effective string value for <paramref name="keyName"/> across all project layers
        /// (project JSON, config/*.local.json, project/*.local.json).
        /// </summary>
        public static string? PeekProjectStringKey(
            string keyName,
            string relativeName,
            string projectDirectory,
            string? configDirectory = null)
            => PeekStringKey(keyName, BuildProjectLayers(relativeName, projectDirectory, configDirectory));

        private static string[] BuildProjectLayers(string relativeName, string projectDirectory, string? configDirectory)
        {
            string[] segments = relativeName.Split('/', '\\');

            var layers = new List<string>
            {
                // Layer 2: this project, all machines.
                Path.Combine(new[] { projectDirectory }.Concat(segments).ToArray())
            };

            // Layer 3: all projects, this machine. Applied before layer 4.
            string? resolvedConfig = ResolveConfigDirectory(configDirectory);
            if (resolvedConfig != null)
                layers.Add(MachineVariantOf(Path.Combine(new[] { resolvedConfig }.Concat(segments).ToArray())));

            // Layer 4: this project, this machine. Highest precedence.
            layers.Add(MachineVariantOf(layers[0]));

            return layers.ToArray();
        }

        /// <summary>
        /// Serializes a configuration object to JSON — useful for generating a template
        /// with current defaults. Do not use on hand-authored project files, as comments are stripped.
        /// </summary>
        public static void Save<T>(T configuration, string jsonFilePath) where T : class
        {
            string? directory = Path.GetDirectoryName(jsonFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(jsonFilePath, JsonSerializer.Serialize(configuration, SerializerOptions));
        }

        /// <summary>
        /// Locates the config directory: uses rooted paths directly, or searches the current
        /// working directory and parent directories. Returns <c>null</c> if not found.
        /// </summary>
        public static string? ResolveConfigDirectory(string? configDirectory)
        {
            string candidate = configDirectory ?? DefaultConfigDirectoryName;

            if (Path.IsPathRooted(candidate))
                return Directory.Exists(candidate) ? candidate : null;

            DirectoryInfo? current = new DirectoryInfo(Directory.GetCurrentDirectory());

            for (int level = 0; level <= MaxParentLevelsToSearch && current != null; level++)
            {
                string path = Path.Combine(current.FullName, candidate);
                if (Directory.Exists(path)) return path;
                current = current.Parent;
            }

            return null;
        }

        /// <summary>
        /// Recursively merges <paramref name="source"/> into <paramref name="target"/>:
        /// nested objects are merged, all other values are replaced. Records key origins and
        /// any actual changes introduced by machine-specific override files.
        /// </summary>
        private static void Merge(
            JsonObject target,
            JsonObject source,
            string prefix,
            string fileLabel,
            ConfigLoadReport report,
            bool machineSpecific)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in source)
            {
                // Root-level "$schema" is editor metadata and must not enter merged values or origins.
                if (string.IsNullOrEmpty(prefix) && string.Equals(entry.Key, "$schema", StringComparison.OrdinalIgnoreCase))
                    continue;

                string keyPath = prefix + entry.Key;

                if (entry.Value is JsonObject sourceObject && target[entry.Key] is JsonObject targetObject)
                {
                    Merge(targetObject, sourceObject, keyPath + ".", fileLabel, report, machineSpecific);
                    continue;
                }

                string before = Describe(target[entry.Key]);
                string after = Describe(entry.Value);

                target[entry.Key] = entry.Value?.DeepClone();
                report.Origins[keyPath] = fileLabel;

                if (machineSpecific && !string.Equals(before, after, StringComparison.Ordinal))
                    report.MachineOverrides.Add(new MachineOverride(fileLabel, keyPath, before, after));
            }
        }

        /// <summary>
        /// Formats a JSON value for human-readable override warnings.
        /// </summary>
        private static string Describe(JsonNode? node)
        {
            if (node == null) return "(not set)";

            if (node is JsonValue value && value.TryGetValue(out string? text)) return text ?? "(null)";

            return node.ToJsonString();
        }
    }
}
