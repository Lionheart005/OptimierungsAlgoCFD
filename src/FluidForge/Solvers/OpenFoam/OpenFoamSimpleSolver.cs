using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
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

            OpenFoamCommandResult solverResult;
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

                string parallelSolverCmd = OpenFoamProcessRunner.BuildParallelToolCommand(solverName, null, _options, mpiCores);
                solverResult = _runner.Run(caseDir, parallelSolverCmd, _options, mpiCores);

                if (solverResult.ExitCode == 0)
                {
                    OpenFoamCommandResult recon = _runner.Run(caseDir, "reconstructPar -latestTime", _options, mpiCores: 1);
                    if (recon.ExitCode != 0)
                    {
                        SimulationLog.Warning($"reconstructPar exited with code {recon.ExitCode}; postProcessing values may still be valid.");
                    }
                }
            }
            else
            {
                if (_options.UsePotentialFoamInit)
                {
                    OpenFoamCommandResult potRes = _runner.Run(caseDir, "potentialFoam -noFunctionObjects", _options, mpiCores: 1);
                    if (potRes.ExitCode != 0)
                        SimulationLog.Warning($"potentialFoam exited with code {potRes.ExitCode}; continuing with simpleFoam.");
                }

                solverResult = _runner.Run(caseDir, solverName, _options, mpiCores: 1);
            }

            if (solverResult.ExitCode != 0)
            {
                MarkFailed(record, $"{solverName} exited with code {solverResult.ExitCode}");
                return;
            }

            if (_options.ExportVtk)
            {
                OpenFoamCommandResult vtkRes = _runner.Run(caseDir, "foamToVTK -latestTime", _options, mpiCores: 1);
                if (vtkRes.ExitCode != 0)
                    SimulationLog.Warning($"foamToVTK exited with code {vtkRes.ExitCode}.");
            }

            OpenFoamPostProcessResult post = OpenFoamPostProcessor.Evaluate(caseDir, _options);
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

            OpenFoamConvergenceVerdict verdict = OpenFoamConvergenceJudge.Judge(solverResult.CombinedOutput, post, _options);
            record.PassiveParameters["OpenFoam_Iterations"] = verdict.Iterations;
            record.PassiveParameters["OpenFoam_Converged"] = verdict.Converged ? 1.0f : 0.0f;

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

        private static void MarkFailed(ModelRecord record, string reason)
        {
            SimulationLog.Error($"OpenFOAM solver failed: {reason}");
            record.SimulationFailed = true;
            record.Feasible = false;
            record.FailedStage = EvaluationFailureStage.Solver;
        }
    }
}
