using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>Result of <see cref="RunResume.Prepare"/>.</summary>
    public sealed class ResumePlan
    {
        /// <summary>The journal already contains its closing marker line: nothing left to do.</summary>
        public bool AlreadyCompleted { get; init; }

        /// <summary>Journal opened for replay (<c>null</c> when <see cref="AlreadyCompleted"/> is true).</summary>
        public RunJournal? Journal { get; init; }

        /// <summary>Seed from the journal header — it always takes precedence.</summary>
        public int Seed { get; init; }

        /// <summary>Number of saved evaluations ready to be replayed.</summary>
        public int StoredSimulations { get; init; }

        /// <summary>Non-fatal warnings (different seed in config, changed source code state).</summary>
        public List<string> Warnings { get; init; } = new();
    }

    /// <summary>
    /// Validation and preparation for <c>--resume</c> (TODO-52, F27/F28). Separated from
    /// <c>Program.cs</c> so it can be unit-tested without kernel or solver dependencies.
    /// </summary>
    public static class RunResume
    {
        /// <summary>Builds the header for a new journal.</summary>
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
        /// Locates the journal, validates configuration, seed, and source code state, and opens it for replay.
        /// </summary>
        /// <exception cref="InvalidOperationException">
        /// No journal exists, or the configuration changed since the run started. Messages begin with
        /// <c>[RESUME]</c> and are reported as user errors (concise message, exit code 1).
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
                    $"{OutputConventions.ResumeTag} No aborted run found ({JsonConfigLoader.Label(path)} is missing). "
                    + $"Start a new run with .\\scripts\\sim.ps1 run -Project {projectName}.");

            if (content.Completed)
                return new ResumePlan { AlreadyCompleted = true, Seed = content.Header.ZufallsSeed, StoredSimulations = content.Entries.Count };

            RunJournalHeader header = content.Header;

            // F27: changed physics or run configuration mid-run must never happen silently.
            IReadOnlyList<string> changed = ConfigFingerprint.Differences(header.KonfigurationsAbschnitte, configurationSections);
            if (changed.Count > 0)
                throw new InvalidOperationException(
                    $"{OutputConventions.ResumeTag} Configuration changed since the run started: {string.Join(", ", changed)}. "
                    + "Results with modified settings would no longer be comparable, so resume is aborted. "
                    + "Either revert the change (the run's original settings are recorded in effective-config.json) "
                    + $"or start fresh with .\\scripts\\sim.ps1 run -Project {projectName}.");

            var warnings = new List<string>();

            if (configuredSeed != 0 && configuredSeed != header.ZufallsSeed)
                warnings.Add(
                    $"[HINT] simulation.json specifies \"RandomSeed\": {configuredSeed}, but this run was started with "
                    + $"{header.ZufallsSeed}. Resuming continues with the journal seed ({header.ZufallsSeed}).");

            bool deployDiffers = header.DeployHash != null && source.DeployHash != null && header.DeployHash != source.DeployHash;
            bool commitDiffers = header.Commit != null && source.Commit != null && header.Commit != source.Commit;
            if (deployDiffers || commitDiffers)
                warnings.Add(
                    $"{OutputConventions.WarningTag} Source code version differs from the start of the run "
                    + $"(start: {header.DeployHash ?? header.Commit}, now: {source.DeployHash ?? source.Commit}). "
                    + "Continuing anyway; if an evaluation diverges as a result, replay will stop there with a warning.");

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
