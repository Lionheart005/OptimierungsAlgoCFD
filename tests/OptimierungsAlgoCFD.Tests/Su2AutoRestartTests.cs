using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using OptimierungsAlgoCFD;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    [Collection(ProcessWideStateCollection.Name)]
    public class Su2AutoRestartTests
    {
        private static string[] BuildHistoryLines(int rows, Func<int, double> cd, int startIter = 0)
        {
            var lines = new List<string>(rows + 1)
            {
                "\"Inner_Iter\", \"rms[Rho]\", \"CD\", \"CL\""
            };
            for (int i = 0; i < rows; i++)
            {
                lines.Add(string.Join(", ",
                    (startIter + i).ToString(CultureInfo.InvariantCulture),
                    "-4.5",
                    cd(i).ToString("R", CultureInfo.InvariantCulture),
                    "0.1"));
            }
            return lines.ToArray();
        }

        private static ModelRecord CreateDummyRecord()
        {
            var record = new ModelRecord { Iteration = 1, Variant = 1 };
            record.PassiveParameters["FrontalArea"] = 1000f;
            return record;
        }

        private static string CreateTempDir(string prefix)
        {
            string dir = Path.Combine(Path.GetTempPath(), prefix + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        [Fact]
        public void Immediate_Convergence_Does_Not_Restart()
        {
            string dir = CreateTempDir("su2_ap1_conv");
            try
            {
                string meshPath = Path.Combine(dir, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshPath, "dummy");

                int calls = 0;
                var solver = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" },
                    AutoRestartOnDrift = true,
                    MaxConvergenceRestarts = 3
                })
                {
                    RunSu2Override = (wd, cores) =>
                    {
                        calls++;
                        File.WriteAllLines(Path.Combine(wd, "history.csv"), BuildHistoryLines(200, _ => 0.030));
                        File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), "restart-data");
                        return (0, "SU2 v8.0.0", string.Empty);
                    }
                };

                var config = new SimulationConfig
                {
                    Su2MaxIterations = 1000,
                    CauchyElements = 100,
                    CauchyTolerance = "1e-5",
                    IterativeToleranceFraction = 0.25f
                };
                config.MeshCalibration.Tolerance = 0.05f;

                var record = CreateDummyRecord();
                solver.Solve(meshPath, record, config, dir);

                Assert.Equal(1, calls);
                Assert.False(record.SimulationFailed);
                Assert.Equal(1.0f, record.PassiveParameters["Su2_Converged"]);
                Assert.Equal(200f, record.PassiveParameters["Su2_Iterations"]);
                Assert.InRange(record.PassiveParameters["Drag"], 0.0299f, 0.0301f);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Early_Cauchy_Stop_With_Drift_Restarts_Tightens_And_Stitches_Monotonically()
        {
            string dir = CreateTempDir("su2_ap1_restart");
            try
            {
                string meshPath = Path.Combine(dir, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshPath, "dummy");

                var observedConfigs = new List<string[]>();
                var solver = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" },
                    AutoRestartOnDrift = true,
                    MaxConvergenceRestarts = 3
                })
                {
                    RunSu2Override = (wd, cores) =>
                    {
                        string[] cfgLines = File.ReadAllLines(Path.Combine(wd, "current_config.cfg"));
                        observedConfigs.Add(cfgLines);

                        if (observedConfigs.Count == 1)
                        {
                            // Früher Cauchy-Stopp bei Iteration 200 mit starker Drift in CD
                            File.WriteAllLines(
                                Path.Combine(wd, "history.csv"),
                                BuildHistoryLines(200, i => 0.050 - 0.0001 * i));
                            File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), "restart-block-0");
                        }
                        else
                        {
                            // Restart-Block zählt ab 0 neu und konvergiert stabil auf 0.030
                            File.WriteAllLines(
                                Path.Combine(wd, "history.csv"),
                                BuildHistoryLines(250, _ => 0.030, startIter: 0));
                        }

                        return (0, string.Empty, string.Empty);
                    }
                };

                var config = new SimulationConfig
                {
                    Su2MaxIterations = 1000,
                    CauchyElements = 100,
                    CauchyTolerance = "1e-5",
                    IterativeToleranceFraction = 0.25f
                };
                config.MeshCalibration.Tolerance = 0.05f;

                var record = CreateDummyRecord();
                solver.Solve(meshPath, record, config, dir);

                Assert.Equal(2, observedConfigs.Count);

                // Block 0: Ausgangsconfig ohne RESTART_SOL
                Assert.Contains("ITER= 1000", observedConfigs[0]);
                Assert.Contains("CONV_CAUCHY_EPS= 1e-5", observedConfigs[0]);
                Assert.Contains("CONV_STARTITER= 100", observedConfigs[0]);
                Assert.DoesNotContain(observedConfigs[0], l => l.StartsWith("RESTART_SOL=", StringComparison.Ordinal));

                // Block 1: Restbudget 800, verschärfte Cauchy-Toleranz 1e-6, CONV_STARTITER= 80, RESTART_SOL= YES
                Assert.Contains("ITER= 800", observedConfigs[1]);
                Assert.Contains("CONV_CAUCHY_EPS= 1e-6", observedConfigs[1]);
                Assert.Contains("CONV_STARTITER= 80", observedConfigs[1]);
                Assert.Contains("RESTART_SOL= YES", observedConfigs[1]);
                Assert.Contains("SOLUTION_FILENAME= restart_flow.dat", observedConfigs[1]);

                // Kombinierte history.csv hat 450 Datenzeilen mit monotoner Iteration 0..449
                string[] stitchedLines = File.ReadAllLines(Path.Combine(dir, "history.csv"));
                Assert.Equal(451, stitchedLines.Length);
                for (int i = 0; i < 450; i++)
                {
                    string[] fields = Su2Solver.SplitCsvLine(stitchedLines[i + 1]);
                    Assert.Equal(i.ToString(CultureInfo.InvariantCulture), fields[0]);
                }

                Assert.False(record.SimulationFailed);
                Assert.Equal(1.0f, record.PassiveParameters["Su2_Converged"]);
                Assert.Equal(450f, record.PassiveParameters["Su2_Iterations"]);
                Assert.InRange(record.PassiveParameters["Drag"], 0.0299f, 0.0301f);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Stationary_Oscillation_Accepted_Without_Restart()
        {
            string dir = CreateTempDir("su2_ap1_osc");
            try
            {
                string meshPath = Path.Combine(dir, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshPath, "dummy");

                int calls = 0;
                var solver = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" },
                    AutoRestartOnDrift = true,
                    MaxConvergenceRestarts = 3
                })
                {
                    RunSu2Override = (wd, cores) =>
                    {
                        calls++;
                        File.WriteAllLines(
                            Path.Combine(wd, "history.csv"),
                            BuildHistoryLines(400, i => 0.030 * (1.0 + 0.2 * Math.Sin(i * 0.09))));
                        File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), "restart-data");
                        return (0, string.Empty, string.Empty);
                    }
                };

                var config = new SimulationConfig
                {
                    Su2MaxIterations = 1000,
                    CauchyElements = 200,
                    CauchyTolerance = "1e-5",
                    IterativeToleranceFraction = 0.25f
                };
                config.MeshCalibration.Tolerance = 0.05f;

                var record = CreateDummyRecord();
                solver.Solve(meshPath, record, config, dir);

                Assert.Equal(1, calls);
                Assert.False(record.SimulationFailed);
                Assert.Equal(1.0f, record.PassiveParameters["Su2_Unsteady"]);
                Assert.InRange(record.PassiveParameters["Drag"], 0.029f, 0.031f);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData(false, 3)]
        [InlineData(true, 0)]
        public void Restarts_Disabled_Does_Not_Restart(bool autoRestartOnDrift, int maxRestarts)
        {
            string dir = CreateTempDir("su2_ap1_disabled");
            try
            {
                string meshPath = Path.Combine(dir, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshPath, "dummy");

                int calls = 0;
                var solver = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" },
                    AutoRestartOnDrift = autoRestartOnDrift,
                    MaxConvergenceRestarts = maxRestarts
                })
                {
                    RunSu2Override = (wd, cores) =>
                    {
                        calls++;
                        File.WriteAllLines(
                            Path.Combine(wd, "history.csv"),
                            BuildHistoryLines(200, i => 0.050 - 0.0001 * i));
                        File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), "restart-data");
                        return (0, string.Empty, string.Empty);
                    }
                };

                var config = new SimulationConfig
                {
                    Su2MaxIterations = 1000,
                    CauchyElements = 100,
                    IterativeToleranceFraction = 0.25f
                };
                config.MeshCalibration.Tolerance = 0.05f;

                var record = CreateDummyRecord();
                solver.Solve(meshPath, record, config, dir);

                Assert.Equal(1, calls);
                Assert.True(record.SimulationFailed);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Budget_Exhausted_Does_Not_Restart_And_MaxRestarts_Is_Capped()
        {
            string dirBudget = CreateTempDir("su2_ap1_budget");
            string dirCap = CreateTempDir("su2_ap1_cap");
            try
            {
                // Fall A: Budget direkt im 1. Block ausgeschöpft (500 von 500)
                string meshA = Path.Combine(dirBudget, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshA, "dummy");
                int callsA = 0;
                var solverA = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" },
                    AutoRestartOnDrift = true,
                    MaxConvergenceRestarts = 3
                })
                {
                    RunSu2Override = (wd, cores) =>
                    {
                        callsA++;
                        File.WriteAllLines(
                            Path.Combine(wd, "history.csv"),
                            BuildHistoryLines(500, i => 0.050 - 0.00005 * i));
                        File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), "restart-data");
                        return (0, string.Empty, string.Empty);
                    }
                };

                var configA = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 100 };
                configA.MeshCalibration.Tolerance = 0.05f;
                var recA = CreateDummyRecord();
                solverA.Solve(meshA, recA, configA, dirBudget);
                Assert.Equal(1, callsA);
                Assert.True(recA.SimulationFailed);

                // Fall B: anhaltende Drift über alle Restarts -> Deckel MaxConvergenceRestarts = 2 (max 3 Blöcke)
                string meshB = Path.Combine(dirCap, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshB, "dummy");
                int callsB = 0;
                var solverB = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" },
                    AutoRestartOnDrift = true,
                    MaxConvergenceRestarts = 2
                })
                {
                    RunSu2Override = (wd, cores) =>
                    {
                        callsB++;
                        File.WriteAllLines(
                            Path.Combine(wd, "history.csv"),
                            BuildHistoryLines(100, i => 0.080 - 0.0002 * ((callsB - 1) * 100 + i)));
                        File.WriteAllText(Path.Combine(wd, "restart.dat"), "restart-data");
                        return (0, string.Empty, string.Empty);
                    }
                };

                var configB = new SimulationConfig { Su2MaxIterations = 2000, CauchyElements = 100 };
                configB.MeshCalibration.Tolerance = 0.05f;
                var recB = CreateDummyRecord();
                solverB.Solve(meshB, recB, configB, dirCap);
                Assert.Equal(3, callsB);
                Assert.Equal(300f, recB.PassiveParameters["Su2_Iterations"]);
                Assert.True(recB.SimulationFailed);
            }
            finally
            {
                if (Directory.Exists(dirBudget)) Directory.Delete(dirBudget, true);
                if (Directory.Exists(dirCap)) Directory.Delete(dirCap, true);
            }
        }

        [Fact]
        public void FindSteadyRestartFile_Picks_Newest_NonEmpty_Candidate()
        {
            string dir = CreateTempDir("su2_ap1_findrestart");
            try
            {
                Assert.Null(Su2Solver.FindSteadyRestartFile(dir));

                // Leere Datei wird ignoriert
                string emptyFlow = Path.Combine(dir, "restart_flow.dat");
                File.WriteAllText(emptyFlow, string.Empty);
                Assert.Null(Su2Solver.FindSteadyRestartFile(dir));

                string restartDat = Path.Combine(dir, "restart.dat");
                File.WriteAllText(restartDat, "old-content");
                File.SetLastWriteTimeUtc(restartDat, new DateTime(2026, 11, 13, 10, 0, 0, DateTimeKind.Utc));

                File.WriteAllText(emptyFlow, "newer-content");
                File.SetLastWriteTimeUtc(emptyFlow, new DateTime(2026, 11, 13, 11, 0, 0, DateTimeKind.Utc));

                Assert.Equal("restart_flow.dat", Su2Solver.FindSteadyRestartFile(dir));

                File.SetLastWriteTimeUtc(restartDat, new DateTime(2026, 11, 13, 12, 0, 0, DateTimeKind.Utc));
                Assert.Equal("restart.dat", Su2Solver.FindSteadyRestartFile(dir));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Theory]
        [InlineData("1e-5", "1e-6")]
        [InlineData("1e-6", "1e-7")]
        [InlineData("1e-7", "1e-7")]
        [InlineData("5e-6", "5e-7")]
        public void TightenTolerance_Respects_Factor_10_And_Floor_1e7(string input, string expected)
        {
            Assert.Equal(expected, Su2Solver.TightenTolerance(input));
        }

        [Fact]
        public void Solver_Logs_Three_Decimal_Fach_Format()
        {
            string dir = CreateTempDir("su2_ap1_fach");
            TextWriter originalOut = Console.Out;
            var captured = new StringWriter();
            try
            {
                var solver = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" }
                });
                var config = new SimulationConfig
                {
                    Su2MaxIterations = 200,
                    CauchyElements = 100,
                    IterativeToleranceFraction = 0.25f
                };
                config.MeshCalibration.Tolerance = 0.05f;

                // 1) ITER erreicht, aber kleine Drift (~0.04-fach) -> "0.0xx-fach der Grenze"
                string histConverged = Path.Combine(dir, "hist_conv.csv");
                File.WriteAllLines(histConverged, BuildHistoryLines(200, i => 0.030 + 0.0000003 * i));

                // 2) Driftende Rechnung (> 1-fach) -> "x.xxx-fach der erlaubten Drift"
                string histDrift = Path.Combine(dir, "hist_drift.csv");
                File.WriteAllLines(histDrift, BuildHistoryLines(200, i => 0.050 - 0.0001 * i));

                Console.SetOut(captured);
                solver.ReadMetricsFromHistory(histConverged, CreateDummyRecord(), config);
                solver.ReadMetricsFromHistory(histDrift, CreateDummyRecord(), config);
                Console.SetOut(originalOut);

                string log = captured.ToString();
                Assert.Matches(@"\d+\.\d{3}-fach der Grenze", log);
                Assert.Matches(@"\d+\.\d{3}-fach der erlaubten Drift", log);
                Assert.DoesNotMatch(@"(?<!\d)\d+\.\d-fach", log);
            }
            finally
            {
                Console.SetOut(originalOut);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void Config_Generator_Emits_Truthful_Residual_Comment_And_Steady_Restart_Keys()
        {
            string dir = CreateTempDir("su2_ap1_cfg");
            try
            {
                string cfgPath = Path.Combine(dir, "restart.cfg");
                var config = SimulationConfig.CreateDefault();
                var options = new Su2SolverOptions();

                Su2ConfigGenerator.Generate(
                    cfgPath,
                    "/tmp/mesh.su2",
                    0.001f,
                    config,
                    options,
                    steadyRestartFile: "restart_flow.dat",
                    convStartIterOverride: 42);

                string[] lines = File.ReadAllLines(cfgPath);
                string[] commentLines = lines.Where(l => l.TrimStart().StartsWith("%")).ToArray();

                // Wahrheitsgemäßer Kommentar zu -8 und ASCII/Längen-Vertrag <= 100
                Assert.DoesNotContain(commentLines, l => l.Contains("Residuum unter 10^-8 faellt", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("CONV_RESIDUAL_MINVAL= -8", StringComparison.OrdinalIgnoreCase)
                    && l.Contains("selten", StringComparison.OrdinalIgnoreCase));
                foreach (string comment in commentLines)
                {
                    Assert.True(comment.Length <= 100, $"Kommentarzeile zu lang ({comment.Length}): {comment}");
                    Assert.All(comment, ch => Assert.True(ch <= 127, $"Nicht-ASCII '{ch}' in: {comment}"));
                }

                Assert.Contains("CONV_STARTITER= 42", lines);
                Assert.Contains("RESTART_SOL= YES", lines);
                Assert.Contains("SOLUTION_FILENAME= restart_flow.dat", lines);
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void PrepareUnsteadyRestart_Falls_Back_To_RestartFlowDat()
        {
            string dir = CreateTempDir("su2_ap1_unsteady_fallback");
            try
            {
                File.WriteAllText(Path.Combine(dir, "restart_flow.dat"), "flow-solution");

                Program.PrepareUnsteadyRestart(dir);

                Assert.True(File.Exists(Path.Combine(dir, "restart_00000.dat")));
                Assert.True(File.Exists(Path.Combine(dir, "restart_00001.dat")));
                Assert.Equal("flow-solution", File.ReadAllText(Path.Combine(dir, "restart_00000.dat")));
                Assert.Equal("flow-solution", File.ReadAllText(Path.Combine(dir, "restart_00001.dat")));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
