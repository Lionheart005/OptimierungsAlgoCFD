using System;
using System.Collections.Generic;
using System.Linq;

using ParameterBounds = (float Min, float Max);

namespace FluidForge.Core
{
    /// <summary>
    /// The framework's "bouncer": checks whether an iteration's winner is stable.
    ///
    /// It re-runs the entire pipeline with a slightly perturbed parameter set
    /// (scaling -> geometry -> meshing -> solver) and compares the re-simulated
    /// <b>fitness</b> against the candidate's fitness. If the difference exceeds
    /// <see cref="SimulationConfig.BouncerTolerance"/>, the model is treated as an outlier
    /// and disqualified, and the next-best candidate is tested instead.
    ///
    /// Deliberately project-agnostic: the validator knows no metric names such as "Drag",
    /// only the fitness computed by the project's <see cref="IFitnessCalculator"/>.
    /// </summary>
    public class ChampionValidator : IModelValidator
    {
        /// <summary>Fitness below which a candidate is not even tested.</summary>
        private const float MinimumFitnessToTest = 0.1f;
        /// <summary>Fitness assigned to a disqualified candidate.</summary>
        private const float DisqualifiedFitness = 0.0001f;

        private readonly IFitnessCalculator _fitness;
        private readonly Random _random;
        private readonly IReadOnlyDictionary<string, ParameterBounds>? _parameterBounds;

        /// <param name="fitness">Evaluates the re-simulation using the same formula as the optimization run.</param>
        /// <param name="random">Optional fixed random source for reproducible tests.</param>
        /// <param name="parameterBounds">Optional parameter bounds for relative jitter and clamping.</param>
        public ChampionValidator(
            IFitnessCalculator fitness,
            Random? random = null,
            IReadOnlyDictionary<string, ParameterBounds>? parameterBounds = null)
        {
            _fitness = fitness;
            _random = random ?? new Random();
            _parameterBounds = parameterBounds;
        }

        public ModelRecord ValidateChampion(
            List<ModelRecord> candidates,
            SimulationConfig config,
            Func<Dictionary<string, float>, ModelRecord> resimulate)
        {
            if (candidates.Count == 0)
                throw new InvalidOperationException("[VALIDATION] No candidates available to validate.");

            Console.WriteLine();
            SimulationLog.Detail("VALIDATION: Testing champion stability...");
            var sortedCandidates = candidates.OrderByDescending(r => r.Fitness).ToList();

            float tolerance = config.BouncerTolerance;

            foreach (var candidate in sortedCandidates)
            {
                if (candidate.Fitness < MinimumFitnessToTest) break;

                float originalFitness = candidate.Fitness;
                SimulationLog.Detail($"Testing Variant {candidate.Variant} (Reference Fitness: {originalFitness:F4})");

                var testParams = JitterParameters(candidate.ActiveParameters, config.BouncerJitter);

                try
                {
                    // Full pipeline run with the perturbed parameter set
                    ModelRecord validationResult = resimulate(testParams);

                    // ...and evaluate with the exact same formula as in the regular run
                    _fitness.CalculateFitness(validationResult, config);
                    float validatedFitness = validationResult.Fitness;

                    float reference = Math.Max(Math.Abs(originalFitness), float.Epsilon);
                    float deviation = Math.Abs(validatedFitness - originalFitness) / reference;

                    // If the flow naturally oscillates (vortex shedding), allow at least twice the combined
                    // uncertainty of both runs so true winners are not randomly rejected.
                    IReadOnlyCollection<string> metrics = _fitness.RequiredMetrics ?? (IReadOnlyCollection<string>)Array.Empty<string>();
                    MetricUncertainty.TryGetFitness(candidate, metrics, out double uncertaintyOriginal);
                    MetricUncertainty.TryGetFitness(validationResult, metrics, out double uncertaintyValidation);
                    float allowed = MetricTolerance.WithUncertainty(tolerance, uncertaintyOriginal, uncertaintyValidation);
                    string widened = allowed > tolerance + 1e-6f
                        ? $", allowed {allowed * 100:F1}% due to flow oscillation"
                        : string.Empty;

                    SimulationLog.Detail($"Validation Fitness: {validatedFitness:F4} (Deviation: {deviation * 100:F1}%{widened})");

                    if (deviation <= allowed)
                    {
                        SimulationLog.Detail("[OK] Stable. Champion confirmed.");
                        return candidate;
                    }

                    SimulationLog.Detail("[REJECTED] Model is unstable. Disqualified.");
                    candidate.Fitness = DisqualifiedFitness;
                    candidate.Disqualified = true;
                }
                catch (Exception)
                {
                    SimulationLog.Detail($"{OutputConventions.ErrorTag} Validation failed. Disqualified.");
                    candidate.Fitness = DisqualifiedFitness;
                    candidate.Disqualified = true;
                }
            }

            SimulationLog.Detail($"{OutputConventions.WarningTag} No model proved stable. Selecting best remaining candidate.");
            return sortedCandidates[0];
        }

        /// <summary>
        /// Perturbs one randomly chosen parameter by +/- <paramref name="jitter"/> (relative to ParameterBounds if present, otherwise absolute).
        /// </summary>
        private Dictionary<string, float> JitterParameters(Dictionary<string, float> parameters, float jitter)
        {
            var testParams = new Dictionary<string, float>(parameters);
            if (testParams.Count == 0) return testParams;

            string keyToJitter = testParams.Keys.ElementAt(_random.Next(testParams.Count));
            float sign = _random.NextDouble() > 0.5 ? 1.0f : -1.0f;
            if (_parameterBounds != null && _parameterBounds.TryGetValue(keyToJitter, out var bounds) && bounds.Max > bounds.Min)
            {
                float range = bounds.Max - bounds.Min;
                float delta = sign * jitter * range;
                testParams[keyToJitter] = Math.Clamp(testParams[keyToJitter] + delta, bounds.Min, bounds.Max);
            }
            else
            {
                testParams[keyToJitter] += sign * jitter;
            }
            return testParams;
        }
    }
}
