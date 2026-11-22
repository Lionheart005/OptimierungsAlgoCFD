using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Project-specific configuration: initial parameters, bounds, targets, and dimensional keys.
    /// Populated by each project in code and overlaid by <c>project.json</c>.
    /// </summary>
    public class ProjectConfig
    {
        public string ProjectName { get; set; } = "";

        // OptimizationAlgorithm lives in SimulationConfig (simulation.json), not here —
        // it is a framework setting rather than a project property.

        public Dictionary<string, float> BaseParameters { get; set; } = new();
        public Dictionary<string, float> MaxDeviations { get; set; } = new();
        public Dictionary<string, (float Min, float Max)> ParameterBounds { get; set; } = new();
        public Dictionary<string, float> OptimizationTargets { get; set; } = new();

        /// <summary>
        /// Parameters that carry physical length units (mm) and are scaled by the rubber-band scaler.
        /// Dimensionless parameters (e.g., TailTaper) are NOT scaled.
        /// </summary>
        public HashSet<string> DimensionalParameters { get; set; } = new();
    }
}
