using System;
using System.Collections.Generic;
using System.Numerics;
using FluidForge.Core;

namespace FluidForge.Projects.StatorCoolingRing
{
    /// <summary>
    /// Pure-C# emergent 12-path vascular network engine for <c>StatorCoolingRing</c>.
    /// Performs topology-free geodesic initialization around the rotor keep-out, 180-step force-directed
    /// relaxation (tension/biharmonic stiffness, 3D teardrop SDF obstacle avoidance, bistable edge bundling,
    /// anti-sliver repulsion, far-side loop bridge, and vertical elevation potential), Murray's-law hydraulic
    /// sizing, 1D flow resistance pre-balancing, analytical packaging/sliver evaluation, and collision-checked
    /// structural rib synthesis — with zero PicoGK dependency.
    /// </summary>
    public static class StatorCoolingRingVascularEngine
    {
        private const int PathCount = StatorCoolingRingConstants.OutletCount;
        private const int NodesPerPath = 48;
        private const int TotalNodes = PathCount * NodesPerPath;
        private const int SegmentsPerPath = NodesPerPath - 1;
        private const int FixedStartNodes = 3; // s = 0, 1, 2
        private const int FixedEndNodes = 3;   // s = 45, 46, 47
        private const int RelaxationIterations = 180;
        private const int ResampleInterval = 15;
        private const int ChunkSize = 8;
        private const int ChunkCount = (SegmentsPerPath + ChunkSize - 1) / ChunkSize; // 6
        private const float RotorSeedRadiusMm = 58.0f;
        private const float MaxStepDisplacementMm = 2.2f;
        private const float MurraySigmaMm = 2.4f;
        private const float DegToRad = StatorCoolingRingConstants.DegToRad;

        /// <summary>
        /// Returns the 12 outlet angular positions in degrees: <c>theta_k = k * 30 + 15</c>.
        /// </summary>
        public static IReadOnlyList<float> GetOutletAnglesDeg()
        {
            var angles = new float[PathCount];
            for (int k = 0; k < PathCount; k++)
                angles[k] = k * 30.0f + 15.0f;
            return angles;
        }

        /// <summary>
        /// Returns the asymmetric HV 3-phase cable harness and NTC/resolver sensor keep-out capsules,
        /// strictly decoupled from the bottom mounting flange and concentric O-ring grooves.
        /// </summary>
        public static ObstacleCapsule[] GetObstacleCapsules() => new[]
        {
            new ObstacleCapsule("HvPhaseRiser", new Vector3(0f, 43.0f, 0.0f), new Vector3(0f, 43.0f, 13.8f), 4.2f),
            new ObstacleCapsule("HvPhaseConduit", new Vector3(0f, 43.0f, 13.8f), new Vector3(0f, 102.0f, 13.8f), 4.2f),
            new ObstacleCapsule("SensorRiser", new Vector3(6.6f, 42.5f, 0.0f), new Vector3(6.6f, 42.5f, 13.8f), 2.4f),
            new ObstacleCapsule("SensorConduit", new Vector3(6.6f, 42.5f, 13.8f), new Vector3(6.6f, 102.0f, 13.8f), 2.4f)
        };

        /// <summary>
        /// Samples a cubic Hermite spline between <paramref name="p0"/> and <paramref name="p1"/>
        /// with endpoint tangents <paramref name="t0"/> and <paramref name="t1"/> in pure C#.
        /// </summary>
        public static List<Vector3> SampleCubicHermite(
            Vector3 p0,
            Vector3 p1,
            Vector3 t0,
            Vector3 t1,
            int samples)
        {
            int count = Math.Max(2, samples);
            var result = new List<Vector3>(count);
            for (int i = 0; i < count; i++)
            {
                float u = i / (float)(count - 1);
                float u2 = u * u;
                float u3 = u2 * u;

                float h00 = 2f * u3 - 3f * u2 + 1f;
                float h10 = u3 - 2f * u2 + u;
                float h01 = -2f * u3 + 3f * u2;
                float h11 = u3 - u2;

                Vector3 pt = h00 * p0 + h10 * t0 + h01 * p1 + h11 * t1;
                if (result.Count == 0 || (pt - result[^1]).LengthSquared() > 1e-8f)
                    result.Add(pt);
            }

            if (result.Count == 1)
                result.Add(p1);
            else
                result[^1] = p1;

            return result;
        }

        /// <summary>
        /// Evaluates the locally scaled structural wall thickness for a channel of fluid radius <paramref name="fluidRadiusMm"/>.
        /// </summary>
        public static float EvaluateLocalWallThicknessMm(
            float fluidRadiusMm,
            StatorCoolingRingTargets targets,
            bool isInletNeck = false)
        {
            if (isInletNeck)
                return MathF.Max(4.2f, targets.WallThicknessMm + 1.5f);

            return Math.Clamp(
                targets.WallThicknessMm + 0.30f * MathF.Max(0f, fluidRadiusMm - targets.OutletRadiusMm),
                targets.WallThicknessMm,
                4.2f);
        }

        /// <summary>
        /// Evaluates the frame-invariant 45-degree self-supporting DfAM teardrop cross-section radius
        /// in world radial direction <paramref name="dirWorld"/>, seamlessly blending to a circle on vertical segments.
        /// </summary>
        public static float EvaluateTeardropCrossSectionRadius(
            float fluidRadiusMm,
            float aspect,
            Vector3 dirWorld,
            Vector3 localZ,
            float endCircularFade = 1.0f)
        {
            Vector3 unitDir = dirWorld.LengthSquared() > 1e-8f ? Vector3.Normalize(dirWorld) : Vector3.UnitZ;
            float localZZ = Math.Clamp(localZ.Z, -1f, 1f);
            float horizWeight = MathF.Sqrt(MathF.Max(0f, 1f - localZZ * localZZ)) * Math.Clamp(endCircularFade, 0f, 1f);

            float cosUp = unitDir.Z;
            float sinHoriz = MathF.Sqrt(MathF.Max(0f, 1f - cosUp * cosUp));

            float safeAspect = MathF.Max(0.25f, aspect);
            float sqrtAspect = MathF.Sqrt(safeAspect);
            float a = fluidRadiusMm / sqrtAspect;
            float b = fluidRadiusMm * sqrtAspect;

            float aEff = fluidRadiusMm + horizWeight * (a - fluidRadiusMm);
            float bEff = fluidRadiusMm + horizWeight * (b - fluidRadiusMm);

            float denomSq = MathF.Max(1e-6f, (bEff * sinHoriz) * (bEff * sinHoriz) + (aEff * cosUp) * (aEff * cosUp));
            float rEllipse = (aEff * bEff) / MathF.Sqrt(denomSq);

            if (cosUp <= 0f)
                return rEllipse;

            float roofScale = MathF.Sqrt(aEff * aEff + bEff * bEff);
            float rRoof = roofScale / MathF.Max(1e-4f, sinHoriz + cosUp);
            float rRoofCapped = MathF.Min(rRoof, 0.94f * roofScale);
            float rTeardrop = MathF.Max(rEllipse, rRoofCapped);
            return rEllipse + horizWeight * (rTeardrop - rEllipse);
        }

        /// <summary>
        /// Smooth fade mask (0 at inlet/outlet circular interfaces, 1 across interior printable sweeps).
        /// </summary>
        public static float EvaluateEndCircularFade(float u)
        {
            float clamped = Math.Clamp(u, 0f, 1f);
            float inletFade = SmoothStep(clamped / 0.12f);
            float outletFade = 1.0f - SmoothStep((clamped - 0.82f) / 0.18f);
            return inletFade * outletFade;
        }

