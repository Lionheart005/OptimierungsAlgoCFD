using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Projects.StatorCoolingRing;
using FluidForge.Solvers.OpenFoam;
using Xunit;

namespace FluidForge.Tests
{
    public sealed class StatorCoolingRingTests
    {
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

        [Fact]
        public void AnalyticalPackaging_BaseParameters_Are_Feasible_With_Positive_BoltClearance()
        {
            ProjectConfig project = LoadProjectConfig();

            StatorCoolingRingPackagingReport report =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(project.BaseParameters, project);

            Assert.True(report.IsFeasible);
            Assert.True(report.OuterRadiusMm <= project.OptimizationTargets["MaxOuterRadiusMm"]);
            Assert.True(report.TotalHeightMm <= project.OptimizationTargets["MaxTotalHeightMm"]);
            Assert.True(report.MinBoltClearanceMm >= project.OptimizationTargets["MinRequiredBoltClearanceMm"]);
            Assert.True(report.MinBranchRadiusMm >= project.OptimizationTargets["MinBranchRadiusMm"]);
        }

        [Fact]
        public void AnalyticalPackaging_Detects_BoltClearance_OuterRadius_Height_And_BranchRadius_Violations()
        {
            ProjectConfig project = LoadProjectConfig();

            // 1. Excessive radial shift + ring radius violates bolt clearance and outer radius
            var radiallyOversized = new Dictionary<string, float>(project.BaseParameters)
            {
                ["RingRadialShiftMm"] = 22.0f,
                ["RingRadiusRatio"] = 1.20f
            };
            StatorCoolingRingPackagingReport radialReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(radiallyOversized, project);
            Assert.False(radialReport.IsFeasible);
            Assert.True(radialReport.MinBoltClearanceMm < project.OptimizationTargets["MinRequiredBoltClearanceMm"]);
            Assert.True(radialReport.OuterRadiusMm > project.OptimizationTargets["MaxOuterRadiusMm"]);

            // 2. Within bounds radial shift + large radius at low branch height still violates 3D bolt clearance
            var boltCollision = new Dictionary<string, float>(project.BaseParameters)
            {
                ["RingRadialShiftMm"] = 10.0f,
                ["RingRadiusRatio"] = 1.20f,
                ["BranchHeightMm"] = 20.0f
            };
            StatorCoolingRingPackagingReport boltReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(boltCollision, project);
            Assert.False(boltReport.IsFeasible);
            Assert.True(boltReport.MinBoltClearanceMm < project.OptimizationTargets["MinRequiredBoltClearanceMm"]);

            // 3. Max height + elevation + max radius exceeds MaxTotalHeightMm = 48 mm
            var tooTall = new Dictionary<string, float>(project.BaseParameters)
            {
                ["BranchHeightMm"] = 30.0f,
                ["FarSideElevationMm"] = 10.0f,
                ["RingRadiusRatio"] = 1.20f,
                ["RingTaperRatio"] = 1.30f
            };
            StatorCoolingRingPackagingReport tallReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(tooTall, project);
            Assert.False(tallReport.IsFeasible);
            Assert.True(tallReport.TotalHeightMm > project.OptimizationTargets["MaxTotalHeightMm"]);

            // 4. Minimum radius + strong taper violates MinBranchRadiusMm = 1.4 mm
            var tooThin = new Dictionary<string, float>(project.BaseParameters)
            {
                ["RingRadiusRatio"] = 0.55f,
                ["RingTaperRatio"] = 0.35f
            };
            StatorCoolingRingPackagingReport thinReport =
                StatorCoolingRingGeometryGenerator.EvaluateAnalyticalPackaging(tooThin, project);
            Assert.False(thinReport.IsFeasible);
            Assert.True(thinReport.MinBranchRadiusMm < project.OptimizationTargets["MinBranchRadiusMm"]);
        }

        [Fact]
        public void OutletAngles_Are_12_Uniform_MirrorSymmetric_And_Avoid_InletAxis()
        {
            IReadOnlyList<float> angles = StatorCoolingRingGeometryGenerator.GetOutletAnglesDeg();

            Assert.Equal(12, angles.Count);
            Assert.DoesNotContain(180.0f, angles);
            Assert.DoesNotContain(0.0f, angles);

            for (int k = 0; k < 6; k++)
            {
                // XZ-plane mirror symmetry: theta_k + theta_{11-k} == 360 deg
                Assert.Equal(360.0f, angles[k] + angles[11 - k], 4);
            }
        }

