namespace FluidForge.Core
{
    /// <summary>
    /// Centralized output directory names, artifact filenames, and structured log tags.
    /// Shared across Core, Solvers, CLI, and tests to avoid brittle hardcoded literals.
    /// </summary>
    public static class OutputConventions
    {
        // Output directory names
        public const string ResultsRootDirectory = "results";
        public const string ErrorLogsDirectory = "error-logs";
        public const string AnalysisDirectory = "analysis";
        public const string HistoriesSubDirectory = "histories";
        public const string CalibrationDirectory = "calibration";
        public const string ResumeDirectory = "resume";
        public const string SourceSnapshotDirectory = "source-snapshot";

        // Output filenames
        public const string ResultsCsvFileName = "results.csv";
        public const string BayesHistoryCsvFileName = "Bayes_History.csv";
        public const string VerificationCsvFileName = "verification.csv";
        public const string JournalFileName = "journal.jsonl";
        public const string EffectiveConfigFileName = "effective-config.json";
        public const string RunInfoFileName = "run-info.json";

        // Mesh refinement source labels
        public const string ManualMeshSource = "MANUAL from simulation.json";

        // Structured log tags
        public const string SystemTag = "[SYSTEM]";
        public const string ConfigTag = "[CONFIG]";
        public const string CalibrationTag = "[CALIBRATION]";
        public const string WarningTag = "[WARNING]";
        public const string ErrorTag = "[ERROR]";
        public const string MeshTag = "[MESH]";
        public const string SolverTag = "[SOLVER]";
        public const string BayesTag = "[BAYES]";
        public const string IdwTag = "[IDW]";
        public const string ResumeTag = "[RESUME]";
        public const string VerificationTag = "[VERIFICATION]";
        public const string AutoStopTag = "[AUTOSTOP]";
        public const string RecheckTag = "[RECHECK]";
        public const string ChampionBanner = "OVERALL CHAMPION";
    }
}