        /// <summary>
        /// Builds the deterministic 12-path emergent vascular network for <paramref name="p"/> and <paramref name="targets"/>.
        /// </summary>
        public static VascularNetwork BuildVascularNetwork(
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets)
        {
            ArgumentNullException.ThrowIfNull(targets);

            float splitAngleDeg = 75.0f * p.UpperLowerSplitBias;
            IReadOnlyList<float> outletAnglesDeg = GetOutletAnglesDeg();
            ObstacleCapsule[] obstacles = GetObstacleCapsules();

            var boltPositionsXY = new Vector2[StatorCoolingRingConstants.BoltAnglesDeg.Length];
            for (int b = 0; b < StatorCoolingRingConstants.BoltAnglesDeg.Length; b++)
            {
                float bRad = StatorCoolingRingConstants.BoltAnglesDeg[b] * DegToRad;
                boltPositionsXY[b] = new Vector2(
                    targets.BoltCircleRadiusMm * MathF.Cos(bRad),
                    targets.BoltCircleRadiusMm * MathF.Sin(bRad));
            }

            var isUpper = new bool[PathCount];
            var thetaNormDeg = new float[PathCount];
            int upperCount = 0;
            int lowerCount = 0;
            int upperBoundaryIdx = -1;
            int lowerBoundaryIdx = -1;
            float bestUpperNorm = float.MaxValue;
            float bestLowerNorm = float.MinValue;

            for (int i = 0; i < PathCount; i++)
            {
                float norm = NormalizeAngleSignedDeg(outletAnglesDeg[i]);
                thetaNormDeg[i] = norm;
                bool up = norm > splitAngleDeg;
                isUpper[i] = up;
                if (up)
                {
                    upperCount++;
                    if (norm < bestUpperNorm)
                    {
                        bestUpperNorm = norm;
                        upperBoundaryIdx = i;
                    }
                }
                else
                {
                    lowerCount++;
                    if (norm > bestLowerNorm)
                    {
                        bestLowerNorm = norm;
                        lowerBoundaryIdx = i;
                    }
                }
            }

            // Flat 1D arrays for deterministic, cache-friendly inner loops
            var nodes = new Vector3[TotalNodes];
            var nextNodes = new Vector3[TotalNodes];
            var tangents = new Vector3[TotalNodes];
            var arcParams = new float[TotalNodes];
            var arcDistances = new float[TotalNodes];
            var rEst = new float[TotalNodes];
            var chunkCenters = new Vector3[PathCount * ChunkCount];
            var chunkRadii = new float[PathCount * ChunkCount];
            var pathMin = new Vector3[PathCount];
            var pathMax = new Vector3[PathCount];
            var lastBundledArc = new float[PathCount];

            // Precompute far-side bridge target at watershed angle (arc radius >= R_bridge_min)
            float splitRad = splitAngleDeg * DegToRad;
            float rBridgeMin = MathF.Max(60.0f, targets.MinInnerRadiusMm + 15.0f);
            float rBridgeTarget = MathF.Max(rBridgeMin, targets.PitchCircleRadiusMm - 1.0f);
            Vector2 farBridgeXY = new(rBridgeTarget * MathF.Cos(splitRad), rBridgeTarget * MathF.Sin(splitRad));

            // Phase A — Topology-free geodesic initialization (12 paths x 48 nodes)
            float yImpulse = -1.25f * p.UpperLowerSplitBias;
            Vector3 inletP0 = new(StatorCoolingRingConstants.DefaultInletSplitXMm, 0f, StatorCoolingRingConstants.DefaultInletZMm);
            Vector3 inletP1 = new(StatorCoolingRingConstants.DefaultInletSplitXMm + 3.5f, 0f, StatorCoolingRingConstants.DefaultInletZMm);
            Vector3 inletP2 = new(StatorCoolingRingConstants.DefaultInletSplitXMm + 7.0f, yImpulse, StatorCoolingRingConstants.DefaultInletZMm);

            for (int i = 0; i < PathCount; i++)
            {
                int row = i * NodesPerPath;
                float rad = outletAnglesDeg[i] * DegToRad;
                float outX = targets.PitchCircleRadiusMm * MathF.Cos(rad);
                float outY = targets.PitchCircleRadiusMm * MathF.Sin(rad);

                nodes[row] = inletP0;
                nodes[row + 1] = inletP1;
                nodes[row + 2] = inletP2;

                nodes[row + NodesPerPath - 3] = new Vector3(outX, outY, StatorCoolingRingConstants.OutletKneeZMm + 4.8f);
                nodes[row + NodesPerPath - 2] = new Vector3(outX, outY, StatorCoolingRingConstants.OutletKneeZMm + 2.2f);
                nodes[row + NodesPerPath - 1] = new Vector3(outX, outY, StatorCoolingRingConstants.OutletKneeZMm);

                InitializeGeodesicInterior(nodes, i, isUpper[i], thetaNormDeg[i], p);
            }

            UpdateChunkBounds(nodes, chunkCenters, chunkRadii, pathMin, pathMax);
            UpdateGeometryCaches(nodes, tangents, arcParams, arcDistances, rEst, chunkCenters, chunkRadii, pathMin, pathMax, p, targets, upperBoundaryIdx, lowerBoundaryIdx, updateRadii: true);

            float maxInteractionRange = MathF.Max(p.BundlingRangeMm, 18.0f) + 2.0f;
            float releaseEnd = 1.0f - p.SplitReleaseBias;
            const float ReleaseWidth = 0.22f;

            // Phase B — 180-step Force-Directed Relaxation
            for (int iter = 0; iter < RelaxationIterations; iter++)
            {
                float progress = iter / (float)(RelaxationIterations - 1);
                float eta = 0.24f + (0.06f - 0.24f) * progress;

                if (iter > 0 && iter % ResampleInterval == 0)
                {
                    for (int i = 0; i < PathCount; i++)
                        ResampleInteriorArcLength(nodes, i);
                    UpdateChunkBounds(nodes, chunkCenters, chunkRadii, pathMin, pathMax);
                    UpdateGeometryCaches(nodes, tangents, arcParams, arcDistances, rEst, chunkCenters, chunkRadii, pathMin, pathMax, p, targets, upperBoundaryIdx, lowerBoundaryIdx, updateRadii: iter % 45 == 0);
                }
                else if (iter > 0)
                {
                    UpdateChunkBounds(nodes, chunkCenters, chunkRadii, pathMin, pathMax);
                    UpdateGeometryCaches(nodes, tangents, arcParams, arcDistances, rEst, chunkCenters, chunkRadii, pathMin, pathMax, p, targets, upperBoundaryIdx, lowerBoundaryIdx, updateRadii: false);
                }

                // Light pre-settle smoothing before the final SDF relaxation tail
                if (iter == RelaxationIterations - 14)
                {
                    ApplyLaplacianSmoothPass(nodes, nextNodes);
                    UpdateChunkBounds(nodes, chunkCenters, chunkRadii, pathMin, pathMax);
                    UpdateGeometryCaches(nodes, tangents, arcParams, arcDistances, rEst, chunkCenters, chunkRadii, pathMin, pathMax, p, targets, upperBoundaryIdx, lowerBoundaryIdx, updateRadii: false);
                }

                for (int i = 0; i < PathCount; i++)
                {
                    int row = i * NodesPerPath;
                    for (int s = 0; s < FixedStartNodes; s++)
                        nextNodes[row + s] = nodes[row + s];
                    for (int s = NodesPerPath - FixedEndNodes; s < NodesPerPath; s++)
                        nextNodes[row + s] = nodes[row + s];

                    // Track last bundled arc position per partner path j in a single forward sweep
                    for (int j = 0; j < PathCount; j++)
                        lastBundledArc[j] = arcDistances[row + 2];

                    for (int s = FixedStartNodes; s < NodesPerPath - FixedEndNodes; s++)
                    {
                        int idx = row + s;
                        Vector3 pos = nodes[idx];
                        Vector3 tan = tangents[idx];
                        float u = arcParams[idx];
                        float rFluid = rEst[idx];
                        float wall = EvaluateLocalWallThicknessMm(rFluid, targets);
                        float endFade = EvaluateEndCircularFade(u);

                        // 1. Tension + Biharmonic Bend Smoothness
                        float stiff = p.PathStiffness;
                        Vector3 fTens = 0.5f * (nodes[idx - 1] + nodes[idx + 1]) - pos;
                        Vector3 fBend = -nodes[idx - 2] + 4.0f * nodes[idx - 1] - 6.0f * pos + 4.0f * nodes[idx + 1] - nodes[idx + 2];
                        Vector3 force = (0.22f + 0.58f * stiff) * fTens + (0.03f + 0.10f * stiff) * fBend;

                        // 2. 3D SDF Obstacle & Packaging Avoidance
                        force += ComputeSdfForce(
                            pos, tan, s, rFluid, wall, p, targets, endFade,
                            boltPositionsXY, obstacles);

                        // 3. Bistable Edge Bundling + Anti-Sliver Repulsion + Far-Side Loop Bridge
                        float muSplit = 1.0f - SmoothStep((u - (releaseEnd - ReleaseWidth)) / ReleaseWidth);
                        float rSnap = p.BundlingRangeMm * muSplit;
                        float inletRepelFade = SmoothStep((u - 0.12f) / 0.10f);

                        Vector3 bundlePullSum = Vector3.Zero;
                        float bundleWeightSum = 0f;
                        Vector3 bridgeForce = Vector3.Zero;
                        Vector3 repelSum = Vector3.Zero;

                        for (int j = 0; j < PathCount; j++)
                        {
                            if (j == i) continue;

                            bool isBridgeCandidate = p.FarSideLoopAttraction > 0.01f &&
                                ((i == upperBoundaryIdx && j == lowerBoundaryIdx) || (i == lowerBoundaryIdx && j == upperBoundaryIdx)) &&
                                u >= 0.74f && u <= 0.90f;

                            if (isBridgeCandidate)
                            {
                                float win = BridgeWindowWeight(u);
                                float bridgeGain = 2.4f * p.FarSideLoopAttraction * p.FarSideLoopAttraction * win;
                                if (iter >= 100 && p.FarSideLoopAttraction >= 0.75f)
                                    bridgeGain *= 2.2f;

                                Vector3 bridgeAnchor = new(farBridgeXY.X, farBridgeXY.Y, pos.Z);
                                bool hitBridge = FindClosestOnPath(
                                    pos, j, nodes, tangents, arcParams, rEst, chunkCenters, chunkRadii, pathMin, pathMax, 65.0f,
                                    out Vector3 qBridge, out _, out float uBridge, out _, out float distBridgeSq);

                                Vector3 pullTarget = bridgeAnchor;
                                if (hitBridge && uBridge >= 0.70f && uBridge <= 0.94f)
                                {
                                    float distBridge = MathF.Sqrt(distBridgeSq);
                                    Vector3 midPt = 0.5f * (pos + qBridge);
                                    float rMidXY = MathF.Sqrt(midPt.X * midPt.X + midPt.Y * midPt.Y);
                                    if (rMidXY < rBridgeMin)
                                    {
                                        Vector2 dirXY = rMidXY > 1e-4f
                                            ? new Vector2(midPt.X / rMidXY, midPt.Y / rMidXY)
                                            : Vector2.Normalize(farBridgeXY);
                                        midPt = new Vector3(dirXY.X * rBridgeMin, dirXY.Y * rBridgeMin, midPt.Z);
                                    }
                                    float snapBlend = 1.0f - SmoothStep((distBridge - 18.0f) / 28.0f);
                                    pullTarget = Vector3.Lerp(bridgeAnchor, midPt, snapBlend);
                                }
                                bridgeForce += bridgeGain * (pullTarget - pos);
                                continue;
                            }

                            bool hit = FindClosestOnPath(
                                pos, j, nodes, tangents, arcParams, rEst, chunkCenters, chunkRadii, pathMin, pathMax, maxInteractionRange,
                                out Vector3 qClosest, out Vector3 qTan, out _, out float rJ, out float distSq);

                            if (!hit) continue;

                            float dist = MathF.Sqrt(distSq);
                            if (dist <= 2.6f)
                                lastBundledArc[j] = arcDistances[idx];

                            float cDir = Vector3.Dot(tan, qTan);

                            bool inSnapZone = cDir > 0.25f && dist < rSnap && rSnap > 1e-4f;
                            if (inSnapZone)
                            {
                                float normD = dist / rSnap;
                                float rawAttr = p.BundlingStrength * cDir * muSplit * (1.0f - normD * normD);

                                // Bistable well split: commit to bundling when strong, otherwise fan out cleanly past inlet neck
                                bool snapCommitted = rawAttr >= 0.18f && (iter < 120 || dist <= 1.75f || rawAttr >= 0.28f);
                                if (!snapCommitted && inletRepelFade > 1e-3f)
                                {
                                    float wallJ = EvaluateLocalWallThicknessMm(rJ, targets);
                                    float dSep = rFluid + rJ + wall + wallJ + 3.0f;
                                    if (dist < dSep)
                                    {
                                        float arcSinceBundle = arcDistances[idx] - lastBundledArc[j];
                                        float yFade = SmoothStep(arcSinceBundle / 5.0f) * inletRepelFade;
                                        if (yFade > 1e-3f)
                                        {
                                            Vector3 away = dist > 1e-4f ? (pos - qClosest) / dist : SideSpreadNormal(pos, i, j);
                                            repelSum += 0.90f * yFade * (dSep - dist) * away;
                                        }
                                    }
                                }
                                else if (snapCommitted)
                                {
                                    lastBundledArc[j] = arcDistances[idx];
                                    float w = 1.85f * rawAttr;
                                    if (iter >= 100 && dist < 3.6f)
                                        w += 2.20f * p.BundlingStrength;
                                    bundlePullSum += w * (qClosest - pos);
                                    bundleWeightSum += w;
                                }
                            }
                            else if (inletRepelFade > 1e-3f)
                            {
                                // Zone 2: Anti-Sliver Repulsion outside Y-bifurcation and inlet transition
                                float wallJ = EvaluateLocalWallThicknessMm(rJ, targets);
                                float dSep = rFluid + rJ + wall + wallJ + 3.0f;
                                if (dist < dSep)
                                {
                                    float arcSinceBundle = arcDistances[idx] - lastBundledArc[j];
                                    float yFade = SmoothStep(arcSinceBundle / 5.0f) * inletRepelFade;
                                    if (yFade > 1e-3f)
                                    {
                                        Vector3 away = dist > 1e-4f ? (pos - qClosest) / dist : SideSpreadNormal(pos, i, j);
                                        repelSum += 0.90f * yFade * (dSep - dist) * away;
                                    }
                                }
                            }
                        }

                        if (bundleWeightSum > 1e-5f)
                            force += 1.15f * bundlePullSum / MathF.Max(1.0f, bundleWeightSum);
                        force += bridgeForce;
                        float repelLenSq = repelSum.LengthSquared();
                        if (repelLenSq > 4.0f * 4.0f)
                            repelSum *= 4.0f / MathF.Sqrt(repelLenSq);
                        force += repelSum;

                        // 4. Vertical Elevation Potential Field
                        float thetaPos = MathF.Atan2(pos.Y, pos.X);
                        float zTarget = p.ElevationBaseMm + p.ElevationTiltMm * 0.5f * (1.0f + MathF.Cos(thetaPos));
                        float elevFade = SmoothStep(u / 0.12f) * (1.0f - SmoothStep((u - 0.80f) / 0.20f));
                        force.Z += 0.22f * elevFade * (zTarget - pos.Z);

                        Vector3 delta = eta * force;
                        float deltaLen = delta.Length();
                        if (deltaLen > MaxStepDisplacementMm)
                            delta *= MaxStepDisplacementMm / deltaLen;

                        nextNodes[idx] = pos + delta;
                    }
                }

                Array.Copy(nextNodes, nodes, TotalNodes);
            }

            UpdateChunkBounds(nodes, chunkCenters, chunkRadii, pathMin, pathMax);
            UpdateGeometryCaches(nodes, tangents, arcParams, arcDistances, rEst, chunkCenters, chunkRadii, pathMin, pathMax, p, targets, upperBoundaryIdx, lowerBoundaryIdx, updateRadii: true);

            // Phase C — Murray's Law (k_eff & r_murray)
            var kEffAll = new float[TotalNodes];
            var rMurrayAll = new float[TotalNodes];

            for (int i = 0; i < PathCount; i++)
            {
                int row = i * NodesPerPath;
                for (int s = 0; s < NodesPerPath; s++)
                {
                    int idx = row + s;
                    float u = arcParams[idx];
                    float kEff = ComputeNodeMultiplicity(
                        i, s, u, nodes, tangents, chunkCenters, chunkRadii, pathMin, pathMax, p, upperBoundaryIdx, lowerBoundaryIdx);
                    kEffAll[idx] = kEff;

                    float rMurray = targets.OutletRadiusMm * p.BaseChannelScale * MathF.Pow(MathF.Max(1.0f, kEff), 1.0f / p.MurrayExponent);

                    if (u < 0.16f)
                    {
                        float tIn = SmoothStep(u / 0.16f);
                        rMurray = targets.InletRadiusMm + tIn * (rMurray - targets.InletRadiusMm);
                    }
                    if (u > 0.92f)
                    {
                        float tOut = SmoothStep((u - 0.92f) / 0.08f);
                        rMurray += tOut * (targets.OutletRadiusMm - rMurray);
                    }

                    rMurrayAll[idx] = rMurray;
                }
            }

            // Phase D — 1D Hydraulic Resistance Pre-Balancing (HydraulicBalanceGain + ResidualThroatTilt)
            var pathLengths = new float[PathCount];
            var resistances = new float[PathCount];
            float sumR = 0f;

            for (int i = 0; i < PathCount; i++)
            {
                int row = i * NodesPerPath;
                float len = 0f;
                float rFric = 0f;
                float rBend = 0f;

                for (int s = 1; s < NodesPerPath; s++)
                {
                    int idx = row + s;
                    float ds = Vector3.Distance(nodes[idx], nodes[idx - 1]);
                    len += ds;
                    float rMid = MathF.Max(0.8f, 0.5f * (rMurrayAll[idx - 1] + rMurrayAll[idx]));
                    rFric += ds / MathF.Pow(rMid, 4.75f);

                    if (s < NodesPerPath - 1)
                    {
                        float dot = Math.Clamp(Vector3.Dot(tangents[idx - 1], tangents[idx]), -1f, 1f);
                        float dAlpha = MathF.Acos(dot);
                        float rNode = MathF.Max(0.8f, rMurrayAll[idx]);
                        rBend += (dAlpha * dAlpha) / MathF.Pow(rNode, 4.0f);
                    }
                }

                pathLengths[i] = len;
                resistances[i] = rFric + 0.35f * rBend;
                sumR += resistances[i];
            }

            float meanR = MathF.Max(1e-9f, sumR / PathCount);
            var paths = new VascularPath[PathCount];

            for (int i = 0; i < PathCount; i++)
            {
                int row = i * NodesPerPath;
                float f1D = Math.Clamp(MathF.Pow(resistances[i] / meanR, 0.21f), 0.72f, 1.38f);
                float fBal = 1.0f + p.HydraulicBalanceGain * (f1D - 1.0f);
                float fTilt = 1.0f + p.ResidualThroatTilt * MathF.Cos(outletAnglesDeg[i] * DegToRad);
                float balanceFactor = Math.Clamp(fBal * fTilt, 0.84f, 1.32f);

                var pts = new Vector3[NodesPerPath];
                var radii = new float[NodesPerPath];
                var mults = new float[NodesPerPath];

                for (int s = 0; s < NodesPerPath; s++)
                {
                    int idx = row + s;
                    pts[s] = nodes[idx];
                    float kEff = kEffAll[idx];
                    mults[s] = kEff;

                    float u = arcParams[idx];
                    float freeMask = 1.0f / (MathF.Max(1.0f, kEff) * MathF.Max(1.0f, kEff));
                    float endWin = SmoothStep((u - 0.55f) / 0.13f) * (1.0f - SmoothStep((u - 0.90f) / 0.08f));
                    float rFinal = MathF.Max(0.8f, rMurrayAll[idx] * (1.0f + freeMask * endWin * (balanceFactor - 1.0f)));
                    radii[s] = rFinal;
                }

                paths[i] = new VascularPath(
                    OutletIndex: i,
                    OutletAngleDeg: outletAnglesDeg[i],
                    IsUpperSide: isUpper[i],
                    Points: pts,
                    FluidRadii: radii,
                    LocalMultiplicity: mults,
                    PathLengthMm: pathLengths[i],
                    ResistanceIndex: resistances[i],
                    BalanceFactor: balanceFactor);
            }

            float meanSpread = ComputeMeanPairwiseSpread(paths);
            return new VascularNetwork(
                Paths: paths,
                UpperCount: upperCount,
                LowerCount: lowerCount,
                SplitAngleDeg: splitAngleDeg,
                UpperBoundaryPathIndex: upperBoundaryIdx,
                LowerBoundaryPathIndex: lowerBoundaryIdx,
                MeanPairwiseCenterlineSpreadMm: meanSpread);
        }

