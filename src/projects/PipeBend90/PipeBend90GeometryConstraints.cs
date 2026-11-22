using System;
using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.PipeBend90
{
    /// <summary>
    /// Pre-CFD geometric constraints for <c>PipeBend90</c>:
    /// enforces fluid volume bounds and minimum bend-to-pipe radius ratio
    /// (<c>BendRadiusMm &gt;= 1.25 * PipeRadiusMm</c>) before meshing and solving.
    /// </summary>
    public sealed class PipeBend90GeometryConstraints : IGeometryConstraintEvaluator
    {
        public const float DefaultMinVolumeMm3 = 10000f;
        public const float DefaultMaxVolumeMm3 = 2000000f;
        public const float DefaultMinBendRadiusRatio = 1.25f;

        /// <inheritdoc />
        public IReadOnlyList<ConstraintEvaluation> EvaluateGeometryConstraints(ModelRecord record, ProjectConfig project)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(project);

            float minVol = project.OptimizationTargets.TryGetValue("MinimumAllowedVolume", out float minV)
                ? minV
                : DefaultMinVolumeMm3;
            float maxVol = project.OptimizationTargets.TryGetValue("MaximumAllowedVolume", out float maxV)
                ? maxV
                : DefaultMaxVolumeMm3;
            float minRatio = project.OptimizationTargets.TryGetValue("MinBendRadiusRatio", out float mr)
                ? mr
                : DefaultMinBendRadiusRatio;

            float volume = record.PassiveParameters.TryGetValue("Volume", out float vol)
                ? vol
                : float.NaN;

            float pipeRadius = ReadParam(record, project, "PipeRadiusMm");
            float bendRadius = ReadParam(record, project, "BendRadiusMm");
            float ratio = float.IsFinite(pipeRadius) && pipeRadius > 0f && float.IsFinite(bendRadius)
                ? bendRadius / pipeRadius
                : float.NaN;

            var constraints = new List<ConstraintEvaluation>(3);
            constraints.AddRange(ConstraintEvaluation.InRange("Volume", volume, minVol, maxVol, isPreSolve: true));
            constraints.Add(ConstraintEvaluation.AtLeast("BendRadiusToPipeRadius", ratio, minRatio, isPreSolve: true));
            return constraints;
        }

        private static float ReadParam(ModelRecord record, ProjectConfig project, string name)
        {
            if (record.ActiveParameters != null && record.ActiveParameters.TryGetValue(name, out float active))
                return active;
            if (project.BaseParameters != null && project.BaseParameters.TryGetValue(name, out float baseline))
                return baseline;
            return float.NaN;
        }
    }
}
