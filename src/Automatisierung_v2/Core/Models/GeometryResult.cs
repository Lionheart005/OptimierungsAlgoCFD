using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Wie eine Geometrie-Metrik von der PicoGK-Arbeitsgröße auf die reale Größe
    /// zurückgerechnet wird. Das Projekt deklariert das — der Kern rät nicht mehr
    /// anhand des Metriknamens.
    /// </summary>
    public enum MetricScaling
    {
        /// <summary>Dimensionslos (z.B. Verhältnisse, Zählwerte) — unverändert übernehmen.</summary>
        None,
        /// <summary>Länge (mm) — mit 1/ShrinkFactor multiplizieren.</summary>
        Linear,
        /// <summary>Fläche (mm²) — quadratisch zurückskalieren.</summary>
        Area,
        /// <summary>Volumen (mm³) — kubisch zurückskalieren.</summary>
        Volume
    }

    /// <summary>
    /// Flexibles Ergebnis der Geometrie-Erzeugung.
    /// Ersetzt das starre Tuple (StlPath, Volume, FrontalArea, SensorDistance).
    /// Jedes Projekt gibt die Metriken zurück, die es braucht — samt Angabe,
    /// wie sie zurückskaliert werden sollen.
    /// </summary>
    public class GeometryResult
    {
        public string StlPath { get; set; } = "";
        public Dictionary<string, float> Metrics { get; set; } = new();
        // z.B. {"Volume": 4200, "FrontalArea": 850, "SensorDistance": 120}
        // oder {"Volume": 3000, "SurfaceArea": 1200, "WallThickness": 2.5}

        /// <summary>
        /// Rückskalierungs-Dimension pro Metrik. Fehlt ein Eintrag,
        /// gilt <see cref="MetricScaling.None"/> (Wert wird unverändert übernommen).
        /// </summary>
        public Dictionary<string, MetricScaling> MetricScalings { get; set; } = new();

        /// <summary>
        /// Metrik samt Dimension eintragen — der bevorzugte Weg für Projekte.
        /// </summary>
        public void AddMetric(string name, float value, MetricScaling scaling = MetricScaling.None)
        {
            Metrics[name] = value;
            MetricScalings[name] = scaling;
        }

        /// <summary>
        /// Deklarierte Dimension einer Metrik; <see cref="MetricScaling.None"/>, wenn nichts angegeben wurde.
        /// </summary>
        public MetricScaling ScalingFor(string name)
            => MetricScalings.TryGetValue(name, out var scaling) ? scaling : MetricScaling.None;
    }
}
