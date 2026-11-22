using System.IO;
using System.Text.RegularExpressions;
using Xunit;

using FluidForge.Core;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-28: the launch scripts are the only place where switching between projects
    /// happens — and the only part this test project cannot execute directly (there is no
    /// bash on the dev machine, and the Windows side talks to a server over SSH).
    ///
    /// <para>
    /// These tests therefore check <b>text</b>, not behavior. That is modest, but it is
    /// the exact safeguard that can save an entire night of compute: they fail if someone
    /// reintroduces the silent project default or breaks the switch chain.
    /// They do <b>not</b> replace a run on the server.
    /// </para>
    /// </summary>
    public class ScriptContractTests
    {
        private static string RepoRoot
        {
            get
            {
                string? projectsRoot = ProjectPaths.ResolveProjectsRoot();

                Assert.True(projectsRoot != null, "Directory src/projects/ was not found.");

                // src/projects -> src -> repo root
                return Path.GetDirectoryName(Path.GetDirectoryName(projectsRoot)!)!;
            }
        }

        /// <summary>The <c>scripts/</c> directory in the repository root.</summary>
        private static string ScriptsDirectory
        {
            get
            {
                string scripts = Path.Combine(RepoRoot, "scripts");

                Assert.True(Directory.Exists(scripts), $"The scripts/ folder is missing (looked at: {scripts}).");

                return scripts;
            }
        }

        private static string Read(string fileName)
        {
            string file = Path.Combine(ScriptsDirectory, fileName);

            Assert.True(File.Exists(file), $"scripts/{fileName} is missing.");

            return File.ReadAllText(file);
        }

        private static string ReadRepoFile(string relativePath)
        {
            string file = Path.Combine(RepoRoot, relativePath);

            Assert.True(File.Exists(file), $"{relativePath} is missing.");

            return File.ReadAllText(file);
        }

        /// <summary>
        /// The same file without its comment lines.
        ///
        /// <para>
        /// Needed when searching for patterns allowed in <b>explanations</b> but forbidden in
        /// <b>code</b>: the comment above <c>PROJECT_NAME</c> quotes the old line
        /// <c>${SIM_PROJECT:-MantaAuv}</c> so readers can later see what was wrong with it.
        /// That explanation is precisely why the bug won't return — it must not trip the test.
        /// </para>
        /// </summary>
        private static string WithoutComments(string text)
        {
            var kept = new System.Collections.Generic.List<string>();

            foreach (string line in text.Split('\n'))
            {
                if (!line.TrimStart().StartsWith("#")) kept.Add(line);
            }

            return string.Join("\n", kept);
        }

        /// <summary>
        /// <c>SIM_PROJECT</c> must not have a fallback value. When <c>${SIM_PROJECT:-MantaAuv}</c>
        /// was there, every typo silently ran MantaAuv — for hours, overwriting the CSV of a
        /// real run in its folder. The empty form <c>${SIM_PROJECT:-}</c> is allowed and intended:
        /// it satisfies <c>set -u</c>, and <c>require_project</c> aborts afterward.
        /// </summary>
        [Fact]
        public void The_Runner_Has_No_Silent_Project_Default()
        {
            string runner = Read("sim-runner.sh");

            var silentDefault = Regex.Match(WithoutComments(runner), @"\$\{SIM_PROJECT:-\s*[^}\s]+\s*\}");

            Assert.False(silentDefault.Success,
                "scripts/sim-runner.sh sets a project default again: "
                + silentDefault.Value
                + ". A typo in SIM_PROJECT would silently run the wrong project. "
                + "Use ${SIM_PROJECT:-} plus require_project.");

            Assert.Contains("require_project", runner);
        }

        /// <summary>
        /// The project name must be passed explicitly into the tmux session. The command line
        /// is started by the tmux <b>server</b>, whose environment comes from whichever invocation
        /// started it — inheriting variables is unreliable here.
        /// </summary>
        [Fact]
        public void The_Runner_Passes_The_Project_Into_The_Tmux_Session()
        {
            Assert.Contains("SIM_PROJECT='$PROJECT_NAME'", Read("sim-runner.sh"));
        }

        /// <summary>
        /// Both sides must support the <c>projects</c> command — it answers
        /// "what is my project called", which is needed when nothing runs without a name.
        /// </summary>
        [Fact]
        public void Both_Scripts_Know_The_Projects_Command()
        {
            Assert.Matches(@"projects\)\s+cmd_projects", Read("sim-runner.sh"));
            Assert.Contains("'projects'", Read("sim.ps1"));
        }

        /// <summary>
        /// The switch chain from Windows to the program: <c>-Project</c> → <c>SIM_PROJECT</c>
        /// → <c>dotnet … "$PROJECT_NAME"</c>. If any link breaks, the wrong project (or none)
        /// runs silently — so every link is asserted here.
        /// </summary>
        [Fact]
        public void The_Project_Switch_Reaches_The_Program()
        {
            Assert.Contains("SIM_PROJECT='$Project'", Read("sim.ps1"));

            string runner = Read("sim-runner.sh");

            Assert.Contains("PROJECT_NAME=\"${SIM_PROJECT:-}\"", runner);
            Assert.Contains("dotnet \"$APP_DLL\" \"$PROJECT_NAME\"", runner);
        }

        /// <summary>
        /// <c>doctor</c> queries the effective configuration from the program instead of extracting
        /// it with <c>sed</c> from a single JSON: since TODO-22 there are four layers, and the
        /// single file is no longer authoritative.
        /// </summary>
        [Fact]
        public void Doctor_Asks_The_Program_For_The_Effective_Configuration()
        {
            string runner = Read("sim-runner.sh");

            Assert.Contains("--print-config", runner);
            Assert.Contains("--list-projects", runner);
        }

        /// <summary>
        /// The upload filter protects machine-specific files — including new ones inside the
        /// project folder, because it matches a name pattern rather than a path. It also keeps
        /// the Windows side itself off the server: the fingerprint is computed over this exact
        /// list, otherwise fixing a README would restart a running job.
        /// </summary>
        [Fact]
        public void The_Deploy_Filter_Still_Protects_The_Machine_Specific_Files()
        {
            string sim = Read("sim.ps1");

            Assert.Contains("*.local.json", sim);
            Assert.Contains(JsonConfigLoader.MachineSpecificSuffix, sim);
            Assert.Contains("'.pdf'", sim);
        }

        /// <summary>
        /// TODO-31: Every ssh and scp invocation carries timeouts. A single call without them
        /// is enough for a dead VPN connection to hang silently forever — right at the one
        /// call site someone forgot when retrofitting.
        /// </summary>
        [Fact]
        public void Every_Ssh_And_Scp_Call_Carries_The_Timeouts()
        {
            string code = WithoutComments(Read("sim.ps1"));

            var calls = Regex.Matches(code, @"&\s*(ssh|scp)\b[^\r\n]*");
            Assert.NotEmpty(calls);

            foreach (Match call in calls)
            {
                Assert.True(call.Value.Contains("@SshOptions"),
                    $"This call in scripts/sim.ps1 has no timeouts: {call.Value.Trim()}");
            }
        }

        /// <summary>
        /// Timeouts are intentionally generous: under full load the server responds sluggishly,
        /// and overly tight values previously dropped healthy connections. Anyone tightening them
        /// must pass this test — and read the rationale in the script.
        /// </summary>
        [Fact]
        public void The_Ssh_Timeouts_Stay_Generous()
        {
            string sim = Read("sim.ps1");

            int Value(string option)
            {
                Match match = Regex.Match(sim, $@"'{option}=(\d+)'");
                Assert.True(match.Success, $"{option} is missing in $SshOptions (scripts/sim.ps1).");
                return int.Parse(match.Groups[1].Value);
            }

            Assert.True(Value("ConnectTimeout") >= 60, "ConnectTimeout below 60 s drops healthy connections under load.");
            Assert.True(Value("ServerAliveInterval") * Value("ServerAliveCountMax") >= 180,
                "A connection should not be considered dead after less than 3 minutes of silence.");
        }

        /// <summary>
        /// TODO-31: When sim.ps1 fetch or status is invoked without an explicit -Project, the
        /// script queries 'lastproject' on the server for the most recently run project instead
        /// of silently using a hardcoded default.
        /// </summary>
        [Fact]
        public void Fetch_And_Status_Query_Lastproject_When_No_Project_Is_Given()
        {
            string sim = Read("sim.ps1");

            Assert.Contains("lastproject", sim);
            Assert.Contains("PSBoundParameters.ContainsKey('Project')", sim);
        }

        /// <summary>
        /// TODO-31: .last_project, like .last_exit, lives only on the server and does not
        /// belong in the Git repository.
        /// </summary>
        [Fact]
        public void Last_Project_File_Is_Ignored_In_Gitignore()
        {
            string gitignore = ReadRepoFile(".gitignore");

            Assert.Contains(".last_project", gitignore);
        }

        /// <summary>
        /// TODO-31: The local target folder for server results includes the project name so
        /// results from different projects never mix in the same timestamp folder:
        /// ServerResults/<Server>_<Project>_<Timestamp>/.
        /// </summary>
        [Fact]
        public void The_Fetch_Target_Folder_Contains_The_Project_Name()
        {
            string sim = Read("sim.ps1");

            Assert.Matches(@"\$\{Server\}_\$\{Project\}_\$stamp", sim);
        }

        /// <summary>
        /// TODO-31: The 'pack' command on the server bundles the results archive and supports
        /// the three scopes: standard (CSV, log, config, histories), champion (+champion VTUs),
        /// and all (entire results folder).
        /// </summary>
        [Fact]
        public void The_Runner_Pack_Command_Knows_The_Three_Scopes()
        {
            string runner = Read("sim-runner.sh");

            Assert.Matches(@"pack\)", runner);
            Assert.Contains("standard", runner);
            Assert.Contains("champion", runner);
            Assert.Contains("all", runner);
        }

        /// <summary>
        /// TODO-31: sim-runner.sh records the active project in .last_project inside cmd_foreground
        /// and prints it via the 'lastproject' command (without require_project).
        /// </summary>
        [Fact]
        public void The_Runner_Has_The_Lastproject_Command()
        {
            string runner = Read("sim-runner.sh");

            Assert.Contains(".last_project", runner);
            Assert.Matches(@"lastproject\)", runner);
        }

        /// <summary>
        /// TODO-42: Both scripts support the 'calibrate' command.
        /// Windows forwards it via -ThenCalibrate to the server, and the runner executes --calibrate.
        /// </summary>
        [Fact]
        public void Both_Scripts_Know_The_Calibrate_Command()
        {
            string sim = Read("sim.ps1");
            string runner = Read("sim-runner.sh");

            Assert.Contains("'calibrate'", sim);
            Assert.Contains("-ThenCalibrate", sim);
            Assert.Matches(@"calibrate\)\s+cmd_calibrate", runner);
            Assert.Contains("--calibrate", runner);
        }

        /// <summary>
        /// TODO-43: Both scripts support the 'recheck' command for fine verification
        /// of individual variants using parameters -Variant and -Finer.
        /// </summary>
        [Fact]
        public void Both_Scripts_Know_The_Recheck_Command()
        {
            string sim = Read("sim.ps1");
            string runner = Read("sim-runner.sh");

            Assert.Contains("'recheck'", sim);
            Assert.Contains("$Variant", sim);
            Assert.Contains("$Finer", sim);
            Assert.Matches(@"recheck\)\s+cmd_recheck", runner);
            Assert.Contains("--recheck", runner);
            Assert.Contains("--finer", runner);
        }

        [Fact]
        public void Runner_Excludes_TemplateInternal3D_Kills_OpenFoam_Processes_And_Checks_Isolated_OpenFoam()
        {
            string runner = Read("sim-runner.sh");
            string setup = ReadRepoFile("src/FluidForge/setup_server.sh");

            Assert.Contains("_TemplateInternal3D", runner);
            Assert.Contains("pkill -9 -u \"$USER\" -f simpleFoam", runner);
            Assert.Contains("pkill -9 -u \"$USER\" -f snappyHexMesh", runner);
            Assert.Contains("pkill -9 -u \"$USER\" -f cartesianMesh", runner);
            Assert.Contains("/usr/lib/openfoam/openfoam2506/etc/bashrc", runner);
            Assert.Contains("/usr/bin/mpirun", runner);
            Assert.Contains("opt-py", runner);

            Assert.Contains("/usr/lib/openfoam/openfoam2506/etc/bashrc", setup);
            Assert.Contains("conda create -y -n openfoam -c conda-forge openfoam", setup);
            Assert.Contains("opt-py", setup);
        }
    }
}
