using System;
using System.Diagnostics;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;
using Xunit;

namespace OptimierungsAlgoCFD.Tests
{
    [Collection(ProcessWideStateCollection.Name)]
    public class Su2ProcessRunnerTests
    {
        [Fact]
        public void BuildExecutionCommand_Uses_Mpi_When_Cores_Gt_1_And_MpiRunPath_Set()
        {
            var config = new SimulationConfig
            {
                MpiRunPath = "custom_mpirun_nonexistent",
                Su2Path = "custom_su2_nonexistent",
                MpiCores = 8
            };

            var (fileName, arguments, usesMpi) = Su2ProcessRunner.BuildExecutionCommand(config);

            Assert.True(usesMpi);
            Assert.Equal("custom_mpirun_nonexistent", fileName);
            Assert.Equal("-n 8 custom_su2_nonexistent current_config.cfg", arguments);
        }

        [Theory]
        [InlineData(1)]
        [InlineData(0)]
        [InlineData(-2)]
        public void BuildExecutionCommand_Uses_Direct_Mode_When_MpiCores_AtMost_One(int cores)
        {
            var config = new SimulationConfig
            {
                MpiRunPath = "custom_mpirun_nonexistent",
                Su2Path = "custom_su2_nonexistent",
                MpiCores = cores
            };

            var (fileName, arguments, usesMpi) = Su2ProcessRunner.BuildExecutionCommand(config);

            Assert.False(usesMpi);
            Assert.Equal("custom_su2_nonexistent", fileName);
            Assert.Equal("current_config.cfg", arguments);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("none")]
        [InlineData("NONE")]
        [InlineData(" None ")]
        public void BuildExecutionCommand_Uses_Direct_Mode_When_MpiRunPath_Missing_Or_None(string? mpiRunPath)
        {
            var config = new SimulationConfig
            {
                MpiRunPath = mpiRunPath!,
                Su2Path = "custom_su2_nonexistent",
                MpiCores = 6
            };

            var (fileName, arguments, usesMpi) = Su2ProcessRunner.BuildExecutionCommand(config, "my_run.cfg");

            Assert.False(usesMpi);
            Assert.Equal("custom_su2_nonexistent", fileName);
            Assert.Equal("my_run.cfg", arguments);
        }

        [Fact]
        public void CreateStartInfo_Sets_OmpNumThreads_In_DirectMode_When_Cores_Gt_1_And_Unset()
        {
            string? originalOmp = Environment.GetEnvironmentVariable("OMP_NUM_THREADS");
            try
            {
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", null);

                var config = new SimulationConfig
                {
                    MpiRunPath = "none",
                    Su2Path = "custom_su2_nonexistent",
                    MpiCores = 6
                };

                ProcessStartInfo startInfo = Su2ProcessRunner.CreateStartInfo("C:\\work", config);

                Assert.Equal("custom_su2_nonexistent", startInfo.FileName);
                Assert.Equal("current_config.cfg", startInfo.Arguments);
                Assert.True(startInfo.Environment.TryGetValue("OMP_NUM_THREADS", out string? ompThreads));
                Assert.Equal("6", ompThreads);
            }
            finally
            {
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", originalOmp);
            }
        }

        [Fact]
        public void CreateStartInfo_Preserves_Existing_OmpNumThreads_In_DirectMode()
        {
            string? originalOmp = Environment.GetEnvironmentVariable("OMP_NUM_THREADS");
            try
            {
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "3");

                var config = new SimulationConfig
                {
                    MpiRunPath = "",
                    Su2Path = "custom_su2_nonexistent",
                    MpiCores = 12
                };

                ProcessStartInfo startInfo = Su2ProcessRunner.CreateStartInfo("C:\\work", config);

                Assert.Equal("3", startInfo.Environment["OMP_NUM_THREADS"]);
            }
            finally
            {
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", originalOmp);
            }
        }

        [Fact]
        public void CreateStartInfo_Does_Not_Set_OmpNumThreads_In_MpiMode()
        {
            string? originalOmp = Environment.GetEnvironmentVariable("OMP_NUM_THREADS");
            try
            {
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", null);

                var config = new SimulationConfig
                {
                    MpiRunPath = "custom_mpirun_nonexistent",
                    Su2Path = "custom_su2_nonexistent",
                    MpiCores = 4
                };

                ProcessStartInfo startInfo = Su2ProcessRunner.CreateStartInfo("C:\\work", config);

                Assert.False(startInfo.Environment.ContainsKey("OMP_NUM_THREADS"));
            }
            finally
            {
                Environment.SetEnvironmentVariable("OMP_NUM_THREADS", originalOmp);
            }
        }

        [Fact]
        public void ExecuteSu2Process_Uses_RunSu2Override_And_Combines_Streams()
        {
            var config = new SimulationConfig { MpiCores = 5 };
            string? capturedDir = null;
            int capturedCores = 0;

            var (exitCode, output) = Su2ProcessRunner.ExecuteSu2Process(
                "/tmp/work",
                config,
                (dir, cores) =>
                {
                    capturedDir = dir;
                    capturedCores = cores;
                    return (42, "stdout-line", "stderr-line");
                });

            Assert.Equal("/tmp/work", capturedDir);
            Assert.Equal(5, capturedCores);
            Assert.Equal(42, exitCode);
            Assert.Equal($"stdout-line{Environment.NewLine}stderr-line", output);
        }
    }
}
