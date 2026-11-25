using System;
using System.Collections.Generic;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk.InternalFlow;
using Leap71.ShapeKernel;
using PicoGK;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Slim PicoGK voxel bridge for <c>StatorCoolingRing</c> v3, delegating pure-C# vascular synthesis,
    /// teardrop cross-section evaluation, and analytical packaging checks to <see cref="StatorCoolingRingVascularEngine"/>.
    /// </summary>
    public static class StatorCoolingRingTopologyBuilder
    {
        private const float DegToRad = StatorCoolingRingConstants.DegToRad;

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.GetOutletAnglesDeg" />
        public static IReadOnlyList<float> GetOutletAnglesDeg()
            => StatorCoolingRingVascularEngine.GetOutletAnglesDeg();

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.GetObstacleCapsules" />
        public static ObstacleCapsule[] GetObstacleCapsules()
            => StatorCoolingRingVascularEngine.GetObstacleCapsules();

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.SampleCubicHermite" />
        public static List<Vector3> SampleCubicHermite(
            Vector3 p0,
            Vector3 p1,
            Vector3 t0,
            Vector3 t1,
            int samples)
            => StatorCoolingRingVascularEngine.SampleCubicHermite(p0, p1, t0, t1, samples);

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.EvaluateLocalWallThicknessMm" />
        public static float EvaluateLocalWallThicknessMm(
            float fluidRadiusMm,
            StatorCoolingRingTargets targets,
            bool isInletNeck = false)
            => StatorCoolingRingVascularEngine.EvaluateLocalWallThicknessMm(fluidRadiusMm, targets, isInletNeck);

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.EvaluateTeardropCrossSectionRadius" />
        public static float EvaluateTeardropCrossSectionRadius(
            float fluidRadiusMm,
            float aspect,
            Vector3 dirWorld,
            Vector3 localZ,
            float endCircularFade = 1.0f)
            => StatorCoolingRingVascularEngine.EvaluateTeardropCrossSectionRadius(fluidRadiusMm, aspect, dirWorld, localZ, endCircularFade);

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(IReadOnlyDictionary{string, float}, ProjectConfig?)" />
        public static StatorCoolingRingPackagingReport EvaluateAnalyticalPackaging(
            IReadOnlyDictionary<string, float> parameters,
            ProjectConfig? project = null)
            => StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(parameters, project);

        /// <inheritdoc cref="StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(VascularNetwork, StatorCoolingRingParams, StatorCoolingRingTargets)" />
        public static StatorCoolingRingPackagingReport EvaluateAnalyticalPackaging(
            VascularNetwork network,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets)
            => StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(network, p, targets);

        /// <summary>
        /// Builds the complete PicoGK fluid voxel volume from a precomputed <see cref="VascularNetwork"/>.
        /// </summary>
        public static Voxels BuildFluidVolumeVoxels(
            VascularNetwork network,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets)
            => BuildTopologyVoxels(network, p, targets, includeWallOffset: false);

        /// <summary>
        /// Builds the complete PicoGK fluid voxel volume directly from parameters and targets.
        /// </summary>
        public static Voxels BuildFluidVolumeVoxels(
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets)
        {
            VascularNetwork network = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, targets);
            return BuildTopologyVoxels(network, p, targets, includeWallOffset: false);
        }

        /// <summary>
        /// Builds the outer solid shell voxels using locally scaled wall thicknesses and 45-degree teardrop envelopes.
        /// </summary>
        public static Voxels BuildLocalVariableOuterShellVoxels(
            VascularNetwork network,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets)
            => BuildTopologyVoxels(network, p, targets, includeWallOffset: true);

        /// <summary>
        /// Builds the outer solid shell voxels directly from parameters and targets.
        /// </summary>
        public static Voxels BuildLocalVariableOuterShellVoxels(
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets)
        {
            VascularNetwork network = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, targets);
            return BuildTopologyVoxels(network, p, targets, includeWallOffset: true);
        }

        /// <summary>
        /// Builds the 13 fixed <see cref="FlowPort"/> definitions (1 radial inlet + 12 axial outlets).
        /// </summary>
        public static IReadOnlyList<FlowPort> BuildFlowPorts(StatorCoolingRingTargets targets)
        {
            var ports = new List<FlowPort>(StatorCoolingRingConstants.OutletCount + 1)
            {
                new("inlet",
                    new Vector3(StatorCoolingRingConstants.DefaultInletXMm, 0f, StatorCoolingRingConstants.DefaultInletZMm),
                    -Vector3.UnitX,
                    targets.InletRadiusMm)
            };

            IReadOnlyList<float> anglesDeg = GetOutletAnglesDeg();
            for (int k = 0; k < StatorCoolingRingConstants.OutletCount; k++)
            {
                float rad = anglesDeg[k] * DegToRad;
                Vector3 center = new(
                    targets.PitchCircleRadiusMm * MathF.Cos(rad),
                    targets.PitchCircleRadiusMm * MathF.Sin(rad),
                    0f);
                ports.Add(new FlowPort($"outlet_{k + 1:D2}", center, -Vector3.UnitZ, targets.OutletRadiusMm));
            }

            return ports;
        }

        private static Voxels BuildTopologyVoxels(
            VascularNetwork network,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets,
            bool includeWallOffset)
        {
            ArgumentNullException.ThrowIfNull(network);
            ArgumentNullException.ThrowIfNull(targets);

            // 1. Fixed straight radial inlet stub at Z = DefaultInletZMm (20.0 mm)
            float inletStartX = StatorCoolingRingConstants.DefaultInletXMm - StatorCoolingRingConstants.PortOverhangMm;
            float inletLen = (StatorCoolingRingConstants.DefaultInletSplitXMm + 1.0f) - inletStartX;
            float inletWall = includeWallOffset ? EvaluateLocalWallThicknessMm(targets.InletRadiusMm, targets, isInletNeck: true) : 0f;
            LocalFrame inletFrame = new(
                new Vector3(inletStartX, 0f, StatorCoolingRingConstants.DefaultInletZMm),
                Vector3.UnitX,
                Vector3.UnitZ);
            BaseCylinder inletPipe = new(inletFrame, inletLen, targets.InletRadiusMm + inletWall);
            inletPipe.SetPolarSteps(96);
            inletPipe.SetLengthSteps(32);
            Voxels assembled = inletPipe.voxConstruct();

            try
            {
                // 2. Sweep all 12 emergent vascular paths with frame-invariant 45-degree teardrop cross-sections
                foreach (VascularPath path in network.Paths)
                {
                    BuildSweepWithOutletExtension(path, targets, out List<Vector3> pts, out List<float> radii, out List<float> fades);
                    using Voxels pathVox = ConstructSweptTeardropVoxels(
                        pts,
                        radii,
                        fades,
                        Frames.EFrameType.MIN_ROTATION,
                        reparamSpacing: 0.35f,
                        polarSteps: 76,
                        lengthSteps: (uint)Math.Max(96, pts.Count * 2),
                        aspect: p.CrossSectionAspect,
                        targets: targets,
                        includeWallOffset: includeWallOffset);
                    ReplaceWithUnion(ref assembled, pathVox);
                }

                if (!includeWallOffset)
                {
                    Voxels filleted = assembled.voxSmoothen(0.35f);
                    assembled.Dispose();
                    assembled = filleted;
                }

                return assembled;
            }
            catch
            {
                assembled.Dispose();
                throw;
            }
        }

        private static void BuildSweepWithOutletExtension(
            VascularPath path,
            StatorCoolingRingTargets targets,
            out List<Vector3> points,
            out List<float> radii,
            out List<float> fades)
        {
            const int ExtensionSteps = 6;
            int n = path.Points.Count;
            points = new List<Vector3>(n + ExtensionSteps);
            radii = new List<float>(n + ExtensionSteps);
            fades = new List<float>(n + ExtensionSteps);

            float totalLen = MathF.Max(1e-4f, path.PathLengthMm);
            float accum = 0f;
            for (int s = 0; s < n; s++)
            {
                if (s > 0)
                    accum += Vector3.Distance(path.Points[s], path.Points[s - 1]);
                float u = accum / totalLen;
                points.Add(path.Points[s]);
                radii.Add(path.FluidRadii[s]);
                fades.Add(StatorCoolingRingVascularEngine.EvaluateEndCircularFade(u));
            }

            Vector3 knee = path.Points[^1];
            float zEnd = -StatorCoolingRingConstants.PortOverhangMm;
            for (int k = 1; k <= ExtensionSteps; k++)
            {
                float frac = k / (float)ExtensionSteps;
                float z = knee.Z + frac * (zEnd - knee.Z);
                points.Add(new Vector3(knee.X, knee.Y, z));
                radii.Add(targets.OutletRadiusMm);
                fades.Add(0f);
            }
        }

        private static Voxels ConstructSweptTeardropVoxels(
            List<Vector3> points,
            List<float> fluidRadii,
            List<float> circularFades,
            Frames.EFrameType frameType,
            float reparamSpacing,
            uint polarSteps,
            uint lengthSteps,
            float aspect,
            StatorCoolingRingTargets targets,
            bool includeWallOffset)
        {
            Frames frames = new(
                points,
                frameType,
                bEnsureFrameContinuity: true,
                fReparametrisationSpacing: reparamSpacing);
            LineModulation fluidMod = BuildArcLengthMappedModulation(points, fluidRadii);
            LineModulation fadeMod = BuildArcLengthMappedModulation(points, circularFades);

            BaseCylinder cyl = new(frames, fluidRadii[0]);
            cyl.SetPolarSteps(polarSteps);
            cyl.SetLengthSteps(lengthSteps);
            cyl.SetRadius(new SurfaceModulation((fPhi, fLR) =>
            {
                float rFluid = fluidMod.fGetModulation(fLR);
                float endFade = fadeMod.fGetModulation(fLR);
                Vector3 localX = frames.vecGetLocalXAlongLength(fLR);
                Vector3 localY = frames.vecGetLocalYAlongLength(fLR);
                Vector3 localZ = frames.vecGetLocalZAlongLength(fLR);
                Vector3 dirWorld = MathF.Cos(fPhi) * localX + MathF.Sin(fPhi) * localY;
                float teardropR = EvaluateTeardropCrossSectionRadius(rFluid, aspect, dirWorld, localZ, endFade);
                return includeWallOffset
                    ? teardropR + EvaluateLocalWallThicknessMm(rFluid, targets)
                    : teardropR;
            }));
            return cyl.voxConstruct();
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

        internal static void ReplaceWithUnion(ref Voxels target, Voxels add)
        {
            Voxels next = target.voxBoolAdd(add);
            target.Dispose();
            target = next;
        }

        internal static void ReplaceWithDifference(ref Voxels target, Voxels subtract)
        {
            Voxels next = target.voxBoolSubtract(subtract);
            target.Dispose();
            target = next;
        }
    }
}
