using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Xunit;

using FluidForge;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
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
                            // Early Cauchy stop at iteration 200 with strong drift in CD
                            File.WriteAllLines(
                                Path.Combine(wd, "history.csv"),
                                BuildHistoryLines(200, i => 0.050 - 0.0001 * i));
                            File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), "restart-block-0");
                        }
                        else
                        {
                            // Restart block restarts iteration count from 0 and converges stably to 0.030
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

                // Block 0: initial config without RESTART_SOL
                Assert.Contains("ITER= 1000", observedConfigs[0]);
                Assert.Contains("CONV_CAUCHY_EPS= 1e-5", observedConfigs[0]);
                Assert.Contains("CONV_STARTITER= 100", observedConfigs[0]);
                Assert.DoesNotContain(observedConfigs[0], l => l.StartsWith("RESTART_SOL=", StringComparison.Ordinal));

                // Block 1: remaining budget 800, tightened Cauchy tolerance 1e-6, CONV_STARTITER= 80, RESTART_SOL= YES
                Assert.Contains("ITER= 800", observedConfigs[1]);
                Assert.Contains("CONV_CAUCHY_EPS= 1e-6", observedConfigs[1]);
                Assert.Contains("CONV_STARTITER= 80", observedConfigs[1]);
                Assert.Contains("RESTART_SOL= YES", observedConfigs[1]);
                Assert.Contains("SOLUTION_FILENAME= restart_flow.dat", observedConfigs[1]);

                // Combined history.csv has 450 data rows with monotonic iteration 0..449
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
                // Case A: budget exhausted directly in 1st block (500 of 500)
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

                // Case B: persistent drift across all restarts -> cap MaxConvergenceRestarts = 2 (max 3 blocks)
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

                // Empty file is ignored
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

        [Theory]
        [InlineData("5e-6", "3e-6", "3e-6")]
        [InlineData("3e-6", "3e-6", "3e-6")]
        [InlineData("1e-6", "3e-6", "1e-6")] // Never-loosen guard: already tighter than floor
        [InlineData("1e-4", "3e-6", "1e-5")]
        public void TightenTolerance_Respects_Configurable_Floor_And_Never_Loosens(
            string input, string floor, string expected)
        {
            Assert.Equal(expected, Su2Solver.TightenTolerance(input, floor));
        }

        [Fact]
        public void Solver_Stops_Without_Redundant_Restart_When_CauchyToleranceFloor_Reached()
        {
            string dir = CreateTempDir("su2_floor_stop");
            TextWriter originalOut = Console.Out;
            var captured = new StringWriter();
            try
            {
                string meshPath = Path.Combine(dir, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshPath, "dummy");

                var observedEps = new List<string>();
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
                        string epsLine = cfgLines.First(l => l.StartsWith("CONV_CAUCHY_EPS=", StringComparison.Ordinal));
                        observedEps.Add(epsLine);

                        // Persistent drift and early Cauchy stop in every block
                        File.WriteAllLines(
                            Path.Combine(wd, "history.csv"),
                            BuildHistoryLines(150, i => 0.080 - 0.0002 * ((observedEps.Count - 1) * 150 + i)));
                        File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), $"restart-{observedEps.Count}");
                        return (0, string.Empty, string.Empty);
                    }
                };

                var config = new SimulationConfig
                {
                    Su2MaxIterations = 2000,
                    CauchyElements = 100,
                    CauchyTolerance = "5e-6",
                    CauchyToleranceFloor = "3e-6",
                    IterativeToleranceFraction = 0.25f
                };
                config.MeshCalibration.Tolerance = 0.05f;

                var record = CreateDummyRecord();
                Console.SetOut(captured);
                solver.Solve(meshPath, record, config, dir);
                Console.SetOut(originalOut);

                // Block 0 runs at 5e-6, Block 1 runs at 3e-6; next tighten would stay at 3e-6 -> no redundant Block 2!
                Assert.Equal(2, observedEps.Count);
                Assert.Equal("CONV_CAUCHY_EPS= 5e-6", observedEps[0]);
                Assert.Equal("CONV_CAUCHY_EPS= 3e-6", observedEps[1]);
                Assert.Contains("CauchyToleranceFloor (3e-6) already reached", captured.ToString());
                Assert.True(record.SimulationFailed);
            }
            finally
            {
                Console.SetOut(originalOut);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
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

                // 1) ITER reached, but small drift (~0.04x) -> "0.0xx x limit"
                string histConverged = Path.Combine(dir, "hist_conv.csv");
                File.WriteAllLines(histConverged, BuildHistoryLines(200, i => 0.030 + 0.0000003 * i));

                // 2) Drifting run (> 1x) -> "x.xxx x allowed drift"
                string histDrift = Path.Combine(dir, "hist_drift.csv");
                File.WriteAllLines(histDrift, BuildHistoryLines(200, i => 0.050 - 0.0001 * i));

                Console.SetOut(captured);
                solver.ReadMetricsFromHistory(histConverged, CreateDummyRecord(), config);
                solver.ReadMetricsFromHistory(histDrift, CreateDummyRecord(), config);
                Console.SetOut(originalOut);

                string log = captured.ToString();
                Assert.Matches(@"\d+\.\d{3}x limit", log);
                Assert.Matches(@"\d+\.\d{3}x allowed drift", log);
                Assert.DoesNotMatch(@"(?<!\d)\d+\.\dx", log);
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

                // Truthful comment on -8 and ASCII/length contract <= 100
                Assert.DoesNotContain(commentLines, l => l.Contains("residual drops below 10^-8", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(commentLines, l => l.Contains("CONV_RESIDUAL_MINVAL= -8", StringComparison.OrdinalIgnoreCase)
                    && l.Contains("rarely", StringComparison.OrdinalIgnoreCase));
                foreach (string comment in commentLines)
                {
                    Assert.True(comment.Length <= 100, $"Comment line too long ({comment.Length}): {comment}");
                    Assert.All(comment, ch => Assert.True(ch <= 127, $"Non-ASCII '{ch}' in: {comment}"));
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

        [Fact]
        public void NonZero_ExitCode_Marks_SimulationFailed_Even_When_History_Has_Enough_Rows()
        {
            string dir = CreateTempDir("su2_nonzero_exit");
            TextWriter originalOut = Console.Out;
            var captured = new StringWriter();
            try
            {
                string meshPath = Path.Combine(dir, "Model_Gen1_Var1.su2");
                File.WriteAllText(meshPath, "dummy");

                var solver = new Su2Solver(new Su2SolverOptions
                {
                    ResultMetrics = new() { ["Drag"] = "CD" }
                })
                {
                    RunSu2Override = (wd, cores) =>
                    {
                        File.WriteAllLines(Path.Combine(wd, "history.csv"), BuildHistoryLines(10, i => i == 9 ? 1e19 : 0.030));
                        return (1, "Error: Residual > 10^20", "MPI_Abort(1)");
                    }
                };

                var config = new SimulationConfig { Su2MaxIterations = 1000, CauchyElements = 100 };
                var record = CreateDummyRecord();

                Console.SetOut(captured);
                solver.Solve(meshPath, record, config, dir);
                Console.SetOut(originalOut);

                Assert.True(record.SimulationFailed);
                Assert.Equal(EvaluationFailureStage.Solver, record.FailedStage);
                Assert.False(record.PassiveParameters.ContainsKey("Drag"));

                string log = captured.ToString();
                Assert.Contains("SU2 exited with code 1 (diverged or aborted)", log);
                Assert.DoesNotContain("Analysis succeeded", log);
                Assert.True(File.Exists(Path.Combine(dir, OutputConventions.ErrorLogsDirectory, "SU2_Error_Model_Gen1_Var1.txt")));
            }
            finally
            {
                Console.SetOut(originalOut);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }

        [Fact]
        public void NonZero_ExitCode_On_Restart_Also_Marks_SimulationFailed()
        {
            string dir = CreateTempDir("su2_nonzero_restart_exit");
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
                        if (calls == 1)
                        {
                            File.WriteAllLines(Path.Combine(wd, "history.csv"), BuildHistoryLines(200, i => 0.050 - 0.0001 * i));
                            File.WriteAllText(Path.Combine(wd, "restart_flow.dat"), "restart-block-0");
                            return (0, string.Empty, string.Empty);
                        }

                        File.WriteAllLines(Path.Combine(wd, "history.csv"), BuildHistoryLines(10, _ => 1e19));
                        return (1, "MPI_Abort(1)", string.Empty);
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

                Assert.Equal(2, calls);
                Assert.True(record.SimulationFailed);
                Assert.Equal(EvaluationFailureStage.Solver, record.FailedStage);
                Assert.True(File.Exists(Path.Combine(dir, OutputConventions.ErrorLogsDirectory, "SU2_Error_Model_Gen1_Var1.txt")));
            }
            finally
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
            }
        }
    }
}
