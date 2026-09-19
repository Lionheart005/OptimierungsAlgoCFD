using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// LÃ¤dt Konfiguration aus JSON-Dateien und legt sie Ã¼ber die einkompilierten Standardwerte.
    ///
    /// <para>
    /// Jede Stufe Ã¼berschreibt nur die SchlÃ¼ssel, die sie tatsÃ¤chlich enthÃ¤lt. Eine fehlende
    /// Datei ist kein Fehler â€” dann gilt, was die Stufe davor ergeben hat. Ein
    /// <b>unbekannter</b> SchlÃ¼ssel ist dagegen ein Abbruch (Entscheidung 8, TODO-22):
    /// vorher fiel ein Tippfehler beim Lesen einfach weg.
    /// </para>
    ///
    /// <para>Die Schichten, von allgemein nach spezifisch:</para>
    /// <list type="number">
    ///   <item>Code-Standards (<c>CreateDefault()</c> bzw. <c>new T()</c>) â€” gelten fÃ¼r alles</item>
    ///   <item><c>src/projects/&lt;Name&gt;/â€¦json</c> â€” dieses Projekt, alle Rechner</item>
    ///   <item><c>config/â€¦local.json</c> â€” alle Projekte, dieser Rechner <i>(Notausgang)</i></item>
    ///   <item><c>src/projects/&lt;Name&gt;/â€¦local.json</c> â€” dieses Projekt, dieser Rechner <i>(Notausgang)</i></item>
    /// </list>
    ///
    /// <para>
    /// Regel in einem Satz: <b>je spezifischer, desto spÃ¤ter â€” und gitignoriert schlÃ¤gt
    /// eingecheckt.</b> Schicht 3 gewinnt Ã¼ber Schicht 2, damit ein Projekt dem Rechner nicht
    /// seinen <c>Su2Path</c> Ã¼berschreiben kann; Schicht 4 gewinnt Ã¼ber alles.
    /// </para>
    ///
    /// <para>
    /// <b>Sollstand ist, dass es die Schichten 3 und 4 nirgends gibt.</b> Der Mechanismus
    /// bleibt als Notausgang fÃ¼r Rechner mit exotischer Installation â€” jede vorhandene Datei
    /// wird beim Start gemeldet, samt jedem SchlÃ¼ssel, den sie dem Projekt aushebelt.
    /// </para>
    /// </summary>
    public static class JsonConfigLoader
    {
        /// <summary>Standard-Verzeichnisname der Konfiguration, relativ zum Repo-Root.</summary>
        public const string DefaultConfigDirectoryName = "config";

        /// <summary>
        /// Endung der maschinenspezifischen Ausnahmedateien. Dasselbe Muster steht in der
        /// <c>.gitignore</c> und im Upload-Filter von <c>sim.ps1</c> â€” eine solche Datei
        /// Ã¼berlebt dadurch jedes Deploy und wird von keinem Ã¼berschrieben.
        /// </summary>
        public const string MachineSpecificSuffix = ".local.json";

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
        /// Legt die angegebenen JSON-Dateien der Reihe nach Ã¼ber <paramref name="defaults"/>.
        /// Nicht vorhandene Dateien werden Ã¼bersprungen.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Eine vorhandene Datei ist kein gÃ¼ltiges JSON oder nennt einen unbekannten SchlÃ¼ssel.
        /// </exception>
        public static T Load<T>(T defaults, params string[] jsonFilePaths) where T : class
            => LoadWithReport(defaults, jsonFilePaths).Value;

        /// <summary>
        /// Wie <see cref="Load{T}"/>, liefert aber zusÃ¤tzlich den Bericht: welcher SchlÃ¼ssel aus
        /// welcher Datei gewonnen hat und welche <c>*.local.json</c> dem Projekt dazwischenfunkt.
        ///
        /// <para>
        /// Ob eine Datei als maschinenspezifisch gilt, entscheidet allein ihr Name
        /// (<see cref="MachineSpecificSuffix"/>) â€” dasselbe Kriterium wie in der
        /// <c>.gitignore</c> und im Upload-Filter von <c>sim.ps1</c>.
        /// </para>
        /// </summary>
        public static ConfigLoadResult<T> LoadWithReport<T>(T defaults, params string[] jsonFilePaths) where T : class
        {
            var report = new ConfigLoadReport();

            JsonNode merged = JsonSerializer.SerializeToNode(defaults, SerializerOptions)
                              ?? new JsonObject();

            foreach (string path in jsonFilePaths)
            {
                string label = Label(path);

                if (!File.Exists(path))
                {
                    Console.WriteLine($"[CONFIG] {label}: nicht vorhanden, Ã¼bersprungen.");
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
                    throw new InvalidOperationException($"[CONFIG] '{path}' ist kein gÃ¼ltiges JSON: {ex.Message}", ex);
                }

                if (merged is not JsonObject target || overlay is not JsonObject source) continue;

                // Erst prÃ¼fen, dann Ã¼bernehmen: ein Tippfehler soll die Zahlen gar nicht
                // erreichen, sondern den Lauf sofort anhalten (Entscheidung 8).
                ConfigSchema.Validate(typeof(T), source, path);

                bool machineSpecific = IsMachineSpecific(path);
                int overridesBefore = report.MachineOverrides.Count;

                Merge(target, source, prefix: string.Empty, label, report, machineSpecific);

                if (machineSpecific && report.MachineOverrides.Count == overridesBefore)
                    report.NeutralMachineFiles.Add(label);
            }

            T? result = merged.Deserialize<T>(SerializerOptions);
            if (result == null)
                throw new InvalidOperationException($"[CONFIG] Konfiguration vom Typ {typeof(T).Name} konnte nicht gelesen werden.");

            report.PrintOrigins();

            return new ConfigLoadResult<T>(result, report);
        }

        /// <summary>
        /// Wie eine Datei im Log und im Warnblock heiÃŸt: relativ zum Arbeitsverzeichnis, wenn sie
        /// darunter liegt, sonst absolut. Immer mit SchrÃ¤gstrichen.
        ///
        /// <para>
        /// Bewusst <b>nicht</b> nur der Dateiname: <c>config/simulation.local.json</c> und
        /// <c>src/projects/MantaAuv/simulation.local.json</c> heiÃŸen gleich, und der Warnblock
        /// soll sagen, welche der beiden zu lÃ¶schen ist.
        /// </para>
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
        /// Ist das eine maschinenspezifische Ausnahmedatei? Entscheidet allein der Dateiname.
        /// </summary>
        public static bool IsMachineSpecific(string path)
            => Path.GetFileName(path).EndsWith(MachineSpecificSuffix, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Der maschinenspezifische Nachbar einer Konfigurationsdatei:
        /// <c>simulation.json</c> â†’ <c>simulation.local.json</c>.
        /// </summary>
        public static string MachineVariantOf(string jsonFilePath)
        {
            if (IsMachineSpecific(jsonFilePath)) return jsonFilePath;

            string? directory = Path.GetDirectoryName(jsonFilePath);
            string name = Path.GetFileNameWithoutExtension(jsonFilePath) + MachineSpecificSuffix;

            return string.IsNullOrEmpty(directory) ? name : Path.Combine(directory, name);
        }

        /// <summary>
        /// LÃ¤dt eine Konfigurationsdatei eines Projekts Ã¼ber alle vier Schichten (TODO-22).
        /// </summary>
        /// <param name="defaults">Schicht 1 â€” die Code-Standards.</param>
        /// <param name="relativeName">
        /// Dateiname relativ zum Projektordner, z.B. <c>project.json</c> oder
        /// <c>solvers/su2.json</c>. SchrÃ¤gstriche sind erlaubt und werden umgesetzt.
        /// </param>
        /// <param name="projectDirectory">Der Ordner des Projekts (Schichten 2 und 4).</param>
        /// <param name="configDirectory">
        /// Das <c>config/</c>-Verzeichnis fÃ¼r Schicht 3. <c>null</c> = suchen; wird keines
        /// gefunden, entfÃ¤llt die Schicht ersatzlos â€” auf einem frischen Rechner ist der Ordner
        /// leer und damit in git gar nicht vorhanden.
        /// </param>
        public static ConfigLoadResult<T> LoadForProject<T>(
            T defaults,
            string relativeName,
            string projectDirectory,
            string? configDirectory = null) where T : class
        {
            string[] segments = relativeName.Split('/', '\\');

            var layers = new List<string>
            {
                // Schicht 2: dieses Projekt, alle Rechner.
                Path.Combine(new[] { projectDirectory }.Concat(segments).ToArray())
            };

            // Schicht 3: alle Projekte, dieser Rechner. Steht bewusst VOR Schicht 4 und damit
            // Ã¼ber dem Projekt â€” ein Projekt soll dem Rechner nicht seine Pfade umbiegen.
            string? resolvedConfig = ResolveConfigDirectory(configDirectory);
            if (resolvedConfig != null)
                layers.Add(MachineVariantOf(Path.Combine(new[] { resolvedConfig }.Concat(segments).ToArray())));

            // Schicht 4: dieses Projekt, dieser Rechner. Gewinnt Ã¼ber alles.
            layers.Add(MachineVariantOf(layers[0]));

            return LoadWithReport(defaults, layers.ToArray());
        }

        /// <summary>
        /// Schreibt ein Konfigurationsobjekt als JSON â€” nÃ¼tzlich, um eine Vorlagedatei
        /// mit den aktuellen Standardwerten zu erzeugen.
        ///
        /// <para>
        /// <b>Nicht</b> fÃ¼r die mitgelieferten Dateien benutzen: das Ergebnis hat keine
        /// Kommentare, und genau die ErklÃ¤rungen tragen einen neuen Nutzer.
        /// </para>
        /// </summary>
        public static void Save<T>(T configuration, string jsonFilePath) where T : class
        {
            string? directory = Path.GetDirectoryName(jsonFilePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            File.WriteAllText(jsonFilePath, JsonSerializer.Serialize(configuration, SerializerOptions));
        }

        /// <summary>
        /// Sucht das Konfigurationsverzeichnis: absoluter Pfad wird direkt genommen, ein
        /// relativer erst im Arbeitsverzeichnis und dann in den Ã¼bergeordneten Verzeichnissen.
        /// Letzteres, weil <c>dotnet run</c> im Projektverzeichnis startet, die Konfiguration
        /// aber im Repo-Root liegt. Gibt <c>null</c> zurÃ¼ck, wenn nichts gefunden wurde.
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
        /// Rekursives ZusammenfÃ¼hren: verschachtelte Objekte werden verschmolzen, alles andere
        /// ersetzt. Ein Projekt, das nur <c>{"Lift":"CL"}</c> schreibt, bekommt <c>Drag</c> aus
        /// der Stufe davor also dazu â€” gewollt, aber es heiÃŸt auch, dass man einen Eintrag nur
        /// Ã¼ber den Code-Standard wieder loswird.
        ///
        /// <para>
        /// Nebenbei wird protokolliert, welche Datei welchen SchlÃ¼ssel gewonnen hat, und ob eine
        /// maschinenspezifische Datei einen Wert tatsÃ¤chlich <b>verÃ¤ndert</b>. Wertgleiche
        /// EintrÃ¤ge zÃ¤hlen nicht: sie hebeln nichts aus und wÃ¼rden den Warnblock zumÃ¼llen.
        /// </para>
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
        /// Ein JSON-Wert, wie ihn ein Mensch im Warnblock lesen will: Zeichenketten ohne
        /// AnfÃ¼hrungszeichen, alles andere so, wie es in der Datei steht.
        /// </summary>
        private static string Describe(JsonNode? node)
        {
            if (node == null) return "(nicht gesetzt)";

            if (node is JsonValue value && value.TryGetValue(out string? text)) return text ?? "(null)";

            return node.ToJsonString();
        }
    }
}
