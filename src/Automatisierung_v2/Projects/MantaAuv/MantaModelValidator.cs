using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject
{
    /// <summary>
    /// Manta-Ray AUV Modell-Validator.
    /// Prüft Stabilität des Champions durch Re-Simulation mit leicht veränderten Parametern.
    /// Validiert anhand des Drag-Werts.
    /// </summary>
    public class MantaModelValidator : IModelValidator
    {
        private readonly ProjectConfig _project;

        public MantaModelValidator(ProjectConfig project)
        {
            _project = project;
        }

        public ModelRecord ValidateChampion(
            List<ModelRecord> candidates,
            SimulationConfig config,
            Func<Dictionary<string, float>, ModelRecord> resimulate)
        {
            Console.WriteLine("\n[VALIDIERUNG] Teste Stabilität des Champions...");
            var sortedCandidates = candidates.OrderByDescending(r => r.Fitness).ToList();
            Random rnd = new Random();

            float tolerance = _project.OptimizationTargets.ContainsKey("BouncerTolerance") 
                ? _project.OptimizationTargets["BouncerTolerance"] 
                : 0.20f;

            foreach (var candidate in sortedCandidates)
            {
                if (candidate.Fitness < 0.1f) break;

                float originalDrag = candidate.PassiveParameters["Drag"];
                Console.WriteLine($"       -> Prüfe Variante {candidate.Variant} (Referenz-Drag: {originalDrag:F4})");

                var testParams = new Dictionary<string, float>(candidate.ActiveParameters);
                string keyToJitter = testParams.Keys.ElementAt(rnd.Next(testParams.Count));
                float jitter = rnd.NextDouble() > 0.5 ? 0.01f : -0.01f;
                testParams[keyToJitter] += jitter;

                try
                {
                    ModelRecord validationResult = resimulate(testParams);
                    float validatedDrag = validationResult.PassiveParameters.ContainsKey("Drag") 
                        ? validationResult.PassiveParameters["Drag"] 
                        : float.MaxValue;

                    float deviation = Math.Abs(validatedDrag - originalDrag) / Math.Abs(originalDrag);

                    Console.WriteLine($"       -> Validierungs-Drag: {validatedDrag:F4} (Abweichung: {deviation * 100:F1}%)");

                    if (deviation <= tolerance)
                    {
                        Console.WriteLine("       -> [OK] Physikalisch stabil. Champion bestätigt.");
                        return candidate; 
                    }
                    else
                    {
                        Console.WriteLine("       -> [ABGELEHNT] Modell ist instabil. Disqualifiziert.");
                        candidate.Fitness = 0.0001f; 
                    }
                }
                catch (Exception)
                {
                    Console.WriteLine("       -> [FEHLER] Validierung fehlgeschlagen. Disqualifiziert.");
                    candidate.Fitness = 0.0001f;
                }
            }

            Console.WriteLine("       -> [WARNUNG] Kein Modell stabil. Wähle das Beste der Reste.");
            return sortedCandidates[0]; 
        }
    }
}
