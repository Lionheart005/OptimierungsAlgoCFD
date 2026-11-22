using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace FluidForge.Core
{
    /// <summary>
    /// Checks before starting the geometry kernel whether the PicoGK initial voxel size in physical space
    /// is sufficiently fine relative to the CFD surface cell size, and optionally refines it when
    /// <see cref="SimulationConfig.AutoVoxelResolution"/> is enabled.
    /// </summary>
    public static class VoxelResolutionAdvisor
    {
        public const float AutoRefineFloorMm = 0.1f;
        public const float WarningRatioToSurfaceCell = 0.8f;
        public const float AutoRefineRatioToSurfaceCell = 0.75f;

        /// <summary>
        /// Resolves the effective PicoGK initial voxel size without mutating <paramref name="sim"/>.
        /// For pipelines without an <see cref="IMeshResolutionTarget"/> (e.g., 2D),
        /// <see cref="SimulationConfig.BaseVoxelResolution"/> is returned unchanged without warning.
        /// </summary>
        public static float Resolve(
            SimulationConfig sim,
            ProjectConfig project,
            IReadOnlyList<SolverStage> stages,
            TextWriter? logWriter = null)
        {
            ArgumentNullException.ThrowIfNull(sim);
            ArgumentNullException.ThrowIfNull(project);
            ArgumentNullException.ThrowIfNull(stages);

            IMeshResolutionTarget? resolutionTarget = null;
            foreach (SolverStage stage in stages)
            {
                if (stage.Mesher is IMeshResolutionTarget target)
                {
                    resolutionTarget = target;
                    break;
                }
            }

            if (resolutionTarget == null)
            {
                return sim.BaseVoxelResolution;
            }

            float targetSize = sim.TargetPicoGkSize > 0f && float.IsFinite(sim.TargetPicoGkSize)
                ? sim.TargetPicoGkSize
                : 1.0f;

            float typicalScale = RubberBandScaler.Create(
                sim.UseRubberBandScaler,
                project.BaseParameters,
                project.DimensionalParameters,
                targetSize).ShrinkFactor;

            if (typicalScale <= 0f || !float.IsFinite(typicalScale))
            {
                typicalScale = 1.0f;
            }

            float baseSurfaceMm = resolutionTarget.TargetSurfaceCellSizeMm;

            if (baseSurfaceMm <= 0f || !float.IsFinite(baseSurfaceMm))
            {
                return sim.BaseVoxelResolution;
            }

            float refine = sim.MeshRefinementFactor > 0f && float.IsFinite(sim.MeshRefinementFactor)
                ? sim.MeshRefinementFactor
                : 1.0f;

            float surfaceMm = baseSurfaceMm * refine;
            float effective = sim.BaseVoxelResolution;
            float realVoxelMm = effective / typicalScale;

            TextWriter writer = logWriter ?? Console.Out;

            if (sim.AutoVoxelResolution && realVoxelMm > WarningRatioToSurfaceCell * surfaceMm)
            {
                float candidate = Math.Min(
                    sim.BaseVoxelResolution,
                    Math.Max(AutoRefineFloorMm, AutoRefineRatioToSurfaceCell * surfaceMm * typicalScale));

                if (candidate < effective)
                {
                    effective = candidate;
                    realVoxelMm = effective / typicalScale;
                    writer.WriteLine(string.Create(
                        CultureInfo.InvariantCulture,
                        $"[INFO] AutoVoxelResolution: refined PicoGK voxel size from {sim.BaseVoxelResolution:0.###} mm to {effective:0.###} mm (physical voxel {realVoxelMm:0.###} mm, target surface cell {surfaceMm:0.###} mm, scale factor {typicalScale:0.###})."));
                }
            }

            if (realVoxelMm > WarningRatioToSurfaceCell * surfaceMm)
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{OutputConventions.WarningTag} Initial voxel size ({effective:0.###} mm in PicoGK space, {realVoxelMm:0.###} mm physical) is coarse compared to target surface cell size ({surfaceMm:0.###} mm; recommended <= {WarningRatioToSurfaceCell * surfaceMm:0.###} mm)."));
            }

            return effective;
        }
    }
}
