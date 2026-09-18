using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Datenmodell für ein einzelnes Modell (Variante).
    /// </summary>
    public class ModelRecord
    {
        public int Iteration { get; set; }
        public int Variant { get; set; }
        
        // Aktive Parameter (werden vom Algorithmus gesteuert/mutiert)
        public Dictionary<string, float> ActiveParameters { get; set; } = new Dictionary<string, float>();
        
        // Passive Parameter / Metriken (Resultate aus der Simulation, z.B. Volumen, Drag)
        public Dictionary<string, float> PassiveParameters { get; set; } = new Dictionary<string, float>();

        /// <summary>
        /// Generischer Fehler-Marker: Vernetzung oder Solver-Kette sind abgebrochen,
        /// die passiven Parameter sind also unvollständig. Der Kern kennt keine
        /// Metriknamen — der <see cref="IFitnessCalculator"/> entscheidet, was das bedeutet.
        /// </summary>
        public bool SimulationFailed { get; set; }

        public float Fitness { get; set; }
        public string StlPath { get; set; } = "";
        public string MeshPath { get; set; } = "";
    }
}
