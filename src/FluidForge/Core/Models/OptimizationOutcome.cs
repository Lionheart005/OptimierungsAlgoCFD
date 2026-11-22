using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace FluidForge.Core
{
    /// <summary>Optimization sense of an objective metric.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum OptimizationDirection
    {
        Minimize,
        Maximize
    }

    /// <summary>Pipeline stage at which a variant evaluation failed or became infeasible.</summary>
    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum EvaluationFailureStage
    {
        None,
        Geometry,
        PreConstraint,
        Mesh,
        Solver,
        PostConstraint
    }

    /// <summary>
    /// Raw physical value of a single optimization objective (unpenalized).
    /// </summary>
    public sealed record ObjectiveValue(
        string Name,
        float Value,
        OptimizationDirection Direction = OptimizationDirection.Maximize,
        float Weight = 1.0f);

    /// <summary>
    /// Evaluation of a single constraint with signed margin.
    /// Convention: <see cref="Margin"/> &gt;= 0 means feasible; &lt; 0 means violated.
    /// </summary>
    public sealed record ConstraintEvaluation(
        string Name,
        float ActualValue,
        float Limit,
        bool IsSatisfied,
        float Margin,
        bool IsPreSolve = false)
    {
        /// <summary>
        /// Signed violation (&lt;= 0 feasible, &gt; 0 violated), directly compatible with Constrained EI.
        /// </summary>
        [JsonIgnore]
        public float Violation => -Margin;

        /// <summary>Constraint <paramref name="actual"/> &lt;= <paramref name="maxLimit"/>.</summary>
        public static ConstraintEvaluation AtMost(string name, float actual, float maxLimit, bool isPreSolve = false)
        {
            float margin = maxLimit - actual;
            bool satisfied = float.IsFinite(actual) && actual <= maxLimit;
            return new ConstraintEvaluation(name, actual, maxLimit, satisfied, margin, isPreSolve);
        }

        /// <summary>Constraint <paramref name="actual"/> &gt;= <paramref name="minLimit"/>.</summary>
        public static ConstraintEvaluation AtLeast(string name, float actual, float minLimit, bool isPreSolve = false)
        {
            float margin = actual - minLimit;
            bool satisfied = float.IsFinite(actual) && actual >= minLimit;
            return new ConstraintEvaluation(name, actual, minLimit, satisfied, margin, isPreSolve);
        }

        /// <summary>
        /// Range constraint <paramref name="minLimit"/> &lt;= <paramref name="actual"/> &lt;= <paramref name="maxLimit"/>,
        /// decomposed into two smooth single-sided constraints (<c>{name}_Min</c> and <c>{name}_Max</c>).
        /// </summary>
        public static IReadOnlyList<ConstraintEvaluation> InRange(
            string name, float actual, float minLimit, float maxLimit, bool isPreSolve = false)
            => new[]
            {
                AtLeast($"{name}_Min", actual, minLimit, isPreSolve),
                AtMost($"{name}_Max", actual, maxLimit, isPreSolve)
            };
    }

    /// <summary>
    /// Structured evaluation result separating raw objectives from constraints.
    /// </summary>
    public sealed class EvaluationOutcome
    {
        public List<ObjectiveValue> Objectives { get; set; } = new();
        public List<ConstraintEvaluation> Constraints { get; set; } = new();
        public float? ScalarFitnessOverride { get; set; }
        public bool IsFeasible => Constraints.All(c => c.IsSatisfied);
    }
}
