using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk.InternalFlow;
using Leap71.ShapeKernel;
using PicoGK;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Parametric emergent 12-path vascular stator end-winding cooling manifold generator built with
    /// LEAP 71 ShapeKernel and PicoGK, exporting multi-patch CFD fluid domains, 3D-printable solid shells,
    /// and stator/HV-harness context environments.
    /// </summary>
    public sealed class StatorCoolingRingGeometryGenerator : IGeometryGenerator
    {
        public const int OutletCount = StatorCoolingRingConstants.OutletCount;
        public const float DefaultPitchCircleRadiusMm = StatorCoolingRingConstants.DefaultPitchCircleRadiusMm;
        public const float DefaultOutletRadiusMm = StatorCoolingRingConstants.DefaultOutletRadiusMm;
        public const float DefaultInletRadiusMm = StatorCoolingRingConstants.DefaultInletRadiusMm;
        public const float DefaultInletXMm = StatorCoolingRingConstants.DefaultInletXMm;
        public const float DefaultInletZMm = StatorCoolingRingConstants.DefaultInletZMm;
        public const float DefaultInletSplitXMm = StatorCoolingRingConstants.DefaultInletSplitXMm;
        public const float DefaultBoltCircleRadiusMm = StatorCoolingRingConstants.DefaultBoltCircleRadiusMm;
        public const float DefaultBoltHoleRadiusMm = StatorCoolingRingConstants.DefaultBoltHoleRadiusMm;
        public const float DefaultBoltKeepOutRadiusMm = StatorCoolingRingConstants.DefaultBoltKeepOutRadiusMm;
        public const float DefaultMinRequiredBoltClearanceMm = StatorCoolingRingConstants.DefaultMinRequiredBoltClearanceMm;
        public const float DefaultMinRequiredObstacleClearanceMm = StatorCoolingRingConstants.DefaultMinRequiredObstacleClearanceMm;
        public const float DefaultWallThicknessMm = StatorCoolingRingConstants.DefaultWallThicknessMm;
        public const float DefaultMinInnerRadiusMm = StatorCoolingRingConstants.DefaultMinInnerRadiusMm;
        public const float DefaultMaxOuterRadiusMm = StatorCoolingRingConstants.DefaultMaxOuterRadiusMm;
        public const float DefaultMaxTotalHeightMm = StatorCoolingRingConstants.DefaultMaxTotalHeightMm;
        public const float DefaultMinBranchRadiusMm = StatorCoolingRingConstants.DefaultMinBranchRadiusMm;
        public const float DefaultMaxFluidVolumeMm3 = StatorCoolingRingConstants.DefaultMaxFluidVolumeMm3;
        public const float DefaultMaxPrintableSolidVolumeMm3 = StatorCoolingRingConstants.DefaultMaxPrintableSolidVolumeMm3;
        public const float DefaultExportPrintableSolid = StatorCoolingRingConstants.DefaultExportPrintableSolid;
        public const float DefaultExportContextEnvironment = StatorCoolingRingConstants.DefaultExportContextEnvironment;

        private readonly StatorCoolingRingTargets _targets;

        /// <summary>
        /// Initializes the generator using <c>OptimizationTargets</c> from <paramref name="project"/>.
        /// </summary>
        public StatorCoolingRingGeometryGenerator(ProjectConfig? project = null)
        {
            _targets = StatorCoolingRingTargets.FromProject(project);
        }

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.GetOutletAnglesDeg" />
        public static IReadOnlyList<float> GetOutletAnglesDeg()
            => StatorCoolingRingVascularEngine.GetOutletAnglesDeg();

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.GetObstacleCapsules" />
        public static ObstacleCapsule[] GetObstacleCapsules()
            => StatorCoolingRingVascularEngine.GetObstacleCapsules();

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(IReadOnlyDictionary{string, float}, ProjectConfig?)" />
        public static StatorCoolingRingPackagingReport EvaluateAnalyticalPackaging(
            IReadOnlyDictionary<string, float> parameters,
            ProjectConfig? project = null)
            => StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(parameters, project);

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
            StatorCoolingRingParams p = StatorCoolingRingParams.Read(parameters);
            VascularNetwork network = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, _targets);
            StatorCoolingRingPackagingReport packaging =
                StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(network, p, _targets);

            string baseName = $"Model_Gen{iteration}_Var{variant}";

            using Voxels rawFluidVoxels = StatorCoolingRingTopologyBuilder.BuildFluidVolumeVoxels(network, p, _targets);
            using Voxels trimmedFluid = TrimPortOverhangsCompact(rawFluidVoxels);
            IReadOnlyList<FlowPort> ports = StatorCoolingRingTopologyBuilder.BuildFlowPorts(_targets);

            InternalFlowExportResult exported = MultiPatchFluidExporter.Export(
                trimmedFluid,
                ports,
                StatorCoolingRingConstants.FixedFluidSeedPoint,
                outputDirectory,
                baseName,
                trimPorts: false,
                applySmoothing: false);

            float printableSolidVolume = 0f;
            bool exportPrintable = iteration > 0 || variant == 1;
            if (packaging.IsFeasible && exportPrintable && _targets.ExportPrintableSolid)
            {
                string printablePath = Path.Combine(outputDirectory, $"{baseName}_printable.stl");
                printableSolidVolume = StatorCoolingRingSolidBuilder.BuildAndExportPrintableSolid(
                    rawFluidVoxels,
                    network,
                    p,
                    _targets,
                    printablePath);
            }

            if (packaging.IsFeasible && _targets.ExportContextEnvironment)
            {
                if (iteration == 0 && variant == 1)
                {
                    string contextPath = Path.Combine(outputDirectory, $"{baseName}_context_environment.stl");
                    StatorCoolingRingSolidBuilder.BuildAndExportContextEnvironment(_targets, contextPath);
                }
                else if (iteration >= 1)
                {
                    string parentDir = Directory.GetParent(outputDirectory)?.FullName ?? outputDirectory;
                    string contextPath = Path.Combine(parentDir, "StatorCoolingRing_context_environment.stl");
                    if (!File.Exists(contextPath))
                    {
                        StatorCoolingRingSolidBuilder.BuildAndExportContextEnvironment(_targets, contextPath);
                    }
                }
            }

            GeometryResult result = exported.ToGeometryResult();
            result.AddMetric("InnerRadiusMm", packaging.InnerRadiusMm, MetricScaling.Linear);
            result.AddMetric("OuterRadiusMm", packaging.OuterRadiusMm, MetricScaling.Linear);
            result.AddMetric("TotalHeightMm", packaging.TotalHeightMm, MetricScaling.Linear);
            result.AddMetric("MinBoltClearanceMm", packaging.MinBoltClearanceMm, MetricScaling.Linear);
            result.AddMetric("MinObstacleClearanceMm", packaging.MinObstacleClearanceMm, MetricScaling.Linear);
            result.AddMetric("MinBranchRadiusMm", packaging.MinBranchRadiusMm, MetricScaling.Linear);
            result.AddMetric("MinUnbundledGapMm", packaging.MinUnbundledGapMm, MetricScaling.Linear);
            result.AddMetric("SliverGapCount", packaging.SliverGapCount, MetricScaling.None);
            result.AddMetric("PrintableSolidVolume", printableSolidVolume, MetricScaling.Volume);
            return result;
        }

        private static Voxels TrimPortOverhangsCompact(Voxels fluidVoxels)
        {
            BaseBox zOutletCutter = new(new LocalFrame(Vector3.Zero, -Vector3.UnitZ), 16.0f, 220.0f, 220.0f);
            using Voxels zCutterVox = zOutletCutter.voxConstruct();
            using Voxels zTrimmed = fluidVoxels.voxBoolSubtract(zCutterVox);

            BaseBox xInletCutter = new(
                new LocalFrame(
                    new Vector3(StatorCoolingRingConstants.DefaultInletXMm, 0f, StatorCoolingRingConstants.DefaultInletZMm),
                    -Vector3.UnitX),
                18.0f, 44.0f, 44.0f);
            using Voxels xCutterVox = xInletCutter.voxConstruct();
            return zTrimmed.voxBoolSubtract(xCutterVox);
        }
    }
}