        /// <summary>
        /// Evaluates analytical packaging dimensions, 3D M5 bolt keep-out clearances, HV-harness obstacle clearances,
        /// and anti-sliver separation gaps from a parameter dictionary.
        /// </summary>
        public static StatorCoolingRingPackagingReport EvaluateAnalyticalPackaging(
            IReadOnlyDictionary<string, float> parameters,
            ProjectConfig? project = null)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            StatorCoolingRingParams p = StatorCoolingRingParams.Read(parameters);
            VascularNetwork network = BuildVascularNetwork(p, targets);
            return EvaluateAnalyticalPackaging(network, p, targets);
        }

        /// <summary>
        /// Evaluates analytical packaging dimensions, 3D M5 bolt keep-out clearances, HV-harness obstacle clearances,
        /// and anti-sliver separation gaps for an already-built <see cref="VascularNetwork"/>.
        /// </summary>
        public static StatorCoolingRingPackagingReport EvaluateAnalyticalPackaging(
            VascularNetwork network,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets)
        {
            ArgumentNullException.ThrowIfNull(network);
            ArgumentNullException.ThrowIfNull(targets);

            ObstacleCapsule[] obstacles = GetObstacleCapsules();

            float minInnerRadius = float.MaxValue;
            float maxOuterRadius = targets.BoltCircleRadiusMm + StatorCoolingRingConstants.DefaultBoltDomeOuterRadiusMm;
            float maxTotalHeight = 0f;
            float minBoltClearance = float.MaxValue;
            float minObstacleClearance = float.MaxValue;
            float minBranchRadius = float.MaxValue;

            var arcLengths = new float[network.Paths.Count][];
            var uParams = new float[network.Paths.Count][];

            for (int i = 0; i < network.Paths.Count; i++)
            {
                VascularPath path = network.Paths[i];
                int n = path.Points.Count;
                arcLengths[i] = new float[n];
                uParams[i] = new float[n];

                float totalLen = 0f;
                for (int s = 1; s < n; s++)
                {
                    totalLen += Vector3.Distance(path.Points[s], path.Points[s - 1]);
                    arcLengths[i][s] = totalLen;
                }
                float invTotal = totalLen > 1e-6f ? 1.0f / totalLen : 1.0f;
                for (int s = 0; s < n; s++)
                    uParams[i][s] = arcLengths[i][s] * invTotal;

                for (int s = 0; s < n; s++)
                {
                    Vector3 center = path.Points[s];
                    float rFluid = path.FluidRadii[s];
                    minBranchRadius = MathF.Min(minBranchRadius, rFluid);

                    if (center.Z < 0f)
                        continue;

                    Vector3 tangent = EstimateTangent(path.Points, s);
                    float wall = EvaluateLocalWallThicknessMm(rFluid, targets);
                    float endFade = EvaluateEndCircularFade(uParams[i][s]);
                    bool includeInnerOuterXY = center.X >= -76.0f;

                    float rTop = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, Vector3.UnitZ, tangent, endFade) + wall;
                    maxTotalHeight = MathF.Max(maxTotalHeight, center.Z + rTop);

                    if (includeInnerOuterXY)
                    {
                        float rCenterXY = MathF.Sqrt(center.X * center.X + center.Y * center.Y);
                        if (rCenterXY > 1e-4f)
                        {
                            Vector3 radialDirXY = new(center.X / rCenterXY, center.Y / rCenterXY, 0f);
                            float rOutward = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, radialDirXY, tangent, endFade) + wall;
                            float rInward = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, -radialDirXY, tangent, endFade) + wall;
                            minInnerRadius = MathF.Min(minInnerRadius, rCenterXY - rInward);
                            maxOuterRadius = MathF.Max(maxOuterRadius, rCenterXY + rOutward);
                        }
                    }