        [Fact]
        public void PreCfdConstraints_Enforce_All_Packaging_And_Volume_Limits()
        {
            ProjectConfig project = LoadProjectConfig();
            var evaluator = new StatorCoolingRingGeometryConstraints();

            var feasible = new ModelRecord();
            feasible.PassiveParameters["OuterRadiusMm"] = 88.5f;
            feasible.PassiveParameters["TotalHeightMm"] = 28.0f;
            feasible.PassiveParameters["MinBoltClearanceMm"] = 3.5f;
            feasible.PassiveParameters["MinBranchRadiusMm"] = 2.0f;
            feasible.PassiveParameters["Volume"] = 45000.0f;

            IReadOnlyList<ConstraintEvaluation> ok = evaluator.EvaluateGeometryConstraints(feasible, project);
            Assert.Equal(6, ok.Count);
            Assert.All(ok, c =>
            {
                Assert.True(c.IsPreSolve);
                Assert.True(c.IsSatisfied);
            });

            var infeasible = new ModelRecord();
            infeasible.PassiveParameters["OuterRadiusMm"] = 95.0f;
            infeasible.PassiveParameters["TotalHeightMm"] = 51.0f;
            infeasible.PassiveParameters["MinBoltClearanceMm"] = 0.5f;
            infeasible.PassiveParameters["MinBranchRadiusMm"] = 1.1f;
            infeasible.PassiveParameters["Volume"] = 195000.0f;

            IReadOnlyList<ConstraintEvaluation> bad = evaluator.EvaluateGeometryConstraints(infeasible, project);
            Assert.Equal(6, bad.Count);
            Assert.False(bad.Single(c => c.Name == "OuterRadiusMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "TotalHeightMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "MinBoltClearanceMm").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "MinBranchRadiusMm").IsSatisfied);
            Assert.True(bad.Single(c => c.Name == "Volume_Min").IsSatisfied);
            Assert.False(bad.Single(c => c.Name == "Volume_Max").IsSatisfied);
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
                    "MinBoltClearanceMm"
                },
                calc.RawComparisonMetrics);

            // Standard feasible case
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

            // Edge case: dp <= 0 must still produce finite positive scalar fitness without dividing by zero
            var nonPositiveDp = new ModelRecord();
            nonPositiveDp.PassiveParameters["PressureDrop"] = -12.0f;
            nonPositiveDp.PassiveParameters["FlowImbalance"] = 0.0f;
            calc.CalculateFitness(nonPositiveDp, simConfig);

            Assert.True(float.IsFinite(nonPositiveDp.Fitness) && nonPositiveDp.Fitness > 0f);
            Assert.Equal(1000.0f / 101.0f, nonPositiveDp.Fitness, 4);

            // Missing metrics fall back to DefaultFailedFitness
            var missing = new ModelRecord();
            missing.PassiveParameters["PressureDrop"] = 1000.0f;
            calc.CalculateFitness(missing, simConfig);
            Assert.Equal(ObjectiveEvaluatorBase.DefaultFailedFitness, missing.Fitness, 6);
        }

        [Fact]
        public void ConfigFiles_And_ProjectWiring_Match_Phase4_Showcase_Specification()
        {
            ProjectConfig project = LoadProjectConfig();
            Assert.Equal(8, project.BaseParameters.Count);
            Assert.Empty(project.DimensionalParameters);

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
            Assert.Equal(12, sim.MaxIterations);
            Assert.Equal(6, sim.VariantsPerIteration);
            Assert.Equal(18, sim.MpiCores);

            var context = new ProjectContext("StatorCoolingRing", ProjectDir, null, sim, project);
            OpenFoamOptions openFoam = context.LoadSolverOptions<OpenFoamOptions>("openfoam");
            Assert.Equal(998.2f, openFoam.Density, 3);
            Assert.Equal(0.001002f, openFoam.DynamicViscosity, 6);
            Assert.Equal("VolumetricFlowRate", openFoam.InletCondition);
            Assert.Equal(0.00016667f, openFoam.InletVolumetricFlowRate, 7);
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
    }
}
