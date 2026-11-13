using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Prüft vor dem Start des Geometrie-Kernels, ob die PicoGK-Start-Voxelgröße im Realraum
    /// ausreichend fein gegenüber der CFD-Oberflächenzellgröße ist, und verfeinert sie
    /// optional bei aktiviertem <see cref="SimulationConfig.AutoVoxelResolution"/>.
    /// </summary>
    public static class VoxelResolutionAdvisor
    {
        public const float AutoRefineFloorMm = 0.1f;
        public const float WarningRatioToSurfaceCell = 0.8f;
        public const float AutoRefineRatioToSurfaceCell = 0.75f;

        /// <summary>
        /// Ermittelt die effektive PicoGK-Start-Voxelgröße ohne <paramref name="sim"/> zu mutieren.
        /// Bei Pipelines ohne 3D-<see cref="GmshCfdMesher"/> (z. B. 2D) wird
        /// <see cref="SimulationConfig.BaseVoxelResolution"/> unverändert und ohne Warnung zurückgegeben.
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

            GmshCfdMesher? gmshMesher = null;
            foreach (SolverStage stage in stages)
            {
                if (stage.Mesher is GmshCfdMesher mesher)
                {
                    gmshMesher = mesher;
                    break;
                }
            }

            if (gmshMesher == null)
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

            GmshMesherOptions gmsh = gmshMesher.Options;
            float baseSurfaceMm = gmsh.SurfaceCellSize > 0f
                ? gmsh.SurfaceCellSize
                : gmsh.BoundaryLayerSizeMin;

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
                        $"[INFO] AutoVoxelResolution: PicoGK-Voxelgröße von {sim.BaseVoxelResolution:0.###} mm auf {effective:0.###} mm verfeinert (Realraum-Voxel {realVoxelMm:0.###} mm, Ziel-Oberflächenzelle {surfaceMm:0.###} mm, Skalenfaktor {typicalScale:0.###})."));
                }
            }

            if (realVoxelMm > WarningRatioToSurfaceCell * surfaceMm)
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"[WARNUNG] Start-Voxelgröße ({effective:0.###} mm im PicoGK-Raum, Realraum {realVoxelMm:0.###} mm) ist grob gegenüber der Ziel-Oberflächenzellgröße ({surfaceMm:0.###} mm; empfohlen <= {WarningRatioToSurfaceCell * surfaceMm:0.###} mm)."));
            }

            return effective;
        }
    }
}
