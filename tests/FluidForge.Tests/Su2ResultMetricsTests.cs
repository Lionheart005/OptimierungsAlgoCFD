using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-26: SU2 no longer returns only Drag. Which column of history.csv maps to which
    /// name in the record is configured in <see cref="Su2SolverOptions.ResultMetrics"/>.
    ///
    /// Tests run against a synthetic sample history.csv instead of a live run — MPI and SU2
    /// are not installed on the development machine.
    /// </summary>
    public class Su2ResultMetricsTests
    {
        /// <summary>
        /// What a history.csv from SU2 looks like: column names in quotes and padded with
        /// spaces; the row of interest is the last one.
        /// </summary>
        private const string HistoryWithAeroCoefficients =
            "\"Inner_Iter\",   \"rms[Rho]\",       \"CD\",       \"CL\",      \"CSF\",      \"CMx\",      \"CMy\",      \"CMz\"\n" +
            "0,              -1.234,           0.900,      0.100,     0.010,     0.001,     0.002,     0.003\n" +
            "1,              -3.456,           0.500,      0.200,     0.020,     0.004,     0.005,     0.006\n" +
            "2,              -6.789,           0.345,      0.678,     0.031,     0.007,     0.123,     0.009\n";

        /// <summary>Writes a history.csv into the temp folder and cleans it up afterward.</summary>
        private static void WithHistoryFile(string content, Action<string> body)
        {
            string path = Path.Combine(Path.GetTempPath(), "history_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllText(path, content);

            try { body(path); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // -------------------------------------------------------------------
        // Default behavior — an existing project must not notice the change
        // -------------------------------------------------------------------

        [Fact]
        public void Default_Options_Read_Exactly_Drag_From_Cd()
        {
            var solver = new Su2Solver();
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            // Exactly one metric, from the last row — and no "Lift" that nobody requested.
            Assert.Equal(new[] { "Drag" }, record.PassiveParameters.Keys);
            Assert.Equal(0.345f, record.PassiveParameters["Drag"], 5);
            Assert.False(record.SimulationFailed);
        }

        [Fact]
        public void Default_Result_Metrics_Are_Drag_To_Cd()
        {
            var defaults = new Su2SolverOptions();

            Assert.Equal(new Dictionary<string, string> { ["Drag"] = "CD" }, defaults.ResultMetrics);
        }

        // -------------------------------------------------------------------
        // Multiple metrics simultaneously
        // -------------------------------------------------------------------

        [Fact]
        public void Configured_Metrics_All_Arrive_Under_Their_Own_Names()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultMetrics = new Dictionary<string, string>
                {
                    ["Drag"] = "CD",
                    ["Lift"] = "CL",
                    ["PitchMoment"] = "CMy"
                }
            });
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            Assert.Equal(0.345f, record.PassiveParameters["Drag"], 5);
            Assert.Equal(0.678f, record.PassiveParameters["Lift"], 5);
            Assert.Equal(0.123f, record.PassiveParameters["PitchMoment"], 5);
            Assert.False(record.SimulationFailed);
        }

        // -------------------------------------------------------------------
        // The actual reason for exact matching (TODO-26)
        // -------------------------------------------------------------------

        /// <summary>
        /// Before TODO-26 the solver searched via StartsWith. "CM" would have matched CMx, CMy,
        /// or CMz — depending solely on column order.
        /// </summary>
        [Fact]
        public void Ambiguous_Prefix_Cm_Does_Not_Silently_Hit_Cmx()
        {
            string[] headers = { "Inner_Iter", "CD", "CL", "CMx", "CMy", "CMz" };

            int index = Su2Solver.FindColumn(headers, "CM", out string problem);

            Assert.Equal(-1, index);
            Assert.Contains("CMx", problem);
            Assert.Contains("CMy", problem);
            Assert.Contains("CMz", problem);
        }

        [Fact]
        public void Ambiguous_Column_Marks_The_Record_As_Failed()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultMetrics = new Dictionary<string, string> { ["Moment"] = "CM" }
            });
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            Assert.True(record.SimulationFailed);
            Assert.False(record.PassiveParameters.ContainsKey("Moment"));
        }

        [Fact]
        public void Exact_Match_Wins_Over_A_Longer_Column_With_The_Same_Prefix()
        {
            string[] headers = { "Inner_Iter", "CD(Wall)", "CD", "CL" };

            int index = Su2Solver.FindColumn(headers, "CD", out string problem);

            Assert.Equal(2, index);
            Assert.Equal(string.Empty, problem);
        }

        /// <summary>
        /// The prefix path stays supported — depending on version and MARKER_MONITORING, SU2 also
        /// writes names like CD(Wall). However, it only applies when unambiguous and reports it.
        /// </summary>
        [Fact]
        public void Unique_Prefix_Is_Used_But_Reported()
        {
            string[] headers = { "Inner_Iter", "CD(Wall)", "CL(Wall)" };

            int index = Su2Solver.FindColumn(headers, "CD", out string problem);

            Assert.Equal(1, index);
            Assert.Contains("CD(Wall)", problem);
        }

        [Fact]
        public void Column_Names_Are_Compared_Case_Insensitively()
        {
            string[] headers = { "Inner_Iter", "CD", "CL" };

            Assert.Equal(1, Su2Solver.FindColumn(headers, "cd", out _));
        }

        // -------------------------------------------------------------------
        // Error cases: better disqualified than half-computed
        // -------------------------------------------------------------------

        [Fact]
        public void Configured_But_Missing_Column_Marks_The_Record_As_Failed()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultMetrics = new Dictionary<string, string>
                {
                    ["Drag"] = "CD",
                    ["Lift"] = "CL_does_not_exist"
                }
            });
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            Assert.True(record.SimulationFailed);
            Assert.False(record.PassiveParameters.ContainsKey("Lift"));
            // Readable metrics are still captured — they appear in the CSV later
            // and help with debugging.
            Assert.Equal(0.345f, record.PassiveParameters["Drag"], 5);
        }

        /// <summary>
        /// Since .NET Core 3.0, "nan" and "inf" parse cleanly to NaN and Infinity — a diverged
        /// simulation writes exactly those. Without the IsFinite check, the value would enter
        /// the record as a metric and poison every formula that uses it.
        /// </summary>
        [Theory]
        [InlineData("nan")]
        [InlineData("inf")]
        [InlineData("-inf")]
        [InlineData("broken")]
        [InlineData("")]
        public void An_Unusable_Value_Marks_The_Record_As_Failed(string value)
        {
            string broken =
                "\"Inner_Iter\", \"CD\"\n" +
                "0,             0.500\n" +
                $"1,             {value}\n";

            var solver = new Su2Solver();
            var record = new ModelRecord();

            WithHistoryFile(broken, path => solver.ReadMetricsFromHistory(path, record));

            Assert.True(record.SimulationFailed);
            Assert.False(record.PassiveParameters.ContainsKey("Drag"));
        }

        [Fact]
        public void Diverged_Astronomical_Coefficient_Marks_The_Record_As_Failed()
        {
            string diverged =
                "\"Inner_Iter\", \"CD\"\n" +
                "0,             0.500\n" +
                "1,             1e19\n";

            var solver = new Su2Solver();
            var record = new ModelRecord();

            WithHistoryFile(diverged, path => solver.ReadMetricsFromHistory(path, record));

            Assert.True(record.SimulationFailed);
            Assert.False(record.PassiveParameters.ContainsKey("Drag"));
        }

        [Fact]
        public void A_History_Without_Data_Rows_Marks_The_Record_As_Failed()
        {
            var solver = new Su2Solver();
            var record = new ModelRecord();

            WithHistoryFile("\"Inner_Iter\", \"CD\"\n", path => solver.ReadMetricsFromHistory(path, record));

            Assert.True(record.SimulationFailed);
        }

        [Fact]
        public void A_Missing_History_File_Marks_The_Record_As_Failed()
        {
            var solver = new Su2Solver();
            var record = new ModelRecord();

            solver.ReadMetricsFromHistory(
                Path.Combine(Path.GetTempPath(), "does_not_exist_" + Guid.NewGuid().ToString("N") + ".csv"),
                record);

            Assert.True(record.SimulationFailed);
        }

        [Fact]
        public void An_Empty_Result_Metrics_Mapping_Marks_The_Record_As_Failed()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultMetrics = new Dictionary<string, string>()
            });
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            // Computing without reading anything is always a configuration error.
            Assert.True(record.SimulationFailed);
            Assert.Empty(record.PassiveParameters);
        }

        // -------------------------------------------------------------------
        // CONV_FIELD and HISTORY_OUTPUT
        // -------------------------------------------------------------------

        [Fact]
        public void Convergence_Field_And_History_Output_Reach_The_Generated_Config()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path, "/tmp/mesh.su2", refArea: 0.001f,
                    config: SimulationConfig.CreateDefault(),
                    options: new Su2SolverOptions
                    {
                        ConvergenceField = "LIFT",
                        HistoryOutput = new[] { "ITER", "AERO_COEFF" }
                    });

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("CONV_FIELD= LIFT", lines);
                Assert.Contains("HISTORY_OUTPUT= (ITER, AERO_COEFF)", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// Unconfigured means code default — an empty line would break SU2 on read.
        /// Same pattern as TunnelShape (TODO-11).
        /// </summary>
        [Fact]
        public void Empty_Convergence_Field_And_History_Output_Fall_Back_To_The_Defaults()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path, "/tmp/mesh.su2", refArea: 0.001f,
                    config: SimulationConfig.CreateDefault(),
                    options: new Su2SolverOptions
                    {
                        ConvergenceField = "   ",
                        HistoryOutput = Array.Empty<string>()
                    });

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("CONV_FIELD= DRAG", lines);
                Assert.Contains("HISTORY_OUTPUT= (ITER, RMS_RES, AERO_COEFF)", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// The default case must produce character-for-character the file that was hardcoded
        /// before TODO-26 — otherwise SU2 computes differently than before.
        /// </summary>
        [Fact]
        public void Default_Options_Keep_The_Previously_Hardcoded_Lines()
        {
            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path, "/tmp/mesh.su2", refArea: 0.001f,
                    config: SimulationConfig.CreateDefault(),
                    options: new Su2SolverOptions());

                string[] lines = File.ReadAllLines(path);

                Assert.Contains("CONV_FIELD= DRAG", lines);
                Assert.Contains("HISTORY_OUTPUT= (ITER, RMS_RES, AERO_COEFF)", lines);
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        /// <summary>
        /// The default list must not be shared: otherwise a project editing its own array
        /// mutates the default for all others.
        /// </summary>
        [Fact]
        public void Each_Options_Instance_Gets_Its_Own_History_Output_Array()
        {
            var first = new Su2SolverOptions();
            var second = new Su2SolverOptions();

            first.HistoryOutput[0] = "MUTATED";

            Assert.Equal("ITER", second.HistoryOutput[0]);
            Assert.Equal("ITER", Su2SolverOptions.DefaultHistoryOutput[0]);
        }
    }
}
