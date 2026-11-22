using System;
using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Central rule defining which evaluations a surrogate model may train on.
    ///
    /// <para>
    /// A record is usable if it did not fail, was not disqualified by the bouncer, has finite fitness,
    /// is not a control run (variant 99), and was evaluated at the same mesh refinement level as the
    /// current run (<c>MeshRefinementFactor</c>, tolerance 1e-6; records without this key are accepted
    /// for backward compatibility). Failed and disqualified points are returned separately for repulsion.
    /// </para>
    /// </summary>
    public sealed class SurrogateTrainingSet
    {
        /// <summary>Variant number reserved for control runs (bouncer, fine verification).</summary>
        public const int ControlRunVariant = 99;

        /// <summary>Usable CFD-evaluated records in history order (includes feasible and PostConstraint records).</summary>
        public IReadOnlyList<ModelRecord> Records { get; }

        /// <summary>Normalized inputs corresponding to <see cref="Records"/>.</summary>
        public IReadOnlyList<double[]> Inputs { get; }

        /// <summary>Subset of <see cref="Records"/> that satisfy all constraints (<c>Feasible == true</c>).</summary>
        public IReadOnlyList<ModelRecord> FeasibleRecords { get; }

        /// <summary>Normalized inputs corresponding to <see cref="FeasibleRecords"/>.</summary>
        public IReadOnlyList<double[]> FeasibleInputs { get; }

        /// <summary>
        /// All non-crashed, non-control-run, same-mesh-level records that carry constraint evaluations,
        /// including <c>PreConstraint</c> records (so constraint GPs learn pre-CFD boundaries).
        /// </summary>
        public IReadOnlyList<ModelRecord> AllConstraintRecords { get; }

        /// <summary>Normalized inputs corresponding to <see cref="AllConstraintRecords"/>.</summary>
        public IReadOnlyList<double[]> AllConstraintInputs { get; }

        /// <summary>
        /// Normalized locations of <c>PreConstraint</c>-rejected records: used for exact-duplicate
        /// prevention in <c>known</c>, without hard crash repulsion.
        /// </summary>
        public IReadOnlyList<double[]> PreConstraintPoints { get; }

        /// <summary>Normalized locations of failed or disqualified evaluations.</summary>
        public IReadOnlyList<double[]> ExcludedPoints { get; }

        /// <summary>
        /// Only genuinely failed evaluations (meshing or solver aborted), excluding bouncer-rejected
        /// candidates. A rejected winner often sits right at a steep optimum; treating it as a crash
        /// would steer the success model away from the optimum.
        /// </summary>
        public IReadOnlyList<double[]> FailedPoints { get; }

        private SurrogateTrainingSet(
            List<ModelRecord> records,
            List<double[]> inputs,
            List<ModelRecord> feasibleRecords,
            List<double[]> feasibleInputs,
            List<ModelRecord> allConstraintRecords,
            List<double[]> allConstraintInputs,
            List<double[]> preConstraintPoints,
            List<double[]> excluded,
            List<double[]> failed)
        {
            Records = records;
            Inputs = inputs;
            FeasibleRecords = feasibleRecords;
            FeasibleInputs = feasibleInputs;
            AllConstraintRecords = allConstraintRecords;
            AllConstraintInputs = allConstraintInputs;
            PreConstraintPoints = preConstraintPoints;
            ExcludedPoints = excluded;
            FailedPoints = failed;
        }

        public static SurrogateTrainingSet Build(SimulationContext context, ParameterSpace space)
        {
            var records = new List<ModelRecord>();
            var inputs = new List<double[]>();
            var feasibleRecords = new List<ModelRecord>();
            var feasibleInputs = new List<double[]>();
            var allConstraintRecords = new List<ModelRecord>();
            var allConstraintInputs = new List<double[]>();
            var preConstraintPoints = new List<double[]>();
            var excluded = new List<double[]>();
            var failed = new List<double[]>();

            foreach (ModelRecord record in context.History)
            {
                if (record.Variant == ControlRunVariant) continue;
                if (!SameMeshLevel(record, context.EffectiveMeshRefinementFactor)) continue;

                // Fallback mesh (uniformly thinned boundary layer) uses a different wall mesh:
                // exclude from training, but do not treat as a solver crash.
                bool meshFallback = record.PassiveParameters.TryGetValue(ModelRecord.MeshFallbackKey, out float fb) && fb > 0.5f;
                double[]? cachedPoint = null;
                double[] GetPoint() => cachedPoint ??= space.ToUnit(record.ActiveParameters);

                if (!record.SimulationFailed && !meshFallback && record.Constraints.Count > 0)
                {
                    allConstraintRecords.Add(record);
                    allConstraintInputs.Add(GetPoint());
                }

                if (record.SimulationFailed || record.Disqualified || meshFallback)
                {
                    double[] point = GetPoint();
                    excluded.Add(point);

                    // A meshing error is not a flow solver crash: apply local repulsion only,
                    // without teaching the success model to avoid the entire shape region.
                    bool meshFailed = record.PassiveParameters.TryGetValue(ModelRecord.MeshFailedKey, out float mf) && mf > 0.5f;
                    if (record.SimulationFailed && !meshFailed) failed.Add(point);
                    continue;
                }

                if (record.IsPreConstraintInfeasible)
                {
                    preConstraintPoints.Add(GetPoint());
                    continue;
                }

                if (!float.IsFinite(record.Fitness)) continue;

                double[] usablePoint = GetPoint();
                records.Add(record);
                inputs.Add(usablePoint);

                if (record.Feasible)
                {
                    feasibleRecords.Add(record);
                    feasibleInputs.Add(usablePoint);
                }
            }

            return new SurrogateTrainingSet(
                records,
                inputs,
                feasibleRecords,
                feasibleInputs,
                allConstraintRecords,
                allConstraintInputs,
                preConstraintPoints,
                excluded,
                failed);
        }

        private static bool SameMeshLevel(ModelRecord record, float effectiveFactor)
        {
            if (!record.PassiveParameters.TryGetValue("MeshRefinementFactor", out float factor)) return true;
            return Math.Abs(factor - effectiveFactor) <= 1e-6f;
        }
    }
}
