using System;
using System.IO;
using Xunit;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-12: The solver no longer hardcodes a metric name. If the configured metric
    /// is missing, it must not silently continue with a fallback area — CD would be
    /// off by orders of magnitude without anyone noticing.
    ///
    /// Warnings are captured via <c>Console.SetOut</c> — process-wide state,
    /// so never run in parallel with other classes (see <see cref="ProcessWideStateCollection"/>).
    /// </summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class Su2ReferenceAreaTests
    {
        /// <summary>Fallback value 1 mm² in m² — same as before TODO-12.</summary>
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
            ModelRecord record = RecordWith("CrossSectionArea", 4000.0f);

            float refArea = new Su2Solver(new Su2SolverOptions { ReferenceAreaMetric = "CrossSectionArea" })
                .ResolveReferenceArea(record);

            Assert.Equal(0.004f, refArea, 9);
        }

        /// <summary>
        /// Exact case from TODO-12: the project names the metric differently from what the solver
        /// is configured for. Previously this passed silently.
        /// </summary>
        [Fact]
        public void Missing_Metric_Warns_And_Falls_Back()
        {
            ModelRecord record = RecordWith("CrossSectionArea", 4000.0f);

            (float refArea, string output) = ResolveAndCaptureConsole(new Su2Solver(), record);

            Assert.Equal(Fallback, refArea, 9);
            Assert.Contains(OutputConventions.WarningTag, output);
            Assert.Contains("FrontalArea", output);
            Assert.Contains("CrossSectionArea", output);   // available metrics are listed
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
            Assert.Contains(OutputConventions.WarningTag, output);
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

        /// <summary>The resolved area lands unchanged as REF_AREA in the SU2 cfg.</summary>
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

        // --- Helper functions -----------------------------------------------

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
