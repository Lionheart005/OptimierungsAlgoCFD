using System.Collections.Generic;
using System.Linq;

using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

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

            // Structural minimum thickness (4.5 mm = 9% of 50 mm chord) and positive lift requirement:
            OptimizationTargets = new Dictionary<string, float>
            {
                { "MinThicknessMm", Naca2DGeometryConstraints.DefaultMinThicknessMm },
                { "MinLift", Naca2DFitnessCalculator.DefaultMinLift }
            },

            // Empty because all three parameters are dimensionless percentages of chord length:
            DimensionalParameters = new HashSet<string>()
        };

        /// <inheritdoc/>
        public override IGeometryGenerator CreateGeometry(ProjectContext context)
            => new Naca2DGeometryGenerator();

        /// <inheritdoc/>
        public override IGeometryConstraintEvaluator? CreateGeometryConstraints(ProjectContext context)
            => new Naca2DGeometryConstraints();

        /// <inheritdoc/>
        public override IFitnessCalculator CreateFitness(ProjectContext context)
        {
            var su2Loaded = context.Loaded.FirstOrDefault(l => l.Name == "solvers/su2.json")?.Value as Su2SolverOptions;
            Su2SolverOptions su2 = su2Loaded ?? context.LoadSolverOptions<Su2SolverOptions>("su2");
            List<float>? weights = su2.OperatingPoints != null && su2.OperatingPoints.Count > 0
                ? su2.OperatingPoints.Select(op => op.Weight).ToList()
                : null;
            return new Naca2DFitnessCalculator(context.Project, weights);
        }
    }
}