                    minBoltClearance = MathF.Min(minBoltClearance, ComputeBoltClearance(
                        center, tangent, rFluid, wall, p.CrossSectionAspect, endFade, targets));
                    minObstacleClearance = MathF.Min(minObstacleClearance, ComputeObstacleClearance(
                        center, tangent, rFluid, wall, p.CrossSectionAspect, endFade, obstacles));
                }
            }

            // Anti-sliver gap analysis between unbundled branch pairs
            int sliverGapCount = 0;
            float minSliverGap = float.MaxValue;
            float minPositiveUnbundledGap = 99.0f;

            int count = network.Paths.Count;
            for (int i = 0; i < count; i++)
            {
                VascularPath pi = network.Paths[i];
                int ni = pi.Points.Count;
                var bundledI = new bool[ni];

                for (int j = i + 1; j < count; j++)
                {
                    VascularPath pj = network.Paths[j];
                    int nj = pj.Points.Count;
                    var bundledJ = new bool[nj];
                    Array.Clear(bundledI, 0, ni);

                    bool isBoundaryPair = p.FarSideLoopAttraction > 0.05f &&
                        ((i == network.UpperBoundaryPathIndex && j == network.LowerBoundaryPathIndex) ||
                         (i == network.LowerBoundaryPathIndex && j == network.UpperBoundaryPathIndex));

                    for (int s = 0; s < ni; s++)
                    {
                        Vector3 ps = pi.Points[s];
                        float rs = pi.FluidRadii[s];
                        for (int t = 0; t < nj; t++)
                        {
                            float d = Vector3.Distance(ps, pj.Points[t]);
                            float fg = d - rs - pj.FluidRadii[t];
                            if (d <= 1.8f || fg < -0.3f)
                            {
                                bundledI[s] = true;
                                bundledJ[t] = true;
                            }
                        }
                    }

                    var exclI = ExpandBundleMask(bundledI, arcLengths[i], 18.0f);
                    var exclJ = ExpandBundleMask(bundledJ, arcLengths[j], 18.0f);

                    for (int s = 0; s < ni; s++)
                    {
                        if (exclI[s]) continue;
                        if (isBoundaryPair && uParams[i][s] >= 0.72f && uParams[i][s] <= 0.92f) continue;

                        Vector3 ps = pi.Points[s];
                        float rs = pi.FluidRadii[s];
                        for (int t = 0; t < nj; t++)
                        {
                            if (exclJ[t]) continue;
                            if (isBoundaryPair && uParams[j][t] >= 0.72f && uParams[j][t] <= 0.92f) continue;

                            float dist = Vector3.Distance(ps, pj.Points[t]);
                            float fluidGap = dist - rs - pj.FluidRadii[t];
                            if (fluidGap >= -0.3f && fluidGap < 2.0f)
                            {
                                sliverGapCount++;
                                minSliverGap = MathF.Min(minSliverGap, fluidGap);
                            }
                            else if (fluidGap >= 2.0f)
                            {
                                minPositiveUnbundledGap = MathF.Min(minPositiveUnbundledGap, fluidGap);
                            }
                        }
                    }
                }
            }

            float minUnbundledGap = sliverGapCount > 0 ? minSliverGap : minPositiveUnbundledGap;

            bool feasible =
                float.IsFinite(minInnerRadius) && minInnerRadius >= targets.MinInnerRadiusMm &&
                float.IsFinite(maxOuterRadius) && maxOuterRadius <= targets.MaxOuterRadiusMm &&
                float.IsFinite(maxTotalHeight) && maxTotalHeight <= targets.MaxTotalHeightMm &&
                float.IsFinite(minBoltClearance) && minBoltClearance >= targets.MinRequiredBoltClearanceMm &&
                float.IsFinite(minObstacleClearance) && minObstacleClearance >= targets.MinRequiredObstacleClearanceMm &&
                float.IsFinite(minBranchRadius) && minBranchRadius >= targets.MinBranchRadiusMm;

            return new StatorCoolingRingPackagingReport(
                InnerRadiusMm: minInnerRadius,
                OuterRadiusMm: maxOuterRadius,
                TotalHeightMm: maxTotalHeight,
                MinBoltClearanceMm: minBoltClearance,
                MinObstacleClearanceMm: minObstacleClearance,
                MinBranchRadiusMm: minBranchRadius,
                MinUnbundledGapMm: minUnbundledGap,
                SliverGapCount: sliverGapCount,
                IsFeasible: feasible);
        }

        /// <summary>
        /// Builds collision-checked hourglass inter-tube stiffening rib segments connecting separated vascular branches.
        /// </summary>
        public static IReadOnlyList<RibSegment> BuildInterTubeRibSegments(
            VascularNetwork network,
            StatorCoolingRingTargets targets)
        {
            ArgumentNullException.ThrowIfNull(network);
            ArgumentNullException.ThrowIfNull(targets);

            ObstacleCapsule[] obstacles = GetObstacleCapsules();
            var sampledNodes = new List<(int PathIdx, int NodeIdx, Vector3 Pos, float OuterR)>();

            for (int i = 0; i < network.Paths.Count; i++)
            {
                VascularPath path = network.Paths[i];
                float totalLen = MathF.Max(1f, path.PathLengthMm);
                float accum = 0f;
                float lastSampleArc = -100f;

                for (int s = 1; s < path.Points.Count - 1; s++)
                {
                    accum += Vector3.Distance(path.Points[s], path.Points[s - 1]);
                    float u = accum / totalLen;
                    if (u < 0.18f || u > 0.85f) continue;
                    if (path.Points[s].Z < 9.0f) continue;
                    if (accum - lastSampleArc < 12.0f) continue;

                    float outerR = path.FluidRadii[s] + EvaluateLocalWallThicknessMm(path.FluidRadii[s], targets);
                    sampledNodes.Add((i, s, path.Points[s], outerR));
                    lastSampleArc = accum;
                }
            }

            var candidates = new List<(float WallGap, int PathA, int PathB, int NodeA, int NodeB, Vector3 A, Vector3 B)>();
            for (int a = 0; a < sampledNodes.Count; a++)
            {
                var na = sampledNodes[a];
                for (int b = a + 1; b < sampledNodes.Count; b++)
                {
                    var nb = sampledNodes[b];
                    if (na.PathIdx == nb.PathIdx) continue;
                    if (MathF.Abs(na.Pos.Z - nb.Pos.Z) > 14.0f) continue;

                    float dist = Vector3.Distance(na.Pos, nb.Pos);
                    float wallGap = dist - na.OuterR - nb.OuterR;
                    if (wallGap < 1.5f || wallGap > 18.0f) continue;

                    candidates.Add((wallGap, na.PathIdx, nb.PathIdx, na.NodeIdx, nb.NodeIdx, na.Pos, nb.Pos));
                }
            }

            candidates.Sort((x, y) =>
            {
                int cmp = x.WallGap.CompareTo(y.WallGap);
                if (cmp != 0) return cmp;
                cmp = x.PathA.CompareTo(y.PathA);
                if (cmp != 0) return cmp;
                cmp = x.PathB.CompareTo(y.PathB);
                if (cmp != 0) return cmp;
                cmp = x.NodeA.CompareTo(y.NodeA);
                return cmp != 0 ? cmp : x.NodeB.CompareTo(y.NodeB);
            });

            var ribs = new List<RibSegment>(40);
            foreach (var cand in candidates)
            {
                if (ribs.Count >= 40) break;

                bool tooCloseToExisting = false;
                foreach (RibSegment existing in ribs)
                {
                    bool sameDir = Vector3.DistanceSquared(cand.A, existing.Start) < 100f &&
                                   Vector3.DistanceSquared(cand.B, existing.End) < 100f;
                    bool revDir = Vector3.DistanceSquared(cand.A, existing.End) < 100f &&
                                  Vector3.DistanceSquared(cand.B, existing.Start) < 100f;
                    if (sameDir || revDir)
                    {
                        tooCloseToExisting = true;
                        break;
                    }
                }
                if (tooCloseToExisting) continue;

                var seg = new RibSegment(cand.A, cand.B, 2.6f, 1.7f, 2.6f);
                if (IsSegmentCollisionFree(seg.Start, seg.End, seg.StartRadiusMm, seg.MidRadiusMm, seg.EndRadiusMm,
                        targets, obstacles, minZAllowed: 6.0f, minBoltClearanceExtra: 1.5f))
                {
                    ribs.Add(seg);
                }
            }

            return ribs;
        }

        /// <summary>
        /// Builds collision-checked adaptive load-path ribs from the 4 M5 DIN-912 domes and up to 6 flange anchors
        /// up to nearby elevated vascular nodes.
        /// </summary>
        public static IReadOnlyList<RibSegment> BuildAdaptiveSupportRibCurves(
            VascularNetwork network,
            StatorCoolingRingTargets targets)
        {
            ArgumentNullException.ThrowIfNull(network);
            ArgumentNullException.ThrowIfNull(targets);

            ObstacleCapsule[] obstacles = GetObstacleCapsules();
            var ribs = new List<RibSegment>(10);

            foreach (float deg in StatorCoolingRingConstants.BoltAnglesDeg)
            {
                float rad = deg * DegToRad;
                Vector3 start = new(74.5f * MathF.Cos(rad), 74.5f * MathF.Sin(rad), 6.5f);

                RibSegment? bestRib = FindBestCollisionFreeRibToNetwork(
                    start,
                    network,
                    targets,
                    obstacles,
                    startR: 2.8f,
                    midR: 2.5f,
                    endR: 2.2f,
                    minTargetZ: 12.0f,
                    maxDistance: 36.0f,
                    minZAllowed: 3.0f,
                    minBoltClearanceExtra: 0.0f);

                if (bestRib.HasValue)
                    ribs.Add(bestRib.Value);
            }

            float[] flangeAnchorAnglesDeg = { 30.0f, 120.0f, 150.0f, 210.0f, 270.0f, 330.0f };
            int flangeCount = 0;
            foreach (float deg in flangeAnchorAnglesDeg)
            {
                if (flangeCount >= 6) break;
                float rad = deg * DegToRad;
                Vector3 start = new(targets.PitchCircleRadiusMm * MathF.Cos(rad), targets.PitchCircleRadiusMm * MathF.Sin(rad), 5.5f);

                RibSegment? bestRib = FindBestCollisionFreeRibToNetwork(
                    start,
                    network,
                    targets,
                    obstacles,
                    startR: 2.5f,
                    midR: 1.9f,
                    endR: 2.2f,
                    minTargetZ: 20.0f,
                    maxDistance: 34.0f,
                    minZAllowed: 2.5f,
                    minBoltClearanceExtra: 1.5f);

                if (bestRib.HasValue)
                {
                    ribs.Add(bestRib.Value);
                    flangeCount++;
                }
            }

            return ribs;
        }

        private static RibSegment? FindBestCollisionFreeRibToNetwork(
            Vector3 start,
            VascularNetwork network,
            StatorCoolingRingTargets targets,
            ObstacleCapsule[] obstacles,
            float startR,
            float midR,
            float endR,
            float minTargetZ,
            float maxDistance,
            float minZAllowed,
            float minBoltClearanceExtra)
        {
            RibSegment? best = null;
            float bestDistSq = maxDistance * maxDistance;

            foreach (VascularPath path in network.Paths)
            {
                int n = path.Points.Count;
                for (int s = 6; s < n - 4; s++)
                {
                    Vector3 pt = path.Points[s];
                    if (pt.Z < minTargetZ) continue;
                    float dSq = Vector3.DistanceSquared(start, pt);
                    if (dSq >= bestDistSq || dSq < 4.0f) continue;

                    if (IsSegmentCollisionFree(start, pt, startR, midR, endR, targets, obstacles, minZAllowed, minBoltClearanceExtra))
                    {
                        bestDistSq = dSq;
                        best = new RibSegment(start, pt, startR, midR, endR);
                    }
                }
            }

            return best;
        }

        private static bool IsSegmentCollisionFree(
            Vector3 start,
            Vector3 end,
            float startR,
            float midR,
            float endR,
            StatorCoolingRingTargets targets,
            ObstacleCapsule[] obstacles,
            float minZAllowed,
            float minBoltClearanceExtra)
        {
            const int Samples = 9;
            float requiredBoltDist = targets.BoltKeepOutRadiusMm + minBoltClearanceExtra;

            for (int k = 0; k < Samples; k++)
            {
                float t = k / (float)(Samples - 1);
                Vector3 pt = Vector3.Lerp(start, end, t);
                float rRib = t <= 0.5f
                    ? startR + (midR - startR) * (2.0f * t)
                    : midR + (endR - midR) * (2.0f * (t - 0.5f));

                float rXY = MathF.Sqrt(pt.X * pt.X + pt.Y * pt.Y);
                if (rXY - rRib < targets.MinInnerRadiusMm + 0.5f) return false;
                if (rXY + rRib > targets.MaxOuterRadiusMm - 0.5f) return false;
                if (pt.Z + rRib > targets.MaxTotalHeightMm - 0.5f) return false;
                if (pt.Z - rRib < minZAllowed) return false;

                foreach (ObstacleCapsule obs in obstacles)
                {
                    float distObs = DistancePointToSegment(pt, obs.Start, obs.End);
                    if (distObs - obs.RadiusMm - rRib < 2.0f)
                        return false;
                }

                foreach (float boltDeg in StatorCoolingRingConstants.BoltAnglesDeg)
                {
                    float bRad = boltDeg * DegToRad;
                    float bx = targets.BoltCircleRadiusMm * MathF.Cos(bRad);
                    float by = targets.BoltCircleRadiusMm * MathF.Sin(bRad);
                    Vector3 axisPt = new(bx, by, Math.Clamp(pt.Z, 0f, targets.MaxTotalHeightMm));
                    float distBolt = Vector3.Distance(pt, axisPt);
                    if (distBolt - rRib < requiredBoltDist)
                        return false;
                }
            }

            return true;
        }

        private static void InitializeGeodesicInterior(
            Vector3[] nodes,
            int pathIdx,
            bool isUpper,
            float thetaNormDeg,
            StatorCoolingRingParams p)
        {
            int row = pathIdx * NodesPerPath;
            Vector3 pStart = nodes[row + 2];
            Vector3 pEnd = nodes[row + NodesPerPath - 3];

            Vector2 a = new(pStart.X, pStart.Y);
            Vector2 b = new(pEnd.X, pEnd.Y);
            float rA = a.Length();
            float rB = b.Length();

            float alphaA = MathF.Atan2(a.Y, a.X);
            if (isUpper && alphaA < 0f) alphaA += 2f * MathF.PI;
            if (!isUpper && alphaA > 0f) alphaA -= 2f * MathF.PI;

            float alphaB = thetaNormDeg * DegToRad;
            if (isUpper && alphaB > alphaA) alphaB -= 2f * MathF.PI;
            if (!isUpper && alphaB < alphaA) alphaB += 2f * MathF.PI;

            float deltaA = MathF.Acos(Math.Clamp(RotorSeedRadiusMm / MathF.Max(RotorSeedRadiusMm + 0.1f, rA), -1f, 1f));
            float deltaB = MathF.Acos(Math.Clamp(RotorSeedRadiusMm / MathF.Max(RotorSeedRadiusMm + 0.1f, rB), -1f, 1f));

            float thetaTA = isUpper ? alphaA - deltaA : alphaA + deltaA;
            float thetaTB = isUpper ? alphaB + deltaB : alphaB - deltaB;

            bool needsArc = isUpper ? (thetaTA > thetaTB) : (thetaTA < thetaTB);

            Vector2 tA = new(RotorSeedRadiusMm * MathF.Cos(thetaTA), RotorSeedRadiusMm * MathF.Sin(thetaTA));
            Vector2 tB = new(RotorSeedRadiusMm * MathF.Cos(thetaTB), RotorSeedRadiusMm * MathF.Sin(thetaTB));

            float seg1Len = needsArc ? Vector2.Distance(a, tA) : Vector2.Distance(a, b);
            float arcLen = needsArc ? RotorSeedRadiusMm * MathF.Abs(thetaTB - thetaTA) : 0f;
            float seg2Len = needsArc ? Vector2.Distance(tB, b) : 0f;
            float total2DLen = MathF.Max(1e-4f, seg1Len + arcLen + seg2Len);

            int span = (NodesPerPath - FixedEndNodes) - (FixedStartNodes - 1);
            for (int s = FixedStartNodes; s < NodesPerPath - FixedEndNodes; s++)
            {
                float u = (s - (FixedStartNodes - 1)) / (float)span;
                float distAlong = u * total2DLen;

                Vector2 xy;
                if (!needsArc)
                {
                    xy = Vector2.Lerp(a, b, u);
                }
                else if (distAlong <= seg1Len)
                {
                    float frac = seg1Len > 1e-5f ? distAlong / seg1Len : 0f;
                    xy = Vector2.Lerp(a, tA, frac);
                }
                else if (distAlong <= seg1Len + arcLen)
                {
                    float frac = arcLen > 1e-5f ? (distAlong - seg1Len) / arcLen : 0f;
                    float ang = thetaTA + frac * (thetaTB - thetaTA);
                    xy = new Vector2(RotorSeedRadiusMm * MathF.Cos(ang), RotorSeedRadiusMm * MathF.Sin(ang));
                }
                else
                {
                    float frac = seg2Len > 1e-5f ? (distAlong - seg1Len - arcLen) / seg2Len : 1f;
                    xy = Vector2.Lerp(tB, b, frac);
                }

                float thetaXY = MathF.Atan2(xy.Y, xy.X);
                float zTarget = p.ElevationBaseMm + p.ElevationTiltMm * 0.5f * (1.0f + MathF.Cos(thetaXY));
                float wIn = SmoothStep(u / 0.18f);
                float wOut = SmoothStep((u - 0.80f) / 0.20f);
                float z = (1.0f - wOut) * (pStart.Z + wIn * (zTarget - pStart.Z)) + wOut * pEnd.Z;

                nodes[row + s] = new Vector3(xy.X, xy.Y, z);
            }
        }

        private static void ApplyLaplacianSmoothPass(Vector3[] nodes, Vector3[] scratch)
        {
            Array.Copy(nodes, scratch, TotalNodes);
            for (int i = 0; i < PathCount; i++)
            {
                int row = i * NodesPerPath;
                for (int s = FixedStartNodes; s < NodesPerPath - FixedEndNodes; s++)
                {
                    int idx = row + s;
                    scratch[idx] = 0.20f * nodes[idx - 1] + 0.60f * nodes[idx] + 0.20f * nodes[idx + 1];
                }
            }
            Array.Copy(scratch, nodes, TotalNodes);
        }

        private static Vector3 ComputeSdfForce(
            Vector3 pos,
            Vector3 tangent,
            int nodeIdx,
            float rFluid,
            float wall,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets,
            float endFade,
            Vector2[] boltPositionsXY,
            ObstacleCapsule[] obstacles)
        {
            Vector3 f = Vector3.Zero;
            const float PreWarn = 3.0f;
            const float InnerPreWarn = 3.5f;
            const float InnerBoundaryMargin = 3.20f;
            const float OuterBoundaryMargin = 1.6f;
            const float KeepOutMargin = 1.6f;

            float rXY = MathF.Sqrt(pos.X * pos.X + pos.Y * pos.Y);
            Vector3 radialXY = rXY > 1e-5f ? new Vector3(pos.X / rXY, pos.Y / rXY, 0f) : Vector3.UnitX;
            float maxTeardropApprox = rFluid * 1.45f + wall;

            // Rotor inner bore keep-out (high tangential glide stiffness to maintain >= 46 mm bore across 14D box)
            if (rXY < targets.MinInnerRadiusMm + maxTeardropApprox + InnerBoundaryMargin + InnerPreWarn)
            {
                float rInward = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, -radialXY, tangent, endFade) + wall;
                float minCenterR = targets.MinInnerRadiusMm + rInward + InnerBoundaryMargin;
                if (rXY < minCenterR + InnerPreWarn)
                    f += radialXY * SoftQuadraticRepulsion(minCenterR - rXY, InnerPreWarn, stiffness: 10.5f);
            }

            // Outer housing boundary (exclude straight inlet neck transition at X < -76 mm)
            if (pos.X >= -76.0f && rXY > targets.MaxOuterRadiusMm - maxTeardropApprox - OuterBoundaryMargin - PreWarn)
            {
                float rOutward = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, radialXY, tangent, endFade) + wall;
                float maxCenterR = targets.MaxOuterRadiusMm - rOutward - OuterBoundaryMargin;
                if (rXY > maxCenterR - PreWarn)
                    f -= radialXY * SoftQuadraticRepulsion(rXY - maxCenterR, PreWarn, stiffness: 4.5f);
            }

            // Top housing cover
            if (pos.Z > targets.MaxTotalHeightMm - maxTeardropApprox - OuterBoundaryMargin - PreWarn)
            {
                float rTop = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, Vector3.UnitZ, tangent, endFade) + wall;
                float maxCenterZ = targets.MaxTotalHeightMm - rTop - OuterBoundaryMargin;
                if (pos.Z > maxCenterZ - PreWarn)
                    f.Z -= SoftQuadraticRepulsion(pos.Z - maxCenterZ, PreWarn, stiffness: 3.0f);
            }

            // Base flange floor (smoothly faded over last 8 free nodes s >= 37 for nozzle descent)
            if (pos.Z < StatorCoolingRingConstants.DefaultFlangeHeightMm + maxTeardropApprox + 2.5f)
            {
                float flangeFade = nodeIdx >= 37 ? 1.0f - SmoothStep((nodeIdx - 37.0f) / 8.0f) : 1.0f;
                if (flangeFade > 1e-3f)
                {
                    float rBottom = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, -Vector3.UnitZ, tangent, endFade) + wall;
                    float minCenterZ = StatorCoolingRingConstants.DefaultFlangeHeightMm + rBottom + 0.5f;
                    if (pos.Z < minCenterZ + 2.0f)
                        f.Z += flangeFade * SoftQuadraticRepulsion(minCenterZ - pos.Z, 2.0f, stiffness: 1.8f);
                }
            }

            // 4x M5 vertical tool shafts (Z = 0..MaxTotalHeightMm)
            float clampedZ = Math.Clamp(pos.Z, 0f, targets.MaxTotalHeightMm);
            float maxBoltCheck = targets.BoltKeepOutRadiusMm + targets.MinRequiredBoltClearanceMm + KeepOutMargin + maxTeardropApprox + PreWarn;
            float maxBoltCheckSq = maxBoltCheck * maxBoltCheck;
            for (int b = 0; b < boltPositionsXY.Length; b++)
            {
                Vector3 boltPt = new(boltPositionsXY[b].X, boltPositionsXY[b].Y, clampedZ);
                Vector3 away = pos - boltPt;
                float distSq = away.LengthSquared();
                if (distSq >= maxBoltCheckSq) continue;

                float dist = MathF.Sqrt(distSq);
                Vector3 unitAway = dist > 1e-5f ? away / dist : -radialXY;
                float rDir = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, -unitAway, tangent, endFade) + wall;
                float reqDist = targets.BoltKeepOutRadiusMm + targets.MinRequiredBoltClearanceMm + KeepOutMargin + rDir;
                if (dist < reqDist + PreWarn)
                    f += unitAway * SoftQuadraticRepulsion(reqDist - dist, PreWarn, stiffness: 3.0f);
            }

            // 4x HV & sensor capsules + ObstacleOverpassBias near-field 3D drift
            float nearestObsClear = float.MaxValue;
            float obsSkipThresh = 22.0f + 4.5f + maxTeardropApprox;
            float obsSkipThreshSq = obsSkipThresh * obsSkipThresh;
            for (int o = 0; o < obstacles.Length; o++)
            {
                ObstacleCapsule obs = obstacles[o];
                Vector3 closest = ClosestPointOnSegment(pos, obs.Start, obs.End);
                Vector3 away = pos - closest;
                float distSq = away.LengthSquared();
                if (distSq >= obsSkipThreshSq) continue;

                float dist = MathF.Sqrt(distSq);
                Vector3 unitAway = dist > 1e-5f ? away / dist : Vector3.UnitZ;
                float rDir = EvaluateTeardropCrossSectionRadius(rFluid, p.CrossSectionAspect, -unitAway, tangent, endFade) + wall;
                float reqDist = obs.RadiusMm + targets.MinRequiredObstacleClearanceMm + KeepOutMargin + rDir;
                float approxClear = dist - obs.RadiusMm - rDir;
                if (approxClear < nearestObsClear)
                    nearestObsClear = approxClear;

                if (dist < reqDist + PreWarn)
                    f += unitAway * SoftQuadraticRepulsion(reqDist - dist, PreWarn, stiffness: 3.0f);
            }

            if (nearestObsClear < 22.0f)
            {
                float wNear = 1.0f - Math.Clamp(nearestObsClear / 22.0f, 0f, 1f);
                if (p.ObstacleOverpassBias >= 0f)
                {
                    f.Z += 0.55f * wNear * p.ObstacleOverpassBias;
                }
                else
                {
                    float negBias = -p.ObstacleOverpassBias;
                    f.Z -= 0.30f * wNear * negBias;
                    float sideSign = pos.X >= 3.3f ? 1.0f : -1.0f;
                    f.X += 0.35f * wNear * negBias * sideSign;
                }
            }

            return f;
        }

        private static Vector3 SideSpreadNormal(Vector3 pos, int pathI, int pathJ)
        {
            float rXY = MathF.Sqrt(pos.X * pos.X + pos.Y * pos.Y);
            Vector3 radial = rXY > 1e-5f ? new Vector3(pos.X / rXY, pos.Y / rXY, 0f) : Vector3.UnitX;
            return pathI < pathJ ? Vector3.UnitZ + 0.25f * radial : -Vector3.UnitZ - 0.25f * radial;
        }

        private static float SoftQuadraticRepulsion(float penetration, float preWarn, float stiffness)
        {
            if (penetration >= 0f)
                return stiffness * (0.18f * preWarn + penetration);
            float t = 1.0f + penetration / preWarn;
            if (t <= 0f) return 0f;
            return stiffness * 0.18f * preWarn * t * t;
        }

        private static void UpdateGeometryCaches(
            Vector3[] nodes,
            Vector3[] tangents,
            float[] arcParams,
            float[] arcDistances,
            float[] rEst,
            Vector3[] chunkCenters,
            float[] chunkRadii,
            Vector3[] pathMin,
            Vector3[] pathMax,
            StatorCoolingRingParams p,
            StatorCoolingRingTargets targets,
            int upperBoundaryIdx,
            int lowerBoundaryIdx,
            bool updateRadii)
        {
            for (int i = 0; i < PathCount; i++)
            {
                int row = i * NodesPerPath;
                float totalLen = 0f;
                arcDistances[row] = 0f;
                for (int s = 1; s < NodesPerPath; s++)
                {
                    totalLen += Vector3.Distance(nodes[row + s], nodes[row + s - 1]);
                    arcDistances[row + s] = totalLen;
                }
                float invTotal = totalLen > 1e-6f ? 1.0f / totalLen : 1.0f;
                for (int s = 0; s < NodesPerPath; s++)
                {
                    int idx = row + s;
                    arcParams[idx] = arcDistances[idx] * invTotal;

                    Vector3 d = s == 0
                        ? nodes[row + 1] - nodes[row]
                        : s == NodesPerPath - 1
                            ? nodes[idx] - nodes[idx - 1]
                            : nodes[idx + 1] - nodes[idx - 1];
                    tangents[idx] = d.LengthSquared() > 1e-10f ? Vector3.Normalize(d) : Vector3.UnitZ;
                }
            }

            if (!updateRadii) return;

            for (int i = 0; i < PathCount; i++)
            {
                int row = i * NodesPerPath;
                for (int s = 0; s < NodesPerPath; s++)
                {
                    int idx = row + s;
                    float u = arcParams[idx];
                    float kEst = ComputeNodeMultiplicity(
                        i, s, u, nodes, tangents, chunkCenters, chunkRadii, pathMin, pathMax, p, upperBoundaryIdx, lowerBoundaryIdx);
                    float r = targets.OutletRadiusMm * p.BaseChannelScale * MathF.Pow(MathF.Max(1.0f, kEst), 1.0f / p.MurrayExponent);
                    if (u < 0.16f)
                    {
                        float tIn = SmoothStep(u / 0.16f);
                        r = targets.InletRadiusMm + tIn * (r - targets.InletRadiusMm);
                    }
                    if (u > 0.92f)
                    {
                        float tOut = SmoothStep((u - 0.92f) / 0.08f);
                        r += tOut * (targets.OutletRadiusMm - r);
                    }
                    rEst[idx] = r;
                }
            }
        }

        private static void ResampleInteriorArcLength(Vector3[] nodes, int pathIdx)
        {
            const int StartIdx = FixedStartNodes - 1; // 2
            const int EndIdx = NodesPerPath - FixedEndNodes; // 45
            int count = EndIdx - StartIdx + 1; // 44 points
            int row = pathIdx * NodesPerPath;

            Span<float> cumArc = stackalloc float[count];
            Span<Vector3> orig = stackalloc Vector3[count];
            orig[0] = nodes[row + StartIdx];
            cumArc[0] = 0f;

            for (int k = 1; k < count; k++)
            {
                orig[k] = nodes[row + StartIdx + k];
                cumArc[k] = cumArc[k - 1] + Vector3.Distance(orig[k], orig[k - 1]);
            }

            float total = cumArc[count - 1];
            if (total < 1e-4f) return;

            int seg = 0;
            for (int k = 1; k < count - 1; k++)
            {
                float target = total * (k / (float)(count - 1));
                while (seg < count - 2 && cumArc[seg + 1] < target)
                    seg++;
                float span = cumArc[seg + 1] - cumArc[seg];
                float frac = span > 1e-6f ? (target - cumArc[seg]) / span : 0f;
                nodes[row + StartIdx + k] = Vector3.Lerp(orig[seg], orig[seg + 1], frac);
            }
        }

        private static void UpdateChunkBounds(
            Vector3[] nodes,
            Vector3[] chunkCenters,
            float[] chunkRadii,
            Vector3[] pathMin,
            Vector3[] pathMax)
        {
            for (int j = 0; j < PathCount; j++)
            {
                int row = j * NodesPerPath;
                int cRow = j * ChunkCount;
                Vector3 pMin = nodes[row];
                Vector3 pMax = nodes[row];
                for (int s = 1; s < NodesPerPath; s++)
                {
                    Vector3 v = nodes[row + s];
                    pMin = Vector3.Min(pMin, v);
                    pMax = Vector3.Max(pMax, v);
                }
                pathMin[j] = pMin;
                pathMax[j] = pMax;

                for (int c = 0; c < ChunkCount; c++)
                {
                    int segStart = c * ChunkSize;
                    int segEnd = Math.Min(SegmentsPerPath, segStart + ChunkSize);
                    Vector3 sum = Vector3.Zero;
                    int ptCount = segEnd - segStart + 1;
                    for (int k = segStart; k <= segEnd; k++)
                        sum += nodes[row + k];
                    Vector3 center = sum / ptCount;
                    float maxDistSq = 0f;
                    for (int k = segStart; k <= segEnd; k++)
                    {
                        float dSq = Vector3.DistanceSquared(center, nodes[row + k]);
                        if (dSq > maxDistSq) maxDistSq = dSq;
                    }
                    chunkCenters[cRow + c] = center;
                    chunkRadii[cRow + c] = MathF.Sqrt(maxDistSq);
                }
            }
        }

        private static bool FindClosestOnPath(
            Vector3 query,
            int otherPathIdx,
            Vector3[] nodes,
            Vector3[] tangents,
            float[] arcParams,
            float[] rEst,
            Vector3[] chunkCenters,
            float[] chunkRadii,
            Vector3[] pathMin,
            Vector3[] pathMax,
            float maxSearchDist,
            out Vector3 closestPt,
            out Vector3 closestTan,
            out float closestU,
            out float closestR,
            out float bestDistSq)
        {
            bestDistSq = maxSearchDist * maxSearchDist;
            closestPt = Vector3.Zero;
            closestTan = Vector3.UnitX;
            closestU = 0f;
            closestR = 2.0f;

            Vector3 mn = pathMin[otherPathIdx];
            Vector3 mx = pathMax[otherPathIdx];
            float paddedSearch = maxSearchDist + 3.5f;
            if (query.X < mn.X - paddedSearch || query.X > mx.X + paddedSearch ||
                query.Y < mn.Y - paddedSearch || query.Y > mx.Y + paddedSearch ||
                query.Z < mn.Z - paddedSearch || query.Z > mx.Z + paddedSearch)
                return false;

            int row = otherPathIdx * NodesPerPath;
            int cRow = otherPathIdx * ChunkCount;
            int bestIdx = -1;
            float bestT = 0f;

            for (int c = 0; c < ChunkCount; c++)
            {
                float maxChunkDist = paddedSearch + chunkRadii[cRow + c];
                if (Vector3.DistanceSquared(query, chunkCenters[cRow + c]) > maxChunkDist * maxChunkDist)
                    continue;

                int segStart = c * ChunkSize;
                int segEnd = Math.Min(SegmentsPerPath, segStart + ChunkSize);
                for (int k = segStart; k < segEnd; k++)
                {
                    int idx = row + k;
                    Vector3 a = nodes[idx];
                    Vector3 b = nodes[idx + 1];
                    float abX = b.X - a.X;
                    float abY = b.Y - a.Y;
                    float abZ = b.Z - a.Z;
                    float abLenSq = abX * abX + abY * abY + abZ * abZ;
                    float t = abLenSq > 1e-8f
                        ? Math.Clamp(((query.X - a.X) * abX + (query.Y - a.Y) * abY + (query.Z - a.Z) * abZ) / abLenSq, 0f, 1f)
                        : 0f;
                    float dx = query.X - (a.X + t * abX);
                    float dy = query.Y - (a.Y + t * abY);
                    float dz = query.Z - (a.Z + t * abZ);
                    float dSq = dx * dx + dy * dy + dz * dz;
                    if (dSq < bestDistSq)
                    {
                        bestDistSq = dSq;
                        bestIdx = idx;
                        bestT = t;
                    }
                }
            }

            if (bestIdx < 0) return false;

            closestPt = Vector3.Lerp(nodes[bestIdx], nodes[bestIdx + 1], bestT);
            closestTan = tangents[bestIdx];
            closestU = arcParams[bestIdx] + bestT * (arcParams[bestIdx + 1] - arcParams[bestIdx]);
            closestR = rEst[bestIdx] + bestT * (rEst[bestIdx + 1] - rEst[bestIdx]);
            return true;
        }

        private static float ComputeNodeMultiplicity(
            int pathIdx,
            int nodeIdx,
            float u,
            Vector3[] nodes,
            Vector3[] tangents,
            Vector3[] chunkCenters,
            float[] chunkRadii,
            Vector3[] pathMin,
            Vector3[] pathMax,
            StatorCoolingRingParams p,
            int upperBoundaryIdx,
            int lowerBoundaryIdx)
        {
            int selfIdx = pathIdx * NodesPerPath + nodeIdx;
            Vector3 pos = nodes[selfIdx];
            Vector3 tan = tangents[selfIdx];
            const float TwoSigmaSq = 2.0f * MurraySigmaMm * MurraySigmaMm;
            const float MaxGaussRange = 9.5f;

            float kEff = 1.0f;
            float bridgeBonus = 0f;
            bool inBridgeAntiParallel = false;

            for (int j = 0; j < PathCount; j++)
            {
                if (j == pathIdx) continue;

                Vector3 mn = pathMin[j];
                Vector3 mx = pathMax[j];
                if (pos.X < mn.X - MaxGaussRange || pos.X > mx.X + MaxGaussRange ||
                    pos.Y < mn.Y - MaxGaussRange || pos.Y > mx.Y + MaxGaussRange ||
                    pos.Z < mn.Z - MaxGaussRange || pos.Z > mx.Z + MaxGaussRange)
                    continue;

                int rowJ = j * NodesPerPath;
                int cRowJ = j * ChunkCount;
                float bestDistSq = MaxGaussRange * MaxGaussRange;
                int bestIdx = -1;
                float bestT = 0f;

                for (int c = 0; c < ChunkCount; c++)
                {
                    float maxChunkDist = MaxGaussRange + chunkRadii[cRowJ + c];
                    if (Vector3.DistanceSquared(pos, chunkCenters[cRowJ + c]) > maxChunkDist * maxChunkDist)
                        continue;

                    int segStart = c * ChunkSize;
                    int segEnd = Math.Min(SegmentsPerPath, segStart + ChunkSize);
                    for (int k = segStart; k < segEnd; k++)
                    {
                        int idx = rowJ + k;
                        Vector3 a = nodes[idx];
                        Vector3 b = nodes[idx + 1];
                        float abX = b.X - a.X;
                        float abY = b.Y - a.Y;
                        float abZ = b.Z - a.Z;
                        float abLenSq = abX * abX + abY * abY + abZ * abZ;
                        float t = abLenSq > 1e-8f
                            ? Math.Clamp(((pos.X - a.X) * abX + (pos.Y - a.Y) * abY + (pos.Z - a.Z) * abZ) / abLenSq, 0f, 1f)
                            : 0f;
                        float dx = pos.X - (a.X + t * abX);
                        float dy = pos.Y - (a.Y + t * abY);
                        float dz = pos.Z - (a.Z + t * abZ);
                        float dSq = dx * dx + dy * dy + dz * dz;
                        if (dSq < bestDistSq)
                        {
                            bestDistSq = dSq;
                            bestIdx = idx;
                            bestT = t;
                        }
                    }
                }

                if (bestIdx < 0) continue;

                Vector3 tanInterp = Vector3.Lerp(tangents[bestIdx], tangents[bestIdx + 1], bestT);
                Vector3 bestTan = tanInterp.LengthSquared() > 1e-8f ? Vector3.Normalize(tanInterp) : tangents[bestIdx];

                float cDir = Vector3.Dot(tan, bestTan);
                float dirWeight = SmoothStep((cDir - 0.15f) / 0.45f);
                float gauss = MathF.Exp(-bestDistSq / TwoSigmaSq);
                kEff += dirWeight * gauss;

                bool isBoundaryPair = p.FarSideLoopAttraction > 0.05f &&
                    ((pathIdx == upperBoundaryIdx && j == lowerBoundaryIdx) ||
                     (pathIdx == lowerBoundaryIdx && j == upperBoundaryIdx));

                if (isBoundaryPair && u >= 0.72f && u <= 0.92f)
                {
                    float win = BridgeWindowWeight(u);
                    bridgeBonus = MathF.Max(bridgeBonus, 0.25f * win * gauss);
                    if (cDir < 0.30f && gauss > 0.10f)
                        inBridgeAntiParallel = true;
                }
            }

            if (bridgeBonus > 0f)
            {
                kEff += bridgeBonus;
                if (inBridgeAntiParallel)
                    kEff = MathF.Min(1.25f, kEff);
            }

            return kEff;
        }

        private static float ComputeMeanPairwiseSpread(IReadOnlyList<VascularPath> paths)
        {
            double sum = 0.0;
            int count = 0;
            int n = paths.Count;
            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    for (int s = 8; s <= 38; s++)
                    {
                        Vector3 p = paths[i].Points[s];
                        float bestSq = float.MaxValue;
                        for (int t = 0; t < paths[j].Points.Count - 1; t++)
                        {
                            Vector3 q = ClosestPointOnSegment(p, paths[j].Points[t], paths[j].Points[t + 1]);
                            float dSq = Vector3.DistanceSquared(p, q);
                            if (dSq < bestSq) bestSq = dSq;
                        }
                        sum += MathF.Sqrt(bestSq);
                        count++;
                    }
                }
            }
            return count > 0 ? (float)(sum / count) : 0f;
        }

        private static bool[] ExpandBundleMask(bool[] bundled, float[] arcs, float marginMm)
        {
            int n = bundled.Length;
            var expanded = new bool[n];
            for (int s = 0; s < n; s++)
            {
                if (!bundled[s]) continue;
                float a = arcs[s];
                for (int k = 0; k < n; k++)
                {
                    if (MathF.Abs(arcs[k] - a) <= marginMm)
                        expanded[k] = true;
                }
            }
            return expanded;
        }

        private static float ComputeBoltClearance(
            Vector3 center,
            Vector3 localZ,
            float rFluid,
            float wall,
            float aspect,
            float endCircularFade,
            StatorCoolingRingTargets targets)
        {
            float minClearance = float.MaxValue;
            foreach (float deg in StatorCoolingRingConstants.BoltAnglesDeg)
            {
                float rad = deg * DegToRad;
                float bx = targets.BoltCircleRadiusMm * MathF.Cos(rad);
                float by = targets.BoltCircleRadiusMm * MathF.Sin(rad);

                float clampedZ = Math.Clamp(center.Z, 0f, targets.MaxTotalHeightMm);
                float dx = bx - center.X;
                float dy = by - center.Y;
                float dxy = MathF.Sqrt(dx * dx + dy * dy);

                Vector3 nearestOnCylinder;
                if (dxy > 1e-5f)
                {
                    float scale = MathF.Max(0f, dxy - targets.BoltKeepOutRadiusMm) / dxy;
                    nearestOnCylinder = dxy <= targets.BoltKeepOutRadiusMm
                        ? new Vector3(center.X, center.Y, clampedZ)
                        : new Vector3(center.X + dx * scale, center.Y + dy * scale, clampedZ);
                }
                else
                {
                    nearestOnCylinder = new Vector3(center.X, center.Y, clampedZ);
                }

                Vector3 toBolt = nearestOnCylinder - center;
                float dist = toBolt.Length();
                float dirRadius = dist > 1e-5f
                    ? EvaluateTeardropCrossSectionRadius(rFluid, aspect, toBolt / dist, localZ, endCircularFade) + wall
                    : EvaluateTeardropCrossSectionRadius(rFluid, aspect, Vector3.UnitX, localZ, endCircularFade) + wall;

                float clearance = dxy <= targets.BoltKeepOutRadiusMm && center.Z >= 0f && center.Z <= targets.MaxTotalHeightMm
                    ? (dxy - targets.BoltKeepOutRadiusMm) - dirRadius
                    : dist - dirRadius;

                if (clearance < minClearance)
                    minClearance = clearance;
            }
            return minClearance;
        }

        private static float ComputeObstacleClearance(
            Vector3 center,
            Vector3 localZ,
            float rFluid,
            float wall,
            float aspect,
            float endCircularFade,
            ObstacleCapsule[] obstacles)
        {
            float minClearance = float.MaxValue;
            foreach (ObstacleCapsule obs in obstacles)
            {
                Vector3 closest = ClosestPointOnSegment(center, obs.Start, obs.End);
                Vector3 toObs = closest - center;
                float dist = toObs.Length();

                float dirRadius = dist > 1e-5f
                    ? EvaluateTeardropCrossSectionRadius(rFluid, aspect, toObs / dist, localZ, endCircularFade) + wall
                    : EvaluateTeardropCrossSectionRadius(rFluid, aspect, -Vector3.UnitZ, localZ, endCircularFade) + wall;

                float clearance = dist - obs.RadiusMm - dirRadius;
                if (clearance < minClearance)
                    minClearance = clearance;
            }
            return minClearance;
        }

        private static Vector3 ClosestPointOnSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab = b - a;
            float abLenSq = ab.LengthSquared();
            float t = abLenSq > 1e-8f ? Math.Clamp(Vector3.Dot(p - a, ab) / abLenSq, 0f, 1f) : 0f;
            return a + t * ab;
        }

        private static float DistancePointToSegment(Vector3 p, Vector3 a, Vector3 b)
            => Vector3.Distance(p, ClosestPointOnSegment(p, a, b));

        private static Vector3 EstimateTangent(IReadOnlyList<Vector3> pts, int index)
        {
            if (pts.Count < 2)
                return Vector3.UnitZ;

            Vector3 d = index == 0
                ? pts[1] - pts[0]
                : index == pts.Count - 1
                    ? pts[^1] - pts[^2]
                    : pts[index + 1] - pts[index - 1];
            return d.LengthSquared() > 1e-10f ? Vector3.Normalize(d) : Vector3.UnitZ;
        }

        private static float BridgeWindowWeight(float u)
            => SmoothStep((u - 0.74f) / 0.04f) * (1.0f - SmoothStep((u - 0.86f) / 0.04f));

        private static float NormalizeAngleSignedDeg(float angleDeg)
        {
            float wrapped = ((angleDeg + 180.0f) % 360.0f + 360.0f) % 360.0f - 180.0f;
            return wrapped <= -180.0f ? wrapped + 360.0f : wrapped;
        }

        private static float SmoothStep(float t)
        {
            float x = Math.Clamp(t, 0f, 1f);
            return x * x * (3f - 2f * x);
        }
    }
}
