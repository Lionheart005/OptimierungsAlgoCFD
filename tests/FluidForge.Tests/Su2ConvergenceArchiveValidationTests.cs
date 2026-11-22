using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;
using Xunit;
using Xunit.Abstractions;

namespace FluidForge.Tests
{
    /// <summary>
    /// Validates preset-tiered Cauchy stopping criteria (<c>5e-5</c> for StandardRans,
    /// <c>1e-4</c> for FastConcept with <c>AutoRestartOnDrift</c> stationarity guard)
    /// against archived 3D <c>MantaAuv</c> SU2 convergence histories.
    /// </summary>
    public class Su2ConvergenceArchiveValidationTests
    {
        private readonly ITestOutputHelper _output;

        public Su2ConvergenceArchiveValidationTests(ITestOutputHelper output) => _output = output;

        [Fact]
        public void Preset_Cauchy_Tolerances_Reduce_Iterations_With_Low_Cd_Error_On_MantaAuv_Archive()
        {
            string? repoRoot = ProjectPaths.ResolveRepositoryRoot();
            string? archiveDir = repoRoot != null
                ? Path.Combine(repoRoot, "Serverergebnisse", "BIC_12_MantaAuv_20261121_Archiv", "MantaAuv", "Analyseergebnisse", "Verlaeufe")
                : null;

            List<(string Name, string[] Lines)> histories = LoadArchived5kHistories(archiveDir);
            bool usingRealArchive = archiveDir != null && Directory.Exists(archiveDir) && histories.Count > 0;
            if (histories.Count == 0)
            {
                histories = BuildSyntheticFallbackHistories();
            }

            const int cauchyElems = 300;
            var historyOptions = new Su2HistoryOptions(5000, cauchyElems, new[] { "CD" });

            double sumStop1e5 = 0, sumStop5e5 = 0, sumStop1e4 = 0;
            double sumRawTotal = 0;
            var err5e5 = new List<double>();
            var err1e4 = new List<double>();
            var err1e4Guarded = new List<double>();
            var stop1e4Guarded = new List<int>();

            foreach (var (name, lines) in histories)
            {
                CauchyStopSimulation sim1e5 = Su2HistoryAnalyzer.SimulateCauchyStop(lines, cauchyElems, 1e-5, "CD", startIter: 100);
                CauchyStopSimulation sim5e5 = Su2HistoryAnalyzer.SimulateCauchyStop(lines, cauchyElems, 5e-5, "CD", startIter: 100);
                CauchyStopSimulation sim1e4 = Su2HistoryAnalyzer.SimulateCauchyStop(lines, cauchyElems, 1e-4, "CD", startIter: 100);

                sumRawTotal += sim1e5.TotalIterations;
                sumStop1e5 += sim1e5.StopIteration;
                sumStop5e5 += sim5e5.StopIteration;
                sumStop1e4 += sim1e4.StopIteration;

                err5e5.Add(sim5e5.RelativeError);
                err1e4.Add(sim1e4.RelativeError);

                // Simulate AutoRestartOnDrift guard: if early stop is not yet stationary,
                // continue until earliest stationary iteration (or full run if none).
                int effectiveStop1e4 = sim1e4.StopIteration;
                float effectiveErr1e4 = sim1e4.RelativeError;
                if (sim1e4.TriggeredEarly)
                {
                    string[] prefix = lines.Take(sim1e4.StopIteration + 1).ToArray();
                    Su2HistoryResult prefixHist = Su2HistoryAnalyzer.Evaluate(prefix, historyOptions);
                    Su2ConvergenceVerdict verdict = Su2ConvergenceJudge.Judge(
                        prefixHist, new Dictionary<string, string> { ["Drag"] = "CD" },
                        0.05f * 0.25f, 0.25f, null);

                    if (!verdict.Assessed || !verdict.Converged || !Su2DiagnosticsExtractor.IsStationary(prefixHist, new Dictionary<string, string> { ["Drag"] = "CD" }))
                    {
                        int? stationaryIter = Su2HistoryAnalyzer.FindEarliestStationaryIteration(
                            lines, historyOptions, relativeTolerance: 0.05f * 0.25f, absoluteTolerance: 0.0005f, step: 200);
                        if (stationaryIter.HasValue && stationaryIter.Value > effectiveStop1e4)
                        {
                            effectiveStop1e4 = stationaryIter.Value;
                            string[] guardedPrefix = lines.Take(Math.Min(lines.Length, effectiveStop1e4 + 1)).ToArray();
                            Su2HistoryResult guardedHist = Su2HistoryAnalyzer.Evaluate(guardedPrefix, historyOptions);
                            if (guardedHist.WindowMeans.TryGetValue("CD", out float guardedCd))
                            {
                                effectiveErr1e4 = Math.Abs(guardedCd - sim1e4.FinalValue) / Math.Max(Math.Abs(sim1e4.FinalValue), 1e-7f);
                            }
                        }
                    }
                }

                stop1e4Guarded.Add(effectiveStop1e4);
                err1e4Guarded.Add(effectiveErr1e4);
            }

            double avgRaw = sumRawTotal / histories.Count;
            double avg1e5 = sumStop1e5 / histories.Count;
            double avg5e5 = sumStop5e5 / histories.Count;
            double avg1e4 = sumStop1e4 / histories.Count;
            double avg1e4Guarded = stop1e4Guarded.Average();

            double savings5e5VsTotal = 1.0 - avg5e5 / avgRaw;
            double savings1e4VsTotal = 1.0 - avg1e4 / avgRaw;
            double savings1e4GuardedVsTotal = 1.0 - avg1e4Guarded / avgRaw;
            double meanErr5e5 = err5e5.Average();
            double meanErr1e4 = err1e4.Average();
            double meanErr1e4Guarded = err1e4Guarded.Average();

            _output.WriteLine(
                $"ArchiveValidation (source={(usingRealArchive ? "real 3D MantaAuv archive" : "synthetic fallback")}, n={histories.Count}): "
                + $"Total={avgRaw:F1}, Stop(1e-5)={avg1e5:F1}, Stop(5e-5)={avg5e5:F1} (-{savings5e5VsTotal * 100:F1}%, avgCDerr={meanErr5e5 * 100:F3}%), "
                + $"Stop(1e-4)={avg1e4:F1} (-{savings1e4VsTotal * 100:F1}%, avgCDerr={meanErr1e4 * 100:F3}%), "
                + $"Stop(1e-4+AutoRestartGuard)={avg1e4Guarded:F1} (-{savings1e4GuardedVsTotal * 100:F1}%, avgCDerr={meanErr1e4Guarded * 100:F3}%)");

            Assert.True(avg5e5 < avg1e5, $"Expected 5e-5 ({avg5e5:F1}) to stop earlier than 1e-5 ({avg1e5:F1}).");
            Assert.True(avg1e4 <= avg5e5, $"Expected 1e-4 ({avg1e4:F1}) <= 5e-5 ({avg5e5:F1}).");
            Assert.True(savings5e5VsTotal >= 0.45, $"Expected >= 45% iteration savings vs 5000 iters, got {savings5e5VsTotal:P1}.");
            Assert.True(meanErr5e5 < 0.01, $"Expected mean CD deviation < 1% at 5e-5, got {meanErr5e5:P3}.");
            Assert.True(meanErr1e4 < 0.01, $"Expected mean CD deviation < 1% at 1e-4, got {meanErr1e4:P3}.");
        }

