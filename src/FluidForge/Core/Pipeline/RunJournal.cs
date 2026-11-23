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

namespace FluidForge.Core
{
    /// <summary>
    /// Journal header: records how the run was started. On <c>resume</c>, the seed is restored
    /// from here and configuration and source state are validated against it (TODO-52).
    /// </summary>
    public sealed class RunJournalHeader
    {
        public int Format { get; set; } = 1;
        public string Projekt { get; set; } = "";
        public int ZufallsSeed { get; set; }
        public string KonfigurationsHash { get; set; } = "";

        /// <summary>One hash per configuration file so any mismatch can name the exact file.</summary>
        public SortedDictionary<string, string> KonfigurationsAbschnitte { get; set; } = new(StringComparer.Ordinal);

        public string? DeployHash { get; set; }
        public string? Commit { get; set; }
        public string Start { get; set; } = "";
    }

    /// <summary>A single journal entry as read from disk.</summary>
    public sealed class RunJournalEntry
    {
        public int Index { get; init; }
        public int Iteration { get; init; }
        public int Variant { get; init; }
        public float Factor { get; init; }
        public string Tag { get; init; } = "";
        public string FidelityLevel { get; init; } = FidelityLevels.High;
        public Dictionary<string, float> Parameters { get; init; } = new();
        public double? DurationSeconds { get; init; }

        /// <summary>Stored <see cref="ModelRecord"/> as JSON — source for every deep copy.</summary>
        internal string RecordJson { get; init; } = "";

        /// <summary>Original file line (used when rewriting on truncation).</summary>
        internal string Line { get; init; } = "";
    }

    /// <summary>Parsed contents of a journal file returned by <see cref="RunJournal.Read"/>.</summary>
    public sealed class RunJournalContent
    {
        public RunJournalHeader Header { get; init; } = new();
        public List<RunJournalEntry> Entries { get; init; } = new();

        /// <summary>True when the closing marker line is present: the run finished cleanly.</summary>
        public bool Completed { get; init; }

        /// <summary>Reported anomalies (truncated last line, corrupt line in the middle).</summary>
        public List<string> Warnings { get; init; } = new();

        internal string HeaderLine { get; init; } = "";
    }

    /// <summary>
    /// Run journal for resuming aborted runs (TODO-51, F25/F26/F29).
    ///
    /// <para>
    /// <b>Replay principle:</b> every result from <see cref="WorkflowController.RunPipeline"/> is
    /// written as one JSON line and flushed to disk immediately. On <c>resume</c>, the same code
    /// starts from the beginning with the same seed; while journal entries remain,
    /// <c>RunPipeline</c> answers from the journal instead of re-running solvers. Afterward,
    /// live evaluations resume and append to the journal. No optimizer needs to serialize its
    /// internal state.
    /// </para>
    ///
    /// <para>
    /// <b>Resilient over strict (F26):</b> if a call mismatches the next entry, replay stops with
    /// a warning. The old journal is backed up, the active one is truncated to the matching prefix,
    /// and live execution continues from there without crashing.
    /// </para>
    ///
    /// <para>
    /// Format (JSON Lines): header on line 1, one entry with <c>Index</c> per call, and
    /// <c>{"End": ..., "Status": "completed"}</c> at completion.
    /// </para>
    /// </summary>
    public sealed class RunJournal
    {
        public const string DirectoryName = OutputConventions.ResumeDirectory;
        public const string FileName = OutputConventions.JournalFileName;

        /// <summary>Allowed mesh factor deviation during replay.</summary>
        public const double FactorTolerance = 1e-6;

        /// <summary>Allowed relative parameter deviation: |Δ| &lt;= 1e-6 * max(1, |v|).</summary>
        public const double ParameterTolerance = 1e-6;

        private const string CompletedStatus = "completed";

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            // Metrics can be NaN; without this setting serialization would throw.
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

        /// <summary>Number of entries currently in the file.</summary>
        public int EntryCount => _entries.Count;

        /// <summary>Number of calls answered from the journal so far.</summary>
        public int ReplayedCount => _replayPosition;

        /// <summary>Total number of entries available for replay when opened.</summary>
        public int ReplayableCount => _replayable;

        /// <summary>True while entries remain to be replayed.</summary>
        public bool IsReplaying => _replayEnabled && _replayPosition < _replayable;

        /// <summary>Recorded duration in seconds of the most recent entry replayed via <see cref="TryReplay"/>.</summary>
        public double? LastReplayedDurationSeconds { get; private set; }

