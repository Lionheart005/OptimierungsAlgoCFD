using System.Collections.Generic;

namespace MyPicoGkProject
{
    /// <summary>
    /// Flexibles Ergebnis der Geometrie-Erzeugung.
    /// Ersetzt das starre Tuple (StlPath, Volume, FrontalArea, SensorDistance).
    /// Jedes Projekt gibt die Metriken zurück, die es braucht.
    /// </summary>
    public class GeometryResult
    {
        public string StlPath { get; set; } = "";
        public Dictionary<string, float> Metrics { get; set; } = new();
        // z.B. {"Volume": 4200, "FrontalArea": 850, "SensorDistance": 120}
        // oder {"Volume": 3000, "SurfaceArea": 1200, "WallThickness": 2.5}
    }
}
