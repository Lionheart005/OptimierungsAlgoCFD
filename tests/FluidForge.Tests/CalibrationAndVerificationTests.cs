using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Tests for TODO-42 (Mesh calibration, CalibrationEvaluator, MeshCalibration)
    /// and TODO-43 (Fine verification of winners, ChampionFineVerifier).
    /// </summary>
    public class CalibrationAndVerificationTests : IDisposable
    {
        private readonly string _workingDirectory;

        public CalibrationAndVerificationTests()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), "calib_verif_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDirectory))
            {
                try { Directory.Delete(_workingDirectory, recursive: true); } catch { }
            }
        }

        #region Part 1: CalibrationEvaluator Tests

        [Fact]
        public void EvaluateCalibration_MonotonicConvergence_SelectsCoarsestWithinTolerance()
        {
            // Reference stage f=0.5: Cd = 0.0400, Cl = 0.4800
            // Stage f=0.71: Cd = 0.04005 (0.125% deviation), Cl = 0.4802 (0.042% deviation)
            // Stage f=1.0:  Cd = 0.0402  (0.50% deviation),  Cl = 0.4810 (0.208% deviation) -> within 2%!
            // Stage f=1.41: Cd = 0.0410  (2.50% deviation) -> above 2% tolerance!
            // Stage f=2.0:  Cd = 0.0500  (25.0% deviation)
            var stages = new List<CalibrationStageData>
            {
                new(0, 2.0f,  10000, 10.0, new Dictionary<string, float> { ["Cd"] = 0.0500f, ["Cl"] = 0.5000f }, 10f, true, 0.9f, true),
                new(1, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Cd"] = 0.0410f, ["Cl"] = 0.4900f }, 12f, true, 0.9f, true),
                new(2, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0402f, ["Cl"] = 0.4810f }, 15f, true, 0.9f, true),
                new(3, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Cd"] = 0.04005f, ["Cl"] = 0.4802f }, 16f, true, 0.9f, true),
                new(4, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.04000f, ["Cl"] = 0.4800f }, 16.2f, true, 0.9f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd", "Cl" }, 0.02f);

            Assert.True(decision.Success, decision.SummaryMessage);
            Assert.NotNull(decision.SelectedStage);
            // Stage 1.0f is the coarsest stage whose relative deviation for all metrics is <= 2%
            Assert.Equal(1.0f, decision.SelectedStage!.Factor);
            Assert.NotNull(decision.Gci);
            Assert.True(decision.Gci!.IsMonotonic);
            Assert.Null(decision.Gci.Warning);
        }

        [Fact]
        public void EvaluateCalibration_OscillatingSequence_SetsWarning()
        {
            // Oscillating sequence for the finest stages:
            // Stage 1.0: Cd = 0.0410
            // Stage 0.71: Cd = 0.0390
            // Stage 0.5: Cd = 0.0400
            // eps_32 = 0.0410 - 0.0390 = +0.0020
            // eps_21 = 0.0390 - 0.0400 = -0.0010 -> eps_32 * eps_21 < 0 (sign change)
            var stages = new List<CalibrationStageData>
            {
                new(0, 2.0f,  10000, 10.0, new Dictionary<string, float> { ["Cd"] = 0.0500f }, 10f, true, 0.9f, true),
                new(1, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Cd"] = 0.0420f }, 12f, true, 0.9f, true),
                new(2, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0410f }, 15f, true, 0.9f, true),
                new(3, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Cd"] = 0.0390f }, 16f, true, 0.9f, true),
                new(4, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.2f, true, 0.9f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.05f);

            Assert.NotNull(decision.Gci);
            Assert.False(decision.Gci!.IsMonotonic);
            Assert.NotNull(decision.Gci.Warning);
            Assert.Contains("not monotonic", decision.Gci.Warning);
        }

        [Fact]
        public void EvaluateCalibration_ToleranceNotReached_ReturnsFailure()
        {
            // All stages (except reference) deviate by more than 2%
            var stages = new List<CalibrationStageData>
            {
                new(0, 2.0f,  10000, 10.0, new Dictionary<string, float> { ["Cd"] = 0.0500f }, 10f, true, 0.9f, true),
                new(1, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Cd"] = 0.0450f }, 12f, true, 0.9f, true),
                new(2, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0430f }, 15f, true, 0.9f, true),
                new(3, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Cd"] = 0.0415f }, 16f, true, 0.9f, true), // 3.75% deviation
                new(4, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.2f, true, 0.9f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Null(decision.SelectedStage);
            Assert.NotNull(decision.FallbackStage);
            Assert.Equal(0.71f, decision.FallbackStage!.Factor);
            Assert.Contains("No coarse level achieves", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateConfirmation3D_ToleranceNotReached_ReturnsFallbackStage()
        {
            // B15b: In 3D confirmation (fCoarse=0.71, fMed=0.50, fFine=0.35):
            // if neither fCoarse nor fMed holds tolerance, FallbackStage returns stage fMed
            var stages3D = new List<CalibrationStageData>
            {
                new(1, 0.71f, 50000, 30.0, new Dictionary<string, float> { ["Cd"] = 0.0600f }, 10f, true, 1.2f, true),
                new(2, 0.50f, 120000, 70.0, new Dictionary<string, float> { ["Cd"] = 0.0550f }, 12f, true, 1.1f, true),
                new(3, 0.35f, 300000, 180.0, new Dictionary<string, float> { ["Cd"] = 0.0450f }, 14f, true, 1.0f, true),
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(0.50f, stages3D, new[] { "Cd" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Null(decision.SelectedStage);
            Assert.NotNull(decision.FallbackStage);
            Assert.Equal(0.50f, decision.FallbackStage!.Factor);
            Assert.Equal(2, decision.FallbackStage.LevelIndex);
        }

        [Fact]
        public void EvaluateCalibration_UnconvergedExtraStage_RecoversLastValidReference()
        {
            // B15a/b: If an extra stage (0.25f) did not converge, but stage 6 (0.35f) is valid,
            // stage 6 is used as the reference stage.
            var stages = new List<CalibrationStageData>
            {
                new(1, 2.00f, 10000, 10.0, new Dictionary<string, float> { ["Cd"] = 0.0500f }, 10f, true, 0.9f, true),
                new(2, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Cd"] = 0.0450f }, 12f, true, 0.9f, true),
                new(3, 1.00f, 70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0420f }, 15f, true, 0.9f, true),
                new(4, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Cd"] = 0.0410f }, 16f, true, 0.9f, true),
                new(5, 0.50f, 550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0402f }, 16.2f, true, 0.9f, true),
                new(6, 0.35f, 1100000, 800.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.5f, true, 0.9f, true),
                new(7, 0.25f, 0, 900.0, new Dictionary<string, float> { ["Cd"] = 0.0000f }, 0f, false, 0.0f, false), // failed stage 7
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.01f);

            // Stage 6 (0.35) was chosen as reference, not the failed stage 7
            Assert.Equal(0.35f, decision.ReferenceStage.Factor);
            Assert.True(decision.ReferenceStage.Converged);
            Assert.True(decision.Success);
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(0.50f, decision.SelectedStage!.Factor);
        }

        [Fact]
        public void EvaluateCalibration_UnconvergedStage_IsSkipped()
        {
            // Stage 1.0f would have small deviation, but did not converge
            var stages = new List<CalibrationStageData>
            {
                new(0, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Cd"] = 0.0450f }, 12f, true, 0.9f, true),
                new(1, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0401f }, 15f, false, 0.9f, true), // Converged = false!
                new(2, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Cd"] = 0.04005f }, 16f, true, 0.9f, true), // within tol
                new(3, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.04000f }, 16.2f, true, 0.9f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.True(decision.Success);
            Assert.NotNull(decision.SelectedStage);
            // Stage 1.0f was skipped; stage 0.71f was selected
            Assert.Equal(0.71f, decision.SelectedStage!.Factor);
        }

        [Fact]
        public void EvaluateCalibration_UnconvergedReference_FailsImmediately()
        {
            var stages = new List<CalibrationStageData>
            {
                new(0, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0401f }, 15f, true, 0.9f, true),
                new(1, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.2f, false, 0.9f, true), // Ref not converged!
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Contains("reference level", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateCalibration_BadYPlusReference_FailsImmediately()
        {
            var stages = new List<CalibrationStageData>
            {
                new(0, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0401f }, 15f, true, 0.9f, true),
                new(1, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.2f, true, 6.0f, false), // Ref YPlus invalid!
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Contains("reference level", decision.SummaryMessage);
        }

        [Fact]
        public void ComputeGci_MatchesAnalyticalFormula()
        {
            // Celik et al. (2008):
            // r = sqrt(2), p = 2.0, Fs = 1.25
            // f1 = 100, f2 = 101, f3 = 103
            // eps21 = 1.0, eps32 = 2.0 -> ratio = 2.0 -> p = ln(2)/ln(sqrt(2)) = 2.0
            // ea21 = 1 / 100 = 0.01 (1%)
            // GCI_fine = (1.25 * 0.01) / ((sqrt(2))^2 - 1) * 100 = (0.0125 / 1.0) * 100 = 1.25 %
            float r = MathF.Sqrt(2f);
            var gci = CalibrationEvaluator.ComputeGci(103f, 101f, 100f, r: r, safetyFactorFs: 1.25f);

            Assert.True(gci.IsMonotonic);
            Assert.True(gci.IsAsymptotic);
            Assert.Null(gci.Warning);
            Assert.InRange(gci.ObservedOrderP, 1.95f, 2.05f);
            Assert.InRange(gci.GciFinePercent, 1.20f, 1.30f);
        }

        [Fact]
        public void ComputeGci_NonMonotonicSequence_MarksNonAsymptoticAndSetsNaN()
        {
            var gci = CalibrationEvaluator.ComputeGci(103f, 99f, 100f);

            Assert.False(gci.IsMonotonic);
            Assert.False(gci.IsAsymptotic);
            Assert.True(float.IsNaN(gci.ObservedOrderP));
            Assert.True(float.IsNaN(gci.GciFinePercent));
            Assert.True(float.IsNaN(gci.GciSelectedPercent));
            Assert.True(float.IsNaN(gci.ExtrapolatedValue));
            Assert.True(float.IsNaN(gci.ExtrapolatedDeviationPercent));
            Assert.NotNull(gci.Warning);
            Assert.Contains("not monotonic", gci.Warning);
        }

        [Fact]
        public void ComputeGci_MonotonicNonAsymptoticDiverging_MarksNonAsymptoticAndSetsNaN()
        {
            // eps32 = 102 - 101 = 1, eps21 = 101 - 99 = 2 => ratio = 0.5 <= 1.0 (differences grow!)
            var gci = CalibrationEvaluator.ComputeGci(102f, 101f, 99f);

            Assert.True(gci.IsMonotonic);
            Assert.False(gci.IsAsymptotic);
            Assert.True(float.IsNaN(gci.ObservedOrderP));
            Assert.True(float.IsNaN(gci.GciFinePercent));
            Assert.True(float.IsNaN(gci.GciSelectedPercent));
            Assert.True(float.IsNaN(gci.ExtrapolatedValue));
            Assert.True(float.IsNaN(gci.ExtrapolatedDeviationPercent));
            Assert.NotNull(gci.Warning);
            Assert.Contains("not in the asymptotic range", gci.Warning);
        }

        [Fact]
        public void ComputeGci_ExactConvergence_ReturnsZeroGciAndValidExtrapolation()
        {
            var gci = CalibrationEvaluator.ComputeGci(0.25f, 0.25f, 0.25f);

            Assert.True(gci.IsMonotonic);
            Assert.True(gci.IsAsymptotic);
            Assert.Null(gci.Warning);
            Assert.Equal(1.0f, gci.ObservedOrderP);
            Assert.Equal(0f, gci.GciFinePercent);
            Assert.Equal(0f, gci.GciSelectedPercent);
            Assert.Equal(0.25f, gci.ExtrapolatedValue);
            Assert.Equal(0f, gci.ExtrapolatedDeviationPercent);
        }

        [Fact]
        public void MeshCalibration_Run_SerializesNonAsymptoticGciSafelyToJsonAndPrintsNa()
        {
            var sim = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Auto", Tolerance = 0.05f }
            };
            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var context = new ProjectContext("Test", null, null, sim, project);
            var simContext = new SimulationContext(sim, project, _workingDirectory);
            simContext.EnsureDirectories();

            // Monotonic, but not asymptotic on the finest three levels (0.50 -> 0.48, 0.71 -> 0.49, 1.00 -> 0.495 => ratio = 0.5 <= 1)
            var (decision, _) = MeshCalibration.Run(context, simContext, (factor, _) =>
            {
                var rec = new ModelRecord { Iteration = 1, Variant = 1, Fitness = 1.0f };
                rec.PassiveParameters["MeshCells"] = 50000f / factor;
                rec.PassiveParameters["YPlus_P95"] = 1.0f;
                rec.PassiveParameters["Su2_Converged"] = 1f;
                rec.PassiveParameters["Drag"] = factor switch
                {
                    > 1.8f => 0.52f,
                    > 1.2f => 0.50f,
                    > 0.9f => 0.495f,
                    > 0.6f => 0.49f,
                    _ => 0.48f
                };
                return rec;
            }, new[] { "Drag" }, dimensions: 3);

            Assert.NotNull(decision.Gci);
            Assert.False(decision.Gci!.IsAsymptotic);

            string latestJson = Path.Combine(_workingDirectory, MeshCalibration.CalibrationDirectoryName, MeshCalibration.LatestCalibrationJson);
            Assert.True(File.Exists(latestJson));

            JsonObject root = JsonNode.Parse(File.ReadAllText(latestJson))!.AsObject();
            JsonObject gciNode = root["Gci"]!.AsObject();
            Assert.Null(gciNode["ObservedOrderP"]);
            Assert.Null(gciNode["GciFinePercent"]);
            Assert.Null(gciNode["GciSelectedPercent"]);
            Assert.Null(gciNode["ExtrapolatedValue"]);
            Assert.False(gciNode["IsAsymptotic"]!.GetValue<bool>());
            Assert.Contains("not in the asymptotic range", gciNode["Warning"]!.GetValue<string>());

            using var sw = new StringWriter();
            MeshCalibration.PrintSummary(decision, sw);
            string printed = sw.ToString();
            Assert.Contains("n/a", printed);
            Assert.Contains(OutputConventions.WarningTag, printed);
            Assert.Contains("not in the asymptotic range", printed);
        }

        [Fact]
        public void EvaluateCalibration_Naca2DUCurve_RejectsCoincidentalCoarseMatch()
        {
            // Real Naca2D U-curve observed on BIC_12: Level 1 (2.00) coincidentally hits Level 6 (0.35),
            // while intermediate levels 0.50..1.41 drop up to ~23% below reference.
            var stages = new List<CalibrationStageData>
            {
                new(1, 2.00f, 10000, 10.0, new Dictionary<string, float> { ["Drag"] = 0.01826f }, 10f, true, 1.0f, true),
                new(2, 1.41f, 20000, 20.0, new Dictionary<string, float> { ["Drag"] = 0.01550f }, 11f, true, 1.0f, true),
                new(3, 1.00f, 40000, 40.0, new Dictionary<string, float> { ["Drag"] = 0.01446f }, 12f, true, 1.0f, true),
                new(4, 0.71f, 80000, 80.0, new Dictionary<string, float> { ["Drag"] = 0.01525f }, 13f, true, 1.0f, true),
                new(5, 0.50f, 160000, 160.0, new Dictionary<string, float> { ["Drag"] = 0.01717f }, 14f, true, 1.0f, true),
                new(6, 0.35f, 320000, 320.0, new Dictionary<string, float> { ["Drag"] = 0.01884f }, 15f, true, 1.0f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Drag" }, 0.05f);

            Assert.False(decision.Success);
            Assert.Null(decision.SelectedStage);
            Assert.Contains("No coarse level achieves", decision.SummaryMessage);
            Assert.Contains("non-monotonic", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateCalibration_IntermediateDip_SelectsContiguousSuffixOnly()
        {
            // Fine-to-coarse suffix 0.71 & 1.00 match within 2%, 1.41 dips out of tolerance (12.5%),
            // and 2.00 coincidentally matches again -> must select 1.00 (Level 3), not 2.00 (Level 1).
            var stages = new List<CalibrationStageData>
            {
                new(1, 2.00f, 10000, 10.0, new Dictionary<string, float> { ["Drag"] = 0.0401f }, 10f, true, 1.0f, true),
                new(2, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Drag"] = 0.0450f }, 11f, true, 1.0f, true),
                new(3, 1.00f, 70000, 60.0, new Dictionary<string, float> { ["Drag"] = 0.0404f }, 12f, true, 1.0f, true),
                new(4, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Drag"] = 0.0402f }, 13f, true, 1.0f, true),
                new(5, 0.50f, 550000, 400.0, new Dictionary<string, float> { ["Drag"] = 0.0400f }, 14f, true, 1.0f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Drag" }, 0.02f);

            Assert.True(decision.Success, decision.SummaryMessage);
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(1.00f, decision.SelectedStage!.Factor);
            Assert.Equal(3, decision.SelectedStage.LevelIndex);
            Assert.Contains("non-monotonic", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateConfirmation3D_MediumExceedsTolerance_RejectsCoincidentalCoarseMatch()
        {
            // Coarse (0.71) coincidentally matches fine (0.35), while medium f2D (0.50) exceeds 2% tolerance.
            var stages3D = new List<CalibrationStageData>
            {
                new(1, 0.71f, 50000, 30.0, new Dictionary<string, float> { ["Drag"] = 0.0452f }, 10f, true, 1.0f, true),
                new(2, 0.50f, 120000, 70.0, new Dictionary<string, float> { ["Drag"] = 0.0520f }, 12f, true, 1.0f, true),
                new(3, 0.35f, 300000, 180.0, new Dictionary<string, float> { ["Drag"] = 0.0450f }, 14f, true, 1.0f, true),
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(0.50f, stages3D, new[] { "Drag" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Null(decision.SelectedStage);
            Assert.Contains("non-monotonic", decision.SummaryMessage);
        }

        #endregion

        #region Part 2: MeshCalibration Config Validation and Hints Tests

        [Fact]
        public void ValidateConfig_AutoMode_WithExplicitRefinementFactor_ThrowsConfigurationException()
        {
            var config = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Auto", Tolerance = 0.02f }
            };

            var report = new ConfigLoadReport();
            report.Origins["MeshRefinementFactor"] = "simulation.json";

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.ValidateConfig(config, report, "TestProject"));

            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
            Assert.Contains("MeshRefinementFactor", ex.Message);
            Assert.Contains("simulation.json", ex.Message);
        }

        [Fact]
        public void ValidateConfig_InvalidMode_ThrowsConfigurationException()
        {
            var config = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "SuperAutomatic" }
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.ValidateConfig(config, null, "TestProject"));

            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
            Assert.Contains("SuperAutomatic", ex.Message);
        }

        [Fact]
        public void ValidateConfig_ManualMode_WithExplicitRefinementFactor_DoesNotThrow()
        {
            var config = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Manual" },
                MeshRefinementFactor = 1.2f
            };

            var report = new ConfigLoadReport();
            report.Origins["MeshRefinementFactor"] = "simulation.json";

            // Should pass without exception
            MeshCalibration.ValidateConfig(config, report, "TestProject");
        }

        [Fact]
        public void ComputeFingerprint_DeterministicAndSensitiveToParameters()
        {
            var p1 = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["Length"] = 100f, ["Width"] = 50f } };
            var p2 = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["Length"] = 100f, ["Width"] = 50f } };
            var p3 = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["Length"] = 120f, ["Width"] = 50f } };

            var ctx1 = new ProjectContext("Test1", _workingDirectory, null, new SimulationConfig(), p1);
            var ctx2 = new ProjectContext("Test2", _workingDirectory, null, new SimulationConfig(), p2);
            var ctx3 = new ProjectContext("Test3", _workingDirectory, null, new SimulationConfig(), p3);

            string fp1 = MeshCalibration.ComputeFingerprint(ctx1);
            string fp2 = MeshCalibration.ComputeFingerprint(ctx2);
            string fp3 = MeshCalibration.ComputeFingerprint(ctx3);

            Assert.Equal(fp1, fp2);
            Assert.NotEqual(fp1, fp3);
        }

        [Fact]
        public void CheckManualCalibrationHints_OutdatedFingerprint_GeneratesOutdatedHint()
        {
            string calibDir = Path.Combine(_workingDirectory, OutputConventions.CalibrationDirectory);
            Directory.CreateDirectory(calibDir);

            // Write a latest_calibration.json with a different fingerprint
            string json = @"{
                ""Fingerprint"": ""OLD_FINGERPRINT_12345"",
                ""SelectedFactor"": 0.71,
                ""Tolerance"": 0.02
            }";
            File.WriteAllText(Path.Combine(calibDir, "latest_calibration.json"), json);

            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var config = new SimulationConfig { MeshRefinementFactor = 0.71f };
            var context = new ProjectContext("Test", _workingDirectory, null, config, project);

            using var sw = new StringWriter();
            MeshCalibration.CheckManualCalibrationHints(context, _workingDirectory, sw);
            string output = sw.ToString();
            Assert.Contains("stale", output);
        }

        [Fact]
        public void CheckManualCalibrationHints_FactorMismatch_GeneratesMismatchHint()
        {
            string calibDir = Path.Combine(_workingDirectory, OutputConventions.CalibrationDirectory);
            Directory.CreateDirectory(calibDir);

            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var config = new SimulationConfig { MeshRefinementFactor = 1.41f }; // differs from 0.71!
            var context = new ProjectContext("Test", _workingDirectory, null, config, project);
            string fingerprint = MeshCalibration.ComputeFingerprint(context);

            string json = $@"{{
                ""Fingerprint"": ""{fingerprint}"",
                ""SelectedFactor"": 0.71,
                ""Tolerance"": 0.02
            }}";
            File.WriteAllText(Path.Combine(calibDir, "latest_calibration.json"), json);

            using var sw = new StringWriter();
            MeshCalibration.CheckManualCalibrationHints(context, _workingDirectory, sw);
            string output = sw.ToString();
            Assert.Contains("0.71", output);
            Assert.Contains("1.41", output);
        }

        #endregion

        #region Part 3: EffectiveConfigWriter Calibration Output Tests

        [Fact]
        public void EffectiveConfigWriter_Records_EffectiveScaleFactor_And_Source()
        {
            var projectDir = Path.Combine(_workingDirectory, "EffConfigProject");
            Directory.CreateDirectory(projectDir);

            var config = new SimulationConfig { MeshRefinementFactor = 1.0f };
            var project = new ProjectConfig { ProjectName = "EffConfigProject" };
            var context = new ProjectContext("EffConfigProject", projectDir, null, config, project);

            var simContext = new SimulationContext(config, project, projectDir);
            simContext.EffectiveMeshRefinementFactor = 0.71f;
            simContext.MeshRefinementSource = "CALIBRATED from latest_calibration.json";
            simContext.CalibrationReportPath = $"{OutputConventions.CalibrationDirectory}/latest_calibration.json";

            var doc = EffectiveConfigWriter.Build(context, simContext);

            Assert.True(doc.ContainsKey("EffectiveScaleFactor"));
            Assert.InRange(doc["EffectiveScaleFactor"]!.GetValue<float>(), 0.70f, 0.72f);

            Assert.True(doc.ContainsKey("ScaleFactorSource"));
            Assert.Equal("CALIBRATED from latest_calibration.json", doc["ScaleFactorSource"]!.GetValue<string>());

            Assert.True(doc.ContainsKey("CalibrationReport"));
            Assert.Equal($"{OutputConventions.CalibrationDirectory}/latest_calibration.json", doc["CalibrationReport"]!.GetValue<string>());
        }

        [Fact]
        public void ResolveMonitoredMetrics_Reads_ResultMetrics_From_Commented_Su2Json()
        {
            // Like the real project files: comments and a trailing comma.
            // Before the server test of 2026-11-06, parsing failed silently -> only "Drag".
            var projectDir = Path.Combine(_workingDirectory, "MetricsProject");
            Directory.CreateDirectory(Path.Combine(projectDir, "solvers"));
            File.WriteAllText(Path.Combine(projectDir, "solvers", "su2.json"),
                "{\n  // Comment\n  \"ResultMetrics\": {\n    \"Drag\": \"CD\", // Drag\n    \"Lift\": \"CL\",\n  },\n}\n");

            var context = new ProjectContext("MetricsProject", projectDir, null, new SimulationConfig(), new ProjectConfig());

            Assert.Equal(new[] { "Drag", "Lift" }, MeshCalibration.ResolveMonitoredMetrics(context));
        }

        #endregion

        #region Part 4: ChampionFineVerifier Tests

        [Fact]
        public void CalculateFineFactor_CalculatesCorrectLevels()
        {
            // Level 1: 1.0 / sqrt(2) approx 0.7071
            float f1 = ChampionFineVerifier.CalculateFineFactor(1.0f, 1);
            Assert.InRange(f1, 0.70f, 0.71f);

            // Level 2: 1.0 / (sqrt(2)^2) = 0.5
            float f2 = ChampionFineVerifier.CalculateFineFactor(1.0f, 2);
            Assert.InRange(f2, 0.499f, 0.501f);
        }

        [Fact]
        public void VerifyFinal_ModeOff_DoesNotRunSimulations()
        {
            var config = new SimulationConfig
            {
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" }
            };
            var project = new ProjectConfig { ProjectName = "TestProj" };
            var simContext = new SimulationContext(config, project, _workingDirectory);

            var champion = new ModelRecord { Iteration = 1, Variant = 1, Fitness = 10f };
            simContext.History.Add(champion);

            int runsCalled = 0;
            ChampionFineVerifier.VerifyFinal(
                simContext,
                (p, f, tag) =>
                {
                    runsCalled++;
                    return new ModelRecord();
                },
                evaluateFitness: r => { },
                monitoredMetrics: null);

            Assert.Equal(0, runsCalled);
            Assert.False(champion.PassiveParameters.ContainsKey("FineVerified"));
        }

        [Fact]
        public void VerifyFinal_FailedFineSimulation_Is_Not_Counted_As_Verified()
        {
            // Server test 2026-11-06: all three fine simulations failed at Gmsh, yet
            // "Ranking holds" and FineVerified = 1 were written to the CSV.
            var config = new SimulationConfig
            {
                MeshRefinementFactor = 1.0f,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Final", RefinementSteps = 1, Tolerance = 0.05f }
            };
            var simContext = new SimulationContext(config, new ProjectConfig { ProjectName = "TestProj" }, _workingDirectory);
            var champion = new ModelRecord
            {
                Iteration = 1, Variant = 1, Fitness = 10.0f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 100.0f }
            };
            simContext.History.Add(champion);

            ChampionFineVerifier.VerifyFinal(
                simContext,
                (p, factor, tag) => new ModelRecord { Iteration = 1, Variant = 1, SimulationFailed = true },
                evaluateFitness: r => { },
                monitoredMetrics: new[] { "Drag" });

            Assert.Equal(0f, champion.PassiveParameters["FineVerified"]);
        }

        [Fact]
        public void VerifyFinal_ModeFinal_RunsOneFineSimulation_WritesCsv_DoesNotAlterHistory()
        {
            var config = new SimulationConfig
            {
                MeshRefinementFactor = 1.0f,
                ChampionVerification = new ChampionVerificationConfig
                {
                    Mode = "Final",
                    RefinementSteps = 1,
                    Tolerance = 0.05f
                }
            };
            var project = new ProjectConfig { ProjectName = "TestProj" };
            var simContext = new SimulationContext(config, project, _workingDirectory);

            var champion = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                Fitness = 10.0f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 100.0f }
            };
            simContext.History.Add(champion);
            int initialHistoryCount = simContext.History.Count;

            int runsCalled = 0;
            float capturedFactor = 0f;
            string? capturedTag = null;

            ChampionFineVerifier.VerifyFinal(
                simContext,
                (p, factor, tag) =>
                {
                    runsCalled++;
                    capturedFactor = factor;
                    capturedTag = tag;
                    return new ModelRecord
                    {
                        Iteration = 1,
                        Variant = 1,
                        Fitness = 10.2f,
                        PassiveParameters = new Dictionary<string, float> { ["Drag"] = 102.0f } // 2% deviation
                    };
                },
                evaluateFitness: r => { },
                monitoredMetrics: new[] { "Drag" });

            Assert.Equal(1, runsCalled);
            Assert.Equal(initialHistoryCount, simContext.History.Count); // History remains untouched!
            Assert.Equal("Fine1_Gen1_Var1", capturedTag);
            Assert.InRange(capturedFactor, 0.70f, 0.71f);

            Assert.True(champion.PassiveParameters.ContainsKey("FineVerified"));
            Assert.Equal(1f, champion.PassiveParameters["FineVerified"]);
            Assert.True(champion.PassiveParameters.ContainsKey("FineDeviation"));
            Assert.InRange(champion.PassiveParameters["FineDeviation"], 0.019f, 0.021f);

            // verification.csv must exist in the output directory
            string csvPath = Path.Combine(_workingDirectory, OutputConventions.VerificationCsvFileName);
            Assert.True(File.Exists(csvPath), $"{OutputConventions.VerificationCsvFileName} was not created.");
            string csvContent = File.ReadAllText(csvPath);
            Assert.Contains("NormalRank", csvContent);
            Assert.Contains("RankingHolds", csvContent);
            Assert.Contains("Drag_Normal", csvContent);
        }

        [Fact]
        public void VerifyFinal_ModeFinalTopN_RunsMultipleSimulations_EvaluatesRankingStability()
        {
            var config = new SimulationConfig
            {
                MeshRefinementFactor = 1.0f,
                ChampionVerification = new ChampionVerificationConfig
                {
                    Mode = "FinalTopN",
                    TopN = 2,
                    RefinementSteps = 1,
                    Tolerance = 0.05f
                }
            };
            var project = new ProjectConfig { ProjectName = "TestProj" };
            var simContext = new SimulationContext(config, project, _workingDirectory);

            var r1 = new ModelRecord
            {
                Iteration = 1, Variant = 1, Fitness = 15.0f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 50.0f }
            };
            var r2 = new ModelRecord
            {
                Iteration = 1, Variant = 2, Fitness = 12.0f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 60.0f }
            };
            var r3 = new ModelRecord
            {
                Iteration = 1, Variant = 3, Fitness = 8.0f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 70.0f }
            };
            simContext.History.AddRange(new[] { r1, r2, r3 });

            var capturedTags = new List<string>();

            ChampionFineVerifier.VerifyFinal(
                simContext,
                (p, factor, tag) =>
                {
                    capturedTags.Add(tag);
                    return new ModelRecord
                    {
                        Fitness = tag.StartsWith("Fine1") ? 14.8f : 11.9f,
                        PassiveParameters = new Dictionary<string, float> { ["Drag"] = 51.0f }
                    };
                },
                evaluateFitness: r => { },
                monitoredMetrics: new[] { "Drag" });

            // Exactly 2 variants (TopN = 2) evaluated
            Assert.Equal(2, capturedTags.Count);
            Assert.Equal("Fine1_Gen1_Var1", capturedTags[0]);
            Assert.Equal("Fine2_Gen1_Var2", capturedTags[1]);
            Assert.True(r1.PassiveParameters.ContainsKey("FineVerified"));
            Assert.True(r2.PassiveParameters.ContainsKey("FineVerified"));
            Assert.False(r3.PassiveParameters.ContainsKey("FineVerified"));
        }

        [Fact]
        public void VerifyIterationWinner_ModeEachIteration_RunsOneFineSimulationPerIteration()
        {
            var config = new SimulationConfig
            {
                MeshRefinementFactor = 1.0f,
                ChampionVerification = new ChampionVerificationConfig
                {
                    Mode = "EachIteration",
                    RefinementSteps = 1,
                    Tolerance = 0.05f
                }
            };
            var project = new ProjectConfig { ProjectName = "TestProj" };
            var simContext = new SimulationContext(config, project, _workingDirectory);

            var winner = new ModelRecord
            {
                Iteration = 2,
                Variant = 3,
                Fitness = 20.0f,
                PassiveParameters = new Dictionary<string, float> { ["Drag"] = 40.0f }
            };

            int calls = 0;
            string? capturedTag = null;

            ChampionFineVerifier.VerifyIterationWinner(
                winner,
                2,
                simContext,
                (p, factor, tag) =>
                {
                    calls++;
                    capturedTag = tag;
                    return new ModelRecord
                    {
                        Fitness = 19.8f,
                        PassiveParameters = new Dictionary<string, float> { ["Drag"] = 40.5f }
                    };
                },
                evaluateFitness: r => { },
                monitoredMetrics: new[] { "Drag" });

            Assert.Equal(1, calls);
            Assert.Equal("Fine_Gen2", capturedTag);
            Assert.True(winner.PassiveParameters.ContainsKey("FineVerified"));
            Assert.True(winner.PassiveParameters.ContainsKey("FineDeviation"));
        }

        [Fact]
        public void MeshCalibration_Run_UsesDynamicYPlusLimitFromSu2Options_AndAcceptsServerLikeThreeDValues()
        {
            string projectDir = Path.Combine(_workingDirectory, "DynYPlusAccept");
            Directory.CreateDirectory(Path.Combine(projectDir, "solvers"));
            File.WriteAllText(Path.Combine(projectDir, "solvers", "su2.json"),
                @"{ ""TargetYPlus"": 1.5, ""YPlusWarnFactor"": 3.0 }");

            var sim = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Auto", Tolerance = 0.15f }
            };
            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var context = new ProjectContext("Test", projectDir, null, sim, project);
            var simContext = new SimulationContext(sim, project, _workingDirectory);
            simContext.EnsureDirectories();

            // Closely modeled on BIC_12 MantaAuv calibration with TargetYPlus=1.5 (P95 between 3.36 and 3.61)
            Func<float, string, ModelRecord> runStage = (factor, _) =>
            {
                var rec = new ModelRecord { Iteration = 1, Variant = 1, Fitness = 1.0f };
                rec.PassiveParameters["Su2_Converged"] = 1f;
                if (factor > 1.8f) { rec.PassiveParameters["MeshCells"] = 109285f; rec.PassiveParameters["Drag"] = 0.84116f; rec.PassiveParameters["YPlus_P95"] = 3.36f; }
                else if (factor > 1.2f) { rec.PassiveParameters["MeshCells"] = 209122f; rec.PassiveParameters["Drag"] = 0.67191f; rec.PassiveParameters["YPlus_P95"] = 3.45f; }
                else if (factor > 0.9f) { rec.PassiveParameters["MeshCells"] = 379450f; rec.PassiveParameters["Drag"] = 0.57189f; rec.PassiveParameters["YPlus_P95"] = 3.56f; }
                else if (factor > 0.6f) { rec.PassiveParameters["MeshCells"] = 616994f; rec.PassiveParameters["Drag"] = 0.51469f; rec.PassiveParameters["YPlus_P95"] = 3.61f; }
                else { rec.PassiveParameters["MeshCells"] = 1409491f; rec.PassiveParameters["Drag"] = 0.45457f; rec.PassiveParameters["YPlus_P95"] = 3.59f; }
                return rec;
            };

            var (decision, _) = MeshCalibration.Run(context, simContext, runStage, new[] { "Drag" }, dimensions: 3);

            Assert.True(decision.Success, decision.SummaryMessage);
            Assert.All(decision.AllStages, st => Assert.True(st.YPlusOk));
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(0.71f, decision.SelectedStage!.Factor);
        }

        [Fact]
        public void MeshCalibration_Run_HonorsCustomSu2YPlusLimit_AndReportsLimitOnFailure()
        {
            string projectDir = Path.Combine(_workingDirectory, "DynYPlusCustom");
            Directory.CreateDirectory(Path.Combine(projectDir, "solvers"));
            File.WriteAllText(Path.Combine(projectDir, "solvers", "su2.json"),
                @"{ ""TargetYPlus"": 2.0, ""YPlusWarnFactor"": 4.0 }"); // Limit = 8.0

            var sim = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Auto", Tolerance = 0.05f }
            };
            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var contextOk = new ProjectContext("Test", projectDir, null, sim, project);
            var simContext = new SimulationContext(sim, project, _workingDirectory);
            simContext.EnsureDirectories();

            var (decisionOk, _) = MeshCalibration.Run(contextOk, simContext, (factor, _) =>
            {
                var rec = new ModelRecord { Iteration = 1, Variant = 1, Fitness = 1.0f };
                rec.PassiveParameters["MeshCells"] = 50000f / factor;
                rec.PassiveParameters["Drag"] = 0.20f;
                rec.PassiveParameters["YPlus_P95"] = 7.20f; // < 8.00
                rec.PassiveParameters["Su2_Converged"] = 1f;
                return rec;
            }, new[] { "Drag" }, dimensions: 3);

            Assert.True(decisionOk.Success, decisionOk.SummaryMessage);

            // Now limit back to 4.50 (1.5 * 3.0), measured P95 = 4.90 -> clear failure with numbers
            File.WriteAllText(Path.Combine(projectDir, "solvers", "su2.json"),
                @"{ ""TargetYPlus"": 1.5, ""YPlusWarnFactor"": 3.0 }");
            var contextFail = new ProjectContext("Test", projectDir, null, sim, project);

            var (decisionFail, _) = MeshCalibration.Run(contextFail, simContext, (factor, _) =>
            {
                var rec = new ModelRecord { Iteration = 1, Variant = 1, Fitness = 1.0f };
                rec.PassiveParameters["MeshCells"] = 50000f / factor;
                rec.PassiveParameters["Drag"] = 0.20f;
                rec.PassiveParameters["YPlus_P95"] = 4.90f; // > 4.50
                rec.PassiveParameters["Su2_Converged"] = 1f;
                return rec;
            }, new[] { "Drag" }, dimensions: 3);

            Assert.False(decisionFail.Success);
            Assert.Contains("4.90", decisionFail.SummaryMessage);
            Assert.Contains("4.50", decisionFail.SummaryMessage);
        }

        [Fact]
        public void ValidateYPlusConsistency_WarnsOnTargetMismatch_AndBlocksThresholdWithLowAutoLimit()
        {
            string projectDir = Path.Combine(_workingDirectory, "YPlusConsistency");
            Directory.CreateDirectory(Path.Combine(projectDir, "solvers"));
            File.WriteAllText(Path.Combine(projectDir, "solvers", "gmsh.json"),
                @"{ ""BoundaryLayerMode"": ""Threshold"", ""TargetYPlus"": 1.0 }");
            File.WriteAllText(Path.Combine(projectDir, "solvers", "su2.json"),
                @"{ ""TargetYPlus"": 1.5, ""YPlusWarnFactor"": 3.0 }");

            var context = new ProjectContext("Test", projectDir, null, SimulationConfig.CreateDefault(), new ProjectConfig());
            context.LoadSolverOptions<GmshMesherOptions>("gmsh");
            context.LoadSolverOptions<Su2SolverOptions>("su2");

            using var sw = new StringWriter();
            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.ValidateYPlusConsistency(context, calibrationActive: true, warningWriter: sw));

            Assert.StartsWith(OutputConventions.ConfigTag, ex.Message);
            Assert.Contains("Threshold", ex.Message);
            Assert.Contains("1", sw.ToString());
            Assert.Contains("1.5", sw.ToString());

            // In Manual/Recheck mode (calibrationActive: false) warning only, no abort:
            using var swManual = new StringWriter();
            MeshCalibration.ValidateYPlusConsistency(context, calibrationActive: false, warningWriter: swManual);
            Assert.Contains("TargetYPlus", swManual.ToString());

            // With explicitly raised YPlusWarnFactor for Threshold (>= 30) also no abort:
            File.WriteAllText(Path.Combine(projectDir, "solvers", "su2.json"),
                @"{ ""TargetYPlus"": 1.5, ""YPlusWarnFactor"": 50.0 }");
            var contextThresholdAllowed = new ProjectContext("Test", projectDir, null, SimulationConfig.CreateDefault(), new ProjectConfig());
            contextThresholdAllowed.LoadSolverOptions<GmshMesherOptions>("gmsh");
            contextThresholdAllowed.LoadSolverOptions<Su2SolverOptions>("su2");
            MeshCalibration.ValidateYPlusConsistency(contextThresholdAllowed, calibrationActive: true, warningWriter: new StringWriter());
        }

        #endregion
    }
}
