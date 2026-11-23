using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace FluidForge.Core
{
    /// <summary>
    /// Canonical fidelity level labels and numeric conversion helpers for multi-fidelity workflows.
    /// </summary>
    public static class FidelityLevels
    {
        public const string Coarse = "Coarse";
        public const string High = "High";
        public const string Verification = "Verification";

        /// <summary>
        /// Converts a fidelity level label to a normalized numeric fidelity in <c>(0, 1]</c>.
        /// </summary>
        public static double ToNumeric(string? level, float coarseMeshFactor = 2.0f)
        {
            if (IsCoarse(level))
            {
                double factor = coarseMeshFactor > 1.0f && float.IsFinite(coarseMeshFactor) ? coarseMeshFactor : 2.0;
                return Math.Clamp(1.0 / factor, 0.1, 0.9);
            }

            return 1.0;
        }

        /// <summary>
        /// Returns <c>true</c> for coarse/low-fidelity labels (<c>Coarse</c>, <c>FastConcept</c>, <c>Low</c>).
        /// </summary>
        public static bool IsCoarse(string? level)
        {
            if (string.IsNullOrWhiteSpace(level)) return false;
            string trimmed = level.Trim();
            return trimmed.Equals(Coarse, StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("FastConcept", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Low", StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>
    /// Data record for a single evaluated model (variant).
    /// </summary>
    public class ModelRecord
    {
        /// <summary>
        /// Key in <see cref="PassiveParameters"/>: 1 if meshing failed (<see cref="MeshGenerationException"/>).
        /// A meshing error is not a solver crash; the Bayesian success model does not learn from it.
        /// </summary>
        public const string MeshFailedKey = "MeshFailed";

        /// <summary>Key in <see cref="PassiveParameters"/>: 1 if the mesh exceeded the cell limit and SU2 was skipped.</summary>
        public const string CellLimitExceededKey = "CellLimitExceeded";

        /// <summary>Key in <see cref="PassiveParameters"/>: 1 if a fallback mesh was used (uniformly thinned boundary layer).</summary>
        public const string MeshFallbackKey = "MeshFallback";

        /// <summary>
        /// Mesh metadata keys in <see cref="PassiveParameters"/> (boundary layer metrics, fallback,
        /// meshing failures, cell limit): not geometry/flow metrics and therefore excluded from
        /// metric comparisons and log summaries.
        /// </summary>
        public static bool IsMeshMetadataKey(string key)
            => key.StartsWith("BoundaryLayer", System.StringComparison.OrdinalIgnoreCase)
               || key.StartsWith("Tunnel", System.StringComparison.OrdinalIgnoreCase)
               || key.StartsWith("MeshCells_", System.StringComparison.OrdinalIgnoreCase)
               || key.Equals(MeshFailedKey, System.StringComparison.OrdinalIgnoreCase)
               || key.Equals(MeshFallbackKey, System.StringComparison.OrdinalIgnoreCase)
               || key.Equals(CellLimitExceededKey, System.StringComparison.OrdinalIgnoreCase);

        public int Iteration { get; set; }
        public int Variant { get; set; }
        
        // Active parameters (controlled/mutated by the optimization algorithm)
        public Dictionary<string, float> ActiveParameters { get; set; } = new Dictionary<string, float>();
        
        // Passive parameters / metrics (simulation outputs, e.g., Volume, Drag)
        public Dictionary<string, float> PassiveParameters { get; set; } = new Dictionary<string, float>();

        /// <summary>
        /// Generic failure flag: meshing or solver chain aborted, so passive parameters are
        /// incomplete. The core knows no metric names — <see cref="IFitnessCalculator"/> decides what this means.
        /// </summary>
        public bool SimulationFailed { get; set; }

        /// <summary>
        /// Rejected as unstable by the bouncer (<see cref="ChampionValidator"/>).
        /// Fitness remains at the penalty value for IDW compatibility; Bayesian optimization
        /// excludes disqualified records from surrogate training.
        /// </summary>
        public bool Disqualified { get; set; }

        /// <summary>
        /// True when all evaluated pre-solve and post-solve constraints are satisfied.
        /// </summary>
        public bool Feasible { get; set; } = true;

        /// <summary>
        /// Pipeline stage where evaluation failed or violated a constraint.
        /// </summary>
        [JsonConverter(typeof(JsonStringEnumConverter))]
        public EvaluationFailureStage FailedStage { get; set; } = EvaluationFailureStage.None;

        /// <summary>
        /// Fidelity level at which this variant was evaluated (<c>Coarse</c>, <c>High</c>, or <c>Verification</c>).
        /// </summary>
        public string FidelityLevel { get; set; } = FidelityLevels.High;

        /// <summary>
        /// Raw physical objectives produced by an <see cref="IObjectiveEvaluator"/>.
        /// </summary>
        public List<ObjectiveValue> Objectives { get; set; } = new();

        /// <summary>
        /// Evaluated pre-solve (geometry) and post-solve (physics) constraints with margins.
        /// </summary>
        public List<ConstraintEvaluation> Constraints { get; set; } = new();

        /// <summary>
        /// True when the variant was rejected by pre-CFD geometry constraints before meshing/solver.
        /// </summary>
        [JsonIgnore]
        public bool IsPreConstraintInfeasible => FailedStage == EvaluationFailureStage.PreConstraint;

        public float Fitness { get; set; }
        public string StlPath { get; set; } = "";
        public string MeshPath { get; set; } = "";

        /// <summary>
        /// Generic alias for <see cref="StlPath"/> (e.g., for 2D contours or OpenFOAM geometry files).
        /// </summary>
        [JsonIgnore]
        public string GeometryPath { get => StlPath; set => StlPath = value; }

        /// <summary>
        /// File paths for named stage handoffs (e.g., OpenFOAM cases or FEM results).
        /// </summary>
        public Dictionary<string, string> Artifacts { get; set; } = new Dictionary<string, string>();
    }
}
