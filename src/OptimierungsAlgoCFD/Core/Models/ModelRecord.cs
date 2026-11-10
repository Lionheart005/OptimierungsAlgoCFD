using System.Collections.Generic;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Datenmodell für ein einzelnes Modell (Variante).
    /// </summary>
    public class ModelRecord
    {
        /// <summary>
        /// Schlüssel in <see cref="PassiveParameters"/>: 1, wenn die Vernetzung scheiterte (<see cref="MeshGenerationException"/>).
        /// Ein Vernetzungsfehler ist kein Strömungsabsturz; das Bayes-Erfolgsmodell lernt daraus nicht.
        /// </summary>
        public const string MeshFailedKey = "MeshFailed";

        /// <summary>Schlüssel in <see cref="PassiveParameters"/>: 1, wenn das Netz über der Zellgrenze lag und SU2 nicht lief.</summary>
        public const string CellLimitExceededKey = "CellLimitExceeded";

        /// <summary>Schlüssel in <see cref="PassiveParameters"/>: 1 bei Ersatznetz (Wandschicht überall verdünnt, Stufe C der Rückfall-Leiter).</summary>
        public const string MeshFallbackKey = "MeshFallback";

        /// <summary>
        /// Netz-Metadaten in <see cref="PassiveParameters"/> (Wandschicht-Kennwerte, Ersatznetz, Vernetzungsfehler, Zellgrenze):
        /// keine Kennwerte der Geometrie und deshalb aus Kennwert-Vergleichen und Log-Auswahl ausgenommen.
        /// </summary>
        public static bool IsMeshMetadataKey(string key)
            => key.StartsWith("BoundaryLayer", System.StringComparison.OrdinalIgnoreCase)
               || key.Equals(MeshFailedKey, System.StringComparison.OrdinalIgnoreCase)
               || key.Equals(MeshFallbackKey, System.StringComparison.OrdinalIgnoreCase)
               || key.Equals(CellLimitExceededKey, System.StringComparison.OrdinalIgnoreCase);

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

        /// <summary>
        /// Vom Türsteher (<see cref="ChampionValidator"/>) als instabil verworfen (TODO-53).
        /// Die Fitness bleibt dabei auf dem niedrigen Strafwert, damit Rsm und Evolution wie
        /// bisher rechnen. Das Kennzeichen ist rein informativ; nur der Bayes-Optimierer
        /// nutzt es, um solche Rechnungen nicht als Lerndaten zu verwenden.
        /// </summary>
        public bool Disqualified { get; set; }

        public float Fitness { get; set; }
        public string StlPath { get; set; } = "";
        public string MeshPath { get; set; } = "";
    }
}
