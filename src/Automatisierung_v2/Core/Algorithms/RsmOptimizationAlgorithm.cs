using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject
{
    /// <summary>
    /// RSM (Response Surface Methodology) Optimierungsalgorithmus.
    /// Surrogate-basiert mit Inverse Distance Weighting (IDW).
    /// Angepasst für das neue Interface (SimulationContext statt SimulationData).
    /// IFitnessCalculator wird per Konstruktor injiziert.
    /// </summary>
    public class RsmOptimizationAlgorithm : IOptimizationAlgorithm
    {
        private readonly Random _random = new Random();
        private readonly IFitnessCalculator _fitnessCalculator;
        private Queue<Dictionary<string, float>> _nextVirtualCandidates = new Queue<Dictionary<string, float>>();

        public RsmOptimizationAlgorithm(IFitnessCalculator fitnessCalculator)
        {
            _fitnessCalculator = fitnessCalculator;
        }

        public string Name => "RSM (Surrogate-basiert / Ersatzmodell)";

        public Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants)
        {
            if (_nextVirtualCandidates.Count > 0)
            {
                return _nextVirtualCandidates.Dequeue();
            }

            Dictionary<string, float> candidate = new Dictionary<string, float>();
            
            // Arbeitspunkt = Laufzeitzustand im Context (TODO-17), nicht die Projekt-Vorgabe.
            if (iteration == 1 && variant == 1) return new Dictionary<string, float>(context.CurrentBaseParameters);

            foreach (var kvp in context.CurrentBaseParameters)
            {
                string paramName = kvp.Key;
                float min = context.Project.ParameterBounds.ContainsKey(paramName) ? context.Project.ParameterBounds[paramName].Min : kvp.Value * 0.5f;
                float max = context.Project.ParameterBounds.ContainsKey(paramName) ? context.Project.ParameterBounds[paramName].Max : kvp.Value * 1.5f;
                
                float randomValue = min + (float)_random.NextDouble() * (max - min);
                candidate.Add(paramName, randomValue);
            }

            return candidate;
        }

        public ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context)
        {
            // ========================================================
            // ZENTRALE FITNESS BERECHNUNG AUFRUFEN (jetzt über Interface)
            // ========================================================
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

            // Nur gelungene Simulationen taugen als Stützstellen. Fehlgeschlagene Modelle
            // tragen keine echte Fitness, sie würden die Antwortfläche verzerren — sie zählen
            // deshalb auch nicht für die DoE-Anzahl. Stattdessen läuft eine Variante mehr.
            var usableSamples = context.History.Where(r => !r.SimulationFailed).ToList();

            if (usableSamples.Count < minRequiredSamples)
            {
                int missing = minRequiredSamples - usableSamples.Count;
                int failed = context.History.Count - usableSamples.Count;
                string failedNote = failed > 0 ? $" ({failed} fehlgeschlagene Simulation(en) zählen nicht mit)" : "";
                Console.WriteLine($"\n[INFO] {Name}: Aufbau des Ersatzmodells. Benötige noch {missing} Basis-Simulationen (DoE).{failedNote}");
                return;
            }

            int virtualSimulations = context.Config.RsmVirtualSimulations;
            Console.WriteLine($"\n[INFO] {Name}: Ersatzmodell ist aktiv!");
            Console.WriteLine($"       -> Simuliere {virtualSimulations:N0} virtuelle Varianten auf der Antwortfläche...");

            var virtualResults = new List<(Dictionary<string, float> Params, float PredictedFitness)>();

            for (int i = 0; i < virtualSimulations; i++)
            {
                bool exploit = _random.NextDouble() < context.Config.RsmExploitationRatio;
                Dictionary<string, float> virtParams = GenerateVirtualCandidate(context, bestRecord, exploit);
                
                float predictedFitness = PredictFitnessSurrogate(virtParams, usableSamples, context.Project.ParameterBounds, context.Config.RsmIdwPower);
                
                virtualResults.Add((virtParams, predictedFitness));
            }

            var topCandidates = virtualResults
                .OrderByDescending(v => v.PredictedFitness)
                .Take(context.Config.VariantsPerIteration)
                .ToList();

            Console.WriteLine($"       -> Virtuelle Optimierung beendet. Erwartete Top-Fitness: {topCandidates[0].PredictedFitness:F3}");
            Console.WriteLine($"       -> Übergebe die {context.Config.VariantsPerIteration} besten Kandidaten an den echten Windkanal.");

            foreach (var candidate in topCandidates)
            {
                _nextVirtualCandidates.Enqueue(candidate.Params);
            }
        }

        /// <summary>
        /// IDW-Schätzung der Fitness. <paramref name="samples"/> enthält nur gelungene
        /// Simulationen — abgebrochene Läufe sind keine gültigen Stützstellen.
        /// </summary>
        private float PredictFitnessSurrogate(Dictionary<string, float> candidate, List<ModelRecord> samples, Dictionary<string, (float Min, float Max)> bounds, float idwPower)
        {
            float numerator = 0f;
            float denominator = 0f;

            foreach (var record in samples)
            {
                float distSq = 0f;

                foreach (var key in candidate.Keys)
                {
                    if (record.ActiveParameters.ContainsKey(key))
                    {
                        float range = bounds[key].Max - bounds[key].Min;
                        if (range <= 0) range = 1f;

                        float diff = (candidate[key] - record.ActiveParameters[key]) / range;
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
            Dictionary<string, float> candidate = new Dictionary<string, float>();

            foreach (var kvp in context.CurrentBaseParameters)
            {
                string key = kvp.Key;
                float min = context.Project.ParameterBounds[key].Min;
                float max = context.Project.ParameterBounds[key].Max;
                float range = max - min;

                float value;
                if (exploit && bestKnown.ActiveParameters.ContainsKey(key))
                {
                    float sigma = range * context.Config.RsmExploitationSigma; 
                    value = bestKnown.ActiveParameters[key] + (float)(NextGaussian() * sigma);
                }
                else
                {
                    value = min + (float)_random.NextDouble() * range;
                }

                candidate.Add(key, Math.Clamp(value, min, max));
            }

            return candidate;
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

