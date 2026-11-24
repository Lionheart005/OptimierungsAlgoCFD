using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk;
using FluidForge.Kernels.PicoGk.InternalFlow;
using Leap71.ShapeKernel;
using PicoGK;

namespace FluidForge.Projects.StatorCoolingRing
{
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
        float WallThicknessMm,
        float MaxOuterRadiusMm,
        float MaxTotalHeightMm,
        float MinBranchRadiusMm,
        float MaxFluidVolumeMm3,
        float MaxPrintableSolidVolumeMm3,
        bool ExportPrintableSolid)
    {
        public static StatorCoolingRingTargets FromProject(ProjectConfig? project)
        {
            IReadOnlyDictionary<string, float>? t = project?.OptimizationTargets;
            float minBoltClearance = ReadTarget(t, "MinRequiredBoltClearanceMm",
                ReadTarget(t, "MinBoltClearanceMm", StatorCoolingRingGeometryGenerator.DefaultMinRequiredBoltClearanceMm));

            return new StatorCoolingRingTargets(
                PitchCircleRadiusMm: ReadPositiveTarget(t, "PitchCircleRadiusMm", StatorCoolingRingGeometryGenerator.DefaultPitchCircleRadiusMm),
                OutletRadiusMm: ReadPositiveTarget(t, "OutletRadiusMm", StatorCoolingRingGeometryGenerator.DefaultOutletRadiusMm),
                InletRadiusMm: ReadPositiveTarget(t, "InletRadiusMm", StatorCoolingRingGeometryGenerator.DefaultInletRadiusMm),
                BoltCircleRadiusMm: ReadPositiveTarget(t, "BoltCircleRadiusMm", StatorCoolingRingGeometryGenerator.DefaultBoltCircleRadiusMm),
                BoltHoleRadiusMm: ReadPositiveTarget(t, "BoltHoleRadiusMm", StatorCoolingRingGeometryGenerator.DefaultBoltHoleRadiusMm),
                BoltKeepOutRadiusMm: ReadPositiveTarget(t, "BoltKeepOutRadiusMm", StatorCoolingRingGeometryGenerator.DefaultBoltKeepOutRadiusMm),
                MinRequiredBoltClearanceMm: minBoltClearance,
                WallThicknessMm: ReadPositiveTarget(t, "WallThicknessMm", StatorCoolingRingGeometryGenerator.DefaultWallThicknessMm),
                MaxOuterRadiusMm: ReadPositiveTarget(t, "MaxOuterRadiusMm", StatorCoolingRingGeometryGenerator.DefaultMaxOuterRadiusMm),
                MaxTotalHeightMm: ReadPositiveTarget(t, "MaxTotalHeightMm", StatorCoolingRingGeometryGenerator.DefaultMaxTotalHeightMm),
                MinBranchRadiusMm: ReadPositiveTarget(t, "MinBranchRadiusMm", StatorCoolingRingGeometryGenerator.DefaultMinBranchRadiusMm),
                MaxFluidVolumeMm3: ReadPositiveTarget(t, "MaxFluidVolumeMm3", StatorCoolingRingGeometryGenerator.DefaultMaxFluidVolumeMm3),
                MaxPrintableSolidVolumeMm3: ReadPositiveTarget(t, "MaxPrintableSolidVolumeMm3", StatorCoolingRingGeometryGenerator.DefaultMaxPrintableSolidVolumeMm3),
                ExportPrintableSolid: ReadTarget(t, "ExportPrintableSolid", StatorCoolingRingGeometryGenerator.DefaultExportPrintableSolid) > 0.5f);
        }

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
    /// Analytical packaging and bolt-clearance summary computed directly from design parameters.
    /// </summary>
    public readonly record struct StatorCoolingRingPackagingReport(
        float OuterRadiusMm,
        float TotalHeightMm,
        float MinBoltClearanceMm,
        float MinBranchRadiusMm,
        bool IsFeasible);

    /// <summary>
    /// Parametric 1-to-12 stator end-winding cooling ring generator built with LEAP 71 ShapeKernel
    /// and PicoGK, exporting both the multi-patch CFD fluid domain and an optional 3D-printable solid shell.
    /// </summary>
    public sealed class StatorCoolingRingGeometryGenerator : IGeometryGenerator
    {
        public const int OutletCount = 12;
        public const float DefaultPitchCircleRadiusMm = 65.0f;
        public const float DefaultOutletRadiusMm = 2.0f;
        public const float DefaultInletRadiusMm = 6.5f;
        public const float DefaultInletXMm = -105.0f;
        public const float DefaultInletSplitXMm = -92.0f;
        public const float DefaultBoltCircleRadiusMm = 80.0f;
        public const float DefaultBoltHoleRadiusMm = 2.75f;
        public const float DefaultBoltKeepOutRadiusMm = 6.5f;
        public const float DefaultBoltKeepOutHeightMm = 12.0f;
        public const float DefaultMinRequiredBoltClearanceMm = 2.0f;
        public const float DefaultWallThicknessMm = 2.5f;
        public const float DefaultBoltLugOuterRadiusMm = 8.5f;
        public const float DefaultBoltLugHeightMm = 7.0f;
        public const float DefaultFlangeHeightMm = 5.5f;
        public const float DefaultMaxOuterRadiusMm = 92.0f;
        public const float DefaultMaxTotalHeightMm = 48.0f;
        public const float DefaultMinBranchRadiusMm = 1.4f;
        public const float DefaultMaxFluidVolumeMm3 = 180000.0f;
        public const float DefaultMaxPrintableSolidVolumeMm3 = 220000.0f;
        public const float DefaultExportPrintableSolid = 1.0f;

        private const float PortOverhangMm = 6.0f;
        private const float OutletKneeZMm = 3.0f;
        private const float DegToRad = MathF.PI / 180.0f;

        private static readonly float[] BoltAnglesDeg = { 45f, 135f, 225f, 315f };

        private readonly StatorCoolingRingTargets _targets;

        /// <summary>
        /// Initializes the generator using <c>OptimizationTargets</c> from <paramref name="project"/>.
        /// </summary>
        public StatorCoolingRingGeometryGenerator(ProjectConfig? project = null)
        {
            _targets = StatorCoolingRingTargets.FromProject(project);
        }

        /// <summary>
        /// Returns the 12 outlet angular positions in degrees: <c>theta_k = k * 30 + 15</c>.
        /// </summary>
        public static IReadOnlyList<float> GetOutletAnglesDeg()
        {
            var angles = new float[OutletCount];
            for (int k = 0; k < OutletCount; k++)
            {
                angles[k] = k * 30.0f + 15.0f;
            }
            return angles;
        }

        /// <summary>
        /// Evaluates analytical packaging dimensions and 3D M5 bolt keep-out clearances
        /// without requiring a PicoGK runtime.
        /// <para>
        /// <c>OuterRadiusMm</c> measures the annular stator manifold envelope (collector ring,
        /// 12 outlet branches, and 4 M5 bolt lugs) excluding the radial side-inlet stub at <c>X = -105 mm</c>.
        /// </para>
        /// </summary>
        public static StatorCoolingRingPackagingReport EvaluateAnalyticalPackaging(
            IReadOnlyDictionary<string, float> parameters,
            ProjectConfig? project = null)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            ActiveParams p = ActiveParams.Read(parameters);

            float ringRadiusMm = targets.PitchCircleRadiusMm + p.RingRadialShiftMm;
            float baseRingFluidRadiusMm = targets.InletRadiusMm * p.RingRadiusRatio;
            float wall = targets.WallThicknessMm;

            float maxOuterRadius = targets.BoltCircleRadiusMm + DefaultBoltLugOuterRadiusMm;
            float maxTotalHeight = 0f;
            float minBoltClearance = float.MaxValue;
            float minBranchRadius = float.MaxValue;

            // 1. Collector ring envelope (360 samples around full 2*pi)
            const int RingSamples = 360;
            for (int i = 0; i < RingSamples; i++)
            {
                float thetaRad = (2f * MathF.PI * i) / RingSamples;
                Vector3 center = EvaluateRingCenter(thetaRad, ringRadiusMm, p.BranchHeightMm, p.FarSideElevationMm);
                float rFluid = EvaluateRingFluidRadius(thetaRad, baseRingFluidRadiusMm, p.RingTaperRatio);
                float rOuterShell = rFluid + wall;

                float radialXY = MathF.Sqrt(center.X * center.X + center.Y * center.Y) + rOuterShell;
                maxOuterRadius = MathF.Max(maxOuterRadius, radialXY);
                maxTotalHeight = MathF.Max(maxTotalHeight, center.Z + rOuterShell);
                minBranchRadius = MathF.Min(minBranchRadius, rFluid);
                minBoltClearance = MathF.Min(minBoltClearance, ComputeBoltClearance(center, rOuterShell, targets));
            }

            // 2. Two symmetric Y-bifurcation arms from X = -92 mm into the collector ring
            float splitHalfAngleRad = EvaluateSplitHalfAngleDeg(p.InletSplitBlend) * DegToRad;
            foreach (float sideSign in new[] { -1f, 1f })
            {
                float thetaJoinRad = MathF.PI + sideSign * splitHalfAngleRad;
                List<Vector3> yPoints = SampleYBifurcationCurve(
                    thetaJoinRad, sideSign, ringRadiusMm, p.BranchHeightMm, p.FarSideElevationMm, samples: 40);
                float rJoin = EvaluateRingFluidRadius(thetaJoinRad, baseRingFluidRadiusMm, p.RingTaperRatio);

                for (int i = 0; i < yPoints.Count; i++)
                {
                    float u = i / (float)(yPoints.Count - 1);
                    float blend = SmoothStep(u);
                    float rFluid = targets.InletRadiusMm + (rJoin - targets.InletRadiusMm) * blend;
                    float rOuterShell = rFluid + wall;

                    maxTotalHeight = MathF.Max(maxTotalHeight, yPoints[i].Z + rOuterShell);
                    minBoltClearance = MathF.Min(minBoltClearance, ComputeBoltClearance(yPoints[i], rOuterShell, targets));
                }
            }

            // 3. Twelve swept outlet arms from collector ring to Z = 0
            for (int k = 0; k < OutletCount; k++)
            {
                ArmDefinition arm = BuildArmDefinition(k, p, targets, ringRadiusMm, baseRingFluidRadiusMm);
                minBranchRadius = MathF.Min(minBranchRadius, MathF.Min(arm.ThroatRadiusMm, targets.OutletRadiusMm));

                for (int i = 0; i < arm.CurvedPoints.Count; i++)
                {
                    float u = i / (float)(arm.CurvedPoints.Count - 1);
                    float rFluid = EvaluateArmRadiusOnCurve(u, arm.StartRadiusMm, arm.ThroatRadiusMm, targets.OutletRadiusMm);
                    float rOuterShell = rFluid + wall;
                    Vector3 pt = arm.CurvedPoints[i];

                    float radialXY = MathF.Sqrt(pt.X * pt.X + pt.Y * pt.Y) + rOuterShell;
                    maxOuterRadius = MathF.Max(maxOuterRadius, radialXY);
                    maxTotalHeight = MathF.Max(maxTotalHeight, pt.Z + rOuterShell);
                    minBranchRadius = MathF.Min(minBranchRadius, rFluid);
                    minBoltClearance = MathF.Min(minBoltClearance, ComputeBoltClearance(pt, rOuterShell, targets));
                }

                // Vertical exit nozzle section [0, OutletKneeZMm]
                float exitOuterShell = targets.OutletRadiusMm + wall;
                Vector3 exitCenter = new(
                    targets.PitchCircleRadiusMm * MathF.Cos(arm.OutletThetaRad),
                    targets.PitchCircleRadiusMm * MathF.Sin(arm.OutletThetaRad),
                    0.5f * OutletKneeZMm);
                minBoltClearance = MathF.Min(minBoltClearance, ComputeBoltClearance(exitCenter, exitOuterShell, targets));
            }

            bool feasible =
                float.IsFinite(maxOuterRadius) && maxOuterRadius <= targets.MaxOuterRadiusMm &&
                float.IsFinite(maxTotalHeight) && maxTotalHeight <= targets.MaxTotalHeightMm &&
                float.IsFinite(minBoltClearance) && minBoltClearance >= targets.MinRequiredBoltClearanceMm &&
                float.IsFinite(minBranchRadius) && minBranchRadius >= targets.MinBranchRadiusMm;

            return new StatorCoolingRingPackagingReport(
                OuterRadiusMm: maxOuterRadius,
                TotalHeightMm: maxTotalHeight,
                MinBoltClearanceMm: minBoltClearance,
                MinBranchRadiusMm: minBranchRadius,
                IsFeasible: feasible);
        }

        /// <inheritdoc />
        public GeometryResult GenerateAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
            ArgumentNullException.ThrowIfNull(config);

            Directory.CreateDirectory(outputDirectory);
            ActiveParams p = ActiveParams.Read(parameters);

            StatorCoolingRingPackagingReport packaging = EvaluateAnalyticalPackaging(parameters, BuildConfigForTargets(_targets));

            float ringRadiusMm = _targets.PitchCircleRadiusMm + p.RingRadialShiftMm;
            float baseRingFluidRadiusMm = _targets.InletRadiusMm * p.RingRadiusRatio;
            string baseName = $"Model_Gen{iteration}_Var{variant}";

            using Voxels rawFluidVoxels = BuildFluidVolumeVoxels(p, ringRadiusMm, baseRingFluidRadiusMm);
            using Voxels trimmedFluid = TrimPortOverhangsCompact(rawFluidVoxels, p.BranchHeightMm);
            IReadOnlyList<FlowPort> ports = BuildFlowPorts(p.BranchHeightMm);
            Vector3 fluidSeedPoint = new(-96.0f, 0.0f, p.BranchHeightMm);

            InternalFlowExportResult exported = MultiPatchFluidExporter.Export(
                trimmedFluid,
                ports,
                fluidSeedPoint,
                outputDirectory,
                baseName,
                trimPorts: false,
                applySmoothing: false);

            float printableSolidVolume = 0f;
            if (packaging.IsFeasible && _targets.ExportPrintableSolid)
            {
                string printablePath = Path.Combine(outputDirectory, $"{baseName}_printable.stl");
                printableSolidVolume = BuildAndExportPrintableSolid(
                    rawFluidVoxels,
                    trimmedFluid,
                    p.BranchHeightMm,
                    printablePath);
            }

            GeometryResult result = exported.ToGeometryResult();
            result.AddMetric("OuterRadiusMm", packaging.OuterRadiusMm, MetricScaling.Linear);
            result.AddMetric("TotalHeightMm", packaging.TotalHeightMm, MetricScaling.Linear);
            result.AddMetric("MinBoltClearanceMm", packaging.MinBoltClearanceMm, MetricScaling.Linear);
            result.AddMetric("MinBranchRadiusMm", packaging.MinBranchRadiusMm, MetricScaling.Linear);
            result.AddMetric("PrintableSolidVolume", printableSolidVolume, MetricScaling.Volume);
            return result;
        }

        private Voxels BuildFluidVolumeVoxels(ActiveParams p, float ringRadiusMm, float baseRingFluidRadiusMm)
        {
            // 1. Closed 3D collector ring built from two overlapping 190-degree half-arcs (seamless union)
            using Voxels upperHalf = BuildRingArcVoxels(
                startDeg: -5f, endDeg: 185f, ringRadiusMm, baseRingFluidRadiusMm, p);
            using Voxels lowerHalf = BuildRingArcVoxels(
                startDeg: 175f, endDeg: 365f, ringRadiusMm, baseRingFluidRadiusMm, p);
            Voxels assembled = upperHalf.voxBoolAdd(lowerHalf);

            try
            {
                // 2. Straight radial inlet stub (with overhang past X = -105 for clean planar trimming)
                float inletStartX = DefaultInletXMm - PortOverhangMm;
                float inletLen = (DefaultInletSplitXMm + 1.0f) - inletStartX;
                LocalFrame inletFrame = new(new Vector3(inletStartX, 0f, p.BranchHeightMm), Vector3.UnitX, Vector3.UnitZ);
                BaseCylinder inletPipe = new(inletFrame, inletLen, _targets.InletRadiusMm);
                inletPipe.SetPolarSteps(96);
                inletPipe.SetLengthSteps(32);
                using Voxels inletVox = inletPipe.voxConstruct();
                ReplaceWithUnion(ref assembled, inletVox);

                // 3. Two symmetric TangentialControlSpline Y-bifurcation arms into the ring
                float splitHalfAngleRad = EvaluateSplitHalfAngleDeg(p.InletSplitBlend) * DegToRad;
                foreach (float sideSign in new[] { -1f, 1f })
                {
                    float thetaJoinRad = MathF.PI + sideSign * splitHalfAngleRad;
                    List<Vector3> yPoints = SampleYBifurcationCurve(
                        thetaJoinRad, sideSign, ringRadiusMm, p.BranchHeightMm, p.FarSideElevationMm, samples: 80);

                    float rJoin = EvaluateRingFluidRadius(thetaJoinRad, baseRingFluidRadiusMm, p.RingTaperRatio);
                    var yRadii = new float[yPoints.Count];
                    for (int i = 0; i < yPoints.Count; i++)
                    {
                        float u = i / (float)(yPoints.Count - 1);
                        yRadii[i] = _targets.InletRadiusMm + (rJoin - _targets.InletRadiusMm) * SmoothStep(u);
                    }

                    Frames yFrames = new(yPoints, Frames.EFrameType.Z, bEnsureFrameContinuity: true, fReparametrisationSpacing: 0.4f);
                    LineModulation yRadMod = BuildArcLengthMappedModulation(yPoints, yRadii);
                    BaseCylinder yArm = new(yFrames, _targets.InletRadiusMm);
                    yArm.SetPolarSteps(84);
                    yArm.SetRadius(new SurfaceModulation(yRadMod));
                    yArm.SetLengthSteps((uint)Math.Max(80, yPoints.Count * 2));

                    using Voxels yArmVox = yArm.voxConstruct();
                    ReplaceWithUnion(ref assembled, yArmVox);
                }

                // 4. Twelve swept ShapeKernel outlet arms terminating vertically past Z = 0
                for (int k = 0; k < OutletCount; k++)
                {
                    using Voxels armVox = BuildOutletArmVoxels(k, p, ringRadiusMm, baseRingFluidRadiusMm);
                    ReplaceWithUnion(ref assembled, armVox);
                }

                // Subtle junction filleting before planar port trimming
                Voxels filleted = assembled.voxSmoothen(0.35f);
                assembled.Dispose();
                assembled = filleted;
                return assembled;
            }
            catch
            {
                assembled.Dispose();
                throw;
            }
        }

        private Voxels BuildRingArcVoxels(
            float startDeg,
            float endDeg,
            float ringRadiusMm,
            float baseRingFluidRadiusMm,
            ActiveParams p)
        {
            const int ArcSamples = 190;
            var points = new List<Vector3>(ArcSamples);
            var radii = new float[ArcSamples];

            for (int i = 0; i < ArcSamples; i++)
            {
                float u = i / (float)(ArcSamples - 1);
                float thetaRad = (startDeg + u * (endDeg - startDeg)) * DegToRad;
                points.Add(EvaluateRingCenter(thetaRad, ringRadiusMm, p.BranchHeightMm, p.FarSideElevationMm));
                radii[i] = EvaluateRingFluidRadius(thetaRad, baseRingFluidRadiusMm, p.RingTaperRatio);
            }

            Frames frames = new(points, Frames.EFrameType.Z, bEnsureFrameContinuity: true, fReparametrisationSpacing: 0.5f);
            LineModulation radiusMod = BuildArcLengthMappedModulation(points, radii);

            BaseCylinder arc = new(frames, baseRingFluidRadiusMm);
            arc.SetPolarSteps(96);
            arc.SetRadius(new SurfaceModulation(radiusMod));
            arc.SetLengthSteps((uint)Math.Max(200, ArcSamples * 2));
            return arc.voxConstruct();
        }

        private Voxels BuildOutletArmVoxels(int index, ActiveParams p, float ringRadiusMm, float baseRingFluidRadiusMm)
        {
            ArmDefinition arm = BuildArmDefinition(index, p, _targets, ringRadiusMm, baseRingFluidRadiusMm);

            var allPoints = new List<Vector3>(arm.CurvedPoints.Count + 12);
            var allRadii = new List<float>(arm.CurvedPoints.Count + 12);

            for (int i = 0; i < arm.CurvedPoints.Count; i++)
            {
                float u = i / (float)(arm.CurvedPoints.Count - 1);
                allPoints.Add(arm.CurvedPoints[i]);
                allRadii.Add(EvaluateArmRadiusOnCurve(u, arm.StartRadiusMm, arm.ThroatRadiusMm, _targets.OutletRadiusMm));
            }

            // Straight vertical nozzle exit section from Z = OutletKneeZMm down past Z = 0 for planar trimming
            float endX = _targets.PitchCircleRadiusMm * MathF.Cos(arm.OutletThetaRad);
            float endY = _targets.PitchCircleRadiusMm * MathF.Sin(arm.OutletThetaRad);
            const int StraightSteps = 12;
            float zEnd = -PortOverhangMm;
            for (int s = 1; s <= StraightSteps; s++)
            {
                float frac = s / (float)StraightSteps;
                float z = OutletKneeZMm + frac * (zEnd - OutletKneeZMm);
                allPoints.Add(new Vector3(endX, endY, z));
                allRadii.Add(_targets.OutletRadiusMm);
            }

            Frames armFrames = new(allPoints, Frames.EFrameType.CYLINDRICAL, bEnsureFrameContinuity: true, fReparametrisationSpacing: 0.35f);
            LineModulation armMod = BuildArcLengthMappedModulation(allPoints, allRadii);

            BaseCylinder armCyl = new(armFrames, _targets.OutletRadiusMm);
            armCyl.SetPolarSteps(72);
            armCyl.SetRadius(new SurfaceModulation(armMod));
            armCyl.SetLengthSteps((uint)Math.Max(80, allPoints.Count * 2));
            return armCyl.voxConstruct();
        }

        private static Voxels TrimPortOverhangsCompact(Voxels fluidVoxels, float branchHeightMm)
        {
            BaseBox zOutletCutter = new(new LocalFrame(Vector3.Zero, -Vector3.UnitZ), 14.0f, 200.0f, 200.0f);
            using Voxels zCutterVox = zOutletCutter.voxConstruct();
            using Voxels zTrimmed = fluidVoxels.voxBoolSubtract(zCutterVox);

            BaseBox xInletCutter = new(
                new LocalFrame(new Vector3(DefaultInletXMm, 0f, branchHeightMm), -Vector3.UnitX),
                18.0f, 40.0f, 40.0f);
            using Voxels xCutterVox = xInletCutter.voxConstruct();
            return zTrimmed.voxBoolSubtract(xCutterVox);
        }

        private float BuildAndExportPrintableSolid(
            Voxels rawFluidVoxels,
            Voxels trimmedFluidVoxels,
            float branchHeightMm,
            string outputStlPath)
        {
            using Voxels offsetShell = trimmedFluidVoxels.voxOffset(_targets.WallThicknessMm);
            Voxels solid = offsetShell.voxSmoothen(0.8f);

            try
            {
                // Base mounting flange ring around the 12 outlets (Z in [0, 5.5 mm])
                float innerFlangeR = MathF.Max(10f, _targets.PitchCircleRadiusMm - 8.5f);
                float outerFlangeR = _targets.PitchCircleRadiusMm + 8.5f;
                BasePipe flangeRing = new(
                    new LocalFrame(Vector3.Zero, Vector3.UnitZ),
                    DefaultFlangeHeightMm,
                    innerFlangeR,
                    outerFlangeR);
                flangeRing.SetPolarSteps(180);
                using Voxels flangeVox = flangeRing.voxConstruct();
                ReplaceWithUnion(ref solid, flangeVox);

                // 4 reinforced M5 bolt mounting lugs at R = 80 mm + radial bridges to flange ring
                foreach (float deg in BoltAnglesDeg)
                {
                    float rad = deg * DegToRad;
                    Vector3 dir = new(MathF.Cos(rad), MathF.Sin(rad), 0f);
                    Vector3 lugPos = dir * _targets.BoltCircleRadiusMm;

                    BaseCylinder lug = new(
                        new LocalFrame(lugPos, Vector3.UnitZ),
                        DefaultBoltLugHeightMm,
                        DefaultBoltLugOuterRadiusMm);
                    lug.SetPolarSteps(72);
                    using Voxels lugVox = lug.voxConstruct();
                    ReplaceWithUnion(ref solid, lugVox);

                    Vector3 bridgeStart = dir * (_targets.PitchCircleRadiusMm + 2.0f);
                    float bridgeLen = _targets.BoltCircleRadiusMm - (_targets.PitchCircleRadiusMm + 2.0f);
                    LocalFrame bridgeFrame = new(
                        new Vector3(bridgeStart.X, bridgeStart.Y, 0.5f * DefaultFlangeHeightMm),
                        dir,
                        Vector3.UnitZ);
                    BaseBox bridge = new(bridgeFrame, bridgeLen, DefaultFlangeHeightMm, 14.0f);
                    using Voxels bridgeVox = bridge.voxConstruct();
                    ReplaceWithUnion(ref solid, bridgeVox);
                }

                // Reinforced inlet hose collar (X in [-105, -88 mm])
                BaseCylinder inletCollar = new(
                    new LocalFrame(new Vector3(DefaultInletXMm, 0f, branchHeightMm), Vector3.UnitX, Vector3.UnitZ),
                    17.0f,
                    _targets.InletRadiusMm + _targets.WallThicknessMm);
                inletCollar.SetPolarSteps(84);
                using Voxels collarVox = inletCollar.voxConstruct();
                ReplaceWithUnion(ref solid, collarVox);

                // Organic fillet pass across structural junctions
                Voxels blended = solid.voxSmoothen(0.5f);
                solid.Dispose();
                solid = blended;

                // Subtract internal fluid channel (including port overhangs so all 13 ports open cleanly)
                ReplaceWithDifference(ref solid, rawFluidVoxels);

                // Subtract 4 M5 through-bolt holes (R = 2.75 mm)
                foreach (float deg in BoltAnglesDeg)
                {
                    float rad = deg * DegToRad;
                    Vector3 holePos = new(
                        _targets.BoltCircleRadiusMm * MathF.Cos(rad),
                        _targets.BoltCircleRadiusMm * MathF.Sin(rad),
                        -4.0f);
                    BaseCylinder hole = new(new LocalFrame(holePos, Vector3.UnitZ), 24.0f, _targets.BoltHoleRadiusMm);
                    hole.SetPolarSteps(64);
                    using Voxels holeVox = hole.voxConstruct();
                    ReplaceWithDifference(ref solid, holeVox);
                }

                // Planar bottom mounting face trim (Z < 0) and inlet face trim (X < -105)
                BaseBox zBottomCutter = new(new LocalFrame(Vector3.Zero, -Vector3.UnitZ), 14.0f, 200.0f, 200.0f);
                using Voxels zCutterVox = zBottomCutter.voxConstruct();
                ReplaceWithDifference(ref solid, zCutterVox);

                BaseBox xInletCutter = new(
                    new LocalFrame(new Vector3(DefaultInletXMm, 0f, branchHeightMm), -Vector3.UnitX),
                    18.0f, 40.0f, 40.0f);
                using Voxels xCutterVox = xInletCutter.voxConstruct();
                ReplaceWithDifference(ref solid, xCutterVox);

                using Mesh solidMesh = new(solid);
                IReadOnlyList<StlTriangle> rawTris = PicoGkMeshMetrics.ToTriangles(solidMesh);
                List<StlTriangle> cleanTris = rawTris
                    .Where(t => t.A != t.B && t.B != t.C && t.C != t.A)
                    .ToList();

                if (cleanTris.Count == 0)
                    return 0f;

                StlWriter.WriteBinary(outputStlPath, cleanTris);
                return MeshMetrics.Measure(cleanTris).Volume;
            }
            finally
            {
                solid.Dispose();
            }
        }

        private IReadOnlyList<FlowPort> BuildFlowPorts(float branchHeightMm)
        {
            var ports = new List<FlowPort>(OutletCount + 1)
            {
                new("inlet", new Vector3(DefaultInletXMm, 0f, branchHeightMm), -Vector3.UnitX, _targets.InletRadiusMm)
            };

            IReadOnlyList<float> anglesDeg = GetOutletAnglesDeg();
            for (int k = 0; k < OutletCount; k++)
            {
                float rad = anglesDeg[k] * DegToRad;
                Vector3 center = new(
                    _targets.PitchCircleRadiusMm * MathF.Cos(rad),
                    _targets.PitchCircleRadiusMm * MathF.Sin(rad),
                    0f);
                ports.Add(new FlowPort($"outlet_{k + 1:D2}", center, -Vector3.UnitZ, _targets.OutletRadiusMm));
            }

            return ports;
        }

        private static ArmDefinition BuildArmDefinition(
            int index,
            ActiveParams p,
            StatorCoolingRingTargets targets,
            float ringRadiusMm,
            float baseRingFluidRadiusMm)
        {
            float outletDeg = index * 30.0f + 15.0f;
            float outletRad = outletDeg * DegToRad;

            // Mirror-symmetric flow direction sign around XZ plane:
            // Upper half (0 < theta < 180, sin > 0): flow runs from 180 -> 0 (decreasing theta, sign = -1).
            // Lower half (180 < theta < 360, sin < 0): flow runs from 180 -> 360 (increasing theta, sign = +1).
            float flowSign = MathF.Sin(outletRad) >= 0f ? -1.0f : 1.0f;

            float startDeg = outletDeg - flowSign * p.BranchArcSweepDeg;
            float startRad = startDeg * DegToRad;

            Vector3 pStart = EvaluateRingCenter(startRad, ringRadiusMm, p.BranchHeightMm, p.FarSideElevationMm);
            Vector3 pKnee = new(
                targets.PitchCircleRadiusMm * MathF.Cos(outletRad),
                targets.PitchCircleRadiusMm * MathF.Sin(outletRad),
                OutletKneeZMm);

            Vector3 flowTangent = EvaluateRingFlowTangent(startRad, flowSign, ringRadiusMm, p.FarSideElevationMm);
            float sweepAbs = MathF.Abs(p.BranchArcSweepDeg);
            float sweepWeight = MathF.Min(1.0f, sweepAbs / 10.0f);
            float sweepSign = p.BranchArcSweepDeg >= 0f ? 1.0f : -1.0f;

            Vector3 startDir;
            if (sweepAbs < 1e-4f && MathF.Abs(p.RingRadialShiftMm) < 1e-4f)
            {
                startDir = -Vector3.UnitZ;
            }
            else
            {
                Vector3 tangentialComponent = sweepSign * flowTangent - 0.35f * Vector3.UnitZ;
                startDir = Vector3.Normalize((1.0f - sweepWeight) * (-Vector3.UnitZ) + sweepWeight * tangentialComponent);
            }

            float chord = (pKnee - pStart).Length();
            float tangentLen = 0.36f * chord;
            var spline = new TangentialControlSpline(
                pStart,
                pKnee,
                startDir,
                -Vector3.UnitZ,
                fStartTangentStrength: tangentLen,
                fEndTangentStrenth: tangentLen,
                bRelativeStartStrength: false,
                bRelativeEndStrength: false);

            List<Vector3> curvedPoints = spline.aGetPoints(40);

            float rRingAtStart = EvaluateRingFluidRadius(startRad, baseRingFluidRadiusMm, p.RingTaperRatio);
            float nearWeight = 0.5f * (1.0f - MathF.Cos(outletRad));
            float throatRadius = targets.OutletRadiusMm * (1.0f + (p.NearPortThroatRatio - 1.0f) * nearWeight);

            return new ArmDefinition(outletRad, rRingAtStart, throatRadius, curvedPoints);
        }

        private static List<Vector3> SampleYBifurcationCurve(
            float thetaJoinRad,
            float sideSign,
            float ringRadiusMm,
            float branchHeightMm,
            float farSideElevationMm,
            uint samples)
        {
            Vector3 start = new(DefaultInletSplitXMm, 0f, branchHeightMm);
            Vector3 end = EvaluateRingCenter(thetaJoinRad, ringRadiusMm, branchHeightMm, farSideElevationMm);

            // Flow tangent from theta = pi toward far side (sideSign = -1 -> decreasing theta; +1 -> increasing theta)
            Vector3 endDir = EvaluateRingFlowTangent(thetaJoinRad, sideSign, ringRadiusMm, farSideElevationMm);
            float chord = (end - start).Length();

            var spline = new TangentialControlSpline(
                start,
                end,
                Vector3.UnitX,
                endDir,
                fStartTangentStrength: 0.38f * chord,
                fEndTangentStrenth: 0.38f * chord,
                bRelativeStartStrength: false,
                bRelativeEndStrength: false);

            return spline.aGetPoints(samples);
        }

        private static Vector3 EvaluateRingCenter(
            float thetaRad,
            float ringRadiusMm,
            float branchHeightMm,
            float farSideElevationMm)
        {
            float z = branchHeightMm + farSideElevationMm * 0.5f * (1.0f + MathF.Cos(thetaRad));
            return new Vector3(
                ringRadiusMm * MathF.Cos(thetaRad),
                ringRadiusMm * MathF.Sin(thetaRad),
                z);
        }

        private static Vector3 EvaluateRingFlowTangent(
            float thetaRad,
            float flowSign,
            float ringRadiusMm,
            float farSideElevationMm)
        {
            float dxDtheta = -ringRadiusMm * MathF.Sin(thetaRad);
            float dyDtheta = ringRadiusMm * MathF.Cos(thetaRad);
            float dzDtheta = -0.5f * farSideElevationMm * MathF.Sin(thetaRad);
            return Vector3.Normalize(flowSign * new Vector3(dxDtheta, dyDtheta, dzDtheta));
        }

        private static float EvaluateRingFluidRadius(float thetaRad, float baseRingFluidRadiusMm, float ringTaperRatio)
        {
            return baseRingFluidRadiusMm * (1.0f + (ringTaperRatio - 1.0f) * 0.5f * (1.0f + MathF.Cos(thetaRad)));
        }

        private static float EvaluateSplitHalfAngleDeg(float inletSplitBlend)
        {
            return 12.0f + 30.0f * Math.Clamp(inletSplitBlend, 0.0f, 1.0f);
        }

        private static float EvaluateArmRadiusOnCurve(float u, float startRadius, float throatRadius, float outletRadius)
        {
            const float ThroatPos = 0.45f;
            if (u <= ThroatPos)
            {
                float t = SmoothStep(u / ThroatPos);
                return startRadius + (throatRadius - startRadius) * t;
            }
            else
            {
                float t = SmoothStep((u - ThroatPos) / (1.0f - ThroatPos));
                return throatRadius + (outletRadius - throatRadius) * t;
            }
        }

        private static float ComputeBoltClearance(Vector3 center, float outerShellRadius, StatorCoolingRingTargets targets)
        {
            float minClearance = float.MaxValue;
            foreach (float deg in BoltAnglesDeg)
            {
                float rad = deg * DegToRad;
                float bx = targets.BoltCircleRadiusMm * MathF.Cos(rad);
                float by = targets.BoltCircleRadiusMm * MathF.Sin(rad);

                float dx = center.X - bx;
                float dy = center.Y - by;
                float dxy = MathF.Sqrt(dx * dx + dy * dy);

                float qr = dxy - targets.BoltKeepOutRadiusMm;
                float qz = MathF.Max(-center.Z, center.Z - DefaultBoltKeepOutHeightMm);

                float sdf = MathF.Sqrt(MathF.Max(qr, 0f) * MathF.Max(qr, 0f) + MathF.Max(qz, 0f) * MathF.Max(qz, 0f))
                          + MathF.Min(MathF.Max(qr, qz), 0f);

                float clearance = sdf - outerShellRadius;
                if (clearance < minClearance)
                    minClearance = clearance;
            }
            return minClearance;
        }

        private static LineModulation BuildArcLengthMappedModulation(
            IReadOnlyList<Vector3> points,
            IReadOnlyList<float> valuesAtPoints)
        {
            var ratios = new float[points.Count];
            var values = new float[points.Count];
            double totalLen = 0.0;
            ratios[0] = 0f;
            values[0] = valuesAtPoints[0];

            for (int i = 1; i < points.Count; i++)
            {
                totalLen += (points[i] - points[i - 1]).Length();
                ratios[i] = (float)totalLen;
                values[i] = valuesAtPoints[i];
            }

            if (totalLen > 1e-6)
            {
                for (int i = 1; i < ratios.Length; i++)
                    ratios[i] /= (float)totalLen;
            }

            return new LineModulation(fRatio =>
            {
                float r = Math.Clamp(fRatio, 0f, 1f);
                int idx = Array.BinarySearch(ratios, r);
                if (idx >= 0) return values[idx];
                int upper = ~idx;
                if (upper <= 0) return values[0];
                if (upper >= ratios.Length) return values[^1];
                int lower = upper - 1;
                float span = ratios[upper] - ratios[lower];
                if (span <= 1e-6f) return values[lower];
                float t = (r - ratios[lower]) / span;
                return values[lower] + t * (values[upper] - values[lower]);
            });
        }

        private static float SmoothStep(float t)
        {
            float x = Math.Clamp(t, 0f, 1f);
            return x * x * (3f - 2f * x);
        }

        private static void ReplaceWithUnion(ref Voxels target, Voxels add)
        {
            Voxels next = target.voxBoolAdd(add);
            target.Dispose();
            target = next;
        }

        private static void ReplaceWithDifference(ref Voxels target, Voxels subtract)
        {
            Voxels next = target.voxBoolSubtract(subtract);
            target.Dispose();
            target = next;
        }

        private static ProjectConfig BuildConfigForTargets(StatorCoolingRingTargets targets) => new()
        {
            OptimizationTargets = new Dictionary<string, float>
            {
                ["PitchCircleRadiusMm"] = targets.PitchCircleRadiusMm,
                ["OutletRadiusMm"] = targets.OutletRadiusMm,
                ["InletRadiusMm"] = targets.InletRadiusMm,
                ["BoltCircleRadiusMm"] = targets.BoltCircleRadiusMm,
                ["BoltHoleRadiusMm"] = targets.BoltHoleRadiusMm,
                ["BoltKeepOutRadiusMm"] = targets.BoltKeepOutRadiusMm,
                ["MinRequiredBoltClearanceMm"] = targets.MinRequiredBoltClearanceMm,
                ["WallThicknessMm"] = targets.WallThicknessMm,
                ["MaxOuterRadiusMm"] = targets.MaxOuterRadiusMm,
                ["MaxTotalHeightMm"] = targets.MaxTotalHeightMm,
                ["MinBranchRadiusMm"] = targets.MinBranchRadiusMm,
                ["MaxFluidVolumeMm3"] = targets.MaxFluidVolumeMm3,
                ["MaxPrintableSolidVolumeMm3"] = targets.MaxPrintableSolidVolumeMm3,
                ["ExportPrintableSolid"] = targets.ExportPrintableSolid ? 1.0f : 0.0f
            }
        };

        private readonly record struct ArmDefinition(
            float OutletThetaRad,
            float StartRadiusMm,
            float ThroatRadiusMm,
            List<Vector3> CurvedPoints);

        private readonly record struct ActiveParams(
            float RingRadiusRatio,
            float RingTaperRatio,
            float InletSplitBlend,
            float BranchHeightMm,
            float BranchArcSweepDeg,
            float FarSideElevationMm,
            float RingRadialShiftMm,
            float NearPortThroatRatio)
        {
            public static ActiveParams Read(IReadOnlyDictionary<string, float> parameters) => new(
                RingRadiusRatio: ReadParam(parameters, "RingRadiusRatio"),
                RingTaperRatio: ReadParam(parameters, "RingTaperRatio"),
                InletSplitBlend: ReadParam(parameters, "InletSplitBlend"),
                BranchHeightMm: ReadParam(parameters, "BranchHeightMm"),
                BranchArcSweepDeg: ReadParam(parameters, "BranchArcSweepDeg"),
                FarSideElevationMm: ReadParam(parameters, "FarSideElevationMm"),
                RingRadialShiftMm: ReadParam(parameters, "RingRadialShiftMm"),
                NearPortThroatRatio: ReadParam(parameters, "NearPortThroatRatio"));

            private static float ReadParam(IReadOnlyDictionary<string, float> parameters, string name)
            {
                if (parameters.TryGetValue(name, out float value))
                    return value;

                throw new InvalidOperationException(
                    $"[GEOMETRY] Missing parameter '{name}' for StatorCoolingRing. Present: "
                    + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                    + ". Add it to BaseParameters and ParameterBounds in project.json.");
            }
        }
    }
}
