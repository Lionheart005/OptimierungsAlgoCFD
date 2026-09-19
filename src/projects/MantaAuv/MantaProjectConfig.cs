using System.Collections.Generic;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Projects.MantaAuv
{
    /// <summary>
    /// Projektspezifische Konfiguration für das Manta-Ray AUV.
    /// Enthält alle Parameter, Bounds und Deviations die bisher in SimulationData hardcodiert waren.
    /// </summary>
    public static class MantaProjectConfig
    {
        public static ProjectConfig Create()
        {
            return new ProjectConfig
            {
                ProjectName = "MantaAuv",

                // Das Optimierungsverfahren steht im Framework (SimulationConfig /
                // simulation.json nebenan), weil es keine Projekteigenschaft ist.

                // --- INITIALE AKTIVE PARAMETER (AUV Konzept) ---
                BaseParameters = new Dictionary<string, float>
                {
                    { "Length", 50.0f },
                    { "Width", 40.0f },
                    { "MainRadius", 4.0f },
                    { "WingRadius", 3.0f },
                    { "TailTaper", 1.0f }
                },

                // --- INITIALE MUTATIONS-STÄRKE (Sigma-Streuung) ---
                MaxDeviations = new Dictionary<string, float>
                {
                    { "Length", 5.0f },
                    { "Width", 4.0f },
                    { "MainRadius", 1.0f },
                    { "WingRadius", 1.0f },
                    { "TailTaper", 0.1f }
                },

                // --- PARAMETER-GRENZEN (Hard-Limits für physikalische Plausibilität) ---
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    { "Length", (30.0f, 100.0f) },
                    { "Width", (20.0f, 80.0f) },
                    { "MainRadius", (4.0f, 15.0f) },
                    { "WingRadius", (2.0f, 10.0f) },
                    { "TailTaper", (0.1f, 1.5f) }
                },

                // --- OPTIMIERUNGSZIELE ---
                OptimizationTargets = new Dictionary<string, float>
                {
                    { "MinimumAllowedVolume", 2000f },
                    { "MaximumAllowedVolume", 85000f },
                    { "DragBalanceFactor", 3f }
                    // BouncerTolerance liegt jetzt im Framework (SimulationConfig),
                    // weil der Bouncer nicht mehr projektspezifisch ist.
                },

                // --- DIMENSIONSBEHAFTETE PARAMETER (werden beim Skalieren berücksichtigt) ---
                // TailTaper ist dimensionslos und wird NICHT skaliert
                DimensionalParameters = new HashSet<string>
                {
                    "Length", "Width", "MainRadius", "WingRadius"
                }
            };
        }
    }
}
