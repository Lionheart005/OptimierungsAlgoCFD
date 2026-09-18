namespace MyPicoGkProject
{
    /// <summary>
    /// Berechnet die Fitness eines Modells basierend auf seinen Metriken.
    /// Jedes Projekt definiert seine eigene Fitness-Formel.
    /// </summary>
    public interface IFitnessCalculator
    {
        void CalculateFitness(ModelRecord record, SimulationConfig config);
    }
}
