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

        /// <summary>Usable records in history order.</summary>
        public IReadOnlyList<ModelRecord> Records { get; }

        /// <summary>Normalized inputs corresponding to <see cref="Records"/>.</summary>
        public IReadOnlyList<double[]> Inputs { get; }

        /// <summary>Normalized locations of failed or disqualified evaluations.</summary>
        public IReadOnlyList<double[]> ExcludedPoints { get; }

        /// <summary>
        /// Only genuinely failed evaluations (meshing or solver aborted), excluding bouncer-rejected
        /// candidates. A rejected winner often sits right at a steep optimum; treating it as a crash
        /// would steer the success model away from the optimum.
        /// </summary>
        public IReadOnlyList<double[]> FailedPoints { get; }

        private SurrogateTrainingSet(List<ModelRecord> records, List<double[]> inputs, List<double[]> excluded, List<double[]> failed)
        {
            Records = records;
            Inputs = inputs;
            ExcludedPoints = excluded;
            FailedPoints = failed;
        }

        public static SurrogateTrainingSet Build(SimulationContext context, ParameterSpace space)
        {
            var records = new List<ModelRecord>();
            var inputs = new List<double[]>();
            var excluded = new List<double[]>();
            var failed = new List<double[]>();

            foreach (ModelRecord record in context.History)
            {
                if (record.Variant == ControlRunVariant) continue;
                if (!SameMeshLevel(record, context.EffectiveMeshRefinementFactor)) continue;

                // Fallback mesh (uniformly thinned boundary layer) uses a different wall mesh:
                // exclude from training, but do not treat as a solver crash.
                bool meshFallback = record.PassiveParameters.TryGetValue(ModelRecord.MeshFallbackKey, out float fb) && fb > 0.5f;

                if (record.SimulationFailed || record.Disqualified || meshFallback)
                {
                    double[] point = space.ToUnit(record.ActiveParameters);
                    excluded.Add(point);

                    // A meshing error is not a flow solver crash: apply local repulsion only,
                    // without teaching the success model to avoid the entire shape region.
                    bool meshFailed = record.PassiveParameters.TryGetValue(ModelRecord.MeshFailedKey, out float mf) && mf > 0.5f;
                    if (record.SimulationFailed && !meshFailed) failed.Add(point);
                    continue;
                }

                if (!float.IsFinite(record.Fitness)) continue;

                records.Add(record);
                inputs.Add(space.ToUnit(record.ActiveParameters));
            }

            return new SurrogateTrainingSet(records, inputs, excluded, failed);
        }

        private static bool SameMeshLevel(ModelRecord record, float effectiveFactor)
        {
            if (!record.PassiveParameters.TryGetValue("MeshRefinementFactor", out float factor)) return true;
            return Math.Abs(factor - effectiveFactor) <= 1e-6f;
        }
    }
}
