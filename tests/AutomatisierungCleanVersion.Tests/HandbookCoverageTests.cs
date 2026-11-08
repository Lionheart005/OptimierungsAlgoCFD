using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Xunit;

using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-49: <c>src/projects/BEDIENHANDBUCH.md</c> erklärt jede Einstellung und jeden Befehl.
    ///
    /// <para>
    /// Ein Handbuch, das eine Einstellung verschweigt, ist schlimmer als keines: man verlässt sich
    /// darauf und sucht dann an der falschen Stelle. Deshalb bricht hier jeder neue Schlüssel und
    /// jeder neue <c>sim.ps1</c>-Befehl den Test, bis er im Handbuch steht — als <c>`Name`</c>
    /// geschrieben, so wie es das Handbuch überall tut.
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
                Assert.True(projectsRoot != null, "Das Verzeichnis src/projects/ wurde nicht gefunden.");

                string file = Path.Combine(projectsRoot!, HandbookFileName);
                Assert.True(File.Exists(file), $"src/projects/{HandbookFileName} fehlt.");

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
                $"Im Bedienhandbuch fehlt/fehlen Einstellung(en) aus {file}: {string.Join(", ", missing)}. "
                + $"Bitte in src/projects/{HandbookFileName}, Abschnitt 4, erklären.");
        }

        [Fact]
        public void Every_Script_Command_Is_Explained()
        {
            string? projectsRoot = ProjectPaths.ResolveProjectsRoot();
            Assert.True(projectsRoot != null);

            string sim = File.ReadAllText(Path.Combine(
                Path.GetDirectoryName(Path.GetDirectoryName(projectsRoot!)!)!, "scripts", "sim.ps1"));

            Match set = Regex.Match(sim, @"\[ValidateSet\(([^\)]*)\)\]");
            Assert.True(set.Success, "Die Befehlsliste (ValidateSet) in scripts/sim.ps1 wurde nicht gefunden.");

            string[] commands = Regex.Matches(set.Groups[1].Value, "'([^']+)'")
                .Select(match => match.Groups[1].Value)
                .ToArray();

            Assert.NotEmpty(commands);

            string handbook = Handbook;
            var missing = commands.Where(command => !handbook.Contains($"`{command}`")).ToList();

            Assert.True(missing.Count == 0,
                $"Im Bedienhandbuch fehlt/fehlen Befehl(e): {string.Join(", ", missing)} (Abschnitt 6).");
        }
    }
}
