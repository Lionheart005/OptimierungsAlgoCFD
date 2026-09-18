using System.Collections.Generic;

namespace MyPicoGkProject
{
    /// <summary>
    /// Projektspezifische Konfiguration. Ersetzt die hardcodierten AUV-Parameter im SimulationData-Konstruktor.
    /// Jedes Projekt füllt diese Klasse — entweder im Code oder perspektivisch aus einer JSON-Datei.
    /// </summary>
    public class ProjectConfig
    {
        public string ProjectName { get; set; } = "";
        public Dictionary<string, float> BaseParameters { get; set; } = new();
        public Dictionary<string, float> MaxDeviations { get; set; } = new();
        public Dictionary<string, (float Min, float Max)> ParameterBounds { get; set; } = new();
        public Dictionary<string, float> OptimizationTargets { get; set; } = new();

        /// <summary>
        /// Parameter die dimensionsbehaftet sind (mm) und beim Skalieren berücksichtigt werden.
        /// Dimensionslose Parameter (z.B. TailTaper) werden NICHT skaliert.
        /// </summary>
        public HashSet<string> DimensionalParameters { get; set; } = new();
    }
}
