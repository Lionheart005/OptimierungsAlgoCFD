using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject
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

        /// <param name="fitness">Bewertet das Re-Simulat — dieselbe Formel wie im Optimierungslauf.</param>
        /// <param name="random">Optional: feste Zufallsquelle für reproduzierbare Tests.</param>
        public ChampionValidator(IFitnessCalculator fitness, Random? random = null)
        {
            _fitness = fitness;
            _random = random ?? new Random();
        }

        public ModelRecord ValidateChampion(
            List<ModelRecord> candidates,
            SimulationConfig config,
            Func<Dictionary<string, float>, ModelRecord> resimulate)
        {
            if (candidates.Count == 0)
                throw new InvalidOperationException("[VALIDIERUNG] Keine Kandidaten zum Prüfen vorhanden.");

            Console.WriteLine("\n[VALIDIERUNG] Teste Stabilität des Champions...");
            var sortedCandidates = candidates.OrderByDescending(r => r.Fitness).ToList();

            float tolerance = config.BouncerTolerance;

            foreach (var candidate in sortedCandidates)
            {
                if (candidate.Fitness < MinimumFitnessToTest) break;

                float originalFitness = candidate.Fitness;
                Console.WriteLine($"       -> Prüfe Variante {candidate.Variant} (Referenz-Fitness: {originalFitness:F4})");

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

                    Console.WriteLine($"       -> Validierungs-Fitness: {validatedFitness:F4} (Abweichung: {deviation * 100:F1}%)");

                    if (deviation <= tolerance)
                    {
                        Console.WriteLine("       -> [OK] Stabil. Champion bestätigt.");
                        return candidate;
                    }

                    Console.WriteLine("       -> [ABGELEHNT] Modell ist instabil. Disqualifiziert.");
                    candidate.Fitness = DisqualifiedFitness;
                }
                catch (Exception)
                {
                    Console.WriteLine("       -> [FEHLER] Validierung fehlgeschlagen. Disqualifiziert.");
                    candidate.Fitness = DisqualifiedFitness;
                }
            }

            Console.WriteLine("       -> [WARNUNG] Kein Modell stabil. Wähle das Beste der Reste.");
            return sortedCandidates[0];
        }

        /// <summary>
        /// Verstellt einen zufällig gewählten Parameter um +/- <paramref name="jitter"/>.
        /// </summary>
        private Dictionary<string, float> JitterParameters(Dictionary<string, float> parameters, float jitter)
        {
            var testParams = new Dictionary<string, float>(parameters);
            if (testParams.Count == 0) return testParams;

            string keyToJitter = testParams.Keys.ElementAt(_random.Next(testParams.Count));
            testParams[keyToJitter] += _random.NextDouble() > 0.5 ? jitter : -jitter;
            return testParams;
        }
    }
}
