using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Xunit;

using FluidForge.Core;

namespace FluidForge.Tests
{
    /// <summary>TODO-W5: Calibration refines further until tolerance holds (or a limit is reached).</summary>
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

            /// <summary>Drag(f) = d∞ + a·f^p: converges only at small f.</summary>
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
            // Server test TMR alpha 10: CD drops by 20% from stage to stage, 0.5 is not enough.
            using var s = new Scenario();
            // 2nd order method d = 0.03 + 0.01 * f²: at f=0.5 tolerance does not hold yet,
            // from reference 0.25 stage 0.35 holds.
            var run = s.Runner(0.03, 0.01, 2.0, 20000);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.Contains(0.35f, s.Factors);
            Assert.True(s.Factors.Distinct().Count() > 5, "Additional stages must have been computed.");
            Assert.True(decision.AllStages.Count > 5);
            Assert.True(decision.Success, decision.SummaryMessage);
            // The finest stage is the reference, and the selected stage is coarser.
            Assert.True(decision.SelectedStage!.Factor > decision.ReferenceStage.Factor);
            Assert.NotNull(decision.NormalizedDeviationsToRef);
        }

        [Fact]
        public void Does_Not_Refine_When_The_Standard_Stages_Already_Pass()
        {
            using var s = new Scenario();
            var run = s.Runner(0.0300, 0.0002, 1.0, 20000); // nearly flat

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.True(decision.Success);
            Assert.DoesNotContain(s.Factors, f => f < 0.5f);
        }

        [Fact]
        public void Stops_At_The_Cell_Limit_And_Reports_Failure()
        {
            using var s = new Scenario(cfg => cfg.MeshCalibration.MaxCells2D = 100_000);
            // Factor 0.5 has 80,000 cells, 0.35 would have ~163,000 > 100,000.
            var run = s.Runner(0.012, 0.03, 1.0, 20000);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.DoesNotContain(s.Factors, f => f < 0.5f);
            Assert.False(decision.Success);
        }

        [Fact]
        public void Does_Not_Refine_When_The_Finest_Stage_Is_Not_Converged()
        {
            // Not converged means: more iterations, not finer mesh.
            using var s = new Scenario();
            var run = s.Runner(0.012, 0.03, 1.0, 20000, converged: false);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.DoesNotContain(s.Factors, f => f < 0.5f);
            Assert.False(decision.Success);
        }

        [Fact]
        public void Two_Dimensional_Estimate_Does_Not_Block_A_Small_Case()
        {
            // 3D would have 64 * 20,000 / 4 > limit 50,000 -> abort; in 2D (16x) it passes.
            using var s = new Scenario(cfg => cfg.MeshCalibration.MaxCells2D = 400_000);
            var run = s.Runner(0.0300, 0.0002, 1.0, 20000);

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, run, new[] { "Drag" }, dimensions: 2);

            Assert.True(decision.Success);
        }

        [Fact]
        public void RefineAdaptively_Discards_Failed_Extra_Stage_Keeping_Last_Valid_As_Reference()
        {
            // B15a: Stage 6 (0.35) converges, stage 7 (0.25) fails (e.g. Gmsh/SU2 SimulationFailed).
            // Stage 7 must not be included in stages; stage 6 remains reference stage and FallbackStage is stage 5 (0.50).
            using var s = new Scenario();
            Func<float, string, ModelRecord> runner = (factor, tag) =>
            {
                s.Factors.Add(factor);
                var rec = new ModelRecord();
                rec.PassiveParameters["MeshCells"] = (float)(20000.0 / (factor * factor));
                rec.PassiveParameters["Drag"] = (float)(0.03 + 0.05 * factor); // Wide deviation, tolerance does not hold yet at 0.35 vs 0.50
                rec.PassiveParameters["YPlus_P95"] = 1.0f;

                if (Math.Abs(factor - 0.25f) < 0.01f)
                {
                    // Stage 7 fails (e.g. Gmsh mesh error or SU2 abort)
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

            // Stage 7 (0.25) was attempted
            Assert.Contains(0.25f, s.Factors);
            // Stage 7 was discarded and is not in AllStages
            Assert.DoesNotContain(decision.AllStages, st => Math.Abs(st.Factor - 0.25f) < 0.01f);
            // Stage 6 (0.35) is the reference stage
            Assert.Equal(0.35f, decision.ReferenceStage.Factor);
            Assert.True(decision.ReferenceStage.Converged);
            Assert.True(decision.ReferenceStage.YPlusOk);
            // Since tolerance was not yet met at stage 6, a FallbackStage exists (stage 5 with 0.50f)
            Assert.NotNull(decision.FallbackStage);
            Assert.Equal(0.50f, decision.FallbackStage!.Factor);
        }

        [Fact]
        public void Run_DoesNotSkipCal2_AndRejectsCoincidentalCal1_WhenCal2ExceedsTolerance()
        {
            using var s = new Scenario(cfg => cfg.MeshCalibration.Tolerance = 0.02f);
            Func<float, string, ModelRecord> runner = (factor, tag) =>
            {
                s.Factors.Add(factor);
                var rec = new ModelRecord();
                rec.PassiveParameters["MeshCells"] = (float)(20000.0 / (factor * factor));
                rec.PassiveParameters["YPlus_P95"] = 1.0f;
                rec.PassiveParameters["Su2_Converged"] = 1f;
                rec.PassiveParameters["Drag"] = factor switch
                {
                    > 1.8f => 0.0401f, // Cal1 (2.00): coincidental match
                    > 1.2f => 0.0460f, // Cal2 (1.41): outlier (+15%)
                    > 0.9f => 0.0404f, // Cal3 (1.00): within 2%
                    > 0.6f => 0.0402f, // Cal4 (0.71): within 2%
                    _ => 0.0400f       // Cal5 (0.50): reference
                };
                return rec;
            };

            var (decision, _) = MeshCalibration.Run(s.Context, s.SimContext, runner, new[] { "Drag" }, dimensions: 2);

            Assert.Contains(1.41f, s.Factors);
            Assert.True(decision.Success, decision.SummaryMessage);
            Assert.NotNull(decision.SelectedStage);
            Assert.Equal(1.00f, decision.SelectedStage!.Factor);
            Assert.Equal(3, decision.SelectedStage.LevelIndex);
            Assert.Contains("non-monotonic", decision.SummaryMessage);
        }
    }
}
