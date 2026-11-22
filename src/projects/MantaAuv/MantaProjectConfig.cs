using System.Collections.Generic;

using FluidForge.Core;

namespace FluidForge.Projects.MantaAuv
{
    /// <summary>
    /// Project-specific default configuration for the Manta-Ray AUV.
    /// </summary>
    public static class MantaProjectConfig
    {
        public static ProjectConfig Create()
        {
            return new ProjectConfig
            {
                ProjectName = "MantaAuv",

                // The optimization algorithm is configured in SimulationConfig / simulation.json
                // because it is a framework setting rather than a project property.

                // --- INITIAL ACTIVE PARAMETERS (AUV concept) ---
                BaseParameters = new Dictionary<string, float>
                {
                    { "Length", 50.0f },
                    { "Width", 40.0f },
                    { "MainRadius", 4.0f },
                    { "WingRadius", 3.0f },
                    { "TailTaper", 1.0f }
                },

                // --- INITIAL MUTATION STEP SIZES (sigma) ---
                MaxDeviations = new Dictionary<string, float>
                {
                    { "Length", 5.0f },
                    { "Width", 4.0f },
                    { "MainRadius", 1.0f },
                    { "WingRadius", 1.0f },
                    { "TailTaper", 0.1f }
                },

                // --- PARAMETER BOUNDS (hard limits for physical plausibility) ---
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    { "Length", (30.0f, 100.0f) },
                    { "Width", (20.0f, 80.0f) },
                    { "MainRadius", (4.0f, 15.0f) },
                    { "WingRadius", (2.0f, 10.0f) },
                    { "TailTaper", (0.1f, 1.5f) }
                },

                // --- OPTIMIZATION TARGETS ---
                OptimizationTargets = new Dictionary<string, float>
                {
                    { "MinimumAllowedVolume", 2000f },
                    { "MaximumAllowedVolume", 85000f },
                    { "DragBalanceFactor", 3f }
                    // BouncerTolerance lives in SimulationConfig because the bouncer is framework-wide.
                },

                // --- DIMENSIONAL PARAMETERS (scaled by RubberBandScaler) ---
                // TailTaper is dimensionless and intentionally omitted.
                DimensionalParameters = new HashSet<string>
                {
                    "Length", "Width", "MainRadius", "WingRadius"
                }
            };
        }
    }
}
