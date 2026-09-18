using System;
using System.IO;
using Xunit;
using MyPicoGkProject;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-12: Der Solver kennt keinen festen Metriknamen mehr. Fehlt die konfigurierte
    /// Metrik, darf er nicht stillschweigend mit einer Ersatzfläche weiterrechnen — der
    /// CD-Wert wäre dann um Größenordnungen falsch, ohne dass es jemand merkt.
    /// </summary>
    public class Su2ReferenceAreaTests
    {
        /// <summary>Ersatzwert 1 mm² in m² — derselbe wie vor TODO-12.</summary>
        private const float Fallback = 0.000001f;

        [Fact]
        public void FrontalArea_Is_The_Default_And_Is_Converted_From_Mm2_To_M2()
        {
            ModelRecord record = RecordWith("FrontalArea", 2500.0f);

            float refArea = new Su2Solver().ResolveReferenceArea(record);

            Assert.Equal(0.0025f, refArea, 9);
        }

        [Fact]
        public void A_Project_Can_Name_Its_Own_Metric()
        {
            ModelRecord record = RecordWith("Spantflaeche", 4000.0f);

            float refArea = new Su2Solver(new Su2SolverOptions { ReferenceAreaMetric = "Spantflaeche" })
                .ResolveReferenceArea(record);

            Assert.Equal(0.004f, refArea, 9);
        }

        /// <summary>
        /// Genau der Fall aus TODO-12: das Projekt nennt die Metrik anders, als der Solver
        /// konfiguriert ist. Vorher lief das still durch.
        /// </summary>
        [Fact]
        public void Missing_Metric_Warns_And_Falls_Back()
        {
            ModelRecord record = RecordWith("Spantflaeche", 4000.0f);

            (float refArea, string output) = ResolveAndCaptureConsole(new Su2Solver(), record);

            Assert.Equal(Fallback, refArea, 9);
            Assert.Contains("[WARNUNG]", output);
            Assert.Contains("FrontalArea", output);
            Assert.Contains("Spantflaeche", output);   // die vorhandenen Metriken werden genannt
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Unconfigured_Metric_Warns_And_Falls_Back(string? metric)
        {
            ModelRecord record = RecordWith("FrontalArea", 2500.0f);
            Su2Solver solver = new Su2Solver(new Su2SolverOptions { ReferenceAreaMetric = metric! });

            (float refArea, string output) = ResolveAndCaptureConsole(solver, record);

            Assert.Equal(Fallback, refArea, 9);
            Assert.Contains("[WARNUNG]", output);
        }

        [Fact]
        public void Metric_Name_Tolerates_Surrounding_Whitespace()
        {
            ModelRecord record = RecordWith("FrontalArea", 2500.0f);

            float refArea = new Su2Solver(new Su2SolverOptions { ReferenceAreaMetric = "  FrontalArea " })
                .ResolveReferenceArea(record);

            Assert.Equal(0.0025f, refArea, 9);
        }

        [Fact]
        public void Record_Without_Any_Metric_Still_Names_The_Config_Key()
        {
            (float refArea, string output) = ResolveAndCaptureConsole(new Su2Solver(), new ModelRecord());

            Assert.Equal(Fallback, refArea, 9);
            Assert.Contains("ReferenceAreaMetric", output);
        }

        /// <summary>Die aufgelöste Fläche landet unverändert als REF_AREA in der SU2-cfg.</summary>
        [Fact]
        public void Resolved_Area_Reaches_The_Generated_Config()
        {
            float refArea = new Su2Solver().ResolveReferenceArea(RecordWith("FrontalArea", 2500.0f));

            string path = Path.Combine(Path.GetTempPath(), "su2cfg_" + Guid.NewGuid().ToString("N") + ".cfg");
            try
            {
                Su2ConfigGenerator.Generate(
                    path, "/tmp/mesh.su2", refArea, SimulationConfig.CreateDefault(), new Su2SolverOptions());

                Assert.Contains("REF_AREA= 0.0025", File.ReadAllLines(path));
            }
            finally
            {
                if (File.Exists(path)) File.Delete(path);
            }
        }

        // --- Hilfsfunktionen ------------------------------------------------

        private static ModelRecord RecordWith(string metric, float valueMm2)
        {
            ModelRecord record = new ModelRecord();
            record.PassiveParameters[metric] = valueMm2;
            return record;
        }

        private static (float RefArea, string Output) ResolveAndCaptureConsole(Su2Solver solver, ModelRecord record)
        {
            TextWriter previous = Console.Out;
            StringWriter captured = new StringWriter();

            try
            {
                Console.SetOut(captured);
                float refArea = solver.ResolveReferenceArea(record);
                return (refArea, captured.ToString());
            }
            finally
            {
                Console.SetOut(previous);
            }
        }
    }
}
