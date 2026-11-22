using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// Simple Inverse-Distance-Weighting (IDW) interpolation baseline.
    /// <para>
    /// Note: this is intentionally <b>not</b> a polynomial Response Surface Model (RSM),
    /// but a simple distance-weighted interpolation over previous evaluations. It cannot
    /// extrapolate beyond observed fitness values and provides no uncertainty quantification.
    /// It serves as a simple, robust comparison baseline for <c>Bayes</c>.
    /// </para>
    /// </summary>
    public class IdwBaselineAlgorithm : IOptimizationAlgorithm
    {
        private readonly Random _random;
        private readonly IFitnessCalculator _fitnessCalculator;
        private readonly Queue<Dictionary<string, float>> _nextVirtualCandidates = new Queue<Dictionary<string, float>>();

        /// <param name="fitnessCalculator">Evaluates the models of an iteration.</param>
        /// <param name="random">
        /// Optional fixed random source. Unset at runtime, or <c>new Random(seed)</c> in tests
        /// for reproducible initial sampling.
        /// </param>
        public IdwBaselineAlgorithm(IFitnessCalculator fitnessCalculator, Random? random = null)
        {
            _fitnessCalculator = fitnessCalculator;
            _random = random ?? new Random();
        }

        public string Name => "IDW Baseline (Inverse Distance Weighting)";

        public Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants)
        {
            if (_nextVirtualCandidates.Count > 0)
            {
                return _nextVirtualCandidates.Dequeue();
            }

            // Operating point = runtime state in context, not the project template.
            if (iteration == 1 && variant == 1)
            {
                return new Dictionary<string, float>(context.CurrentBaseParameters);
            }

            var candidate = new Dictionary<string, float>();
            foreach (var kvp in context.CurrentBaseParameters)
            {
                var (min, max) = ResolveBounds(context.Project.ParameterBounds, kvp.Key, kvp.Value);
                float randomValue = min + (float)_random.NextDouble() * (max - min);
                candidate.Add(kvp.Key, randomValue);
            }

            return candidate;
        }

        public ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context)
        {
            foreach (var record in records)
            {
                _fitnessCalculator.CalculateFitness(record, context.Config);
            }

            return records.OrderByDescending(r => r.Fitness).First();
        }

        public void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord)
        {
            _nextVirtualCandidates.Clear();

            int k = context.CurrentBaseParameters.Count;
            int minRequiredSamples = ((k + 1) * (k + 2)) / 2 + 2;

            // Only successful simulations serve as support points. Failed models carry no
            // valid fitness and would distort IDW interpolation.
            var usableSamples = context.History.Where(r => !r.SimulationFailed).ToList();

            if (usableSamples.Count < minRequiredSamples)
            {
                int missing = minRequiredSamples - usableSamples.Count;
                int failed = context.History.Count - usableSamples.Count;
                string failedNote = failed > 0 ? $" ({failed} failed simulation(s) excluded)" : "";
                Console.WriteLine($"\n[INFO] {Name}: Building support points. Need {missing} more baseline simulation(s).{failedNote}");
                return;
            }

            int virtualSimulations = context.Config.IdwVirtualSimulations;
            int variantsToQueue = Math.Max(0, context.Config.VariantsPerIteration);
            if (virtualSimulations <= 0 || variantsToQueue == 0)
            {
                return;
            }

            Console.WriteLine($"\n[INFO] {Name}: IDW interpolation active!");
            Console.WriteLine($"       -> Evaluating {virtualSimulations:N0} virtual candidates via IDW...");

            var virtualResults = new List<(Dictionary<string, float> Params, float PredictedFitness)>(virtualSimulations);

            for (int i = 0; i < virtualSimulations; i++)
            {
                bool exploit = _random.NextDouble() < context.Config.IdwExploitationRatio;
                Dictionary<string, float> virtParams = GenerateVirtualCandidate(context, bestRecord, exploit);

                float predictedFitness = PredictFitnessSurrogate(virtParams, usableSamples, context.Project.ParameterBounds, context.Config.IdwPower);

                virtualResults.Add((virtParams, predictedFitness));
            }

            var topCandidates = virtualResults
                .OrderByDescending(v => v.PredictedFitness)
                .Take(variantsToQueue)
                .ToList();

            if (topCandidates.Count == 0)
            {
                return;
            }

            Console.WriteLine($"       -> Virtual evaluation complete. Expected top fitness: {topCandidates[0].PredictedFitness:F3}");
            Console.WriteLine($"       -> Queuing top {topCandidates.Count} candidate(s) for simulation.");

            foreach (var candidate in topCandidates)
            {
                _nextVirtualCandidates.Enqueue(candidate.Params);
            }
        }

        /// <summary>
        /// IDW fitness estimate. <paramref name="samples"/> contains only successful simulations —
        /// aborted runs are not valid support points.
        ///
        /// Public and static so interpolation can be unit-tested with known support points
        /// without running a full optimization loop.
        /// </summary>
        public static float PredictFitnessSurrogate(Dictionary<string, float> candidate, List<ModelRecord> samples, Dictionary<string, (float Min, float Max)> bounds, float idwPower)
        {
            float numerator = 0f;
            float denominator = 0f;

            foreach (var record in samples)
            {
                float distSq = 0f;

                foreach (var key in candidate.Keys)
                {
                    if (record.ActiveParameters.TryGetValue(key, out float sampleValue))
                    {
                        float range = bounds.TryGetValue(key, out var paramBounds)
                            ? paramBounds.Max - paramBounds.Min
                            : 1f;
                        if (range <= 0f) range = 1f;

                        float diff = (candidate[key] - sampleValue) / range;
                        distSq += diff * diff;
                    }
                }

                if (distSq < 1e-8f) return record.Fitness;

                float weight = 1.0f / (float)Math.Pow(distSq, idwPower / 2.0);

                numerator += weight * record.Fitness;
                denominator += weight;
            }

            if (denominator <= 0f) return 0f;

            return numerator / denominator;
        }

        private Dictionary<string, float> GenerateVirtualCandidate(SimulationContext context, ModelRecord bestKnown, bool exploit)
        {
            var candidate = new Dictionary<string, float>();

            foreach (var kvp in context.CurrentBaseParameters)
            {
                string key = kvp.Key;
                var (min, max) = ResolveBounds(context.Project.ParameterBounds, key, kvp.Value);
                float range = max - min;

                float value;
                if (exploit && bestKnown.ActiveParameters.TryGetValue(key, out float bestValue))
                {
                    float sigma = range * context.Config.IdwExploitationSigma;
                    value = bestValue + (float)(NextGaussian() * sigma);
                }
                else
                {
                    value = min + (float)_random.NextDouble() * range;
                }

                candidate.Add(key, Math.Clamp(value, min, max));
            }

            return candidate;
        }

        private static (float Min, float Max) ResolveBounds(
            IReadOnlyDictionary<string, (float Min, float Max)> parameterBounds,
            string key,
            float fallbackValue)
        {
            if (parameterBounds.TryGetValue(key, out var bounds))
            {
                return bounds.Min <= bounds.Max ? bounds : (bounds.Max, bounds.Min);
            }

            float a = fallbackValue * 0.5f;
            float b = fallbackValue * 1.5f;
            return a <= b ? (a, b) : (b, a);
        }

        private double NextGaussian()
        {
            double u1 = 1.0 - _random.NextDouble();
            double u2 = 1.0 - _random.NextDouble();
            if (u1 <= 0) u1 = 0.000001;
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }
    }
}
