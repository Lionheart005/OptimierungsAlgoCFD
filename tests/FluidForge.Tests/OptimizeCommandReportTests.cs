using System;
using System.ComponentModel;
using System.IO;
using FluidForge.Cli;
using FluidForge.Core;
using Xunit;

namespace FluidForge.Tests
{
    [Collection(ProcessWideStateCollection.Name)]
    public class OptimizeCommandReportTests
    {
        private static string CaptureReportOutput(Exception exception)
        {
            TextWriter originalOut = Console.Out;
            var buffer = new StringWriter();
            try
            {
                Console.SetOut(buffer);
                OptimizeCommand.Report(exception);
            }
            finally
            {
                Console.SetOut(originalOut);
            }
            return buffer.ToString();
        }

        [Fact]
        public void Report_Preserves_Bracketed_User_Error_Message()
        {
            var ex = new InvalidOperationException($"{OutputConventions.ConfigTag} Bad setting.");
            string output = CaptureReportOutput(ex);

            Assert.Contains("[CONFIG] Bad setting.", output);
            Assert.DoesNotContain("[CRITICAL]", output);
        }

        [Fact]
        public void Report_Formats_DllNotFoundException_For_PicoGk_With_Actionable_Hints()
        {
            var ex = new TypeInitializationException(
                "PicoGK.Library",
                new DllNotFoundException("Unable to load shared library 'libpicogk.so' or one of its dependencies."));

            string output = CaptureReportOutput(ex);

            Assert.Contains(OutputConventions.ErrorTag, output);
            Assert.Contains("libpicogk.so", output);
            Assert.Contains("setup_server.sh", output);
            Assert.Contains("sim.ps1 doctor", output);
            Assert.DoesNotContain("[CRITICAL]", output);
        }

        [Fact]
        public void Report_Formats_Win32Exception_With_Doctor_And_LocalConfig_Hints()
        {
            var ex = new Win32Exception(2, "No such file or directory: gmsh");
            string output = CaptureReportOutput(ex);

            Assert.Contains(OutputConventions.ErrorTag, output);
            Assert.Contains("sim.ps1 doctor", output);
            Assert.Contains("config/simulation.local.json", output);
            Assert.Contains("SU2_CFD", output);
            Assert.DoesNotContain("[CRITICAL]", output);
        }

        [Fact]
        public void Report_Formats_FileNotFoundException_And_DirectoryNotFoundException_Cleanly()
        {
            string fileOutput = CaptureReportOutput(new FileNotFoundException("Mesh file missing.", "mesh.su2"));
            Assert.Contains(OutputConventions.ErrorTag, fileOutput);
            Assert.Contains("mesh.su2", fileOutput);
            Assert.DoesNotContain("[CRITICAL]", fileOutput);

            string dirOutput = CaptureReportOutput(new DirectoryNotFoundException("Missing folder /tmp/work"));
            Assert.Contains(OutputConventions.ErrorTag, dirOutput);
            Assert.Contains("Missing folder /tmp/work", dirOutput);
            Assert.DoesNotContain("[CRITICAL]", dirOutput);
        }

        [Fact]
        public void Report_Falls_Back_To_Critical_For_Unexpected_Exceptions()
        {
            string output = CaptureReportOutput(new ArgumentNullException("param"));
            Assert.Contains("[ERROR] [CRITICAL] Program aborted:", output);
        }
    }
}
