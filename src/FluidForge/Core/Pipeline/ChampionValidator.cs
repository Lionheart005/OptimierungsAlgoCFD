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
            var sortedCandidates = candidates
                .OrderByDescending(r => r.Feasible && !r.SimulationFailed)
                .ThenByDescending(r => r.Fitness)
                .ToList();

            float tolerance = config.BouncerTolerance;

            foreach (var candidate in sortedCandidates)
            {
                if (candidate.SimulationFailed || !candidate.Feasible || candidate.IsPreConstraintInfeasible)
                    continue;

                if (_fitness is IObjectiveEvaluator objectiveEvaluator && candidate.Objectives.Count > 0)
                {
                    if (ValidateCandidateByObjectives(candidate, objectiveEvaluator, config, tolerance, resimulate))
                        return candidate;
                    continue;
                }

                if (candidate.Fitness < MinimumFitnessToTest) break;

                float originalFitness = candidate.Fitness;
                SimulationLog.Detail($"Testing Variant {candidate.Variant} (Reference Fitness: {originalFitness:F4})");

                var (testParams, _, _) = JitterParametersWithInfo(candidate.ActiveParameters, config.BouncerJitter);

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

                    Disqualify(candidate, "[REJECTED] Model is unstable. Disqualified.");
                }
                catch (Exception)
                {
                    Disqualify(candidate, $"{OutputConventions.ErrorTag} Validation failed. Disqualified.");
                }
            }

            SimulationLog.Detail($"{OutputConventions.WarningTag} No model proved stable. Selecting best remaining candidate.");
            return sortedCandidates[0];
        }

        private bool ValidateCandidateByObjectives(
            ModelRecord candidate,
            IObjectiveEvaluator objectiveEvaluator,
            SimulationConfig config,
            float tolerance,
            Func<Dictionary<string, float>, ModelRecord> resimulate)
        {
            string refSummary = string.Join(", ", candidate.Objectives.Select(o => $"{o.Name}={o.Value:F4}"));
            SimulationLog.Detail($"Testing Variant {candidate.Variant} (Reference Objectives: {refSummary})");

            var (testParams, jitteredKey, delta) = JitterParametersWithInfo(candidate.ActiveParameters, config.BouncerJitter);

            try
            {
                ModelRecord validationResult = resimulate(testParams);

                // If jitter stepped just past a pre-CFD geometry boundary, retry once in the opposite
                // direction so CFD stability is evaluated within the feasible domain.
                if (validationResult.IsPreConstraintInfeasible && jitteredKey != null && delta != 0f)
                {
                    var retryParams = new Dictionary<string, float>(candidate.ActiveParameters);
                    float opposite = retryParams[jitteredKey] - delta;
                    if (_parameterBounds != null && _parameterBounds.TryGetValue(jitteredKey, out var bounds) && bounds.Max > bounds.Min)
                    {
                        opposite = Math.Clamp(opposite, bounds.Min, bounds.Max);
                    }
                    retryParams[jitteredKey] = opposite;
                    SimulationLog.Detail($"{OutputConventions.WarningTag} Validation jitter hit pre-CFD constraint; retrying opposite direction on '{jitteredKey}'.");
                    validationResult = resimulate(retryParams);
                }

                _fitness.CalculateFitness(validationResult, config);

                if (validationResult.SimulationFailed || validationResult.IsPreConstraintInfeasible || validationResult.Objectives.Count == 0)
                {
                    Disqualify(candidate, "[REJECTED] Validation failed or violated pre-CFD constraints. Disqualified.");
                    return false;
                }

                float maxDeviation = 0f;
                var summaryParts = new List<string>(candidate.Objectives.Count);

                foreach (ObjectiveValue origObj in candidate.Objectives)
                {
                    ObjectiveValue? newObj = validationResult.Objectives.FirstOrDefault(
                        o => string.Equals(o.Name, origObj.Name, StringComparison.OrdinalIgnoreCase));
                    if (newObj == null || !float.IsFinite(origObj.Value) || !float.IsFinite(newObj.Value))
                    {
                        Disqualify(candidate, "[REJECTED] Validation objectives incomplete or non-finite. Disqualified.");
                        return false;
                    }

                    float dev = MathF.Abs(newObj.Value - origObj.Value) / MathF.Max(MathF.Abs(origObj.Value), 1e-6f);
                    if (dev > maxDeviation) maxDeviation = dev;
                    summaryParts.Add($"{origObj.Name}: {origObj.Value:F4} -> {newObj.Value:F4} ({dev * 100:F1}%)");
                }

                IReadOnlyCollection<string> metrics = objectiveEvaluator.RawComparisonMetrics.Count > 0
                    ? objectiveEvaluator.RawComparisonMetrics
                    : (_fitness.RequiredMetrics ?? (IReadOnlyCollection<string>)Array.Empty<string>());
                MetricUncertainty.TryGetFitness(candidate, metrics, out double uncertaintyOriginal);
                MetricUncertainty.TryGetFitness(validationResult, metrics, out double uncertaintyValidation);
                float allowed = MetricTolerance.WithUncertainty(tolerance, uncertaintyOriginal, uncertaintyValidation);
                string widened = allowed > tolerance + 1e-6f
                    ? $", allowed {allowed * 100:F1}% due to flow oscillation"
                    : string.Empty;

                SimulationLog.Detail($"Validation Objectives: {string.Join(", ", summaryParts)} (Max Deviation: {maxDeviation * 100:F1}%{widened})");

                if (maxDeviation <= allowed)
                {
                    SimulationLog.Detail("[OK] Stable. Champion confirmed.");
                    return true;
                }

                Disqualify(candidate, "[REJECTED] Model is unstable. Disqualified.");
                return false;
            }
            catch (Exception)
            {
                Disqualify(candidate, $"{OutputConventions.ErrorTag} Validation failed. Disqualified.");
                return false;
            }
        }

        private static void Disqualify(ModelRecord candidate, string message)
        {
            SimulationLog.Detail(message);
            candidate.Fitness = DisqualifiedFitness;
            candidate.Disqualified = true;
        }

        /// <summary>
        /// Perturbs one randomly chosen parameter by +/- <paramref name="jitter"/> (relative to ParameterBounds if present, otherwise absolute).
        /// </summary>
        private (Dictionary<string, float> Params, string? JitteredKey, float Delta) JitterParametersWithInfo(
            Dictionary<string, float> parameters, float jitter)
        {
            var testParams = new Dictionary<string, float>(parameters);
            if (testParams.Count == 0) return (testParams, null, 0f);

            string keyToJitter = testParams.Keys.ElementAt(_random.Next(testParams.Count));
            float sign = _random.NextDouble() > 0.5 ? 1.0f : -1.0f;
            float delta;
            if (_parameterBounds != null && _parameterBounds.TryGetValue(keyToJitter, out var bounds) && bounds.Max > bounds.Min)
            {
                float range = bounds.Max - bounds.Min;
                delta = sign * jitter * range;
                testParams[keyToJitter] = Math.Clamp(testParams[keyToJitter] + delta, bounds.Min, bounds.Max);
            }
            else
            {
                delta = sign * jitter;
                testParams[keyToJitter] += delta;
            }
            return (testParams, keyToJitter, delta);
        }
    }
}
