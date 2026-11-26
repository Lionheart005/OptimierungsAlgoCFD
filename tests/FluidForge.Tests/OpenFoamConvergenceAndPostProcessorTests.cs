using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidForge.Solvers.OpenFoam;
using Xunit;

namespace FluidForge.Tests
{
    public sealed class OpenFoamConvergenceAndPostProcessorTests : TempDirectoryTest
    {
        [Fact]
        public void ConvergenceJudge_Filters_By_ResultMetrics_In_External3D_And_Internal3D()
        {
            // External3D: ResultMetrics = Drag only -> PressureDrop drift & MassBalanceError must NOT fail
            var extSeries = new Dictionary<string, IReadOnlyList<float>>
            {
                ["Drag"] = Enumerable.Repeat(0.25f, 30).ToArray(),
                ["PressureDrop"] = Enumerable.Range(1, 30).Select(i => (float)i * 10f).ToArray()
            };
            var extPost = new OpenFoamPostProcessResult(
                Metrics: new Dictionary<string, float> { ["Drag"] = 0.25f, ["PressureDrop"] = 300f, ["MassBalanceError"] = 5.0f },
                MonitoredSeries: extSeries,
                YPlusMed: 4f,
                YPlusP95: 5f,
                YPlusMax: 6f,
                MassBalanceErrorPercent: 5.0f,
                FlowImbalancePercent: null,
                InletSpeedMs: 1.5f,
                HasNonFiniteValues: false);

            var extOpts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };

            OpenFoamConvergenceVerdict extVerdict = OpenFoamConvergenceJudge.Judge(
                "Time = 300\nSIMPLE solution converged in 300 iterations\n", extPost, extOpts);
            Assert.True(extVerdict.MassBalanceOk);
            Assert.True(extVerdict.Converged, extVerdict.Summary);

            // Internal3D: ResultMetrics = PressureDrop only -> Lift drift does NOT fail, MassBalanceError > 1% DOES fail
            var intSeries = new Dictionary<string, IReadOnlyList<float>>
            {
                ["PressureDrop"] = Enumerable.Repeat(150f, 30).ToArray(),
                ["Lift"] = Enumerable.Range(1, 30).Select(i => (float)i * 0.5f).ToArray()
            };
            var intPostOk = new OpenFoamPostProcessResult(
                Metrics: new Dictionary<string, float> { ["PressureDrop"] = 150f, ["Lift"] = 15f, ["MassBalanceError"] = 0.2f },
                MonitoredSeries: intSeries,
                YPlusMed: 4f,
                YPlusP95: 5f,
                YPlusMax: 6f,
                MassBalanceErrorPercent: 0.2f,
                FlowImbalancePercent: 0f,
                InletSpeedMs: 2.0f,
                HasNonFiniteValues: false);

            var intOpts = new OpenFoamOptions
            {
                DomainMode = "Internal3D",
                MaxMassBalanceErrorPercent = 1.0f,
                ResultMetrics = new Dictionary<string, string> { ["PressureDrop"] = "PressureDrop" }
            };

            OpenFoamConvergenceVerdict intVerdictOk = OpenFoamConvergenceJudge.Judge(
                "Time = 300\nSIMPLE solution converged in 300 iterations\n", intPostOk, intOpts);
            Assert.True(intVerdictOk.MassBalanceOk);
            Assert.True(intVerdictOk.Converged, intVerdictOk.Summary);

            var intPostBadMb = intPostOk with { MassBalanceErrorPercent = 2.5f };
            OpenFoamConvergenceVerdict intVerdictBad = OpenFoamConvergenceJudge.Judge(
                "Time = 300\nSIMPLE solution converged in 300 iterations\n", intPostBadMb, intOpts);
            Assert.False(intVerdictBad.MassBalanceOk);
            Assert.False(intVerdictBad.Converged);
        }

