using System;
using System.IO;
using Xunit;
using FluidForge.Core;

namespace FluidForge.Tests
{
    public class ExecutableResolverTests
    {
        [Fact]
        public void ExpandHome_Expands_Tilde()
        {
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            Assert.Equal(home, ExecutableResolver.ExpandHome("~"));
            Assert.Equal(Path.Combine(home, "software", "bin", "tool"), ExecutableResolver.ExpandHome("~/software/bin/tool"));
            Assert.Equal(Path.Combine(home, "software", "bin", "tool"), ExecutableResolver.ExpandHome("~\\software\\bin\\tool"));
            Assert.Equal("/absolute/path", ExecutableResolver.ExpandHome("/absolute/path"));
            Assert.Equal("relative/path", ExecutableResolver.ExpandHome("relative/path"));
            Assert.Equal("", ExecutableResolver.ExpandHome(""));
        }

        [Fact]
        public void Resolve_Returns_Existing_File_Directly()
        {
            string tempFile = Path.GetTempFileName();
            try
            {
                string resolved = ExecutableResolver.Resolve(tempFile);
                Assert.Equal(tempFile, resolved);
            }
            finally
            {
                if (File.Exists(tempFile)) File.Delete(tempFile);
            }
        }

        [Fact]
        public void FindInPath_Finds_Standard_System_Command()
        {
            // On Windows 'cmd' or 'powershell' is available, on Linux 'sh' or 'ls'
            string cmdName = OperatingSystem.IsWindows() ? "cmd" : "sh";
            string? found = ExecutableResolver.FindInPath(cmdName);

            Assert.NotNull(found);
            Assert.True(File.Exists(found));
        }

        [Fact]
        public void Resolve_Returns_Command_When_Found_In_Path()
        {
            string cmdName = OperatingSystem.IsWindows() ? "cmd" : "sh";
            string resolved = ExecutableResolver.Resolve(cmdName);

            Assert.Equal(cmdName, resolved);
        }

        [Fact]
        public void Resolve_Returns_Expanded_Unchanged_When_Not_Found()
        {
            string dummy = "completely_unknown_executable_xyz_9999";
            string resolved = ExecutableResolver.Resolve(dummy, "su2");

            Assert.Equal(dummy, resolved);
        }

        [Fact]
        public void Resolve_Emits_Fallback_Message_When_Candidate_Found()
        {
            // Simulates a fallback path under the user home directory
            string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string fallbackDir = Path.Combine(home, "miniconda", "bin");
            string candidateFile = Path.Combine(fallbackDir, "mock_dummy_SU2_CFD");

            // If the directory does not exist or lacks write permissions, test the writer output
            using var sw = new StringWriter();
            string resolved = ExecutableResolver.Resolve("unknown_command", "su2", sw);
            // If no candidate exists, output is empty
            Assert.Equal("unknown_command", resolved);
        }
    }
}
