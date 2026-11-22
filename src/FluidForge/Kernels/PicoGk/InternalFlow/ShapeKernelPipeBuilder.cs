using System;
using System.Collections.Generic;
using System.Numerics;
using Leap71.ShapeKernel;
using PicoGK;

namespace FluidForge.Kernels.PicoGk.InternalFlow
{
    /// <summary>
    /// Built internal-flow voxel volume with port definitions and interior seed point.
    /// </summary>
    public sealed record BuiltFluidVolume(
        Voxels FluidVoxels,
        IReadOnlyList<FlowPort> Ports,
        Vector3 FluidSeedPoint);

    /// <summary>
    /// Parametric ShapeKernel pipe builders for internal-flow validation and optimization geometries.
    /// </summary>
    public static class ShapeKernelPipeBuilder
    {
        /// <summary>
        /// Builds a straight circular fluid volume along +X from <c>x = 0</c> (<c>inlet</c>, normal -X)
        /// to <c>x = lengthMm</c> (<c>outlet</c>, normal +X), with end extensions for clean planar trimming.
        /// </summary>
        public static BuiltFluidVolume BuildStraightPipe(
            float lengthMm,
            float radiusMm,
            float portExtensionMm = 4f)
        {
            if (lengthMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(lengthMm), "lengthMm must be positive.");
            if (radiusMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(radiusMm), "radiusMm must be positive.");
            if (portExtensionMm < 0f)
                throw new ArgumentOutOfRangeException(nameof(portExtensionMm), "portExtensionMm must be non-negative.");

            LocalFrame startFrame = new LocalFrame(
                new Vector3(-portExtensionMm, 0f, 0f),
                Vector3.UnitX,
                Vector3.UnitZ);

            float totalLength = lengthMm + 2f * portExtensionMm;
            Frames frames = new Frames(totalLength, startFrame, fReparametrisationSpacing: 0.5f);
            BaseCylinder pipeCore = new BaseCylinder(frames, radiusMm);
            pipeCore.SetPolarSteps(180);
            pipeCore.SetLengthSteps((uint)Math.Max(20, (int)MathF.Ceiling(totalLength * 2f)));

            Voxels fluidVoxels = pipeCore.voxConstruct();

            var ports = new[]
            {
                new FlowPort("inlet", new Vector3(0f, 0f, 0f), -Vector3.UnitX, radiusMm),
                new FlowPort("outlet", new Vector3(lengthMm, 0f, 0f), Vector3.UnitX, radiusMm)
            };

            Vector3 seedPoint = new Vector3(lengthMm * 0.5f, 0f, 0f);
            return new BuiltFluidVolume(fluidVoxels, ports, seedPoint);
        }