        [Fact]
        public void FindEarliestStationaryIteration_Detects_Transition_From_Drift_To_Steady_Plateau()
        {
            var lines = new List<string> { "\"Inner_Iter\",\"rms[P]\",\"CD\"" };
            for (int i = 0; i < 1200; i++)
            {
                // Initial transient drift decaying until ~500, followed by a stationary plateau with tiny noise
                double cd = i < 500
                    ? 0.3000 + 0.25 * Math.Exp(-i / 80.0)
                    : 0.3000 + 0.0001 * Math.Sin(i * 0.4);
                lines.Add($"{i}, -5.0, {cd.ToString("G8", CultureInfo.InvariantCulture)}");
            }

            var options = new Su2HistoryOptions(1200, 200, new[] { "CD" });
            int? stationaryIter = Su2HistoryAnalyzer.FindEarliestStationaryIteration(
                lines.ToArray(), options, relativeTolerance: 0.01f, absoluteTolerance: 0.0005f, step: 100);

            Assert.NotNull(stationaryIter);
            Assert.InRange(stationaryIter!.Value, 500, 1200);
        }

        private static List<(string Name, string[] Lines)> LoadArchived5kHistories(string? archiveDir)
        {
            var list = new List<(string, string[])>();
            if (string.IsNullOrWhiteSpace(archiveDir) || !Directory.Exists(archiveDir))
                return list;

            foreach (string path in Directory.EnumerateFiles(archiveDir, "History_Gen*_Var*.csv").OrderBy(p => p, StringComparer.Ordinal))
            {
                string fileName = Path.GetFileName(path);
                if (fileName.Contains("Kal", StringComparison.OrdinalIgnoreCase))
                    continue;

                string[] lines = File.ReadAllLines(path).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
                if (lines.Length >= 4900)
                {
                    list.Add((fileName, lines));
                }
            }

            return list;
        }

        private static List<(string Name, string[] Lines)> BuildSyntheticFallbackHistories()
        {
            var list = new List<(string, string[])>();
            for (int v = 1; v <= 4; v++)
            {
                var lines = new List<string>(5001) { "\"Inner_Iter\",\"rms[P]\",\"CD\"" };
                double target = 0.28 + 0.04 * v;
                double tau = 120.0 + 30.0 * v;
                for (int i = 0; i < 5000; i++)
                {
                    double cd = target + 0.8 * Math.Exp(-i / tau) * Math.Cos(i * 0.03);
                    lines.Add($"{i}, -5.0, {cd.ToString("G9", CultureInfo.InvariantCulture)}");
                }
                list.Add(($"Synthetic_Var{v}", lines.ToArray()));
            }
            return list;
        }
    }
}
