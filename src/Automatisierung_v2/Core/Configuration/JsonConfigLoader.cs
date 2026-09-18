using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPicoGkProject
{
    /// <summary>
    /// Lädt Konfiguration aus JSON-Dateien und legt sie über die einkompilierten Standardwerte.
    ///
    /// Prinzip: Standardwerte → Basisdatei → lokale Überlagerung. Jede Stufe überschreibt nur
    /// die Schlüssel, die sie tatsächlich enthält. Eine fehlende Datei ist kein Fehler —
    /// dann gelten die Standardwerte. Damit läuft das Programm auch ohne config/-Ordner,
    /// und auf einem Rechner mit anderen Programmpfaden reicht eine kleine
    /// <c>*.local.json</c> mit nur den abweichenden Einträgen.
    /// </summary>
    public static class JsonConfigLoader
    {
        /// <summary>Standard-Verzeichnisname der Konfiguration, relativ zum Repo-Root.</summary>
        public const string DefaultConfigDirectoryName = "config";

        /// <summary>Wie viele Ebenen nach oben nach dem config-Verzeichnis gesucht wird.</summary>
        private const int MaxParentLevelsToSearch = 6;

        private static readonly JsonSerializerOptions SerializerOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
            WriteIndented = true
        };

        /// <summary>
        /// Lädt die Framework-Konfiguration aus <c>simulation.json</c>, überlagert von
        /// <c>simulation.local.json</c> (maschinenspezifisch, gehört nicht ins Repo).
        /// </summary>
        /// <param name="configDirectory">
        /// Konfigurationsverzeichnis. <c>null</c> = <see cref="DefaultConfigDirectoryName"/>,
        /// ausgehend vom Arbeitsverzeichnis und notfalls in den übergeordneten Verzeichnissen gesucht.
        /// </param>
        public static SimulationConfig LoadSimulationConfig(string? configDirectory = null)
        {
            string? directory = ResolveConfigDirectory(configDirectory);

            if (directory == null)
            {
                Console.WriteLine($"[CONFIG] Kein Verzeichnis '{configDirectory ?? DefaultConfigDirectoryName}' gefunden — nutze Standardwerte.");
                return SimulationConfig.CreateDefault();
            }

            Console.WriteLine($"[CONFIG] Konfigurationsverzeichnis: {directory}");

            return Load(
                SimulationConfig.CreateDefault(),
                Path.Combine(directory, "simulation.json"),
                Path.Combine(directory, "simulation.local.json"));
        }

        /// <summary>
        /// Legt die angegebenen JSON-Dateien der Reihe nach über <paramref name="defaults"/>.
        /// Nicht vorhandene Dateien werden übersprungen.
        /// </summary>
        /// <exception cref="InvalidOperationException">Eine vorhandene Datei ist kein gültiges JSON.</exception>
        public static T Load<T>(T defaults, params string[] jsonFilePaths) where T : class
        {
            JsonNode merged = JsonSerializer.SerializeToNode(defaults, SerializerOptions)
                              ?? new JsonObject();

            foreach (string path in jsonFilePaths)
            {
                if (!File.Exists(path))
                {
                    Console.WriteLine($"[CONFIG] {Path.GetFileName(path)}: nicht vorhanden, übersprungen.");
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
                    throw new InvalidOperationException($"[CONFIG] '{path}' ist kein gültiges JSON: {ex.Message}", ex);
                }

                if (merged is JsonObject target && overlay is JsonObject source)
                {
                    Merge(target, source);
                    Console.WriteLine($"[CONFIG] {Path.GetFileName(path)}: {source.Count} Einträge übernommen.");
                }
            }

            T? result = merged.Deserialize<T>(SerializerOptions);
            if (result == null)
                throw new InvalidOperationException($"[CONFIG] Konfiguration vom Typ {typeof(T).Name} konnte nicht gelesen werden.");

            return result;
        }

        /// <summary>
        /// Schreibt ein Konfigurationsobjekt als JSON — nützlich, um eine Vorlagedatei
        /// mit den aktuellen Standardwerten zu erzeugen.
        /// </summary>
        public static void Save<T>(T configuration, string jsonFilePath) where T : class
        {
            string? directory = Path.GetDirectoryName(jsonFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(jsonFilePath, JsonSerializer.Serialize(configuration, SerializerOptions));
        }

        /// <summary>
        /// Sucht das Konfigurationsverzeichnis: absoluter Pfad wird direkt genommen, ein
        /// relativer erst im Arbeitsverzeichnis und dann in den übergeordneten Verzeichnissen.
        /// Letzteres, weil <c>dotnet run</c> im Projektverzeichnis startet, die Konfiguration
        /// aber im Repo-Root liegt. Gibt <c>null</c> zurück, wenn nichts gefunden wurde.
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

        /// <summary>Rekursives Zusammenführen: verschachtelte Objekte werden verschmolzen, alles andere ersetzt.</summary>
        private static void Merge(JsonObject target, JsonObject source)
        {
            foreach (KeyValuePair<string, JsonNode?> entry in source)
            {
                if (entry.Value is JsonObject sourceObject && target[entry.Key] is JsonObject targetObject)
                {
                    Merge(targetObject, sourceObject);
                }
                else
                {
                    target[entry.Key] = entry.Value?.DeepClone();
                }
            }
        }
    }
}
