using System;
using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>Ergebnis von <see cref="RunResume.Prepare"/>.</summary>
    public sealed class ResumePlan
    {
        /// <summary>Das Journal hat seine Schlusszeile: nichts zu tun.</summary>
        public bool AlreadyCompleted { get; init; }

        /// <summary>Zur Wiedergabe geöffnetes Journal (<c>null</c> bei <see cref="AlreadyCompleted"/>).</summary>
        public RunJournal? Journal { get; init; }

        /// <summary>Seed aus der Kopfzeile des Journals — er gewinnt immer.</summary>
        public int Seed { get; init; }

        /// <summary>So viele Rechnungen stehen zur Übernahme bereit.</summary>
        public int StoredSimulations { get; init; }

        /// <summary>Hinweise, die den Lauf nicht aufhalten (anderer Seed, anderer Code-Stand).</summary>
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Prüfungen und Vorbereitung für <c>--resume</c> (TODO-52, F27/F28). Getrennt von
    /// <c>Program.cs</c>, damit sie ohne Kernel und ohne Solver prüfbar sind.
    /// </summary>
    public static class RunResume
    {
        /// <summary>Kopfzeile eines neuen Journals.</summary>
        public static RunJournalHeader CreateHeader(
            string project, int seed, SortedDictionary<string, string> configurationSections, RunInfoSource source)
        {
            return new RunJournalHeader
            {
                Projekt = project,
                ZufallsSeed = seed,
                KonfigurationsAbschnitte = new SortedDictionary<string, string>(configurationSections, StringComparer.Ordinal),
                KonfigurationsHash = ConfigFingerprint.Combine(configurationSections),
                DeployHash = source.DeployHash,
                Commit = source.Commit,
                Start = RunInfoWriter.Now()
            };
        }

        /// <summary>
        /// Sucht das Journal, prüft Konfiguration, Seed und Code-Stand und öffnet es zur Wiedergabe.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// Kein Journal vorhanden, oder die Konfiguration hat sich seit dem Start geändert. Die
        /// Meldungen beginnen mit <c>[WIEDERAUFNAHME]</c> und gelten damit als Bedienfehler
        /// (kurze Meldung, Exit-Code 1).
        /// </exception>
        public static ResumePlan Prepare(
            string workingDirectory,
            string projectName,
            SortedDictionary<string, string> configurationSections,
            int configuredSeed,
            RunInfoSource source)
        {
            string path = RunJournal.PathFor(workingDirectory);
            RunJournalContent? content = RunJournal.Read(path);

            if (content == null)
                throw new InvalidOperationException(
                    $"[WIEDERAUFNAHME] Kein abgebrochener Lauf gefunden ({JsonConfigLoader.Label(path)} fehlt). "
                    + $"Neu starten mit .\\scripts\\sim.ps1 run -Project {projectName}.");

            if (content.Completed)
                return new ResumePlan { AlreadyCompleted = true, Seed = content.Header.ZufallsSeed, StoredSimulations = content.Entries.Count };

            RunJournalHeader header = content.Header;

            // F27: eine geänderte Physik mitten im Lauf darf nie still passieren.
            IReadOnlyList<string> changed = ConfigFingerprint.Differences(header.KonfigurationsAbschnitte, configurationSections);
            if (changed.Count > 0)
                throw new InvalidOperationException(
                    $"[WIEDERAUFNAHME] Die Konfiguration hat sich seit dem Start des Laufs geändert: {string.Join(", ", changed)}. "
                    + "Mit geänderten Werten wären die Ergebnisse nicht mehr vergleichbar, deshalb wird nicht fortgesetzt. "
                    + "Entweder die Änderung rückgängig machen (die Werte des Laufs stehen in effective-config.json) "
                    + $"oder neu starten mit .\\scripts\\sim.ps1 run -Project {projectName}.");

            var warnings = new List<string>();

            if (configuredSeed != 0 && configuredSeed != header.ZufallsSeed)
                warnings.Add(
                    $"[HINWEIS] In simulation.json steht \"RandomSeed\": {configuredSeed}, der Lauf wurde aber mit "
                    + $"{header.ZufallsSeed} gestartet. Für die Fortsetzung gilt der Wert aus dem Journal ({header.ZufallsSeed}).");

            bool deployDiffers = header.DeployHash != null && source.DeployHash != null && header.DeployHash != source.DeployHash;
            bool commitDiffers = header.Commit != null && source.Commit != null && header.Commit != source.Commit;
            if (deployDiffers || commitDiffers)
                warnings.Add(
                    "[WARNUNG] Der Code-Stand weicht vom Start des Laufs ab "
                    + $"(Start: {header.DeployHash ?? header.Commit}, jetzt: {source.DeployHash ?? source.Commit}). "
                    + "Es wird trotzdem fortgesetzt; weicht dadurch eine Rechnung ab, endet die Wiedergabe dort mit Warnung.");

            foreach (string warning in warnings) Console.WriteLine(warning);

            RunJournal journal = RunJournal.Resume(workingDirectory, content);

            return new ResumePlan
            {
                Journal = journal,
                Seed = header.ZufallsSeed,
                StoredSimulations = journal.ReplayableCount,
                Warnings = warnings
            };
        }
    }
}
