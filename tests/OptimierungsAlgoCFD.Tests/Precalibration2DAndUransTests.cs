using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.Planar2D;
using OptimierungsAlgoCFD.Projects.MantaAuv;
using Moq;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Tests für TODO-48 (2D-Vorkalibrierung für 3D-Läufe, IHasRepresentativeSection, 3-Stufen-3D-Bestätigung)
    /// und TODO-37 (zeitgenauer Modus / URANS mit Dual-Time-Stepping für Einzelprüfungen).
    /// </summary>
    public class Precalibration2DAndUransTests
    {
        // =====================================================================
        // Part A: 3-Stufen-3D-Bestätigung (CalibrationEvaluator.EvaluateConfirmation3D)
        // =====================================================================

        [Fact]
        public void Build3DConfirmationFactors_Returns_Three_Factors_Around_F2D()
        {
            float[] factors = CalibrationEvaluator.Build3DConfirmationFactors(1.0f);
            Assert.Equal(3, factors.Length);
            Assert.Equal(1.41f, factors[0]);
            Assert.Equal(1.00f, factors[1]);
            Assert.Equal(0.71f, factors[2]);

            float[] factors071 = CalibrationEvaluator.Build3DConfirmationFactors(0.71f);
            Assert.Equal(3, factors071.Length);
            Assert.Equal(1.00f, factors071[0]);
            Assert.Equal(0.71f, factors071[1]);
            Assert.Equal(0.50f, factors071[2]);
        }

        [Fact]
        public void EvaluateConfirmation3D_Selects_Coarse_Stage_When_Already_Within_Tolerance()
        {
            // 3D-Stufen um f2D = 1.0f:
            // Stufe 1: Grob 1.41, Stufe 2: Medium 1.00, Stufe 3: Referenz 0.71
            var stages = new List<CalibrationStageData>
            {
                new(LevelIndex: 1, Factor: 1.41f, MeshCells: 10000, DurationSeconds: 10,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0508f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.2f, YPlusOk: true),
                new(LevelIndex: 2, Factor: 1.00f, MeshCells: 25000, DurationSeconds: 20,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0504f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.0f, YPlusOk: true),
                new(LevelIndex: 3, Factor: 0.71f, MeshCells: 65000, DurationSeconds: 50,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0500f }, Fitness: 20f,
                    Converged: true, YPlusP95: 0.8f, YPlusOk: true)
            };

            // Toleranz 2 % (0.02). Grobe Stufe weicht um |0.0508 - 0.0500| / 0.0500 = 1.6 % ab -> <= 2 %!
            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.True(decision.Success);
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(1, decision.SelectedStage!.LevelIndex);
            Assert.Equal(1.41f, decision.SelectedStage.Factor);
            Assert.NotNull(decision.Gci);
            Assert.Contains("Grobe 3D-Stufe", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateConfirmation3D_Selects_F2D_When_Coarse_Exceeds_Tolerance_But_F2D_Passes()
        {
            var stages = new List<CalibrationStageData>
            {
                new(LevelIndex: 1, Factor: 1.41f, MeshCells: 10000, DurationSeconds: 10,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0525f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.2f, YPlusOk: true), // Abweichung: 5.0 % -> über 2 %
                new(LevelIndex: 2, Factor: 1.00f, MeshCells: 25000, DurationSeconds: 20,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0505f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.0f, YPlusOk: true), // Abweichung: 1.0 % -> innerhalb 2 %
                new(LevelIndex: 3, Factor: 0.71f, MeshCells: 65000, DurationSeconds: 50,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0500f }, Fitness: 20f,
                    Converged: true, YPlusP95: 0.8f, YPlusOk: true)
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.True(decision.Success);
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(2, decision.SelectedStage!.LevelIndex);
            Assert.Equal(1.00f, decision.SelectedStage.Factor);
            Assert.Contains("2D-Stufe", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateConfirmation3D_Fails_When_Neither_Coarse_Nor_F2D_Passes()
        {
            var stages = new List<CalibrationStageData>
            {
                new(LevelIndex: 1, Factor: 1.41f, MeshCells: 10000, DurationSeconds: 10,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0560f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.2f, YPlusOk: true), // Abweichung: 12 %
                new(LevelIndex: 2, Factor: 1.00f, MeshCells: 25000, DurationSeconds: 20,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0520f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.0f, YPlusOk: true), // Abweichung: 4 %
                new(LevelIndex: 3, Factor: 0.71f, MeshCells: 65000, DurationSeconds: 50,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0500f }, Fitness: 20f,
                    Converged: true, YPlusP95: 0.8f, YPlusOk: true)
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Null(decision.SelectedStage);
            Assert.Contains("Weder die grobe 3D-Stufe noch der 2D-Faktor", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateConfirmation3D_Fails_If_Reference_Stage_Not_Converged()
        {
            var stages = new List<CalibrationStageData>
            {
                new(LevelIndex: 1, Factor: 1.41f, MeshCells: 10000, DurationSeconds: 10,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0505f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.2f, YPlusOk: true),
                new(LevelIndex: 2, Factor: 1.00f, MeshCells: 25000, DurationSeconds: 20,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0502f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.0f, YPlusOk: true),
                new(LevelIndex: 3, Factor: 0.71f, MeshCells: 65000, DurationSeconds: 50,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0500f }, Fitness: 20f,
                    Converged: false, YPlusP95: 0.8f, YPlusOk: true) // Referenz nicht konvergiert!
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Contains("3D-Referenzstufe", decision.SummaryMessage);
        }

        // =====================================================================
        // Part B: Precalibration-Support-Validierung & Section2D
        // =====================================================================

        [Fact]
        public void ValidatePrecalibrationSupport_Throws_For_MantaAuv()
        {
            var config = SimulationConfig.CreateDefault();
            config.MeshCalibration.Use2DPrecalibration = true;

            var manta = new MantaProject();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.ValidatePrecalibrationSupport(config, manta));

            Assert.Contains("[KALIBRIERUNG]", ex.Message);
            Assert.Contains("Use2DPrecalibration", ex.Message);
            Assert.Contains("MantaAuv", ex.Message);
            Assert.Contains("IHasRepresentativeSection", ex.Message);
        }

        /// <summary>
        /// Ein Projekt, das <see cref="IHasRepresentativeSection"/> anbietet, darf die 2D-Vorkalibrierung nutzen.
        /// (Bis 08.11.2026 diente dafür ein Beispielprojekt; ein Stub tut es genauso.)
        /// </summary>
        [Fact]
        public void ValidatePrecalibrationSupport_Succeeds_For_A_Project_With_A_Representative_Section()
        {
            var config = SimulationConfig.CreateDefault();
            config.MeshCalibration.Use2DPrecalibration = true;

            var project = new Mock<IProjectDefinition>();
            project.SetupGet(p => p.Name).Returns("MitProfilschnitt");
            project.As<IHasRepresentativeSection>();

            // Darf keine Ausnahme werfen:
            MeshCalibration.ValidatePrecalibrationSupport(config, project.Object);

            Assert.IsAssignableFrom<IHasRepresentativeSection>(project.Object);
        }

        // =====================================================================
        // Part C: RunWithPrecalibration2D End-to-End mit Mocks
        // =====================================================================

        [Fact]
        public void MeshCalibration_RunWithPrecalibration2D_Executes_2D_And_3D_Stages()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "precalib_e2e_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);

            try
            {
                var simConfig = SimulationConfig.CreateDefault();
                simConfig.MeshCalibration.Mode = "Auto";
                simConfig.MeshCalibration.Tolerance = 0.02f;
                simConfig.MeshCalibration.Use2DPrecalibration = true;

                var projConfig = new ProjectConfig();
                projConfig.BaseParameters["MaxCamber"] = 0f;
                projConfig.BaseParameters["CamberPosition"] = 40f;
                projConfig.BaseParameters["MaxThickness"] = 12f;

                var context = new ProjectContext("Beispielprojekt", tempDir, null, simConfig, projConfig);
                var simContext = new SimulationContext(simConfig, projConfig, tempDir);

                var stages2DRecorded = new List<(float Factor, string Tag)>();
                var stages3DRecorded = new List<(float Factor, string Tag)>();

                // Mock 2D stage runner
                ModelRecord Run2D(float factor, string tag)
                {
                    stages2DRecorded.Add((factor, tag));
                    var rec = new ModelRecord();
                    rec.PassiveParameters["MeshCells"] = 1000f / factor;
                    rec.PassiveParameters["Drag"] = 0.050f + (factor * 0.0002f);
                    rec.PassiveParameters["YPlus_P95"] = 1.0f;
                    rec.PassiveParameters["Su2_Converged"] = 1.0f;
                    return rec;
                }

                // Mock 3D stage runner
                ModelRecord Run3D(float factor, string tag)
                {
                    stages3DRecorded.Add((factor, tag));
                    var rec = new ModelRecord();
                    rec.PassiveParameters["MeshCells"] = 50000f / (factor * factor * factor);
                    rec.PassiveParameters["Drag"] = 0.050f + (factor * 0.0003f); // Weicht weniger als 2 % ab
                    rec.PassiveParameters["YPlus_P95"] = 1.1f;
                    rec.PassiveParameters["Su2_Converged"] = 1.0f;
                    return rec;
                }

                var (decision, reportPath) = MeshCalibration.RunWithPrecalibration2D(
                    context,
                    simContext,
                    Run2D,
                    Run3D,
                    new[] { "Drag" });

                Assert.True(decision.Success);
                Assert.NotNull(decision.SelectedStage);
                Assert.NotEmpty(reportPath);

                // Beide Berichte müssen existieren
                string calibDir = Path.Combine(tempDir, reportPath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(Path.Combine(calibDir, "calibration_2d.csv")));
                Assert.True(File.Exists(Path.Combine(calibDir, "calibration.csv")));
                Assert.True(File.Exists(Path.Combine(calibDir, "calibration.json")));

                // Latest-Kopien müssen existieren
                string latestJson = Path.Combine(tempDir, MeshCalibration.CalibrationDirectoryName, MeshCalibration.LatestCalibrationJson);
                Assert.True(File.Exists(latestJson));

                // Herkunftstext muss korrekt gesetzt sein
                Assert.Contains("KALIBRIERT: 2D-Vorkalibrierung, in 3D bestätigt", simContext.MeshRefinementSource);

                // 2D-Stufen und 3D-Stufen wurden gerufen
                Assert.NotEmpty(stages2DRecorded);
                Assert.NotEmpty(stages3DRecorded);
                Assert.Equal(3, stages3DRecorded.Count);
            }
            finally
            {
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        // =====================================================================
        // Part D: URANS / TimeDomain Config-Generierung
        // =====================================================================

        [Fact]
        public void Su2ConfigGenerator_Emits_TimeDomain_Keys_When_Enabled()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2_urans_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var config = SimulationConfig.CreateDefault();
                var options = new Su2SolverOptions();
                options.TimeDomain.Enabled = true;
                options.TimeDomain.TimeStep = 0.0025f;
                options.TimeDomain.TimeIterations = 300;
                options.TimeDomain.InnerIterations = 20;
                options.TimeDomain.AveragingStartFraction = 0.6f;

                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, config, options);

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("TIME_DOMAIN= YES", lines);
                Assert.Contains("TIME_MARCHING= DUAL_TIME_STEPPING-2ND_ORDER", lines);
                Assert.Contains("TIME_STEP= 0.0025", lines);
                Assert.Contains("TIME_ITER= 300", lines);
                Assert.Contains("INNER_ITER= 20", lines);
                Assert.Contains("WINDOW_START_ITER= 180", lines); // 300 * 0.6 = 180
                Assert.Contains("WINDOW_FUNCTION= HANN_SQUARE", lines);
                // SU2 7.5.1 verbietet ITER im zeitgenauen Modus (Servertest 06.11.2026).
                Assert.DoesNotContain(lines, l => l.StartsWith("ITER="));
                // Zeitmittel mitschreiben, sonst gibt es nur den Momentanwert.
                Assert.Contains(lines, l => l.StartsWith("HISTORY_OUTPUT=") && l.Contains("TAVG_AERO_COEFF"));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Su2ConfigGenerator_Computes_Automatic_TimeStep_From_Strouhal()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2_urans_strouhal_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var config = SimulationConfig.CreateDefault();
                var options = new Su2SolverOptions();
                options.FreestreamVelocity = 2.0f; // U = 2 m/s
                options.ReferenceLength = 0.1f;    // L = 0.1 m
                options.TimeDomain.Enabled = true;
                options.TimeDomain.TimeStep = 0.0f; // Automatisch!
                options.TimeDomain.StrouhalEstimate = 0.2f; // f = 0.2 * 2 / 0.1 = 4 Hz -> T = 0.25 s
                options.TimeDomain.StepsPerPeriod = 25;     // dt = 0.25 / 25 = 0.01 s

                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, config, options);

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("TIME_DOMAIN= YES", lines);
                Assert.Contains("TIME_STEP= 0.01", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        [Fact]
        public void Urans_Restart_From_Steady_Uses_Absolute_Time_Indices_And_Limits_Output()
        {
            // Servertest 06.11.2026: TIME_ITER ist ein absoluter Endindex (60 bei Neustart ab 2 = 58 Schritte),
            // und SU2 schrieb ohne OUTPUT_WRT_FREQ in jedem Zeitschritt eine 6-MB-Restart-Datei.
            string path = Path.Combine(Path.GetTempPath(), "su2_urans_restart_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var options = new Su2SolverOptions();
                options.TimeDomain.Enabled = true;
                options.TimeDomain.TimeStep = 0.0001f;
                options.TimeDomain.TimeIterations = 1000;
                options.TimeDomain.AveragingStartFraction = 0.5f;
                options.TimeDomain.RestartIteration = 2;

                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, SimulationConfig.CreateDefault(), options);
                string[] lines = File.ReadAllLines(path);

                Assert.Contains("TIME_ITER= 1002", lines);          // 2 + 1000
                Assert.Contains("WINDOW_START_ITER= 502", lines);   // 2 + 500: letzte 10 von 20 Perioden
                Assert.Contains("RESTART_SOL= YES", lines);
                Assert.Contains("RESTART_ITER= 2", lines);
                Assert.Contains("SOLUTION_FILENAME= restart.dat", lines);
                Assert.Contains("OUTPUT_WRT_FREQ= 100000", lines);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void Urans_From_Freestream_Has_No_Restart_Lines()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2_urans_cold_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var options = new Su2SolverOptions();
                options.TimeDomain.Enabled = true;
                options.TimeDomain.TimeStep = 0.0001f;

                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, SimulationConfig.CreateDefault(), options);
                string[] lines = File.ReadAllLines(path);

                Assert.DoesNotContain(lines, l => l.StartsWith("RESTART_SOL="));
                Assert.Contains("TIME_ITER= 1000", lines);
                Assert.Contains("WINDOW_START_ITER= 500", lines);
            }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        [Fact]
        public void Urans_Defaults_Cover_Twenty_Periods()
        {
            var d = new Su2TimeDomainOptions();
            Assert.Equal(20, d.TimeIterations / d.StepsPerPeriod);
            Assert.Equal(10, (int)(d.TimeIterations * (1 - d.AveragingStartFraction)) / d.StepsPerPeriod);
        }

        [Fact]
        public void Su2ConfigGenerator_Omits_TimeDomain_When_Disabled()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2_steady_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                var config = SimulationConfig.CreateDefault();
                var options = new Su2SolverOptions();
                options.TimeDomain.Enabled = false;

                Su2ConfigGenerator.Generate(path, "/tmp/mesh.su2", 0.001f, config, options);

                string[] lines = File.ReadAllLines(path);

                Assert.DoesNotContain("TIME_DOMAIN= YES", lines);
                Assert.DoesNotContain("TIME_MARCHING= DUAL_TIME_STEPPING-2ND_ORDER", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }
    }
}
