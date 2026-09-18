using System.Collections.Generic;

namespace MyPicoGkProject
{
    /// <summary>
    /// Interface für Optimierungsalgorithmen.
    /// Arbeitet mit SimulationContext statt SimulationData (entkoppelt von konkreten AUV-Parametern).
    /// IFitnessCalculator wird per Konstruktor in die Algorithmen injiziert.
    /// </summary>
    public interface IOptimizationAlgorithm
    {
        /// <summary>
        /// Name des Algorithmus für die Konsolen-Ausgabe.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Generiert die neuen Parameter für die aktuelle Variante.
        /// </summary>
        Dictionary<string, float> GenerateParameters(SimulationContext context, int iteration, int variant, int maxVariants);

        /// <summary>
        /// Wertet eine komplette Iteration aus und gibt den besten Kandidaten zurück.
        /// </summary>
        ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext context);

        /// <summary>
        /// Bereitet den Algorithmus auf die nächste Iteration vor (z.B. neue Basiswerte setzen).
        /// </summary>
        void PrepareNextIteration(SimulationContext context, List<ModelRecord> currentRecords, ModelRecord bestRecord);
    }
}

