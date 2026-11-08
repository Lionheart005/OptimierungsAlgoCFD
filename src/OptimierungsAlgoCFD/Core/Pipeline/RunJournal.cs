using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Kopfzeile des Journals: womit der Lauf gestartet wurde. Bei <c>resume</c> kommt der Seed
    /// von hier, und Konfiguration bzw. Code-Stand werden dagegen geprüft (TODO-52).
    /// </summary>
    public sealed class RunJournalHeader
    {
        public int Format { get; set; } = 1;
        public string Projekt { get; set; } = "";
        public int ZufallsSeed { get; set; }
        public string KonfigurationsHash { get; set; } = "";

        /// <summary>Je Konfigurationsdatei ein Hash, damit eine Abweichung die Datei nennen kann.</summary>
        public SortedDictionary<string, string> KonfigurationsAbschnitte { get; set; } = new(StringComparer.Ordinal);

        public string? DeployHash { get; set; }
        public string? Commit { get; set; }
        public string Start { get; set; } = "";
    }

    /// <summary>Ein Eintrag des Journals, so wie er gelesen wurde.</summary>
    public sealed class RunJournalEntry
    {
        public int Index { get; init; }
        public int Iteration { get; init; }
        public int Variant { get; init; }
        public float Factor { get; init; }
        public string Tag { get; init; } = "";
        public Dictionary<string, float> Parameters { get; init; } = new();

        /// <summary>Der gespeicherte <see cref="ModelRecord"/> als JSON — Quelle jeder tiefen Kopie.</summary>
        internal string RecordJson { get; init; } = "";

        /// <summary>Die Zeile, wie sie in der Datei stand (für das Neuschreiben beim Kürzen).</summary>
        internal string Line { get; init; } = "";
    }

    /// <summary>Was <see cref="RunJournal.Read"/> in einer Journaldatei gefunden hat.</summary>
    public sealed class RunJournalContent
    {
        public RunJournalHeader Header { get; init; } = new();
        public List<RunJournalEntry> Entries { get; init; } = new();

        /// <summary>Die Schlusszeile ist vorhanden: der Lauf ist regulär zu Ende gegangen.</summary>
        public bool Completed { get; init; }

        /// <summary>Gemeldete Auffälligkeiten (halbe letzte Zeile, kaputte Zeile mittendrin).</summary>
        public List<string> Warnings { get; init; } = new();

        internal string HeaderLine { get; init; } = "";
    }

    /// <summary>
    /// Lauf-Journal für die Wiederaufnahme abgebrochener Läufe (TODO-51, F25/F26/F29).
    ///
    /// <para>
    /// <b>Prinzip Wiedergabe:</b> jedes Ergebnis von <see cref="WorkflowController.RunPipeline"/>
    /// wird als eine Zeile JSON mitgeschrieben und sofort auf die Platte gebracht. Bei
    /// <c>resume</c> läuft derselbe Code mit demselben Seed von vorn; solange das Journal reicht,
    /// antwortet <c>RunPipeline</c> von hier, statt zu rechnen. Danach wird echt gerechnet und
    /// weiter angehängt. Kein Optimierer muss dafür seinen inneren Zustand speichern.
    /// </para>
    ///
    /// <para>
    /// <b>Robust statt streng (F26):</b> passt ein Aufruf nicht zum nächsten Eintrag, endet die
    /// Wiedergabe mit Warnung. Das alte Journal wird gesichert, das aktuelle auf die passenden
    /// Einträge gekürzt, und ab dort wird gerechnet. Nichts bricht ab.
    /// </para>
    ///
    /// <para>
    /// Format (JSON Lines): erste Zeile Kopf, dann je Aufruf ein Eintrag mit <c>Index</c>, am Ende
    /// <c>{"Ende": …, "Status": "abgeschlossen"}</c>.
    /// </para>
    /// </summary>
    public sealed class RunJournal
    {
        public const string DirectoryName = "Wiederaufnahme";
        public const string FileName = "journal.jsonl";

        /// <summary>Erlaubte Abweichung des Netzfaktors bei der Wiedergabe.</summary>
        public const double FactorTolerance = 1e-6;

        /// <summary>Erlaubte relative Abweichung eines Parameters: |Δ| ≤ 1e-6 · max(1, |v|).</summary>
        public const double ParameterTolerance = 1e-6;

        private const string CompletedStatus = "abgeschlossen";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Ein Kennwert kann NaN sein; ohne diese Zeile würfe das Schreiben.
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };

        private readonly List<RunJournalEntry> _entries;
        private readonly int _replayable;
        private int _replayPosition;
        private bool _replayEnabled;

        private RunJournal(string filePath, RunJournalHeader header, List<RunJournalEntry> entries, bool replay)
        {
            FilePath = filePath;
            Header = header;
            _entries = entries;
            _replayable = replay ? entries.Count : 0;
            _replayEnabled = replay && entries.Count > 0;
        }

        public string FilePath { get; }
        public RunJournalHeader Header { get; }

        /// <summary>Einträge, die die Datei gerade enthält.</summary>
        public int EntryCount => _entries.Count;

        /// <summary>So viele Aufrufe wurden bisher aus dem Journal beantwortet.</summary>
        public int ReplayedCount => _replayPosition;

        /// <summary>So viele Einträge standen beim Öffnen zur Wiedergabe bereit.</summary>
        public int ReplayableCount => _replayable;

        /// <summary>Es stehen noch Einträge zur Wiedergabe an.</summary>
        public bool IsReplaying => _replayEnabled && _replayPosition < _replayable;

        public static string DirectoryFor(string workingDirectory) => Path.Combine(workingDirectory, DirectoryName);

        public static string PathFor(string workingDirectory) => Path.Combine(DirectoryFor(workingDirectory), FileName);

        // =====================================================================
        // Anlegen, Lesen, Fortsetzen
        // =====================================================================

        /// <summary>
        /// Beginnt ein neues Journal. Ein vorhandenes wird nicht gelöscht, sondern in
        /// <c>journal_&lt;yyyyMMdd_HHmmss&gt;.jsonl</c> umbenannt (F28). Gibt den Archivpfad
        /// über <paramref name="archivedTo"/> zurück, <c>null</c> wenn es keins gab.
        /// </summary>
        public static RunJournal StartNew(string workingDirectory, RunJournalHeader header, out string? archivedTo)
        {
            string path = PathFor(workingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            archivedTo = null;
            if (File.Exists(path))
            {
                archivedTo = UniqueSibling(path, "journal_" + Stamp());
                File.Move(path, archivedTo);
            }

            File.WriteAllText(path, Serialize(header) + "\n", new UTF8Encoding(false));
            return new RunJournal(path, header, new List<RunJournalEntry>(), replay: false);
        }

        /// <inheritdoc cref="StartNew(string, RunJournalHeader, out string?)"/>
        public static RunJournal StartNew(string workingDirectory, RunJournalHeader header)
            => StartNew(workingDirectory, header, out _);

        /// <summary>
        /// Liest eine Journaldatei. <c>null</c>, wenn sie nicht existiert.
        /// Eine kaputte <b>letzte</b> Zeile (Absturz beim Schreiben) wird mit Warnung ignoriert;
        /// eine kaputte Zeile mittendrin beendet die brauchbaren Einträge, ebenfalls mit Warnung.
        /// </summary>
        /// <exception cref="InvalidOperationException">Die Kopfzeile ist unlesbar.</exception>
        public static RunJournalContent? Read(string path)
        {
            if (!File.Exists(path)) return null;

            string[] lines = File.ReadAllLines(path, Encoding.UTF8)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToArray();

            RunJournalHeader? header = null;
            try
            {
                if (lines.Length > 0) header = JsonSerializer.Deserialize<RunJournalHeader>(lines[0], Options);
            }
            catch (JsonException)
            {
                header = null;
            }

            if (header == null)
                throw new InvalidOperationException(
                    $"[WIEDERAUFNAHME] Die Kopfzeile von {JsonConfigLoader.Label(path)} ist unlesbar. "
                    + "Der Lauf lässt sich nicht fortsetzen; neu starten mit .\\scripts\\sim.ps1 run -Project <Name>.");

            var entries = new List<RunJournalEntry>();
            var warnings = new List<string>();
            bool completed = false;

            for (int i = 1; i < lines.Length; i++)
            {
                bool isLast = i == lines.Length - 1;
                RunJournalEntry? entry = null;
                bool isEnd = false;

                try
                {
                    JsonObject node = JsonNode.Parse(lines[i])!.AsObject();

                    if (node.ContainsKey("Index"))
                    {
                        entry = ParseEntry(node, lines[i]);
                        if (entry.Index != entries.Count) entry = null;
                    }
                    else if (node.ContainsKey("Ende"))
                    {
                        isEnd = true;
                    }
                }
                catch (Exception)
                {
                    entry = null;
                }

                if (isEnd)
                {
                    completed = true;
                    continue;
                }

                if (entry != null)
                {
                    entries.Add(entry);
                    continue;
                }

                string warning = isLast
                    ? $"[WARNUNG] Wiederaufnahme: Die letzte Zeile des Journals ist unvollständig (Absturz beim Schreiben) und wird ignoriert. Diese Rechnung wird neu gemacht."
                    : $"[WARNUNG] Wiederaufnahme: Zeile {i + 1} des Journals ist unlesbar. Nur die {entries.Count} Einträge davor werden übernommen.";
                warnings.Add(warning);
                Console.WriteLine(warning);
                break;
            }

            return new RunJournalContent
            {
                Header = header,
                HeaderLine = lines[0],
                Entries = entries,
                Completed = completed,
                Warnings = warnings
            };
        }

        /// <summary>
        /// Öffnet ein gelesenes Journal zur Wiedergabe. Die Datei wird dabei sauber neu
        /// geschrieben (Kopf plus brauchbare Einträge), damit eine halbe letzte Zeile nicht vor
        /// den neuen Einträgen stehen bleibt.
        /// </summary>
        public static RunJournal Resume(string workingDirectory, RunJournalContent content)
        {
            string path = PathFor(workingDirectory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var entries = new List<RunJournalEntry>(content.Entries);
            Rewrite(path, content.HeaderLine.Length > 0 ? content.HeaderLine : Serialize(content.Header), entries);

            return new RunJournal(path, content.Header, entries, replay: true);
        }

        // =====================================================================
        // Wiedergabe und Mitschreiben
        // =====================================================================

        /// <summary>
        /// Beantwortet einen Aufruf aus dem Journal, wenn der nächste Eintrag passt. Gibt dann
        /// eine <b>tiefe Kopie</b> des gespeicherten Datensatzes zurück — jedes Mal ein neues
        /// Objekt, weil die Aufrufer es verändern (Fitness, Disqualifizierung, FineVerified).
        /// Passt er nicht, endet die Wiedergabe (F26) und es wird <c>false</c> geliefert.
        /// </summary>
        public bool TryReplay(
            int iteration, int variant, float factor, string tag,
            Dictionary<string, float> parameters, out ModelRecord? record)
        {
            record = null;
            if (!IsReplaying) return false;

            RunJournalEntry entry = _entries[_replayPosition];
            string? mismatch = Compare(entry, iteration, variant, factor, tag, parameters);

            if (mismatch != null)
            {
                StopReplayAtDeviation(mismatch);
                return false;
            }

            ModelRecord copy = JsonSerializer.Deserialize<ModelRecord>(entry.RecordJson, Options)
                               ?? throw new InvalidOperationException($"[WIEDERAUFNAHME] Eintrag {entry.Index} ist leer.");

            // Wie im echten Lauf: der Datensatz führt das übergebene Wörterbuch selbst.
            copy.ActiveParameters = parameters;

            _replayPosition++;
            record = copy;

            Console.WriteLine(
                $"[WIEDERAUFNAHME] Gen{iteration}_Var{variant}{(string.IsNullOrEmpty(tag) ? "" : $" ({tag})")} "
                + $"aus dem Journal übernommen ({_replayPosition}/{_replayable})");

            if (_replayPosition == _replayable)
                Console.WriteLine($"[WIEDERAUFNAHME] Alle {_replayable} gesicherten Rechnungen übernommen — ab hier wird wieder gerechnet.");

            return true;
        }

        /// <summary>
        /// Hängt das Ergebnis eines echten Aufrufs an. Muss <b>sofort bei der Rückgabe</b>
        /// aufgerufen werden, bevor die Aufrufer den Datensatz verändern. Die Zeile wird mit
        /// <c>Flush(true)</c> auf die Platte gebracht (F29).
        /// </summary>
        public void Append(
            int iteration, int variant, float factor, string tag,
            Dictionary<string, float> parameters, ModelRecord record)
        {
            var node = new JsonObject
            {
                ["Index"] = _entries.Count,
                ["Iteration"] = iteration,
                ["Variant"] = variant,
                ["Factor"] = JsonSerializer.SerializeToNode(factor, Options),
                ["Tag"] = tag,
                ["Parameters"] = JsonSerializer.SerializeToNode(parameters, Options),
                ["Record"] = JsonSerializer.SerializeToNode(record, Options)
            };

            string line = node.ToJsonString(Options);
            AppendLine(line);

            _entries.Add(ParseEntry(node, line));
        }

        /// <summary>Schreibt die Schlusszeile, nachdem der Lauf regulär zu Ende gegangen ist.</summary>
        public void MarkCompleted()
        {
            var node = new JsonObject
            {
                ["Ende"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                ["Status"] = CompletedStatus
            };
            AppendLine(node.ToJsonString(Options));
        }

        // =====================================================================
        // Intern
        // =====================================================================

        private void StopReplayAtDeviation(string mismatch)
        {
            int matched = _replayPosition;
            string backup = UniqueSibling(FilePath, "journal_abweichung_" + Stamp());

            try
            {
                File.Copy(FilePath, backup);
            }
            catch (Exception exception)
            {
                backup = $"(Sicherung fehlgeschlagen: {exception.Message})";
            }

            _entries.RemoveRange(matched, _entries.Count - matched);
            Rewrite(FilePath, Serialize(Header), _entries);
            _replayEnabled = false;

            Console.WriteLine(
                $"[WARNUNG] Wiederaufnahme: Aufruf {matched + 1} weicht vom Journal ab ({mismatch}). "
                + $"Die Wiedergabe endet hier; {matched} Rechnung(en) wurden übernommen, ab hier wird echt gerechnet. "
                + $"Das vorherige Journal liegt gesichert unter {JsonConfigLoader.Label(backup)}.");
        }

        private static string? Compare(
            RunJournalEntry entry, int iteration, int variant, float factor, string tag,
            Dictionary<string, float> parameters)
        {
            if (entry.Iteration != iteration || entry.Variant != variant)
                return $"erwartet Gen{entry.Iteration}_Var{entry.Variant}, erhalten Gen{iteration}_Var{variant}";

            if (!string.Equals(entry.Tag, tag ?? "", StringComparison.Ordinal))
                return $"Tag '{entry.Tag}' erwartet, '{tag}' erhalten";

            if (!Close(entry.Factor, factor, FactorTolerance, relative: false))
                return $"Netzfaktor {Format(entry.Factor)} erwartet, {Format(factor)} erhalten";

            var missing = entry.Parameters.Keys.Except(parameters.Keys).ToList();
            var extra = parameters.Keys.Except(entry.Parameters.Keys).ToList();
            if (missing.Count > 0 || extra.Count > 0)
                return $"andere Parameternamen (fehlen: {string.Join(", ", missing)}; neu: {string.Join(", ", extra)})";

            foreach (var stored in entry.Parameters)
            {
                float now = parameters[stored.Key];
                if (!Close(stored.Value, now, ParameterTolerance, relative: true))
                    return $"Parameter '{stored.Key}': {Format(stored.Value)} erwartet, {Format(now)} erhalten";
            }

            return null;
        }

        private static bool Close(float stored, float now, double tolerance, bool relative)
        {
            if (float.IsNaN(stored) || float.IsNaN(now)) return float.IsNaN(stored) && float.IsNaN(now);
            if (stored == now) return true;

            double limit = relative ? tolerance * Math.Max(1.0, Math.Abs((double)stored)) : tolerance;
            return Math.Abs((double)stored - now) <= limit;
        }

        private static string Format(float value) => value.ToString("R", CultureInfo.InvariantCulture);

        private static RunJournalEntry ParseEntry(JsonObject node, string line)
        {
            return new RunJournalEntry
            {
                Index = node["Index"]!.GetValue<int>(),
                Iteration = node["Iteration"]!.GetValue<int>(),
                Variant = node["Variant"]!.GetValue<int>(),
                Factor = node["Factor"].Deserialize<float>(Options),
                Tag = node["Tag"]?.GetValue<string>() ?? "",
                Parameters = node["Parameters"].Deserialize<Dictionary<string, float>>(Options) ?? new(),
                RecordJson = node["Record"]?.ToJsonString(Options)
                             ?? throw new JsonException("Eintrag ohne Record."),
                Line = line
            };
        }

        private static string Serialize(RunJournalHeader header) => JsonSerializer.Serialize(header, Options);

        private void AppendLine(string line)
        {
            byte[] bytes = new UTF8Encoding(false).GetBytes(line + "\n");
            using var stream = new FileStream(FilePath, FileMode.Append, FileAccess.Write, FileShare.Read);
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush(flushToDisk: true);
        }

        /// <summary>Schreibt Kopf und Einträge über eine Temp-Datei neu (nie ein halbes Journal).</summary>
        private static void Rewrite(string path, string headerLine, IEnumerable<RunJournalEntry> entries)
        {
            var builder = new StringBuilder();
            builder.Append(headerLine).Append('\n');
            foreach (RunJournalEntry entry in entries) builder.Append(entry.Line).Append('\n');

            string temporary = path + ".tmp";
            File.WriteAllText(temporary, builder.ToString(), new UTF8Encoding(false));
            File.Move(temporary, path, overwrite: true);
        }

        private static string Stamp() => DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);

        private static string UniqueSibling(string path, string baseName)
        {
            string directory = Path.GetDirectoryName(path)!;
            string candidate = Path.Combine(directory, baseName + ".jsonl");
            for (int n = 2; File.Exists(candidate); n++)
                candidate = Path.Combine(directory, $"{baseName}_{n}.jsonl");
            return candidate;
        }
    }
}
