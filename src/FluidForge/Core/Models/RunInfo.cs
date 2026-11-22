using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Contents of the run flight-recorder <c>results/&lt;Project&gt;/run-info.json</c>:
    /// records which code revision, tool versions, random seed, and host machine a run used.
    /// Purely informational — nothing in the program makes decisions based on it.
    /// </summary>
    public sealed class RunInfo
    {
        public const string StatusRunning = "running";
        public const string StatusCompleted = "completed";
        public const string StatusAborted = "aborted";

        public int Format { get; set; } = 1;
        public string Project { get; set; } = "";
        public string StartedAt { get; set; } = "";
        public string? FinishedAt { get; set; }

        /// <summary><c>running</c>, <c>completed</c>, or <c>aborted</c>. After a hard abort
        /// (reboot, <c>kill</c>), <c>running</c> remains; <c>status</c> detects this
        /// via the journal.</summary>
        public string Status { get; set; } = StatusRunning;

        public List<string> CommandLine { get; set; } = new();
        public int RandomSeed { get; set; }
        public RunInfoMachine Host { get; set; } = new();
        public RunInfoSource Source { get; set; } = new();

        /// <summary>Tool name → version, <c>"unknown"</c> if unavailable.</summary>
        public Dictionary<string, string> Tools { get; set; } = new();

        /// <summary>Fingerprint of the loaded configuration values (<see cref="ConfigFingerprint"/>).</summary>
        public string ConfigHash { get; set; } = "";

        public List<RunInfoResume> Resumes { get; set; } = new();
    }

    public sealed class RunInfoMachine
    {
        public string Name { get; set; } = "";
        public string OperatingSystem { get; set; } = "";
        public int ProcessorCount { get; set; }
        public string DotNet { get; set; } = "";
    }

    /// <summary>
    /// Source revision metadata. On the server read from <c>deploy-info.json</c> (written by
    /// <c>sim.ps1</c> during deploy); locally read directly from <c>.git</c> (<see cref="GitInfo"/>).
    /// </summary>
    public sealed class RunInfoSource
    {
        public string? Commit { get; set; }
        public string? Branch { get; set; }
        public string? DeployHash { get; set; }
        public string? DeployedAt { get; set; }

        /// <summary><c>"true"</c>, <c>"false"</c>, or <c>"unknown"</c>.</summary>
        public string UncommittedChanges { get; set; } = "unknown";
    }

    public sealed class RunInfoResume
    {
        public string Timestamp { get; set; } = "";
        public int AdoptedEvaluations { get; set; }
    }
}
