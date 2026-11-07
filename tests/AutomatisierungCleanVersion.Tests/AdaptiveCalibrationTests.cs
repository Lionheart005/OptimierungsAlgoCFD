using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using MyPicoGkProject.Core;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>TODO-W5: Die Kalibrierung verfeinert weiter, bis die Toleranz hält (oder eine Grenze greift).</summary>
    [Collection(ProcessWideStateCollection.Name)]
    public class AdaptiveCalibrationTests
    {
        [Fact]
        public void Refinement_Factors_Continue_By_Sqrt2()
        {
            Assert.Equal(0.35f, MeshCalibration.NextFinerFactor(0.5f));
            Assert.Equal(0.25f, MeshCalibration.NextFinerFactor(0.35f));
            Assert.Equal(0.18f, MeshCalibration.NextFinerFactor(0.25f));
        }

        [Fact]
        public void Cell_Estimate_Uses_The_Dimension()
        {
            Assert.Equal(16000L, MeshCalibration.EstimateCells(1000, 2.0f, 0.5f, 2));
            Assert.Equal(64000L, MeshCalibration.EstimateCells(1000, 2.0f, 0.5f, 3));
        }

        private sealed class Scenario : IDisposable
        {
            public string Dir = Path.Combine(Path.GetTempPath(), "adaptcal_" + Guid.NewGuid().ToString("N"));
            public List<float> Factors = new();
            public ProjectContext Context;
            public SimulationContext SimContext;

            public Scenario(Action<SimulationConfig>? configure = null)
            {
                Directory.CreateDirectory(Dir);
                var sim = SimulationConfig.CreateDefault();
                sim.MeshCalibration.Tolerance = 0.02f;
                configure?.Invoke(sim);
                var proj = new ProjectConfig { ProjectName = "T" };
                Context = new ProjectContext("T", Dir, null, sim, proj);
                SimContext = new SimulationContext(sim, proj, Dir);
            }

            public void Dispose()
            {
                if (Directory.Exists(Dir)) Directory.Delete(Dir, true);
            }

            /// <summary>Drag(f) = d∞ + a·f^p: konvergiert erst bei kleinem f.</summary>
            public Func<float, string, ModelRecord> Runner(double dInf, double a, double p, double cells0, bool converged = true)
                => (factor, tag) =>
                {
                    Factors.Add(factor);
                    var rec = new ModelRecord();
                    rec.PassiveParameters["MeshCells"] = (float)(cells0 / (factor * factor));
                    rec.PassiveParameters["Drag"] = (float)(dInf + a * Math.Pow(factor, p));
                    rec.PassiveParameters["YPlus_P95"] = 1.0f;
                    rec.PassiveParameters["Su2_Converged"] = converged ? 1f : 0f;
                    return rec;
                };
        }

        [Fact]
        public void Refines_Beyond_Factor_Half_Until_Tolerance_Holds()
        {
            // Servertest TMR alpha 10: CD fällt von Stufe zu Stufe um 20 %, 0,5 reicht nicht.
            using var s = new Scenario();
            // Verfahren 2. Ordnung d = 0.03 + 0.01 * f²: bei f=0.5 reicht die Toleranz noch nicht,
            // ab Referenz 0.25 hält die Stufe 0.35.
            var run = s.Runner(0.03, 0.01, 2.0, 20000);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.Contains(0.35f, s.Factors);
            Assert.True(s.Factors.Distinct().Count() > 5, "Es müssen zusätzliche Stufen gerechnet worden sein.");
            Assert.True(decision.AllStages.Count > 5);
            Assert.True(decision.Success, decision.SummaryMessage);
            // Die feinste Stufe ist die Referenz, und die gewählte liegt gröber.
            Assert.True(decision.SelectedStage!.Factor > decision.ReferenceStage.Factor);
            Assert.NotNull(decision.NormalizedDeviationsToRef);
        }

        [Fact]
        public void Does_Not_Refine_When_The_Standard_Stages_Already_Pass()
        {
            using var s = new Scenario();
            var run = s.Runner(0.0300, 0.0002, 1.0, 20000); // fast flach

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.True(decision.Success);
            Assert.DoesNotContain(s.Factors, f => f < 0.5f);
        }

        [Fact]
        public void Stops_At_The_Cell_Limit_And_Reports_Failure()
        {
            using var s = new Scenario(cfg => cfg.MeshCalibration.MaxCells2D = 100_000);
            // Faktor 0.5 hat 80 000 Zellen, 0.35 hätte ~163 000 > 100 000.
            var run = s.Runner(0.012, 0.03, 1.0, 20000);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.DoesNotContain(s.Factors, f => f < 0.5f);
            Assert.False(decision.Success);
        }

        [Fact]
        public void Does_Not_Refine_When_The_Finest_Stage_Is_Not_Converged()
        {
            // Nicht auskonvergiert heißt: mehr Iterationen, nicht feineres Netz.
            using var s = new Scenario();
            var run = s.Runner(0.012, 0.03, 1.0, 20000, converged: false);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.DoesNotContain(s.Factors, f => f < 0.5f);
            Assert.False(decision.Success);
        }

        [Fact]
        public void Two_Dimensional_Estimate_Does_Not_Block_A_Small_Case()
        {
            // 3D hätte 64 × 20 000 / 4 > Grenze 50 000 → Abbruch; in 2D (16 ×) geht es durch.
            using var s = new Scenario(cfg => cfg.MeshCalibration.MaxCells2D = 400_000);
            var run = s.Runner(0.0300, 0.0002, 1.0, 20000);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.True(decision.Success);
        }

        [Fact]
        public void RefineAdaptively_Discards_Failed_Extra_Stage_Keeping_Last_Valid_As_Reference()
        {
            // B15a: Stufe 6 (0.35) konvergiert, Stufe 7 (0.25) schlägt fehl (z.B. Gmsh/SU2 SimulationFailed).
            // Stufe 7 darf nicht in stages aufgenommen werden; Stufe 6 bleibt Referenzstufe und FallbackStage ist Stufe 5 (0.50).
            using var s = new Scenario();
            Func<float, string, ModelRecord> runner = (factor, tag) =>
            {
                s.Factors.Add(factor);
                var rec = new ModelRecord();
                rec.PassiveParameters["MeshCells"] = (float)(20000.0 / (factor * factor));
                rec.PassiveParameters["Drag"] = (float)(0.03 + 0.05 * factor); // Weite Abweichung, Toleranz hält noch nicht bei 0.35 vs 0.50
                rec.PassiveParameters["YPlus_P95"] = 1.0f;

                if (Math.Abs(factor - 0.25f) < 0.01f)
                {
                    // Stufe 7 schlägt fehl (z.B. Gmsh-Netzfehler oder SU2-Abbruch)
                    rec.SimulationFailed = true;
                    rec.PassiveParameters["MeshCells"] = 0f;
                }
                else
                {
                    rec.PassiveParameters["Su2_Converged"] = 1f;
                }
                return rec;
            };

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, runner, new[] { "Drag" }, dimensions: 2);

            // Stufe 7 (0.25) wurde versucht
            Assert.Contains(0.25f, s.Factors);
            // Stufe 7 wurde aber verworfen und ist nicht in AllStages
            Assert.DoesNotContain(decision.AllStages, st => Math.Abs(st.Factor - 0.25f) < 0.01f);
            // Stufe 6 (0.35) ist die Referenzstufe
            Assert.Equal(0.35f, decision.ReferenceStage.Factor);
            Assert.True(decision.ReferenceStage.Converged);
            Assert.True(decision.ReferenceStage.YPlusOk);
            // Da die Toleranz bei Stufe 6 noch nicht erreicht wurde, existiert eine FallbackStage (Stufe 5 mit 0.50f)
            Assert.NotNull(decision.FallbackStage);
            Assert.Equal(0.50f, decision.FallbackStage!.Factor);
        }
    }
}
