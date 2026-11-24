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
            SymmetryAxis symAxis = StlHalfClipper.ParseConfigValue(_options.SymmetryPlane);
            Su2ConfigGenerator.ResolveTurbulenceModel(_options.TurbulenceModel);

            float fullRefArea = ResolveReferenceArea(record);
            (float effectiveRefArea, float physicalFullAreaForForce) = ResolveEffectiveSymmetryAreas(record, fullRefArea, symAxis);

            bool is2dGeometry = _options.Dimensions == 2 ||
                                (!string.IsNullOrEmpty(record.StlPath) && record.StlPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase));
            (float lengthMm, string lengthSource) = ResolveGeometryLengthMm(record, is2dGeometry);
            float effectiveRefLength = ResolveEffectiveReferenceLength(record, is2dGeometry, lengthMm);

            if (!record.PassiveParameters.ContainsKey("BoundaryLayerHexShare"))
            {
                float hexShare = GmshCfdMesher.ReadSu2BoundaryLayerHexShare(meshPath);
                if (float.IsFinite(hexShare)) record.PassiveParameters["BoundaryLayerHexShare"] = hexShare;
            }

            if (_options.OperatingPoints != null && _options.OperatingPoints.Count > 0)
            {
                SolveMultiPoint(
                    meshPath, record, config, workingDirectory,
                    symAxis, effectiveRefArea, physicalFullAreaForForce,
                    is2dGeometry, lengthMm, lengthSource, effectiveRefLength);
                return;
            }

            (float effectiveAoA, bool aoaFromActive) = ResolveAngleOfAttack(record);

            FlowConditions flow = _flow ?? FlowConditions.From(config, _options);
            if (!_velocityReported)
            {
                SimulationLog.Detail($"Freestream: {flow.Velocity.ToString("0.###", CultureInfo.InvariantCulture)} m/s ({flow.VelocitySource}).");
                _velocityReported = true;
            }

            float reynolds = flow.ComputeReynoldsNumber(lengthMm * 0.001f);
            if (reynolds > 0f) record.PassiveParameters["ReynoldsNumber"] = reynolds;

            SimulationLog.Detail(
                $"Freestream {flow.Velocity.ToString("0.0", CultureInfo.InvariantCulture)} m/s, Re_L = {reynolds.ToString("0.0e0", CultureInfo.InvariantCulture)} (L = {lengthMm.ToString("0.0", CultureInfo.InvariantCulture)} mm {lengthSource})");

            ReportLowReynoldsIfNeeded(reynolds, _options.TurbulenceModel);

            Su2SolverOptions runOptions = _options;
            if (aoaFromActive)
            {
                runOptions = _options.Clone();
                runOptions.AngleOfAttackDeg = effectiveAoA;
            }

            RunSinglePoint(
                meshPath, record, config, workingDirectory,
                runOptions, flow, effectiveRefArea, effectiveAoA, effectiveRefLength,
                is2dGeometry, archiveSuffix: null);

            if (symAxis != SymmetryAxis.None &&
                !record.SimulationFailed &&
                record.PassiveParameters.TryGetValue("Drag", out float dragVal) &&
                float.IsFinite(dragVal))
            {
                record.PassiveParameters["DragForceN"] =
                    0.5f * flow.Density * flow.Velocity * flow.Velocity * dragVal * physicalFullAreaForForce;
            }
        }

        private void SolveMultiPoint(
            string meshPath,
            ModelRecord record,
            SimulationConfig config,
            string workingDirectory,
            SymmetryAxis symAxis,
            float effectiveRefArea,
            float physicalFullAreaForForce,
            bool is2dGeometry,
            float lengthMm,
            string lengthSource,
            float effectiveRefLength)
        {
            List<OperatingPointOptions> ops = _options.OperatingPoints;
            double totalWeight = 0.0;
            for (int j = 0; j < ops.Count; j++)
            {
                OperatingPointOptions op = ops[j] ?? throw new InvalidOperationException(
                    $"{OutputConventions.ConfigTag} OperatingPoints[{j}] must not be null.");
                if (!float.IsFinite(op.Weight) || op.Weight <= 0f)
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} OperatingPoints[{j}] ('{op.Name}') must have finite Weight > 0 (got {op.Weight}).");
                }
                if (op.Velocity.HasValue && (!float.IsFinite(op.Velocity.Value) || op.Velocity.Value <= 0f))
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} OperatingPoints[{j}] ('{op.Name}') has invalid Velocity '{op.Velocity.Value}'; must be finite and > 0.");
                }
                totalWeight += op.Weight;
            }

            double[] normalizedWeights = ops.Select(op => op.Weight / totalWeight).ToArray();
            bool anyOpVelocity = ops.Any(op => op.Velocity.HasValue);
            float baseVelocity = anyOpVelocity || _flow == null
                ? Su2ConfigGenerator.ResolveVelocity(config, _options, out _)
                : _flow.Velocity;

            if (!_velocityReported)
            {
                SimulationLog.Detail($"Multi-point evaluation: {ops.Count} operating point(s) on shared mesh {Path.GetFileName(meshPath)}.");
                _velocityReported = true;
            }

            float density = _flow?.Density ?? _options.Density;
            float viscosity = _flow?.DynamicViscosity ?? _options.DynamicViscosity;

            var metricKeys = (_options.ResultMetrics?.Keys ?? Enumerable.Empty<string>())
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .ToList();
            var weightedMetrics = metricKeys.ToDictionary(k => k, _ => 0.0, StringComparer.OrdinalIgnoreCase);
            var weightedOsc = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var weightedIterErr = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var weightedUncertainty = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

            int sumIterations = 0;
            bool allConverged = true;
            bool anyUnsteady = false;
            float? maxYpMed = null;
            float? maxYp95 = null;
            float? maxYpMax = null;
            float maxReynolds = 0f;
            double weightedDragForceN = 0.0;
            bool hasDragForce = false;

            for (int j = 0; j < ops.Count; j++)
            {
                OperatingPointOptions op = ops[j];
                string opSuffix = $"_OP{j + 1}";
                string opLabel = string.IsNullOrWhiteSpace(op.Name) ? $"OP{j + 1}" : op.Name.Trim();
                float opVel = op.Velocity ?? baseVelocity;
                var opFlow = new FlowConditions(opVel, density, viscosity, $"OperatingPoint {opLabel}");
                float opRe = opFlow.ComputeReynoldsNumber(lengthMm * 0.001f);
                if (opRe > maxReynolds) maxReynolds = opRe;

                SimulationLog.Detail(
                    $"Operating point {j + 1}/{ops.Count} '{opLabel}' (alpha={op.AngleOfAttackDeg.ToString("0.##", CultureInfo.InvariantCulture)}°, "
                    + $"U={opVel.ToString("0.###", CultureInfo.InvariantCulture)} m/s, weight={normalizedWeights[j].ToString("0.###", CultureInfo.InvariantCulture)}, "
                    + $"Re_L={opRe.ToString("0.0e0", CultureInfo.InvariantCulture)} at L={lengthMm.ToString("0.0", CultureInfo.InvariantCulture)} mm {lengthSource})");

                ReportLowReynoldsIfNeeded(opRe, _options.TurbulenceModel);

                Su2SolverOptions opOptions = _options.Clone();
                opOptions.AngleOfAttackDeg = op.AngleOfAttackDeg;
                opOptions.FreestreamVelocity = opVel;
                opOptions.OperatingPoints = new List<OperatingPointOptions>();

                CleanSurfaceFiles(workingDirectory);

                var opRecord = new ModelRecord
                {
                    Iteration = record.Iteration,
                    Variant = record.Variant,
                    StlPath = record.StlPath,
                    MeshPath = record.MeshPath
                };

                RunSinglePoint(
                    meshPath, opRecord, config, workingDirectory,
                    opOptions, opFlow, effectiveRefArea, op.AngleOfAttackDeg, effectiveRefLength,
                    is2dGeometry, archiveSuffix: opSuffix);

                if (opRecord.SimulationFailed)
                {
                    record.SimulationFailed = true;
                    record.FailedStage = EvaluationFailureStage.Solver;
                    record.Feasible = false;
                    return;
                }

                double w = normalizedWeights[j];
                foreach (string metric in metricKeys)
                {
                    if (opRecord.PassiveParameters.TryGetValue(metric, out float val) && float.IsFinite(val))
                    {
                        record.PassiveParameters[$"{metric}{opSuffix}"] = val;
                        weightedMetrics[metric] += w * val;
                    }

                    string oscKey = $"Su2_Osc_{metric}";
                    if (opRecord.PassiveParameters.TryGetValue(oscKey, out float oscVal) && float.IsFinite(oscVal))
                    {
                        weightedOsc[metric] = weightedOsc.GetValueOrDefault(metric) + w * oscVal;
                    }

                    string errKey = $"Su2_IterErr_{metric}";
                    if (opRecord.PassiveParameters.TryGetValue(errKey, out float errVal) && float.IsFinite(errVal))
                    {
                        weightedIterErr[metric] = weightedIterErr.GetValueOrDefault(metric) + w * errVal;
                    }

                    string uncKey = MetricUncertainty.KeyFor(metric);
                    if (opRecord.PassiveParameters.TryGetValue(uncKey, out float uncVal) && float.IsFinite(uncVal))
                    {
                        weightedUncertainty[metric] = weightedUncertainty.GetValueOrDefault(metric) + w * uncVal;
                    }
                }

                record.PassiveParameters[$"AngleOfAttackDeg{opSuffix}"] = op.AngleOfAttackDeg;
                record.PassiveParameters[$"Velocity{opSuffix}"] = opVel;
                if (opRe > 0f) record.PassiveParameters[$"ReynoldsNumber{opSuffix}"] = opRe;

                if (opRecord.PassiveParameters.TryGetValue("Su2_Iterations", out float opIter))
                {
                    record.PassiveParameters[$"Su2_Iterations{opSuffix}"] = opIter;
                    sumIterations += (int)MathF.Round(opIter);
                }

                if (opRecord.PassiveParameters.TryGetValue("Su2_Converged", out float opConv))
                {
                    record.PassiveParameters[$"Su2_Converged{opSuffix}"] = opConv;
                    if (opConv < 0.5f) allConverged = false;
                }

                if (opRecord.PassiveParameters.TryGetValue("Su2_Unsteady", out float opUns) && opUns >= 0.5f)
                {
                    anyUnsteady = true;
                }

                if (opRecord.PassiveParameters.TryGetValue("YPlus_Median", out float ypMed))
                {
                    maxYpMed = maxYpMed.HasValue ? MathF.Max(maxYpMed.Value, ypMed) : ypMed;
                }
                if (opRecord.PassiveParameters.TryGetValue("YPlus_P95", out float yp95))
                {
                    record.PassiveParameters[$"YPlus_P95{opSuffix}"] = yp95;
                    maxYp95 = maxYp95.HasValue ? MathF.Max(maxYp95.Value, yp95) : yp95;
                }
                if (opRecord.PassiveParameters.TryGetValue("YPlus_Max", out float ypMax))
                {
                    maxYpMax = maxYpMax.HasValue ? MathF.Max(maxYpMax.Value, ypMax) : ypMax;
                }

                if (symAxis != SymmetryAxis.None &&
                    opRecord.PassiveParameters.TryGetValue("Drag", out float opDrag) &&
                    float.IsFinite(opDrag))
                {
                    hasDragForce = true;
                    weightedDragForceN += w * (0.5 * density * opVel * opVel * opDrag * physicalFullAreaForForce);
                }
            }

            foreach (var kv in weightedMetrics)
                record.PassiveParameters[kv.Key] = (float)kv.Value;
            foreach (var kv in weightedOsc)
                record.PassiveParameters[$"Su2_Osc_{kv.Key}"] = (float)kv.Value;
            foreach (var kv in weightedIterErr)
                record.PassiveParameters[$"Su2_IterErr_{kv.Key}"] = (float)kv.Value;
            foreach (var kv in weightedUncertainty)
                record.PassiveParameters[MetricUncertainty.KeyFor(kv.Key)] = (float)kv.Value;

            record.PassiveParameters["Su2_Iterations"] = sumIterations;
            record.PassiveParameters["Su2_Converged"] = allConverged ? 1.0f : 0.0f;
            record.PassiveParameters["Su2_Unsteady"] = anyUnsteady ? 1.0f : 0.0f;
            if (maxYpMed.HasValue) record.PassiveParameters["YPlus_Median"] = maxYpMed.Value;
            if (maxYp95.HasValue) record.PassiveParameters["YPlus_P95"] = maxYp95.Value;
            if (maxYpMax.HasValue) record.PassiveParameters["YPlus_Max"] = maxYpMax.Value;
            if (maxReynolds > 0f) record.PassiveParameters["ReynoldsNumber"] = maxReynolds;
            record.PassiveParameters["OperatingPointCount"] = ops.Count;

            if (symAxis != SymmetryAxis.None && hasDragForce)
            {
                record.PassiveParameters["DragForceN"] = (float)weightedDragForceN;
            }
        }

        private void RunSinglePoint(
            string meshPath,
            ModelRecord record,
            SimulationConfig config,
            string workingDirectory,
            Su2SolverOptions runOptions,
            FlowConditions flow,
            float refArea,
            float effectiveAoA,
            float effectiveRefLength,
            bool is2dGeometry,
            string? archiveSuffix)
        {
            SimulationLog.Detail($"Running flow analysis for: {Path.GetFileName(meshPath)}...");

            string configPath = Path.Combine(workingDirectory, "current_config.cfg");
            string historyPath = Path.Combine(workingDirectory, "history.csv");

            bool isUnsteady = runOptions.TimeDomain != null && runOptions.TimeDomain.Enabled;
            Su2RestartController.CleanHistoryFiles(workingDirectory, historyPath);
            if (!isUnsteady)
            {
                // Prevents a missing restart export from falling back to the previous variant's solution.
                Su2RestartController.CleanSteadyRestartFiles(workingDirectory);
            }

            bool isControlRun = IsControlRunTag(ExtractTag(meshPath, record));
            SimulationConfig originalEffectiveConfig = isControlRun ? config.WithControlRunProfile() : config;
            if (isControlRun)
            {
                SimulationLog.Detail($"Control run: strict profile (up to {originalEffectiveConfig.Su2MaxIterations} iterations, window {originalEffectiveConfig.CauchyElements}, "
                    + $"iterative error at most {originalEffectiveConfig.IterativeToleranceFraction * 100:0}% of mesh tolerance).");
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

                    if (exitCode != 0)
                    {
                        SimulationLog.Error($"SU2 exited with code {exitCode} (diverged or aborted).");
                        Su2ResultArchiver.ArchiveFailureArtifacts(workingDirectory, meshPath, record, historyPath, configPath, su2Output, archiveSuffix);
                        record.SimulationFailed = true;
                        record.FailedStage = EvaluationFailureStage.Solver;
                        return;
                    }

                    if (restartCount == 0)
                    {
                        if (currentLines == null || currentLines.Length < 5)
                        {
                            Su2ResultArchiver.ArchiveFailureArtifacts(workingDirectory, meshPath, record, historyPath, configPath, su2Output, archiveSuffix);
                            record.FailedStage = EvaluationFailureStage.Solver;
                            return;
                        }
                    }
                    else
                    {
                        if (currentLines == null || currentLines.Length < 2)
                        {
                            SimulationLog.Warning($"[SU2] Restart #{restartCount} yielded no usable data; keeping previous history.");
                            if (previousCumulativeLines != null) File.WriteAllLines(historyPath, previousCumulativeLines);
                            break;
                        }

                        currentLines = StitchHistoryCsv(previousCumulativeLines!, currentLines);
                        File.WriteAllLines(historyPath, currentLines);
                    }

                    if (isUnsteady || !runOptions.AutoRestartOnDrift || runOptions.MaxConvergenceRestarts <= 0)
                        break;
                    if (restartCount >= runOptions.MaxConvergenceRestarts)
                        break;

                    if (!Su2RestartController.TryCheckNeedsSteadyRestart(
                            currentLines!, originalEffectiveConfig, runOptions.ResultMetrics,
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
                        + $"-> automatic restart #{restartCount}/{runOptions.MaxConvergenceRestarts} from {steadyRestartFile} "
                        + $"(remaining={remainingBudget}, CONV_CAUCHY_EPS={currentBlockEps}, CONV_STARTITER={convStartIter}).");
                }

                Su2ResultArchiver.ArchiveRunArtifacts(workingDirectory, meshPath, record, historyPath, configPath, archiveSuffix);
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

        private void ReportLowReynoldsIfNeeded(float reynolds, string? turbulenceModel)
        {
            if (_transitionalReported) return;
            string? warning = Su2RegimeAdvisor.CheckLowReynoldsWarning(reynolds, turbulenceModel);
            if (warning != null)
            {
                SimulationLog.Warning(warning);
                _transitionalReported = true;
            }
        }

        private static (float EffectiveRefArea, float PhysicalFullArea) ResolveEffectiveSymmetryAreas(
            ModelRecord record, float fullOrRecordedArea, SymmetryAxis symAxis)
        {
            if (symAxis == SymmetryAxis.None)
                return (fullOrRecordedArea, fullOrRecordedArea);

            bool clippedFromFull;
            if (record.PassiveParameters.TryGetValue("SymmetryClippedFromFull", out float clippedFlag))
            {
                clippedFromFull = clippedFlag > 0.5f;
            }
            else if (!string.IsNullOrEmpty(record.StlPath) && File.Exists(record.StlPath) &&
                     !record.StlPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            {
                clippedFromFull = IsFullBodyStlAlongAxis(record.StlPath, symAxis);
            }
            else
            {
                clippedFromFull = true;
            }

            record.PassiveParameters["SymmetryFactor"] = 2.0f;
            if (clippedFromFull)
                return (fullOrRecordedArea * 0.5f, fullOrRecordedArea);

            return (fullOrRecordedArea, fullOrRecordedArea * 2.0f);
        }

        private static bool IsFullBodyStlAlongAxis(string stlPath, SymmetryAxis axis)
        {
            try
            {
                MeshBounds bounds = StlWriter.ReadBounds(stlPath);
                if (bounds.IsEmpty) return true;

                float minC = axis == SymmetryAxis.Y0 ? bounds.Min.Y : bounds.Min.Z;
                float maxC = axis == SymmetryAxis.Y0 ? bounds.Max.Y : bounds.Max.Z;
                float span = MathF.Max(0f, maxC - minC);
                float tol = MathF.Max(1e-4f * span, 1e-5f);
                return minC < -tol && maxC > tol;
            }
            catch
            {
                return true;
            }
        }

        private static void CleanSurfaceFiles(string workingDirectory)
        {
            foreach (string name in new[] { "surface.csv", "surface_flow.csv", "surface.vtu", "surface_flow.vtu", "vol_solution.vtu" })
            {
                string path = Path.Combine(workingDirectory, name);
                if (File.Exists(path))
                {
                    try { File.Delete(path); } catch (IOException) { }
                }
            }
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
