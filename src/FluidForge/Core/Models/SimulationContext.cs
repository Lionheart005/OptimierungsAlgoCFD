using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace FluidForge.Core
{
    /// <summary>
    /// Central runtime database (formerly SimulationData).
    /// Combines framework configuration, project configuration, and runtime state.
    /// Directory creation is separated from the constructor.
    /// </summary>
    public class SimulationContext
    {
        public SimulationConfig Config { get; set; }
        public ProjectConfig Project { get; set; }
        public string WorkingDirectory { get; set; }
        public List<ModelRecord> History { get; set; } = new();

        /// <summary>
        /// Current operating point of the optimization — the parameter set around which
        /// mutations or sampling occur. Starts as a copy of <see cref="ProjectConfig.BaseParameters"/>
        /// and is updated by the algorithm.
        ///
        /// Kept here rather than in <see cref="ProjectConfig"/>: the configuration comes from
        /// JSON and is an input specification, not mutable runtime state.
        /// </summary>
        public Dictionary<string, float> CurrentBaseParameters { get; set; } = new();

        /// <summary>
        /// Current mutation strength (sigma) per parameter. Starts as a copy of
        /// <see cref="ProjectConfig.MaxDeviations"/>; the evolutionary algorithm tightens
        /// or widens it across iterations.
        /// </summary>
        public Dictionary<string, float> CurrentDeviations { get; set; } = new();

        /// <summary>
        /// Effective mesh refinement factor for this run. Starts at <see cref="SimulationConfig.MeshRefinementFactor"/>
        /// and may be overridden by calibration or verification runs without mutating the config.
        /// </summary>
        public float EffectiveMeshRefinementFactor { get; set; } = 1.0f;

        /// <summary>
        /// Origin of the effective mesh refinement factor.
        /// Default: <see cref="OutputConventions.ManualMeshSource"/>, or calibrated in auto mode.
        /// </summary>
        public string MeshRefinementSource { get; set; } = OutputConventions.ManualMeshSource;

        /// <summary>
        /// Relative or absolute path to the calibration report, if calibrated.
        /// </summary>
        public string? CalibrationReportPath { get; set; } = null;

        /// <summary>
        /// Optional tag for the current run (e.g., "Cal3" or "Verif").
        /// Included in mesh filenames to prevent overwriting.
        /// </summary>
        public string CurrentRunTag { get; set; } = "";

        /// <summary>
        /// Upper limit on MEASURED cell count for the current evaluation (0 = unlimited).
        /// Calibration sets this for its stages: if the mesh exceeds it, SU2 is skipped.
        /// </summary>
        public long CellLimit { get; set; }

        /// <summary>
        /// Fidelity level label for the currently executing pipeline call (<c>Coarse</c>, <c>High</c>, or <c>Verification</c>).
        /// </summary>
        public string CurrentFidelityLevel { get; set; } = FidelityLevels.High;

        /// <summary>
        /// Optional solver iteration cap for the currently executing pipeline call (e.g., Stage 1 coarse screening).
        /// </summary>
        public int? CurrentMaxSolverIterations { get; set; } = null;

        /// <param name="workingDirectory">
        /// Defaults to <c>results/&lt;ProjectName&gt;/</c> in the current working directory.
        /// </param>
        public SimulationContext(SimulationConfig config, ProjectConfig project, string? workingDirectory = null)
        {
            Config = config;
            Project = project;
            WorkingDirectory = workingDirectory ?? DefaultWorkingDirectory(project);

            ResetRuntimeState();
        }

        /// <summary>
        /// <c>results/&lt;ProjectName&gt;/</c>. Without a project name, falls back to the
        /// shared <c>results/</c> root (used by unit tests constructing a bare <see cref="ProjectConfig"/>).
        /// </summary>
        public static string DefaultWorkingDirectory(ProjectConfig project)
        {
            string root = Path.Combine(Directory.GetCurrentDirectory(), OutputConventions.ResultsRootDirectory);
            string name = (project.ProjectName ?? string.Empty).Trim();

            return name.Length == 0 ? root : Path.Combine(root, name);
        }

        /// <summary>
        /// Resets runtime state to the project defaults. Called in the constructor so that
        /// a second run in the same process starts cleanly from configured values.
        /// </summary>
        public void ResetRuntimeState()
        {
            CurrentBaseParameters = new Dictionary<string, float>(Project.BaseParameters);
            CurrentDeviations = new Dictionary<string, float>(Project.MaxDeviations);
            EffectiveMeshRefinementFactor = Config.MeshRefinementFactor > 0f ? Config.MeshRefinementFactor : 1.0f;
            MeshRefinementSource = OutputConventions.ManualMeshSource;
            CalibrationReportPath = null;
            CurrentRunTag = "";
            CellLimit = 0;
            CurrentFidelityLevel = FidelityLevels.High;
            CurrentMaxSolverIterations = null;
        }

        /// <summary>
        /// Creates required output directories. Separated from the constructor for testability.
        /// </summary>
        public void EnsureDirectories()
        {
            Directory.CreateDirectory(WorkingDirectory);
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, OutputConventions.ErrorLogsDirectory));
            Directory.CreateDirectory(Path.Combine(WorkingDirectory, OutputConventions.AnalysisDirectory));
        }

        // Dynamic CSV export (automatically handles all active and passive parameters)
        public void ExportToCsv()
        {
            if (History.Count == 0) return;

            string csvPath = Path.Combine(WorkingDirectory, OutputConventions.ResultsCsvFileName);
            StringBuilder csvBuilder = new StringBuilder();
            
            // Collect columns across ALL records, not just the first one: otherwise a metric
            // would be missing from the entire export if the first record failed.
            var activeKeys = History.SelectMany(r => r.ActiveParameters.Keys).Distinct().ToList();
            // Exclude passive keys that share a name with an active parameter (e.g., AngleOfAttackDeg)
            // so we do not emit duplicate column headers.
            var passiveKeys = History.SelectMany(r => r.PassiveParameters.Keys).Distinct().Where(k => !activeKeys.Contains(k)).ToList();
            var objectiveNames = History.SelectMany(r => r.Objectives.Select(o => o.Name)).Distinct(StringComparer.Ordinal).ToList();
            var constraintNames = History.SelectMany(r => r.Constraints.Select(c => c.Name)).Distinct(StringComparer.Ordinal).ToList();

            // Header
            string header = "Iteration;Variant;" +
                            string.Join(";", activeKeys) + ";" +
                            string.Join(";", passiveKeys) + ";" +
                            "Fitness;StlPath;MeshPath;SimulationFailed;Disqualified;Feasible;FailedStage;FidelityLevel";
            if (objectiveNames.Count > 0)
            {
                header += ";" + string.Join(";", objectiveNames.Select(n => $"Obj_{n}"));
            }
            if (constraintNames.Count > 0)
            {
                header += ";" + string.Join(";", constraintNames.Select(n => $"Con_{n}_Margin"));
            }
            csvBuilder.AppendLine(header);

            // Data rows. Invariant culture ensures "." decimal separators regardless of OS locale.
            CultureInfo inv = CultureInfo.InvariantCulture;
            foreach (var record in History)
            {
                var activeVals = activeKeys.Select(k => record.ActiveParameters.ContainsKey(k) ? record.ActiveParameters[k].ToString("F3", inv) : "0");
                var passiveVals = passiveKeys.Select(k => record.PassiveParameters.ContainsKey(k) ? record.PassiveParameters[k].ToString("F5", inv) : "0");
                string fidelityLevel = string.IsNullOrWhiteSpace(record.FidelityLevel) ? FidelityLevels.High : record.FidelityLevel;

                string line = $"{record.Iteration};{record.Variant};" +
                              $"{string.Join(";", activeVals)};" +
                              $"{string.Join(";", passiveVals)};" +
                              $"{record.Fitness.ToString("F5", inv)};{record.StlPath};{record.MeshPath};" +
                              $"{(record.SimulationFailed ? 1 : 0)};{(record.Disqualified ? 1 : 0)};" +
                              $"{(record.Feasible ? 1 : 0)};{record.FailedStage};{fidelityLevel}";

                if (objectiveNames.Count > 0)
                {
                    var objVals = objectiveNames.Select(n =>
                    {
                        ObjectiveValue? obj = record.Objectives.FirstOrDefault(o => string.Equals(o.Name, n, StringComparison.Ordinal));
                        return obj != null ? obj.Value.ToString("F5", inv) : "";
                    });
                    line += ";" + string.Join(";", objVals);
                }

                if (constraintNames.Count > 0)
                {
                    var conVals = constraintNames.Select(n =>
                    {
                        ConstraintEvaluation? con = record.Constraints.FirstOrDefault(c => string.Equals(c.Name, n, StringComparison.Ordinal));
                        return con != null ? con.Margin.ToString("F5", inv) : "";
                    });
                    line += ";" + string.Join(";", conVals);
                }

                csvBuilder.AppendLine(line);
            }

            File.WriteAllText(csvPath, csvBuilder.ToString(), Encoding.UTF8);
        }
    }
}
