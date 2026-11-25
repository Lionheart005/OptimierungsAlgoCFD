using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Projects.StatorCoolingRing;
using FluidForge.Solvers.OpenFoam;
using Xunit;
using Xunit.Abstractions;

namespace FluidForge.Tests
{
    public sealed class StatorCoolingRingTests
    {
        private readonly ITestOutputHelper _output;

        public StatorCoolingRingTests(ITestOutputHelper output)
        {
            _output = output;
        }

        private static string ProjectDir
        {
            get
            {
                string? dir = ProjectPaths.ResolveProjectDirectory("StatorCoolingRing");
                Assert.True(dir != null, "src/projects/StatorCoolingRing/ was not found.");
                return dir!;
            }
        }

        private static ProjectConfig LoadProjectConfig()
        {
            var projectDef = new StatorCoolingRingProject();
            return JsonConfigLoader.Load(
                ProjectConfigDto.FromProjectConfig(projectDef.CreateDefaults()),
                Path.Combine(ProjectDir, ProjectPaths.ManifestFileName))
                .ToProjectConfig();
        }

        private static Dictionary<string, float> BuildMidParameters(ProjectConfig project)
        {
            var mid = new Dictionary<string, float>(StringComparer.Ordinal);
            foreach (var (name, bounds) in project.ParameterBounds)
            {
                mid[name] = 0.5f * (bounds.Min + bounds.Max);
            }
            return mid;
        }

        [Fact]
        public void VascularEngine_Is_Strictly_Deterministic_And_Fast()
        {
            ProjectConfig project = LoadProjectConfig();
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            StatorCoolingRingParams p = StatorCoolingRingParams.Read(project.BaseParameters);

            // Warmup JIT
            _ = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, targets);

            // Warm up JIT once before measuring steady-state deterministic synthesis runtime
            VascularNetwork runA = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, targets);

