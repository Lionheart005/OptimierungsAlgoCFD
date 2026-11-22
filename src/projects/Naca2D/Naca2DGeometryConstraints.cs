using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.Naca2D
{
    /// <summary>
    /// Pre-CFD structural thickness constraint for 2D NACA airfoils.
    /// Rejects unrealistically thin profiles before meshing and SU2 execution.
    /// </summary>
    public sealed class Naca2DGeometryConstraints : IGeometryConstraintEvaluator
    {
        /// <summary>Default minimum airfoil thickness in mm (9% of 50 mm chord).</summary>
        public const float DefaultMinThicknessMm = 4.5f;

        /// <inheritdoc />
        public IReadOnlyList<ConstraintEvaluation> EvaluateGeometryConstraints(ModelRecord record, ProjectConfig project)
        {
            float minThicknessMm = project.OptimizationTargets.TryGetValue("MinThicknessMm", out float configured)
                ? configured
                : DefaultMinThicknessMm;

            float thicknessMm = record.PassiveParameters.TryGetValue("Thickness", out float measured)
                ? measured
                : float.NaN;

            return new[]
            {
                ConstraintEvaluation.AtLeast("MinThicknessMm", thicknessMm, minThicknessMm, isPreSolve: true)
            };
        }
    }
}
