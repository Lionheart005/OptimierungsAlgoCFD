using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using FluidForge.Core;
using FluidForge.Kernels.Planar2D;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// SU2 CFD Solver. Coordinates config generation, process execution,
    /// automatic convergence restarts, result archiving, and diagnostic evaluation.
    /// </summary>
    public class Su2Solver : ISimulationSolver
    {
        private readonly Su2SolverOptions _options;
        private readonly FlowConditions? _flow;
        private readonly Su2DiagnosticsExtractor _diagnostics;

        /// <summary>
        /// Freestream velocity is logged once per run rather than per variant because it does not
        /// change across variants.
        /// </summary>
        private bool _velocityReported;
        private bool _transitionalReported;
        private bool _versionReported;

        /// <param name="options">Configuration from the project's solvers/su2.json; defaults apply if omitted.</param>
        /// <param name="flow">Shared flow conditions; resolved from config and options if null.</param>
        public Su2Solver(Su2SolverOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new Su2SolverOptions();
            _flow = flow;
            _diagnostics = new Su2DiagnosticsExtractor(_options);
        }

        public Su2SolverOptions Options => _options;
        public FlowConditions? Flow => _flow;

        /// <summary>
        /// Optional test hook for SU2 process execution (enables end-to-end tests of
        /// <see cref="Solve"/> without an actual <c>SU2_CFD</c>/<c>mpirun</c> binary).
        /// </summary>
        internal Func<string, int, (int ExitCode, string StdOut, string StdErr)>? RunSu2Override { get; set; }

        public string Name => "SU2 CFD Solver";

        public void Solve(
            string meshPath,
            ModelRecord record,
            SimulationConfig config,
            string workingDirectory)
        {
            float refArea = ResolveReferenceArea(record);
            (float effectiveAoA, bool aoaFromActive) = ResolveAngleOfAttack(record);

            FlowConditions flow = _flow ?? FlowConditions.From(config, _options);
            if (!_velocityReported)
            {
                SimulationLog.Detail($"Freestream: {flow.Velocity.ToString("0.###", CultureInfo.InvariantCulture)} m/s ({flow.VelocitySource}).");
                _velocityReported = true;
            }

            bool is2dGeometry = _options.Dimensions == 2 ||
                                (!string.IsNullOrEmpty(record.StlPath) && record.StlPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            (float lengthMm, string lengthSource) = ResolveGeometryLengthMm(record, is2dGeometry);

            float reynolds = flow.ComputeReynoldsNumber(lengthMm * 0.001f);
            if (reynolds > 0f) record.PassiveParameters["ReynoldsNumber"] = reynolds;

            if (!record.PassiveParameters.ContainsKey("BoundaryLayerHexShare"))
            {
                float hexShare = GmshCfdMesher.ReadSu2BoundaryLayerHexShare(meshPath);
                if (float.IsFinite(hexShare)) record.PassiveParameters["BoundaryLayerHexShare"] = hexShare;
            }

            SimulationLog.Detail(
                $"Freestream {flow.Velocity.ToString("0.0", CultureInfo.InvariantCulture)} m/s, Re_L = {reynolds.ToString("0.0e0", CultureInfo.InvariantCulture)} (L = {lengthMm.ToString("0.0", CultureInfo.InvariantCulture)} mm {lengthSource})");

            if (!_transitionalReported && reynolds > 0f && reynolds < 5e5f)
            {
                SimulationLog.Detail("[INFO] Re_L is in the laminar-transitional regime (< 5e5); turbulence model assumes fully turbulent flow.");
                _transitionalReported = true;
            }

            SimulationLog.Detail($"Running flow analysis for: {Path.GetFileName(meshPath)}...");

            string configPath = Path.Combine(workingDirectory, "current_config.cfg");
            string historyPath = Path.Combine(workingDirectory, "history.csv");

            bool isUnsteady = _options.TimeDomain != null && _options.TimeDomain.Enabled;
            Su2RestartController.CleanHistoryFiles(workingDirectory, historyPath);
            if (!isUnsteady)
            {
                // Prevents a missing restart export from falling back to the previous variant's solution.
                Su2RestartController.CleanSteadyRestartFiles(workingDirectory);
            }

            float effectiveRefLength = ResolveEffectiveReferenceLength(record, is2dGeometry, lengthMm);

            bool isControlRun = IsControlRunTag(ExtractTag(meshPath, record));
            SimulationConfig originalEffectiveConfig = isControlRun ? config.WithControlRunProfile() : config;
            if (isControlRun)
            {
                SimulationLog.Detail($"Control run: strict profile (up to {originalEffectiveConfig.Su2MaxIterations} iterations, window {originalEffectiveConfig.CauchyElements}, "
                    + $"iterative error at most {originalEffectiveConfig.IterativeToleranceFraction * 100:0}% of mesh tolerance).");
            }

            Su2SolverOptions runOptions = _options;
            if (aoaFromActive)
            {
                runOptions = _options.Clone();
                runOptions.AngleOfAttackDeg = effectiveAoA;
            }

            Su2ConfigGenerator.Generate(configPath, meshPath, refArea, originalEffectiveConfig, runOptions, flow, effectiveRefLength);

            try
            {
                int restartCount = 0;
                string currentBlockEps = originalEffectiveConfig.CauchyTolerance;
                SimulationConfig blockConfig = originalEffectiveConfig;
                string[]? previousCumulativeLines = null;

                while (true)
                {
                    var (exitCode, su2Output) = Su2ProcessRunner.ExecuteSu2Process(workingDirectory, blockConfig, RunSu2Override);

                    if (!_versionReported)
                    {
                        string? version = TryParseVersion(su2Output);
                        if (version != null)
                        {
                            _versionReported = true;
                            RunInfoWriter.SetProgramVersion(workingDirectory, "SU2", version);
                        }
                    }

                    Su2RestartController.ResolveBlockHistoryFile(workingDirectory, historyPath);

                    string[]? currentLines = File.Exists(historyPath)
                        ? File.ReadAllLines(historyPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                        : null;

                    if (restartCount == 0)
                    {
                        if (currentLines == null || currentLines.Length < 5)
                        {
                            Su2ResultArchiver.ArchiveFailureArtifacts(workingDirectory, meshPath, record, historyPath, configPath, su2Output);
                            return;
                        }
                    }
                    else
                    {
                        if (exitCode != 0 || currentLines == null || currentLines.Length < 2)
                        {
                            SimulationLog.Warning($"[SU2] Restart #{restartCount} yielded no usable data (ExitCode={exitCode}); keeping previous history.");
                            if (previousCumulativeLines != null) File.WriteAllLines(historyPath, previousCumulativeLines);
                            break;
                        }

                        currentLines = StitchHistoryCsv(previousCumulativeLines!, currentLines);
                        File.WriteAllLines(historyPath, currentLines);
                    }

                    if (isUnsteady || !_options.AutoRestartOnDrift || _options.MaxConvergenceRestarts <= 0 || exitCode != 0)
                        break;
                    if (restartCount >= _options.MaxConvergenceRestarts)
                        break;

                    if (!Su2RestartController.TryCheckNeedsSteadyRestart(
                            currentLines!, originalEffectiveConfig, _options.ResultMetrics,
                            out int totalIterations, out float worstNormalized))
                        break;

                    string? steadyRestartFile = FindSteadyRestartFile(workingDirectory);
                    if (steadyRestartFile == null) break;

                    int remainingBudget = originalEffectiveConfig.Su2MaxIterations - totalIterations;
                    if (remainingBudget <= 0) break;

                    restartCount++;
                    previousCumulativeLines = currentLines;
                    Su2RestartController.CleanHistoryFiles(workingDirectory, historyPath);

                    currentBlockEps = TightenTolerance(currentBlockEps);
                    int convStartIter = Math.Min(100, Math.Max(0, remainingBudget / 10));
                    blockConfig = originalEffectiveConfig.WithSu2BlockOverrides(remainingBudget, currentBlockEps);

                    Su2ConfigGenerator.Generate(
                        configPath, meshPath, refArea, blockConfig, runOptions,
                        flow, effectiveRefLength, steadyRestartFile, convStartIter);

                    SimulationLog.Detail(
                        $"[SU2] Drift after {totalIterations} iterations ({worstNormalized.ToString("0.000", CultureInfo.InvariantCulture)}x limit) "
                        + $"-> automatic restart #{restartCount}/{_options.MaxConvergenceRestarts} from {steadyRestartFile} "
                        + $"(remaining={remainingBudget}, CONV_CAUCHY_EPS={currentBlockEps}, CONV_STARTITER={convStartIter}).");
                }

                Su2ResultArchiver.ArchiveRunArtifacts(workingDirectory, meshPath, record, historyPath, configPath);
            }
            catch (Exception e)
            {
                throw new Exception($"Critical system error while running SU2: {e.Message}");
            }

            ReadMetricsFromHistory(historyPath, record, originalEffectiveConfig, isControlRun);
            EvaluateSurfaceYPlus(Path.Combine(workingDirectory, "surface.csv"), record);

            int effectiveDims = is2dGeometry ? 2 : runOptions.Dimensions;
            EvaluateSurfaceAerodynamics(workingDirectory, refArea, effectiveAoA, effectiveDims, record);
        }

        private (float EffectiveAoA, bool FromActive) ResolveAngleOfAttack(ModelRecord record)
        {
            float effectiveAoA = _options.AngleOfAttackDeg;
            bool aoaFromActive = false;
            if (record.ActiveParameters != null)
            {
                foreach (var kv in record.ActiveParameters)
                {
                    if (kv.Key.Equals("AngleOfAttackDeg", StringComparison.OrdinalIgnoreCase) ||
                        kv.Key.Equals("AngleOfAttack", StringComparison.OrdinalIgnoreCase))
                    {
                        effectiveAoA = kv.Value;
                        aoaFromActive = true;
                        break;
                    }
                }
            }

            if (aoaFromActive || effectiveAoA != 0f)
                record.PassiveParameters["AngleOfAttackDeg"] = effectiveAoA;
            if (aoaFromActive)
                SimulationLog.Detail($"Angle of attack (actively optimized): {effectiveAoA.ToString("0.##", CultureInfo.InvariantCulture)}°");

            return (effectiveAoA, aoaFromActive);
        }

        private (float LengthMm, string Source) ResolveGeometryLengthMm(ModelRecord record, bool is2dGeometry)
        {
            float fallbackMm = _options.ReferenceLength * 1000f;
            float scaleFactor = record.PassiveParameters.TryGetValue("ScaleFactor", out float sf) && sf > 0f ? sf : 1.0f;

            if (is2dGeometry && !string.IsNullOrEmpty(record.StlPath) && File.Exists(record.StlPath) &&
                record.StlPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    ContourFile2D contourFile = Shape2D.LoadContour(record.StlPath);
                    BBox2 bounds = BBox2.Empty;
                    if (contourFile.Contours != null)
                    {
                        foreach (var c in contourFile.Contours)
                        {
                            if (c.Points == null) continue;
                            foreach (var p in c.Points)
                            {
                                if (p != null && p.Length >= 2) bounds = bounds.Include(new Vector2(p[0], p[1]));
                            }
                        }
                    }

                    if (!bounds.IsEmpty && bounds.Size.X > 0f)
                        return (bounds.Size.X / scaleFactor, "from 2D bounding box");
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(record.StlPath) && File.Exists(record.StlPath))
            {
                try
                {
                    MeshBounds bounds = StlWriter.ReadBounds(record.StlPath);
                    if (!bounds.IsEmpty && bounds.Size.X > 0f)
                        return (bounds.Size.X / scaleFactor, "from bounding box");
                }
                catch { }
            }

            return (fallbackMm, "from ReferenceLength");
        }

        private float ResolveEffectiveReferenceLength(ModelRecord record, bool is2dGeometry, float lengthMm)
        {
            float effectiveRefLength = _options.ReferenceLength;
            if (is2dGeometry && MathF.Abs(_options.ReferenceLength - 0.01f) < 1e-6f)
            {
                if (record.PassiveParameters.TryGetValue("Chord", out float chordMmVal) && chordMmVal > 0f)
                    effectiveRefLength = chordMmVal * 0.001f;
                else if (lengthMm > 0f && lengthMm != _options.ReferenceLength * 1000f)
                    effectiveRefLength = lengthMm * 0.001f;
            }
            return effectiveRefLength;
        }

        public AerodynamicDragBreakdown? EvaluateSurfaceAerodynamics(
            string workingDirectory, float refAreaOrChordM, float angleOfAttackDeg, int dimensions, ModelRecord record)
            => _diagnostics.EvaluateSurfaceAerodynamics(workingDirectory, refAreaOrChordM, angleOfAttackDeg, dimensions, record);

        public YPlusStatistics? EvaluateSurfaceYPlus(string surfaceCsvPath, ModelRecord record)
            => _diagnostics.EvaluateSurfaceYPlus(surfaceCsvPath, record);

        public float ResolveReferenceArea(ModelRecord record)
            => _diagnostics.ResolveReferenceArea(record);

        public void ReadMetricsFromHistory(string historyPath, ModelRecord record, SimulationConfig? config = null, bool controlRun = false)
            => _diagnostics.ReadMetricsFromHistory(historyPath, record, config, controlRun);

        internal static bool IsStationary(Su2HistoryResult history, IReadOnlyDictionary<string, string> wanted)
            => Su2DiagnosticsExtractor.IsStationary(history, wanted);

        public static int FindColumn(string[] headers, string columnName, out string problem)
            => Su2DiagnosticsExtractor.FindColumn(headers, columnName, out problem);

        public static string[] SplitCsvLine(string line)
            => Su2DiagnosticsExtractor.SplitCsvLine(line);

        public static string ArchiveBaseName(ModelRecord record, string? tag = null)
            => Su2ResultArchiver.ArchiveBaseName(record, tag);

        public static string? TryParseVersion(string? consoleOutput)
            => Su2ProcessRunner.TryParseVersion(consoleOutput);

        public static string ExtractTag(string meshPath, ModelRecord record)
            => Su2ResultArchiver.ExtractTag(meshPath, record);

        public static bool IsControlRunTag(string? tag)
            => Su2ResultArchiver.IsControlRunTag(tag);

        public static string ArchiveBaseNameFromMesh(string meshPath, ModelRecord record)
            => Su2ResultArchiver.ArchiveBaseNameFromMesh(meshPath, record);

        internal static string? FindSteadyRestartFile(string workingDirectory)
            => Su2RestartController.FindSteadyRestartFile(workingDirectory);

        internal static string TightenTolerance(string? currentTolerance)
            => Su2RestartController.TightenTolerance(currentTolerance);

        internal static string[] StitchHistoryCsv(string[] previousLines, string[] newLines)
            => Su2RestartController.StitchHistoryCsv(previousLines, newLines);
    }
}