        /// <summary>
        /// Builds a 90-degree pipe bend turning smoothly from +X to +Y in the XY plane,
        /// with straight inlet/outlet legs and optional smooth radius modulation at the bend midpoint.
        /// </summary>
        public static BuiltFluidVolume BuildPipeBend90(
            float pipeRadiusMm,
            float bendRadiusMm,
            float inletStraightMm,
            float outletStraightMm,
            float radiusModulationMid = 1.0f,
            float portExtensionMm = 4f)
        {
            if (pipeRadiusMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(pipeRadiusMm), "pipeRadiusMm must be positive.");
            if (bendRadiusMm <= 0f)
                throw new ArgumentOutOfRangeException(nameof(bendRadiusMm), "bendRadiusMm must be positive.");
            if (inletStraightMm < 0f)
                throw new ArgumentOutOfRangeException(nameof(inletStraightMm), "inletStraightMm must be non-negative.");
            if (outletStraightMm < 0f)
                throw new ArgumentOutOfRangeException(nameof(outletStraightMm), "outletStraightMm must be non-negative.");
            if (radiusModulationMid <= 0f)
                throw new ArgumentOutOfRangeException(nameof(radiusModulationMid), "radiusModulationMid must be positive.");
            if (portExtensionMm < 0f)
                throw new ArgumentOutOfRangeException(nameof(portExtensionMm), "portExtensionMm must be non-negative.");

            const float SpacingMm = 0.5f;
            var points = new List<Vector3>();

            // 1. Inlet straight (including -portExtensionMm overhang)
            float totalInletLen = portExtensionMm + inletStraightMm;
            int inletSteps = Math.Max(4, (int)MathF.Ceiling(totalInletLen / SpacingMm));
            for (int i = 0; i < inletSteps; i++)
            {
                float x = -portExtensionMm + totalInletLen * (i / (float)inletSteps);
                points.Add(new Vector3(x, 0f, 0f));
            }

            // 2. 90-degree circular arc centered at (inletStraightMm, bendRadiusMm, 0) from -90 deg to 0 deg
            float arcLength = 0.5f * MathF.PI * bendRadiusMm;
            int arcSteps = Math.Max(24, (int)MathF.Ceiling(arcLength / SpacingMm));
            for (int i = 0; i < arcSteps; i++)
            {
                float frac = i / (float)arcSteps;
                float theta = -0.5f * MathF.PI + frac * (0.5f * MathF.PI);
                float x = inletStraightMm + bendRadiusMm * MathF.Cos(theta);
                float y = bendRadiusMm + bendRadiusMm * MathF.Sin(theta);
                points.Add(new Vector3(x, y, 0f));
            }

            // 3. Outlet straight along +Y (including +portExtensionMm overhang)
            float totalOutletLen = outletStraightMm + portExtensionMm;
            int outletSteps = Math.Max(4, (int)MathF.Ceiling(totalOutletLen / SpacingMm));
            float outletX = inletStraightMm + bendRadiusMm;
            for (int i = 0; i <= outletSteps; i++)
            {
                float y = bendRadiusMm + totalOutletLen * (i / (float)outletSteps);
                points.Add(new Vector3(outletX, y, 0f));
            }

            Frames frames = new Frames(points, Vector3.UnitZ, bEnsureFrameContinuity: true, fReparametrisationSpacing: SpacingMm);
            BaseCylinder pipeCore = new BaseCylinder(frames, pipeRadiusMm);
            pipeCore.SetPolarSteps(180);

            if (MathF.Abs(radiusModulationMid - 1.0f) > 1e-5f)
            {
                float totalCenterlineLen = totalInletLen + arcLength + totalOutletLen;
                float tBendStart = totalInletLen / totalCenterlineLen;
                float tBendEnd = (totalInletLen + arcLength) / totalCenterlineLen;

                var radiusLine = new LineModulation(fRatio =>
                {
                    if (fRatio <= tBendStart || fRatio >= tBendEnd)
                        return pipeRadiusMm;

                    float u = (fRatio - tBendStart) / (tBendEnd - tBendStart);
                    float bump = 0.5f * (1f - MathF.Cos(2f * MathF.PI * u));
                    float scale = 1f + (radiusModulationMid - 1f) * bump;
                    return pipeRadiusMm * scale;
                });
                pipeCore.SetRadius(new SurfaceModulation(radiusLine));
            }

            pipeCore.SetLengthSteps((uint)Math.Max(200, points.Count));

            Voxels fluidVoxels = pipeCore.voxConstruct();

            var ports = new[]
            {
                new FlowPort("inlet", new Vector3(0f, 0f, 0f), -Vector3.UnitX, pipeRadiusMm),
                new FlowPort("outlet", new Vector3(outletX, bendRadiusMm + outletStraightMm, 0f), Vector3.UnitY, pipeRadiusMm)
            };

            float sin45 = MathF.Sin(MathF.PI / 4f);
            float cos45 = MathF.Cos(MathF.PI / 4f);
            Vector3 seedPoint = new Vector3(
                inletStraightMm + bendRadiusMm * sin45,
                bendRadiusMm * (1f - cos45),
                0f);

            return new BuiltFluidVolume(fluidVoxels, ports, seedPoint);
        }
    }
}