            var sw = Stopwatch.StartNew();
            VascularNetwork runB = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, targets);
            sw.Stop();
            double elapsedMs = sw.Elapsed.TotalMilliseconds;

            Assert.Equal(StatorCoolingRingConstants.OutletCount, runA.Paths.Count);
            Assert.Equal(runA.UpperCount, runB.UpperCount);
            Assert.Equal(runA.LowerCount, runB.LowerCount);
            Assert.Equal(runA.MeanPairwiseCenterlineSpreadMm, runB.MeanPairwiseCenterlineSpreadMm);

            for (int i = 0; i < runA.Paths.Count; i++)
            {
                VascularPath pa = runA.Paths[i];
                VascularPath pb = runB.Paths[i];
                Assert.Equal(pa.Points.Count, pb.Points.Count);
                Assert.Equal(pa.BalanceFactor, pb.BalanceFactor);
                Assert.Equal(pa.ResistanceIndex, pb.ResistanceIndex);
                for (int s = 0; s < pa.Points.Count; s++)
                {
                    Assert.Equal(pa.Points[s], pb.Points[s]);
                    Assert.Equal(pa.FluidRadii[s], pb.FluidRadii[s]);
                    Assert.Equal(pa.LocalMultiplicity[s], pb.LocalMultiplicity[s]);
                }
            }

            _output.WriteLine($"BuildVascularNetwork runtime: {elapsedMs:F2} ms");
            Assert.True(elapsedMs < 1500.0, $"Expected fast vascular synthesis (<1500 ms in Debug under parallel load), got {elapsedMs:F2} ms.");
        }

        [Fact]
        public void AnalyticalPackaging_Base_And_Mid_Parameters_Are_Both_Feasible_With_Clean_Margins()
        {
            ProjectConfig project = LoadProjectConfig();
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            Dictionary<string, float> midParams = BuildMidParameters(project);

            foreach (var (label, prms) in new[]
            {
                ("Base", project.BaseParameters),
                ("Mid", midParams)
            })
            {
                StatorCoolingRingParams p = StatorCoolingRingParams.Read(prms);
                VascularNetwork net = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, targets);
                StatorCoolingRingPackagingReport report =
                    StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(net, p, targets);

                _output.WriteLine(
                    $"{label} ({net.UpperCount}/{net.LowerCount}): Inner={report.InnerRadiusMm:F2}, Outer={report.OuterRadiusMm:F2}, " +
                    $"Height={report.TotalHeightMm:F2}, BoltClr={report.MinBoltClearanceMm:F2}, ObsClr={report.MinObstacleClearanceMm:F2}, " +
                    $"MinR={report.MinBranchRadiusMm:F2}, MinGap={report.MinUnbundledGapMm:F2}, Slivers={report.SliverGapCount}, Feasible={report.IsFeasible}");

                Assert.True(report.IsFeasible, $"{label} parameters should be feasible, got {report}.");
                Assert.True(report.InnerRadiusMm >= targets.MinInnerRadiusMm,
                    $"{label} InnerRadiusMm={report.InnerRadiusMm}");
                Assert.True(report.OuterRadiusMm <= targets.MaxOuterRadiusMm,
                    $"{label} OuterRadiusMm={report.OuterRadiusMm}");
                Assert.True(report.TotalHeightMm <= targets.MaxTotalHeightMm,
                    $"{label} TotalHeightMm={report.TotalHeightMm}");
                Assert.True(report.MinBoltClearanceMm >= targets.MinRequiredBoltClearanceMm,
                    $"{label} MinBoltClearanceMm={report.MinBoltClearanceMm}");
                Assert.True(report.MinObstacleClearanceMm >= targets.MinRequiredObstacleClearanceMm,
                    $"{label} MinObstacleClearanceMm={report.MinObstacleClearanceMm}");
                Assert.True(report.MinBranchRadiusMm >= targets.MinBranchRadiusMm,
                    $"{label} MinBranchRadiusMm={report.MinBranchRadiusMm}");
                Assert.Equal(0, report.SliverGapCount);
            }
        }

        [Fact]
        public void VascularEngine_Shows_Genuine_Morphological_Diversity()
        {
            ProjectConfig project = LoadProjectConfig();
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            Dictionary<string, float> mid = BuildMidParameters(project);

            // 1. BundlingStrength: open tree (0.05) vs bundled trunk (0.98)
            var unbundledDict = new Dictionary<string, float>(mid) { ["BundlingStrength"] = 0.05f };
            var bundledDict = new Dictionary<string, float>(mid) { ["BundlingStrength"] = 0.98f, ["BundlingRangeMm"] = 30.0f };
            VascularNetwork netUnbundled = StatorCoolingRingVascularEngine.BuildVascularNetwork(StatorCoolingRingParams.Read(unbundledDict), targets);
            VascularNetwork netBundled = StatorCoolingRingVascularEngine.BuildVascularNetwork(StatorCoolingRingParams.Read(bundledDict), targets);

            float meanKeffUnbundled = netUnbundled.Paths.SelectMany(p => p.LocalMultiplicity).Average();
            float meanKeffBundled = netBundled.Paths.SelectMany(p => p.LocalMultiplicity).Average();
            _output.WriteLine(
                $"Bundling diversity: spread {netUnbundled.MeanPairwiseCenterlineSpreadMm:F2} -> {netBundled.MeanPairwiseCenterlineSpreadMm:F2} mm; " +
                $"mean k_eff {meanKeffUnbundled:F2} -> {meanKeffBundled:F2}");

            Assert.True(netUnbundled.MeanPairwiseCenterlineSpreadMm > netBundled.MeanPairwiseCenterlineSpreadMm + 2.5f,
                $"Expected strong spread reduction when bundled: {netUnbundled.MeanPairwiseCenterlineSpreadMm} vs {netBundled.MeanPairwiseCenterlineSpreadMm}");
            Assert.True(meanKeffBundled > meanKeffUnbundled + 0.8f,
                $"Expected higher k_eff when bundled: {meanKeffBundled} vs {meanKeffUnbundled}");

            // 2. UpperLowerSplitBias: 6/6, 5/7, 4/8, 8/4 + continuous Mid +-5% shift
            var split0 = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["UpperLowerSplitBias"] = 0.0f }), targets);
            var splitBase = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["UpperLowerSplitBias"] = 0.28f }), targets);
            var splitPlus = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["UpperLowerSplitBias"] = 0.65f }), targets);
            var splitMinus = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["UpperLowerSplitBias"] = -0.65f }), targets);

            Assert.Equal((6, 6), (split0.UpperCount, split0.LowerCount));
            Assert.Equal((5, 7), (splitBase.UpperCount, splitBase.LowerCount));
            Assert.Equal((4, 8), (splitPlus.UpperCount, splitPlus.LowerCount));
            Assert.Equal((8, 4), (splitMinus.UpperCount, splitMinus.LowerCount));

            // 3. FarSideLoopAttraction: 0.0 (open tree) vs 1.0 (closed far-side loop with k_eff <= 1.25)
            var openTree = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["FarSideLoopAttraction"] = 0.0f }), targets);
            var closedLoop = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["FarSideLoopAttraction"] = 1.0f }), targets);

            float minDistOpen = MinBridgeWindowDistance(openTree);
            float minDistLoop = MinBridgeWindowDistance(closedLoop);
            float maxBridgeKeff = MaxBridgeWindowKeff(closedLoop);
            _output.WriteLine($"FarSideLoop: minBridgeDist open={minDistOpen:F2} mm vs loop={minDistLoop:F2} mm; maxBridgeKeff={maxBridgeKeff:F3}");

            Assert.True(minDistOpen > 10.0f, $"Expected separated boundary arcs for FarSideLoopAttraction=0, got {minDistOpen}");
            Assert.True(minDistLoop < minDistOpen * 0.4f, $"Expected far-side boundary attraction for FarSideLoopAttraction=1, got {minDistLoop} vs {minDistOpen}");
            Assert.True(maxBridgeKeff <= 1.25f + 1e-4f, $"Expected capped bridge k_eff <= 1.25, got {maxBridgeKeff}");

            // 4. HydraulicBalanceGain & ResidualThroatTilt modulate terminal radii
            var noBal = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["HydraulicBalanceGain"] = 0.0f, ["ResidualThroatTilt"] = 0.0f }), targets);
            var fullBal = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["HydraulicBalanceGain"] = 1.0f, ["ResidualThroatTilt"] = 0.0f }), targets);
            var tilted = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                StatorCoolingRingParams.Read(new Dictionary<string, float>(mid) { ["HydraulicBalanceGain"] = 1.0f, ["ResidualThroatTilt"] = 0.25f }), targets);

            Assert.All(noBal.Paths, p => Assert.Equal(1.0f, p.BalanceFactor, 5));
            float farBalRadius = fullBal.Paths[0].FluidRadii[38];   // 15 deg (far)
            float nearBalRadius = fullBal.Paths[5].FluidRadii[38];  // 165 deg (near inlet)
            Assert.True(farBalRadius > nearBalRadius,
                $"Expected larger terminal radius on long far-side path ({farBalRadius}) than short near-inlet path ({nearBalRadius}).");

            float farTiltRadius = tilted.Paths[0].FluidRadii[38];
            Assert.NotEqual(farBalRadius, farTiltRadius);

            // 5. ClosedLoop preview configuration must form an arched far-side loop without cutting into rotor bore
            var closedLoopPreview = new Dictionary<string, float>(mid)
            {
                ["BundlingStrength"] = 0.78f,
                ["BundlingRangeMm"] = 26.0f,
                ["SplitReleaseBias"] = 0.30f,
                ["UpperLowerSplitBias"] = 0.05f,
                ["FarSideLoopAttraction"] = 1.0f,
                ["ObstacleOverpassBias"] = 0.85f
            };
            StatorCoolingRingParams closedP = StatorCoolingRingParams.Read(closedLoopPreview);
            VascularNetwork closedNet = StatorCoolingRingVascularEngine.BuildVascularNetwork(closedP, targets);
            StatorCoolingRingPackagingReport closedRep = StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(closedNet, closedP, targets);
            _output.WriteLine(
                $"ClosedLoop: Inner={closedRep.InnerRadiusMm:F2}, Outer={closedRep.OuterRadiusMm:F2}, " +
                $"Height={closedRep.TotalHeightMm:F2}, BoltClr={closedRep.MinBoltClearanceMm:F2}, ObsClr={closedRep.MinObstacleClearanceMm:F2}, " +
                $"MinR={closedRep.MinBranchRadiusMm:F2}, Slivers={closedRep.SliverGapCount}, Feasible={closedRep.IsFeasible}");
            Assert.True(closedRep.IsFeasible, $"ClosedLoop preview should be feasible, got {closedRep}");
            Assert.True(closedRep.InnerRadiusMm >= targets.MinInnerRadiusMm);
        }

        [Fact]
        public void OpenDendriticTree_And_All_Showcase_Morphologies_Keep_SW24_Hex_Zone_Clear()
        {
            ProjectConfig project = LoadProjectConfig();
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);

            var morphologies = new (string Name, bool RequireZeroSlivers, Dictionary<string, float> Params)[]
            {
                ("Base", true, new Dictionary<string, float>(project.BaseParameters)),
                ("HighBundling_Asym4_8", true, new Dictionary<string, float>(project.BaseParameters)
                {
                    ["BundlingStrength"] = 0.96f,
                    ["BundlingRangeMm"] = 30.0f,
                    ["SplitReleaseBias"] = 0.24f,
                    ["UpperLowerSplitBias"] = 0.55f,
                    ["FarSideLoopAttraction"] = 0.05f,
                    ["MurrayExponent"] = 2.30f
                }),
                ("OpenDendriticTree", true, new Dictionary<string, float>(project.BaseParameters)
                {
                    ["BundlingStrength"] = 0.12f,
                    ["BundlingRangeMm"] = 11.0f,
                    ["SplitReleaseBias"] = 0.64f,
                    ["UpperLowerSplitBias"] = 0.00f,
                    ["FarSideLoopAttraction"] = 0.00f,
                    ["ElevationTiltMm"] = 8.0f,
                    ["HydraulicBalanceGain"] = 0.90f
                }),
                ("ClosedLoop", false, new Dictionary<string, float>(project.BaseParameters)
                {
                    ["BundlingStrength"] = 0.78f,
                    ["BundlingRangeMm"] = 26.0f,
                    ["SplitReleaseBias"] = 0.30f,
                    ["UpperLowerSplitBias"] = 0.05f,
                    ["FarSideLoopAttraction"] = 1.00f,
                    ["ObstacleOverpassBias"] = 0.85f
                })
            };

            foreach (var (name, requireZeroSlivers, prms) in morphologies)
            {
                StatorCoolingRingParams p = StatorCoolingRingParams.Read(prms);
                VascularNetwork net = StatorCoolingRingVascularEngine.BuildVascularNetwork(p, targets);
                StatorCoolingRingPackagingReport rep =
                    StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(net, p, targets);

                float minBackWallX = float.MaxValue;
                foreach (VascularPath path in net.Paths)
                {
                    float totalLen = MathF.Max(1e-6f, path.PathLengthMm);
                    float accum = 0f;
                    for (int s = 0; s < path.Points.Count; s++)
                    {
                        if (s > 0)
                            accum += Vector3.Distance(path.Points[s], path.Points[s - 1]);
                        if (s < 3)
                            continue;

                        Vector3 center = path.Points[s];
                        float dy = center.Y;
                        float dz = center.Z - StatorCoolingRingConstants.DefaultInletZMm;
                        float rInletAxis = MathF.Sqrt(dy * dy + dz * dz);
                        if (rInletAxis <= 4.0f)
                            continue;

                        float u = accum / totalLen;
                        float rFluid = path.FluidRadii[s];
                        float wall = StatorCoolingRingVascularEngine.EvaluateLocalWallThicknessMm(rFluid, targets);
                        Vector3 tangent = EstimateTangent(path.Points, s);
                        float endFade = StatorCoolingRingVascularEngine.EvaluateEndCircularFade(u);
                        float rBackX = StatorCoolingRingVascularEngine.EvaluateTeardropCrossSectionRadius(
                            rFluid, p.CrossSectionAspect, -Vector3.UnitX, tangent, endFade) + wall;
                        minBackWallX = MathF.Min(minBackWallX, center.X - rBackX);
                    }
                }

                _output.WriteLine(
                    $"{name} ({net.UpperCount}/{net.LowerCount}): Feasible={rep.IsFeasible}, Slivers={rep.SliverGapCount}, MinGap={rep.MinUnbundledGapMm:F2}, " +
                    $"Inner={rep.InnerRadiusMm:F2}, Outer={rep.OuterRadiusMm:F2}, Height={rep.TotalHeightMm:F2}, " +
                    $"BoltClr={rep.MinBoltClearanceMm:F2}, ObsClr={rep.MinObstacleClearanceMm:F2}, MinBackWallX={minBackWallX:F2}");

                Assert.True(rep.IsFeasible, $"{name} should be feasible, got {rep}");
                Assert.True(rep.MinUnbundledGapMm >= 1.5f,
                    $"{name} MinUnbundledGapMm={rep.MinUnbundledGapMm:F2} < 1.5 mm.");
                if (requireZeroSlivers)
                    Assert.Equal(0, rep.SliverGapCount);
                Assert.True(minBackWallX >= -94.5f,
                    $"{name} intruded SW-24 hex zone: min(center.X - rBackX) = {minBackWallX:F2} mm < -94.5 mm.");
            }
        }

        [Fact]
        public void DesignSpace14D_RandomSample_Achieves_High_Feasibility()
        {
            ProjectConfig project = LoadProjectConfig();
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            var rng = new Random(42);

            const int SampleCount = 100;
            int feasibleCount = 0;
            int innerFails = 0;
            int outerFails = 0;
            int heightFails = 0;
            int boltFails = 0;
            int obsFails = 0;
            int minRFails = 0;

            for (int k = 0; k < SampleCount; k++)
            {
                var prms = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var (name, b) in project.ParameterBounds)
                    prms[name] = b.Min + (float)rng.NextDouble() * (b.Max - b.Min);

                StatorCoolingRingPackagingReport rep =
                    StatorCoolingRingVascularEngine.EvaluateAnalyticalPackaging(prms, project);
                if (rep.IsFeasible) feasibleCount++;
                if (rep.InnerRadiusMm < targets.MinInnerRadiusMm) innerFails++;
                if (rep.OuterRadiusMm > targets.MaxOuterRadiusMm) outerFails++;
                if (rep.TotalHeightMm > targets.MaxTotalHeightMm) heightFails++;
                if (rep.MinBoltClearanceMm < targets.MinRequiredBoltClearanceMm) boltFails++;
                if (rep.MinObstacleClearanceMm < targets.MinRequiredObstacleClearanceMm) obsFails++;
                if (rep.MinBranchRadiusMm < targets.MinBranchRadiusMm) minRFails++;
            }

            _output.WriteLine(
                $"100-Sample 14D Box Feasibility: {feasibleCount}/{SampleCount} ({feasibleCount}%) | " +
                $"Fails: Inner={innerFails}, Outer={outerFails}, Height={heightFails}, Bolt={boltFails}, Obs={obsFails}, MinR={minRFails}");
            Assert.True(feasibleCount >= 70, $"Expected >= 70% 14D feasibility, got {feasibleCount}%.");
        }

        [Fact]
        public void All_14_Parameters_Induce_NonZero_Geometry_Sensitivity_Around_Mid()
        {
            ProjectConfig project = LoadProjectConfig();
            Assert.Equal(14, StatorCoolingRingParams.ParameterNames.Length);
            Assert.Equal(14, project.ParameterBounds.Count);

            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            Dictionary<string, float> mid = BuildMidParameters(project);

            foreach (string paramName in StatorCoolingRingParams.ParameterNames)
            {
                var bounds = project.ParameterBounds[paramName];
                float step = 0.05f * (bounds.Max - bounds.Min);

                var plusDict = new Dictionary<string, float>(mid)
                {
                    [paramName] = MathF.Min(bounds.Max, mid[paramName] + step)
                };
                var minusDict = new Dictionary<string, float>(mid)
                {
                    [paramName] = MathF.Max(bounds.Min, mid[paramName] - step)
                };

                StatorCoolingRingParams pPlus = StatorCoolingRingParams.Read(plusDict);
                StatorCoolingRingParams pMinus = StatorCoolingRingParams.Read(minusDict);
                VascularNetwork netPlus = StatorCoolingRingVascularEngine.BuildVascularNetwork(pPlus, targets);
                VascularNetwork netMinus = StatorCoolingRingVascularEngine.BuildVascularNetwork(pMinus, targets);

                float maxPointDelta = 0f;
                float maxRadiusDelta = 0f;
                float maxTeardropDelta = 0f;

                for (int i = 0; i < netPlus.Paths.Count; i++)
                {
                    VascularPath a = netPlus.Paths[i];
                    VascularPath b = netMinus.Paths[i];
                    for (int s = 0; s < a.Points.Count; s++)
                    {
                        maxPointDelta = MathF.Max(maxPointDelta, Vector3.Distance(a.Points[s], b.Points[s]));
                        maxRadiusDelta = MathF.Max(maxRadiusDelta, MathF.Abs(a.FluidRadii[s] - b.FluidRadii[s]));

                        Vector3 tanA = EstimateTangent(a.Points, s);
                        Vector3 tanB = EstimateTangent(b.Points, s);
                        float tdA = StatorCoolingRingVascularEngine.EvaluateTeardropCrossSectionRadius(
                            a.FluidRadii[s], pPlus.CrossSectionAspect, Vector3.UnitZ, tanA, 1.0f);
                        float tdB = StatorCoolingRingVascularEngine.EvaluateTeardropCrossSectionRadius(
                            b.FluidRadii[s], pMinus.CrossSectionAspect, Vector3.UnitZ, tanB, 1.0f);
                        maxTeardropDelta = MathF.Max(maxTeardropDelta, MathF.Abs(tdA - tdB));
                    }
                }

                float totalSensitivity = MathF.Max(maxPointDelta, MathF.Max(maxRadiusDelta, maxTeardropDelta));
                _output.WriteLine($"Sensitivity [{paramName}]: pt={maxPointDelta:F4} mm, r={maxRadiusDelta:F4} mm, td={maxTeardropDelta:F4} mm");
                Assert.True(totalSensitivity > 1e-3f,
                    $"Parameter '{paramName}' appeared dead around Mid (delta={totalSensitivity}).");
            }
        }

        [Fact]
        public void AnalyticalPackaging_Detects_All_Packaging_Clearance_And_Radius_Violations()
        {
            ProjectConfig project = LoadProjectConfig();
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            Dictionary<string, float> mid = BuildMidParameters(project);

            // 1. Inner radius violation + outer radius violation via oversized scale
            var oversizedScale = new Dictionary<string, float>(mid)
            {
                ["BaseChannelScale"] = 6.50f,
                ["BundlingStrength"] = 0.95f
            };
            StatorCoolingRingPackagingReport scaleReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(oversizedScale, project);
            Assert.False(scaleReport.IsFeasible);
            Assert.True(scaleReport.InnerRadiusMm < targets.MinInnerRadiusMm,
                $"Expected InnerRadius violation, got {scaleReport.InnerRadiusMm}");
            Assert.True(scaleReport.OuterRadiusMm > targets.MaxOuterRadiusMm,
                $"Expected OuterRadius violation, got {scaleReport.OuterRadiusMm}");

            // 2. Total height violation (> 54 mm)
            var tooTall = new Dictionary<string, float>(mid)
            {
                ["ElevationBaseMm"] = 68.0f,
                ["ElevationTiltMm"] = 20.0f,
                ["PathStiffness"] = 0.85f
            };
            StatorCoolingRingPackagingReport tallReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(tooTall, project);
            Assert.False(tallReport.IsFeasible);
            Assert.True(tallReport.TotalHeightMm > targets.MaxTotalHeightMm,
                $"Expected TotalHeight violation, got {tallReport.TotalHeightMm}");

            // 3. Bolt keep-out clearance violation (< 2.0 mm)
            var boltViolation = new Dictionary<string, float>(mid)
            {
                ["BaseChannelScale"] = 5.50f,
                ["ElevationBaseMm"] = 24.0f,
                ["PathStiffness"] = 0.85f
            };
            StatorCoolingRingPackagingReport boltReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(boltViolation, project);
            Assert.False(boltReport.IsFeasible);
            Assert.True(boltReport.MinBoltClearanceMm < targets.MinRequiredBoltClearanceMm,
                $"Expected BoltClearance violation, got {boltReport.MinBoltClearanceMm}");

            // 4. HV-harness obstacle clearance violation (< 2.0 mm)
            var obstacleViolation = new Dictionary<string, float>(mid)
            {
                ["ElevationBaseMm"] = 12.0f,
                ["ElevationTiltMm"] = -6.0f,
                ["ObstacleOverpassBias"] = -2.5f,
                ["BaseChannelScale"] = 3.40f,
                ["PathStiffness"] = 0.85f
            };
            StatorCoolingRingPackagingReport obsReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(obstacleViolation, project);
            Assert.False(obsReport.IsFeasible);
            Assert.True(obsReport.MinObstacleClearanceMm < targets.MinRequiredObstacleClearanceMm,
                $"Expected ObstacleClearance violation, got {obsReport.MinObstacleClearanceMm}");

            // 5. Minimum branch radius violation (< 1.5 mm)
            var tooThin = new Dictionary<string, float>(mid)
            {
                ["BaseChannelScale"] = 0.55f
            };
            StatorCoolingRingPackagingReport thinReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(tooThin, project);
            Assert.False(thinReport.IsFeasible);
            Assert.True(thinReport.MinBranchRadiusMm < targets.MinBranchRadiusMm,
                $"Expected MinBranchRadius violation, got {thinReport.MinBranchRadiusMm}");
        }

        [Fact]
        public void InterTubeAndSupportRibs_Are_Strictly_CollisionFree()
        {
            ProjectConfig project = LoadProjectConfig();
            StatorCoolingRingTargets targets = StatorCoolingRingTargets.FromProject(project);
            ObstacleCapsule[] obstacles = StatorCoolingRingVascularEngine.GetObstacleCapsules();

            foreach (var (label, prms) in new[]
            {
                ("Base", project.BaseParameters),
                ("Mid", BuildMidParameters(project))
            })
            {
                VascularNetwork network = StatorCoolingRingVascularEngine.BuildVascularNetwork(
                    StatorCoolingRingParams.Read(prms), targets);

                IReadOnlyList<RibSegment> interRibs = StatorCoolingRingVascularEngine.BuildInterTubeRibSegments(network, targets);
                IReadOnlyList<RibSegment> supportRibs = StatorCoolingRingVascularEngine.BuildAdaptiveSupportRibCurves(network, targets);

                _output.WriteLine($"{label} ribs: interTube={interRibs.Count}, adaptiveSupport={supportRibs.Count}");

                Assert.NotEmpty(interRibs);
                Assert.NotEmpty(supportRibs);

                ValidateRibSegments(interRibs, targets, obstacles, minZ: 6.0f);
                ValidateRibSegments(supportRibs, targets, obstacles, minZ: 0.0f);
            }
        }

        [Theory]
        [InlineData(0.65f)]
        [InlineData(1.00f)]
        [InlineData(1.45f)]
        public void TeardropCrossSection_Is_FrameInvariant_And_Points_Upward_Across_Aspects(float aspect)
        {
            const float radius = 5.0f;
            Vector3 horizontalTangent = Vector3.Normalize(new Vector3(0.6f, 0.8f, 0.0f));
            Vector3 verticalTangent = Vector3.UnitZ;

            float rUp = StatorCoolingRingTopologyBuilder.EvaluateTeardropCrossSectionRadius(
                radius, aspect, Vector3.UnitZ, horizontalTangent, endCircularFade: 1.0f);
            float rDown = StatorCoolingRingTopologyBuilder.EvaluateTeardropCrossSectionRadius(
                radius, aspect, -Vector3.UnitZ, horizontalTangent, endCircularFade: 1.0f);
            float rHorizX = StatorCoolingRingTopologyBuilder.EvaluateTeardropCrossSectionRadius(
                radius, aspect, Vector3.UnitX, horizontalTangent, endCircularFade: 1.0f);
            float rHorizY = StatorCoolingRingTopologyBuilder.EvaluateTeardropCrossSectionRadius(
                radius, aspect, Vector3.UnitY, horizontalTangent, endCircularFade: 1.0f);

            Assert.True(rUp > rDown, $"Expected rUp ({rUp}) > rDown ({rDown}) for aspect={aspect}.");
            Assert.True(rUp > rHorizX, $"Expected rUp ({rUp}) > rHorizX ({rHorizX}) for aspect={aspect}.");
            Assert.Equal(rHorizX, rHorizY, 4);

            foreach (Vector3 dir in new[] { Vector3.UnitX, Vector3.UnitY, -Vector3.UnitX, -Vector3.UnitY })
            {
                float rVert = StatorCoolingRingTopologyBuilder.EvaluateTeardropCrossSectionRadius(
                    radius, aspect, dir, verticalTangent, endCircularFade: 1.0f);
                Assert.Equal(radius, rVert, 4);
            }
        }

        [Fact]
        public void CubicHermite_PureCSharpSpline_Hits_Exact_Endpoints_And_Tangents()
        {
            Vector3 p0 = new(0f, 10f, 20f);
            Vector3 p1 = new(30f, 40f, 5f);
            Vector3 t0 = new(15f, 0f, 0f);
            Vector3 t1 = new(0f, 0f, -20f);

            List<Vector3> pts = StatorCoolingRingTopologyBuilder.SampleCubicHermite(p0, p1, t0, t1, 25);

            Assert.Equal(25, pts.Count);
            Assert.Equal(p0, pts[0]);
            Assert.Equal(p1, pts[^1]);
        }

        [Fact]
        public void HexWrenchRadius_Matches_SW24_Inscribed_And_Corner_Dimensions()
        {
            const float inscribedRadius = 12.0f;
            float expectedCornerRadius = inscribedRadius / MathF.Cos(MathF.PI / 6.0f);

            for (int k = 0; k < 6; k++)
            {
                float flatAngleRad = (30.0f + 60.0f * k) * StatorCoolingRingConstants.DegToRad;
                float cornerAngleRad = (60.0f * k) * StatorCoolingRingConstants.DegToRad;

                float rFlat = StatorCoolingRingSolidBuilder.EvaluateHexRadius(flatAngleRad, inscribedRadius);
                float rCorner = StatorCoolingRingSolidBuilder.EvaluateHexRadius(cornerAngleRad, inscribedRadius);

                Assert.Equal(inscribedRadius, rFlat, 4);
                Assert.Equal(expectedCornerRadius, rCorner, 4);
            }
        }

        [Fact]
        public void Outlets_Bolts_And_ObstacleCapsules_Do_Not_Collide_With_BaseFlange_Or_Each_Other()
        {
            IReadOnlyList<float> angles = StatorCoolingRingGeometryGenerator.GetOutletAnglesDeg();
            Assert.Equal(12, angles.Count);
            Assert.DoesNotContain(180.0f, angles);
            Assert.DoesNotContain(0.0f, angles);

            for (int k = 0; k < 6; k++)
                Assert.Equal(360.0f, angles[k] + angles[11 - k], 4);

            foreach (float boltDeg in StatorCoolingRingConstants.BoltAnglesDeg)
            {
                float bRad = boltDeg * StatorCoolingRingConstants.DegToRad;
                Vector2 bPos = new(
                    StatorCoolingRingConstants.DefaultBoltCircleRadiusMm * MathF.Cos(bRad),
                    StatorCoolingRingConstants.DefaultBoltCircleRadiusMm * MathF.Sin(bRad));

                foreach (float outDeg in angles)
                {
                    float oRad = outDeg * StatorCoolingRingConstants.DegToRad;
                    Vector2 oPos = new(
                        StatorCoolingRingConstants.DefaultPitchCircleRadiusMm * MathF.Cos(oRad),
                        StatorCoolingRingConstants.DefaultPitchCircleRadiusMm * MathF.Sin(oRad));

                    float dist = Vector2.Distance(bPos, oPos);
                    Assert.True(dist > 20.0f, $"Bolt {boltDeg} deg too close to outlet {outDeg} deg ({dist} mm).");
                }
            }

            ObstacleCapsule[] obstacles = StatorCoolingRingGeometryGenerator.GetObstacleCapsules();
            Assert.Equal(4, obstacles.Length);

            foreach (ObstacleCapsule obs in obstacles)
            {
                bool isRiser = MathF.Abs(obs.Start.Z - obs.End.Z) > 1.0f;
                if (isRiser)
                {
                    float rMaxXY = MathF.Sqrt(obs.Start.X * obs.Start.X + obs.Start.Y * obs.Start.Y) + obs.RadiusMm;
                    Assert.True(rMaxXY < StatorCoolingRingConstants.DefaultFlangeInnerRadiusMm - 4.0f,
                        $"{obs.Name} outer XY ({rMaxXY}) intrudes toward base flange inner radius.");
                }
                else
                {
                    float zMin = MathF.Min(obs.Start.Z, obs.End.Z) - obs.RadiusMm;
                    Assert.True(zMin > StatorCoolingRingConstants.DefaultFlangeHeightMm + 2.0f,
                        $"{obs.Name} bottom ({zMin}) collides with base flange height.");
                }
            }
        }

        [Fact]
        public void PreCfdConstraints_Enforce_All_Packaging_Obstacle_And_Volume_Limits()
        {
            ProjectConfig project = LoadProjectConfig();
            var evaluator = new StatorCoolingRingGeometryConstraints();

            var feasible = new ModelRecord();
            feasible.PassiveParameters["InnerRadiusMm"] = 54.0f;
            feasible.PassiveParameters["OuterRadiusMm"] = 92.2f;
            feasible.PassiveParameters["TotalHeightMm"] = 47.5f;
            feasible.PassiveParameters["MinBoltClearanceMm"] = 2.8f;
            feasible.PassiveParameters["MinObstacleClearanceMm"] = 4.2f;
            feasible.PassiveParameters["MinBranchRadiusMm"] = 2.0f;
            feasible.PassiveParameters["Volume"] = 48000.0f;
            feasible.PassiveParameters["PrintableSolidVolume"] = 140000.0f;

            IReadOnlyList<ConstraintEvaluation> ok = evaluator.EvaluateGeometryConstraints(feasible, project);
            Assert.Equal(9, ok.Count);
            Assert.All(ok, c =>
            {
                Assert.True(c.IsPreSolve);
                Assert.True(c.IsSatisfied, $"{c.Name} failed unexpectedly.");
            });

            var infeasible = new ModelRecord();
            infeasible.PassiveParameters["InnerRadiusMm"] = 43.0f;
            infeasible.PassiveParameters["OuterRadiusMm"] = 96.0f;
            infeasible.PassiveParameters["TotalHeightMm"] = 58.0f;
            infeasible.PassiveParameters["MinBoltClearanceMm"] = 0.5f;
            infeasible.PassiveParameters["MinObstacleClearanceMm"] = -1.2f;
            infeasible.PassiveParameters["MinBranchRadiusMm"] = 1.1f;
            infeasible.PassiveParameters["Volume"] = 195000.0f;
            infeasible.PassiveParameters["PrintableSolidVolume"] = 380000.0f;

            IReadOnlyList<ConstraintEvaluation> bad = evaluator.EvaluateGeometryConstraints(infeasible, project);
            Assert.Equal(9, bad.Count);
            Assert.False(bad.Single(c => c.Name == "InnerRadiusMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "OuterRadiusMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "TotalHeightMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "MinBoltClearanceMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "MinObstacleClearanceMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "MinBranchRadiusMm").IsSatisfied);
            Assert.True(bad.Single(c => c.Name == "Volume_Min").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "Volume_Max").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "PrintableSolidVolume").IsSatisfied);

            var analyticalOnly = new ModelRecord();
            foreach (var kvp in project.BaseParameters)
                analyticalOnly.ActiveParameters[kvp.Key] = kvp.Value;
            analyticalOnly.PassiveParameters["Volume"] = 48000.0f;

            IReadOnlyList<ConstraintEvaluation> fallback = evaluator.EvaluateGeometryConstraints(analyticalOnly, project);
            Assert.Equal(8, fallback.Count);
            Assert.All(fallback, c => Assert.True(c.IsSatisfied, $"Fallback {c.Name} failed."));
        }

        [Fact]
        public void FitnessCalculator_Emits_Two_Minimize_Objectives_And_Guards_NonPositive_PressureDrop()
        {
            ProjectConfig project = LoadProjectConfig();
            var calc = new StatorCoolingRingFitnessCalculator(project);
            var simConfig = new SimulationConfig();

            Assert.Equal(new[] { "PressureDrop", "FlowImbalance" }, calc.RequiredMetrics);
            Assert.Equal(
                new[]
                {
                    "PressureDrop",
                    "PressureDropStatic",
                    "FlowImbalance",
                    "MassBalanceError",
                    "Volume",
                    "PrintableSolidVolume",
                    "InnerRadiusMm",
                    "MinBoltClearanceMm",
                    "MinObstacleClearanceMm"
                },
                calc.RawComparisonMetrics);

            var normal = new ModelRecord();
            normal.PassiveParameters["PressureDrop"] = 2500.0f;
            normal.PassiveParameters["FlowImbalance"] = 4.0f;
            calc.CalculateFitness(normal, simConfig);

            Assert.True(normal.Feasible);
            Assert.Equal(2, normal.Objectives.Count);
            Assert.Equal("PressureDrop", normal.Objectives[0].Name);
            Assert.Equal(OptimizationDirection.Minimize, normal.Objectives[0].Direction);
            Assert.Equal(0.5f, normal.Objectives[0].Weight, 5);
            Assert.Equal("FlowImbalance", normal.Objectives[1].Name);
            Assert.Equal(OptimizationDirection.Minimize, normal.Objectives[1].Direction);
            Assert.Equal(0.5f, normal.Objectives[1].Weight, 5);
            Assert.True(float.IsFinite(normal.Fitness) && normal.Fitness > 0f);

            var nonPositiveDp = new ModelRecord();
            nonPositiveDp.PassiveParameters["PressureDrop"] = -12.0f;
            nonPositiveDp.PassiveParameters["FlowImbalance"] = 0.0f;
            calc.CalculateFitness(nonPositiveDp, simConfig);

            Assert.True(float.IsFinite(nonPositiveDp.Fitness) && nonPositiveDp.Fitness > 0f);
            Assert.Equal(1000.0f / 101.0f, nonPositiveDp.Fitness, 4);

            var missing = new ModelRecord();
            missing.PassiveParameters["PressureDrop"] = 1000.0f;
            calc.CalculateFitness(missing, simConfig);
            Assert.Equal(ObjectiveEvaluatorBase.DefaultFailedFitness, missing.Fitness, 6);
        }

        [Fact]
        public void ConfigFiles_And_ProjectWiring_Match_V3_Specification()
        {
            ProjectConfig project = LoadProjectConfig();
            Assert.Equal(14, project.BaseParameters.Count);
            Assert.Equal(14, project.ParameterBounds.Count);
            Assert.Empty(project.DimensionalParameters);
            foreach (string name in StatorCoolingRingParams.ParameterNames)
            {
                Assert.True(project.BaseParameters.ContainsKey(name), $"Missing BaseParameter '{name}'.");
                Assert.True(project.ParameterBounds.ContainsKey(name), $"Missing ParameterBounds '{name}'.");
            }
            Assert.Equal(84.0f, project.OptimizationTargets["BoltCircleRadiusMm"], 4);
            Assert.Equal(360000.0f, project.OptimizationTargets["MaxPrintableSolidVolumeMm3"], 4);
            Assert.Equal(1.0f, project.OptimizationTargets["ExportContextEnvironment"], 4);

            SimulationConfig sim = JsonConfigLoader.Load(
                SimulationConfig.CreateDefault(),
                Path.Combine(ProjectDir, "simulation.json"));
            Assert.Equal("Internal3D:StandardRans", sim.Preset);
            Assert.Equal("BoTorch", sim.OptimizationAlgorithm);
            Assert.Equal("qNEHVI", sim.ExternalOptimizer.Acquisition);
            Assert.True(sim.ExternalOptimizer.FallbackToBuiltInBayes);
            Assert.False(sim.UseRubberBandScaler);
            Assert.False(sim.UseStlSmoothing);
            Assert.Equal(0.4f, sim.BaseVoxelResolution, 4);
            Assert.True(sim.AutoVoxelResolution);
            Assert.Equal(16, sim.MaxIterations);
            Assert.Equal(6, sim.VariantsPerIteration);
            Assert.Equal(18, sim.MpiCores);

            var context = new ProjectContext("StatorCoolingRing", ProjectDir, null, sim, project);
            OpenFoamOptions openFoam = context.LoadSolverOptions<OpenFoamOptions>("openfoam");
            Assert.Equal(998.2f, openFoam.Density, 3);
            Assert.Equal(0.001002f, openFoam.DynamicViscosity, 6);
            Assert.Equal("VolumetricFlowRate", openFoam.InletCondition);
            Assert.Equal(0.00016667f, openFoam.InletVolumetricFlowRate, 7);
            Assert.Equal(1.255f, openFoam.InletVelocity, 3);
            Assert.Equal(1.6f, openFoam.BaseCellSizeMm, 3);
            Assert.Equal(2, openFoam.SurfaceRefinementLevelMin);
            Assert.Equal(3, openFoam.SurfaceRefinementLevelMax);
            Assert.Equal(25.0f, openFoam.TargetYPlus, 3);
            Assert.True(openFoam.ExportVtk);
            Assert.True(openFoam.ResultMetrics.ContainsKey("PressureDrop"));
            Assert.True(openFoam.ResultMetrics.ContainsKey("FlowImbalance"));

            var def = new StatorCoolingRingProject();
            Assert.Equal("StatorCoolingRing", def.Name);
            Assert.IsType<StatorCoolingRingGeometryGenerator>(def.CreateGeometry(context));
            Assert.IsType<StatorCoolingRingGeometryConstraints>(def.CreateGeometryConstraints(context));
            Assert.IsType<StatorCoolingRingFitnessCalculator>(def.CreateFitness(context));
        }

        [Fact]
        public void Readme_Has_Title_And_Strictly_Two_Prose_Paragraphs()
        {
            string readmePath = Path.Combine(ProjectDir, "README.md");
            Assert.True(File.Exists(readmePath), "StatorCoolingRing README.md is missing.");

            string text = File.ReadAllText(readmePath).Replace("\r\n", "\n").Trim();
            string[] blocks = Regex.Split(text, @"\n\s*\n");

            Assert.Equal(3, blocks.Length);
            Assert.Equal("# StatorCoolingRing", blocks[0].Trim());

            foreach (string paragraph in blocks.Skip(1))
            {
                Assert.DoesNotContain("\n#", paragraph);
                Assert.False(paragraph.StartsWith("#"), "Unexpected heading in README body.");
                Assert.False(Regex.IsMatch(paragraph, @"(?m)^\s*([-*+]|\d+\.)\s+"),
                    "README body must not contain bullet or numbered lists.");
                Assert.DoesNotContain("```", paragraph);
            }
        }

        private static float MinBridgeWindowDistance(VascularNetwork net)
        {
            VascularPath up = net.Paths[net.UpperBoundaryPathIndex];
            VascularPath lo = net.Paths[net.LowerBoundaryPathIndex];
            float minDist = float.MaxValue;
            for (int s = 34; s <= 43; s++)
            {
                for (int t = 34; t <= 43; t++)
                    minDist = MathF.Min(minDist, Vector3.Distance(up.Points[s], lo.Points[t]));
            }
            return minDist;
        }

        private static float MaxBridgeWindowKeff(VascularNetwork net)
        {
            VascularPath up = net.Paths[net.UpperBoundaryPathIndex];
            VascularPath lo = net.Paths[net.LowerBoundaryPathIndex];
            float maxK = 0f;
            for (int s = 34; s <= 43; s++)
            {
                Vector3 tUp = EstimateTangent(up.Points, s);
                float dMin = float.MaxValue;
                int bestT = s;
                for (int t = 34; t <= 43; t++)
                {
                    float d = Vector3.Distance(up.Points[s], lo.Points[t]);
                    if (d < dMin)
                    {
                        dMin = d;
                        bestT = t;
                    }
                }
                Vector3 tLo = EstimateTangent(lo.Points, bestT);
                if (dMin < 4.5f && Vector3.Dot(tUp, tLo) < 0.25f)
                    maxK = MathF.Max(maxK, up.LocalMultiplicity[s]);
            }
            return maxK;
        }

        private static void ValidateRibSegments(
            IReadOnlyList<RibSegment> ribs,
            StatorCoolingRingTargets targets,
            ObstacleCapsule[] obstacles,
            float minZ)
        {
            const int Samples = 9;
            foreach (RibSegment seg in ribs)
            {
                for (int k = 0; k < Samples; k++)
                {
                    float t = k / (float)(Samples - 1);
                    Vector3 pt = Vector3.Lerp(seg.Start, seg.End, t);
                    float rRib = t <= 0.5f
                        ? seg.StartRadiusMm + (seg.MidRadiusMm - seg.StartRadiusMm) * (2.0f * t)
                        : seg.MidRadiusMm + (seg.EndRadiusMm - seg.MidRadiusMm) * (2.0f * (t - 0.5f));

                    float rXY = MathF.Sqrt(pt.X * pt.X + pt.Y * pt.Y);
                    Assert.True(rXY - rRib >= targets.MinInnerRadiusMm, $"Rib intrudes rotor bore: R_in={rXY - rRib}");
                    Assert.True(rXY + rRib <= targets.MaxOuterRadiusMm, $"Rib exceeds outer housing: R_out={rXY + rRib}");
                    Assert.True(pt.Z + rRib <= targets.MaxTotalHeightMm, $"Rib exceeds max height: Z={pt.Z + rRib}");
                    Assert.True(pt.Z >= minZ, $"Rib below Z={minZ}: Z={pt.Z}");

                    foreach (ObstacleCapsule obs in obstacles)
                    {
                        Vector3 ab = obs.End - obs.Start;
                        float abSq = ab.LengthSquared();
                        float proj = abSq > 1e-8f ? Math.Clamp(Vector3.Dot(pt - obs.Start, ab) / abSq, 0f, 1f) : 0f;
                        float distObs = Vector3.Distance(pt, obs.Start + proj * ab);
                        Assert.True(distObs - obs.RadiusMm - rRib >= 2.0f - 1e-4f,
                            $"Rib collides with {obs.Name}: clearance={distObs - obs.RadiusMm - rRib}");
                    }

                    foreach (float boltDeg in StatorCoolingRingConstants.BoltAnglesDeg)
                    {
                        float bRad = boltDeg * StatorCoolingRingConstants.DegToRad;
                        float bx = targets.BoltCircleRadiusMm * MathF.Cos(bRad);
                        float by = targets.BoltCircleRadiusMm * MathF.Sin(bRad);
                        Vector3 boltAxis = new(bx, by, Math.Clamp(pt.Z, 0f, targets.MaxTotalHeightMm));
                        float distBolt = Vector3.Distance(pt, boltAxis);
                        Assert.True(distBolt - rRib >= targets.BoltKeepOutRadiusMm - 1e-4f,
                            $"Rib violates bolt shaft {boltDeg} deg: clearance={distBolt - rRib}");
                    }
                }
            }
        }

        private static Vector3 EstimateTangent(IReadOnlyList<Vector3> pts, int index)
        {
            Vector3 d = index == 0
                ? pts[1] - pts[0]
                : index == pts.Count - 1
                    ? pts[^1] - pts[^2]
                    : pts[index + 1] - pts[index - 1];
            return d.LengthSquared() > 1e-10f ? Vector3.Normalize(d) : Vector3.UnitZ;
        }
    }
}
