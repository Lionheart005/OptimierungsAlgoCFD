using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using OptimierungsAlgoCFD.Core;
using Xunit;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Automatisches Ende der Optimierung (AutoStop, 09.11.2026): die reine Entscheidungsregel.
    /// </summary>
    public class AutoStopDecisionTests
    {
        private static List<ModelRecord> History(params float[] fitness)
            => fitness.Select((f, i) => new ModelRecord { Iteration = 1, Variant = i + 1, Fitness = f }).ToList();

        private static AutoStopConfig On(float minImprovement = 0.01f, int window = 3)
            => new AutoStopConfig { Mode = "On", MinImprovement = minImprovement, WindowVariants = window };

        [Fact]
        public void Does_Not_Stop_Before_There_Is_A_Reference_Before_The_Window()
        {
            // 3 Varianten, Fenster 3: es gibt kein "vorher".
            var decision = AutoStop.Evaluate(History(5f, 5f, 5f), On(window: 3));

            Assert.False(decision.Stop);
            Assert.Null(decision.Improvement);
        }

        [Fact]
        public void Stops_When_The_Best_Value_Stagnates_Inside_The_Window()
        {
            // vorher: Bestwert 10. Fenster (3): 9, 10.05, 8 -> Bestwert 10.05 = +0,5 % < 1 %.
            var decision = AutoStop.Evaluate(History(4f, 10f, 9f, 10.05f, 8f), On(window: 3));

            Assert.True(decision.Stop);
            Assert.Equal(0.005, decision.Improvement!.Value, 4);
        }

        [Fact]
        public void Continues_When_The_Window_Brings_Enough_Improvement()
        {
            // vorher 10, jetzt 10.2 = +2 % >= 1 %.
            var decision = AutoStop.Evaluate(History(10f, 9f, 10.2f, 9f), On(window: 3));

            Assert.False(decision.Stop);
            Assert.Equal(0.02, decision.Improvement!.Value, 4);
        }

        [Fact]
        public void Only_The_Last_Window_Counts_Not_Earlier_Progress()
        {
            // Früher großer Sprung (1 -> 10), danach Stillstand über das ganze Fenster.
            var decision = AutoStop.Evaluate(History(1f, 10f, 10f, 10f, 10f), On(window: 3));

            Assert.True(decision.Stop);
            Assert.Equal(0.0, decision.Improvement!.Value, 6);
        }

        [Fact]
        public void Failed_Variants_Count_In_The_Window_But_Never_Provide_The_Best_Value()
        {
            var history = History(10f, 50f, 9f, 9f);
            history[1].SimulationFailed = true; // trägt eine Fitness, zählt aber nicht als Bestwert

            // vorher (2 Varianten): nur die 10 gilt. Fenster: 9, 9 -> keine Verbesserung.
            var decision = AutoStop.Evaluate(history, On(window: 2));

            Assert.True(decision.Stop);
            Assert.Equal(0.0, decision.Improvement!.Value, 6);
        }

        [Fact]
        public void Does_Not_Stop_When_No_Valid_Variant_Exists_Before_The_Window()
        {
            var history = History(10f, 10f, 10f);
            history[0].SimulationFailed = true;

            Assert.False(AutoStop.Evaluate(history, On(window: 2)).Stop);
        }

        [Fact]
        public void Does_Not_Stop_When_The_Reference_Is_Zero()
        {
            Assert.False(AutoStop.Evaluate(History(0f, 0f, 0f), On(window: 2)).Stop);
        }

        [Fact]
        public void Works_With_Negative_Fitness_Values()
        {
            // -10 -> -9.95: 0,5 % besser bezogen auf |vorher|.
            var decision = AutoStop.Evaluate(History(-10f, -12f, -9.95f), On(window: 2));

            Assert.True(decision.Stop);
            Assert.Equal(0.005, decision.Improvement!.Value, 4);
        }

        [Theory]
        [InlineData("Off", false)]
        [InlineData("off", false)]
        [InlineData("On", true)]
        [InlineData(" ON ", true)]
        public void Mode_Is_Read_Case_Insensitive(string mode, bool expected)
        {
            Assert.Equal(expected, AutoStop.IsEnabled(new AutoStopConfig { Mode = mode }));
        }

        [Fact]
        public void Default_Is_Off()
        {
            Assert.False(AutoStop.IsEnabled(new SimulationConfig().AutoStop));
        }

        [Theory]
        [InlineData("Auto", 0.01f, 20)]
        [InlineData("On", -0.01f, 20)]
        [InlineData("On", 0.01f, 0)]
        public void Nonsense_Values_Are_A_Configuration_Error(string mode, float minImprovement, int window)
        {
            var config = new AutoStopConfig { Mode = mode, MinImprovement = minImprovement, WindowVariants = window };

            var ex = Assert.Throws<InvalidOperationException>(() => AutoStop.IsEnabled(config));
            Assert.Contains("AutoStop", ex.Message);
        }
    }

    /// <summary>
    /// AutoStop im echten Ablauf des <see cref="WorkflowController"/>, mit Attrappen statt Geometrie und Solver.
    /// </summary>
    public class AutoStopWorkflowTests : IDisposable
    {
        private readonly string _workingDirectory;

        public AutoStopWorkflowTests()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), "autostop_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, recursive: true);
        }

        /// <summary>
        /// Jede Runde liefert dieselben Fitnesswerte (= Variantennummer), es wird also nie etwas besser.
        /// Optional wird in einer Runde ein Sprung eingebaut.
        /// </summary>
        private (SimulationContext Context, WorkflowController Controller) Build(
            int iterations, int variants, AutoStopConfig autoStop, int? jumpInIteration = null)
        {
            var config = new SimulationConfig
            {
                MaxIterations = iterations,
                VariantsPerIteration = variants,
                UseRubberBandScaler = false,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" },
                AutoStop = autoStop
            };
            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { { "Length", 100f } },
                MaxDeviations = new Dictionary<string, float> { { "Length", 5f } },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { { "Length", (30f, 200f) } }
            };
            var context = new SimulationContext(config, project, _workingDirectory);

            var geometry = new Mock<IGeometryGenerator>();
            geometry.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns((int i, int v, Dictionary<string, float> p, string d, SimulationConfig c)
                    => new GeometryResult { StlPath = $"model_{i}_{v}.stl" });

            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                .Returns((MeshRequest r) => $"mesh_{r.Iteration}_{r.Variant}.su2");

            var solver = new Mock<ISimulationSolver>();
            solver.SetupGet(s => s.Name).Returns("Solver");

            var optimizer = new Mock<IOptimizationAlgorithm>();
            optimizer.SetupGet(o => o.Name).Returns("Test-Optimierer");
            optimizer.Setup(o => o.GenerateParameters(
                    It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns((SimulationContext c, int i, int v, int max) => new Dictionary<string, float> { { "Length", 100f + v } });
            optimizer.Setup(o => o.EvaluateAndSelectBest(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationContext>()))
                .Returns((List<ModelRecord> records, SimulationContext c) =>
                {
                    foreach (var r in records)
                        r.Fitness = r.Variant + (jumpInIteration.HasValue && r.Iteration >= jumpInIteration.Value ? 10f : 0f);
                    return records.OrderByDescending(r => r.Fitness).First();
                });

            var validator = new Mock<IModelValidator>();
            validator.Setup(v => v.ValidateChampion(
                    It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(),
                    It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                .Returns((List<ModelRecord> c, SimulationConfig cfg, Func<Dictionary<string, float>, ModelRecord> re)
                    => c.OrderByDescending(r => r.Fitness).First());

            var controller = new WorkflowController(
                geometry.Object, new[] { new SolverStage(mesher.Object, solver.Object) },
                validator.Object, optimizer.Object, context);
            return (context, controller);
        }

        [Fact]
        public void Off_Runs_The_Full_Length()
        {
            var (context, controller) = Build(5, 2, new AutoStopConfig { Mode = "Off" });

            controller.RunOptimization();

            Assert.Equal(10, context.History.Count);
        }

        [Fact]
        public void On_Ends_After_The_First_Iteration_Without_Improvement()
        {
            // Fenster 2 = eine Runde. Nach Runde 1 fehlt das "vorher", nach Runde 2 ist nichts besser geworden.
            var (context, controller) = Build(5, 2, new AutoStopConfig { Mode = "On", MinImprovement = 0.01f, WindowVariants = 2 });

            controller.RunOptimization();

            Assert.Equal(4, context.History.Count);
            Assert.Equal(2, context.History.Max(r => r.Iteration));
        }

        [Fact]
        public void An_Improvement_Keeps_The_Run_Going_Until_It_Stagnates_Again()
        {
            // Fenster 4 = zwei Runden, Sprung von 2 auf 12 in Runde 3.
            // Nach Runde 2: noch kein "vorher". Nach Runde 3 und 4: vorher 2, jetzt 12 -> weiter.
            // Nach Runde 5: vorher (Runde 1–3) 12, jetzt 12 -> Stopp.
            var (context, controller) = Build(8, 2,
                new AutoStopConfig { Mode = "On", MinImprovement = 0.01f, WindowVariants = 4 }, jumpInIteration: 3);

            controller.RunOptimization();

            Assert.Equal(5, context.History.Max(r => r.Iteration));
        }

        [Fact]
        public void The_Upper_Limit_Still_Applies()
        {
            // Fenster größer als der ganze Lauf: das automatische Ende greift nie, MaxIterations begrenzt.
            var (context, controller) = Build(3, 2, new AutoStopConfig { Mode = "On", WindowVariants = 50 });

            controller.RunOptimization();

            Assert.Equal(6, context.History.Count);
        }

        [Fact]
        public void Invalid_Settings_Abort_Before_The_First_Variant()
        {
            var (context, controller) = Build(3, 2, new AutoStopConfig { Mode = "Vielleicht" });

            Assert.Throws<InvalidOperationException>(() => controller.RunOptimization());
            Assert.Empty(context.History);
        }
    }
}
