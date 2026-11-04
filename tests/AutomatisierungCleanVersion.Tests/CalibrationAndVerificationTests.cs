using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Tests für TODO-42 (Netzkalibrierung, CalibrationEvaluator, MeshCalibration)
    /// und TODO-43 (Feine Nachrechnung der Gewinner, ChampionFineVerifier).
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
            // Referenzstufe f=0.5: Cd = 0.0400, Cl = 0.4800
            // Stufe f=0.71: Cd = 0.04005 (0.125 % Abweichung), Cl = 0.4802 (0.042 % Abweichung)
            // Stufe f=1.0:  Cd = 0.0402  (0.50 % Abweichung),  Cl = 0.4810 (0.208 % Abweichung) -> innerhalb 2 %!
            // Stufe f=1.41: Cd = 0.0410  (2.50 % Abweichung) -> über 2 % Toleranz!
            // Stufe f=2.0:  Cd = 0.0500  (25.0 % Abweichung)
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
            // Stufe 1.0f ist die gröbste Stufe, deren relative Abweichung für alle Metriken <= 2 % ist
            Assert.Equal(1.0f, decision.SelectedStage!.Factor);
            Assert.NotNull(decision.Gci);
            Assert.True(decision.Gci!.IsMonotonic);
            Assert.Null(decision.Gci.Warning);
        }

        [Fact]
        public void EvaluateCalibration_OscillatingSequence_SetsWarning()
        {
            // Oszillierende Folge für die feinsten Stufen:
            // Stufe 1.0: Cd = 0.0410
            // Stufe 0.71: Cd = 0.0390
            // Stufe 0.5: Cd = 0.0400
            // eps_32 = 0.0410 - 0.0390 = +0.0020
            // eps_21 = 0.0390 - 0.0400 = -0.0010 -> eps_32 * eps_21 < 0 (Vorzeichenwechsel)
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
            Assert.Contains("nicht monoton", decision.Gci.Warning);
        }

        [Fact]
        public void EvaluateCalibration_ToleranceNotReached_ReturnsFailure()
        {
            // Alle Stufen (außer Referenz) weichen mehr als 1 % ab
            var stages = new List<CalibrationStageData>
            {
                new(0, 2.0f,  10000, 10.0, new Dictionary<string, float> { ["Cd"] = 0.0500f }, 10f, true, 0.9f, true),
                new(1, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Cd"] = 0.0450f }, 12f, true, 0.9f, true),
                new(2, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0430f }, 15f, true, 0.9f, true),
                new(3, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Cd"] = 0.0415f }, 16f, true, 0.9f, true), // 3.75 % Abweichung
                new(4, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.2f, true, 0.9f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Null(decision.SelectedStage);
            Assert.Contains("Keine Grobstufe erreicht die Toleranz", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateCalibration_UnconvergedStage_IsSkipped()
        {
            // Stufe 1.0f hätte kleine Abweichung, ist aber nicht konvergiert
            var stages = new List<CalibrationStageData>
            {
                new(0, 1.41f, 25000, 25.0, new Dictionary<string, float> { ["Cd"] = 0.0450f }, 12f, true, 0.9f, true),
                new(1, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0401f }, 15f, false, 0.9f, true), // Converged = false!
                new(2, 0.71f, 200000, 150.0, new Dictionary<string, float> { ["Cd"] = 0.04005f }, 16f, true, 0.9f, true), // innerhalb Tol
                new(3, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.04000f }, 16.2f, true, 0.9f, true),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.True(decision.Success);
            Assert.NotNull(decision.SelectedStage);
            // Stufe 1.0f wurde übersprungen; Stufe 0.71f wurde gewählt
            Assert.Equal(0.71f, decision.SelectedStage!.Factor);
        }

        [Fact]
        public void EvaluateCalibration_UnconvergedReference_FailsImmediately()
        {
            var stages = new List<CalibrationStageData>
            {
                new(0, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0401f }, 15f, true, 0.9f, true),
                new(1, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.2f, false, 0.9f, true), // Ref nicht konvergiert!
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Contains("Referenzstufe", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateCalibration_BadYPlusReference_FailsImmediately()
        {
            var stages = new List<CalibrationStageData>
            {
                new(0, 1.0f,  70000, 60.0, new Dictionary<string, float> { ["Cd"] = 0.0401f }, 15f, true, 0.9f, true),
                new(1, 0.5f,  550000, 400.0, new Dictionary<string, float> { ["Cd"] = 0.0400f }, 16.2f, true, 6.0f, false), // Ref YPlus unzulässig!
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Cd" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Contains("Referenzstufe", decision.SummaryMessage);
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
            Assert.Null(gci.Warning);
            Assert.InRange(gci.ObservedOrderP, 1.95f, 2.05f);
            Assert.InRange(gci.GciFinePercent, 1.20f, 1.30f);
        }

        #endregion

        #region Part 2: MeshCalibration Config Validation and Hints Tests

        [Fact]
        public void ValidateConfig_AutoMode_WithExplicitRefinementFactor_ThrowsKonfigurationException()
        {
            var config = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Auto", Tolerance = 0.02f }
            };

            var report = new ConfigLoadReport();
            report.Origins["MeshRefinementFactor"] = "simulation.json";

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.ValidateConfig(config, report, "TestProject"));

            Assert.StartsWith("[KONFIGURATION]", ex.Message);
            Assert.Contains("MeshRefinementFactor", ex.Message);
            Assert.Contains("simulation.json", ex.Message);
        }

        [Fact]
        public void ValidateConfig_InvalidMode_ThrowsKonfigurationException()
        {
            var config = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "SuperAutomatic" }
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.ValidateConfig(config, null, "TestProject"));

            Assert.StartsWith("[KONFIGURATION]", ex.Message);
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

            // Soll ohne Ausnahme durchlaufen
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
            string calibDir = Path.Combine(_workingDirectory, "Kalibrierung");
            Directory.CreateDirectory(calibDir);

            // Schreibe ein latest_calibration.json mit anderem Fingerabdruck
            string json = @"{
                ""Fingerprint"": ""ALT_FINGERPRINT_12345"",
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
            Assert.Contains("veraltet", output);
        }

        [Fact]
        public void CheckManualCalibrationHints_FactorMismatch_GeneratesMismatchHint()
        {
            string calibDir = Path.Combine(_workingDirectory, "Kalibrierung");
            Directory.CreateDirectory(calibDir);

            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var config = new SimulationConfig { MeshRefinementFactor = 1.41f }; // weicht von 0.71 ab!
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
        public void EffectiveConfigWriter_Records_WirksamerNetzfaktor_And_Source()
        {
            var projectDir = Path.Combine(_workingDirectory, "EffConfigProject");
            Directory.CreateDirectory(projectDir);

            var config = new SimulationConfig { MeshRefinementFactor = 1.0f };
            var project = new ProjectConfig { ProjectName = "EffConfigProject" };
            var context = new ProjectContext("EffConfigProject", projectDir, null, config, project);

            var simContext = new SimulationContext(config, project, projectDir);
            simContext.EffectiveMeshRefinementFactor = 0.71f;
            simContext.MeshRefinementSource = "KALIBRIERT aus latest_calibration.json";
            simContext.CalibrationReportPath = "Kalibrierung/latest_calibration.json";

            var doc = EffectiveConfigWriter.Build(context, simContext);

            Assert.True(doc.ContainsKey("WirksamerNetzfaktor"));
            Assert.InRange(doc["WirksamerNetzfaktor"]!.GetValue<float>(), 0.70f, 0.72f);

            Assert.True(doc.ContainsKey("NetzfaktorQuelle"));
            Assert.Equal("KALIBRIERT aus latest_calibration.json", doc["NetzfaktorQuelle"]!.GetValue<string>());

            Assert.True(doc.ContainsKey("KalibrierBericht"));
            Assert.Equal("Kalibrierung/latest_calibration.json", doc["KalibrierBericht"]!.GetValue<string>());
        }

        #endregion

        #region Part 4: ChampionFineVerifier Tests

        [Fact]
        public void CalculateFineFactor_CalculatesCorrectLevels()
        {
            // Stufe 1: 1.0 / sqrt(2) approx 0.7071
            float f1 = ChampionFineVerifier.CalculateFineFactor(1.0f, 1);
            Assert.InRange(f1, 0.70f, 0.71f);

            // Stufe 2: 1.0 / (sqrt(2)^2) = 0.5
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
                        PassiveParameters = new Dictionary<string, float> { ["Drag"] = 102.0f } // 2% Abweichung
                    };
                },
                evaluateFitness: r => { },
                monitoredMetrics: new[] { "Drag" });

            Assert.Equal(1, runsCalled);
            Assert.Equal(initialHistoryCount, simContext.History.Count); // History bleibt unberührt!
            Assert.Equal("Fein1", capturedTag);
            Assert.InRange(capturedFactor, 0.70f, 0.71f);

            Assert.True(champion.PassiveParameters.ContainsKey("FineVerified"));
            Assert.Equal(1f, champion.PassiveParameters["FineVerified"]);
            Assert.True(champion.PassiveParameters.ContainsKey("FineDeviation"));
            Assert.InRange(champion.PassiveParameters["FineDeviation"], 0.019f, 0.021f);

            // Verifikation.csv muss im Ausgabeverzeichnis existieren
            string csvPath = Path.Combine(_workingDirectory, "Verifikation.csv");
            Assert.True(File.Exists(csvPath), "Verifikation.csv wurde nicht angelegt.");
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
                        Fitness = tag == "Fein1" ? 14.8f : 11.9f,
                        PassiveParameters = new Dictionary<string, float> { ["Drag"] = 51.0f }
                    };
                },
                evaluateFitness: r => { },
                monitoredMetrics: new[] { "Drag" });

            // Genau 2 Varianten (TopN = 2) gerechnet
            Assert.Equal(2, capturedTags.Count);
            Assert.Equal("Fein1", capturedTags[0]);
            Assert.Equal("Fein2", capturedTags[1]);
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
            Assert.Equal("Fein_Gen2", capturedTag);
            Assert.True(winner.PassiveParameters.ContainsKey("FineVerified"));
            Assert.True(winner.PassiveParameters.ContainsKey("FineDeviation"));
        }

        #endregion
    }
}
