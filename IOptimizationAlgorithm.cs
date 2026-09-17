using System.Collections.Generic;

namespace MyPicoGkProject
{
    public interface IOptimizationAlgorithm
    {
        /// <summary>
        /// Name des Algorithmus für die Konsolen-Ausgabe.
        /// </summary>
        string Name { get; }

        /// <summary>
        /// Generiert die neuen Parameter für die aktuelle Variante.
        /// </summary>
        Dictionary<string, float> GenerateParameters(SimulationData data, int iteration, int variant, int maxVariants);

        /// <summary>
        /// Wertet eine komplette Iteration aus und gibt den besten Kandidaten zurück.
        /// </summary>
        ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationData data);

        /// <summary>
        /// Bereitet den Algorithmus auf die nächste Iteration vor (z.B. neue Basiswerte setzen).
        /// </summary>
        void PrepareNextIteration(SimulationData data, List<ModelRecord> currentRecords, ModelRecord bestRecord);
    }
}