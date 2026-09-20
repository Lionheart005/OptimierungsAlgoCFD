using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Ein Schlüssel, den eine maschinenspezifische <c>*.local.json</c> gegenüber den
    /// eingecheckten Schichten tatsächlich verändert.
    /// </summary>
    /// <param name="File">Dateiname der local-Datei.</param>
    /// <param name="Key">Schlüsselpfad, z.B. <c>MaxIterations</c> oder <c>BaseParameters.Length</c>.</param>
    /// <param name="PreviousValue">Was ohne diese Datei gegolten hätte.</param>
    /// <param name="NewValue">Was jetzt gilt.</param>
    public sealed record MachineOverride(string File, string Key, string PreviousValue, string NewValue);

    /// <summary>
    /// Was beim Laden einer Konfiguration tatsächlich passiert ist: welcher Schlüssel aus welcher
    /// Datei gewonnen hat, und welche maschinenspezifische Datei dem Projekt dazwischengefunkt hat.
    ///
    /// <para>
    /// Der Bericht ist bewusst Daten und kein reiner Konsolen-Nebeneffekt: TODO-25 schreibt ihn
    /// zusätzlich als <c>effective-config.json</c> neben die Ergebnisse, damit ein alter Lauf
    /// rekonstruierbar bleibt, egal was danach an den Dateien passiert.
    /// </para>
    /// </summary>
    public sealed class ConfigLoadReport
    {
        /// <summary>Schlüsselpfad → Datei, aus der der wirksame Wert stammt. Fehlt ein Schlüssel hier, gilt der Code-Standard.</summary>
        public Dictionary<string, string> Origins { get; } = new();

        /// <summary>Schlüssel, die eine <c>*.local.json</c> gegenüber den eingecheckten Schichten verändert.</summary>
        public List<MachineOverride> MachineOverrides { get; } = new();

        /// <summary>
        /// Vorhandene <c>*.local.json</c>-Dateien, die nichts verändern. Sie sind kein Problem,
        /// sollen aber auch nicht unsichtbar bleiben — sonst sucht jemand später den Grund für
        /// eine Abweichung in einer Datei, die gar keine verursacht.
        /// </summary>
        public List<string> NeutralMachineFiles { get; } = new();

        /// <summary>
        /// Der auffällige Startblock aus TODO-22. Sollstand nach dem Umbau ist, dass es
        /// <b>nirgends</b> eine <c>*.local.json</c> gibt: die Programmpfade sind PATH-relativ,
        /// alles andere ist Projektsache. Jede vorhandene Datei ist damit ein Sonderfall, den
        /// das Programm bei jedem Start laut meldet — mit jedem einzelnen Schlüssel, den sie
        /// dem Projekt aushebelt, und beiden Werten.
        ///
        /// <para>
        /// Wertgleiche Schlüssel stehen bewusst nicht drin: sonst ginge die eigentliche Meldung
        /// im Rauschen unter. Eine local-Datei, die nichts ändert, erzeugt deshalb gar keinen
        /// Block, sondern nur eine Zeile.
        /// </para>
        /// </summary>
        /// <param name="projectName">
        /// Nur für den Hinweistext, wohin die Werte gehören. Ohne Angabe steht dort ein Platzhalter.
        /// </param>
        public void PrintMachineOverrideWarning(string? projectName = null)
        {
            foreach (string file in NeutralMachineFiles)
                Console.WriteLine($"[CONFIG] {file}: vorhanden, ändert aber keinen Wert.");

            if (MachineOverrides.Count == 0) return;

            string target = string.IsNullOrWhiteSpace(projectName) ? "<Projekt>" : projectName.Trim();
            int keyWidth = Math.Min(28, Math.Max(20, MachineOverrides.Max(entry => entry.Key.Length) + 2));

            var files = MachineOverrides.GroupBy(entry => entry.File).ToList();

            Console.WriteLine("==================================================================");
            Console.WriteLine(files.Count == 1
                ? "[WARNUNG] Eine maschinenspezifische Datei überschreibt dieses Projekt:"
                : $"[WARNUNG] {files.Count} maschinenspezifische Dateien überschreiben dieses Projekt:");

            foreach (var group in files)
            {
                Console.WriteLine($"          {group.Key}");

                foreach (MachineOverride entry in group)
                {
                    Console.WriteLine($"          {entry.Key.PadRight(keyWidth)}"
                                      + $"Projekt: {entry.PreviousValue}   ->   local: {entry.NewValue}");
                }
            }

            Console.WriteLine(files.Count == 1
                ? "          Normalerweise sollte es diese Datei nicht geben. Gehören die"
                : "          Normalerweise sollte es diese Dateien nicht geben. Gehören die");
            Console.WriteLine($"          Werte zum Projekt, dann nach src/projects/{target}/ übernehmen");
            Console.WriteLine(files.Count == 1 ? "          und die Datei löschen." : "          und die Dateien löschen.");
            Console.WriteLine("==================================================================");
        }

        /// <summary>
        /// Gibt je Schlüssel die gewinnende Datei aus — nach Datei gruppiert, damit jeder
        /// Schlüssel genau einmal erscheint und die Liste lesbar bleibt. Vorher stand im Log
        /// nur „N Einträge übernommen“, was bei vier Schichten nichts mehr aussagt.
        /// </summary>
        public void PrintOrigins()
        {
            if (Origins.Count == 0) return;

            Console.WriteLine("[CONFIG] Herkunft der wirksamen Werte (alles Ungenannte: Code-Standard):");

            foreach (var group in Origins.GroupBy(entry => entry.Value).OrderBy(group => group.Key, StringComparer.Ordinal))
            {
                string keys = string.Join(", ", group.Select(entry => entry.Key).OrderBy(key => key, StringComparer.Ordinal));
                Console.WriteLine($"           {group.Key}");
                Console.WriteLine($"             {keys}");
            }
        }
    }

    /// <summary>
    /// Eine geladene Konfigurationsdatei, wie sie in die <c>effective-config.json</c> wandert.
    /// </summary>
    /// <param name="Name">Name relativ zum Projektordner, z.B. <c>solvers/su2.json</c>.</param>
    /// <param name="Value">Das fertige Konfigurationsobjekt, wie der Lauf es benutzt hat.</param>
    /// <param name="Report">Woher jeder Schlüssel kam.</param>
    public sealed record LoadedConfiguration(string Name, object Value, ConfigLoadReport Report);

    /// <summary>Ergebnis eines Ladevorgangs: der fertige Wert und der Bericht dazu.</summary>
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
