using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using FluidForge.Core;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Runs steady-state OpenFOAM (<c>simpleFoam</c>) in serial or parallel MPI mode,
    /// parses <c>postProcessing/</c> metrics, evaluates convergence and mass-balance gates,
    /// and writes all quantities into <see cref="ModelRecord.PassiveParameters"/>.
    /// </summary>
    public sealed class OpenFoamSimpleSolver : ISimulationSolver
    {
        private readonly OpenFoamOptions _options;
        private readonly IOpenFoamCommandRunner _runner;

        /// <param name="options">OpenFOAM options loaded from <c>solvers/openfoam.json</c>; defaults apply if null.</param>
        /// <param name="runner">Command runner (injectable for unit tests).</param>
        public OpenFoamSimpleSolver(OpenFoamOptions? options = null, IOpenFoamCommandRunner? runner = null)
        {
            _options = options ?? new OpenFoamOptions();
            _runner = runner ?? new OpenFoamProcessRunner();
        }

        /// <inheritdoc />
        public string Name => "OpenFOAM simpleFoam Solver";

        public OpenFoamOptions Options => _options;

        /// <inheritdoc />
        public void Solve(
            string meshPath,
            ModelRecord record,
            SimulationConfig config,
            string workingDirectory)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(meshPath);
            ArgumentNullException.ThrowIfNull(record);
            ArgumentNullException.ThrowIfNull(config);

            string caseDir = Directory.Exists(meshPath)
                ? meshPath
                : Path.GetDirectoryName(meshPath) ?? workingDirectory;

            record.Artifacts["OpenFoamCase"] = caseDir;
            Directory.CreateDirectory(caseDir);
            File.WriteAllText(Path.Combine(caseDir, "case.foam"), string.Empty);

            int mpiCores = Math.Max(1, config.MpiCores);
            string solverName = string.IsNullOrWhiteSpace(_options.Solver) ? "simpleFoam" : _options.Solver.Trim();
            int endTimeBudget = ReadEndTimeBudget(caseDir, _options.EndTime);

            if (mpiCores > 1)
            {
                OpenFoamCommandResult decomp = _runner.Run(caseDir, "decomposePar -force", _options, mpiCores: 1);
                if (decomp.ExitCode != 0)
                {
                    MarkFailed(record, $"decomposePar failed (ExitCode {decomp.ExitCode})");
                    return;
                }

                if (_options.UsePotentialFoamInit)
                {
                    string potCmd = OpenFoamProcessRunner.BuildParallelToolCommand("potentialFoam", "-noFunctionObjects", _options, mpiCores);
                    OpenFoamCommandResult potRes = _runner.Run(caseDir, potCmd, _options, mpiCores);
                    if (potRes.ExitCode != 0)
                        SimulationLog.Warning($"potentialFoam -parallel exited with code {potRes.ExitCode}; continuing with simpleFoam.");
                }
            }
            else if (_options.UsePotentialFoamInit)
            {
                OpenFoamCommandResult potRes = _runner.Run(caseDir, "potentialFoam -noFunctionObjects", _options, mpiCores: 1);
                if (potRes.ExitCode != 0)
                    SimulationLog.Warning($"potentialFoam exited with code {potRes.ExitCode}; continuing with simpleFoam.");
            }

            string solverCmd = mpiCores > 1
                ? OpenFoamProcessRunner.BuildParallelToolCommand(solverName, null, _options, mpiCores)
                : solverName;

            int maxRestarts = Math.Max(0, _options.MaxConvergenceRestarts);
            float floor = MathF.Max(_options.ResidualTargetFloor, 1e-10f);
            float currentResidualTarget = _options.ResidualTarget > 0f ? _options.ResidualTarget : 1e-4f;
            int restartsUsed = 0;
            float lastRestartTime = 0f;

            OpenFoamCommandResult solverResult = new(1, string.Empty, string.Empty);
            OpenFoamPostProcessResult? post = null;
            OpenFoamConvergenceVerdict? verdict = null;

            for (int attempt = 0; attempt <= maxRestarts; attempt++)
            {
                solverResult = _runner.Run(caseDir, solverCmd, _options, mpiCores);
                if (solverResult.ExitCode != 0)
                {
                    MarkFailed(record, $"{solverName} exited with code {solverResult.ExitCode}");
                    return;
                }

                post = OpenFoamPostProcessor.Evaluate(caseDir, _options);
                verdict = OpenFoamConvergenceJudge.Judge(solverResult.CombinedOutput, post, _options);

                float latestTime = 0f;
                bool canRestart = _options.AutoRestartOnDrift &&
                                  attempt < maxRestarts &&
                                  !verdict.DivergedOrNaN &&
                                  verdict.MassBalanceOk &&
                                  verdict.HasMonotonicDrift &&
                                  !verdict.IsUnsteadyStationary &&
                                  verdict.Iterations < endTimeBudget &&
                                  currentResidualTarget > floor &&
                                  TryGetLatestTimeDirectory(caseDir, mpiCores, out latestTime) &&
                                  latestTime > lastRestartTime;

                if (!canRestart)
                    break;

                currentResidualTarget = MathF.Max(currentResidualTarget * 0.1f, floor);
                PatchControlDictStartFromLatestTime(caseDir);
                PatchFvSolutionResidualControl(caseDir, currentResidualTarget);
                restartsUsed++;
                lastRestartTime = latestTime;

                string driftList = verdict.DriftingMetrics is { Count: > 0 }
                    ? string.Join(", ", verdict.DriftingMetrics)
                    : "monitored metrics";
                SimulationLog.Detail(
                    $"OpenFOAM drift after {verdict.Iterations} iters ({driftList}) " +
                    $"-> auto-restart #{restartsUsed}/{maxRestarts} from t={latestTime.ToString("G6", CultureInfo.InvariantCulture)}, " +
                    $"residualControl={currentResidualTarget.ToString("G3", CultureInfo.InvariantCulture)}.");
            }

            if (mpiCores > 1)
            {
                OpenFoamCommandResult recon = _runner.Run(caseDir, "reconstructPar -latestTime", _options, mpiCores: 1);
                if (recon.ExitCode != 0)
                {
                    SimulationLog.Warning($"reconstructPar exited with code {recon.ExitCode}; postProcessing values may still be valid.");
                }
            }

            if (_options.ExportVtk)
            {
                OpenFoamCommandResult vtkRes = _runner.Run(caseDir, "foamToVTK -latestTime", _options, mpiCores: 1);
                if (vtkRes.ExitCode != 0)
                    SimulationLog.Warning($"foamToVTK exited with code {vtkRes.ExitCode}.");
            }

            post ??= OpenFoamPostProcessor.Evaluate(caseDir, _options);
            verdict ??= OpenFoamConvergenceJudge.Judge(solverResult.CombinedOutput, post, _options);

            foreach (KeyValuePair<string, float> kvp in post.Metrics)
            {
                if (float.IsFinite(kvp.Value))
                    record.PassiveParameters[kvp.Key] = kvp.Value;
            }

            if (_options.ResultMetrics != null)
            {
                foreach (KeyValuePair<string, string> mapping in _options.ResultMetrics)
                {
                    string targetKey = (mapping.Key ?? string.Empty).Trim();
                    string sourceKey = (mapping.Value ?? string.Empty).Trim();
                    if (targetKey.Length > 0 && sourceKey.Length > 0 &&
                        !record.PassiveParameters.ContainsKey(targetKey) &&
                        post.Metrics.TryGetValue(sourceKey, out float mappedVal) &&
                        float.IsFinite(mappedVal))
                    {
                        record.PassiveParameters[targetKey] = mappedVal;
                    }
                }
            }

            float uRef = post.InletSpeedMs ?? _options.InletVelocity;
            float lRef = _options.HydraulicDiameter > 0f ? _options.HydraulicDiameter : _options.ReferenceLength;
            if (uRef > 0f && lRef > 0f && _options.Density > 0f && _options.DynamicViscosity > 0f)
            {
                record.PassiveParameters["ReynoldsNumber"] = _options.Density * uRef * lRef / _options.DynamicViscosity;
            }

            record.PassiveParameters["OpenFoam_Iterations"] = verdict.Iterations;
            record.PassiveParameters["OpenFoam_Converged"] = verdict.Converged ? 1.0f : 0.0f;
            record.PassiveParameters["OpenFoam_Unsteady"] = verdict.IsUnsteadyStationary ? 1.0f : 0.0f;
            record.PassiveParameters["OpenFoam_Restarts"] = restartsUsed;

            if (verdict.MetricUncertainties != null)
            {
                foreach (KeyValuePair<string, float> kvp in verdict.MetricUncertainties)
                {
                    if (float.IsFinite(kvp.Value))
                        record.PassiveParameters[MetricUncertainty.KeyFor(kvp.Key)] = kvp.Value;
                }
            }

            if (verdict.IsUnsteadyStationary)
            {
                string uncSummary = verdict.MetricUncertainties is { Count: > 0 }
                    ? string.Join(", ", verdict.MetricUncertainties.Select(
                        kvp => $"{kvp.Key}: ±{(kvp.Value * 100f).ToString("0.00", CultureInfo.InvariantCulture)}%"))
                    : "n/a";
                SimulationLog.Detail(
                    $"OpenFOAM detected stationary oscillation (vortex shedding); using TailMean over trailing window (relative uncertainty: {uncSummary}).");
            }

            if (post.YPlusP95.HasValue)
            {
                float warnLimit = _options.TargetYPlus * _options.YPlusWarnFactor;
                if (post.YPlusP95.Value > warnLimit)
                {
                    SimulationLog.Warning(
                        $"y+ (P95 = {post.YPlusP95.Value.ToString("0.00", CultureInfo.InvariantCulture)}, "
                        + $"Max = {(post.YPlusMax ?? post.YPlusP95.Value).ToString("0.00", CultureInfo.InvariantCulture)}) "
                        + $"exceeds limit {warnLimit.ToString("0.00", CultureInfo.InvariantCulture)}.");
                }
            }

            // Hard mass-balance gate + divergence/convergence check
            if (!verdict.MassBalanceOk || verdict.DivergedOrNaN || (_options.TreatUnconvergedAsFailed && !verdict.Converged))
            {
                MarkFailed(record, verdict.Summary);
                return;
            }

            SimulationLog.Detail(
                $"OpenFOAM solved ({verdict.Iterations} iters"
                + (record.PassiveParameters.TryGetValue("PressureDrop", out float dp)
                    ? $", PressureDrop = {dp.ToString("0.##", CultureInfo.InvariantCulture)} Pa"
                    : string.Empty)
                + (post.MassBalanceErrorPercent.HasValue
                    ? $", MassErr = {post.MassBalanceErrorPercent.Value.ToString("0.000", CultureInfo.InvariantCulture)}%"
                    : string.Empty)
                + ").");
        }

        private static int ReadEndTimeBudget(string caseDir, int configuredEndTime)
        {
            int fallback = Math.Max(1, configuredEndTime);
            string controlDictPath = Path.Combine(caseDir, "system", "controlDict");
            if (!File.Exists(controlDictPath)) return fallback;

            string content = File.ReadAllText(controlDictPath);
            Match m = Regex.Match(content, @"^\s*endTime\s+(\d+)\s*;", RegexOptions.Multiline | RegexOptions.CultureInvariant);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed > 0)
                return parsed;

            return fallback;
        }

        private static bool TryGetLatestTimeDirectory(string caseDir, int mpiCores, out float latestTime)
        {
            latestTime = 0f;
            string searchRoot = mpiCores > 1 ? Path.Combine(caseDir, "processor0") : caseDir;
            if (!Directory.Exists(searchRoot)) return false;

            bool found = false;
            foreach (string dir in Directory.EnumerateDirectories(searchRoot))
            {
                string name = Path.GetFileName(dir);
                if (float.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out float t) &&
                    float.IsFinite(t) && t > 0f)
                {
                    if (!found || t > latestTime)
                    {
                        latestTime = t;
                        found = true;
                    }
                }
            }

            return found;
        }

        private static void PatchControlDictStartFromLatestTime(string caseDir)
        {
            string controlDictPath = Path.Combine(caseDir, "system", "controlDict");
            if (!File.Exists(controlDictPath)) return;

            string content = File.ReadAllText(controlDictPath);
            string updated = Regex.Replace(
                content,
                @"(?m)^([ \t]*startFrom[ \t]+)[^;\r\n]*;",
                "$1latestTime;",
                RegexOptions.CultureInvariant);
            File.WriteAllText(controlDictPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void PatchFvSolutionResidualControl(string caseDir, float newTarget)
        {
            string fvSolutionPath = Path.Combine(caseDir, "system", "fvSolution");
            if (!File.Exists(fvSolutionPath)) return;

            string content = File.ReadAllText(fvSolutionPath);
            string targetStr = newTarget.ToString("G6", CultureInfo.InvariantCulture);

            string updated = Regex.Replace(
                content,
                @"(residualControl\s*\{)([^}]*)(\})",
                match =>
                {
                    string body = Regex.Replace(
                        match.Groups[2].Value,
                        @"(?m)^([ \t]*(?:""[^""]+""|[A-Za-z_][A-Za-z0-9_.*|()]*)[ \t]+)[^;\r\n]*(;)",
                        inner => inner.Groups[1].Value + targetStr + inner.Groups[2].Value,
                        RegexOptions.CultureInvariant);
                    return match.Groups[1].Value + body + match.Groups[3].Value;
                },
                RegexOptions.CultureInvariant);

            File.WriteAllText(fvSolutionPath, updated, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }

        private static void MarkFailed(ModelRecord record, string reason)
        {
            SimulationLog.Error($"OpenFOAM solver failed: {reason}");
            record.SimulationFailed = true;
            record.Feasible = false;
            record.FailedStage = EvaluationFailureStage.Solver;
        }
    }
}