        [Fact]
        public void ConvergenceJudge_Fails_When_Configured_ResultMetrics_Series_Is_Missing()
        {
            var post = new OpenFoamPostProcessResult(
                Metrics: new Dictionary<string, float> { ["PressureDrop"] = 100f },
                MonitoredSeries: new Dictionary<string, IReadOnlyList<float>>
                {
                    ["PressureDrop"] = Enumerable.Repeat(100f, 20).ToArray()
                },
                YPlusMed: null,
                YPlusP95: null,
                YPlusMax: null,
                MassBalanceErrorPercent: 0.1f,
                FlowImbalancePercent: 0f,
                InletSpeedMs: 1.0f,
                HasNonFiniteValues: false);

            var opts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };

            OpenFoamConvergenceVerdict verdict = OpenFoamConvergenceJudge.Judge("Time = 200\n", post, opts);
            Assert.False(verdict.Converged);
            Assert.Contains("None of the configured ResultMetrics", verdict.Summary, StringComparison.Ordinal);
        }

        [Fact]
        public void ConvergenceJudge_Applies_MetricTolerance_Atol_For_Symmetric_Body_Near_Zero_Lift()
        {
            // Cl oscillates around ~1e-6 with small numerical noise; relative drift would be huge without atol = 5e-3.
            var liftSeries = Enumerable.Range(0, 40)
                .Select(i => i < 20 ? 1e-6f : -2e-6f)
                .ToArray();

            var post = new OpenFoamPostProcessResult(
                Metrics: new Dictionary<string, float> { ["Lift"] = -2e-6f },
                MonitoredSeries: new Dictionary<string, IReadOnlyList<float>> { ["Lift"] = liftSeries },
                YPlusMed: null,
                YPlusP95: null,
                YPlusMax: null,
                MassBalanceErrorPercent: null,
                FlowImbalancePercent: null,
                InletSpeedMs: null,
                HasNonFiniteValues: false);

            var opts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                ResultMetrics = new Dictionary<string, string> { ["Lift"] = "Lift" }
            };

            OpenFoamConvergenceVerdict verdict = OpenFoamConvergenceJudge.Judge("Time = 200\n", post, opts);
            Assert.True(verdict.Converged, verdict.Summary);
            Assert.False(verdict.HasMonotonicDrift);
        }

        [Fact]
        public void ConvergenceJudge_Detects_Stationary_Oscillation_And_Allows_Slightly_Elevated_Residuals()
        {
            // 60 samples with TrailingWindowIterations=60 -> ResolveEffectiveTailCount = 20 >= 16.
            // Pure sine around 0.25 with amplitude 0.05 -> mean1 != mean2 slightly, but no monotonic trend.
            var dragSeries = Enumerable.Range(0, 60)
                .Select(i => 0.25f + 0.05f * MathF.Sin(2f * MathF.PI * i / 7f))
                .ToArray();

            var post = new OpenFoamPostProcessResult(
                Metrics: new Dictionary<string, float> { ["Drag"] = 0.25f },
                MonitoredSeries: new Dictionary<string, IReadOnlyList<float>> { ["Drag"] = dragSeries },
                YPlusMed: null,
                YPlusP95: null,
                YPlusMax: null,
                MassBalanceErrorPercent: null,
                FlowImbalancePercent: null,
                InletSpeedMs: null,
                HasNonFiniteValues: false);

            var opts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                TrailingWindowIterations = 60,
                ResidualTarget = 1e-4f,
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };

            // Slightly elevated residuals (5e-4 > 1e-4, but <= 1e-3 and <= max(100*1e-4, 0.1))
            string log = "Time = 600\nSolving for p, Initial residual = 0.01, Final residual = 5e-4\n";
            OpenFoamConvergenceVerdict verdict = OpenFoamConvergenceJudge.Judge(log, post, opts);

            Assert.True(verdict.Converged, verdict.Summary);
            Assert.True(verdict.IsUnsteadyStationary);
            Assert.False(verdict.HasMonotonicDrift);
            Assert.NotNull(verdict.MetricUncertainties);
            Assert.True(verdict.MetricUncertainties!.ContainsKey("Drag"));
            Assert.True(verdict.MetricUncertainties["Drag"] > 0f);
        }

        [Fact]
        public void ConvergenceJudge_Detects_Monotonic_Drift_Ramp_And_Populates_DriftingMetrics()
        {
            var ramp = Enumerable.Range(0, 60)
                .Select(i => 0.20f + 0.005f * i)
                .ToArray();

            var post = new OpenFoamPostProcessResult(
                Metrics: new Dictionary<string, float> { ["Drag"] = ramp[^1] },
                MonitoredSeries: new Dictionary<string, IReadOnlyList<float>> { ["Drag"] = ramp },
                YPlusMed: null,
                YPlusP95: null,
                YPlusMax: null,
                MassBalanceErrorPercent: null,
                FlowImbalancePercent: null,
                InletSpeedMs: null,
                HasNonFiniteValues: false);

            var opts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                TrailingWindowIterations = 60,
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };

            OpenFoamConvergenceVerdict verdict = OpenFoamConvergenceJudge.Judge("Time = 600\n", post, opts);
            Assert.False(verdict.Converged);
            Assert.True(verdict.HasMonotonicDrift);
            Assert.False(verdict.IsUnsteadyStationary);
            Assert.NotNull(verdict.DriftingMetrics);
            Assert.Contains("Drag", verdict.DriftingMetrics!);
        }

        [Fact]
        public void PostProcessor_Merges_Restart_Folders_Chronologically_With_Overlap_And_Empty_YPlus_Guard()
        {
            string caseDir = Sub("post_restart_merge");

            // forceCoeffs across 0/, 178/, 350/ with overlapping timestamps (later folder wins)
            WriteFile(Path.Combine(caseDir, "postProcessing", "forceCoeffs", "0", "coefficient.dat"),
                "# Time Cd Cl\n100 0.90 0.01\n178 0.80 0.02\n200 0.77 0.03\n");
            WriteFile(Path.Combine(caseDir, "postProcessing", "forceCoeffs", "178", "coefficient.dat"),
                "# Time Cd Cl\n178 0.50 0.04\n300 0.40 0.05\n350 0.35 0.06\n");
            WriteFile(Path.Combine(caseDir, "postProcessing", "forceCoeffs", "350", "coefficient.dat"),
                "# Time Cd Cl\n350 0.30 0.07\n400 0.25 0.08\n");

            // yPlus: 0/ has valid data, 178/ has empty/header-only file -> must NOT erase 0/ values
            WriteFile(Path.Combine(caseDir, "postProcessing", "yPlus", "0", "yPlus.dat"),
                "# Time patch min max average\n178 wall 0.5 8.0 4.0\n");
            WriteFile(Path.Combine(caseDir, "postProcessing", "yPlus", "178", "yPlus.dat"),
                "# Time patch min max average\n");

            var opts = new OpenFoamOptions { DomainMode = "External3D", TrailingWindowIterations = 100 };
            OpenFoamPostProcessResult res = OpenFoamPostProcessor.Evaluate(caseDir, opts);

            Assert.True(res.MonitoredSeries.TryGetValue("Drag", out IReadOnlyList<float>? cdSeries));
            // Expected merged points at times: 100 (0.90), 178 (0.50 from 178/), 300 (0.40), 350 (0.30 from 350/), 400 (0.25)
            Assert.Equal(5, cdSeries!.Count);
            Assert.Equal(0.90f, cdSeries[0], 3);
            Assert.Equal(0.50f, cdSeries[1], 3);
            Assert.Equal(0.40f, cdSeries[2], 3);
            Assert.Equal(0.30f, cdSeries[3], 3);
            Assert.Equal(0.25f, cdSeries[4], 3);

            Assert.Equal(4.0f, res.YPlusMed!.Value, 3);
            Assert.Equal(8.0f, res.YPlusMax!.Value, 3);
        }

        private static void WriteFile(string path, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
        }
    }
}
