using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject
{
    public class EvolutionaryAlgorithm : IOptimizationAlgorithm
    {
        private readonly Random _random = new Random();

        public string Name => "Evolutionärer Algorithmus (Box-Muller)";

        public Dictionary<string, float> GenerateParameters(SimulationData data, int iteration, int variant, int maxVariants)
        {
            if (variant == 1) return new Dictionary<string, float>(data.BaseParameters);

            int totalMutants = maxVariants - 1;
            int mutatedIndex = variant - 2; 
            float percentile = totalMutants > 1 ? (float)mutatedIndex / (totalMutants - 1) : 0.5f;

            float spreadFactor = 1.0f;
            float mutationChance = 1.0f;
            
            if (percentile <= data.Settings.RoleFineTuner)      { spreadFactor = 0.1f; mutationChance = 0.3f; }
            else if (percentile <= data.Settings.RoleCautious)  { spreadFactor = 0.5f; mutationChance = 0.6f; }
            else if (percentile <= data.Settings.RoleNormal)    { spreadFactor = 1.0f; mutationChance = 1.0f; }
            else                                                { spreadFactor = 2.0f; mutationChance = 1.0f; }

            int guaranteedIndex = _random.Next(data.BaseParameters.Count);
            Dictionary<string, float> mutant = new Dictionary<string, float>();

            for (int attempt = 0; attempt < 50; attempt++)
            {
                mutant.Clear();
                int currentIndex = 0;

                foreach (var kvp in data.BaseParameters)
                {
                    string paramName = kvp.Key;
                    float mutatedValue = kvp.Value;

                    if (currentIndex == guaranteedIndex || _random.NextDouble() < mutationChance)
                    {
                        float sigma = data.MaxDeviations.ContainsKey(paramName) ? data.MaxDeviations[paramName] : 0f;
                        mutatedValue += (float)(NextGaussian() * sigma * spreadFactor);
                    }

                    if (data.ParameterBounds.TryGetValue(paramName, out var bounds))
                    {
                        mutatedValue = Math.Clamp(mutatedValue, bounds.Min, bounds.Max);
                    }
                    else 
                    {
                        if (mutatedValue < 0.1f) mutatedValue = 0.1f; 
                    }

                    mutant.Add(paramName, mutatedValue);
                    currentIndex++;
                }

                if (!IsTooSimilarToHistory(mutant, data.History, percentile, data)) break; 
            }

            return mutant;
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
            Console.WriteLine($"\n[INFO] {Name}: Bereite nächste Stufe vor...");
            
            foreach (var kvp in bestRecord.ActiveParameters)
            {
                data.BaseParameters[kvp.Key] = kvp.Value;
                Console.WriteLine($"       -> Neues Basis-Gen [{kvp.Key}]: {kvp.Value:F3}");
            }

            var parent = currentRecords.FirstOrDefault(r => r.Variant == 1);
            float parentFitness = parent != null ? parent.Fitness : 0f;
            
            int successCount = currentRecords.Count(r => r.Fitness > parentFitness);
            int amountOfMutations = currentRecords.Count - 1;

            if (amountOfMutations > 0)
            {
                float successRate = (float)successCount / amountOfMutations;
                Console.WriteLine($"       -> Erfolgsquote der Mutationen: {successRate * 100:F1}%");

                float adjustmentFactor = 1.0f;
                if (successRate > 0.2f) adjustmentFactor = 1.2f;  
                else if (successRate < 0.2f) adjustmentFactor = 0.85f; 
                
                if (adjustmentFactor != 1.0f)
                {
                    foreach (var key in data.MaxDeviations.Keys.ToList())
                    {
                        data.MaxDeviations[key] *= adjustmentFactor;
                    }
                    Console.WriteLine($"       -> Mutations-Stärke (Sigma) angepasst (Faktor {adjustmentFactor:F2}).");
                }
            }
        }

        private double NextGaussian()
        {
            double u1 = 1.0 - _random.NextDouble();
            double u2 = 1.0 - _random.NextDouble();
            if (u1 <= 0) u1 = 0.000001; 
            return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Sin(2.0 * Math.PI * u2);
        }

        private bool IsTooSimilarToHistory(Dictionary<string, float> mutant, List<ModelRecord> history, double percentile, SimulationData data)
        {
            if (history == null || history.Count == 0) return false;

            float requiredDistance = percentile <= data.Settings.RoleFineTuner 
                ? data.Settings.MinimalDeviationExploitation 
                : data.Settings.MinimalDeviationExploration;

            foreach (var record in history)
            {
                float euklidDistSq = 0f;
                foreach (var key in mutant.Keys)
                {
                    if (record.ActiveParameters.ContainsKey(key))
                    {
                        float diff = mutant[key] - record.ActiveParameters[key];
                        euklidDistSq += diff * diff;
                    }
                }
                if (Math.Sqrt(euklidDistSq) < requiredDistance) return true; 
            }
            return false;
        }
    }
}