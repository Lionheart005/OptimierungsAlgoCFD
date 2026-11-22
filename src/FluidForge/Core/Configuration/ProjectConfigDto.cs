using System;
using System.Collections.Generic;
using System.Linq;

namespace FluidForge.Core
{
    /// <summary>
    /// JSON representation of <see cref="ProjectConfig"/>.
    ///
    /// Separate DTO class because <c>System.Text.Json</c> cannot serialize the ValueTuple
    /// <c>(float Min, float Max)</c> in <see cref="ProjectConfig.ParameterBounds"/> (Item1/Item2
    /// are fields, not properties). In JSON this is written as <c>"Length": { "Min": 30, "Max": 100 }</c>.
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

        public ProjectConfig ToProjectConfig()
        {
            var deviations = new Dictionary<string, float>(MaxDeviations);
            foreach (KeyValuePair<string, ParameterBoundsDto> entry in ParameterBounds)
            {
                if (!deviations.ContainsKey(entry.Key))
                {
                    deviations[entry.Key] = MathF.Max((entry.Value.Max - entry.Value.Min) * 0.1f, 1e-4f);
                }
            }

            return new ProjectConfig
            {
                ProjectName = ProjectName,
                BaseParameters = new Dictionary<string, float>(BaseParameters),
                MaxDeviations = deviations,
                ParameterBounds = ParameterBounds.ToDictionary(
                    entry => entry.Key,
                    entry => (entry.Value.Min, entry.Value.Max)),
                OptimizationTargets = new Dictionary<string, float>(OptimizationTargets),
                DimensionalParameters = new HashSet<string>(DimensionalParameters)
            };
        }
    }

    /// <summary>Lower and upper bound of a parameter, JSON-serializable.</summary>
    public class ParameterBoundsDto
    {
        public float Min { get; set; }
        public float Max { get; set; }
    }
}
