using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject
{
    public class RsmOptimizationAlgorithm : IOptimizationAlgorithm
    {
        private readonly Random _random = new Random();
        private Queue<Dictionary<string, float>> _nextVirtualCandidates = new Queue<Dictionary<string, float>>();

        public string Name => "RSM (Surrogate-basiert / Ersatzmodell)";

        public Dictionary<string, float> GenerateParameters(SimulationData data, int iteration, int variant, int maxVariants)
        {
            if (_nextVirtualCandidates.Count > 0)
            {
                return _nextVirtualCandidates.Dequeue();
            }

            Dictionary<string, float> candidate = new Dictionary<string, float>();
            
            if (iteration == 1 && variant == 1) return new Dictionary<string, float>(data.BaseParameters);

            foreach (var kvp in data.BaseParameters)
            {
                string paramName = kvp.Key;
                float min = data.ParameterBounds.ContainsKey(paramName) ? data.ParameterBounds[paramName].Min : kvp.Value * 0.5f;
                float max = data.ParameterBounds.ContainsKey(paramName) ? data.ParameterBounds[paramName].Max : kvp.Value * 1.5f;
                
                float randomValue = min + (float)_random.NextDouble() * (max - min);
                candidate.Add(paramName, randomValue);
            }

            return candidate;
        }

        public ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationData data)
        {
            // ========================================================
            // ZENTRALE FITNESS BERECHNUNG AUFRUFEN
            // ========================================================
            foreach (var record in records)
            {
                FitnessCalculator.CalculateFitness(record, data);
            }

            return records.OrderByDescending(r => r.Fitness).First();
        }

        public void PrepareNextIteration(SimulationData data, List<ModelRecord> currentRecords, ModelRecord bestRecord)
        {
            _nextVirtualCandidates.Clear();

            int k = data.BaseParameters.Count;
            int minRequiredSamples = ((k + 1) * (k + 2)) / 2 + 2;

            if (data.History.Count < minRequiredSamples)
            {
                int missing = minRequiredSamples - data.History.Count;
                Console.WriteLine($"\n[INFO] {Name}: Aufbau des Ersatzmodells. Benötige noch {missing} Basis-Simulationen (DoE).");
                return;
            }

            int virtualSimulations = data.Settings.RsmVirtualSimulations;
            Console.WriteLine($"\n[INFO] {Name}: Ersatzmodell ist aktiv!");
            Console.WriteLine($"       -> Simuliere {virtualSimulations:N0} virtuelle Varianten auf der Antwortfläche...");

            var virtualResults = new List<(Dictionary<string, float> Params, float PredictedFitness)>();

            for (int i = 0; i < virtualSimulations; i++)
            {
                bool exploit = _random.NextDouble() < data.Settings.RsmExploitationRatio;
                Dictionary<string, float> virtParams = GenerateVirtualCandidate(data, bestRecord, exploit);
                
                float predictedFitness = PredictFitnessSurrogate(virtParams, data.History, data.ParameterBounds, data.Settings.RsmIdwPower);
                
                virtualResults.Add((virtParams, predictedFitness));
            }

            var topCandidates = virtualResults
                .OrderByDescending(v => v.PredictedFitness)
                .Take(data.Settings.VariantsPerIteration)
                .ToList();

            Console.WriteLine($"       -> Virtuelle Optimierung beendet. Erwartete Top-Fitness: {topCandidates[0].PredictedFitness:F3}");
            Console.WriteLine($"       -> Übergebe die {data.Settings.VariantsPerIteration} besten Kandidaten an den echten Windkanal.");

            foreach (var candidate in topCandidates)
            {
                _nextVirtualCandidates.Enqueue(candidate.Params);
            }
        }

        private float PredictFitnessSurrogate(Dictionary<string, float> candidate, List<ModelRecord> history, Dictionary<string, (float Min, float Max)> bounds, float idwPower)
        {
            float numerator = 0f;
            float denominator = 0f;

            foreach (var record in history)
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

            return numerator / denominator;
        }

        private Dictionary<string, float> GenerateVirtualCandidate(SimulationData data, ModelRecord bestKnown, bool exploit)
        {
            Dictionary<string, float> candidate = new Dictionary<string, float>();

            foreach (var kvp in data.BaseParameters)
            {
                string key = kvp.Key;
                float min = data.ParameterBounds[key].Min;
                float max = data.ParameterBounds[key].Max;
                float range = max - min;

                float value;
                if (exploit && bestKnown.ActiveParameters.ContainsKey(key))
                {
                    float sigma = range * data.Settings.RsmExploitationSigma; 
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