        public static string DirectoryFor(string workingDirectory) => Path.Combine(workingDirectory, DirectoryName);

        public static string PathFor(string workingDirectory) => Path.Combine(DirectoryFor(workingDirectory), FileName);

        // =====================================================================
        // Creation, reading, resuming
        // =====================================================================

        /// <summary>
        /// Starts a new journal. Any existing journal is not deleted, but renamed to
        /// <c>journal_&lt;yyyyMMdd_HHmmss&gt;.jsonl</c> (F28). Outputs the archive path via
        /// <paramref name="archivedTo"/>, or <c>null</c> if none existed.
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
        /// Reads a journal file. Returns <c>null</c> if it does not exist.
        /// A truncated <b>last</b> line (crash during write) is ignored with a warning;
        /// a corrupt line in the middle stops reading usable entries, also with a warning.
        /// </summary>
        /// <exception cref="InvalidOperationException">The header line is unreadable.</exception>
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
                    $"{OutputConventions.ResumeTag} The header line of {JsonConfigLoader.Label(path)} is unreadable. "
                    + "Cannot resume this run; start fresh with .\\scripts\\sim.ps1 run -Project <Name>.");

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
                    else if (node.ContainsKey("End") || node.ContainsKey("Ende"))
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
                    ? $"{OutputConventions.WarningTag} Resume: the last line of the journal is incomplete (crash during write) and will be ignored. That evaluation will be recomputed."
                    : $"{OutputConventions.WarningTag} Resume: line {i + 1} of the journal is unreadable. Only the {entries.Count} preceding entries will be reused.";
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
        /// Opens a parsed journal for replay. Rewrites the file cleanly (header plus valid entries)
        /// so a partial trailing line never remains ahead of newly appended entries.
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
        // Replay and recording
        // =====================================================================

        /// <summary>
        /// Answers a call from the journal when the next entry matches. Returns a <b>deep copy</b>
        /// of the stored record — a fresh object each time because callers mutate it (Fitness,
        /// Disqualified, FineVerified). On mismatch, replay stops (F26) and returns <c>false</c>.
        /// </summary>
        public bool TryReplay(
            int iteration, int variant, float factor, string tag,
            Dictionary<string, float> parameters, out ModelRecord? record,
            string? fidelityLevel = null)
        {
            record = null;
            LastReplayedDurationSeconds = null;
            if (!IsReplaying) return false;

            RunJournalEntry entry = _entries[_replayPosition];
            string? mismatch = Compare(entry, iteration, variant, factor, tag, parameters, fidelityLevel);

            if (mismatch != null)
            {
                StopReplayAtDeviation(mismatch);
                return false;
            }

            ModelRecord copy = JsonSerializer.Deserialize<ModelRecord>(entry.RecordJson, Options)
                               ?? throw new InvalidOperationException($"{OutputConventions.ResumeTag} Entry {entry.Index} is empty.");

            // As in a live run, the record references the caller's parameter dictionary directly.
            copy.ActiveParameters = parameters;

            _replayPosition++;
            LastReplayedDurationSeconds = entry.DurationSeconds;
            record = copy;

            Console.WriteLine(
                $"{OutputConventions.ResumeTag} Gen{iteration}_Var{variant}{(string.IsNullOrEmpty(tag) ? "" : $" ({tag})")} "
                + $"restored from journal ({_replayPosition}/{_replayable})");

            if (_replayPosition == _replayable)
                Console.WriteLine($"{OutputConventions.ResumeTag} All {_replayable} saved evaluation(s) restored — resuming live simulation from here.");

            return true;
        }

        /// <summary>
        /// Appends the result of a live call. Must be called <b>immediately on return</b> before
        /// callers mutate the record. Flushed to disk with <c>Flush(true)</c> (F29).
        /// </summary>
        public void Append(
            int iteration, int variant, float factor, string tag,
            Dictionary<string, float> parameters, ModelRecord record,
            double? durationSeconds = null,
            string? fidelityLevel = null)
        {
            string resolvedFidelity = !string.IsNullOrWhiteSpace(fidelityLevel)
                ? fidelityLevel
                : (!string.IsNullOrWhiteSpace(record.FidelityLevel) ? record.FidelityLevel : FidelityLevels.High);

            var node = new JsonObject
            {
                ["Index"] = _entries.Count,
                ["Iteration"] = iteration,
                ["Variant"] = variant,
                ["Factor"] = JsonSerializer.SerializeToNode(factor, Options),
                ["Tag"] = tag,
                ["FidelityLevel"] = resolvedFidelity,
                ["Parameters"] = JsonSerializer.SerializeToNode(parameters, Options),
                ["Record"] = JsonSerializer.SerializeToNode(record, Options)
            };

            if (durationSeconds.HasValue && double.IsFinite(durationSeconds.Value))
            {
                node["DurationSeconds"] = durationSeconds.Value;
            }

            string line = node.ToJsonString(Options);
            AppendLine(line);

            _entries.Add(ParseEntry(node, line));
        }

