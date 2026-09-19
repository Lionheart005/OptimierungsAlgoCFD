using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Schreibt <c>Ergebnisse/&lt;Projekt&gt;/effective-config.json</c> — die Konfiguration, mit
    /// der dieser Lauf tatsächlich gerechnet hat, samt Herkunft je Schlüssel (TODO-25).
    ///
    /// <para>
    /// <b>Wozu:</b> die Ergebnis-CSV überlebt Monate, die JSON-Dateien werden bis dahin
    /// zehnmal geändert. Ohne diese Datei lässt sich später nicht mehr sagen, mit welcher
    /// Netzfeinheit oder welchem Laufumfang ein alter Datensatz entstanden ist. Sie liegt
    /// deshalb bewusst <b>neben</b> den Ergebnissen und nicht bei den Quellen.
    /// </para>
    ///
    /// <para>
    /// Das ist eine Ausgabedatei, kein Konfigurationsdokument — sie darf deshalb maschinell
    /// erzeugt werden. Die mitgelieferten <c>*.json</c> unter <c>src/projects/</c> dagegen
    /// nie: dort steckt die Erklärung in den Kommentaren, und ein Serialisierer wirft die weg.
    /// </para>
    /// </summary>
    public static class EffectiveConfigWriter
    {
        /// <summary>Dateiname im Ergebnisordner.</summary>
        public const string FileName = "effective-config.json";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        /// <summary>
        /// Schreibt die Datei in <paramref name="workingDirectory"/> und gibt den Pfad zurück.
        /// Muss aufgerufen werden, <b>nachdem</b> alle Solver-Optionen geladen sind — sonst
        /// fehlen sie in der Aufstellung.
        /// </summary>
        public static string Write(string workingDirectory, ProjectContext context)
        {
            Directory.CreateDirectory(workingDirectory);

            string path = Path.Combine(workingDirectory, FileName);
            File.WriteAllText(path, Build(context).ToJsonString(Options));

            return path;
        }

        /// <summary>Baut den Inhalt. Getrennt vom Schreiben, damit er ohne Datei prüfbar ist.</summary>
        public static JsonObject Build(ProjectContext context)
        {
            var sections = new JsonObject();

            foreach (LoadedConfiguration loaded in context.Loaded)
            {
                var origins = new JsonObject();
                foreach (KeyValuePair<string, string> entry in loaded.Report.Origins.OrderBy(e => e.Key, StringComparer.Ordinal))
                    origins[entry.Key] = entry.Value;

                sections[loaded.Name] = new JsonObject
                {
                    // Die Werte so, wie der Lauf sie benutzt hat.
                    ["Werte"] = JsonSerializer.SerializeToNode(loaded.Value, loaded.Value.GetType(), Options),
                    // Und je Schlüssel die Datei, die ihn gewonnen hat. Was hier fehlt, stand
                    // in keiner Datei und kam aus dem Code-Standard.
                    ["HerkunftJeSchluessel"] = origins
                };
            }

            var machineOverrides = new JsonArray();
            foreach (MachineOverride entry in context.Loaded.SelectMany(loaded => loaded.Report.MachineOverrides))
            {
                machineOverrides.Add(new JsonObject
                {
                    ["Datei"] = entry.File,
                    ["Schluessel"] = entry.Key,
                    ["Projektwert"] = entry.PreviousValue,
                    ["WirksamerWert"] = entry.NewValue
                });
            }

            return new JsonObject
            {
                ["Hinweis"] =
                    "Automatisch erzeugt. Haelt fest, womit dieser Lauf gerechnet hat -- die "
                    + "Quelldateien koennen sich seitdem geaendert haben. Nicht von Hand bearbeiten; "
                    + "Einstellungen gehoeren nach src/projects/<Name>/.",
                ["Projekt"] = context.Name,
                ["Projektordner"] = context.ProjectDirectory == null
                    ? null
                    : JsonConfigLoader.Label(context.ProjectDirectory),
                ["Zeitpunkt"] = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                ["Abschnitte"] = sections,
                // Normalerweise leer. Ist sie es nicht, hat eine gitignorierte Datei mitgeredet,
                // die niemand hochgeladen hat -- und das erklaert spaeter die Abweichung.
                ["MaschinenspezifischeUeberschreibungen"] = machineOverrides
            };
        }
    }
}
