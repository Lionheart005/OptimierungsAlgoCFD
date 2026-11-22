using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// Ensures the modular documentation under <c>docs/</c> explains every configurable JSON setting,
    /// every <c>scripts/sim.ps1</c> command, and every curated physics preset.
    /// </summary>
    public class HandbookCoverageTests
    {
        private static string RepoRoot
        {
            get
            {
                string? root = ProjectPaths.ResolveRepositoryRoot();
                Assert.True(root != null, "Repository root was not found.");
                return root!;
            }
        }

        private static string ReadDoc(string fileName)
        {
            string file = Path.Combine(RepoRoot, "docs", fileName);
            Assert.True(File.Exists(file), $"docs/{fileName} is missing.");
            return File.ReadAllText(file);
        }

        public static IEnumerable<object[]> SettingTypes() => new[]
        {
            new object[] { typeof(SimulationConfig), "simulation.json" },
            new object[] { typeof(AutoStopConfig), "simulation.json (AutoStop)" },
            new object[] { typeof(BayesConfig), "simulation.json (Bayes)" },
            new object[] { typeof(ControlRunConfig), "simulation.json (ControlRuns)" },
            new object[] { typeof(MeshCalibrationConfig), "simulation.json (MeshCalibration)" },
            new object[] { typeof(ChampionVerificationConfig), "simulation.json (ChampionVerification)" },
            new object[] { typeof(Su2SolverOptions), "solvers/su2.json" },
            new object[] { typeof(Su2TimeDomainOptions), "solvers/su2.json (TimeDomain)" },
            new object[] { typeof(GmshMesherOptions), "solvers/gmsh.json" },
            new object[] { typeof(Gmsh2DMesherOptions), "solvers/gmsh.json (2D)" },
            new object[] { typeof(ProjectConfigDto), "project.json" },
        };

        [Theory]
        [MemberData(nameof(SettingTypes))]
        public void Every_Setting_Is_Explained(Type type, string file)
        {
            string reference = ReadDoc("config-reference.md");

            var missing = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite)
                .Select(property => property.Name)
                .Where(name => !reference.Contains($"`{name}`"))
                .ToList();

            Assert.True(missing.Count == 0,
                $"docs/config-reference.md is missing setting(s) from {file}: {string.Join(", ", missing)}. "
                + "Please document every setting wrapped in backticks.");
        }

        [Fact]
        public void Every_Script_Command_Is_Explained()
        {
            string sim = File.ReadAllText(Path.Combine(RepoRoot, "scripts", "sim.ps1"));

            Match set = Regex.Match(sim, @"\[ValidateSet\(([^\)]*)\)\]");
            Assert.True(set.Success, "Command list (ValidateSet) in scripts/sim.ps1 was not found.");

            string[] commands = Regex.Matches(set.Groups[1].Value, "'([^']+)'")
                .Select(match => match.Groups[1].Value)
                .ToArray();

            Assert.NotEmpty(commands);

            string workflowDoc = ReadDoc("server-workflow.md");
            var missing = commands.Where(command => !workflowDoc.Contains($"`{command}`")).ToList();

            Assert.True(missing.Count == 0,
                $"docs/server-workflow.md is missing command(s): {string.Join(", ", missing)}.");
        }

        [Fact]
        public void Every_Preset_Is_Documented()
        {
            string presetsDoc = ReadDoc("physics-presets.md");
            var missing = SimulationPresets.All
                .Select(p => p.Name)
                .Where(name => !presetsDoc.Contains($"`{name}`"))
                .ToList();

            Assert.True(missing.Count == 0,
                $"docs/physics-presets.md is missing preset(s): {string.Join(", ", missing)}.");
        }
    }
}
