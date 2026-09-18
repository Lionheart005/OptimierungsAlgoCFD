using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject
{
    /// <summary>
    /// Evolutionärer Algorithmus mit Box-Muller Mutation und Rollenverteilung.
    /// Angepasst für das neue Interface (SimulationContext statt SimulationData).
    /// IFitnessCalculator wird per Konstruktor injiziert.
    /// </summary>
    public class EvolutionaryAlgorithm : IOptimizationAlgorithm
    {
        private readonly Random _random;
        private readonly IFitnessCalculator _fitnessCalculator;

        /// <param name="fitnessCalculator">Bewertet die Modelle einer Iteration.</param>
        /// <param name="random">
        /// Optional: feste Zufallsquelle. Im Lauf ungesetzt (echter Zufall), in Tests
        /// ein <c>new Random(seed)</c>, damit Mutationen reproduzierbar sind (TODO-19).
        /// </param>
        public EvolutionaryAlgorithm(IFitnessCalculator fitnessCalculator, Random? random = null)
        {
            _fitnessCalculator = fitnessCalculator;
            _random = random ?? new Random();
        }

        public string Name => "Evolutionärer Algorithmus (Box-Muller)";

        public Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants)
        {
            // Arbeitspunkt und Sigma sind Laufzeitzustand (TODO-17) und stehen im Context,
            // nicht in der Projekt-Konfiguration.
            if (variant == 1) return new Dictionary<string, float>(context.CurrentBaseParameters);

            int totalMutants = maxVariants - 1;
            int mutatedIndex = variant - 2; 
            float percentile = totalMutants > 1 ? (float)mutatedIndex / (totalMutants - 1) : 0.5f;

            float spreadFactor = 1.0f;
            float mutationChance = 1.0f;
            
            if (percentile <= context.Config.RoleFineTuner)      { spreadFactor = 0.1f; mutationChance = 0.3f; }
            else if (percentile <= context.Config.RoleCautious)  { spreadFactor = 0.5f; mutationChance = 0.6f; }
            else if (percentile <= context.Config.RoleNormal)    { spreadFactor = 1.0f; mutationChance = 1.0f; }
            else                                                 { spreadFactor = 2.0f; mutationChance = 1.0f; }

            int guaranteedIndex = _random.Next(context.CurrentBaseParameters.Count);
            Dictionary<string, float> mutant = new Dictionary<string, float>();

            for (int attempt = 0; attempt < 50; attempt++)
            {
                mutant.Clear();
                int currentIndex = 0;

                foreach (var kvp in context.CurrentBaseParameters)
                {
                    string paramName = kvp.Key;
                    float mutatedValue = kvp.Value;

                    if (currentIndex == guaranteedIndex || _random.NextDouble() < mutationChance)
                    {
                        float sigma = context.CurrentDeviations.ContainsKey(paramName) ? context.CurrentDeviations[paramName] : 0f;
                        mutatedValue += (float)(NextGaussian() * sigma * spreadFactor);
                    }

                    if (context.Project.ParameterBounds.TryGetValue(paramName, out var bounds))
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

                if (!IsTooSimilarToHistory(mutant, context.History, percentile, context)) break; 
            }

            return mutant;
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
            Console.WriteLine($"\n[INFO] {Name}: Bereite nächste Stufe vor...");
            
            foreach (var kvp in bestRecord.ActiveParameters)
            {
                context.CurrentBaseParameters[kvp.Key] = kvp.Value;
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
                    foreach (var key in context.CurrentDeviations.Keys.ToList())
                    {
                        context.CurrentDeviations[key] *= adjustmentFactor;
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

        private bool IsTooSimilarToHistory(Dictionary<string, float> mutant, List<ModelRecord> history, double percentile, SimulationContext context)
        {
            if (history == null || history.Count == 0) return false;

            float requiredDistance = percentile <= context.Config.RoleFineTuner 
                ? context.Config.MinimalDeviationExploitation 
                : context.Config.MinimalDeviationExploration;

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

