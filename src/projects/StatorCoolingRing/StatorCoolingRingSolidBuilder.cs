using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk;
using Leap71.ShapeKernel;
using PicoGK;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Mechanical solid and context-environment STL exporter for <c>StatorCoolingRing</c> v3.
    /// Builds a 3D-printable manifold with locally scaled walls, 4x M5 DIN-912 counterbored domes,
    /// collision-checked hourglass inter-tube rib matrix and adaptive load-path support struts,
    /// organic voxel filleting, post-fillet sharp SW-24 G3/8" hex boss, O-ring grooves, and dowel pin holes.
    /// </summary>
    public static class StatorCoolingRingSolidBuilder
    {
        private const float DegToRad = StatorCoolingRingConstants.DegToRad;

        /// <summary>
        /// Builds and exports the watertight 3D-printable mechanical solid (<c>*_printable.stl</c>).
        /// </summary>
        public static float BuildAndExportPrintableSolid(
            Voxels rawFluidVoxels,
            VascularNetwork network,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets,
            string outputStlPath)
        {
            ArgumentNullException.ThrowIfNull(rawFluidVoxels);
            ArgumentNullException.ThrowIfNull(network);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputStlPath);

            Voxels solid = StatorCoolingRingTopologyBuilder.BuildLocalVariableOuterShellVoxels(network, p, targets);

            try
            {
                // 1. Base mounting flange ring (Z = 0..6.0 mm, R = 53.0..76.8 mm)
                BasePipe flangeRing = new(
                    new LocalFrame(Vector3.Zero, Vector3.UnitZ),
                    StatorCoolingRingConstants.DefaultFlangeHeightMm,
                    StatorCoolingRingConstants.DefaultFlangeInnerRadiusMm,
                    StatorCoolingRingConstants.DefaultFlangeOuterRadiusMm);
                flangeRing.SetPolarSteps(180);
                using Voxels flangeVox = flangeRing.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, flangeVox);

                // 2. 4x M5 DIN-912 mounting domes + radial flange bridges (60, 120, 240, 300 deg)
                foreach (float deg in StatorCoolingRingConstants.BoltAnglesDeg)
                {
                    float rad = deg * DegToRad;
                    Vector3 dir = new(MathF.Cos(rad), MathF.Sin(rad), 0f);
                    Vector3 domePos = dir * targets.BoltCircleRadiusMm;

                    BaseCylinder dome = new(
                        new LocalFrame(domePos, Vector3.UnitZ),
                        StatorCoolingRingConstants.DefaultBoltDomeHeightMm,
                        StatorCoolingRingConstants.DefaultBoltDomeOuterRadiusMm);
                    dome.SetPolarSteps(72);
                    using Voxels domeVox = dome.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, domeVox);

                    // Radial connection bridge from flange edge (R = 73.5) to dome axis (R = 84.0), Z = 0..7.5 mm
                    float bridgeStartR = 73.5f;
                    float bridgeLen = MathF.Max(2.0f, targets.BoltCircleRadiusMm - bridgeStartR);
                    LocalFrame bridgeFrame = new(
                        new Vector3(dir.X * bridgeStartR, dir.Y * bridgeStartR, 0.5f * 7.5f),
                        dir,
                        Vector3.UnitZ);
                    BaseBox bridge = new(bridgeFrame, bridgeLen, 7.5f, 15.5f);
                    using Voxels bridgeVox = bridge.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, bridgeVox);
                }

                // 3. Stage 2a & 2b: Collision-checked hourglass inter-tube rib matrix and adaptive dome/flange support struts
                BuildInterTubeRibMatrix(ref solid, network, targets);
                BuildAdaptiveSupportStruts(ref solid, network, targets);

                // 4. Continuous G3/8" cylindrical boss core (X = -108.0..-85.5, R = 11.8 mm) before organic filleting
                float inletZ = StatorCoolingRingConstants.DefaultInletZMm;
                BaseCylinder bossCore = new(
                    new LocalFrame(new Vector3(-108.0f, 0f, inletZ), Vector3.UnitX, Vector3.UnitZ),
                    22.5f,
                    11.8f);
                bossCore.SetPolarSteps(84);
                using Voxels bossCoreVox = bossCore.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, bossCoreVox);

                // 5. Inlet support bracket behind hex flats, strictly at X <= -78 mm (keeps 180 deg dowel pin hole & SW-24 clear)
                BaseBox consoleFoot = new(
                    new LocalFrame(new Vector3(-87.0f, 0f, 0.0f), Vector3.UnitZ, Vector3.UnitX),
                    6.5f,
                    18.0f,
                    26.0f);
                using Voxels footVox = consoleFoot.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, footVox);

                BaseBox consoleWeb = new(
                    new LocalFrame(new Vector3(-87.5f, 0f, 5.0f), Vector3.UnitZ, Vector3.UnitX),
                    13.5f,
                    14.0f,
                    10.0f);
                using Voxels webVox = consoleWeb.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, webVox);

                var consoleLattice = new Lattice();
                consoleLattice.AddBeam(new Vector3(-92.5f, 0f, 3.5f), new Vector3(-91.5f, 0f, 15.5f), 3.5f, 3.4f, true);
                consoleLattice.AddBeam(new Vector3(-81.0f, 0f, 3.5f), new Vector3(-87.0f, 0f, 15.5f), 3.8f, 3.6f, true);
                foreach (float sideDeg in new[] { 165f, 195f })
                {
                    float sRad = sideDeg * DegToRad;
                    float ySign = sideDeg < 180f ? 1f : -1f;
                    Vector3 flangeAnchor = new(74.0f * MathF.Cos(sRad), 74.0f * MathF.Sin(sRad), 4.5f);
                    Vector3 bossAnchor = new(-90.0f, ySign * 8.5f, 15.5f);
                    consoleLattice.AddBeam(bossAnchor, flangeAnchor, 3.0f, 2.8f, true);
                }
                using Voxels consoleStrutVox = new(consoleLattice);
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, consoleStrutVox);

                // 6. Stage 3: Organic PicoGK voxel closing & smoothing (blends ribs, struts, and forks smoothly)
                using (Voxels closed = solid.voxOverOffset(2.6f, 0.0f))
                {
                    solid.Dispose();
                    solid = closed.voxSmoothen(0.55f);
                }

                // 7. Post-fillet crisp SW-24 hex wrench boss (X = -106.5..-96.5): subtract outer clearance first, then union hex
                BaseCylinder hexWrench = new(
                    new LocalFrame(new Vector3(-106.5f, 0f, inletZ), Vector3.UnitX, Vector3.UnitZ),
                    10.0f,
                    12.0f);
                hexWrench.SetPolarSteps(120);
                hexWrench.SetLengthSteps(16);
                hexWrench.SetRadius(new SurfaceModulation((fPhi, _) => EvaluateHexRadius(fPhi, 12.0f)));
                using Voxels hexVox = hexWrench.voxConstruct();

                BaseCylinder outerClearCyl = new(
                    new LocalFrame(new Vector3(-106.5f, 0f, inletZ), Vector3.UnitX, Vector3.UnitZ),
                    10.0f,
                    26.0f);
                outerClearCyl.SetPolarSteps(96);
                using Voxels outerClearVox = outerClearCyl.voxConstruct();

                using Voxels wrenchClearanceCut = outerClearVox.voxBoolSubtract(hexVox);
                StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, wrenchClearanceCut);
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, hexVox);

                // 8. Subtract exact unsmoothed CFD fluid volume
                StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, rawFluidVoxels);

                // 9. Subtract G3/8" core bore (R = 7.5 mm, X = -114..-96) + 45 deg chamfer + transition cone
                BaseCylinder g38Core = new(
                    new LocalFrame(new Vector3(-114.0f, 0f, inletZ), Vector3.UnitX, Vector3.UnitZ),
                    18.0f,
                    7.5f);
                g38Core.SetPolarSteps(84);
                using Voxels g38CoreVox = g38Core.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, g38CoreVox);

                BaseCone g38Chamfer = new(
                    new LocalFrame(new Vector3(-108.5f, 0f, inletZ), Vector3.UnitX, Vector3.UnitZ),
                    2.0f,
                    9.0f,
                    7.5f);
                using Voxels chamferVox = g38Chamfer.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, chamferVox);

                BaseCone g38Transition = new(
                    new LocalFrame(new Vector3(-96.0f, 0f, inletZ), Vector3.UnitX, Vector3.UnitZ),
                    2.5f,
                    7.5f,
                    targets.InletRadiusMm);
                using Voxels transVox = g38Transition.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, transVox);

                // 8. Subtract 4x M5 through-bolt holes (R = 2.75) and DIN-912 counterbores / continuous tool access (R = 5.0)
                foreach (float deg in StatorCoolingRingConstants.BoltAnglesDeg)
                {
                    float rad = deg * DegToRad;
                    float bx = targets.BoltCircleRadiusMm * MathF.Cos(rad);
                    float by = targets.BoltCircleRadiusMm * MathF.Sin(rad);

                    BaseCylinder throughHole = new(
                        new LocalFrame(new Vector3(bx, by, -4.0f), Vector3.UnitZ),
                        18.0f,
                        targets.BoltHoleRadiusMm);
                    throughHole.SetPolarSteps(64);
                    using Voxels throughVox = throughHole.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, throughVox);

                    BaseCylinder counterbore = new(
                        new LocalFrame(new Vector3(bx, by, StatorCoolingRingConstants.DefaultBoltSeatHeightMm), Vector3.UnitZ),
                        53.0f,
                        StatorCoolingRingConstants.DefaultBoltCounterboreRadiusMm);
                    counterbore.SetPolarSteps(72);
                    using Voxels counterboreVox = counterbore.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, counterboreVox);
                }

                // 9. Subtract 2 concentric O-ring sealing grooves on Z = 0 (width 3.2 mm, depth 2.0 mm)
                float halfGroove = 0.5f * StatorCoolingRingConstants.DefaultORingWidthMm;
                float grooveHeight = StatorCoolingRingConstants.DefaultORingDepthMm + 1.0f;
                foreach (float grooveR in new[]
                {
                    StatorCoolingRingConstants.DefaultInnerORingRadiusMm,
                    StatorCoolingRingConstants.DefaultOuterORingRadiusMm
                })
                {
                    BasePipe groove = new(
                        new LocalFrame(new Vector3(0f, 0f, -1.0f), Vector3.UnitZ),
                        grooveHeight,
                        grooveR - halfGroove,
                        grooveR + halfGroove);
                    groove.SetPolarSteps(180);
                    using Voxels grooveVox = groove.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, grooveVox);
                }

                // 10. Subtract 2x dowel pin holes (R = 2.0 mm, depth 6.0 mm) at 0 deg and 180 deg on R = 65.0 mm
                foreach (float signX in new[] { -1.0f, 1.0f })
                {
                    BaseCylinder dowel = new(
                        new LocalFrame(new Vector3(signX * targets.PitchCircleRadiusMm, 0f, -1.0f), Vector3.UnitZ),
                        StatorCoolingRingConstants.DefaultDowelPinDepthMm + 1.0f,
                        StatorCoolingRingConstants.DefaultDowelPinRadiusMm);
                    dowel.SetPolarSteps(48);
                    using Voxels dowelVox = dowel.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, dowelVox);
                }

                // 11. Planar bottom mounting face (Z < 0) & planar inlet DIN-3852 sealing face (X < -108.0)
                BaseBox zBottomCutter = new(new LocalFrame(Vector3.Zero, -Vector3.UnitZ), 16.0f, 220.0f, 220.0f);
                using Voxels zCutterVox = zBottomCutter.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, zCutterVox);

                BaseBox xInletCutter = new(
                    new LocalFrame(new Vector3(StatorCoolingRingConstants.DefaultInletXMm, 0f, inletZ), -Vector3.UnitX),
                    18.0f, 44.0f, 44.0f);
                using Voxels xCutterVox = xInletCutter.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithDifference(ref solid, xCutterVox);

                return ExportCleanWatertightStl(solid, outputStlPath);
            }
            finally
            {
                solid.Dispose();
            }
        }

        /// <summary>
        /// Builds and exports the stator packaging and HV-harness context environment STL (<c>*_context_environment.stl</c>).
        /// </summary>
        public static void BuildAndExportContextEnvironment(
            StatorCoolingRingTargets targets,
            string outputStlPath)
        {
            ArgumentNullException.ThrowIfNull(targets);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputStlPath);

            // 1. Stator inner bore / rotor keep-out sleeve
            BasePipe innerSleeve = new(
                new LocalFrame(new Vector3(0f, 0f, -6.0f), Vector3.UnitZ),
                60.0f,
                44.0f,
                45.8f);
            innerSleeve.SetPolarSteps(128);
            Voxels env = innerSleeve.voxConstruct();

            try
            {
                // 2. Outer housing envelope indicator ring + top cover reference ring
                BasePipe outerRing = new(
                    new LocalFrame(Vector3.Zero, Vector3.UnitZ),
                    8.0f,
                    94.2f,
                    96.2f);
                outerRing.SetPolarSteps(128);
                using Voxels outerVox = outerRing.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref env, outerVox);

                BasePipe topRing = new(
                    new LocalFrame(new Vector3(0f, 0f, targets.MaxTotalHeightMm), Vector3.UnitZ),
                    1.5f,
                    92.0f,
                    95.0f);
                topRing.SetPolarSteps(128);
                using Voxels topVox = topRing.voxConstruct();
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref env, topVox);

                // 3. 4x M5 DIN-912 socket screw heads + vertical tool-access keep-out columns
                foreach (float deg in StatorCoolingRingConstants.BoltAnglesDeg)
                {
                    float rad = deg * DegToRad;
                    float bx = targets.BoltCircleRadiusMm * MathF.Cos(rad);
                    float by = targets.BoltCircleRadiusMm * MathF.Sin(rad);

                    BaseCylinder screwHead = new(
                        new LocalFrame(new Vector3(bx, by, StatorCoolingRingConstants.DefaultBoltSeatHeightMm), Vector3.UnitZ),
                        5.0f,
                        4.25f);
                    screwHead.SetPolarSteps(48);
                    using Voxels headVox = screwHead.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref env, headVox);

                    BaseCylinder toolColumn = new(
                        new LocalFrame(new Vector3(bx, by, 0f), Vector3.UnitZ),
                        targets.MaxTotalHeightMm + 2.0f,
                        5.0f);
                    toolColumn.SetPolarSteps(48);
                    using Voxels colVox = toolColumn.voxConstruct();
                    StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref env, colVox);
                }

                // 4. All 4 asymmetric HV-harness & sensor-conduit ObstacleCapsules
                var obsLattice = new Lattice();
                foreach (ObstacleCapsule obs in StatorCoolingRingTopologyBuilder.GetObstacleCapsules())
                    obsLattice.AddBeam(obs.Start, obs.End, obs.RadiusMm, obs.RadiusMm, true);
                using Voxels obsVox = new(obsLattice);
                StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref env, obsVox);

                ExportCleanWatertightStl(env, outputStlPath);
            }
            finally
            {
                env.Dispose();
            }
        }

        private static void BuildInterTubeRibMatrix(
            ref Voxels solid,
            VascularNetwork network,
            StatorCoolingRingTargets targets)
        {
            IReadOnlyList<RibSegment> ribs = StatorCoolingRingVascularEngine.BuildInterTubeRibSegments(network, targets);
            UnionHourglassRibSegments(ref solid, ribs);
        }

        private static void BuildAdaptiveSupportStruts(
            ref Voxels solid,
            VascularNetwork network,
            StatorCoolingRingTargets targets)
        {
            IReadOnlyList<RibSegment> ribs = StatorCoolingRingVascularEngine.BuildAdaptiveSupportRibCurves(network, targets);
            UnionHourglassRibSegments(ref solid, ribs);
        }

        private static void UnionHourglassRibSegments(ref Voxels solid, IReadOnlyList<RibSegment> segments)
        {
            if (segments.Count == 0)
                return;

            var lattice = new Lattice();
            foreach (RibSegment seg in segments)
            {
                Vector3 mid = 0.5f * (seg.Start + seg.End);
                lattice.AddBeam(seg.Start, mid, seg.StartRadiusMm, seg.MidRadiusMm, true);
                lattice.AddBeam(mid, seg.End, seg.MidRadiusMm, seg.EndRadiusMm, true);
            }

            using Voxels ribVox = new(lattice);
            StatorCoolingRingTopologyBuilder.ReplaceWithUnion(ref solid, ribVox);
        }

        internal static float EvaluateHexRadius(float phi, float inscribedRadius)
        {
            const float Sector = MathF.PI / 3.0f;
            float wrapped = ((phi % Sector) + Sector) % Sector - 0.5f * Sector;
            return inscribedRadius / MathF.Cos(wrapped);
        }

        private static float ExportCleanWatertightStl(Voxels vox, string outputStlPath)
        {
            using Mesh mesh = new(vox);
            IReadOnlyList<StlTriangle> rawTris = PicoGkMeshMetrics.ToTriangles(mesh);
            List<StlTriangle> cleanTris = rawTris
                .Where(t => t.A != t.B && t.B != t.C && t.C != t.A)
                .ToList();

            if (cleanTris.Count == 0)
                return 0f;

            StlWriter.WriteBinary(outputStlPath, cleanTris);
            return MeshMetrics.Measure(cleanTris).Volume;
        }
    }
}
