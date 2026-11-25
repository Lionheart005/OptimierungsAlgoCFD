using System;
using System.Collections.Generic;
using System.Numerics;
using FluidForge.Core;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Fixed geometric, interface, and packaging constants for <c>StatorCoolingRing</c>.
    /// </summary>
    public static class StatorCoolingRingConstants
    {
        public const int OutletCount = 12;

        public const float DefaultPitchCircleRadiusMm = 65.0f;
        public const float DefaultOutletRadiusMm = 2.0f;
        public const float OutletKneeZMm = 5.0f;
        public const float PortOverhangMm = 6.0f;

        public const float DefaultInletRadiusMm = 6.5f;
        public const float DefaultInletXMm = -108.0f;
        public const float DefaultInletZMm = 20.0f;
        public const float DefaultInletSplitXMm = -90.0f;
        public static readonly Vector3 FixedFluidSeedPoint = new(-100.0f, 0.0f, DefaultInletZMm);

        public static readonly float[] BoltAnglesDeg = { 60f, 120f, 240f, 300f };
        public const float DefaultBoltCircleRadiusMm = 84.0f;
        public const float DefaultBoltHoleRadiusMm = 2.75f;
        public const float DefaultBoltSeatHeightMm = 7.0f;
        public const float DefaultBoltCounterboreRadiusMm = 5.0f;
        public const float DefaultBoltDomeOuterRadiusMm = 8.2f;
        public const float DefaultBoltDomeHeightMm = 13.5f;
        public const float DefaultBoltKeepOutRadiusMm = 6.2f;
        public const float DefaultMinRequiredBoltClearanceMm = 2.0f;
        public const float DefaultMinRequiredObstacleClearanceMm = 2.0f;

        public const float DefaultFlangeInnerRadiusMm = 53.0f;
        public const float DefaultFlangeOuterRadiusMm = 76.8f;
        public const float DefaultFlangeHeightMm = 6.0f;
        public const float DefaultInnerORingRadiusMm = 56.8f;
        public const float DefaultOuterORingRadiusMm = 72.4f;
        public const float DefaultORingWidthMm = 3.2f;
        public const float DefaultORingDepthMm = 2.0f;
        public const float DefaultDowelPinRadiusMm = 2.0f;
        public const float DefaultDowelPinDepthMm = 6.0f;

        public const float DefaultWallThicknessMm = 2.5f;
        public const float DefaultMinInnerRadiusMm = 46.0f;
        public const float DefaultMaxOuterRadiusMm = 94.0f;
        public const float DefaultMaxTotalHeightMm = 54.0f;
        public const float DefaultMinBranchRadiusMm = 1.5f;
        public const float DefaultMaxFluidVolumeMm3 = 180000.0f;
        public const float DefaultMaxPrintableSolidVolumeMm3 = 360000.0f;
        public const float DefaultExportPrintableSolid = 1.0f;
        public const float DefaultExportContextEnvironment = 1.0f;

        public const float DegToRad = MathF.PI / 180.0f;
    }

    /// <summary>
    /// Cylindrical capsule representing a physical stator harness or sensor keep-out obstacle.
    /// </summary>
    public readonly record struct ObstacleCapsule(
        string Name,
        Vector3 Start,
        Vector3 End,
        float RadiusMm);

    /// <summary>
    /// Resolved fixed mechanical interface and packaging limits for <c>StatorCoolingRing</c>.
    /// </summary>
    public sealed record StatorCoolingRingTargets(
        float PitchCircleRadiusMm,
        float OutletRadiusMm,
        float InletRadiusMm,
        float BoltCircleRadiusMm,
        float BoltHoleRadiusMm,
        float BoltKeepOutRadiusMm,
        float MinRequiredBoltClearanceMm,
        float MinRequiredObstacleClearanceMm,
        float WallThicknessMm,
        float MinInnerRadiusMm,
        float MaxOuterRadiusMm,
        float MaxTotalHeightMm,
        float MinBranchRadiusMm,
        float MaxFluidVolumeMm3,
        float MaxPrintableSolidVolumeMm3,
        bool ExportPrintableSolid,
        bool ExportContextEnvironment)
    {
        public static StatorCoolingRingTargets FromProject(ProjectConfig? project)
        {
            IReadOnlyDictionary<string, float>? t = project?.OptimizationTargets;
            float minBoltClearance = ReadTarget(t, "MinRequiredBoltClearanceMm",
                ReadTarget(t, "MinBoltClearanceMm", StatorCoolingRingConstants.DefaultMinRequiredBoltClearanceMm));
            float minObstacleClearance = ReadTarget(t, "MinRequiredObstacleClearanceMm",
                ReadTarget(t, "MinObstacleClearanceMm", StatorCoolingRingConstants.DefaultMinRequiredObstacleClearanceMm));

            return new StatorCoolingRingTargets(
                PitchCircleRadiusMm: ReadPositiveTarget(t, "PitchCircleRadiusMm", StatorCoolingRingConstants.DefaultPitchCircleRadiusMm),
                OutletRadiusMm: ReadPositiveTarget(t, "OutletRadiusMm", StatorCoolingRingConstants.DefaultOutletRadiusMm),
                InletRadiusMm: ReadPositiveTarget(t, "InletRadiusMm", StatorCoolingRingConstants.DefaultInletRadiusMm),
                BoltCircleRadiusMm: ReadPositiveTarget(t, "BoltCircleRadiusMm", StatorCoolingRingConstants.DefaultBoltCircleRadiusMm),
                BoltHoleRadiusMm: ReadPositiveTarget(t, "BoltHoleRadiusMm", StatorCoolingRingConstants.DefaultBoltHoleRadiusMm),
                BoltKeepOutRadiusMm: ReadPositiveTarget(t, "BoltKeepOutRadiusMm", StatorCoolingRingConstants.DefaultBoltKeepOutRadiusMm),
                MinRequiredBoltClearanceMm: minBoltClearance,
                MinRequiredObstacleClearanceMm: minObstacleClearance,
                WallThicknessMm: ReadPositiveTarget(t, "WallThicknessMm", StatorCoolingRingConstants.DefaultWallThicknessMm),
                MinInnerRadiusMm: ReadPositiveTarget(t, "MinInnerRadiusMm", StatorCoolingRingConstants.DefaultMinInnerRadiusMm),
                MaxOuterRadiusMm: ReadPositiveTarget(t, "MaxOuterRadiusMm", StatorCoolingRingConstants.DefaultMaxOuterRadiusMm),
                MaxTotalHeightMm: ReadPositiveTarget(t, "MaxTotalHeightMm", StatorCoolingRingConstants.DefaultMaxTotalHeightMm),
                MinBranchRadiusMm: ReadPositiveTarget(t, "MinBranchRadiusMm", StatorCoolingRingConstants.DefaultMinBranchRadiusMm),
                MaxFluidVolumeMm3: ReadPositiveTarget(t, "MaxFluidVolumeMm3", StatorCoolingRingConstants.DefaultMaxFluidVolumeMm3),
                MaxPrintableSolidVolumeMm3: ReadPositiveTarget(t, "MaxPrintableSolidVolumeMm3", StatorCoolingRingConstants.DefaultMaxPrintableSolidVolumeMm3),
                ExportPrintableSolid: ReadTarget(t, "ExportPrintableSolid", StatorCoolingRingConstants.DefaultExportPrintableSolid) > 0.5f,
                ExportContextEnvironment: ReadTarget(t, "ExportContextEnvironment", StatorCoolingRingConstants.DefaultExportContextEnvironment) > 0.5f);
        }

        public ProjectConfig ToProjectConfig() => new()
        {
            OptimizationTargets = new Dictionary<string, float>
            {
                ["PitchCircleRadiusMm"] = PitchCircleRadiusMm,
                ["OutletRadiusMm"] = OutletRadiusMm,
                ["InletRadiusMm"] = InletRadiusMm,
                ["BoltCircleRadiusMm"] = BoltCircleRadiusMm,
                ["BoltHoleRadiusMm"] = BoltHoleRadiusMm,
                ["BoltKeepOutRadiusMm"] = BoltKeepOutRadiusMm,
                ["MinRequiredBoltClearanceMm"] = MinRequiredBoltClearanceMm,
                ["MinRequiredObstacleClearanceMm"] = MinRequiredObstacleClearanceMm,
                ["WallThicknessMm"] = WallThicknessMm,
                ["MinInnerRadiusMm"] = MinInnerRadiusMm,
                ["MaxOuterRadiusMm"] = MaxOuterRadiusMm,
                ["MaxTotalHeightMm"] = MaxTotalHeightMm,
                ["MinBranchRadiusMm"] = MinBranchRadiusMm,
                ["MaxFluidVolumeMm3"] = MaxFluidVolumeMm3,
                ["MaxPrintableSolidVolumeMm3"] = MaxPrintableSolidVolumeMm3,
                ["ExportPrintableSolid"] = ExportPrintableSolid ? 1.0f : 0.0f,
                ["ExportContextEnvironment"] = ExportContextEnvironment ? 1.0f : 0.0f
            }
        };

        private static float ReadTarget(IReadOnlyDictionary<string, float>? targets, string key, float fallback)
        {
            if (targets != null && targets.TryGetValue(key, out float value) && float.IsFinite(value))
                return value;
            return fallback;
        }

        private static float ReadPositiveTarget(IReadOnlyDictionary<string, float>? targets, string key, float fallback)
        {
            if (targets == null || !targets.TryGetValue(key, out float value))
                return fallback;
            if (!float.IsFinite(value) || value <= 0f)
            {
                throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} StatorCoolingRing OptimizationTargets.{key} must be finite and > 0 (got {value}).");
            }
            return value;
        }
    }

    /// <summary>
    /// Validated 14 continuous design parameters for the emergent vascular <c>StatorCoolingRing</c> generator.
    /// </summary>
    public readonly record struct StatorCoolingRingParams(
        float BundlingStrength,
        float BundlingRangeMm,
        float SplitReleaseBias,
        float MurrayExponent,
        float BaseChannelScale,
        float UpperLowerSplitBias,
        float FarSideLoopAttraction,
        float PathStiffness,
        float ElevationBaseMm,
        float ElevationTiltMm,
        float ObstacleOverpassBias,
        float CrossSectionAspect,
        float HydraulicBalanceGain,
        float ResidualThroatTilt)
    {
        public static readonly string[] ParameterNames =
        {
            "BundlingStrength",
            "BundlingRangeMm",
            "SplitReleaseBias",
            "MurrayExponent",
            "BaseChannelScale",
            "UpperLowerSplitBias",
            "FarSideLoopAttraction",
            "PathStiffness",
            "ElevationBaseMm",
            "ElevationTiltMm",
            "ObstacleOverpassBias",
            "CrossSectionAspect",
            "HydraulicBalanceGain",
            "ResidualThroatTilt"
        };

        public static StatorCoolingRingParams Read(IReadOnlyDictionary<string, float> parameters)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            return new StatorCoolingRingParams(
                BundlingStrength: ReadParam(parameters, "BundlingStrength"),
                BundlingRangeMm: ReadParam(parameters, "BundlingRangeMm"),
                SplitReleaseBias: ReadParam(parameters, "SplitReleaseBias"),
                MurrayExponent: ReadParam(parameters, "MurrayExponent"),
                BaseChannelScale: ReadParam(parameters, "BaseChannelScale"),
                UpperLowerSplitBias: ReadParam(parameters, "UpperLowerSplitBias"),
                FarSideLoopAttraction: ReadParam(parameters, "FarSideLoopAttraction"),
                PathStiffness: ReadParam(parameters, "PathStiffness"),
                ElevationBaseMm: ReadParam(parameters, "ElevationBaseMm"),
                ElevationTiltMm: ReadParam(parameters, "ElevationTiltMm"),
                ObstacleOverpassBias: ReadParam(parameters, "ObstacleOverpassBias"),
                CrossSectionAspect: ReadParam(parameters, "CrossSectionAspect"),
                HydraulicBalanceGain: ReadParam(parameters, "HydraulicBalanceGain"),
                ResidualThroatTilt: ReadParam(parameters, "ResidualThroatTilt"));
        }

        public static bool HasAllParameters(ModelRecord record, ProjectConfig project)
        {
            foreach (string name in ParameterNames)
            {
                bool inActive = record.ActiveParameters != null && record.ActiveParameters.ContainsKey(name);
                bool inBase = project.BaseParameters != null && project.BaseParameters.ContainsKey(name);
                if (!inActive && !inBase)
                    return false;
            }
            return true;
        }

        private static float ReadParam(IReadOnlyDictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value) && float.IsFinite(value))
                return value;

            throw new InvalidOperationException(
                $"[GEOMETRY] Missing or non-finite parameter '{name}' for StatorCoolingRing. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters and ParameterBounds in project.json.");
        }
    }

    /// <summary>
    /// Emergent vascular flow path connecting the shared inlet split to one of the 12 stator outlets.
    /// </summary>
    public readonly record struct VascularPath(
        int OutletIndex,
        float OutletAngleDeg,
        bool IsUpperSide,
        IReadOnlyList<Vector3> Points,
        IReadOnlyList<float> FluidRadii,
        IReadOnlyList<float> LocalMultiplicity,
        float PathLengthMm,
        float ResistanceIndex,
        float BalanceFactor);

    /// <summary>
    /// Collision-checked structural rib segment with hourglass radius profile.
    /// </summary>
    public readonly record struct RibSegment(
        Vector3 Start,
        Vector3 End,
        float StartRadiusMm,
        float MidRadiusMm,
        float EndRadiusMm);

    /// <summary>
    /// Complete 12-path emergent vascular network produced by <see cref="StatorCoolingRingVascularEngine"/>.
    /// </summary>
    public sealed record VascularNetwork(
        IReadOnlyList<VascularPath> Paths,
        int UpperCount,
        int LowerCount,
        float SplitAngleDeg,
        int UpperBoundaryPathIndex,
        int LowerBoundaryPathIndex,
        float MeanPairwiseCenterlineSpreadMm);

    /// <summary>
    /// Analytical packaging, bolt keep-out, HV-harness obstacle clearance, and anti-sliver summary.
    /// </summary>
    public readonly record struct StatorCoolingRingPackagingReport(
        float InnerRadiusMm,
        float OuterRadiusMm,
        float TotalHeightMm,
        float MinBoltClearanceMm,
        float MinObstacleClearanceMm,
        float MinBranchRadiusMm,
        float MinUnbundledGapMm,
        int SliverGapCount,
        bool IsFeasible);
}
