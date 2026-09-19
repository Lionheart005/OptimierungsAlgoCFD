using System;
using System.Collections.Generic;
using System.IO;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-26: SU2 liefert nicht mehr nur Drag. Welche Spalte der history.csv unter welchem
    /// Namen in den Record wandert, steht in <see cref="Su2SolverOptions.ResultMetrics"/>.
    ///
    /// Die Tests arbeiten auf einer geschriebenen Beispiel-history.csv statt auf einem echten
    /// Lauf — MPI und SU2 gibt es auf dem Entwicklungsrechner nicht.
    /// </summary>
    public class Su2ResultMetricsTests
    {
        /// <summary>
        /// So sieht eine history.csv von SU2 aus: Spaltennamen in Anführungszeichen und mit
        /// Leerzeichen ausgerichtet, die interessante Zeile ist die letzte.
        /// </summary>
        private const string HistoryWithAeroCoefficients =
            "\"Inner_Iter\",   \"rms[Rho]\",       \"CD\",       \"CL\",      \"CSF\",      \"CMx\",      \"CMy\",      \"CMz\"\n" +
            "0,              -1.234,           0.900,      0.100,     0.010,     0.001,     0.002,     0.003\n" +
            "1,              -3.456,           0.500,      0.200,     0.020,     0.004,     0.005,     0.006\n" +
            "2,              -6.789,           0.345,      0.678,     0.031,     0.007,     0.123,     0.009\n";

        /// <summary>Schreibt eine history.csv in den Temp-Ordner und räumt sie hinterher weg.</summary>
        private static void WithHistoryFile(string content, Action<string> body)
        {
            string path = Path.Combine(Path.GetTempPath(), "history_" + Guid.NewGuid().ToString("N") + ".csv");
            File.WriteAllText(path, content);

            try { body(path); }
            finally { if (File.Exists(path)) File.Delete(path); }
        }

        // -------------------------------------------------------------------
        // Standardverhalten — ein bestehendes Projekt darf vom Umbau nichts merken
        // -------------------------------------------------------------------

        [Fact]
        public void Default_Options_Read_Exactly_Drag_From_Cd()
        {
            var solver = new Su2Solver();
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            // Genau eine Größe, aus der letzten Zeile — und kein "Lift", das keiner bestellt hat.
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
        // Mehrere Größen gleichzeitig
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
                    ["Nickmoment"] = "CMy"
                }
            });
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            Assert.Equal(0.345f, record.PassiveParameters["Drag"], 5);
            Assert.Equal(0.678f, record.PassiveParameters["Lift"], 5);
            Assert.Equal(0.123f, record.PassiveParameters["Nickmoment"], 5);
            Assert.False(record.SimulationFailed);
        }

        // -------------------------------------------------------------------
        // Der eigentliche Grund für den exakten Vergleich (TODO-26)
        // -------------------------------------------------------------------

        /// <summary>
        /// Vor TODO-26 suchte der Solver per StartsWith. "CM" hätte damit CMx, CMy oder CMz
        /// getroffen — welche, hing allein an der Spaltenreihenfolge.
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
        /// Der Präfix-Weg bleibt erhalten — SU2 schreibt je nach Version und MARKER_MONITORING
        /// auch Namen wie CD(Wall). Er greift aber nur bei Eindeutigkeit und meldet sich.
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
        // Fehlerfälle: lieber ausgemustert als halb gerechnet
        // -------------------------------------------------------------------

        [Fact]
        public void Configured_But_Missing_Column_Marks_The_Record_As_Failed()
        {
            var solver = new Su2Solver(new Su2SolverOptions
            {
                ResultMetrics = new Dictionary<string, string>
                {
                    ["Drag"] = "CD",
                    ["Auftrieb"] = "CL_gibt_es_nicht"
                }
            });
            var record = new ModelRecord();

            WithHistoryFile(HistoryWithAeroCoefficients, path => solver.ReadMetricsFromHistory(path, record));

            Assert.True(record.SimulationFailed);
            Assert.False(record.PassiveParameters.ContainsKey("Auftrieb"));
            // Die lesbaren Größen werden trotzdem übernommen — sie stehen später in der CSV
            // und helfen bei der Fehlersuche.
            Assert.Equal(0.345f, record.PassiveParameters["Drag"], 5);
        }

        /// <summary>
        /// "nan" und "inf" parst .NET seit Core 3.0 klaglos zu NaN bzw. Infinity — eine
        /// divergierte Simulation schreibt genau das. Ohne die IsFinite-Prüfung stünde der
        /// Wert als Metrik im Record und vergiftete jede Formel, die damit rechnet.
        /// </summary>
        [Theory]
        [InlineData("nan")]
        [InlineData("inf")]
        [InlineData("-inf")]
        [InlineData("kaputt")]
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
                Path.Combine(Path.GetTempPath(), "gibt_es_nicht_" + Guid.NewGuid().ToString("N") + ".csv"),
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

            // Rechnen und nichts auslesen ist immer ein Konfigurationsfehler.
            Assert.True(record.SimulationFailed);
            Assert.Empty(record.PassiveParameters);
        }

        // -------------------------------------------------------------------
        // CONV_FIELD und HISTORY_OUTPUT
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
        /// Nicht konfiguriert heißt Code-Standard — eine leere Zeile würde SU2 beim Einlesen
        /// zerlegen. Gleiches Muster wie bei TunnelShape (TODO-11).
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
        /// Der Standardfall muss Zeichen für Zeichen die Datei erzeugen, die vor TODO-26
        /// hartcodiert dort stand — sonst rechnet SU2 anders als bisher.
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
        /// Die Standard-Liste darf nicht geteilt werden: sonst verstellt ein Projekt, das
        /// sein eigenes Array bearbeitet, die Vorgabe aller anderen.
        /// </summary>
        [Fact]
        public void Each_Options_Instance_Gets_Its_Own_History_Output_Array()
        {
            var first = new Su2SolverOptions();
            var second = new Su2SolverOptions();

            first.HistoryOutput[0] = "VERSTELLT";

            Assert.Equal("ITER", second.HistoryOutput[0]);
            Assert.Equal("ITER", Su2SolverOptions.DefaultHistoryOutput[0]);
        }
    }
}
