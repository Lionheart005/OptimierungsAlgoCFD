using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Tests für die Auswertung und Archivierung von SU2-Verläufen (history.csv, TODO-35).
    /// </summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class Su2HistoryAnalyzerTests
    {
        private const string HistoryConverged =
            "\"Inner_Iter\",   \"rms[Rho]\",       \"CD\",       \"CL\"\n" +
            "0,              -1.500,           0.520,      0.100\n" +
            "100,            -3.800,           0.380,      0.150\n" +
            "150,            -5.200,           0.315,      0.198\n" +
            "178,            -6.700,           0.310,      0.200\n" +
            "179,            -6.800,           0.310,      0.200\n";

        private const string HistoryStoppedAtIter =
            "\"Inner_Iter\",   \"rms[Rho]\",       \"CD\",       \"CL\"\n" +
            "0,              -1.200,           0.600,      0.100\n" +
            "200,            -2.500,           0.420,      0.180\n" +
            "400,            -3.200,           0.360,      0.210\n" +
            "498,            -3.790,           0.346,      0.215\n" +
            "499,            -3.800,           0.345,      0.215\n";

        private const string HistoryOscillating =
            "\"Inner_Iter\",   \"rms[Rho]\",       \"CD\",       \"CL\"\n" +
            "0,              -1.500,           0.500,      0.100\n" +
            "100,            -3.000,           0.380,      0.150\n" +
            "200,            -4.500,           0.300,      0.200\n" +
            "201,            -4.500,           0.350,      0.200\n" +
            "202,            -4.500,           0.300,      0.200\n" +
            "203,            -4.500,           0.350,      0.200\n";

        private static void WithHistoryFile(string content, Action<string> body)
        {
            string path = Path.Combine(Path.GetTempPath(), "history_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllText(path, content);
            try { body(path); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        private static string CaptureConsole(Action action)
        {
            var originalOut = Console.Out;
            using var sw = new StringWriter();
            try
            {
                Console.SetOut(sw);
                action();
                return sw.ToString();
            }
            finally
            {
                Console.SetOut(originalOut);
            }
        }

        // -------------------------------------------------------------------
        // 1. Reine Verlaufsauswertung: Su2HistoryAnalyzer.Evaluate
        // -------------------------------------------------------------------

        [Fact]
        public void History_Converged_Before_Iter_Is_Evaluated_Correctly()
        {
            string[] lines = HistoryConverged.TrimEnd().Split('\n');
            var options = new Su2HistoryOptions(
                maxIterations: 500,
                cauchyElems: 3,
                metricColumns: new[] { "CD", "CL" });

            Su2HistoryResult result = Su2HistoryAnalyzer.Evaluate(lines, options);

            // Letzte Zeile hat Inner_Iter = 179 -> 180 Iterationen
            Assert.Equal(180, result.Iterations);
            Assert.True(result.Converged);

            // -1.500 - (-6.800) = 5.300 Dekaden Abfall
            Assert.Equal(5.300f, result.ResidualDrop, 3);

            // Letzte 3 Zeilen CD: 0.315, 0.310, 0.310 -> Mean ≈ 0.311667, (0.315 - 0.310) / 0.311667 ≈ 0.01607
            Assert.True(result.Oscillation.ContainsKey("CD"));
            Assert.InRange(result.Oscillation["CD"], 0.015f, 0.017f);
        }

        [Fact]
        public void History_Stopped_Exactly_At_Iter_Is_Not_Converged()
        {
            string[] lines = HistoryStoppedAtIter.TrimEnd().Split('\n');
            var options = new Su2HistoryOptions(
                maxIterations: 500,
                cauchyElems: 150,
                metricColumns: new[] { "CD" });

            Su2HistoryResult result = Su2HistoryAnalyzer.Evaluate(lines, options);

            // Inner_Iter = 499 -> 500 Iterationen (genau ITER erreicht)
            Assert.Equal(500, result.Iterations);
            Assert.False(result.Converged);

            // -1.200 - (-3.800) = 2.600 Dekaden
            Assert.Equal(2.600f, result.ResidualDrop, 3);
        }

        [Fact]
        public void History_Oscillating_Reports_Oscillation_Over_Cauchy_Window()
        {
            string[] lines = HistoryOscillating.TrimEnd().Split('\n');
            var options = new Su2HistoryOptions(
                maxIterations: 500,
                cauchyElems: 4,
                metricColumns: new[] { "CD" });

            Su2HistoryResult result = Su2HistoryAnalyzer.Evaluate(lines, options);

            // Letzte 4 Zeilen: 0.300, 0.350, 0.300, 0.350 -> Mean = 0.325, Span = 0.050 -> Osc = 0.050 / 0.325 ≈ 0.1538
            Assert.True(result.Oscillation.ContainsKey("CD"));
            Assert.Equal(0.050f / 0.325f, result.Oscillation["CD"], 3);
            Assert.Equal(0.325f, result.WindowMeans["CD"], 3);
        }

        [Fact]
        public void History_Without_Rms_Column_Returns_NaN_Residual_Drop()
        {
            string[] lines = { "\"Inner_Iter\", \"CD\"", "0, 0.5", "1, 0.4" };
            var options = new Su2HistoryOptions(500, 10, new[] { "CD" });

            Su2HistoryResult result = Su2HistoryAnalyzer.Evaluate(lines, options);

            Assert.Equal(2, result.Iterations);
            Assert.True(float.IsNaN(result.ResidualDrop));
        }

        [Fact]
        public void History_With_Zero_Mean_Does_Not_Divide_By_Zero()
        {
            string[] lines = { "\"Inner_Iter\", \"CL\"", "0, 0.0", "1, 0.0" };
            var options = new Su2HistoryOptions(500, 2, new[] { "CL" });

            Su2HistoryResult result = Su2HistoryAnalyzer.Evaluate(lines, options);

            Assert.Equal(0.0f, result.Oscillation["CL"]);
        }

        // -------------------------------------------------------------------
        // 2. Warnungen auf der Konsole
        // -------------------------------------------------------------------

        [Fact]
        public void Unconverged_Run_Logs_Warning_With_Actual_Numbers_And_Column()
        {
            var solver = new Su2Solver();
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 150 };

            string output = CaptureConsole(() =>
            {
                WithHistoryFile(HistoryStoppedAtIter, path => solver.ReadMetricsFromHistory(path, record, config));
            });

            Assert.Contains("[WARNUNG] SU2 hat ITER=500 erreicht, ohne dass das Kriterium erfüllt war – CD ist der Stand bei Iteration 500, kein konvergierter Wert.", output);
        }

        [Fact]
        public void Oscillating_Run_Logs_Warning_About_Unsteady_Flow()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                OscillationWarnLimit = 0.02f
            });
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 4 };

            string output = CaptureConsole(() =>
            {
                WithHistoryFile(HistoryOscillating, path => solver.ReadMetricsFromHistory(path, record, config));
            });

            Assert.Contains("schwingt um ±", output);
            Assert.Contains("Hinweis auf instationäre Strömung (Wirbelablösung)", output);
        }

        // -------------------------------------------------------------------
        // 3. PassiveParameters Einträge (Su2_Iterations, Su2_Converged, Su2_Osc_*)
        // -------------------------------------------------------------------

        [Fact]
        public void History_Analysis_Populates_Su2_Passive_Parameters()
        {
            var solver = new Su2Solver();
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 3 };

            WithHistoryFile(HistoryConverged, path => solver.ReadMetricsFromHistory(path, record, config));

            Assert.Equal(180f, record.PassiveParameters["Su2_Iterations"]);
            Assert.Equal(1.0f, record.PassiveParameters["Su2_Converged"]);
            Assert.True(record.PassiveParameters.ContainsKey("Su2_Osc_Drag"));
            Assert.InRange(record.PassiveParameters["Su2_Osc_Drag"], 0.015f, 0.017f);
        }

        // -------------------------------------------------------------------
        // 4. Optionen: ResultAveraging & TreatUnconvergedAsFailed
        // -------------------------------------------------------------------

        [Fact]
        public void ResultAveraging_Last_Yields_The_Same_Value_As_Before()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultAveraging = "Last"
            });
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 4 };

            WithHistoryFile(HistoryOscillating, path => solver.ReadMetricsFromHistory(path, record, config));

            // Letzte Zeile von HistoryOscillating hat CD = 0.350
            Assert.Equal(0.350f, record.PassiveParameters["Drag"], 3);
        }

        [Fact]
        public void ResultAveraging_CauchyWindowMean_Yields_The_Window_Mean()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultAveraging = "CauchyWindowMean"
            });
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 4 };

            WithHistoryFile(HistoryOscillating, path => solver.ReadMetricsFromHistory(path, record, config));

            // Letzte 4 Zeilen: 0.300, 0.350, 0.300, 0.350 -> Mittelwert = 0.325
            Assert.Equal(0.325f, record.PassiveParameters["Drag"], 3);
        }

        [Fact]
        public void Unknown_ResultAveraging_Warns_And_Falls_Back_To_Last()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultAveraging = "Unbekannt"
            });
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 4 };

            string output = CaptureConsole(() =>
            {
                WithHistoryFile(HistoryOscillating, path => solver.ReadMetricsFromHistory(path, record, config));
            });

            Assert.Contains("[WARNUNG] Unbekanntes ResultAveraging 'Unbekannt' — nutze 'Last'.", output);
            Assert.Equal(0.350f, record.PassiveParameters["Drag"], 3);
        }

        [Fact]
        public void Unknown_ResultAveraging_Warns_Only_Once_Per_Solver_Instance()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultAveraging = "Unbekannt",
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "CD", ["Lift"] = "CL" }
            });
            var record1 = new ModelRecord();
            var record2 = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 4 };

            string output = CaptureConsole(() =>
            {
                WithHistoryFile(HistoryOscillating, path =>
                {
                    solver.ReadMetricsFromHistory(path, record1, config);
                    solver.ReadMetricsFromHistory(path, record2, config);
                });
            });

            int occurrences = (output.Length - output.Replace("Unbekanntes ResultAveraging", "").Length) / "Unbekanntes ResultAveraging".Length;
            Assert.Equal(1, occurrences);
        }

        [Fact]
        public void TreatUnconvergedAsFailed_Marks_Record_When_Unconverged()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                TreatUnconvergedAsFailed = true
            });
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 150 };

            WithHistoryFile(HistoryStoppedAtIter, path => solver.ReadMetricsFromHistory(path, record, config));

            Assert.True(record.SimulationFailed);
        }

        [Fact]
        public void Default_TreatUnconvergedAsFailed_Leaves_Record_Non_Failed()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                TreatUnconvergedAsFailed = false
            });
            var record = new ModelRecord();
            var config = new SimulationConfig { Su2MaxIterations = 500, CauchyElements = 150 };

            WithHistoryFile(HistoryStoppedAtIter, path => solver.ReadMetricsFromHistory(path, record, config));

            Assert.False(record.SimulationFailed);
        }

        // -------------------------------------------------------------------
        // 5. Dateinamens-Helper: ArchiveBaseName
        // -------------------------------------------------------------------

        [Fact]
        public void ArchiveBaseName_Without_Tag_Formats_Gen_And_Var()
        {
            var record = new ModelRecord { Iteration = 3, Variant = 7 };

            Assert.Equal("Gen3_Var7", Su2Solver.ArchiveBaseName(record));
            Assert.Equal("Gen3_Var7", Su2Solver.ArchiveBaseName(record, null));
            Assert.Equal("Gen3_Var7", Su2Solver.ArchiveBaseName(record, ""));
            Assert.Equal("Gen3_Var7", Su2Solver.ArchiveBaseName(record, "   "));
        }

        [Fact]
        public void ArchiveBaseName_With_Tag_Appends_Tag()
        {
            var record = new ModelRecord { Iteration = 2, Variant = 4 };

            Assert.Equal("Gen2_Var4_Calib", Su2Solver.ArchiveBaseName(record, "Calib"));
            Assert.Equal("Gen2_Var4_Verification", Su2Solver.ArchiveBaseName(record, "  Verification  "));
        }

        [Fact]
        public void ArchiveBaseNameFromMesh_Takes_Tag_From_Mesh_Name()
        {
            // Servertest 06.11.2026: alle fünf Kalibrierstufen schrieben in dieselbe History_Gen1_Var1.csv.
            var record = new ModelRecord { Iteration = 1, Variant = 1 };

            Assert.Equal("Gen1_Var1", Su2Solver.ArchiveBaseNameFromMesh("/x/Model_Gen1_Var1.su2", record));
            Assert.Equal("Gen1_Var1_Kal5", Su2Solver.ArchiveBaseNameFromMesh("/x/Model_Gen1_Var1_Kal5.su2", record));
            Assert.Equal("Gen1_Var1_Recheck", Su2Solver.ArchiveBaseNameFromMesh("Model_Gen1_Var1_Recheck.su2", record));
            // Fremde Namen: ohne Tag wie bisher.
            Assert.Equal("Gen1_Var1", Su2Solver.ArchiveBaseNameFromMesh("netz.su2", record));
            Assert.Equal("Gen1_Var1", Su2Solver.ArchiveBaseNameFromMesh("Model_Gen1_Var10.su2", record));
        }
    }
}
