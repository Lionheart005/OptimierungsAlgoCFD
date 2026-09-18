using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-15: der "ABSOLUTE CHAMPION" am Ende des Laufs muss der beste Record der
    /// gesamten History sein — vorher war es der Gewinner der letzten Iteration.
    /// </summary>
    public class WorkflowControllerChampionTests
    {
        private static ModelRecord Record(int iteration, int variant, float fitness) => new ModelRecord
        {
            Iteration = iteration,
            Variant = variant,
            Fitness = fitness
        };

        [Fact]
        public void Picks_Best_Fitness_Not_Last_Entry()
        {
            var history = new List<ModelRecord>
            {
                Record(1, 1, 5.0f),
                Record(1, 2, 42.0f),   // bester Lauf, liegt aber nicht am Ende
                Record(2, 1, 7.0f),
                Record(2, 2, 9.0f)     // Gewinner der letzten Iteration
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.NotNull(best);
            Assert.Equal(1, best!.Iteration);
            Assert.Equal(2, best.Variant);
            Assert.Equal(42.0f, best.Fitness);
        }

        /// <summary>
        /// Disqualifizierte (Bouncer) und fehlgeschlagene Modelle tragen eine sehr kleine
        /// Fitness — sie dürfen nie gewinnen, solange ein gültiges Modell existiert.
        /// </summary>
        [Fact]
        public void Ignores_Disqualified_And_Failed_Records()
        {
            var failed = Record(1, 1, 0.0001f);
            failed.SimulationFailed = true;

            var history = new List<ModelRecord>
            {
                failed,
                Record(1, 2, 0.0001f),  // vom ChampionValidator disqualifiziert
                Record(1, 3, 1.5f)
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.Equal(3, best!.Variant);
        }

        /// <summary>Bei Gleichstand gewinnt der frühere Eintrag — sonst wäre die Auswahl zufällig.</summary>
        [Fact]
        public void Ties_Go_To_The_Earlier_Record()
        {
            var history = new List<ModelRecord>
            {
                Record(1, 1, 3.0f),
                Record(2, 1, 3.0f)
            };

            var best = WorkflowController.SelectBestRecord(history);

            Assert.Equal(1, best!.Iteration);
        }

        [Fact]
        public void Empty_History_Yields_Null()
        {
            Assert.Null(WorkflowController.SelectBestRecord(new List<ModelRecord>()));
        }
    }

    /// <summary>
    /// TODO-19: Orchestrierung des Controllers mit ausschließlich gemockten Interfaces —
    /// ohne PicoGK, Gmsh oder SU2. Geprüft wird, was der Controller selbst verantwortet:
    /// Reihenfolge der Solver-Kette, Übernahme der Metriken, Mesh-Cache und das Verhalten
    /// bei Fehlern in Vernetzung oder Solver.
    /// </summary>
    public class WorkflowControllerOrchestrationTests : IDisposable
    {
        private readonly string _workingDirectory;

        public WorkflowControllerOrchestrationTests()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), "wfctest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, recursive: true);
        }

        private SimulationContext Context(int iterations = 1, int variants = 2, bool useScaler = false)
        {
            var config = new SimulationConfig
            {
                MaxIterations = iterations,
                VariantsPerIteration = variants,
                UseRubberBandScaler = useScaler,
                TargetPicoGkSize = 50f
            };

            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { { "Length", 100f } },
                MaxDeviations = new Dictionary<string, float> { { "Length", 5f } },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { { "Length", (30f, 200f) } },
                DimensionalParameters = new HashSet<string> { "Length" }
            };

            return new SimulationContext(config, project, _workingDirectory);
        }

        /// <summary>Geometrie-Attrappe: schreibt nichts, liefert feste Metriken.</summary>
        private static IGeometryGenerator Geometry(params (string Name, float Value, MetricScaling Scaling)[] metrics)
        {
            var mock = new Mock<IGeometryGenerator>();
            mock.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns((int iter, int var_, Dictionary<string, float> p, string dir, SimulationConfig c) =>
                {
                    var result = new GeometryResult { StlPath = $"model_{iter}_{var_}.stl" };
                    foreach (var metric in metrics) result.AddMetric(metric.Name, metric.Value, metric.Scaling);
                    return result;
                });
            return mock.Object;
        }

        /// <summary>Optimierer-Attrappe: gibt die Basisparameter zurück und bewertet nach Variantennummer.</summary>
        private static IOptimizationAlgorithm Optimizer()
        {
            var mock = new Mock<IOptimizationAlgorithm>();
            mock.SetupGet(o => o.Name).Returns("Test-Optimierer");
            mock.Setup(o => o.GenerateParameters(
                    It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns((SimulationContext ctx, int iter, int var_, int max) =>
                    new Dictionary<string, float> { { "Length", 100f + var_ } });
            mock.Setup(o => o.EvaluateAndSelectBest(It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationContext>()))
                .Returns((List<ModelRecord> records, SimulationContext ctx) =>
                {
                    foreach (var record in records) record.Fitness = record.Variant;
                    return records.OrderByDescending(r => r.Fitness).First();
                });
            return mock.Object;
        }

        /// <summary>Validator-Attrappe: bestätigt immer den Kandidaten mit der höchsten Fitness.</summary>
        private static IModelValidator PassthroughValidator()
        {
            var mock = new Mock<IModelValidator>();
            mock.Setup(v => v.ValidateChampion(
                    It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(),
                    It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                .Returns((List<ModelRecord> c, SimulationConfig cfg, Func<Dictionary<string, float>, ModelRecord> re)
                    => c.OrderByDescending(r => r.Fitness).First());
            return mock.Object;
        }

        private static IMeshGenerator Mesher(List<string> log, string name, bool throws = false)
        {
            var mock = new Mock<IMeshGenerator>();
            mock.Setup(m => m.GenerateMesh(
                    It.IsAny<string>(), It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<SimulationConfig>(), It.IsAny<float>(), It.IsAny<string>()))
                .Returns((string stl, int iter, int var_, SimulationConfig c, float scale, string dir) =>
                {
                    log.Add(name);
                    if (throws) throw new InvalidOperationException($"{name} kann dieses Modell nicht vernetzen");
                    return $"{name}_{iter}_{var_}.su2";
                });
            return mock.Object;
        }

        private static ISimulationSolver Solver(List<string> log, string name, bool throws = false)
        {
            var mock = new Mock<ISimulationSolver>();
            mock.SetupGet(s => s.Name).Returns(name);
            mock.Setup(s => s.Solve(
                    It.IsAny<string>(), It.IsAny<ModelRecord>(),
                    It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                .Callback((string mesh, ModelRecord record, SimulationConfig c, string dir) =>
                {
                    log.Add(name);
                    if (throws) throw new InvalidOperationException($"{name} ist abgestürzt");
                    record.PassiveParameters[name + "Result"] = 1f;
                });
            return mock.Object;
        }

        [Fact]
        public void Solver_Chain_Runs_In_Order_For_Every_Variant()
        {
            var log = new List<string>();
            var stages = new[]
            {
                new SolverStage(Mesher(log, "MesherA"), Solver(log, "SolverA")),
                new SolverStage(Mesher(log, "MesherB"), Solver(log, "SolverB"))
            };

            var context = Context(iterations: 1, variants: 1);
            new WorkflowController(Geometry(), stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(new[] { "MesherA", "SolverA", "MesherB", "SolverB" }, log);
        }

        /// <summary>
        /// Stufen, die sich eine Mesher-INSTANZ teilen, vernetzen nur einmal (TODO-4).
        /// Der Record führt den Pfad der ersten Stufe.
        /// </summary>
        [Fact]
        public void Shared_Mesher_Instance_Meshes_Only_Once()
        {
            var log = new List<string>();
            var shared = Mesher(log, "Gemeinsam");
            var stages = new[]
            {
                new SolverStage(shared, Solver(log, "SolverA")),
                new SolverStage(shared, Solver(log, "SolverB"))
            };

            var context = Context(iterations: 1, variants: 1);
            new WorkflowController(Geometry(), stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(new[] { "Gemeinsam", "SolverA", "SolverB" }, log);
            Assert.Equal("Gemeinsam_1_1.su2", context.History[0].MeshPath);
        }

        /// <summary>
        /// Die vom Projekt deklarierten Metriken landen im Record — samt Rückskalierung.
        /// Bei abgeschaltetem Scaler ist die Rückskalierung die Identität.
        /// </summary>
        [Fact]
        public void Geometry_Metrics_Land_In_The_Record()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var geometry = Geometry(
                ("Volume", 4200f, MetricScaling.Volume),
                ("FrontalArea", 850f, MetricScaling.Area),
                ("TailTaper", 1.25f, MetricScaling.None));

            var context = Context(iterations: 1, variants: 1);
            new WorkflowController(geometry, stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            var record = context.History.Single();
            Assert.Equal(4200f, record.PassiveParameters["Volume"], 3);
            Assert.Equal(850f, record.PassiveParameters["FrontalArea"], 3);
            Assert.Equal(1.25f, record.PassiveParameters["TailTaper"], 3);
            Assert.Equal(1f, record.PassiveParameters["ScaleFactor"], 5);   // Scaler aus
            Assert.Equal("model_1_1.stl", record.StlPath);
            Assert.Equal(1f, record.PassiveParameters["SResult"], 5);        // vom Solver geschrieben
            Assert.False(record.SimulationFailed);
        }

        /// <summary>
        /// Eingeschalteter Scaler: der Controller schrumpft die Parameter für die Geometrie
        /// und rechnet die Metriken dimensionsgerecht zurück.
        /// </summary>
        [Fact]
        public void Metrics_Are_Restored_Dimension_Aware_When_The_Scaler_Runs()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            // Length = 101 (Variante 1), Zielgröße 50 -> ShrinkFactor ~0.495
            var geometry = Geometry(
                ("Volume", 1f, MetricScaling.Volume),
                ("Area", 1f, MetricScaling.Area),
                ("Length", 1f, MetricScaling.Linear),
                ("Ratio", 1f, MetricScaling.None));

            var context = Context(iterations: 1, variants: 1, useScaler: true);
            new WorkflowController(geometry, stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            var record = context.History.Single();
            float shrink = record.PassiveParameters["ScaleFactor"];
            Assert.Equal(50f / 101f, shrink, 5);

            Assert.Equal(MathF.Pow(1f / shrink, 3), record.PassiveParameters["Volume"], 3);
            Assert.Equal(MathF.Pow(1f / shrink, 2), record.PassiveParameters["Area"], 3);
            Assert.Equal(1f / shrink, record.PassiveParameters["Length"], 3);
            Assert.Equal(1f, record.PassiveParameters["Ratio"], 5);   // dimensionslos
        }

        /// <summary>
        /// Ein Fehler im Mesher darf den ganzen Lauf nicht abbrechen: der Record wird als
        /// fehlgeschlagen markiert, die Schleife läuft weiter (TODO-2).
        /// </summary>
        [Fact]
        public void Failing_Mesher_Marks_The_Record_But_Does_Not_Abort_The_Run()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M", throws: true), Solver(log, "S")) };

            var context = Context(iterations: 2, variants: 2);
            new WorkflowController(Geometry(("Volume", 10f, MetricScaling.Volume)), stages,
                    PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(4, context.History.Count);
            Assert.All(context.History, r => Assert.True(r.SimulationFailed));
            Assert.DoesNotContain("S", log);                        // Solver lief nie
            Assert.All(context.History, r => Assert.Equal(10f, r.PassiveParameters["Volume"], 3));
        }

        [Fact]
        public void Failing_Solver_Marks_The_Record_But_Does_Not_Abort_The_Run()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S", throws: true)) };

            var context = Context(iterations: 1, variants: 2);
            new WorkflowController(Geometry(), stages, PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(2, context.History.Count);
            Assert.All(context.History, r => Assert.True(r.SimulationFailed));
        }

        /// <summary>Alle Varianten aller Iterationen landen in der History und im CSV.</summary>
        [Fact]
        public void Every_Variant_Is_Recorded_And_Exported()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var context = Context(iterations: 3, variants: 4);
            new WorkflowController(Geometry(("Volume", 1f, MetricScaling.Volume)), stages,
                    PassthroughValidator(), Optimizer(), context)
                .RunOptimization();

            Assert.Equal(12, context.History.Count);
            Assert.Equal(new[] { 1, 2, 3 }, context.History.Select(r => r.Iteration).Distinct());
            Assert.Equal(new[] { 1, 2, 3, 4 }, context.History.Select(r => r.Variant).Distinct());

            string csv = Path.Combine(_workingDirectory, "Simulation_Results.csv");
            Assert.True(File.Exists(csv), "Simulation_Results.csv wurde nicht geschrieben.");
            Assert.Equal(13, File.ReadAllLines(csv).Length);   // Kopfzeile + 12 Datensätze
        }

        /// <summary>
        /// Der Validator bekommt genau die Records der laufenden Iteration — nicht die History —
        /// und sein Re-Simulations-Callback fährt dieselbe Pipeline unter Variante 99.
        /// </summary>
        [Fact]
        public void Validator_Sees_The_Current_Iteration_And_Can_Resimulate()
        {
            var log = new List<string>();
            var stages = new[] { new SolverStage(Mesher(log, "M"), Solver(log, "S")) };

            var seenCounts = new List<int>();
            ModelRecord? resimulated = null;

            var validator = new Mock<IModelValidator>();
            validator.Setup(v => v.ValidateChampion(
                    It.IsAny<List<ModelRecord>>(), It.IsAny<SimulationConfig>(),
                    It.IsAny<Func<Dictionary<string, float>, ModelRecord>>()))
                .Returns((List<ModelRecord> c, SimulationConfig cfg, Func<Dictionary<string, float>, ModelRecord> re) =>
                {
                    seenCounts.Add(c.Count);
                    resimulated = re(new Dictionary<string, float> { { "Length", 60f } });
                    return c.OrderByDescending(r => r.Fitness).First();
                });

            var context = Context(iterations: 2, variants: 3);
            new WorkflowController(Geometry(), stages, validator.Object, Optimizer(), context)
                .RunOptimization();

            Assert.Equal(new[] { 3, 3 }, seenCounts);              // je Iteration nur deren Varianten
            Assert.NotNull(resimulated);
            Assert.Equal(99, resimulated!.Variant);                // ValidationVariantNumber
            Assert.Equal(6, context.History.Count);                // Validierungsläufe zählen nicht mit
        }
    }
}
