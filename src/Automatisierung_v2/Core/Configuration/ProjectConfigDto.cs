using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// JSON-Abbild der <see cref="ProjectConfig"/>.
    ///
    /// Eigene Klasse, weil <c>System.Text.Json</c> das ValueTuple
    /// <c>(float Min, float Max)</c> in <see cref="ProjectConfig.ParameterBounds"/> nicht
    /// serialisieren kann (Item1/Item2 sind Felder, keine Properties). In der JSON-Datei
    /// steht deshalb <c>"Length": { "Min": 30, "Max": 100 }</c>.
    /// </summary>
    public class ProjectConfigDto
    {
        public string ProjectName { get; set; } = "";
        public Dictionary<string, float> BaseParameters { get; set; } = new();
        public Dictionary<string, float> MaxDeviations { get; set; } = new();
        public Dictionary<string, ParameterBoundsDto> ParameterBounds { get; set; } = new();
        public Dictionary<string, float> OptimizationTargets { get; set; } = new();
        public List<string> DimensionalParameters { get; set; } = new();

        public static ProjectConfigDto FromProjectConfig(ProjectConfig config) => new ProjectConfigDto
        {
            ProjectName = config.ProjectName,
            BaseParameters = new Dictionary<string, float>(config.BaseParameters),
            MaxDeviations = new Dictionary<string, float>(config.MaxDeviations),
            ParameterBounds = config.ParameterBounds.ToDictionary(
                entry => entry.Key,
                entry => new ParameterBoundsDto { Min = entry.Value.Min, Max = entry.Value.Max }),
            OptimizationTargets = new Dictionary<string, float>(config.OptimizationTargets),
            DimensionalParameters = config.DimensionalParameters.ToList()
        };

        public ProjectConfig ToProjectConfig() => new ProjectConfig
        {
            ProjectName = ProjectName,
            BaseParameters = new Dictionary<string, float>(BaseParameters),
            MaxDeviations = new Dictionary<string, float>(MaxDeviations),
            ParameterBounds = ParameterBounds.ToDictionary(
                entry => entry.Key,
                entry => (entry.Value.Min, entry.Value.Max)),
            OptimizationTargets = new Dictionary<string, float>(OptimizationTargets),
            DimensionalParameters = new HashSet<string>(DimensionalParameters)
        };
    }

    /// <summary>Unter- und Obergrenze eines Parameters, JSON-tauglich.</summary>
    public class ParameterBoundsDto
    {
        public float Min { get; set; }
        public float Max { get; set; }
    }
}
