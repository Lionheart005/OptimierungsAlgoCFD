using System.Collections.Generic;

using FluidForge.Composition;
using FluidForge.Core;

namespace FluidForge.Projects.Naca2D
{
    /// <summary>
    /// Project wiring for the 2D CFD project Naca2D.
    /// Two-dimensional NACA airfoil based on <see cref="Cfd2DProjectDefinition"/>:
    /// runs without PicoGK or Xvfb using Gmsh2DMesher and 2D SU2.
    /// </summary>
    public sealed class Naca2DProject : Cfd2DProjectDefinition
    {
        /// <summary>
        /// Must match the folder name under <c>src/projects/</c> exactly.
        /// </summary>
        public override string Name => "Naca2D";

        /// <summary>
        /// Layer 1 configuration: code defaults for the 3 NACA airfoil parameters.
        /// All 3 parameters are dimensionless percentages relative to chord length.
        /// </summary>
        public override ProjectConfig CreateDefaults() => new ProjectConfig
        {
            // Initial active parameters (% of chord length c):
            // NACA 0012: 0% camber, 40% camber position, 12% max thickness.
            BaseParameters = new Dictionary<string, float>
            {
                { "MaxCamber", 0.0f },
                { "CamberPosition", 40.0f },
                { "MaxThickness", 12.0f }
            },

            // Initial mutation step sizes per parameter:
            MaxDeviations = new Dictionary<string, float>
            {
                { "MaxCamber", 0.8f },
                { "CamberPosition", 6.0f },
                { "MaxThickness", 2.0f }
            },

            // Hard bounds for physical plausibility:
            ParameterBounds = new Dictionary<string, (float Min, float Max)>
            {
                { "MaxCamber", (0.0f, 8.0f) },
                { "CamberPosition", (15.0f, 75.0f) },
                { "MaxThickness", (6.0f, 24.0f) }
            },

            // Empty because fitness is formulated as glide ratio CL / CD with no external targets:
            OptimizationTargets = new Dictionary<string, float>(),

            // Empty because all three parameters are dimensionless percentages of chord length:
            DimensionalParameters = new HashSet<string>()
        };

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new Naca2DGeometryGenerator();

        /// <inheritdoc/>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
            => new Naca2DFitnessCalculator(context.Project);
    }
}
