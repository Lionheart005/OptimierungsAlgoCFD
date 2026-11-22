using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.MantaAuv
{
    /// <summary>
    /// Pre-CFD payload volume constraint for the Manta-Ray AUV.
    /// Rejects variants outside <c>[MinimumAllowedVolume, MaximumAllowedVolume]</c>
    /// before meshing and CFD execution, avoiding artificial penalty cliffs.
    /// </summary>
    public sealed class MantaGeometryConstraints : IGeometryConstraintEvaluator
    {
        /// <inheritdoc />
        public IReadOnlyList<ConstraintEvaluation> EvaluateGeometryConstraints(ModelRecord record, ProjectConfig project)
        {
            float minVol = project.OptimizationTargets.TryGetValue("MinimumAllowedVolume", out float minV)
                ? minV
                : 2000f;
            float maxVol = project.OptimizationTargets.TryGetValue("MaximumAllowedVolume", out float maxV)
                ? maxV
                : 85000f;

            float volume = record.PassiveParameters.TryGetValue("Volume", out float v)
                ? v
                : float.NaN;

            return ConstraintEvaluation.InRange("Volume", volume, minVol, maxVol, isPreSolve: true);
        }
    }
}
