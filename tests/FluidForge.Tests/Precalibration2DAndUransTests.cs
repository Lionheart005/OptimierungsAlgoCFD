using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using FluidForge.Core;
using FluidForge.Kernels.Planar2D;
using FluidForge.Projects.MantaAuv;
using Moq;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Tests for TODO-48 (2D precalibration for 3D runs, IHasRepresentativeSection, 3-level 3D confirmation)
    /// and TODO-37 (time-accurate mode / URANS with dual-time-stepping for single verification runs).
    /// </summary>
    public class Precalibration2DAndUransTests
    {
        // =====================================================================
        // Part A: 3-level 3D confirmation (CalibrationEvaluator.EvaluateConfirmation3D)
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
            // 3D levels around f2D = 1.0f:
            // Level 1: Coarse 1.41, Level 2: Medium 1.00, Level 3: Reference 0.71
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

            // Tolerance 2 % (0.02). Coarse level deviates by |0.0508 - 0.0500| / 0.0500 = 1.6 % -> <= 2 %!
            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.True(decision.Success);
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(1, decision.SelectedStage!.LevelIndex);
            Assert.Equal(1.41f, decision.SelectedStage.Factor);
            Assert.NotNull(decision.Gci);
            Assert.Contains("Coarse 3D level", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateConfirmation3D_Selects_F2D_When_Coarse_Exceeds_Tolerance_But_F2D_Passes()
        {
            var stages = new List<CalibrationStageData>
            {
                new(LevelIndex: 1, Factor: 1.41f, MeshCells: 10000, DurationSeconds: 10,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0525f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.2f, YPlusOk: true), // Deviation: 5.0 % -> exceeds 2 %
                new(LevelIndex: 2, Factor: 1.00f, MeshCells: 25000, DurationSeconds: 20,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0505f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.0f, YPlusOk: true), // Deviation: 1.0 % -> within 2 %
                new(LevelIndex: 3, Factor: 0.71f, MeshCells: 65000, DurationSeconds: 50,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0500f }, Fitness: 20f,
                    Converged: true, YPlusP95: 0.8f, YPlusOk: true)
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.True(decision.Success);
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(2, decision.SelectedStage!.LevelIndex);
            Assert.Equal(1.00f, decision.SelectedStage.Factor);
            Assert.Contains("2D level", decision.SummaryMessage);
        }

        [Fact]
        public void EvaluateConfirmation3D_Fails_When_Neither_Coarse_Nor_F2D_Passes()
        {
            var stages = new List<CalibrationStageData>
            {
                new(LevelIndex: 1, Factor: 1.41f, MeshCells: 10000, DurationSeconds: 10,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0560f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.2f, YPlusOk: true), // Deviation: 12 %
                new(LevelIndex: 2, Factor: 1.00f, MeshCells: 25000, DurationSeconds: 20,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0520f }, Fitness: 20f,
                    Converged: true, YPlusP95: 1.0f, YPlusOk: true), // Deviation: 4 %
                new(LevelIndex: 3, Factor: 0.71f, MeshCells: 65000, DurationSeconds: 50,
                    Metrics: new Dictionary<string, float> { ["Drag"] = 0.0500f }, Fitness: 20f,
                    Converged: true, YPlusP95: 0.8f, YPlusOk: true)
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Null(decision.SelectedStage);
            Assert.Contains("Neither the coarse 3D level nor the 2D factor", decision.SummaryMessage);
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
                    Converged: false, YPlusP95: 0.8f, YPlusOk: true) // Reference not converged!
            };

            var decision = CalibrationEvaluator.EvaluateConfirmation3D(1.0f, stages, new[] { "Drag" }, 0.02f);

            Assert.False(decision.Success);
            Assert.Contains("3D reference level", decision.SummaryMessage);
        }

        // =====================================================================
        // Part B: Precalibration support validation & Section2D
        // =====================================================================

        [Fact]
        public void ValidatePrecalibrationSupport_Throws_For_MantaAuv()
        {
            var config = SimulationConfig.CreateDefault();
            config.MeshCalibration.Use2DPrecalibration = true;

            var manta = new MantaProject();

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.ValidatePrecalibrationSupport(config, manta));

            Assert.Contains(OutputConventions.CalibrationTag, ex.Message);
            Assert.Contains("Use2DPrecalibration", ex.Message);
            Assert.Contains("MantaAuv", ex.Message);
            Assert.Contains("IHasRepresentativeSection", ex.Message);
        }

        /// <summary>
        /// A project providing <see cref="IHasRepresentativeSection"/> may use 2D precalibration.
        /// (Until 2026-11-08 an example project served this purpose; a stub works just as well.)
        /// </summary>
        [Fact]
        public void ValidatePrecalibrationSupport_Succeeds_For_A_Project_With_A_Representative_Section()
        {
            var config = SimulationConfig.CreateDefault();
            config.MeshCalibration.Use2DPrecalibration = true;

            var project = new Mock<IProjectDefinition>();
            project.SetupGet(p => p.Name).Returns("WithProfileSection");
            project.As<IHasRepresentativeSection>();

            // Must not throw:
            MeshCalibration.ValidatePrecalibrationSupport(config, project.Object);

            Assert.IsAssignableFrom<IHasRepresentativeSection>(project.Object);
        }

        // =====================================================================
        // Part C: RunWithPrecalibration2D End-to-End with mocks
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

                var context = new ProjectContext("SampleProject", tempDir, null, simConfig, projConfig);
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
                    rec.PassiveParameters["Drag"] = 0.050f + (factor * 0.0003f); // Deviates by less than 2 %
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

                // Both reports must exist
                string calibDir = Path.Combine(tempDir, reportPath.Replace('/', Path.DirectorySeparatorChar));
                Assert.True(File.Exists(Path.Combine(calibDir, "calibration_2d.csv")));
                Assert.True(File.Exists(Path.Combine(calibDir, "calibration.csv")));
                Assert.True(File.Exists(Path.Combine(calibDir, "calibration.json")));

                // Latest copies must exist
                string latestJson = Path.Combine(tempDir, MeshCalibration.CalibrationDirectoryName, MeshCalibration.LatestCalibrationJson);
                Assert.True(File.Exists(latestJson));

                // Source text must be set correctly
                Assert.Contains("CALIBRATED: 2D precalibration, confirmed in 3D", simContext.MeshRefinementSource);

                // 2D stages and 3D stages were called
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
        // Part D: URANS / TimeDomain config generation
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
                // SU2 7.5.1 forbids ITER in time-accurate mode (server test 2026-11-06).
                Assert.DoesNotContain(lines, l => l.StartsWith("ITER="));
                // Record time average, otherwise only the instantaneous value is written.
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
                options.TimeDomain.TimeStep = 0.0f; // Automatic!
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
            // Server test 2026-11-06: TIME_ITER is an absolute end index (60 when restarting from 2 = 58 steps),
            // and without OUTPUT_WRT_FREQ SU2 wrote a 6 MB restart file at every time step.
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
                Assert.Contains("WINDOW_START_ITER= 502", lines);   // 2 + 500: last 10 of 20 periods
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

        [Fact]
        public void ResolvePrecalibrationGmsh2DOptions_LoadsGmsh2dJson_OrFallsBackFromLoadedGmsh3D()
        {
            string dirWith2d = Path.Combine(Path.GetTempPath(), "precal_gmsh2d_" + Guid.NewGuid().ToString("N"));
            string dirWithout2d = Path.Combine(Path.GetTempPath(), "precal_gmsh3d_" + Guid.NewGuid().ToString("N"));
            try
            {
                // Case 1: solvers/gmsh2d.json exists -> loaded directly
                Directory.CreateDirectory(Path.Combine(dirWith2d, "solvers"));
                File.WriteAllText(Path.Combine(dirWith2d, "solvers", "gmsh2d.json"),
                    @"{ ""TargetYPlus"": 0.8, ""BoundaryLayerGrowthRate"": 1.15, ""DetailBoxLengthFactor"": 2.5 }");

                var ctx1 = new ProjectContext("P1", dirWith2d, null, SimulationConfig.CreateDefault(), new ProjectConfig());
                var opt1 = Program.ResolvePrecalibrationGmsh2DOptions(ctx1);
                Assert.Equal(0.8f, opt1.TargetYPlus);
                Assert.Equal(1.15f, opt1.BoundaryLayerGrowthRate);
                Assert.Equal(2.5f, opt1.DetailBoxLengthFactor);

                // Case 2: no gmsh2d.json, but solvers/gmsh.json (with 3D keys) was already loaded -> no schema error, compatible values are adopted
                Directory.CreateDirectory(Path.Combine(dirWithout2d, "solvers"));
                File.WriteAllText(Path.Combine(dirWithout2d, "solvers", "gmsh.json"),
                    @"{ ""TunnelSizeX"": 1200.0, ""TargetYPlus"": 1.75, ""BoundaryLayerGrowthRate"": 1.32, ""DetailBoxLengthFactor"": 2.25 }");

                var ctx2 = new ProjectContext("P2", dirWithout2d, null, SimulationConfig.CreateDefault(), new ProjectConfig());
                ctx2.LoadSolverOptions<GmshMesherOptions>("gmsh");

                var opt2 = Program.ResolvePrecalibrationGmsh2DOptions(ctx2);
                Assert.Equal(1.75f, opt2.TargetYPlus);
                Assert.Equal(1.32f, opt2.BoundaryLayerGrowthRate);
                Assert.Equal(2.25f, opt2.DetailBoxLengthFactor);
            }
            finally
            {
                if (Directory.Exists(dirWith2d)) Directory.Delete(dirWith2d, true);
                if (Directory.Exists(dirWithout2d)) Directory.Delete(dirWithout2d, true);
            }
        }
    }
}
