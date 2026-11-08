using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// TODO-W1 (kombinierte Toleranz), TODO-W3 (Drift statt Cauchy) und TODO-W4 (Profil der Kontrollrechnungen).
    /// </summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class ConvergenceAndToleranceTests
    {
        // ------------------------------------------------------------------
        // W1: MetricTolerance
        // ------------------------------------------------------------------

        [Fact]
        public void Near_Zero_Values_Use_The_Absolute_Part()
        {
            // Servertest 06.11.2026: CL 0,00005 gegen -0,00052 galt als "1 727 %" und scheiterte.
            float atol = MetricTolerance.AbsoluteFor("Lift");
            Assert.Equal(0.005f, atol);
            Assert.True(MetricTolerance.Within(-0.00052f, 0.00005f, 0.02f, atol));
            Assert.True(MetricTolerance.Normalized(-0.00052f, 0.00005f, 0.02f, atol) < 0.2f);
        }

        [Fact]
        public void Large_Values_Use_The_Relative_Part()
        {
            // CL 1,0 gegen 1,05: 5 % bei 2 % Toleranz und 0,005 Mindestbetrag -> nicht in Ordnung.
            float n = MetricTolerance.Normalized(1.05f, 1.0f, 0.02f, 0.005f);
            Assert.InRange(n, 1.99f, 2.01f);
            Assert.False(MetricTolerance.Within(1.05f, 1.0f, 0.02f, 0.005f));
            Assert.True(MetricTolerance.Within(1.02f, 1.0f, 0.02f, 0.005f));
        }

        [Fact]
        public void Overrides_Win_And_Unknown_Metrics_Default_To_Zero()
        {
            var overrides = new Dictionary<string, float> { ["lift"] = 0.01f };
            Assert.Equal(0.01f, MetricTolerance.AbsoluteFor("Lift", overrides));
            Assert.Equal(1e-4f, MetricTolerance.AbsoluteFor("Drag", overrides));
            Assert.Equal(0f, MetricTolerance.AbsoluteFor("Volume", overrides));
        }

        [Fact]
        public void Zero_Tolerance_Only_Accepts_Equality()
        {
            Assert.Equal(0f, MetricTolerance.Normalized(1f, 1f, 0f, 0f));
            Assert.True(float.IsPositiveInfinity(MetricTolerance.Normalized(1.1f, 1f, 0f, 0f)));
        }

        [Fact]
        public void Calibration_Accepts_A_Zero_Lift_Sequence_That_Is_Relatively_Huge()
        {
            // alpha = 0: CL schwankt um 0, CD konvergiert sauber.
            var stages = new List<CalibrationStageData>
            {
                Stage(1, 2.0f, 0.01336f, -0.00074f),
                Stage(2, 1.41f, 0.01036f, 0.00021f),
                Stage(3, 1.0f, 0.00830f, -0.00023f),
                Stage(4, 0.71f, 0.00805f, -0.00052f),
                Stage(5, 0.5f, 0.00799f, 0.00005f),
            };

            var decision = CalibrationEvaluator.EvaluateCalibration(stages, new[] { "Drag", "Lift" }, 0.02f);

            Assert.True(decision.Success, decision.SummaryMessage);
            // Stufe 3 weicht bei CD um 3,9 % ab (> 2 % + 0,0001), Stufe 4 um 0,75 % -> Stufe 4.
            Assert.Equal(4, decision.SelectedStage!.LevelIndex);
        }

        private static CalibrationStageData Stage(int level, float factor, float drag, float lift)
            => new(level, factor, 1000 * level, 1.0, new Dictionary<string, float> { ["Drag"] = drag, ["Lift"] = lift },
                   0f, true, 1.2f, true);

        [Fact]
        public void Richardson_Extrapolation_Estimates_The_Mesh_Independent_Value()
        {
            // 2. Ordnung, r = sqrt(2): f3 = 0.05, f2 = 0.04, f1 = 0.035 -> p = 2, f_inf = 0.035 - 0.005 = 0.030
            var gci = CalibrationEvaluator.ComputeGci(0.05f, 0.04f, 0.035f);

            Assert.Equal(2.0f, gci.ObservedOrderP, 1);
            Assert.Equal(0.030f, gci.ExtrapolatedValue, 3);
            Assert.InRange(gci.ExtrapolatedDeviationPercent, 16f, 17f);   // 0,035 liegt ~16,7 % über 0,030
        }

        [Fact]
        public void Richardson_Is_Not_Reported_For_Non_Monotonic_Sequences()
        {
            var gci = CalibrationEvaluator.ComputeGci(0.05f, 0.03f, 0.04f);
            Assert.False(gci.IsMonotonic);
            Assert.True(float.IsNaN(gci.ExtrapolatedValue));
        }

        // ------------------------------------------------------------------
        // W3: Drift im Endfenster
        // ------------------------------------------------------------------

        private static string[] History(int rows, Func<int, double> cd, Func<int, double> cl)
        {
            var lines = new List<string> { "\"Inner_Iter\", \"rms[P]\", \"CD\", \"CL\"" };
            for (int i = 0; i < rows; i++)
            {
                lines.Add(string.Join(",", i.ToString(CultureInfo.InvariantCulture), "-6.0",
                    cd(i).ToString("R", CultureInfo.InvariantCulture), cl(i).ToString("R", CultureInfo.InvariantCulture)));
            }
            return lines.ToArray();
        }

        private static Su2HistoryResult Eval(string[] lines, int window = 100)
            => Su2HistoryAnalyzer.Evaluate(lines, new Su2HistoryOptions(2000, window, new[] { "CD", "CL" }));

        private static readonly Dictionary<string, string> Columns = new() { ["Drag"] = "CD", ["Lift"] = "CL" };

        [Fact]
        public void Steady_Oscillation_Has_No_Drift_And_Counts_As_Converged()
        {
            // Wirbelablösung: CD pendelt um 0,03 mit ±5 % und fester Periode.
            var h = Eval(History(600, i => 0.03 * (1 + 0.05 * Math.Sin(i * 0.3)), i => 1.0));

            Assert.Equal(100, h.TailRows);
            Assert.True(h.WindowDrift["CD"] < 0.03f * 0.01f);
            Assert.InRange(h.OscillationAmplitude["CD"], 0.08f, 0.12f);

            var verdict = Su2ConvergenceJudge.Judge(h, Columns, 0.005f, 0.25f, null);
            Assert.True(verdict.Assessed);
            Assert.True(verdict.Converged, $"schlechtester Kennwert {verdict.WorstMetric}: {verdict.WorstNormalized}");
        }

        [Fact]
        public void Slow_Drift_Is_Detected_Even_Though_Single_Steps_Are_Tiny()
        {
            // Servertest: CD wanderte im Fenster um 3 % weiter, SU2 hatte "konvergiert" gemeldet.
            // 0,36 → 0,324 über 400 Zeilen = ~2,5 % je 100-Zeilen-Fenster.
            var h = Eval(History(400, i => 0.0360 - 0.0036 * i / 400.0, i => 1.0));

            var verdict = Su2ConvergenceJudge.Judge(h, Columns, 0.005f, 0.25f, null);

            Assert.True(verdict.Assessed);
            Assert.False(verdict.Converged);
            Assert.Equal("Drag", verdict.WorstMetric);
        }

        [Fact]
        public void Drift_Is_Not_Confused_With_Oscillation_Amplitude()
        {
            // Reine Drift ohne Pendeln: Amplitude nach Trendabzug ~0.
            var h = Eval(History(400, i => 0.03 + 0.00001 * i, i => 1.0));

            Assert.True(h.OscillationAmplitude["CD"] < 0.001f);
            Assert.True(h.WindowDrift["CD"] > 0.0004f);
        }

        [Fact]
        public void Short_Tail_Is_Not_Assessed_And_Old_Rule_Applies()
        {
            var h = Eval(History(30, i => 0.03, i => 1.0), window: 150);

            Assert.True(h.TailRows < Su2ConvergenceJudge.MinTailRows);
            Assert.False(Su2ConvergenceJudge.Judge(h, Columns, 0.005f, 0.25f, null).Assessed);
        }

        [Fact]
        public void Near_Zero_Lift_Drift_Uses_The_Absolute_Floor()
        {
            // CL ~ 0 und wandert um 0,0003: relativ riesig, absolut unter 0,005 * 0,25.
            var h = Eval(History(400, i => 0.03, i => 0.0003 * i / 400.0));

            var verdict = Su2ConvergenceJudge.Judge(h, Columns, 0.005f, 0.25f, null);

            Assert.True(verdict.Converged, $"{verdict.WorstMetric}: {verdict.WorstNormalized}");
        }

        [Fact]
        public void Solver_Takes_The_Tail_Mean_And_Reports_Converged_At_Iter_Limit()
        {
            // 500 Iterationen = ITER erreicht, Kennwerte aber stabil: früher "nicht konvergiert".
            string path = Path.Combine(Path.GetTempPath(), "hist_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllLines(path, History(500, i => 0.03 * (1 + 0.02 * Math.Sin(i * 0.4)), i => 1.0 + 0.01 * Math.Sin(i * 0.4)));
            try
            {
                var options = new Su2SolverOptions { ResultMetrics = new() { ["Drag"] = "CD", ["Lift"] = "CL" }, ResultAveraging = "TailMean" };
                var solver = new Su2Solver(options);
                var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 100 };
                var record = new ModelRecord { Iteration = 1, Variant = 1 };

                solver.ReadMetricsFromHistory(path, record, config);

                Assert.Equal(1.0f, record.PassiveParameters["Su2_Converged"]);
                Assert.InRange(record.PassiveParameters["Drag"], 0.0295f, 0.0305f);
                Assert.True(record.PassiveParameters["Su2_IterErr_Drag"] <= 1.0f);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Solver_Flags_A_Drifting_Run_And_TreatUnconvergedAsFailed_Applies()
        {
            string path = Path.Combine(Path.GetTempPath(), "hist_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllLines(path, History(250, i => 0.0360 - 0.0036 * i / 250.0, i => 1.0));
            try
            {
                var options = new Su2SolverOptions { ResultMetrics = new() { ["Drag"] = "CD" }, TreatUnconvergedAsFailed = true };
                var solver = new Su2Solver(options);
                var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 100 };
                var record = new ModelRecord { Iteration = 1, Variant = 1 };

                solver.ReadMetricsFromHistory(path, record, config);

                // SU2 hätte bei Iteration 250 "vor ITER" aufgehört, die Drift-Prüfung sieht es anders.
                Assert.Equal(0.0f, record.PassiveParameters["Su2_Converged"]);
                Assert.True(record.SimulationFailed);
            }
            finally { File.Delete(path); }
        }

        // ------------------------------------------------------------------
        // W2: SU2-Version aus dem Banner, Bezug im 2D-Schnitt
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("|   / __| | | |_  )   Release 7.5.1 \"Blackbird\"          |", "7.5.1")]
        [InlineData("SU2 v8.0.0 \"Harrier\"", "8.0.0")]
        [InlineData("Release 7.5 \"x\"", "7.5")]
        public void Su2_Version_Is_Read_From_The_Banner(string line, string expected)
            => Assert.Equal(expected, Su2Solver.TryParseVersion("irgendwas" + Environment.NewLine + line + Environment.NewLine + "mehr"));

        [Fact]
        public void Su2_Version_Is_Null_Without_Banner()
        {
            Assert.Null(Su2Solver.TryParseVersion(null));
            Assert.Null(Su2Solver.TryParseVersion("Kein Banner hier"));
        }

        [Fact]
        public void RunInfo_Version_Is_Filled_Only_When_Unknown()
        {
            string dir = Path.Combine(Path.GetTempPath(), "runinfo_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                var info = new RunInfo { Projekt = "T", Programme = new() { ["SU2"] = "unbekannt", ["Gmsh"] = "4.11.1" } };
                RunInfoWriter.Write(dir, info);

                RunInfoWriter.SetProgramVersion(dir, "SU2", "7.5.1");
                RunInfoWriter.SetProgramVersion(dir, "Gmsh", "9.9.9");   // bekannt: bleibt
                RunInfoWriter.SetProgramVersion(Path.Combine(dir, "gibtsnicht"), "SU2", "1.0"); // wirft nie

                var read = RunInfoWriter.Read(dir)!;
                Assert.Equal("7.5.1", read.Programme["SU2"]);
                Assert.Equal("4.11.1", read.Programme["Gmsh"]);
            }
            finally { Directory.Delete(dir, true); }
        }

        // ------------------------------------------------------------------
        // W4: Profil der Kontrollrechnungen
        // ------------------------------------------------------------------

        [Theory]
        [InlineData("Kal1", true)]
        [InlineData("Kal2D_3", true)]
        [InlineData("Kal3D_4", true)]
        [InlineData("Fein1", true)]
        [InlineData("Fein_Gen3", true)]
        [InlineData("Recheck", true)]
        [InlineData("", false)]
        [InlineData(null, false)]
        public void Control_Run_Tags_Are_Recognized(string? tag, bool expected)
            => Assert.Equal(expected, Su2Solver.IsControlRunTag(tag));

        [Fact]
        public void The_Doorman_Control_Run_Is_Not_A_Control_Run()
        {
            // Variante 99 (Türsteher) trägt keinen Tag und rechnet mit dem normalen Profil.
            var record = new ModelRecord { Iteration = 3, Variant = 99 };
            Assert.Equal(string.Empty, Su2Solver.ExtractTag("/x/Model_Gen3_Var99.su2", record));
            Assert.Equal("Kal5", Su2Solver.ExtractTag("/x/Model_Gen3_Var99_Kal5.su2", record));
        }

        [Fact]
        public void Control_Profile_Is_Stricter_But_Never_Weaker()
        {
            var normal = new SimulationConfig { Su2MaxIterations = 3000, CauchyElements = 300, CauchyTolerance = "1e-5" };
            var control = normal.WithControlRunProfile();

            Assert.Equal(20000, control.Su2MaxIterations);
            Assert.Equal(500, control.CauchyElements);
            Assert.Equal("1e-6", control.CauchyTolerance);
            Assert.Equal(0.1f, control.IterativeToleranceFraction);
            // Das Original bleibt unverändert.
            Assert.Equal(3000, normal.Su2MaxIterations);
            Assert.Equal(0.25f, normal.IterativeToleranceFraction);

            var big = new SimulationConfig { Su2MaxIterations = 50000, CauchyElements = 900 }.WithControlRunProfile();
            Assert.Equal(50000, big.Su2MaxIterations);
            Assert.Equal(900, big.CauchyElements);
        }

        // ------------------------------------------------------------------ Paket B: drei Zustände

        [Fact]
        public void Batch_Statistics_Tell_An_Oscillation_From_A_Drift()
        {
            var oscillation = Enumerable.Range(0, 400).Select(i => (float)(0.03 * (1 + 0.3 * Math.Sin(i * 0.37)))).ToList();
            var drift = Enumerable.Range(0, 400).Select(i => (float)(0.03 * (1 + 0.0005 * i))).ToList();
            var constant = Enumerable.Repeat(0.03f, 400).ToList();

            Assert.True(Su2HistoryAnalyzer.BatchStatistics(oscillation, out double seOsc, out bool sigOsc));
            Assert.False(sigOsc);
            Assert.True(seOsc > 0);

            Assert.True(Su2HistoryAnalyzer.BatchStatistics(drift, out _, out bool sigDrift));
            Assert.True(sigDrift);

            Assert.True(Su2HistoryAnalyzer.BatchStatistics(constant, out double seConst, out bool sigConst));
            Assert.False(sigConst);
            Assert.Equal(0.0, seConst, 9);

            Assert.False(Su2HistoryAnalyzer.BatchStatistics(new List<float> { 1, 2, 3 }, out _, out _));   // zu kurz
        }

        private static ModelRecord ReadWithFailOnUnconverged(string[] history, out string path)
        {
            path = Path.Combine(Path.GetTempPath(), "hist_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllLines(path, history);
            var options = new Su2SolverOptions { ResultMetrics = new() { ["Drag"] = "CD" }, ResultAveraging = "Last", TreatUnconvergedAsFailed = true };
            var config = new SimulationConfig { Su2MaxIterations = 2000, CauchyElements = 200 };
            var record = new ModelRecord { Iteration = 1, Variant = 1 };
            new Su2Solver(options).ReadMetricsFromHistory(path, record, config);
            return record;
        }

        [Fact]
        public void Vortex_Shedding_Is_Unsteady_But_Stable_Not_A_Failure()
        {
            // CD pendelt langsam und kräftig (±20 %, Periode etwa 70 Zeilen) um 0,03: die Halbfenster-Mittel
            // unterscheiden sich, die einfache Drift-Prüfung schlägt an, aber es gibt keinen Trend.
            var record = ReadWithFailOnUnconverged(History(800, i => 0.03 * (1 + 0.2 * Math.Sin(i * 0.09)), i => 1.0), out string path);
            try
            {
                Assert.False(record.SimulationFailed);
                Assert.Equal(1f, record.PassiveParameters["Su2_Unsteady"]);
                Assert.InRange(record.PassiveParameters["Drag"], 0.029f, 0.031f);   // Fenstermittel, nicht der letzte Wert
                Assert.True(MetricUncertainty.TryGet(record, "Drag", out double u));
                Assert.InRange(u, 1e-5, 0.1);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_Real_Drift_Still_Counts_As_Failed_When_Configured()
        {
            // CD wandert stetig weiter: echter iterativer Fehler.
            var record = ReadWithFailOnUnconverged(History(800, i => 0.03 * (1 + 0.0005 * i), i => 1.0), out string path);
            try
            {
                Assert.True(record.SimulationFailed);
                Assert.Equal(0f, record.PassiveParameters["Su2_Unsteady"]);
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void A_Converged_Run_Has_A_Tiny_Uncertainty()
        {
            var record = ReadWithFailOnUnconverged(History(800, i => 0.03, i => 1.0), out string path);
            try
            {
                Assert.False(record.SimulationFailed);
                Assert.Equal(0f, record.PassiveParameters["Su2_Unsteady"]);
                Assert.True(MetricUncertainty.TryGet(record, "Drag", out double u));
                Assert.True(u < 1e-6, $"u = {u}");
            }
            finally { File.Delete(path); }
        }

        [Fact]
        public void Control_Runs_Always_Use_The_Tail_Mean()
        {
            string path = Path.Combine(Path.GetTempPath(), "hist_" + Guid.NewGuid().ToString("N") + ".csv");
            // Auskonvergiert, nur der letzte Wert weicht minimal ab: normale Rechnung nimmt ihn ("Last"),
            // die Kontrollrechnung das Mittel des Endfensters.
            File.WriteAllLines(path, History(400, i => i == 399 ? 0.0302 : 0.03, i => 1.0));
            try
            {
                var options = new Su2SolverOptions { ResultMetrics = new() { ["Drag"] = "CD" }, ResultAveraging = "Last" };
                var solver = new Su2Solver(options);
                var config = new SimulationConfig { Su2MaxIterations = 2000, CauchyElements = 100 };

                var normalRecord = new ModelRecord { Iteration = 1, Variant = 1 };
                solver.ReadMetricsFromHistory(path, normalRecord, config, controlRun: false);
                var controlRecord = new ModelRecord { Iteration = 1, Variant = 1 };
                solver.ReadMetricsFromHistory(path, controlRecord, config, controlRun: true);

                Assert.Equal(0.0302f, normalRecord.PassiveParameters["Drag"], 5);
                Assert.InRange(controlRecord.PassiveParameters["Drag"], 0.0299f, 0.0306f);
            }
            finally { File.Delete(path); }
        }
    }
}
