using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.Planar2D;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// SU2 CFD Solver. Koordiniert Konfigurationsgenerierung, Prozessausführung,
    /// automatische Konvergenz-Restarts, Ergebnisarchivierung und Diagnostikauswertung.
    /// </summary>
    public class Su2Solver : ISimulationSolver
    {
        private readonly Su2SolverOptions _options;
        private readonly FlowConditions? _flow;
        private readonly Su2DiagnosticsExtractor _diagnostics;

        /// <summary>
        /// Die Anströmung wird einmal pro Lauf gemeldet, nicht bei jeder Variante — sie ändert
        /// sich zwischen den Varianten nicht, und im Log soll sie trotzdem stehen (TODO-38).
        /// </summary>
        private bool _velocityReported;
        private bool _transitionalReported;
        private bool _versionReported;

        /// <param name="options">Vorgabedaten aus solvers/su2.json des Projekts; ohne Angabe gelten die Standardwerte.</param>
        /// <param name="flow">Gemeinsame Strömungsbedingungen (TODO-38); wenn null, werden sie aus config und options aufgelöst.</param>
        public Su2Solver(Su2SolverOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new Su2SolverOptions();
            _flow = flow;
            _diagnostics = new Su2DiagnosticsExtractor(_options);
        }

        public Su2SolverOptions Options => _options;
        public FlowConditions? Flow => _flow;

        /// <summary>
        /// Optionaler Test-Hook für die SU2-Prozessausführung (ermöglicht End-to-End-Tests von
        /// <see cref="Solve"/> ohne echtes <c>SU2_CFD</c>/<c>mpirun</c>).
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
                SimulationLog.Detail($"Anströmung: {flow.Velocity.ToString("0.###", CultureInfo.InvariantCulture)} m/s ({flow.VelocitySource}).");
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
                $"Anströmung {flow.Velocity.ToString("0.0", CultureInfo.InvariantCulture)} m/s, Re_L = {reynolds.ToString("0.0e0", CultureInfo.InvariantCulture)} (L = {lengthMm.ToString("0.0", CultureInfo.InvariantCulture)} mm {lengthSource})");

            if (!_transitionalReported && reynolds > 0f && reynolds < 5e5f)
            {
                SimulationLog.Detail("[HINWEIS] Re_L liegt im laminar-transitionalen Bereich (< 5e5); das Turbulenzmodell rechnet vollturbulent.");
                _transitionalReported = true;
            }

            SimulationLog.Detail($"Strömungsanalyse läuft für: {Path.GetFileName(meshPath)}...");

            string configPath = Path.Combine(workingDirectory, "current_config.cfg");
            string historyPath = Path.Combine(workingDirectory, "history.csv");

            bool isUnsteady = _options.TimeDomain != null && _options.TimeDomain.Enabled;
            Su2RestartController.CleanHistoryFiles(workingDirectory, historyPath);
            if (!isUnsteady)
            {
                // Verhindert, dass ein fehlender Restart-Export einer neuen Variante auf die Lösung der Vorgängervariante zurückfällt.
                Su2RestartController.CleanSteadyRestartFiles(workingDirectory);
            }

            float effectiveRefLength = ResolveEffectiveReferenceLength(record, is2dGeometry, lengthMm);

            bool isControlRun = IsControlRunTag(ExtractTag(meshPath, record));
            SimulationConfig originalEffectiveConfig = isControlRun ? config.WithControlRunProfile() : config;
            if (isControlRun)
            {
                SimulationLog.Detail($"Kontrollrechnung: strenges Profil (bis {originalEffectiveConfig.Su2MaxIterations} Iterationen, Fenster {originalEffectiveConfig.CauchyElements}, "
                    + $"iterativer Fehler höchstens {originalEffectiveConfig.IterativeToleranceFraction * 100:0} % der Netz-Toleranz).");
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
                            SimulationLog.Warning($"[SU2] Restart #{restartCount} lieferte keine verwertbaren Daten (ExitCode={exitCode}); nutze bisherigen Verlauf.");
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
                        $"[SU2] Drift nach {totalIterations} Iterationen ({worstNormalized.ToString("0.000", CultureInfo.InvariantCulture)}-fach der Grenze) "
                        + $"-> automatischer Restart #{restartCount}/{_options.MaxConvergenceRestarts} aus {steadyRestartFile} "
                        + $"(Restbudget={remainingBudget}, CONV_CAUCHY_EPS={currentBlockEps}, CONV_STARTITER={convStartIter}).");
                }

                Su2ResultArchiver.ArchiveRunArtifacts(workingDirectory, meshPath, record, historyPath, configPath);
            }
            catch (Exception e)
            {
                throw new Exception($"Kritischer Systemfehler beim Ausführen von SU2: {e.Message}");
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
                SimulationLog.Detail($"Anstellwinkel (aktiv optimiert): {effectiveAoA.ToString("0.##", CultureInfo.InvariantCulture)}°");

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
                        return (bounds.Size.X / scaleFactor, "aus der 2D-Bounding-Box");
                }
                catch { }
            }
            else if (!string.IsNullOrEmpty(record.StlPath) && File.Exists(record.StlPath))
            {
                try
                {
                    MeshBounds bounds = StlWriter.ReadBounds(record.StlPath);
                    if (!bounds.IsEmpty && bounds.Size.X > 0f)
                        return (bounds.Size.X / scaleFactor, "aus der Bounding Box");
                }
                catch { }
            }

            return (fallbackMm, "aus ReferenceLength");
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
