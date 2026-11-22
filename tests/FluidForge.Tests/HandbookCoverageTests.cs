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
    /// TODO-49: <c>src/projects/BEDIENHANDBUCH.md</c> explains every setting and every command.
    ///
    /// <para>
    /// A handbook that omits a setting is worse than none: you rely on it and then look in the wrong place.
    /// Therefore every new key and every new <c>sim.ps1</c> command breaks this test until it is documented
    /// in the handbook — written as <c>`Name`</c>, just as the handbook does everywhere.
    /// </para>
    /// </summary>
    public class HandbookCoverageTests
    {
        private const string HandbookFileName = "BEDIENHANDBUCH.md";

        private static string Handbook
        {
            get
            {
                string? projectsRoot = ProjectPaths.ResolveProjectsRoot();
                Assert.True(projectsRoot != null, "Directory src/projects/ was not found.");

                string file = Path.Combine(projectsRoot!, HandbookFileName);
                Assert.True(File.Exists(file), $"src/projects/{HandbookFileName} is missing.");

                return File.ReadAllText(file);
            }
        }

        public static IEnumerable<object[]> SettingTypes() => new[]
        {
            new object[] { typeof(SimulationConfig), "simulation.json" },
            new object[] { typeof(BayesConfig), "simulation.json (Bayes)" },
            new object[] { typeof(Su2SolverOptions), "solvers/su2.json" },
            new object[] { typeof(GmshMesherOptions), "solvers/gmsh.json" },
            new object[] { typeof(Gmsh2DMesherOptions), "solvers/gmsh.json (2D)" },
            new object[] { typeof(ProjectConfigDto), "project.json" },
        };

        [Theory]
        [MemberData(nameof(SettingTypes))]
        public void Every_Setting_Is_Explained(System.Type type, string file)
        {
            string handbook = Handbook;

            var missing = type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(property => property.CanWrite)
                .Select(property => property.Name)
                .Where(name => !handbook.Contains($"`{name}`"))
                .ToList();

            Assert.True(missing.Count == 0,
                $"Handbook is missing setting(s) from {file}: {string.Join(", ", missing)}. "
                + $"Please document in src/projects/{HandbookFileName}, Section 4.");
        }

        [Fact]
        public void Every_Script_Command_Is_Explained()
        {
            string? projectsRoot = ProjectPaths.ResolveProjectsRoot();
            Assert.True(projectsRoot != null);

            string sim = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(projectsRoot!)!)!, "scripts", "sim.ps1"));

            Match set = Regex.Match(sim, @"\[ValidateSet\(([^\)]*)\)\]");
            Assert.True(set.Success, "Command list (ValidateSet) in scripts/sim.ps1 was not found.");

            string[] commands = Regex.Matches(set.Groups[1].Value, "'([^']+)'")
                .Select(match => match.Groups[1].Value)
                .ToArray();

            Assert.NotEmpty(commands);

            string handbook = Handbook;
            var missing = commands.Where(command => !handbook.Contains($"`{command}`")).ToList();

            Assert.True(missing.Count == 0,
                $"Handbook is missing command(s): {string.Join(", ", missing)} (Section 6).");
        }
    }
}
