using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Moq;
using OptimierungsAlgoCFD.Composition;
using OptimierungsAlgoCFD.Core;
using Xunit;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>Gemeinsamer Temp-Ordner je Testklasse.</summary>
    public abstract class TempDirectoryTest : IDisposable
    {
        protected TempDirectoryTest()
        {
            Root = Path.Combine(Path.GetTempPath(), GetType().Name + "_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        protected string Root { get; }

        protected string Sub(string name)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(path);
            return path;
        }

        public void Dispose()
        {
            try { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); } catch (IOException) { }
        }
    }

    // =========================================================================
    // TODO-50: Seed, Fahrtenschreiber, Quellstand
    // =========================================================================

    public class RandomSeedTests
    {
        [Fact]
        public void RandomSeed_Defaults_To_Zero()
        {
            Assert.Equal(0, SimulationConfig.CreateDefault().RandomSeed);
        }

        [Fact]
        public void Resolve_Draws_A_Nonzero_Seed_For_Zero_And_Keeps_A_Configured_One()
        {
            for (int i = 0; i < 50; i++) Assert.NotEqual(0, RunSeed.Resolve(0));
            Assert.Equal(42, RunSeed.Resolve(42));
            Assert.Equal(-7, RunSeed.Resolve(-7));
        }

        [Fact]
        public void The_Log_Line_Tells_How_To_Repeat_The_Run()
        {
            Assert.Contains("\"RandomSeed\": 123456", RunSeed.Describe(123456, 0));
            Assert.Contains("aus simulation.json", RunSeed.Describe(5, 5));
        }

        private static SimulationContext Context()
        {
            var config = new SimulationConfig { RsmVirtualSimulations = 200 };
            var project = new ProjectConfig
            {
                ProjectName = "Seed",
                BaseParameters = new Dictionary<string, float> { ["A"] = 10f, ["B"] = 2f, ["C"] = 0.5f },
                MaxDeviations = new Dictionary<string, float> { ["A"] = 3f, ["B"] = 0.5f, ["C"] = 0.1f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    ["A"] = (1f, 20f), ["B"] = (1f, 4f), ["C"] = (0.1f, 1f)
                }
            };
            return new SimulationContext(config, project, Path.GetTempPath());
        }

        private static List<Dictionary<string, float>> Sequence(string algorithm, int seed)
        {
            var fitness = new Mock<IFitnessCalculator>().Object;
            IOptimizationAlgorithm optimizer = OptimizationAlgorithmFactory.Create(algorithm, fitness, RunSeed.ForOptimizer(seed));
            SimulationContext context = Context();
            return Enumerable.Range(1, 6).Select(v => optimizer.GenerateParameters(context, 1, v, 6)).ToList();
        }

        [Theory]
        [InlineData("Rsm")]
        [InlineData("Evolution")]
        public void Same_Seed_Gives_The_Same_Parameter_Sequence(string algorithm)
        {
            var first = Sequence(algorithm, 1234);
            var second = Sequence(algorithm, 1234);

            Assert.Equal(first.Count, second.Count);
            for (int i = 0; i < first.Count; i++) Assert.Equal(first[i], second[i]);
        }

        private static List<Dictionary<string, float>> Jitters(int seed)
        {
            var fitness = new Mock<IFitnessCalculator>();
            fitness.Setup(f => f.CalculateFitness(It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>()))
                   .Callback((ModelRecord r, SimulationConfig c) => r.Fitness = 1f);

            var captured = new List<Dictionary<string, float>>();
            var validator = new ChampionValidator(fitness.Object, RunSeed.ForValidator(seed));

            for (int round = 0; round < 5; round++)
            {
                var candidate = new ModelRecord
                {
                    Fitness = 1f,
                    ActiveParameters = new Dictionary<string, float> { ["A"] = 1f, ["B"] = 2f, ["C"] = 3f }
                };
                validator.ValidateChampion(
                    new List<ModelRecord> { candidate },
                    new SimulationConfig { BouncerJitter = 0.5f },
                    p => { captured.Add(p); return new ModelRecord(); });
            }

            return captured;
        }

        [Fact]
        public void Same_Seed_Gives_The_Same_Bouncer_Jitter()
        {
            var first = Jitters(99);
            var second = Jitters(99);

            Assert.Equal(5, first.Count);
            for (int i = 0; i < first.Count; i++) Assert.Equal(first[i], second[i]);
        }
    }

    public class RunInfoWriterTests : TempDirectoryTest
    {
        [Fact]
        public void Writes_All_Fields_And_Sets_Status_And_End_When_Finished()
        {
            string dir = Sub("Ergebnisse");
            var source = new RunInfoSource { Commit = "abc", Branch = "main", DeployHash = "1234", UncommittedChanges = "false" };
            var tools = new Dictionary<string, string> { ["SU2"] = "7.5.1", ["Gmsh"] = ToolVersionProbe.Unknown };

            RunInfo info = RunInfoWriter.Create("Test", 4711, new[] { "Test", "--resume" }, source, tools, "sha256:ff");
            string path = RunInfoWriter.Write(dir, info);

            Assert.Equal(Path.Combine(dir, RunInfoWriter.FileName), path);

            JsonObject json = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            foreach (string key in new[] { "Format", "Projekt", "Start", "Ende", "Status", "Aufruf", "ZufallsSeed",
                                           "Rechner", "Quellstand", "Programme", "KonfigurationsHash", "Wiederaufnahmen" })
                Assert.True(json.ContainsKey(key), $"run-info.json fehlt '{key}'.");

            Assert.Equal("laeuft", json["Status"]!.GetValue<string>());
            Assert.Null(json["Ende"]);
            Assert.Equal(4711, json["ZufallsSeed"]!.GetValue<int>());
            Assert.Equal("7.5.1", json["Programme"]!["SU2"]!.GetValue<string>());
            Assert.Equal("main", json["Quellstand"]!["Branch"]!.GetValue<string>());
            Assert.True(json["Rechner"]!["Prozessorkerne"]!.GetValue<int>() > 0);

            RunInfoWriter.Finish(dir, RunInfo.StatusCompleted);

            RunInfo? finished = RunInfoWriter.Read(dir);
            Assert.NotNull(finished);
            Assert.Equal("abgeschlossen", finished!.Status);
            Assert.False(string.IsNullOrEmpty(finished.Ende));

            RunInfoWriter.AddResume(dir, 37);
            RunInfo? resumed = RunInfoWriter.Read(dir);
            Assert.Equal("laeuft", resumed!.Status);
            Assert.Null(resumed.Ende);
            Assert.Equal(37, resumed.Wiederaufnahmen.Single().UebernommeneRechnungen);
        }

        [Fact]
        public void Finish_Without_File_Does_Not_Throw()
        {
            RunInfoWriter.Finish(Sub("leer"), RunInfo.StatusAborted);
        }

        [Fact]
        public void Deploy_Info_Wins_Over_The_Git_Folder()
        {
            string repo = Sub("repo");
            GitInfoTests.WriteGit(repo, "ref: refs/heads/local", ("refs/heads/local", new string('a', 40)));
            File.WriteAllText(Path.Combine(repo, RunInfoWriter.DeployHashFileName), "hash-aus-datei");

            RunInfoSource fromGit = RunInfoWriter.ReadSourceState(repo);
            Assert.Equal(new string('a', 40), fromGit.Commit);
            Assert.Equal("local", fromGit.Branch);
            Assert.Equal("hash-aus-datei", fromGit.DeployHash);
            Assert.Equal("unbekannt", fromGit.UncommittedChanges);

            File.WriteAllText(Path.Combine(repo, RunInfoWriter.DeployInfoFileName),
                "{\"Commit\":\"bbbb\",\"Branch\":\"server\",\"DeployHash\":\"0123\",\"DeployZeit\":\"jetzt\",\"UncommittedChanges\":\"true\"}");

            RunInfoSource fromDeploy = RunInfoWriter.ReadSourceState(repo);
            Assert.Equal("bbbb", fromDeploy.Commit);
            Assert.Equal("server", fromDeploy.Branch);
            Assert.Equal("0123", fromDeploy.DeployHash);
            Assert.Equal("true", fromDeploy.UncommittedChanges);
        }

        [Fact]
        public void Source_Copy_Takes_Cs_And_Json_But_Not_Bin_And_Obj()
        {
            string project = Sub("projekt");
            void Put(string relative)
            {
                string file = Path.Combine(project, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, relative);
            }

            Put("Geometrie.cs");
            Put("simulation.json");
            Put(Path.Combine("solvers", "su2.json"));
            Put("Notizen.md");
            Put(Path.Combine("bin", "Alt.cs"));
            Put(Path.Combine("obj", "x.json"));

            string results = Sub("ergebnisse");
            string stale = Path.Combine(results, RunInfoWriter.SourceSnapshotDirectoryName, "Weg.cs");
            Directory.CreateDirectory(Path.GetDirectoryName(stale)!);
            File.WriteAllText(stale, "vom letzten Lauf");

            int copied = RunInfoWriter.CopyProjectSources(project, results);

            string snapshot = Path.Combine(results, RunInfoWriter.SourceSnapshotDirectoryName);
            var files = Directory.EnumerateFiles(snapshot, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(snapshot, f).Replace('\\', '/'))
                .OrderBy(f => f, StringComparer.Ordinal)
                .ToList();

            Assert.Equal(3, copied);
            Assert.Equal(new[] { "Geometrie.cs", "simulation.json", "solvers/su2.json" }, files);
        }
    }

    public class ToolVersionProbeTests
    {
        [Fact]
        public void A_Missing_Program_Yields_Unknown_Without_Exception()
        {
            string version = ToolVersionProbe.Query(
                "dieses_programm_gibt_es_nicht_" + Guid.NewGuid().ToString("N"), "--version",
                new Regex(@"(\d+\.\d+)"), TimeSpan.FromSeconds(5));

            Assert.Equal(ToolVersionProbe.Unknown, version);
        }

        [Fact]
        public void An_Empty_Program_Name_Yields_Unknown()
        {
            Assert.Equal(ToolVersionProbe.Unknown,
                ToolVersionProbe.Query("", "", new Regex("(x)"), TimeSpan.FromSeconds(1)));
        }

        [Fact]
        public void Probe_Lists_Every_Program_Even_If_None_Is_Installed()
        {
            var config = new SimulationConfig
            {
                Su2Path = "kein_su2_" + Guid.NewGuid().ToString("N"),
                GmshPath = "kein_gmsh_" + Guid.NewGuid().ToString("N"),
                MpiRunPath = "kein_mpirun_" + Guid.NewGuid().ToString("N")
            };

            var versions = ToolVersionProbe.Probe(config);

            Assert.Equal(ToolVersionProbe.Unknown, versions["SU2"]);
            Assert.Equal(ToolVersionProbe.Unknown, versions["Gmsh"]);
            Assert.Equal(ToolVersionProbe.Unknown, versions["mpirun"]);
            Assert.Matches(@"^\d+\.\d+\.\d+$", versions["PicoGK"]);
        }
    }

    public class GitInfoTests : TempDirectoryTest
    {
        internal static void WriteGit(string repo, string head, params (string Path, string Content)[] files)
        {
            string git = Path.Combine(repo, ".git");
            Directory.CreateDirectory(git);
            File.WriteAllText(Path.Combine(git, "HEAD"), head + "\n");
            foreach (var (relative, content) in files)
            {
                string file = Path.Combine(git, relative.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, content + "\n");
            }
        }

        private static readonly string Sha1 = "0123456789abcdef0123456789abcdef01234567";
        private static readonly string Sha2 = "fedcba9876543210fedcba9876543210fedcba98";

        [Fact]
        public void Reads_A_Loose_Branch_Reference()
        {
            string repo = Sub("lose");
            WriteGit(repo, "ref: refs/heads/MajorPhysikFixes", ("refs/heads/MajorPhysikFixes", Sha1));

            GitInfo? info = GitInfo.Read(repo);

            Assert.NotNull(info);
            Assert.Equal(Sha1, info!.Commit);
            Assert.Equal("MajorPhysikFixes", info.Branch);
        }

        [Fact]
        public void Reads_A_Packed_Reference()
        {
            string repo = Sub("gepackt");
            WriteGit(repo, "ref: refs/heads/feature/x",
                ("packed-refs", $"# pack-refs with: peeled fully-peeled sorted\n{Sha2} refs/heads/main\n{Sha1} refs/heads/feature/x\n^{Sha2}"));

            GitInfo? info = GitInfo.Read(repo);

            Assert.Equal(Sha1, info!.Commit);
            Assert.Equal("feature/x", info.Branch);
        }

        [Fact]
        public void Reads_A_Detached_Head()
        {
            string repo = Sub("abgekoppelt");
            WriteGit(repo, Sha2);

            GitInfo? info = GitInfo.Read(repo);

            Assert.Equal(Sha2, info!.Commit);
            Assert.Null(info.Branch);
        }

        [Fact]
        public void Without_Git_Folder_It_Is_Null()
        {
            Assert.Null(GitInfo.Read(Sub("ohne")));
            Assert.Null(GitInfo.Read(null));
        }
    }

    public class ConfigFingerprintTests : TempDirectoryTest
    {
        private ProjectContext Load(string json)
        {
            string project = Path.Combine(Root, "p_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(project);
            File.WriteAllText(Path.Combine(project, "simulation.json"), json);

            var simulation = JsonConfigLoader.LoadForProject(SimulationConfig.CreateDefault(), "simulation.json", project);
            var context = new ProjectContext("Test", project, null, simulation.Value, new ProjectConfig());
            context.Record("simulation.json", simulation);
            return context;
        }

        [Fact]
        public void The_Seed_Does_Not_Count_But_Every_Other_Value_Does()
        {
            var baseline = ConfigFingerprint.Sections(Load("{ \"MaxIterations\": 3 }"));
            var otherSeed = ConfigFingerprint.Sections(Load("{ \"MaxIterations\": 3, \"RandomSeed\": 99 }"));
            var otherValue = ConfigFingerprint.Sections(Load("{ \"MaxIterations\": 4 }"));

            Assert.StartsWith("sha256:", baseline["simulation.json"]);
            Assert.Empty(ConfigFingerprint.Differences(baseline, otherSeed));
            Assert.Equal(new[] { "simulation.json" }, ConfigFingerprint.Differences(baseline, otherValue));
            Assert.Equal(ConfigFingerprint.Combine(baseline), ConfigFingerprint.Combine(otherSeed));
            Assert.NotEqual(ConfigFingerprint.Combine(baseline), ConfigFingerprint.Combine(otherValue));
        }
    }

    // =========================================================================
    // TODO-51: Journal und Wiedergabe
    // =========================================================================

    /// <summary>
    /// Echte Optimierer und echter Türsteher mit festem Seed, gemockte Geometrie und ein
    /// gemockter Solver, der seine Aufrufe zählt. So lässt sich prüfen, dass ein fortgesetzter
    /// Lauf genau die fehlenden Rechnungen macht und dasselbe Ergebnis liefert.
    /// </summary>
    public class RunJournalReplayTests : TempDirectoryTest
    {
        private const int Seed = 20261106;

        private sealed class TestFitness : IFitnessCalculator
        {
            public void CalculateFitness(ModelRecord record, SimulationConfig config)
            {
                record.Fitness = record.SimulationFailed || !record.PassiveParameters.TryGetValue("Drag", out float drag)
                    ? 0.0001f
                    : 10f / (1f + drag);
            }
        }

        private sealed class Run
        {
            public WorkflowController Controller = null!;
            public SimulationContext Context = null!;
            public int SolverCalls;
        }

        private static IGeometryGenerator Geometry()
        {
            var mock = new Mock<IGeometryGenerator>();
            mock.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns((int i, int v, Dictionary<string, float> p, string d, SimulationConfig c) =>
                {
                    var result = new GeometryResult { StlPath = $"model_{i}_{v}.stl" };
                    result.AddMetric("Volume", p.Values.Sum(), MetricScaling.None);
                    return result;
                });
            return mock.Object;
        }

        private static Run Build(string directory, string algorithm, RunJournal? journal, string verification = "Final")
        {
            var run = new Run();

            var config = new SimulationConfig
            {
                MaxIterations = 3,
                VariantsPerIteration = 4,
                UseRubberBandScaler = false,
                RsmVirtualSimulations = 300,
                BouncerTolerance = 0.002f,     // streng: der Türsteher lehnt auch einmal ab
                BouncerJitter = 3f,
                ChampionVerification = new ChampionVerificationConfig { Mode = verification }
            };
            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { ["Length"] = 100f, ["Width"] = 20f },
                MaxDeviations = new Dictionary<string, float> { ["Length"] = 10f, ["Width"] = 4f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)>
                {
                    ["Length"] = (30f, 200f), ["Width"] = (10f, 50f)
                }
            };
            run.Context = new SimulationContext(config, project, directory);

            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                  .Returns((MeshRequest r) => $"mesh_{r.Iteration}_{r.Variant}.su2");

            var solver = new Mock<ISimulationSolver>();
            solver.SetupGet(s => s.Name).Returns("Zähl-Solver");
            solver.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                  .Callback((string mesh, ModelRecord record, SimulationConfig c, string d) =>
                  {
                      run.SolverCalls++;
                      float l = record.ActiveParameters["Length"], w = record.ActiveParameters["Width"];
                      float factor = record.PassiveParameters["MeshRefinementFactor"];
                      record.PassiveParameters["Drag"] = (0.5f + MathF.Pow((l - 120f) / 50f, 2) + MathF.Pow((w - 25f) / 10f, 2))
                                                         * (1f + 0.01f * (factor - 1f));
                  });

            var fitness = new TestFitness();
            run.Controller = new WorkflowController(
                Geometry(),
                new[] { new SolverStage(mesher.Object, solver.Object) },
                new ChampionValidator(fitness, RunSeed.ForValidator(Seed)),
                OptimizationAlgorithmFactory.Create(algorithm, fitness, RunSeed.ForOptimizer(Seed)),
                run.Context,
                new[] { "Drag" },
                journal);
            return run;
        }

        private static RunJournalHeader Header() => RunResume.CreateHeader(
            "Test", Seed,
            new SortedDictionary<string, string>(StringComparer.Ordinal) { ["simulation.json"] = "sha256:test" },
            new RunInfoSource());

        private static string Csv(string directory) => File.ReadAllText(Path.Combine(directory, "Simulation_Results.csv"));

        /// <summary>Ein vollständiger Lauf mit Journal. Liefert Ordner, Solver-Aufrufe und Journalzeilen.</summary>
        private (string Directory, int SolverCalls, string[] Lines) FullRun(string algorithm)
        {
            string directory = Sub("voll_" + algorithm);
            RunJournal journal = RunJournal.StartNew(directory, Header());
            Run run = Build(directory, algorithm, journal);

            run.Controller.RunOptimization();
            journal.MarkCompleted();

            return (directory, run.SolverCalls, File.ReadAllLines(RunJournal.PathFor(directory)));
        }

        /// <summary>Legt in einem neuen Ordner ein Journal mit Kopf und den ersten k Einträgen an.</summary>
        private string Truncated(string name, string[] lines, int k, bool halfLine)
        {
            string directory = Sub(name);
            string path = RunJournal.PathFor(directory);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            var kept = lines.Take(1 + k).ToList();
            string content = string.Join("\n", kept) + "\n";
            if (halfLine) content += lines[1 + k].Substring(0, lines[1 + k].Length / 2);
            File.WriteAllText(path, content);
            return directory;
        }

        [Fact]
        public void A_Full_Run_Writes_One_Entry_Per_Pipeline_Call_Plus_The_End_Line()
        {
            var (directory, solverCalls, lines) = FullRun("Rsm");

            RunJournalContent content = RunJournal.Read(RunJournal.PathFor(directory))!;

            Assert.True(content.Completed);
            Assert.Equal(solverCalls, content.Entries.Count);
            Assert.True(solverCalls > 12, "Zu den 12 Varianten kommen Türsteher und Feinprüfung.");
            Assert.Equal(solverCalls + 2, lines.Length);   // Kopf + Einträge + Schluss
            Assert.StartsWith("{\"Ende\"", lines[^1]);
            Assert.Contains(content.Entries, e => e.Variant == 99);              // Türsteher
            Assert.Contains(content.Entries, e => e.Tag.Length > 0);             // Feinprüfung
            Assert.Equal(Seed, content.Header.ZufallsSeed);
        }

        /// <summary>Die Kernprüfung (TODO-51): fortgesetzt = ununterbrochen, mit genau N − k Rechnungen.</summary>
        [Theory]
        [InlineData("Rsm", 5, false)]
        [InlineData("Rsm", 13, true)]
        [InlineData("Evolution", 7, false)]
        [InlineData("Evolution", 15, true)]
        [InlineData("Rsm", 0, false)]
        [InlineData("Bayes", 6, false)]
        [InlineData("Bayes", 14, true)]
        [InlineData("Bayes", 0, false)]
        public void A_Resumed_Run_Computes_Only_The_Missing_Calls_And_Ends_Byte_Identical(string algorithm, int k, bool halfLine)
        {
            var (fullDirectory, n, lines) = FullRun(algorithm);
            Assert.True(k < n);

            string directory = Truncated($"fortgesetzt_{algorithm}_{k}", lines, k, halfLine);
            RunJournalContent content = RunJournal.Read(RunJournal.PathFor(directory))!;
            Assert.False(content.Completed);
            Assert.Equal(k, content.Entries.Count);
            Assert.Equal(halfLine ? 1 : 0, content.Warnings.Count);

            RunJournal journal = RunJournal.Resume(directory, content);
            Run resumed = Build(directory, algorithm, journal);
            resumed.Controller.RunOptimization();
            journal.MarkCompleted();

            Assert.Equal(n - k, resumed.SolverCalls);
            Assert.Equal(k, journal.ReplayedCount);
            Assert.Equal(Csv(fullDirectory), Csv(directory));

            // Das fortgesetzte Journal ist am Ende wieder vollständig und gleich lang.
            RunJournalContent after = RunJournal.Read(RunJournal.PathFor(directory))!;
            Assert.True(after.Completed);
            Assert.Equal(n, after.Entries.Count);
            Assert.Empty(after.Warnings);
        }

        [Fact]
        public void A_Half_Last_Line_Is_Ignored_And_Reported()
        {
            var (_, _, lines) = FullRun("Rsm");
            string directory = Truncated("halb", lines, 3, halfLine: true);

            RunJournalContent content = RunJournal.Read(RunJournal.PathFor(directory))!;

            Assert.Equal(3, content.Entries.Count);
            Assert.Contains("unvollständig", Assert.Single(content.Warnings));
        }

        [Fact]
        public void A_Deviation_Ends_The_Replay_Keeps_A_Backup_And_Still_Completes_The_Run()
        {
            var (fullDirectory, n, lines) = FullRun("Rsm");
            const int j = 6;

            // In Eintrag j einen Parameter verstellen (Zeile 0 ist der Kopf).
            var changed = lines.ToArray();
            JsonObject entry = JsonNode.Parse(changed[1 + j])!.AsObject();
            entry["Parameters"]!["Length"] = 123.456f;
            changed[1 + j] = entry.ToJsonString();
            // Alle n Einträge, nur ohne Schlusszeile.
            string directory = Truncated("abweichung", changed, n, halfLine: false);

            RunJournal journal = RunJournal.Resume(directory, RunJournal.Read(RunJournal.PathFor(directory))!);
            Run resumed = Build(directory, "Rsm", journal);
            resumed.Controller.RunOptimization();
            journal.MarkCompleted();

            Assert.Equal(n - j, resumed.SolverCalls);
            Assert.Equal(j, journal.ReplayedCount);
            Assert.Single(Directory.GetFiles(RunJournal.DirectoryFor(directory), "journal_abweichung_*.jsonl"));
            Assert.Equal(Csv(fullDirectory), Csv(directory));

            RunJournalContent after = RunJournal.Read(RunJournal.PathFor(directory))!;
            Assert.True(after.Completed);
            Assert.Equal(n, after.Entries.Count);
        }

        [Fact]
        public void Replayed_Records_Are_Deep_Copies()
        {
            string directory = Sub("kopie");
            RunJournal writer = RunJournal.StartNew(directory, Header());
            var parameters = new Dictionary<string, float> { ["Length"] = 1f };
            var record = new ModelRecord { Iteration = 1, Variant = 1, ActiveParameters = parameters };
            record.PassiveParameters["Drag"] = 2f;

            writer.Append(1, 1, 1f, "", parameters, record);
            writer.Append(1, 1, 1f, "", parameters, record);

            // Der Aufrufer verändert den Datensatz nach der Rückgabe — das Journal nicht.
            record.Fitness = 9f;
            record.PassiveParameters["Drag"] = 99f;

            RunJournal reader = RunJournal.Resume(directory, RunJournal.Read(RunJournal.PathFor(directory))!);

            Assert.True(reader.TryReplay(1, 1, 1f, "", parameters, out ModelRecord? first));
            Assert.Equal(2f, first!.PassiveParameters["Drag"]);
            Assert.Equal(0f, first.Fitness);
            Assert.Same(parameters, first.ActiveParameters);

            first.PassiveParameters["Drag"] = 55f;
            first.SimulationFailed = true;

            Assert.True(reader.TryReplay(1, 1, 1f, "", parameters, out ModelRecord? second));
            Assert.NotSame(first, second);
            Assert.Equal(2f, second!.PassiveParameters["Drag"]);
            Assert.False(second.SimulationFailed);
            Assert.False(reader.IsReplaying);
        }

        [Fact]
        public void Floats_And_NaN_Survive_The_Round_Trip_Exactly()
        {
            string directory = Sub("float");
            RunJournal writer = RunJournal.StartNew(directory, Header());
            var parameters = new Dictionary<string, float> { ["A"] = 0.1f + 0.2f, ["B"] = 1f / 3f };
            var record = new ModelRecord { ActiveParameters = parameters };
            record.PassiveParameters["X"] = MathF.PI * 1e-7f;
            record.PassiveParameters["Leer"] = float.NaN;
            writer.Append(2, 3, 0.7071068f, "Verif", parameters, record);

            RunJournal reader = RunJournal.Resume(directory, RunJournal.Read(RunJournal.PathFor(directory))!);
            Assert.True(reader.TryReplay(2, 3, 0.7071068f, "Verif", new Dictionary<string, float>(parameters), out ModelRecord? copy));

            Assert.Equal(MathF.PI * 1e-7f, copy!.PassiveParameters["X"]);
            Assert.True(float.IsNaN(copy.PassiveParameters["Leer"]));
        }

        [Fact]
        public void Starting_A_New_Journal_Archives_The_Old_One()
        {
            string directory = Sub("archiv");
            RunJournal.StartNew(directory, Header()).Append(1, 1, 1f, "", new(), new ModelRecord());

            RunJournal.StartNew(directory, Header(), out string? archived);

            Assert.NotNull(archived);
            Assert.Matches(@"journal_\d{8}_\d{6}(_\d+)?\.jsonl$", archived!);
            Assert.Equal(2, File.ReadAllLines(archived!).Length);
            Assert.Single(File.ReadAllLines(RunJournal.PathFor(directory)));
        }

        [Fact]
        public void Without_A_Journal_Nothing_Is_Written()
        {
            string directory = Sub("ohne");
            Run run = Build(directory, "Rsm", journal: null, verification: "Off");

            run.Controller.RunOptimization();

            Assert.False(Directory.Exists(RunJournal.DirectoryFor(directory)));
            Assert.False(run.Controller.LastCallWasReplayed);
        }
    }

    // =========================================================================
    // TODO-52: resume
    // =========================================================================

    public class RunResumeTests : TempDirectoryTest
    {
        private static SortedDictionary<string, string> Sections(string simulationHash) =>
            new(StringComparer.Ordinal)
            {
                ["project.json"] = "sha256:p",
                ["simulation.json"] = simulationHash,
                ["solvers/su2.json"] = "sha256:s"
            };

        private string WithJournal(bool completed, int seed = 777, string? deployHash = "d1")
        {
            string directory = Sub("lauf_" + Guid.NewGuid().ToString("N"));
            var header = RunResume.CreateHeader("Test", seed, Sections("sha256:a"), new RunInfoSource { DeployHash = deployHash });
            RunJournal journal = RunJournal.StartNew(directory, header);
            journal.Append(1, 1, 1f, "", new Dictionary<string, float> { ["L"] = 1f }, new ModelRecord());
            journal.Append(1, 2, 1f, "", new Dictionary<string, float> { ["L"] = 2f }, new ModelRecord());
            if (completed) journal.MarkCompleted();
            return directory;
        }

        [Fact]
        public void Without_A_Journal_The_Message_Is_Clear_And_Counts_As_User_Error()
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                RunResume.Prepare(Sub("leer"), "Naca2D", Sections("sha256:a"), 0, new RunInfoSource()));

            Assert.StartsWith("[WIEDERAUFNAHME] Kein abgebrochener Lauf gefunden", error.Message);
            Assert.Contains(".\\scripts\\sim.ps1 run -Project Naca2D", error.Message);
        }

        [Fact]
        public void A_Completed_Journal_Means_Nothing_To_Do()
        {
            ResumePlan plan = RunResume.Prepare(WithJournal(completed: true), "Test", Sections("sha256:a"), 0, new RunInfoSource());

            Assert.True(plan.AlreadyCompleted);
            Assert.Null(plan.Journal);
        }

        [Fact]
        public void A_Changed_Simulation_Json_Aborts_And_Names_The_File()
        {
            string directory = WithJournal(completed: false);

            var error = Assert.Throws<InvalidOperationException>(() =>
                RunResume.Prepare(directory, "Test", Sections("sha256:GEAENDERT"), 0, new RunInfoSource()));

            Assert.StartsWith("[WIEDERAUFNAHME]", error.Message);
            Assert.Contains("simulation.json", error.Message);
            Assert.DoesNotContain("su2.json", error.Message);
        }

        [Fact]
        public void The_Seed_From_The_Journal_Wins_And_A_Code_Change_Only_Warns()
        {
            string directory = WithJournal(completed: false, seed: 777, deployHash: "alt");

            ResumePlan plan = RunResume.Prepare(directory, "Test", Sections("sha256:a"), 5, new RunInfoSource { DeployHash = "neu" });

            Assert.False(plan.AlreadyCompleted);
            Assert.Equal(777, plan.Seed);
            Assert.Equal(2, plan.StoredSimulations);
            Assert.NotNull(plan.Journal);
            Assert.True(plan.Journal!.IsReplaying);
            Assert.Equal(2, plan.Warnings.Count);
            Assert.Contains(plan.Warnings, w => w.Contains("777") && w.Contains("5"));
            Assert.Contains(plan.Warnings, w => w.Contains("Code-Stand"));
        }
    }

    public class ResumeScriptContractTests
    {
        private static string Read(string name)
        {
            string? projectsRoot = ProjectPaths.ResolveProjectsRoot();
            Assert.NotNull(projectsRoot);
            string repo = Path.GetDirectoryName(Path.GetDirectoryName(projectsRoot!)!)!;
            return File.ReadAllText(Path.Combine(repo, "scripts", name));
        }

        [Fact]
        public void Both_Scripts_Know_The_Resume_Command()
        {
            string sim = Read("sim.ps1");
            string runner = Read("sim-runner.sh");

            System.Text.RegularExpressions.Match set = Regex.Match(sim, @"\[ValidateSet\(([^\)]*)\)\]");
            Assert.Contains("'resume'", set.Groups[1].Value);
            Assert.Contains("Invoke-Runner -Arguments 'resume'", sim);

            Assert.Contains("resume)     cmd_resume ;;", runner);
            Assert.Contains("_foreground --resume", runner);
        }

        [Fact]
        public void Status_Reports_An_Aborted_Run()
        {
            string runner = Read("sim-runner.sh");

            Assert.Contains("Wiederaufnahme/journal.jsonl", runner);
            Assert.Contains("Abgebrochener Lauf gefunden", runner);
            Assert.Contains("sim.ps1 resume -Project", runner);
        }

        [Fact]
        public void Fetch_Brings_The_Run_Info_And_The_Source_Copy()
        {
            string runner = Read("sim-runner.sh");

            Assert.Contains("\"run-info.json\"", runner);
            Assert.Contains("\"Quellstand\"", runner);
        }

        [Fact]
        public void Deploy_Writes_The_Deploy_Info_Next_To_The_Hash()
        {
            string sim = Read("sim.ps1");

            Assert.Contains("deploy-info.json", sim);
            Assert.Contains("Write-RemoteDeployInfo -LocalHash $localHash", sim);
        }
    }
}
