using System;
using System.Collections.Generic;
using FluidForge.Core;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Pre-CFD packaging, bolt-clearance, and volume constraints for <c>StatorCoolingRing</c>.
    /// Short-circuits meshing and OpenFOAM solving for variants violating physical stator envelope limits.
    /// </summary>
    public sealed class StatorCoolingRingGeometryConstraints : IGeometryConstraintEvaluator
    {
        public const float DefaultMinFluidVolumeMm3 = 1000.0f;

        /// <inheritdoc />
        public IReadOnlyList<ConstraintEvaluation> EvaluateGeometryConstraints(ModelRecord record, ProjectConfig project)
        {
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(project);

            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);

            float outerRadius = ReadMetric(record, "OuterRadiusMm");
            float totalHeight = ReadMetric(record, "TotalHeightMm");
            float minBoltClearance = ReadMetric(record, "MinBoltClearanceMm");
            float minBranchRadius = ReadMetric(record, "MinBranchRadiusMm");

            // Allow pure-analytical constraint evaluation in tests even when PicoGK metrics weren't populated.
            if ((!float.IsFinite(outerRadius) || !float.IsFinite(totalHeight) ||
                 !float.IsFinite(minBoltClearance) || !float.IsFinite(minBranchRadius)) &&
                HasAllActiveParams(record, project))
            {
                Dictionary<string, float> mergedParams = BuildMergedParameters(record, project);
                StatorCoolingRingPackagingReport report =
                    StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(mergedParams, project);

                if (!float.IsFinite(outerRadius)) outerRadius = report.OuterRadiusMm;
                if (!float.IsFinite(totalHeight)) totalHeight = report.TotalHeightMm;
                if (!float.IsFinite(minBoltClearance)) minBoltClearance = report.MinBoltClearanceMm;
                if (!float.IsFinite(minBranchRadius)) minBranchRadius = report.MinBranchRadiusMm;
            }

            float volume = ReadMetric(record, "Volume");
            float minFluidVolume = project.OptimizationTargets.TryGetValue("MinFluidVolumeMm3", out float mv) && float.IsFinite(mv)
                ? mv
                : DefaultMinFluidVolumeMm3;

            var constraints = new List<ConstraintEvaluation>(6)
            {
                ConstraintEvaluation.AtMost("OuterRadiusMm", outerRadius, targets.MaxOuterRadiusMm, isPreSolve: true),
                ConstraintEvaluation.AtMost("TotalHeightMm", totalHeight, targets.MaxTotalHeightMm, isPreSolve: true),
                ConstraintEvaluation.AtLeast("MinBoltClearanceMm", minBoltClearance, targets.MinRequiredBoltClearanceMm, isPreSolve: true),
                ConstraintEvaluation.AtLeast("MinBranchRadiusMm", minBranchRadius, targets.MinBranchRadiusMm, isPreSolve: true)
            };
            constraints.AddRange(ConstraintEvaluation.InRange(
                "Volume", volume, minFluidVolume, targets.MaxFluidVolumeMm3, isPreSolve: true));

            float solidVolume = ReadMetric(record, "PrintableSolidVolume");
            if (float.IsFinite(solidVolume) && solidVolume > 0f)
            {
                constraints.Add(ConstraintEvaluation.AtMost(
                    "PrintableSolidVolume", solidVolume, targets.MaxPrintableSolidVolumeMm3, isPreSolve: true));
            }

            return constraints;
        }

        private static float ReadMetric(ModelRecord record, string name)
        {
            return record.PassiveParameters != null && record.PassiveParameters.TryGetValue(name, out float value)
                ? value
                : float.NaN;
        }

        private static bool HasAllActiveParams(ModelRecord record, ProjectConfig project)
        {
            string[] names =
            {
                "RingRadiusRatio", "RingTaperRatio", "InletSplitBlend", "BranchHeightMm",
                "BranchArcSweepDeg", "FarSideElevationMm", "RingRadialShiftMm", "NearPortThroatRatio"
            };
            foreach (string name in names)
            {
                bool inActive = record.ActiveParameters != null && record.ActiveParameters.ContainsKey(name);
                bool inBase = project.BaseParameters != null && project.BaseParameters.ContainsKey(name);
                if (!inActive && !inBase) return false;
            }
            return true;
        }

        private static Dictionary<string, float> BuildMergedParameters(ModelRecord record, ProjectConfig project)
        {
            var merged = new Dictionary<string, float>(StringComparer.Ordinal);
            if (project.BaseParameters != null)
            {
                foreach (var kvp in project.BaseParameters)
                    merged[kvp.Key] = kvp.Value;
            }
            if (record.ActiveParameters != null)
            {
                foreach (var kvp in record.ActiveParameters)
                    merged[kvp.Key] = kvp.Value;
            }
            return merged;
        }
    }
}
