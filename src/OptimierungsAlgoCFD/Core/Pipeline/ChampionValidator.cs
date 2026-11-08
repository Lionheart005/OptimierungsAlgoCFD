using System;
using System.Collections.Generic;
using System.Linq;

using ParameterBounds = (float Min, float Max);

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Der "Türsteher" des Frameworks: prüft, ob der Gewinner einer Iteration stabil ist.
    ///
    /// Er lässt mit leicht verstelltem Parametersatz die komplette Schleife nochmal laufen
    /// (Skalierung → Geometrie → Vernetzung → Solver) und vergleicht die <b>Fitness</b> des
    /// Re-Simulats mit der des Kandidaten. Weicht sie stärker ab als
    /// <see cref="SimulationConfig.BouncerTolerance"/>, gilt das Modell als Ausreißer und
    /// wird disqualifiziert; geprüft wird dann der nächstbeste Kandidat.
    ///
    /// Bewusst projektunabhängig: der Validator kennt keine Metriknamen wie "Drag",
    /// sondern nur die Fitness, die der <see cref="IFitnessCalculator"/> des Projekts liefert.
    /// </summary>
    public class ChampionValidator : IModelValidator
    {
        /// <summary>Fitness, unterhalb derer ein Kandidat gar nicht erst geprüft wird.</summary>
        private const float MinimumFitnessToTest = 0.1f;
        /// <summary>Fitness, die ein disqualifizierter Kandidat bekommt.</summary>
        private const float DisqualifiedFitness = 0.0001f;

        private readonly IFitnessCalculator _fitness;
        private readonly Random _random;
        private readonly IReadOnlyDictionary<string, ParameterBounds>? _parameterBounds;

        /// <param name="fitness">Bewertet das Re-Simulat — dieselbe Formel wie im Optimierungslauf.</param>
        /// <param name="random">Optional: feste Zufallsquelle für reproduzierbare Tests.</param>
        /// <param name="parameterBounds">Optionale Parametergrenzen für relativen Jitter und Clamping.</param>
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
                throw new InvalidOperationException("[VALIDIERUNG] Keine Kandidaten zum Prüfen vorhanden.");

            Console.WriteLine();
            SimulationLog.Detail("VALIDIERUNG: Teste Stabilität des Champions...");
            var sortedCandidates = candidates.OrderByDescending(r => r.Fitness).ToList();

            float tolerance = config.BouncerTolerance;

            foreach (var candidate in sortedCandidates)
            {
                if (candidate.Fitness < MinimumFitnessToTest) break;

                float originalFitness = candidate.Fitness;
                SimulationLog.Detail($"Prüfe Variante {candidate.Variant} (Referenz-Fitness: {originalFitness:F4})");

                var testParams = JitterParameters(candidate.ActiveParameters, config.BouncerJitter);

                try
                {
                    // Komplette Schleife mit dem verstellten Parametersatz
                    ModelRecord validationResult = resimulate(testParams);

                    // ...und mit derselben Formel bewerten wie im regulären Lauf
                    _fitness.CalculateFitness(validationResult, config);
                    float validatedFitness = validationResult.Fitness;

                    float reference = Math.Max(Math.Abs(originalFitness), float.Epsilon);
                    float deviation = Math.Abs(validatedFitness - originalFitness) / reference;

                    // Schwankt das Ergebnis von Natur aus (Wirbelablösung), darf die Abweichung mindestens so groß
                    // sein wie die doppelte Schwankung beider Rechnungen; sonst würden echte Gewinner zufällig verworfen.
                    IReadOnlyCollection<string> metrics = _fitness.RequiredMetrics ?? (IReadOnlyCollection<string>)Array.Empty<string>();
                    MetricUncertainty.TryGetFitness(candidate, metrics, out double uncertaintyOriginal);
                    MetricUncertainty.TryGetFitness(validationResult, metrics, out double uncertaintyValidation);
                    float allowed = MetricTolerance.WithUncertainty(tolerance, uncertaintyOriginal, uncertaintyValidation);
                    string widened = allowed > tolerance + 1e-6f
                        ? $", erlaubt {allowed * 100:F1}% wegen Schwankung der Rechnung"
                        : string.Empty;

                    SimulationLog.Detail($"Validierungs-Fitness: {validatedFitness:F4} (Abweichung: {deviation * 100:F1}%{widened})");

                    if (deviation <= allowed)
                    {
                        SimulationLog.Detail("[OK] Stabil. Champion bestätigt.");
                        return candidate;
                    }

                    SimulationLog.Detail("[ABGELEHNT] Modell ist instabil. Disqualifiziert.");
                    candidate.Fitness = DisqualifiedFitness;
                    candidate.Disqualified = true;
                }
                catch (Exception)
                {
                    SimulationLog.Detail("[FEHLER] Validierung fehlgeschlagen. Disqualifiziert.");
                    candidate.Fitness = DisqualifiedFitness;
                    candidate.Disqualified = true;
                }
            }

            SimulationLog.Detail("[WARNUNG] Kein Modell stabil. Wähle das Beste der Reste.");
            return sortedCandidates[0];
        }

        /// <summary>
        /// Verstellt einen zufällig gewählten Parameter um +/- <paramref name="jitter"/> (relativ bzgl. ParameterBounds falls vorhanden, sonst absolut).
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