        /// <summary>Writes the closing marker line after the run has completed normally.</summary>
        public void MarkCompleted()
        {
            var node = new JsonObject
            {
                ["End"] = DateTimeOffset.Now.ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture),
                ["Status"] = CompletedStatus
            };
            AppendLine(node.ToJsonString(Options));
        }

        // =====================================================================
        // Internal helpers
        // =====================================================================

        private void StopReplayAtDeviation(string mismatch)
        {
            int matched = _replayPosition;
            string backup = UniqueSibling(FilePath, "journal_deviation_" + Stamp());

            try
            {
                File.Copy(FilePath, backup);
            }
            catch (Exception exception)
            {
                backup = $"(backup failed: {exception.Message})";
            }

            _entries.RemoveRange(matched, _entries.Count - matched);
            Rewrite(FilePath, Serialize(Header), _entries);
            _replayEnabled = false;

            Console.WriteLine(
                $"{OutputConventions.WarningTag} Resume: call {matched + 1} deviates from the journal ({mismatch}). "
                + $"Replay stops here; {matched} evaluation(s) were restored and live computation resumes now. "
                + $"The previous journal has been backed up to {JsonConfigLoader.Label(backup)}.");
        }

        private static string? Compare(
            RunJournalEntry entry, int iteration, int variant, float factor, string tag,
            Dictionary<string, float> parameters,
            string? fidelityLevel)
        {
            if (entry.Iteration != iteration || entry.Variant != variant)
                return $"expected Gen{entry.Iteration}_Var{entry.Variant}, got Gen{iteration}_Var{variant}";

            if (!string.Equals(entry.Tag, tag ?? "", StringComparison.Ordinal))
                return $"expected tag '{entry.Tag}', got '{tag}'";

            if (fidelityLevel != null && !string.Equals(entry.FidelityLevel, fidelityLevel, StringComparison.OrdinalIgnoreCase))
                return $"expected fidelity '{entry.FidelityLevel}', got '{fidelityLevel}'";

            if (!Close(entry.Factor, factor, FactorTolerance, relative: false))
                return $"expected mesh factor {Format(entry.Factor)}, got {Format(factor)}";

            var missing = entry.Parameters.Keys.Except(parameters.Keys).ToList();
            var extra = parameters.Keys.Except(entry.Parameters.Keys).ToList();
            if (missing.Count > 0 || extra.Count > 0)
                return $"parameter names differ (missing: {string.Join(", ", missing)}; added: {string.Join(", ", extra)})";

            foreach (var stored in entry.Parameters)
            {
                float now = parameters[stored.Key];
                if (!Close(stored.Value, now, ParameterTolerance, relative: true))
                    return $"parameter '{stored.Key}': expected {Format(stored.Value)}, got {Format(now)}";
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
            JsonNode? durationNode = node["DurationSeconds"];
            double? durationSeconds = durationNode != null
                ? durationNode.GetValue<double>()
                : null;

            return new RunJournalEntry
            {
                Index = node["Index"]!.GetValue<int>(),
                Iteration = node["Iteration"]!.GetValue<int>(),
                Variant = node["Variant"]!.GetValue<int>(),
                Factor = node["Factor"].Deserialize<float>(Options),
                Tag = node["Tag"]?.GetValue<string>() ?? "",
                FidelityLevel = node["FidelityLevel"]?.GetValue<string>() ?? FidelityLevels.High,
                Parameters = node["Parameters"].Deserialize<Dictionary<string, float>>(Options) ?? new(),
                DurationSeconds = durationSeconds,
                RecordJson = node["Record"]?.ToJsonString(Options)
                             ?? throw new JsonException("Entry is missing Record."),
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

        /// <summary>Rewrites header and entries via a temp file (never leaves a half-written journal).</summary>
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
