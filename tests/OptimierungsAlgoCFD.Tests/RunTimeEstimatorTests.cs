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
    /// Zeitprognose (Plan zeitschaetzung.md). Geprüft wird nur die Rechnung und die Anzeige, mit Werten, die jeder
    /// Test selbst setzt, nicht die Einstellungen eines Projekts.
    /// </summary>
    public class RunTimeEstimatorTests
    {
        private static SimulationConfig Config(string mode = "Off", int topN = 3, int iterations = 10, int perIteration = 10)
            => new()
            {
                MaxIterations = iterations,
                VariantsPerIteration = perIteration,
                Su2MaxIterations = 5000,
                ChampionVerification = { Mode = mode, TopN = topN, RefinementSteps = 1 },
                ControlRuns = { Su2MaxIterations = 20000 }
            };

        private static RunTimeEstimator Estimator(SimulationConfig config, int dimensions = 3, bool autoStop = false, params double[] samples)
        {
            var estimator = new RunTimeEstimator(config, dimensions, autoStop);
            foreach (double s in samples) estimator.AddVariant(s);
            return estimator;
        }

        // --- Anzeige -------------------------------------------------------------------------------------

        [Theory]
        [InlineData(4.0, "4 s")]
        [InlineData(59.6, "1 min")]
        [InlineData(34 * 60.0, "34 min")]
        [InlineData(4 * 3600.0 + 49 * 60.0, "4 h 49 min")]
        [InlineData(23 * 3600.0 + 59 * 60.0 + 40, "1 T 0 h")]
        [InlineData(12 * 86400.0 + 17 * 3600.0 + 34 * 60.0, "12 T 18 h")]
        public void Durations_Are_Readable_And_Keep_The_Days(double seconds, string expected)
        {
            Assert.Equal(expected, SimulationLog.FormatDuration(TimeSpan.FromSeconds(seconds)));
        }

        [Fact]
        public void The_Old_Display_Hid_The_Days_The_New_One_Does_Not()
        {
            // 12 Tage 17 Stunden erschienen früher als "17:34:24".
            TimeSpan twelveDays = new TimeSpan(12, 17, 34, 24);
            Assert.StartsWith("12 T", SimulationLog.FormatDuration(twelveDays));
        }

        [Fact]
        public void A_Date_Carries_The_German_Weekday_Regardless_Of_Culture()
        {
            Assert.Equal("Mo 23.11. 09:00", SimulationLog.FormatDateTime(new DateTime(2026, 11, 23, 9, 0, 0)));
            Assert.Equal("So 06.12. 07:05", SimulationLog.FormatDateTime(new DateTime(2026, 12, 6, 7, 5, 0)));
        }

        // --- Rechnung ------------------------------------------------------------------------------------

        [Fact]
        public void Without_A_Measurement_There_Is_No_Estimate()
        {
            Assert.Null(Estimator(Config()).Estimate(1, 1, iterationFinished: false));
        }

        [Fact]
        public void Only_The_Last_Ten_Variants_Set_The_Pace()
        {
            // Zwei teure Ausreißer am Anfang, danach zehnmal 100 s: der alte Gesamtschnitt läge bei 400 s.
            var estimator = Estimator(Config(), 3, false, new[] { 1000.0, 1000.0 }.Concat(Enumerable.Repeat(100.0, 10)).ToArray());

            Assert.Equal(100.0, estimator.RecentMeanSeconds!.Value, 6);
        }

        [Fact]
        public void A_Zero_Or_Invalid_Duration_Is_Ignored()
        {
            var estimator = Estimator(Config());
            estimator.AddVariant(0);
            estimator.AddVariant(double.NaN);
            estimator.AddVariant(-5);

            Assert.Equal(0, estimator.SampleCount);
            Assert.Null(estimator.RecentMeanSeconds);
        }

        [Fact]
        public void The_Remaining_Time_Counts_Open_Variants_And_One_Champion_Check_Per_Open_Iteration()
        {
            var estimator = Estimator(Config(), 3, false, 100.0);

            // Iteration 4, 7 von 10 Varianten fertig: 3 Varianten offen + 6 volle Iterationen = 63, dazu die Prüfung
            // der laufenden Iteration und der sechs weiteren = 7. Zusammen 70 x 100 s.
            RunTimeEstimate e = estimator.Estimate(4, 7, iterationFinished: false)!;

            Assert.Equal(7000.0, e.Remaining.TotalSeconds, 6);
        }

        [Fact]
        public void The_Estimate_Of_The_Current_Iteration_Includes_Its_Champion_Check()
        {
            var estimator = Estimator(Config(), 3, false, 100.0);

            RunTimeEstimate e = estimator.Estimate(4, 7, iterationFinished: false)!;

            Assert.Equal(4, e.IterationNumber);
            Assert.Equal(400.0, e.IterationRemaining.TotalSeconds, 6); // 3 Varianten + Prüfung
        }

        [Fact]
        public void A_Finished_Iteration_Looks_Ahead_To_The_Next_One()
        {
            var estimator = Estimator(Config(), 3, false, 100.0);

            RunTimeEstimate e = estimator.Estimate(5, 10, iterationFinished: true)!;

            Assert.Equal(6, e.IterationNumber);
            Assert.Equal(1100.0, e.IterationRemaining.TotalSeconds, 6); // 10 Varianten + Prüfung
            Assert.Equal(5500.0, e.Remaining.TotalSeconds, 6);           // 5 Iterationen zu je 1100 s
        }

        [Fact]
        public void After_The_Last_Iteration_Nothing_Is_Left_To_Optimize()
        {
            var estimator = Estimator(Config(), 3, false, 100.0);

            RunTimeEstimate e = estimator.Estimate(10, 10, iterationFinished: true)!;

            Assert.Equal(0, e.IterationNumber);
            Assert.Equal(TimeSpan.Zero, e.Remaining);
        }

        [Fact]
        public void The_Final_Verification_Is_Reported_Separately_And_Scales_With_Cells_And_Iteration_Limit()
        {
            var estimator = Estimator(Config("FinalTopN", topN: 3), 3, false, 100.0);

            RunTimeEstimate e = estimator.Estimate(1, 1, iterationFinished: false)!;

            // 3D: Zellen wachsen je Stufe mit sqrt(2)^2,4, das strenge Profil rechnet 20000 statt 5000 Iterationen.
            double perRun = 100.0 * Math.Pow(Math.Sqrt(2.0), 2.4) * 4.0;
            Assert.Equal(3, e.FineCandidates);
            Assert.Equal(3 * perRun, e.FineVerification.TotalSeconds, 3);
            // Die Optimierung selbst bleibt davon unberührt: 99 offene Varianten und 10 Champion-Prüfungen.
            Assert.Equal(100.0 * (99 + 10), e.Remaining.TotalSeconds, 6);
        }

        [Fact]
        public void Two_Dimensions_Grow_Slower_Than_Three()
        {
            var estimator2D = Estimator(Config("Final"), 2, false, 100.0);
            var estimator3D = Estimator(Config("Final"), 3, false, 100.0);

            Assert.True(
                estimator2D.Estimate(1, 1, false)!.FineVerification < estimator3D.Estimate(1, 1, false)!.FineVerification);
        }

        [Theory]
        [InlineData("Off")]
        [InlineData("EachIteration")]
        public void Without_A_Final_Verification_There_Is_No_Extra_Block_At_The_End(string mode)
        {
            var estimator = Estimator(Config(mode), 3, false, 100.0);

            RunTimeEstimate e = estimator.Estimate(1, 1, iterationFinished: false)!;

            Assert.Equal(0, e.FineCandidates);
            Assert.Equal(TimeSpan.Zero, e.FineVerification);
        }

        [Fact]
        public void Verifying_Each_Iteration_Adds_One_Fine_Run_To_Every_Open_Iteration()
        {
            var off = Estimator(Config("Off"), 3, false, 100.0).Estimate(5, 10, iterationFinished: true)!;
            var each = Estimator(Config("EachIteration"), 3, false, 100.0).Estimate(5, 10, iterationFinished: true)!;

            double perRun = 100.0 * Math.Pow(Math.Sqrt(2.0), 2.4) * 4.0;
            Assert.Equal(off.Remaining.TotalSeconds + 5 * perRun, each.Remaining.TotalSeconds, 3);
        }

        [Fact]
        public void With_The_Automatic_End_On_The_Estimate_Is_An_Upper_Bound()
        {
            Assert.True(Estimator(Config(), 3, true, 100.0).Estimate(1, 1, false)!.UpperBound);
            Assert.False(Estimator(Config(), 3, false, 100.0).Estimate(1, 1, false)!.UpperBound);
        }

        [Fact]
        public void The_Control_Profile_Never_Makes_The_Fine_Run_Cheaper_Than_A_Normal_One()
        {
            SimulationConfig config = Config("Final");
            config.ControlRuns.Su2MaxIterations = 1000; // weniger als die normalen 5000

            double factor = RunTimeEstimator.FineCostFactor(config, 3);

            Assert.True(factor >= Math.Pow(Math.Sqrt(2.0), 2.4) - 1e-9);
        }

        // --- Zeilen im Log -------------------------------------------------------------------------------

        [Fact]
        public void The_Footer_Without_An_Estimate_Shows_Only_The_Duration()
        {
            var lines = SimulationLog.VariantFooterLines(4 * 3600.0 + 49 * 60.0, null, new DateTime(2026, 11, 20, 10, 0, 0));

            Assert.Single(lines);
            Assert.Equal("└─ ⏱ Dauer: 4 h 49 min", lines[0]);
        }

        [Fact]
        public void The_Footer_Names_Date_And_Remaining_Time_And_The_Final_Verification()
        {
            var estimator = Estimator(Config("FinalTopN"), 3, false, 3600.0);
            RunTimeEstimate e = estimator.Estimate(1, 1, iterationFinished: false)!;
            DateTime now = new DateTime(2026, 11, 20, 10, 0, 0);

            var lines = SimulationLog.VariantFooterLines(3600.0, e, now);

            Assert.Equal(2, lines.Count);
            Assert.Contains("Prognose: Optimierung fertig ca.", lines[0]);
            Assert.Contains(SimulationLog.FormatDateTime(now + e.Remaining), lines[0]);
            Assert.Contains("Rest ≈", lines[0]);
            Assert.Contains("Prognose: danach Feinprüfung bis zu", lines[1]);
            Assert.Contains("3 Kandidaten", lines[1]);
        }

        [Fact]
        public void The_Footer_Says_At_Most_When_The_Automatic_End_Is_On()
        {
            var estimator = Estimator(Config("Off"), 3, true, 3600.0);
            RunTimeEstimate e = estimator.Estimate(1, 1, iterationFinished: false)!;

            var lines = SimulationLog.VariantFooterLines(3600.0, e, new DateTime(2026, 11, 20, 10, 0, 0));

            Assert.Contains("höchstens", lines[0]);
        }

        [Fact]
        public void The_Iteration_Line_Names_The_End_Of_The_Next_Iteration()
        {
            var estimator = Estimator(Config("Off"), 3, false, 3600.0);
            RunTimeEstimate e = estimator.Estimate(5, 10, iterationFinished: true)!;
            DateTime now = new DateTime(2026, 11, 23, 9, 0, 0);

            string line = SimulationLog.TimeForecastLine(e, now)!;

            Assert.StartsWith("[PROGNOSE] Iteration 6 fertig ca. ", line);
            Assert.Contains(SimulationLog.FormatDateTime(now + TimeSpan.FromHours(11)), line);
        }

        [Fact]
        public void Without_An_Estimate_The_Iteration_Prints_No_Line()
        {
            Assert.Null(SimulationLog.TimeForecastLine(null, DateTime.Now));
        }
    }

    /// <summary>
    /// Die Prognose und der Grenz-Hinweis erscheinen im echten Ablauf des <see cref="WorkflowController"/>,
    /// nicht nur in den Hilfsfunktionen.
    /// </summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class RunTimeForecastInTheWorkflowTests : IDisposable
    {
        private readonly string _workingDirectory =
            Path.Combine(Path.GetTempPath(), "RunTimeForecast_" + Guid.NewGuid().ToString("N"));

        public RunTimeForecastInTheWorkflowTests() => Directory.CreateDirectory(_workingDirectory);

        public void Dispose()
        {
            try { Directory.Delete(_workingDirectory, recursive: true); } catch (IOException) { }
        }

        private string RunAndCapture(float lengthUpperBound)
        {
            var config = new SimulationConfig
            {
                MaxIterations = 2,
                VariantsPerIteration = 2,
                UseRubberBandScaler = false,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" }
            };
            var project = new ProjectConfig
            {
                ProjectName = "Test",
                BaseParameters = new Dictionary<string, float> { { "Length", 100f } },
                MaxDeviations = new Dictionary<string, float> { { "Length", 5f } },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { { "Length", (30f, lengthUpperBound) } }
            };
            var context = new SimulationContext(config, project, _workingDirectory);

            var geometry = new Mock<IGeometryGenerator>();
            geometry.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns(new GeometryResult { StlPath = "model.stl" });
            var mesher = new Mock<IMeshGenerator>();
            mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>())).Returns("mesh.su2");
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
                    foreach (var r in records) r.Fitness = r.Variant;
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

            var original = Console.Out;
            var buffer = new StringWriter();
            try
            {
                Console.SetOut(buffer);
                controller.RunOptimization();
            }
            finally
            {
                Console.SetOut(original);
            }
            return buffer.ToString();
        }

        [Fact]
        public void Every_Variant_Footer_And_Every_Iteration_Carry_A_Forecast()
        {
            string output = RunAndCapture(lengthUpperBound: 200f);

            // Nach der ersten fertigen Variante gibt es eine Messung, also schon in der Fußzeile.
            Assert.Contains("Prognose: Optimierung fertig ca.", output);
            // Nach Iteration 1 der Blick auf die nächste, nach der letzten nur noch "fertig".
            Assert.Contains("[PROGNOSE] Iteration 2 fertig ca.", output);
            Assert.Contains("[PROGNOSE] Optimierung fertig", output);
            // Das alte Format ist weg.
            Assert.DoesNotContain("Geschätzte Restzeit", output);
        }

        [Fact]
        public void A_Champion_On_The_Bound_Is_Reported_After_Each_Iteration_Not_Only_At_The_End()
        {
            // Der Champion jeder Runde ist Variante 2 mit Length = 102: das ist die obere Grenze.
            string output = RunAndCapture(lengthUpperBound: 102f);

            Assert.Contains("[GRENZE] Champion von Iteration 1 liegt an der Parametergrenze: Length = 102 (obere Grenze 102)", output);
            Assert.Contains("[GRENZE] Champion von Iteration 2", output);
        }

        [Fact]
        public void A_Champion_Inside_The_Bounds_Triggers_No_Boundary_Line()
        {
            Assert.DoesNotContain("[GRENZE]", RunAndCapture(lengthUpperBound: 200f));
        }
    }
}
