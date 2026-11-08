using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using MyPicoGkProject.Core;
using Xunit;
using Xunit.Abstractions;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Gemeinsame Bausteine der Bayes-Tests: ein kompletter Lauf mit dem echten
    /// <see cref="WorkflowController"/>, aber gemockter Geometrie und gemocktem Solver.
    /// Feste Seeds, keine Zeitgrenzen (F22).
    /// </summary>
    internal static class BayesHarness
    {
        public sealed class Problem
        {
            public string[] Names = Array.Empty<string>();
            public (float Min, float Max)[] Bounds = Array.Empty<(float, float)>();
            public float[] Start = Array.Empty<float>();
            public Action<ModelRecord> Solve = _ => { };
            public Func<IFitnessCalculator> Fitness = () => new SingleValueFitness(v => v);
            public int Iterations = 6;
            public int Variants = 6;
            public BayesConfig? Bayes;
            public string Verification = "Off";
        }

        /// <summary>Fitness aus einem einzigen Kennwert "Value", ohne deklarierte Kennwerte.</summary>
        public sealed class SingleValueFitness : IFitnessCalculator
        {
            private readonly Func<double, double> _map;
            public SingleValueFitness(Func<double, double> map) => _map = map;

            public void CalculateFitness(ModelRecord record, SimulationConfig config)
            {
                record.Fitness = record.SimulationFailed || !record.PassiveParameters.TryGetValue("Value", out float v)
                    ? 0.0001f
                    : (float)_map(v);
            }
        }

        public static SimulationContext NewContext(Problem p, string directory)
        {
            var config = new SimulationConfig
            {
                MaxIterations = p.Iterations,
                VariantsPerIteration = p.Variants,
                UseRubberBandScaler = false,
                RsmVirtualSimulations = 2000,
                ChampionVerification = new ChampionVerificationConfig { Mode = p.Verification },
                Bayes = p.Bayes ?? new BayesConfig()
            };

            var project = new ProjectConfig { ProjectName = "BayesTest" };
            for (int i = 0; i < p.Names.Length; i++)
            {
                project.BaseParameters[p.Names[i]] = p.Start[i];
                project.MaxDeviations[p.Names[i]] = (p.Bounds[i].Max - p.Bounds[i].Min) * 0.1f;
                project.ParameterBounds[p.Names[i]] = p.Bounds[i];
            }

            return new SimulationContext(config, project, directory);
        }

        public static (SimulationContext Context, IOptimizationAlgorithm Optimizer) Run(
            Problem p, string algorithm, int seed, string directory, Func<IFitnessCalculator, Random, IOptimizationAlgorithm>? factory = null)
        {
            SimulationContext context = NewContext(p, directory);
            Directory.CreateDirectory(directory);

            var geometry = new Mock<IGeometryGenerator>();
            geometry.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns((int i, int v, Dictionary<string, float> prm, string d, SimulationConfig c) =>
                    new GeometryResult { StlPath = $"model_{i}_{v}.stl" });

            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                  .Returns((MeshRequest r) => $"mesh_{r.Iteration}_{r.Variant}.su2");

            var solver = new Mock<ISimulationSolver>();
            solver.SetupGet(s => s.Name).Returns("Test-Solver");
            solver.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                  .Callback((string mesh, ModelRecord record, SimulationConfig c, string d) => p.Solve(record));

            IFitnessCalculator fitness = p.Fitness();
            IOptimizationAlgorithm optimizer = factory != null
                ? factory(fitness, RunSeed.ForOptimizer(seed))
                : OptimizationAlgorithmFactory.Create(algorithm, fitness, RunSeed.ForOptimizer(seed));

            (optimizer as IOptimizationPreflight)?.Preflight(context);

            var controller = new WorkflowController(
                geometry.Object,
                new[] { new SolverStage(mesher.Object, solver.Object) },
                new ChampionValidator(fitness, RunSeed.ForValidator(seed), context.Project.ParameterBounds),
                optimizer,
                context,
                fitness.RequiredMetrics);

            controller.RunOptimization();
            return (context, optimizer);
        }

        public static float Best(SimulationContext context) =>
            context.History.Where(r => !r.SimulationFailed && !r.Disqualified).Max(r => r.Fitness);

        // ---------------------------------------------------------------- Testfunktionen

        public static double Branin(double x1, double x2)
        {
            const double a = 1, b = 5.1 / (4 * Math.PI * Math.PI), c = 5 / Math.PI, r = 6, s = 10, t = 1 / (8 * Math.PI);
            return a * Math.Pow(x2 - b * x1 * x1 + c * x1 - r, 2) + s * (1 - t) * Math.Cos(x1) + s;
        }

        private static readonly double[] HartmannA = { 10, 3, 17, 3.5, 1.7, 8, 0.05, 10, 17, 0.1, 8, 14, 3, 3.5, 1.7, 10, 17, 8, 17, 8, 0.05, 10, 0.1, 14 };
        private static readonly double[] HartmannP =
        {
            1312, 1696, 5569, 124, 8283, 5886, 2329, 4135, 8307, 3736, 1004, 9991,
            2348, 1451, 3522, 2883, 3047, 6650, 4047, 8828, 8732, 5743, 1091, 381
        };
        private static readonly double[] HartmannAlpha = { 1.0, 1.2, 3.0, 3.2 };

        public static double Hartmann6(double[] x)
        {
            double sum = 0;
            for (int i = 0; i < 4; i++)
            {
                double inner = 0;
                for (int j = 0; j < 6; j++)
                {
                    double diff = x[j] - HartmannP[i * 6 + j] * 1e-4;
                    inner += HartmannA[i * 6 + j] * diff * diff;
                }
                sum += HartmannAlpha[i] * Math.Exp(-inner);
            }
            return -sum;   // Minimum -3.32237
        }

        public static Problem BraninProblem() => new Problem
        {
            Names = new[] { "X1", "X2" },
            Bounds = new[] { (-5f, 10f), (0f, 15f) },
            Start = new[] { 2.5f, 7.5f },
            Solve = r => r.PassiveParameters["Value"] = (float)Branin(r.ActiveParameters["X1"], r.ActiveParameters["X2"]),
            // Fitness = Gütezahl, Maximum 1/(1+0.3979) bei dem bekannten Minimum
            Fitness = () => new SingleValueFitness(v => 1.0 / (1.0 + v))
        };

        public static Problem HartmannProblem() => new Problem
        {
            Names = Enumerable.Range(1, 6).Select(i => "X" + i).ToArray(),
            Bounds = Enumerable.Repeat((0f, 1f), 6).ToArray(),
            Start = Enumerable.Repeat(0.5f, 6).ToArray(),
            Solve = r => r.PassiveParameters["Value"] = (float)Hartmann6(Enumerable.Range(1, 6).Select(i => (double)r.ActiveParameters["X" + i]).ToArray()),
            Fitness = () => new SingleValueFitness(v => 0.001 - v)   // Maximum 3.323
        };
    }

    public class BayesFactoryAndSetupTests : TempDirectoryTest
    {
        [Theory]
        [InlineData("Bayes")]
        [InlineData("bayes")]
        [InlineData("  BAYES ")]
        public void The_Factory_Recognises_Bayes(string name)
        {
            var optimizer = OptimizationAlgorithmFactory.Create(name, new BayesHarness.SingleValueFitness(v => v));
            Assert.IsType<BayesOptimizationAlgorithm>(optimizer);
        }

        [Fact]
        public void Supported_Algorithms_List_Bayes_And_Rsm_Stays_The_Default()
        {
            Assert.Contains("Bayes", OptimizationAlgorithmFactory.SupportedAlgorithms);
            Assert.Equal("Rsm", OptimizationAlgorithmFactory.DefaultAlgorithm);
            Assert.IsType<RsmOptimizationAlgorithm>(OptimizationAlgorithmFactory.Create("", new BayesHarness.SingleValueFitness(v => v)));
            Assert.Equal("Rsm", new SimulationConfig().OptimizationAlgorithm);
        }

        [Fact]
        public void Iteration_One_Variant_One_Is_The_Start_Value_And_All_Proposals_Stay_In_Bounds()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Variants = 6;
            var context = BayesHarness.NewContext(problem, Sub("grenzen"));
            var optimizer = new BayesOptimizationAlgorithm(new BayesHarness.SingleValueFitness(v => v), random: new Random(1));

            var first = optimizer.GenerateParameters(context, 1, 1, 6);
            Assert.Equal(2.5f, first["X1"]);
            Assert.Equal(7.5f, first["X2"]);

            for (int variant = 2; variant <= 6; variant++)
            {
                var p = optimizer.GenerateParameters(context, 1, variant, 6);
                Assert.InRange(p["X1"], -5f, 10f);
                Assert.InRange(p["X2"], 0f, 15f);
            }
        }

        [Fact]
        public void The_Initial_Design_Is_Latin_Hypercube_Stratified()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Variants = 6;   // n0 = max(6, 2*2+2) = 6 → 5 LHS-Punkte nach dem Startwert
            var context = BayesHarness.NewContext(problem, Sub("lhs"));
            var optimizer = new BayesOptimizationAlgorithm(new BayesHarness.SingleValueFitness(v => v), random: new Random(5));

            optimizer.GenerateParameters(context, 1, 1, 6);
            var points = Enumerable.Range(2, 5).Select(v => optimizer.GenerateParameters(context, 1, v, 6)).ToList();

            Assert.Equal(6, optimizer.InitialSampleCount);
            foreach (var (name, min, max) in new[] { ("X1", -5f, 10f), ("X2", 0f, 15f) })
            {
                var strata = points.Select(p => (int)Math.Floor((p[name] - min) / (max - min) * 5)).OrderBy(i => i).ToList();
                Assert.Equal(Enumerable.Range(0, 5), strata);
            }
        }

        [Fact]
        public void A_Missing_Bound_Stops_Before_The_First_Calculation_And_Names_The_Parameter()
        {
            var problem = BayesHarness.BraninProblem();
            var context = BayesHarness.NewContext(problem, Sub("ohne"));
            context.Project.ParameterBounds.Remove("X2");
            var optimizer = new BayesOptimizationAlgorithm(new BayesHarness.SingleValueFitness(v => v), random: new Random(1));

            var ex = Assert.Throws<InvalidOperationException>(() => optimizer.Preflight(context));
            Assert.Contains("X2", ex.Message);
            Assert.Contains("ParameterBounds", ex.Message);
            Assert.Contains("project.json", ex.Message);

            // Auch ohne Vorprüfung: schon der erste Vorschlag bricht ab.
            var lazy = new BayesOptimizationAlgorithm(new BayesHarness.SingleValueFitness(v => v), random: new Random(1));
            Assert.Throws<InvalidOperationException>(() => lazy.GenerateParameters(context, 1, 1, 6));
        }

        [Fact]
        public void A_Fixed_Parameter_Stays_Fixed_And_Is_Not_Modelled()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Bounds = new[] { (-5f, 10f), (7.5f, 7.5f) };   // X2 fest
            problem.Iterations = 3;
            problem.Variants = 4;
            var (context, _) = BayesHarness.Run(problem, "Bayes", 3, Sub("fest"));

            Assert.All(context.History, r => Assert.Equal(7.5f, r.ActiveParameters["X2"]));
            Assert.True(context.History.Select(r => r.ActiveParameters["X1"]).Distinct().Count() > 6);
        }

        [Fact]
        public void An_Unknown_Setting_Is_Rejected_With_A_Clear_Message()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Bayes = new BayesConfig { SurrogateTarget = "Magie" };
            var context = BayesHarness.NewContext(problem, Sub("magie"));
            var optimizer = new BayesOptimizationAlgorithm(new BayesHarness.SingleValueFitness(v => v));

            var ex = Assert.Throws<InvalidOperationException>(() => optimizer.Preflight(context));
            Assert.Contains("SurrogateTarget", ex.Message);
        }
    }

    public class BayesTrustRegionTests
    {
        [Theory]
        [InlineData("Auto", 9, false)]
        [InlineData("Auto", 10, true)]
        [InlineData("Auto", 20, true)]
        [InlineData("On", 3, true)]
        [InlineData("Off", 20, false)]
        [InlineData(null, 12, true)]
        public void The_Auto_Threshold_Is_Ten_Free_Parameters(string? setting, int d, bool expected)
        {
            Assert.Equal(expected, TrustRegionState.IsActive(setting, d));
        }

        [Fact]
        public void The_Edge_Length_Halves_After_The_Defined_Number_Of_Failures()
        {
            var region = new TrustRegionState(freeDimensions: 12, variantsPerIteration: 5);   // ⌈max(4/5, 12/5)⌉ = 3
            Assert.Equal(3, region.FailureTolerance);

            region.Register(false);
            region.Register(false);
            Assert.Equal(0.8, region.Length);
            region.Register(false);
            Assert.Equal(0.4, region.Length);
        }

        [Fact]
        public void The_Edge_Length_Doubles_After_Three_Successes_Up_To_The_Maximum()
        {
            var region = new TrustRegionState(10, 10);
            region.Register(true); region.Register(true);
            Assert.Equal(0.8, region.Length);
            region.Register(true);
            Assert.Equal(1.6, region.Length);

            region.Register(true); region.Register(true); region.Register(true);
            Assert.Equal(1.6, region.Length);   // Deckel
        }

        [Fact]
        public void A_Success_Resets_The_Failure_Count()
        {
            var region = new TrustRegionState(12, 5);
            region.Register(false); region.Register(false);
            region.Register(true);
            region.Register(false); region.Register(false);
            Assert.Equal(0.8, region.Length);
        }

        [Fact]
        public void Below_The_Minimum_A_Restart_Is_Due_And_Resets_The_Length()
        {
            var region = new TrustRegionState(4, 4);   // Toleranz 1
            for (int i = 0; i < 7; i++)
            {
                Assert.False(region.NeedsRestart);
                region.Register(false);
            }

            Assert.True(region.NeedsRestart);
            region.Restart();
            Assert.False(region.NeedsRestart);
            Assert.Equal(TrustRegionState.InitialLength, region.Length);
        }
    }

    public class BayesRunTests : TempDirectoryTest
    {
        private readonly ITestOutputHelper _output;
        public BayesRunTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void A_Full_Run_With_Mocks_Works_And_Writes_Predictions_And_The_History_File()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Iterations = 4;
            problem.Variants = 5;
            string directory = Sub("voll");

            var (context, optimizer) = BayesHarness.Run(problem, "Bayes", 7, directory);

            Assert.Equal(20, context.History.Count);
            Assert.Equal(SurrogateTargetMode.Fitness, ((BayesOptimizationAlgorithm)optimizer).ActiveMode);

            // Versuchsplan (Iteration 1): keine Vorhersage. Später: Vorhersage samt Band.
            Assert.All(context.History.Where(r => r.Iteration == 1),
                r => Assert.False(r.PassiveParameters.ContainsKey(BayesOptimizationAlgorithm.PredictedFitnessKey)));
            var predicted = context.History.Where(r => r.PassiveParameters.ContainsKey(BayesOptimizationAlgorithm.PredictedFitnessKey)).ToList();
            Assert.True(predicted.Count >= 5, $"Vorhersagen: {predicted.Count}");
            foreach (var r in predicted)
            {
                Assert.True(r.PassiveParameters[BayesOptimizationAlgorithm.PredictedLowKey]
                            <= r.PassiveParameters[BayesOptimizationAlgorithm.PredictedFitnessKey] + 1e-6f);
                Assert.True(r.PassiveParameters[BayesOptimizationAlgorithm.PredictedHighKey]
                            >= r.PassiveParameters[BayesOptimizationAlgorithm.PredictedFitnessKey] - 1e-6f);
            }

            // Sie landen als Spalten in der CSV.
            string csv = File.ReadAllText(Path.Combine(directory, "Simulation_Results.csv"));
            Assert.Contains("Bayes_PredictedFitness", csv.Split('\n')[0]);
            Assert.Contains("Bayes_Explorer", csv.Split('\n')[0]);

            // Verlaufsdatei: Kopfzeile, eine Zeile je vorbereiteter Iteration (3 von 4).
            string[] lines = File.ReadAllLines(Path.Combine(directory, "Analyseergebnisse", BayesOptimizationAlgorithm.HistoryFileName));
            Assert.StartsWith("Iteration;Modus;Lernpunkte;Vertrauensbereich_L;Abweichung_Mittel;Anteil_im_Band;BesteFitness;Relevanz_X1;Relevanz_X2", lines[0]);
            Assert.Equal(1 + 3, lines.Length);
            Assert.Contains("Fitness", lines[^1].Split(';')[1]);
        }

        [Fact]
        public void Rsm_Writes_No_Bayes_History_File()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Iterations = 3;
            problem.Variants = 5;
            string directory = Sub("rsm");

            BayesHarness.Run(problem, "Rsm", 7, directory);

            Assert.False(File.Exists(Path.Combine(directory, "Analyseergebnisse", BayesOptimizationAlgorithm.HistoryFileName)));
            Assert.DoesNotContain("Bayes_", File.ReadAllText(Path.Combine(directory, "Simulation_Results.csv")));
        }

        [Fact]
        public void The_Run_Is_Reproducible_For_A_Fixed_Seed()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Iterations = 4;
            problem.Variants = 5;

            var (a, _) = BayesHarness.Run(problem, "Bayes", 11, Sub("a"));
            var (b, _) = BayesHarness.Run(problem, "Bayes", 11, Sub("b"));

            Assert.Equal(
                a.History.SelectMany(r => r.ActiveParameters.Values),
                b.History.SelectMany(r => r.ActiveParameters.Values));
        }

        [Fact]
        public void EvaluateAndSelectBest_Is_Stateless_Extra_Calls_Do_Not_Change_The_Next_Proposals()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Iterations = 2;
            problem.Variants = 5;

            (SimulationContext, IOptimizationAlgorithm) Plain() => BayesHarness.Run(problem, "Bayes", 4, Sub("plain"));
            var (plainContext, _) = Plain();

            // Derselbe Lauf, nur dass jede Bewertung mehrfach aufgerufen wird.
            IOptimizationAlgorithm Wrap(IFitnessCalculator f, Random r) => new ExtraEvaluationWrapper(new BayesOptimizationAlgorithm(f, random: r));
            var (extraContext, _) = BayesHarness.Run(problem, "Bayes", 4, Sub("extra"), Wrap);

            Assert.Equal(
                plainContext.History.SelectMany(r => r.ActiveParameters.Values),
                extraContext.History.SelectMany(r => r.ActiveParameters.Values));
        }

        private sealed class ExtraEvaluationWrapper : IOptimizationAlgorithm
        {
            private readonly IOptimizationAlgorithm _inner;
            public ExtraEvaluationWrapper(IOptimizationAlgorithm inner) => _inner = inner;
            public string Name => _inner.Name;
            public Dictionary<string, float> GenerateParameters(SimulationContext c, int i, int v, int m) => _inner.GenerateParameters(c, i, v, m);
            public ModelRecord EvaluateAndSelectBest(List<ModelRecord> records, SimulationContext c)
            {
                _inner.EvaluateAndSelectBest(records, c);
                _inner.EvaluateAndSelectBest(records, c);
                return _inner.EvaluateAndSelectBest(records, c);
            }
            public void PrepareNextIteration(SimulationContext c, List<ModelRecord> r, ModelRecord b) => _inner.PrepareNextIteration(c, r, b);
        }

        [Fact]
        public void A_Batch_Has_Distinct_Points_Without_Duplicates_Of_The_History()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Iterations = 5;
            problem.Variants = 6;
            var (context, _) = BayesHarness.Run(problem, "Bayes", 9, Sub("dubletten"));

            var all = context.History.Select(r => (r.ActiveParameters["X1"], r.ActiveParameters["X2"])).ToList();
            Assert.Equal(all.Count, all.Distinct().Count());

            foreach (var iteration in context.History.GroupBy(r => r.Iteration))
            {
                var points = iteration.Select(r => (r.ActiveParameters["X1"], r.ActiveParameters["X2"])).ToList();
                Assert.Equal(points.Count, points.Distinct().Count());
            }
        }

        [Fact]
        public void The_Proposals_Of_One_Round_Are_Spread_Out_Not_Near_Duplicates()
        {
            // Gemessen am 08.11.2026 auf dem Server: vier Vorschläge einer Runde lagen fast aufeinander.
            var problem = BayesHarness.BraninProblem();
            problem.Iterations = 5;
            problem.Variants = 6;
            var (context, _) = BayesHarness.Run(problem, "Bayes", 9, Sub("streuung"));

            double minimum = double.PositiveInfinity;
            foreach (var round in context.History.Where(r => r.Iteration >= 3).GroupBy(r => r.Iteration))
            {
                var points = round.Select(r => new[] { (r.ActiveParameters["X1"] + 5.0) / 15.0, r.ActiveParameters["X2"] / 15.0 }).ToList();
                for (int i = 0; i < points.Count; i++)
                    for (int j = i + 1; j < points.Count; j++)
                        minimum = Math.Min(minimum, Math.Sqrt(Math.Pow(points[i][0] - points[j][0], 2) + Math.Pow(points[i][1] - points[j][1], 2)));
            }

            Assert.True(minimum > 0.01, $"kleinster Abstand zweier Vorschläge einer Runde: {minimum:F4}");
        }

        [Fact]
        public void Failed_And_Disqualified_Records_Are_Not_Learned_And_Their_Surroundings_Are_Avoided()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Variants = 6;
            var context = BayesHarness.NewContext(problem, Sub("abstossung"));
            var spy = new SpyModel();
            var optimizer = new BayesOptimizationAlgorithm(
                new BayesHarness.SingleValueFitness(v => 1.0 / (1.0 + v)),
                random: new Random(2),
                modelFactory: () => spy);
            optimizer.Preflight(context);

            // Sechs gute Rechnungen verteilt im Raum, dazu ein fehlgeschlagener und ein disqualifizierter Punkt.
            var random = new Random(3);
            for (int i = 0; i < 8; i++)
            {
                var record = new ModelRecord { Iteration = 1, Variant = i + 1 };
                record.ActiveParameters["X1"] = -5f + 15f * (float)random.NextDouble();
                record.ActiveParameters["X2"] = 15f * (float)random.NextDouble();
                record.PassiveParameters["Value"] = (float)BayesHarness.Branin(record.ActiveParameters["X1"], record.ActiveParameters["X2"]);
                record.Fitness = 1f / (1f + record.PassiveParameters["Value"]);
                if (i == 6) { record.SimulationFailed = true; record.ActiveParameters["X1"] = 4.0f; record.ActiveParameters["X2"] = 4.0f; }
                if (i == 7) { record.Disqualified = true; record.Fitness = 0.0001f; record.ActiveParameters["X1"] = 8.0f; record.ActiveParameters["X2"] = 11.0f; }
                context.History.Add(record);
            }

            optimizer.PrepareNextIteration(context, context.History.ToList(), context.History[0]);

            Assert.Equal(6, spy.LastFitCount);   // nur die sechs nutzbaren

            double radius = 0.05 * Math.Sqrt(2);
            var excluded = new[] { (4.0, 4.0), (8.0, 11.0) };
            for (int variant = 1; variant <= 6; variant++)
            {
                var p = optimizer.GenerateParameters(context, 2, variant, 6);
                foreach (var (ex1, ex2) in excluded)
                {
                    double dx = (p["X1"] - ex1) / 15.0, dy = (p["X2"] - ex2) / 15.0;
                    Assert.True(Math.Sqrt(dx * dx + dy * dy) > radius / 2, $"Vorschlag {variant} liegt zu nah an ({ex1}, {ex2})");
                }
            }
        }

        /// <summary>Wie ein Gauß-Prozess, zählt aber mit, mit wie vielen Punkten er lernen musste.</summary>
        private sealed class SpyModel : ISurrogateModel
        {
            private readonly GaussianProcess _inner = new(new Random(1));
            public int LastFitCount { get; private set; }
            public List<double> LastOutputs { get; } = new();
            public void Fit(IReadOnlyList<double[]> inputs, IReadOnlyList<double> outputs)
            {
                LastFitCount = inputs.Count;
                LastOutputs.Clear();
                LastOutputs.AddRange(outputs);
                _inner.Fit(inputs, outputs);
            }
            public (double Mean, double Variance) Predict(double[] input) => _inner.Predict(input);
            public void AddPseudoObservation(double[] input, double output) => _inner.AddPseudoObservation(input, output);
            public IReadOnlyList<double>? ParameterRelevance => _inner.ParameterRelevance;
        }

        // ---------------------------------------------------------------- Vergleich mit Rsm (Testfunktionen)

        private (double Bayes, double Rsm) Compare(BayesHarness.Problem problem, int seed)
        {
            var (b, _) = BayesHarness.Run(problem, "Bayes", seed, Sub($"b_{problem.Names.Length}_{seed}"));
            var (r, _) = BayesHarness.Run(problem, "Rsm", seed, Sub($"r_{problem.Names.Length}_{seed}"));
            return (BayesHarness.Best(b), BayesHarness.Best(r));
        }

        /// <summary>
        /// Branin (2D), 6 × 6 Rechnungen, 3 feste Seeds, mit echtem Türsteher. Gemessen am 08.11.2026:
        /// Bayes 0,705 / 0,709 / 0,684, Rsm 0,608 / 0,080 / 0,702, Optimum 0,715. Je Seed schwankt das
        /// Ergebnis (kleines Budget), deshalb zählt die Summe über die Seeds.
        /// </summary>
        [Fact]
        public void Bayes_Is_Close_To_The_Optimum_And_Better_Than_Rsm_On_Branin()
        {
            double optimum = 1.0 / (1.0 + 0.397887);
            double bayesSum = 0, rsmSum = 0;

            foreach (int seed in new[] { 1, 2, 3 })
            {
                var (bayes, rsm) = Compare(BayesHarness.BraninProblem(), seed);
                _output.WriteLine($"Branin Seed {seed}: Bayes {bayes:F4}, Rsm {rsm:F4}, Optimum {optimum:F4}");
                Assert.True(bayes >= optimum * 0.8, $"Seed {seed}: Bayes {bayes} ist nicht nah am Optimum {optimum}");
                bayesSum += bayes;
                rsmSum += rsm;
            }

            Assert.True(bayesSum >= rsmSum, $"Summe Bayes {bayesSum} < Summe Rsm {rsmSum}");
        }

        /// <summary>
        /// Hartmann-6 (6D) mit nur 36 Rechnungen ist für jedes Verfahren schwer und stark vom Zufall
        /// abhängig (je Seed schwankt das Ergebnis um mehr als 1,0). Gemessen am 08.11.2026:
        /// Bayes 2,58 / 1,95 / 1,38, Rsm 2,93 / 1,33 / 1,48, Optimum 3,32. Deshalb zählt nur der Mittelwert.
        /// </summary>
        [Fact]
        public void Bayes_Is_Better_Than_Rsm_On_Hartmann6_On_Average()
        {
            double bayesSum = 0, rsmSum = 0;

            foreach (int seed in new[] { 1, 2, 3 })
            {
                var (bayes, rsm) = Compare(BayesHarness.HartmannProblem(), seed);
                _output.WriteLine($"Hartmann6 Seed {seed}: Bayes {bayes:F4}, Rsm {rsm:F4}, Optimum 3.3224");
                Assert.True(bayes >= 1.0, $"Seed {seed}: Bayes {bayes} ist zu weit vom Optimum 3.32");
                bayesSum += bayes;
                rsmSum += rsm;
            }

            Assert.True(bayesSum >= rsmSum, $"Summe Bayes {bayesSum} < Summe Rsm {rsmSum}");
            Assert.True(bayesSum / 3 >= 1.8, $"Mittel {bayesSum / 3}");
        }

        [Fact]
        public void With_Twenty_Parameters_The_Trust_Region_Beats_The_Best_Design_Point_Clearly()
        {
            const int d = 20;
            double Objective(double[] x)
            {
                double sum = 0;
                for (int i = 0; i < x.Length; i++)
                {
                    double a = x[i] - 0.3;
                    sum += a * a;
                    if (i + 1 < x.Length) sum += 0.1 * a * (x[i + 1] - 0.3);
                }
                return sum;
            }

            var problem = new BayesHarness.Problem
            {
                Names = Enumerable.Range(1, d).Select(i => "P" + i).ToArray(),
                Bounds = Enumerable.Repeat((0f, 1f), d).ToArray(),
                Start = Enumerable.Repeat(0.8f, d).ToArray(),
                Solve = r => r.PassiveParameters["Value"] = (float)Objective(Enumerable.Range(1, d).Select(i => (double)r.ActiveParameters["P" + i]).ToArray()),
                Fitness = () => new BayesHarness.SingleValueFitness(v => 10.0 - v),
                Iterations = 8,
                Variants = 10
            };

            var (context, optimizer) = BayesHarness.Run(problem, "Bayes", 5, Sub("zwanzig"));
            var bayes = (BayesOptimizationAlgorithm)optimizer;
            Assert.True(bayes.TrustRegionActive);

            int designPoints = bayes.InitialSampleCount;   // 42
            double bestDesign = context.History.Take(designPoints).Max(r => r.Fitness);
            double best = BayesHarness.Best(context);
            _output.WriteLine($"20D: bester Versuchsplan-Punkt {bestDesign:F3}, Ende {best:F3}");

            // Fitness = 10 - f; ein klarer Gewinn heißt: der Abstand zum Optimum (10) sinkt auf unter 60 %.
            Assert.True(10.0 - best < 0.6 * (10.0 - bestDesign), $"f(Entwurf) = {10 - bestDesign:F3}, f(Ende) = {10 - best:F3}");
        }

        [Fact]
        public void The_Training_Set_Is_Capped_At_MaxTrainingPoints()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Iterations = 6;
            problem.Variants = 6;
            problem.Bayes = new BayesConfig { MaxTrainingPoints = 12, CandidatePool = 500 };
            var spy = new SpyModel();

            var (_, _) = BayesHarness.Run(problem, "Bayes", 2, Sub("deckel"),
                (f, r) => new BayesOptimizationAlgorithm(f, random: r, modelFactory: () => spy));

            Assert.Equal(12, spy.LastFitCount);   // 30 gerechnet, aber höchstens 12 gesehen
        }
    }

    public class BayesMetricsModeTests : TempDirectoryTest
    {
        /// <summary>
        /// Manta-ähnlich: Fitness = A·B / C³, mal 0,1 außerhalb eines A-Bereichs. A, B und C verlaufen
        /// glatt, die Fitness springt. Die Fitness deklariert ihre Kennwerte.
        /// </summary>
        private sealed class MantaLikeFitness : IFitnessCalculator
        {
            private readonly bool _readsUndeclared;
            public MantaLikeFitness(bool readsUndeclared = false) => _readsUndeclared = readsUndeclared;

            public IReadOnlyCollection<string> RequiredMetrics => new[] { "A", "B", "C" };

            public void CalculateFitness(ModelRecord record, SimulationConfig config)
            {
                if (record.SimulationFailed ||
                    !record.PassiveParameters.TryGetValue("A", out float a) ||
                    !record.PassiveParameters.TryGetValue("B", out float b) ||
                    !record.PassiveParameters.TryGetValue("C", out float c))
                {
                    record.Fitness = 0.0001f;
                    return;
                }

                float fitness = a * b / MathF.Pow(MathF.Max(c, 0.001f), 3);
                if (a < 2.0f || a > 3.0f) fitness *= 0.1f;

                if (_readsUndeclared && record.PassiveParameters.TryGetValue("Geheim", out float secret)) fitness += secret;

                record.PassiveParameters["Verhaeltnis"] = a / MathF.Max(c, 0.001f);   // schreibt zusätzliche Schlüssel (wie GlideRatio)
                record.Fitness = fitness;
            }
        }

        private static BayesHarness.Problem MantaLike(Func<IFitnessCalculator>? fitness = null)
        {
            const int d = 5;
            return new BayesHarness.Problem
            {
                Names = Enumerable.Range(1, d).Select(i => "P" + i).ToArray(),
                Bounds = Enumerable.Repeat((0f, 1f), d).ToArray(),
                Start = Enumerable.Repeat(0.5f, d).ToArray(),
                Solve = r =>
                {
                    float p1 = r.ActiveParameters["P1"], p2 = r.ActiveParameters["P2"], p3 = r.ActiveParameters["P3"],
                          p4 = r.ActiveParameters["P4"], p5 = r.ActiveParameters["P5"];
                    r.PassiveParameters["A"] = 1.5f + 1.5f * p1 + 0.5f * p2 * p2;
                    r.PassiveParameters["B"] = 1.0f + p2 + 0.3f * p4;
                    r.PassiveParameters["C"] = 0.5f + (p3 - 0.7f) * (p3 - 0.7f) + 0.5f * (p5 - 0.3f) * (p5 - 0.3f) + 0.2f * p1;
                },
                Fitness = fitness ?? (() => new MantaLikeFitness()),
                Iterations = 6,
                Variants = 6
            };
        }

        [Fact]
        public void Auto_Picks_Metrics_When_The_Fitness_Declares_Metrics_And_Fitness_Otherwise()
        {
            var withMetrics = BayesHarness.NewContext(MantaLike(), Sub("auto1"));
            var a = new BayesOptimizationAlgorithm(new MantaLikeFitness(), random: new Random(1));
            a.Preflight(withMetrics);
            Assert.Equal(SurrogateTargetMode.Metrics, a.ActiveMode);

            var without = BayesHarness.NewContext(BayesHarness.BraninProblem(), Sub("auto2"));
            var b = new BayesOptimizationAlgorithm(new BayesHarness.SingleValueFitness(v => v), random: new Random(1));
            b.Preflight(without);
            Assert.Equal(SurrogateTargetMode.Fitness, b.ActiveMode);
        }

        [Fact]
        public void Metrics_Without_Declared_Metrics_Stops_Before_The_First_Calculation()
        {
            var problem = BayesHarness.BraninProblem();
            problem.Bayes = new BayesConfig { SurrogateTarget = "Metrics" };
            var context = BayesHarness.NewContext(problem, Sub("leer"));
            var optimizer = new BayesOptimizationAlgorithm(new BayesHarness.SingleValueFitness(v => v));

            var ex = Assert.Throws<InvalidOperationException>(() => optimizer.Preflight(context));
            Assert.Contains("RequiredMetrics", ex.Message);
        }

        [Fact]
        public void A_Fitness_That_Reads_Undeclared_Metrics_Falls_Back_To_Fitness_With_A_Warning()
        {
            var problem = MantaLike(() => new MantaLikeFitness(readsUndeclared: true));
            var baseSolve = problem.Solve;
            problem.Solve = r =>
            {
                baseSolve(r);
                r.PassiveParameters["Geheim"] = 1.0f + r.ActiveParameters["P1"];   // nicht deklariert, verändert die Fitness
            };

            var (_, optimizer) = BayesHarness.Run(problem, "Bayes", 3, Sub("rueckfall"));

            Assert.Equal(SurrogateTargetMode.Fitness, ((BayesOptimizationAlgorithm)optimizer).ActiveMode);
        }

        [Fact]
        public void A_Fitness_That_Needs_Only_Declared_Metrics_Stays_In_Metrics_Mode()
        {
            var (context, optimizer) = BayesHarness.Run(MantaLike(), "Bayes", 3, Sub("bleibt"));
            Assert.Equal(SurrogateTargetMode.Metrics, ((BayesOptimizationAlgorithm)optimizer).ActiveMode);

            // Throwaway-Datensätze kommen nie in die History: genau 36 Einträge.
            Assert.Equal(36, context.History.Count);

            // Auch im Metrics-Modus stehen die Vorhersagespalten in der CSV.
            Assert.Contains(context.History, r => r.PassiveParameters.ContainsKey(BayesOptimizationAlgorithm.PredictedFitnessKey));
        }

        [Fact]
        public void Metrics_Mode_Clearly_Beats_Fitness_Mode_And_Rsm_On_A_Manta_Like_Problem()
        {
            double metrics = 0, fitness = 0, rsm = 0;
            foreach (int seed in new[] { 1, 2, 3 })
            {
                var m = MantaLike();
                var (cm, _) = BayesHarness.Run(m, "Bayes", seed, Sub($"m{seed}"));
                metrics += BayesHarness.Best(cm);

                var f = MantaLike();
                f.Bayes = new BayesConfig { SurrogateTarget = "Fitness" };
                var (cf, _) = BayesHarness.Run(f, "Bayes", seed, Sub($"f{seed}"));
                fitness += BayesHarness.Best(cf);

                var (cr, _) = BayesHarness.Run(MantaLike(), "Rsm", seed, Sub($"r{seed}"));
                rsm += BayesHarness.Best(cr);
            }

            // Gemessen am 08.11.2026 (Summe über 3 Seeds): Metrics 110,4, Fitness 78,6, Rsm 82,2.
            // Der Modus "Fitness" hat an der Sprungstelle der Formel keinen Vorteil gegenüber Rsm,
            // der Modus "Metrics" schon. Genau dafür gibt es ihn.
            Assert.True(metrics >= fitness * 1.1, $"Metrics {metrics} nicht klar besser als Fitness {fitness}");
            Assert.True(metrics >= rsm * 1.1, $"Metrics {metrics} nicht klar besser als Rsm {rsm}");
            Assert.True(fitness >= rsm * 0.85, $"Fitness {fitness} deutlich schlechter als Rsm {rsm}");
        }

        [Fact]
        public void A_Positive_Metric_Is_Learned_On_The_Log_Scale_And_Mixed_Signs_Are_Not()
        {
            var problem = MantaLike();
            problem.Iterations = 2;
            problem.Variants = 8;
            var models = new List<RecordingModel>();

            BayesHarness.Run(problem, "Bayes", 3, Sub("log"),
                (f, r) => new BayesOptimizationAlgorithm(f, random: r, modelFactory: () => { var m = new RecordingModel(); models.Add(m); return m; }));

            // Die Modelle entstehen in der Reihenfolge A, B, C; alle Kennwerte sind positiv → ln.
            Assert.Equal(3, models.Count);
            Assert.All(models, m => Assert.All(m.LastOutputs, v => Assert.True(Math.Abs(v) < 5)));   // ln(~1..10) ≈ 0..2.3

            var mixed = MantaLike();
            mixed.Iterations = 2;
            mixed.Variants = 8;
            var baseSolve = mixed.Solve;
            mixed.Solve = r => { baseSolve(r); r.PassiveParameters["A"] -= 2.5f; };   // wechselt das Vorzeichen
            var mixedModels = new List<RecordingModel>();
            BayesHarness.Run(mixed, "Bayes", 3, Sub("gemischt"),
                (f, r) => new BayesOptimizationAlgorithm(f, random: r, modelFactory: () => { var m = new RecordingModel(); mixedModels.Add(m); return m; }));

            Assert.Contains(mixedModels[0].LastOutputs, v => v < 0);   // A wird roh gelernt
        }

        private sealed class RecordingModel : ISurrogateModel
        {
            private readonly GaussianProcess _inner = new(new Random(1));
            public List<double> LastOutputs { get; } = new();
            public void Fit(IReadOnlyList<double[]> inputs, IReadOnlyList<double> outputs)
            {
                LastOutputs.Clear();
                LastOutputs.AddRange(outputs);
                _inner.Fit(inputs, outputs);
            }
            public (double Mean, double Variance) Predict(double[] input) => _inner.Predict(input);
            public void AddPseudoObservation(double[] input, double output) => _inner.AddPseudoObservation(input, output);
            public IReadOnlyList<double>? ParameterRelevance => _inner.ParameterRelevance;
        }

        [Fact]
        public void A_Parameter_Without_Influence_Ends_Up_Last_In_The_Importance_File()
        {
            var problem = MantaLike();
            problem.Solve = r =>
            {
                // P5 hat keinen Einfluss
                r.PassiveParameters["A"] = 1.5f + 1.5f * r.ActiveParameters["P1"];
                r.PassiveParameters["B"] = 1.0f + r.ActiveParameters["P2"];
                r.PassiveParameters["C"] = 0.5f + (r.ActiveParameters["P3"] - 0.7f) * (r.ActiveParameters["P3"] - 0.7f) + 0.2f * r.ActiveParameters["P4"];
            };
            string directory = Sub("wichtig");

            BayesHarness.Run(problem, "Bayes", 3, directory);

            string[] lines = File.ReadAllLines(Path.Combine(directory, "Analyseergebnisse", BayesOptimizationAlgorithm.HistoryFileName));
            string[] header = lines[0].Split(';');
            string[] last = lines[^1].Split(';');
            int first = Array.IndexOf(header, "Relevanz_P1");
            var relevance = Enumerable.Range(0, 5)
                .Select(i => double.Parse(last[first + i], System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();

            Assert.True(relevance[4] <= relevance.Take(4).Max() * 0.5,
                $"P5 sollte kaum wichtig sein: {string.Join(", ", relevance.Select(v => v.ToString("F2")))}");
        }
    }
}
