using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Moq;
using Xunit;

using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Plan wandschicht_zellzahl.md (WZ4–WZ6, WZ10): Vernetzungsfehler sind keine Strömungsabstürze, Ersatznetze werden
    /// gekennzeichnet, die Zellgrenze gilt für die gemessene Zellzahl, Netzkennwerte stehen in der CSV.
    /// </summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class MeshRobustnessTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "meshrob_" + Guid.NewGuid().ToString("N"));

        public MeshRobustnessTests() => Directory.CreateDirectory(_dir);

        public void Dispose()
        {
            if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
        }

        // ------------------------------------------------------------------ Pipeline

        private SimulationContext Context()
        {
            var config = new SimulationConfig { UseRubberBandScaler = false, ChampionVerification = new ChampionVerificationConfig { Mode = "Off" } };
            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { { "Length", 100f } },
                MaxDeviations = new Dictionary<string, float> { { "Length", 5f } },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { { "Length", (30f, 200f) } },
            };
            return new SimulationContext(config, project, _dir);
        }

        private static IGeometryGenerator Geometry()
        {
            var mock = new Mock<IGeometryGenerator>();
            mock.Setup(g => g.GenerateAndExport(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns(new GeometryResult { StlPath = "model.stl" });
            return mock.Object;
        }

        private static IOptimizationAlgorithm Optimizer() => new Mock<IOptimizationAlgorithm>().Object;

        private static IModelValidator Validator() => new Mock<IModelValidator>().Object;

        private WorkflowController Controller(SimulationContext context, IMeshGenerator mesher, ISimulationSolver solver)
            => new WorkflowController(Geometry(), new[] { new SolverStage(mesher, solver) }, Validator(), Optimizer(), context);

        private static (ISimulationSolver Solver, List<string> Calls) CountingSolver()
        {
            var calls = new List<string>();
            var mock = new Mock<ISimulationSolver>();
            mock.SetupGet(s => s.Name).Returns("S");
            mock.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                .Callback(() => calls.Add("Solve"));
            return (mock.Object, calls);
        }

        private string FakeSu2(int cells)
        {
            string path = Path.Combine(_dir, $"fake_{cells}.su2");
            File.WriteAllText(path, $"NDIME= 3\nNELEM= {cells}\n");
            return path;
        }

        private static Dictionary<string, float> Parameters() => new() { { "Length", 100f } };

        [Fact]
        public void Mesh_Metrics_Are_Copied_Into_The_Record()
        {
            var mesher = new Mock<IMeshGenerator>();
            mesher.As<IMeshMetricsSource>().SetupGet(m => m.LastMeshMetrics).Returns(new Dictionary<string, float>
            {
                ["BoundaryLayerLayers"] = 21f, ["BoundaryLayerThicknessMm"] = 2.22f, ["BoundaryLayerStage"] = 1f, ["MeshFallback"] = 0f,
            });
            string su2 = FakeSu2(1000);
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Returns(su2);
            var (solver, calls) = CountingSolver();

            ModelRecord record = Controller(Context(), mesher.Object, solver).RunPipeline(Parameters(), 1, 1);

            Assert.False(record.SimulationFailed);
            Assert.Equal(21f, record.PassiveParameters["BoundaryLayerLayers"]);
            Assert.Equal(2.22f, record.PassiveParameters["BoundaryLayerThicknessMm"]);
            Assert.Equal(1000f, record.PassiveParameters["MeshCells"]);
            Assert.Single(calls);
        }

        [Fact]
        public void A_Mesh_Generation_Error_Is_Marked_As_Mesh_Failure_Not_As_Solver_Crash()
        {
            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Throws(new MeshGenerationException("Gmsh: Invalid boundary mesh"));
            var (solver, calls) = CountingSolver();

            ModelRecord record = Controller(Context(), mesher.Object, solver).RunPipeline(Parameters(), 1, 1);

            Assert.True(record.SimulationFailed);
            Assert.Equal(1f, record.PassiveParameters[ModelRecord.MeshFailedKey]);
            Assert.Empty(calls);
        }

        [Fact]
        public void Any_Other_Error_Is_Not_Marked_As_Mesh_Failure()
        {
            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Throws(new InvalidOperationException("Gmsh fehlt"));
            var (solver, _) = CountingSolver();

            ModelRecord record = Controller(Context(), mesher.Object, solver).RunPipeline(Parameters(), 1, 1);

            Assert.True(record.SimulationFailed);
            Assert.False(record.PassiveParameters.ContainsKey(ModelRecord.MeshFailedKey));
        }

        [Fact]
        public void Cell_Limit_Is_Checked_On_The_Measured_Cells_Before_The_Solver_Runs()
        {
            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Returns(FakeSu2(6000));
            var (solver, calls) = CountingSolver();
            SimulationContext context = Context();
            context.CellLimit = 5000;

            ModelRecord record = Controller(context, mesher.Object, solver).RunPipeline(Parameters(), 1, 1);

            Assert.True(record.SimulationFailed);
            Assert.Equal(1f, record.PassiveParameters[ModelRecord.CellLimitExceededKey]);
            Assert.Equal(6000f, record.PassiveParameters["MeshCells"]);
            Assert.Empty(calls);
        }

        [Fact]
        public void Cell_Limit_Does_Not_Stop_A_Mesh_Below_The_Limit_Or_Without_A_Limit()
        {
            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Returns(FakeSu2(6000));

            var (solverA, callsA) = CountingSolver();
            SimulationContext below = Context();
            below.CellLimit = 6000;                   // genau an der Grenze: erlaubt
            Controller(below, mesher.Object, solverA).RunPipeline(Parameters(), 1, 1);
            Assert.Single(callsA);

            var (solverB, callsB) = CountingSolver();
            Controller(Context(), mesher.Object, solverB).RunPipeline(Parameters(), 1, 1);   // CellLimit 0 = keine Grenze
            Assert.Single(callsB);
        }

        // ------------------------------------------------------------------ Trainingsdaten (Bayes)

        private SurrogateTrainingSet TrainingSet(params ModelRecord[] records)
        {
            var project = new ProjectConfig
            {
                ProjectName = "Raum",
                BaseParameters = new Dictionary<string, float> { ["Length"] = 100f },
                MaxDeviations = new Dictionary<string, float> { ["Length"] = 10f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { ["Length"] = (50f, 150f) },
            };
            var context = new SimulationContext(new SimulationConfig(), project, _dir);
            context.History.AddRange(records);
            return SurrogateTrainingSet.Build(context, ParameterSpace.Create(context));
        }

        private static ModelRecord Rec(int variant, float length)
        {
            var r = new ModelRecord { Iteration = 1, Variant = variant, Fitness = 1f };
            r.ActiveParameters["Length"] = length;
            return r;
        }

        [Fact]
        public void A_Solver_Crash_Feeds_The_Success_Model()
        {
            var crash = Rec(2, 60f);
            crash.SimulationFailed = true;

            var set = TrainingSet(Rec(1, 100f), crash);

            Assert.Single(set.Records);
            Assert.Single(set.ExcludedPoints);
            Assert.Single(set.FailedPoints);
        }

        [Fact]
        public void A_Mesh_Failure_Repels_Locally_But_Does_Not_Teach_The_Success_Model()
        {
            // FZ7: ein Gmsh-Fehler schreckt nicht die ganze Formzone ab, nur die enge Umgebung
            var meshFail = Rec(2, 60f);
            meshFail.SimulationFailed = true;
            meshFail.PassiveParameters[ModelRecord.MeshFailedKey] = 1f;

            var set = TrainingSet(Rec(1, 100f), meshFail);

            Assert.Single(set.Records);
            Assert.Single(set.ExcludedPoints);
            Assert.Empty(set.FailedPoints);
        }

        [Fact]
        public void A_Fallback_Mesh_Is_No_Training_Data_And_No_Crash()
        {
            var fallback = Rec(2, 60f);
            fallback.PassiveParameters[ModelRecord.MeshFallbackKey] = 1f;

            var set = TrainingSet(Rec(1, 100f), fallback);

            Assert.Single(set.Records);
            Assert.Single(set.ExcludedPoints);
            Assert.Empty(set.FailedPoints);
        }

        // ------------------------------------------------------------------ Bericht am Laufende

        [Fact]
        public void Robustness_Report_Counts_Stages_And_Names_The_Special_Shapes()
        {
            ModelRecord Make(int v, float stage, float fitness = 10f)
            {
                var r = new ModelRecord { Iteration = 1, Variant = v, Fitness = fitness, StlPath = $"Model_Gen1_Var{v}.stl" };
                r.PassiveParameters["BoundaryLayerStage"] = stage;
                return r;
            }
            var failed = new ModelRecord { Iteration = 1, Variant = 5, SimulationFailed = true, StlPath = "Model_Gen1_Var5.stl" };
            failed.PassiveParameters[ModelRecord.MeshFailedKey] = 1f;

            var lines = MeshRobustnessReport.Lines(new[] { Make(1, 1), Make(2, 1), Make(3, 2), Make(4, 3, 7.5f), failed });

            string all = string.Join("\n", lines);
            Assert.Contains("2 mit Standard-Wandschicht (Stufe A), 1 örtlich gedünnt (Stufe B), 1 mit Ersatznetz (Stufe C), 1 nicht vernetzbar", all);
            Assert.Contains("Formen mit Ersatznetz", all);
            Assert.Contains("Variante 4", all);
            Assert.Contains("Nicht vernetzbare Formen", all);
            Assert.Contains("Variante 5", all);
        }

        [Fact]
        public void Robustness_Report_Is_Silent_Without_Wall_Layer_Data()
        {
            Assert.Empty(MeshRobustnessReport.Lines(new[] { new ModelRecord { Variant = 1 } }));
            Assert.Empty(MeshRobustnessReport.Lines(Array.Empty<ModelRecord>()));
        }

        [Fact]
        public void Mesh_Metadata_Keys_Are_Not_Geometry_Metrics()
        {
            Assert.True(ModelRecord.IsMeshMetadataKey("BoundaryLayerLayers"));
            Assert.True(ModelRecord.IsMeshMetadataKey("BoundaryLayerHexShare"));
            Assert.True(ModelRecord.IsMeshMetadataKey(ModelRecord.MeshFailedKey));
            Assert.True(ModelRecord.IsMeshMetadataKey(ModelRecord.MeshFallbackKey));
            Assert.True(ModelRecord.IsMeshMetadataKey(ModelRecord.CellLimitExceededKey));
            Assert.False(ModelRecord.IsMeshMetadataKey("Drag"));
            Assert.False(ModelRecord.IsMeshMetadataKey("Volume"));
        }

        // ------------------------------------------------------------------ Kalibrierung

        private static CalibrationStageData Stage(int level, float factor, int cells, int layers = 0, int wallStage = 0, double seconds = 60)
            => new(level, factor, cells, seconds, new Dictionary<string, float> { ["Drag"] = 1f }, 0f, true, 1f, true,
                BoundaryLayerLayers: layers, BoundaryLayerThicknessMm: layers > 0 ? 2.2f : 0f, BoundaryLayerStage: wallStage);

        [Fact]
        public void Cell_Estimate_Uses_The_Measured_Exponent_Not_The_Cube()
        {
            // MantaAuv 09.11.2026 (gemessen): Faktor 0,71 → 321 952, 0,50 → 696 802, 0,35 → 1 695 747 Zellen
            var stages = new List<CalibrationStageData>
            {
                Stage(4, 0.71f, 321_952), Stage(5, 0.50f, 696_802), Stage(6, 0.35f, 1_695_747),
            };

            long next = MeshCalibration.EstimateNextCells(stages, 0.25f, dimensions: 3);

            // gemessener Exponent ≈ 2,5 → rund 3,9 Mio.; die alte kubische Annahme ergab 4,65 Mio. (und mit nur einer
            // Messung, Stufe 1, für Faktor 0,5 sogar 3,05 Mio. statt der gemessenen 0,70 Mio.)
            Assert.InRange(next, 3_600_000L, 4_300_000L);
            long old = MeshCalibration.EstimateCells(1_695_747, 0.35f, 0.25f, 3);
            Assert.True(next < old);
        }

        [Fact]
        public void Cell_Estimate_Falls_Back_To_The_Dimension_With_One_Measurement()
        {
            var one = new List<CalibrationStageData> { Stage(1, 2.0f, 1000) };

            Assert.Equal(MeshCalibration.EstimateCells(1000, 2.0f, 0.5f, 3), MeshCalibration.EstimateNextCells(one, 0.5f, 3));
            Assert.Equal(0L, MeshCalibration.EstimateNextCells(new List<CalibrationStageData>(), 0.5f, 3));
        }

        [Fact]
        public void Cell_Estimate_Exponent_Is_Clamped_To_Half_The_Dimension_And_The_Dimension()
        {
            // kaum Zuwachs bei feinerem Netz → Exponent nach unten auf 1,5 (3D) begrenzt; mehr als kubisch → auf 3 begrenzt
            var flat = new List<CalibrationStageData> { Stage(1, 1.0f, 1000), Stage(2, 0.5f, 1010) };
            var steep = new List<CalibrationStageData> { Stage(1, 1.0f, 1000), Stage(2, 0.5f, 100_000) };

            Assert.Equal((long)(1010 * Math.Pow(0.5 / 0.25, 1.5)), MeshCalibration.EstimateNextCells(flat, 0.25f, 3));
            Assert.Equal((long)(100_000 * Math.Pow(0.5 / 0.25, 3.0)), MeshCalibration.EstimateNextCells(steep, 0.25f, 3));
        }

        [Theory]
        [InlineData(30, "30 s")]
        [InlineData(900.6, "15 min")]
        [InlineData(11411.8, "3 h 10 min")]
        [InlineData(26603.8, "7 h 23 min")]
        public void Durations_Are_Readable_In_The_Log(double seconds, string expected)
            => Assert.Equal(expected, MeshCalibration.FormatDuration(seconds));

        [Fact]
        public void Layer_Mismatch_Between_Stages_Is_Reported()
        {
            // Bericht-Lauf 09.11.2026: Stufen 1–3 hatten 21, Stufen 4–6 nur 16 Schichten
            var stages = new List<CalibrationStageData>
            {
                Stage(1, 2.0f, 100, layers: 21, wallStage: 1), Stage(2, 1.41f, 200, layers: 21, wallStage: 1),
                Stage(4, 0.71f, 300, layers: 16, wallStage: 1),
            };

            string? text = MeshCalibration.DescribeLayerMismatch(stages);

            Assert.NotNull(text);
            Assert.Contains("unterschiedliche Schichtzahlen", text);
            Assert.Contains("Stufe 4: 16", text);
        }

        [Fact]
        public void Equal_Layers_Give_No_Warning_But_A_Fallback_Mesh_Does()
        {
            var same = new List<CalibrationStageData>
            {
                Stage(1, 2.0f, 100, layers: 21, wallStage: 1), Stage(5, 0.5f, 200, layers: 21, wallStage: 2),
            };
            Assert.Null(MeshCalibration.DescribeLayerMismatch(same));
            Assert.Null(MeshCalibration.DescribeLayerMismatch(new List<CalibrationStageData> { Stage(1, 2.0f, 100) }));

            var withFallback = new List<CalibrationStageData>
            {
                Stage(1, 2.0f, 100, layers: 21, wallStage: 1), Stage(5, 0.5f, 200, layers: 21, wallStage: 3),
            };
            string? text = MeshCalibration.DescribeLayerMismatch(withFallback);
            Assert.NotNull(text);
            Assert.Contains("Ersatznetz", text);
            Assert.Contains("5", text);
        }

        [Fact]
        public void Fingerprint_Contains_The_Mesher_Scheme_Version()
        {
            Assert.False(string.IsNullOrWhiteSpace(MeshCalibration.MesherSchemeVersion));
        }

        // ------------------------------------------------------------------ Zellgrenze in der Kalibrierung

        private sealed class Scenario : IDisposable
        {
            public string Dir = Path.Combine(Path.GetTempPath(), "meshcal_" + Guid.NewGuid().ToString("N"));
            public List<float> Factors = new();
            public ProjectContext Context;
            public SimulationContext SimContext;

            public Scenario(long maxCells3D)
            {
                Directory.CreateDirectory(Dir);
                var sim = SimulationConfig.CreateDefault();
                sim.MeshCalibration.Tolerance = 0.02f;
                sim.MeshCalibration.MaxCells3D = maxCells3D;
                var proj = new ProjectConfig { ProjectName = "T" };
                Context = new ProjectContext("T", Dir, null, sim, proj);
                SimContext = new SimulationContext(sim, proj, Dir);
            }

            public void Dispose()
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            }

            /// <summary>
            /// Wie die Pipeline: ein Netz über <see cref="SimulationContext.CellLimit"/> rechnet nicht, trägt das Kennzeichen
            /// und ist gescheitert. Zellen wachsen mit f^−2 (cells0 / f²), CD fällt mit f nicht auf einen festen Wert.
            /// </summary>
            public Func<float, string, ModelRecord> Runner(double cells0)
                => (factor, tag) =>
                {
                    Factors.Add(factor);
                    var rec = new ModelRecord();
                    float cells = (float)(cells0 / (factor * factor));
                    rec.PassiveParameters["MeshCells"] = cells;
                    rec.PassiveParameters["YPlus_P95"] = 1.0f;
                    if (SimContext.CellLimit > 0 && cells > SimContext.CellLimit)
                    {
                        rec.PassiveParameters[ModelRecord.CellLimitExceededKey] = 1f;
                        rec.SimulationFailed = true;
                        return rec;
                    }
                    rec.PassiveParameters["Drag"] = (float)(0.012 + 0.03 * factor);
                    rec.PassiveParameters["Su2_Converged"] = 1f;
                    return rec;
                };
        }

        [Fact]
        public void Reference_Stage_Over_The_Measured_Limit_Aborts_The_Calibration_Before_Solving_It()
        {
            using var s = new Scenario(maxCells3D: 100_000);
            var run = s.Runner(cells0: 40_000);   // Faktor 0,5 → 40 000 / 0,25 = 160 000 Zellen > Grenze 100 000

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 3));

            Assert.Contains("Referenznetz", ex.Message);
            Assert.Contains("gemessen", ex.Message);
            Assert.Equal(0L, s.SimContext.CellLimit);   // aufgeräumt
        }

        [Fact]
        public void Extra_Stage_Over_The_Measured_Limit_Is_Not_Solved_And_Ends_The_Refinement()
        {
            // Faktor 0,5 → 80 000 Zellen (erlaubt), die Vorschau für 0,35 liegt bei ~163 000 (1,63 × Grenze 100 000 → übersprungen),
            // bei Grenze 120 000 liegt die Vorschau darunter × 1,5 → vernetzt, gemessen zu groß → nicht gerechnet
            using var s = new Scenario(maxCells3D: 120_000);
            var run = s.Runner(cells0: 20_000);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 3);

            Assert.Contains(0.35f, s.Factors);                       // wurde versucht (vernetzt)
            Assert.DoesNotContain(decision.AllStages, st => Math.Abs(st.Factor - 0.35f) < 0.01f);   // aber nicht gewertet
            Assert.DoesNotContain(s.Factors, f => f < 0.35f);        // und danach endet es
            Assert.Equal(0L, s.SimContext.CellLimit);
        }
    }
}
