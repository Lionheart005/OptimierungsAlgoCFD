using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.Planar2D;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// SU2 CFD Solver. Führt eine inkompressible RANS-Simulation durch.
    ///
    /// Welche Größen aus der history.csv in <c>record.PassiveParameters</c> landen, steht in
    /// <see cref="Su2SolverOptions.ResultMetrics"/> — der Solver kennt seit TODO-26 keinen
    /// festen Metriknamen mehr, auch nicht im Fehlerfall.
    /// </summary>
    public class Su2Solver : ISimulationSolver
    {
        /// <summary>
        /// Umrechnung der Geometrie-Metriken (mm²) in SU2-Einheiten (m²). Das ganze Framework
        /// rechnet in mm — PicoGK, die STL-Dateien und die Gmsh-Skalierung ebenso.
        /// </summary>
        private const float SquareMmToSquareM = 0.000001f;
        private const float MmToM = 0.001f;

        /// <summary>
        /// Ersatz-Referenzfläche in mm², wenn die konfigurierte Metrik fehlt. Bewusst derselbe
        /// Wert wie vor TODO-12, damit sich im Fehlerfall nur die Meldung ändert, nicht die Zahl.
        /// </summary>
        private const float FallbackReferenceAreaMm2 = 1.0f;

        private readonly Su2SolverOptions _options;
        private readonly FlowConditions? _flow;

        /// <summary>
        /// Die Anströmung wird einmal pro Lauf gemeldet, nicht bei jeder Variante — sie ändert
        /// sich zwischen den Varianten nicht, und im Log soll sie trotzdem stehen (TODO-38).
        /// </summary>
        private bool _velocityReported;
        private bool _transitionalReported;
        private bool _versionReported;
        private bool _unknownResultAveragingReported;

        /// <param name="options">Vorgabedaten aus solvers/su2.json des Projekts; ohne Angabe gelten die Standardwerte.</param>
        /// <param name="flow">Gemeinsame Strömungsbedingungen (TODO-38); wenn null, werden sie aus config und options aufgelöst.</param>
        public Su2Solver(Su2SolverOptions? options = null, FlowConditions? flow = null)
        {
            _options = options ?? new Su2SolverOptions();
            _flow = flow;
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
            {
                record.PassiveParameters["AngleOfAttackDeg"] = effectiveAoA;
            }

            if (aoaFromActive)
            {
                SimulationLog.Detail($"Anstellwinkel (aktiv optimiert): {effectiveAoA.ToString("0.##", CultureInfo.InvariantCulture)}°");
            }

            FlowConditions flow = _flow ?? FlowConditions.From(config, _options);

            if (!_velocityReported)
            {
                SimulationLog.Detail($"Anströmung: {flow.Velocity.ToString("0.###", CultureInfo.InvariantCulture)} m/s ({flow.VelocitySource}).");
                _velocityReported = true;
            }

            float lengthMm;
            string lengthSource;
            float scaleFactor = record.PassiveParameters.TryGetValue("ScaleFactor", out float sf) && sf > 0f ? sf : 1.0f;

            bool is2dGeometry = _options.Dimensions == 2 ||
                                (!string.IsNullOrEmpty(record.StlPath) && record.StlPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

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
                                if (p != null && p.Length >= 2)
                                    bounds = bounds.Include(new Vector2(p[0], p[1]));
                            }
                        }
                    }

                    if (!bounds.IsEmpty && bounds.Size.X > 0f)
                    {
                        lengthMm = bounds.Size.X / scaleFactor;
                        lengthSource = "aus der 2D-Bounding-Box";
                    }
                    else
                    {
                        lengthMm = _options.ReferenceLength * 1000f;
                        lengthSource = "aus ReferenceLength";
                    }
                }
                catch
                {
                    lengthMm = _options.ReferenceLength * 1000f;
                    lengthSource = "aus ReferenceLength";
                }
            }
            else if (!string.IsNullOrEmpty(record.StlPath) && File.Exists(record.StlPath))
            {
                try
                {
                    MeshBounds bounds = StlWriter.ReadBounds(record.StlPath);
                    if (!bounds.IsEmpty && bounds.Size.X > 0f)
                    {
                        lengthMm = bounds.Size.X / scaleFactor;
                        lengthSource = "aus der Bounding Box";
                    }
                    else
                    {
                        lengthMm = _options.ReferenceLength * 1000f;
                        lengthSource = "aus ReferenceLength";
                    }
                }
                catch
                {
                    lengthMm = _options.ReferenceLength * 1000f;
                    lengthSource = "aus ReferenceLength";
                }
            }
            else
            {
                lengthMm = _options.ReferenceLength * 1000f;
                lengthSource = "aus ReferenceLength";
            }

            float reynolds = flow.ComputeReynoldsNumber(lengthMm * 0.001f);
            if (reynolds > 0f)
            {
                record.PassiveParameters["ReynoldsNumber"] = reynolds;
            }

            if (!record.PassiveParameters.ContainsKey("BoundaryLayerHexShare"))
            {
                float hexShare = GmshCfdMesher.ReadSu2BoundaryLayerHexShare(meshPath);
                if (float.IsFinite(hexShare))
                {
                    record.PassiveParameters["BoundaryLayerHexShare"] = hexShare;
                }
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
            CleanHistoryFiles(workingDirectory, historyPath);
            if (!isUnsteady)
            {
                // Verhindert, dass ein fehlender Restart-Export einer neuen Variante auf die Lösung der Vorgängervariante zurückfällt.
                CleanSteadyRestartFiles(workingDirectory);
            }

            float effectiveRefLength = _options.ReferenceLength;
            if (is2dGeometry && MathF.Abs(_options.ReferenceLength - 0.01f) < 1e-6f)
            {
                if (record.PassiveParameters.TryGetValue("Chord", out float chordMmVal) && chordMmVal > 0f)
                {
                    effectiveRefLength = chordMmVal * 0.001f;
                }
                else if (lengthMm > 0f && lengthMm != _options.ReferenceLength * 1000f)
                {
                    effectiveRefLength = lengthMm * 0.001f;
                }
            }

            // Kontrollrechnungen (Kalibrierstufen, Feinprüfung, Recheck) rechnen mit dem strengen
            // Profil (TODO-W4); normale Varianten und die Türsteher-Kontrolle mit den normalen Werten.
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
                    var (exitCode, su2Output) = ExecuteSu2Process(workingDirectory, blockConfig);

                    // Version aus dem Banner der ersten Rechnung in den Fahrtenschreiber (TODO-W2).
                    if (!_versionReported)
                    {
                        string? version = TryParseVersion(su2Output);
                        if (version != null)
                        {
                            _versionReported = true;
                            RunInfoWriter.SetProgramVersion(workingDirectory, "SU2", version);
                        }
                    }

                    // Nach einem Neustart schreibt SU2 die Historie als history_00002.csv (Neustart-Index im Namen).
                    // Die jüngste solche Datei wird zu history.csv (TODO-W7).
                    ResolveBlockHistoryFile(workingDirectory, historyPath);

                    string[]? currentLines = File.Exists(historyPath)
                        ? File.ReadAllLines(historyPath).Where(l => !string.IsNullOrWhiteSpace(l)).ToArray()
                        : null;

                    if (restartCount == 0)
                    {
                        bool isSu2Successful = currentLines != null && currentLines.Length >= 5;
                        if (!isSu2Successful)
                        {
                            string errorLogDirectory = Path.Combine(workingDirectory, "FehlerLogs");
                            Directory.CreateDirectory(errorLogDirectory);

                            string modelName = Path.GetFileNameWithoutExtension(meshPath);
                            string errorLogPath = Path.Combine(errorLogDirectory, $"SU2_Error_{modelName}.txt");
                            File.WriteAllText(errorLogPath, string.IsNullOrWhiteSpace(su2Output) ? "Kein Konsolen-Output von SU2 abgefangen." : su2Output);
                            Console.WriteLine();
                            SimulationLog.Warning($"CFD-Simulation für {modelName} abgebrochen.");
                            SimulationLog.Detail($"Details exportiert nach: Ergebnisse/FehlerLogs/{Path.GetFileName(errorLogPath)}");
                            Console.WriteLine();

                            // Nur noch das generische Flag: vor TODO-26 stand hier
                            // PassiveParameters["Drag"] = float.MaxValue und damit der letzte
                            // projektspezifische Begriff im Solver. Der IFitnessCalculator wertet
                            // das Flag ohnehin zuerst aus.
                            record.SimulationFailed = true;
                            string errorBase = ArchiveBaseNameFromMesh(meshPath, record);
                            TryCopyArchiveFile(historyPath, Path.Combine(errorLogDirectory, $"History_{errorBase}.csv"));
                            TryCopyArchiveFile(configPath, Path.Combine(errorLogDirectory, $"Config_{errorBase}.cfg"));
                            return;
                        }
                    }
                    else
                    {
                        if (exitCode != 0 || currentLines == null || currentLines.Length < 2)
                        {
                            SimulationLog.Warning($"[SU2] Restart #{restartCount} lieferte keine verwertbaren Daten (ExitCode={exitCode}); nutze bisherigen Verlauf.");
                            if (previousCumulativeLines != null)
                                File.WriteAllLines(historyPath, previousCumulativeLines);
                            break;
                        }

                        currentLines = StitchHistoryCsv(previousCumulativeLines!, currentLines);
                        File.WriteAllLines(historyPath, currentLines);
                    }

                    if (isUnsteady || !_options.AutoRestartOnDrift || _options.MaxConvergenceRestarts <= 0 || exitCode != 0)
                        break;

                    if (restartCount >= _options.MaxConvergenceRestarts)
                        break;

                    if (!TryCheckNeedsSteadyRestart(
                            currentLines!,
                            originalEffectiveConfig,
                            _options.ResultMetrics,
                            out int totalIterations,
                            out float worstNormalized))
                    {
                        break;
                    }

                    string? steadyRestartFile = FindSteadyRestartFile(workingDirectory);
                    if (steadyRestartFile == null)
                        break;

                    int remainingBudget = originalEffectiveConfig.Su2MaxIterations - totalIterations;
                    if (remainingBudget <= 0)
                        break;

                    restartCount++;
                    previousCumulativeLines = currentLines;
                    CleanHistoryFiles(workingDirectory, historyPath);

                    currentBlockEps = TightenTolerance(currentBlockEps);
                    int convStartIter = Math.Min(100, Math.Max(0, remainingBudget / 10));
                    blockConfig = originalEffectiveConfig.WithSu2BlockOverrides(remainingBudget, currentBlockEps);

                    Su2ConfigGenerator.Generate(
                        configPath,
                        meshPath,
                        refArea,
                        blockConfig,
                        runOptions,
                        flow,
                        effectiveRefLength,
                        steadyRestartFile,
                        convStartIter);

                    SimulationLog.Detail(
                        $"[SU2] Drift nach {totalIterations} Iterationen ({worstNormalized.ToString("0.000", CultureInfo.InvariantCulture)}-fach der Grenze) "
                        + $"-> automatischer Restart #{restartCount}/{_options.MaxConvergenceRestarts} aus {steadyRestartFile} "
                        + $"(Restbudget={remainingBudget}, CONV_CAUCHY_EPS={currentBlockEps}, CONV_STARTITER={convStartIter}).");
                }

                // VTU-Dateien sichern
                string analysisResultsDir = Path.Combine(workingDirectory, "Analyseergebnisse");
                Directory.CreateDirectory(analysisResultsDir);
                
                string defaultSurfaceVtu = Path.Combine(workingDirectory, "surface.vtu");
                // Mit Tag (Kal1..5, Fine, Recheck): eigener Name, sonst überschreibt z. B. die
                // Feinprüfung die Dateien der normalen Variante (Servertest 06.11.2026).
                string archiveBase = ArchiveBaseNameFromMesh(meshPath, record);
                string savedSurfaceVtu = Path.Combine(analysisResultsDir, $"Surface_{archiveBase}.vtu");
                if (File.Exists(defaultSurfaceVtu)) File.Copy(defaultSurfaceVtu, savedSurfaceVtu, true);

                string defaultSurfaceCsv = Path.Combine(workingDirectory, "surface.csv");
                string savedSurfaceCsv = Path.Combine(analysisResultsDir, $"Surface_{archiveBase}.csv");
                if (File.Exists(defaultSurfaceCsv)) File.Copy(defaultSurfaceCsv, savedSurfaceCsv, true);

                string defaultVolumeVtu = Path.Combine(workingDirectory, "vol_solution.vtu");
                string savedVolumeVtu = Path.Combine(analysisResultsDir, $"Volume_{archiveBase}.vtu");
                if (File.Exists(defaultVolumeVtu)) File.Copy(defaultVolumeVtu, savedVolumeVtu, true);

                // Verlauf und Konfiguration sichern (TODO-35)
                string verlaeufeDir = Path.Combine(analysisResultsDir, "Verlaeufe");
                Directory.CreateDirectory(verlaeufeDir);
                TryCopyArchiveFile(historyPath, Path.Combine(verlaeufeDir, $"History_{archiveBase}.csv"));
                TryCopyArchiveFile(configPath, Path.Combine(verlaeufeDir, $"Config_{archiveBase}.cfg"));
            }
            catch (Exception e)
            {
                throw new Exception($"Kritischer Systemfehler beim Ausführen von SU2: {e.Message}");
            }

            // Setzt bei einer nicht lesbaren Größe selbst record.SimulationFailed.
            ReadMetricsFromHistory(historyPath, record, originalEffectiveConfig, isControlRun);

            // y+-Statistik der Oberfläche auswerten (TODO-41)
            EvaluateSurfaceYPlus(Path.Combine(workingDirectory, "surface.csv"), record);

            // Druck-/Reibungswiderstand über surface.vtu integrieren (AP-6)
            int effectiveDims = is2dGeometry ? 2 : runOptions.Dimensions;
            EvaluateSurfaceAerodynamics(workingDirectory, refArea, effectiveAoA, effectiveDims, record);
        }

        /// <summary>
        /// Wertet optional <c>CD_p</c> und <c>CD_v</c> aus <c>surface.vtu</c> bzw. <c>surface_flow.vtu</c>
        /// aus und trägt <c>Su2_CD_p</c> und <c>Su2_CD_v</c> in <paramref name="record"/> ein.
        /// </summary>
        public AerodynamicDragBreakdown? EvaluateSurfaceAerodynamics(
            string workingDirectory,
            float refAreaOrChordM,
            float angleOfAttackDeg,
            int dimensions,
            ModelRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (refAreaOrChordM <= 0f || !float.IsFinite(refAreaOrChordM))
                return null;

            AerodynamicDragBreakdown? breakdown = Su2SurfaceAnalyzer.EvaluateDragBreakdownFromDirectory(
                workingDirectory, refAreaOrChordM, angleOfAttackDeg, dimensions);

            if (breakdown != null &&
                double.IsFinite(breakdown.PressureDragCoeff) &&
                double.IsFinite(breakdown.ViscousDragCoeff))
            {
                record.PassiveParameters["Su2_CD_p"] = (float)breakdown.PressureDragCoeff;
                record.PassiveParameters["Su2_CD_v"] = (float)breakdown.ViscousDragCoeff;
            }

            return breakdown;
        }

        /// <summary>
        /// Wertet die y+-Statistik der Bauteiloberfläche aus und trägt YPlus_Median, YPlus_P95 und YPlus_Max
        /// in <c>record.PassiveParameters</c> ein. Überschreitet P95 das Limit YPlusWarnFactor * TargetYPlus,
        /// wird eine Warnung ausgegeben (TODO-41).
        /// </summary>
        public YPlusStatistics? EvaluateSurfaceYPlus(string surfaceCsvPath, ModelRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            YPlusStatistics? stats = null;
            if (File.Exists(surfaceCsvPath))
            {
                try
                {
                    string[] lines = File.ReadAllLines(surfaceCsvPath);
                    stats = Su2SurfaceAnalyzer.EvaluateSurfaceCsv(lines);
                }
                catch
                {
                    stats = null;
                }
            }

            if (stats == null)
            {
                string? dir = Path.GetDirectoryName(surfaceCsvPath);
                if (!string.IsNullOrEmpty(dir))
                {
                    stats = Su2SurfaceAnalyzer.EvaluateFromDirectory(dir);
                }
            }

            if (stats != null)
            {
                record.PassiveParameters["YPlus_Median"] = stats.Median;
                record.PassiveParameters["YPlus_P95"] = stats.P95;
                record.PassiveParameters["YPlus_Max"] = stats.Max;

                float warnLimit = _options.YPlusWarnFactor * _options.TargetYPlus;
                if (stats.P95 > warnLimit)
                {
                    SimulationLog.Detail(
                        $"[WARNUNG] y+ (95 %-Quantil = {stats.P95.ToString("0.00", CultureInfo.InvariantCulture)}, Max = {stats.Max.ToString("0.00", CultureInfo.InvariantCulture)}) überschreitet das Limit {warnLimit.ToString("0.00", CultureInfo.InvariantCulture)} (YPlusWarnFactor × TargetYPlus) – Grenzschichtauflösung prüfen.");
                }
            }

            return stats;
        }

        /// <summary>
        /// Bestimmt SU2 REF_AREA aus den Metriken des Modells. Welche Metrik gemeint ist, steht
        /// in <see cref="Su2SolverOptions.ReferenceAreaMetric"/> — der Solver kennt keinen festen
        /// Namen mehr (TODO-12).
        ///
        /// In 3D (Dimensions != 2) ist die Referenzgröße eine Fläche in mm² und wird mit 1e-6 in m² umgerechnet.
        /// In 2D (Dimensions == 2 oder .json Kontur) ist REF_AREA die Sehnenlänge je Einheitsspannweite (1 m),
        /// also eine Länge in mm, die mit 1e-3 (0.001) in Meter umgerechnet wird (TODO-46).
        ///
        /// Fehlt die Metrik, wird weitergerechnet, aber <b>nicht stillschweigend</b>: der
        /// CD-Wert wäre dann um Größenordnungen falsch, und genau das war vorher nicht zu sehen.
        /// </summary>
        public float ResolveReferenceArea(ModelRecord record)
        {
            bool is2D = _options.Dimensions == 2 ||
                        (!string.IsNullOrEmpty(record.StlPath) && record.StlPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase));

            float conversionFactor = is2D ? MmToM : SquareMmToSquareM;
            string metric = (_options.ReferenceAreaMetric ?? string.Empty).Trim();

            if (metric.Length == 0)
            {
                WarnAboutMissingReferenceArea("Es ist keine Referenzflächen-Metrik konfiguriert", record, conversionFactor);
                return FallbackReferenceAreaMm2 * conversionFactor;
            }

            if (record.PassiveParameters.TryGetValue(metric, out float val))
                return val * conversionFactor;

            if (is2D && record.PassiveParameters.TryGetValue("Chord", out float chordVal))
                return chordVal * conversionFactor;

            WarnAboutMissingReferenceArea($"Die Metrik '{metric}' fehlt in den Ergebnissen dieses Modells", record, conversionFactor);
            return FallbackReferenceAreaMm2 * conversionFactor;
        }

        private static void WarnAboutMissingReferenceArea(string reason, ModelRecord record, float conversionFactor = SquareMmToSquareM)
        {
            string known = record.PassiveParameters.Count == 0
                ? "(keine)"
                : string.Join(", ", record.PassiveParameters.Keys);

            Console.WriteLine($"[WARNUNG] {reason} — SU2 rechnet ersatzweise mit REF_AREA = "
                              + (FallbackReferenceAreaMm2 * conversionFactor).ToString(CultureInfo.InvariantCulture)
                              + " m². Der CD-Wert ist damit um Größenordnungen falsch.");
            Console.WriteLine($"          Vorhandene Metriken: {known}.");
            Console.WriteLine("          Der Name gehört in die solvers/su2.json des Projekts unter 'ReferenceAreaMetric'.");
        }

        /// <summary>
        /// Übernimmt die in <see cref="Su2SolverOptions.ResultMetrics"/> zugeordneten Größen aus
        /// der letzten Zeile der SU2-history.csv in den Record (TODO-26). Vor TODO-26 las der
        /// Solver fest die erste Spalte, die mit <c>CD</c> anfing.
        ///
        /// Öffentlich, damit sie ohne MPI und SU2 testbar ist — dasselbe Muster wie
        /// <see cref="ResolveReferenceArea"/> (TODO-12).
        /// </summary>
        /// <remarks>
        /// Kann auch nur eine der konfigurierten Größen nicht gelesen werden, setzt die Methode
        /// <see cref="ModelRecord.SimulationFailed"/>. Wer eine Größe konfiguriert, braucht sie —
        /// eine Fitness aus halben Daten wäre schlimmer als ein ausgemustertes Modell.
        /// </remarks>
        public void ReadMetricsFromHistory(string historyPath, ModelRecord record, SimulationConfig? config = null, bool controlRun = false)
        {
            Dictionary<string, string> wanted = _options.ResultMetrics;

            if (wanted == null || wanted.Count == 0)
            {
                Console.WriteLine("[WARNUNG] Es ist keine Ergebnisgröße konfiguriert — SU2 hat gerechnet,");
                Console.WriteLine("          aber es wird nichts ausgelesen. Die Zuordnung gehört unter");
                Console.WriteLine("          'ResultMetrics' in die su2.json (z.B. \"Drag\": \"CD\").");
                record.SimulationFailed = true;
                return;
            }

            string[] lines;
            try
            {
                lines = File.ReadAllLines(historyPath);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"    -> Fehler beim Lesen der Ergebnisse: {ex.Message}");
                record.SimulationFailed = true;
                return;
            }

            if (lines.Length < 2)
            {
                Console.WriteLine($"    -> Fehler: {Path.GetFileName(historyPath)} enthält keine Datenzeile.");
                record.SimulationFailed = true;
                return;
            }

            string[] headers = SplitCsvLine(lines[0]);
            string[] finalValues = SplitCsvLine(lines[lines.Length - 1]);

            // Verlauf analysieren (TODO-35)
            SimulationConfig effectiveConfig = config ?? new SimulationConfig();
            var metricCols = wanted.Values.Distinct().ToList();
            var historyOptions = new Su2HistoryOptions(effectiveConfig.Su2MaxIterations, effectiveConfig.CauchyElements, metricCols);
            Su2HistoryResult historyResult = Su2HistoryAnalyzer.Evaluate(lines, historyOptions);

            // Iterativer Fehler (TODO-W3): Drift der Kennwerte im Endfenster statt "SU2 hat vor ITER
            // aufgehört". Bei zu kurzem Fenster (< Su2ConvergenceJudge.MinTailRows) gilt die alte Regel.
            Su2ConvergenceVerdict verdict = config != null
                ? Su2ConvergenceJudge.Judge(
                    historyResult, wanted,
                    effectiveConfig.IterativeToleranceFraction * effectiveConfig.MeshCalibration.Tolerance,
                    effectiveConfig.IterativeToleranceFraction,
                    effectiveConfig.MeshCalibration.AbsoluteTolerances)
                : new Su2ConvergenceVerdict(false, false, new Dictionary<string, float>(), null, 0f);
            bool converged = verdict.Assessed ? verdict.Converged : historyResult.Converged;

            if (config != null)
            {
                record.PassiveParameters["Su2_Iterations"] = historyResult.Iterations;
                record.PassiveParameters["Su2_Converged"] = converged ? 1.0f : 0.0f;

                foreach (KeyValuePair<string, string> entry in wanted)
                {
                    string metricName = (entry.Key ?? string.Empty).Trim();
                    string columnName = (entry.Value ?? string.Empty).Trim();
                    if (metricName.Length > 0 && OscillationFor(historyResult, columnName, out float osc))
                    {
                        record.PassiveParameters[$"Su2_Osc_{metricName}"] = osc;
                    }
                    if (metricName.Length > 0 && verdict.Normalized.TryGetValue(metricName, out float iterErr))
                    {
                        record.PassiveParameters[$"Su2_IterErr_{metricName}"] = iterErr;
                    }
                    if (metricName.Length > 0 && historyResult.TailUncertainty.TryGetValue(columnName, out float uncertainty))
                    {
                        record.PassiveParameters[MetricUncertainty.KeyFor(metricName)] = uncertainty;
                    }
                }
            }

            // Instationär, aber stabil (Paket B): die einfache Drift-Prüfung schlägt an, aber keiner der
            // Kennwerte zeigt einen statistisch gesicherten Trend. Typisch für Wirbelablösung: der Wert
            // pendelt um einen festen Mittelwert. Dann zählt der Mittelwert des Endfensters, mit seiner
            // Unsicherheit, und die Rechnung ist kein Fehlschlag.
            bool unsteadyStationary = verdict.Assessed && !converged && IsStationary(historyResult, wanted);
            if (config != null && verdict.Assessed)
            {
                record.PassiveParameters["Su2_Unsteady"] = unsteadyStationary ? 1.0f : 0.0f;
            }

            if (unsteadyStationary)
            {
                string uncertaintyText = string.Join(", ", wanted
                    .Where(e => historyResult.TailUncertainty.ContainsKey((e.Value ?? string.Empty).Trim()))
                    .Select(e => $"{e.Key} ±{(historyResult.TailUncertainty[(e.Value ?? string.Empty).Trim()] * 100f).ToString("0.0", CultureInfo.InvariantCulture)} %"));
                SimulationLog.Detail($"[HINWEIS] Instationär, aber statistisch stabil (kein gesicherter Trend im Endfenster): "
                    + $"es zählt der Mittelwert des Endfensters ({uncertaintyText}).");
            }
            else if (verdict.Assessed)
            {
                if (!converged)
                {
                    string worst = verdict.WorstMetric ?? ResolveConvergenceColumn(wanted);
                    string worstColumn = wanted.TryGetValue(worst, out string? col) ? col : worst;
                    float driftPercent = historyResult.WindowDrift.TryGetValue(worstColumn, out float dr)
                        && historyResult.TailMeans.TryGetValue(worstColumn, out float tm)
                        ? dr / Math.Max(Math.Abs(tm), 1e-7f) * 100f : float.NaN;
                    SimulationLog.Detail($"[WARNUNG] SU2 nicht auskonvergiert nach {historyResult.Iterations} Iterationen: {worst} driftet im Endfenster "
                        + $"({historyResult.TailRows} Zeilen) um {driftPercent.ToString("0.00", CultureInfo.InvariantCulture)} % "
                        + $"({verdict.WorstNormalized.ToString("0.000", CultureInfo.InvariantCulture)}-fach der erlaubten Drift).");

                    if (_options.TreatUnconvergedAsFailed)
                    {
                        record.SimulationFailed = true;
                    }
                }
                else if (historyResult.Iterations >= effectiveConfig.Su2MaxIterations)
                {
                    // ITER erreicht, aber die Kennwerte stehen: das Cauchy-Kriterium von SU2 ist bei
                    // schwingenden Rechnungen oft zu streng, die Rechnung ist trotzdem brauchbar.
                    SimulationLog.Detail($"SU2 hat ITER={effectiveConfig.Su2MaxIterations} erreicht, die Kennwerte stehen aber (iterativer Fehler "
                        + $"{verdict.WorstNormalized.ToString("0.000", CultureInfo.InvariantCulture)}-fach der Grenze, Ergebnis = Mittel des Endfensters).");
                }
            }
            else if (!historyResult.Converged)
            {
                string convCol = ResolveConvergenceColumn(wanted);
                SimulationLog.Detail($"[WARNUNG] SU2 hat ITER={effectiveConfig.Su2MaxIterations} erreicht, ohne dass das Kriterium erfüllt war – {convCol} ist der Stand bei Iteration {historyResult.Iterations}, kein konvergierter Wert.");

                if (_options.TreatUnconvergedAsFailed)
                {
                    record.SimulationFailed = true;
                }
            }

            foreach (KeyValuePair<string, string> entry in wanted)
            {
                string metricName = (entry.Key ?? string.Empty).Trim();
                string columnName = (entry.Value ?? string.Empty).Trim();
                if (OscillationFor(historyResult, columnName, out float osc) && osc > _options.OscillationWarnLimit)
                {
                    float percent = osc * 100f;
                    string shownName = metricName.Length > 0 ? metricName : columnName;
                    SimulationLog.Detail($"[WARNUNG] {shownName} schwingt um ±{percent.ToString("0.#", CultureInfo.InvariantCulture)} % – Hinweis auf instationäre Strömung (Wirbelablösung).");
                }
            }

            bool complete = true;

            foreach (KeyValuePair<string, string> entry in wanted)
            {
                string metricName = (entry.Key ?? string.Empty).Trim();
                string columnName = (entry.Value ?? string.Empty).Trim();

                if (metricName.Length == 0 || columnName.Length == 0)
                {
                    WarnAboutMetric(metricName, columnName,
                        "Metrikname oder Spaltenname ist leer.", headers);
                    complete = false;
                    continue;
                }

                // Zeitgenauer Modus: das Zeitmittel (tavg[CD]) statt des Momentanwerts nehmen.
                string problem = string.Empty;
                int index = (_options.TimeDomain != null && _options.TimeDomain.Enabled)
                    ? Array.FindIndex(headers, h => h.Equals($"tavg[{columnName}]", StringComparison.OrdinalIgnoreCase))
                    : -1;
                if (index >= 0)
                    Console.WriteLine($"    -> {metricName}: zeitgemittelter Wert (tavg[{columnName}]) aus dem URANS-Lauf.");
                else
                    index = FindColumn(headers, columnName, out problem);

                if (index < 0)
                {
                    WarnAboutMetric(metricName, columnName, problem, headers);
                    complete = false;
                    continue;
                }

                // Treffer, aber nur über das Präfix: verwendbar, trotzdem meldenswert.
                if (problem.Length > 0)
                    Console.WriteLine($"[WARNUNG] Ergebnisgröße '{metricName}': {problem}");

                if (index >= finalValues.Length)
                {
                    WarnAboutMetric(metricName, columnName,
                        $"Die letzte Zeile hat nur {finalValues.Length} Felder, die Spalte steht an Position {index + 1}.",
                        headers);
                    complete = false;
                    continue;
                }

                // float.IsFinite fängt zusätzlich "nan" und "inf" ab: beides parst .NET
                // klaglos, und eine divergierte Simulation liefert genau das. Ohne die
                // Prüfung stünde NaN als Metrik im Record und vergiftete jede Formel,
                // die damit rechnet.
                if (!float.TryParse(finalValues[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value))
                {
                    WarnAboutMetric(metricName, columnName,
                        $"SU2 hat keinen gültigen Zahlenwert geschrieben: '{finalValues[index]}'.", headers);
                    complete = false;
                    continue;
                }

                float finalMetricValue = value;
                string averaging = (_options.ResultAveraging ?? string.Empty).Trim();
                // Kontrollrechnungen liefern immer das Mittel des Endfensters, nie den letzten Zeitschritt.
                // Instationär-stabile Rechnungen ebenso: der letzte Wert wäre eine zufällige Phase der Schwingung.
                if (controlRun || unsteadyStationary) averaging = "TailMean";

                if (averaging.Equals("TailMean", StringComparison.OrdinalIgnoreCase))
                {
                    // Mittel über min(CauchyElements, Zeilen/2): ohne Anlaufphase (TODO-W3).
                    if (historyResult.TailRows >= 2
                        && historyResult.TailMeans.TryGetValue(columnName, out float tailVal)
                        && float.IsFinite(tailVal))
                    {
                        finalMetricValue = tailVal;
                    }
                }
                else if (averaging.Equals("CauchyWindowMean", StringComparison.OrdinalIgnoreCase))
                {
                    if (historyResult.WindowMeans.TryGetValue(columnName, out float meanVal) &&
                        float.IsFinite(meanVal))
                    {
                        finalMetricValue = meanVal;
                    }
                }
                else if (!averaging.Equals("Last", StringComparison.OrdinalIgnoreCase) && averaging.Length > 0)
                {
                    if (!_unknownResultAveragingReported)
                    {
                        Console.WriteLine($"[WARNUNG] Unbekanntes ResultAveraging '{_options.ResultAveraging}' — nutze 'Last'.");
                        _unknownResultAveragingReported = true;
                    }
                }

                record.PassiveParameters[metricName] = finalMetricValue;
                SimulationLog.Detail($"Analyse erfolgreich. {metricName} ({columnName}): {finalMetricValue:F5}");
            }

            if (!complete) record.SimulationFailed = true;
        }

        /// <summary>
        /// Instationär, aber stabil: für jeden Kennwert ist die Blockmittel-Statistik vorhanden und
        /// zeigt keinen gesicherten Trend (siehe <see cref="Su2HistoryAnalyzer.BatchStatistics"/>).
        /// </summary>
        internal static bool IsStationary(Su2HistoryResult history, IReadOnlyDictionary<string, string> wanted)
        {
            bool any = false;
            foreach (string column in wanted.Values)
            {
                string name = (column ?? string.Empty).Trim();
                if (name.Length == 0) continue;
                if (!history.TrendSignificant.TryGetValue(name, out bool significant)) return false;
                if (significant) return false;
                any = true;
            }
            return any;
        }

        /// <summary>
        /// Sucht die Spalte zu einem konfigurierten Namen. Zuerst <b>exakt</b> (Groß-/Kleinschreibung
        /// egal; Anführungszeichen und Leerzeichen hat <see cref="SplitCsvLine"/> schon entfernt).
        ///
        /// Erst wenn das leer ausgeht, greift ein Präfix-Treffer — aber nur, wenn er
        /// <b>eindeutig</b> ist. Vor TODO-26 suchte der Solver pauschal per
        /// <c>StartsWith("CD")</c>; bei Momenten wäre das fatal, weil <c>"CM"</c> auf CMx, CMy
        /// und CMz passt und allein die Spaltenreihenfolge entschieden hätte, welche gewinnt.
        /// Der Präfix-Weg bleibt trotzdem, weil SU2 je nach Version und MARKER_MONITORING auch
        /// Namen wie <c>CD(Wall)</c> schreibt.
        /// </summary>
        /// <param name="problem">
        /// Leer bei einem sauberen Treffer. Bei Rückgabe &lt; 0 die Begründung, bei einem
        /// Präfix-Treffer der Hinweis, welche Spalte ersatzweise genommen wurde.
        /// </param>
        /// <returns>Spaltenindex, oder -1 wenn die Spalte fehlt oder mehrdeutig ist.</returns>
        public static int FindColumn(string[] headers, string columnName, out string problem)
        {
            problem = string.Empty;

            int exact = Array.FindIndex(headers, h => h.Equals(columnName, StringComparison.OrdinalIgnoreCase));
            if (exact >= 0) return exact;

            var prefixHits = new List<int>();
            for (int i = 0; i < headers.Length; i++)
            {
                if (headers[i].StartsWith(columnName, StringComparison.OrdinalIgnoreCase))
                    prefixHits.Add(i);
            }

            if (prefixHits.Count == 1)
            {
                problem = $"Keine Spalte heißt genau '{columnName}' — ersatzweise '{headers[prefixHits[0]]}' verwendet.";
                return prefixHits[0];
            }

            if (prefixHits.Count > 1)
            {
                problem = $"'{columnName}' ist mehrdeutig und passt auf mehrere Spalten: "
                          + string.Join(", ", prefixHits.Select(i => headers[i]))
                          + ". Bitte den vollen Spaltennamen eintragen.";
                return -1;
            }

            problem = $"Die Spalte '{columnName}' steht nicht in der history.csv.";
            return -1;
        }

        /// <summary>
        /// Zerlegt eine Zeile der history.csv und räumt Anführungszeichen und die Auffüll-
        /// Leerzeichen weg, mit denen SU2 seine Spalten ausrichtet.
        /// </summary>
        public static string[] SplitCsvLine(string line)
        {
            string[] fields = line.Split(',');

            for (int i = 0; i < fields.Length; i++)
                fields[i] = fields[i].Replace("\"", "").Trim();

            return fields;
        }

        /// <summary>
        /// Meldet eine nicht lesbare Ergebnisgröße. Die vorhandenen Spalten mitzudrucken ist der
        /// eigentliche Nutzen — ein Tippfehler in der Zuordnung ist so sofort sichtbar, genau wie
        /// bei der Referenzflächen-Metrik aus TODO-12.
        /// </summary>
        private static void WarnAboutMetric(string metricName, string columnName, string problem, string[] headers)
        {
            string shownName = metricName.Length == 0 ? "(ohne Namen)" : metricName;
            string shownColumn = columnName.Length == 0 ? "(ohne Spalte)" : columnName;

            Console.WriteLine($"[WARNUNG] Ergebnisgröße '{shownName}' <- Spalte '{shownColumn}': {problem}");
            Console.WriteLine($"          Vorhandene Spalten: {(headers.Length == 0 ? "(keine)" : string.Join(", ", headers))}.");
            Console.WriteLine("          Die Zuordnung steht unter 'ResultMetrics' in der su2.json; welche Spalten");
            Console.WriteLine("          SU2 überhaupt schreibt, steuert 'HistoryOutput' (AERO_COEFF liefert die Beiwerte).");
            Console.WriteLine("          Dieses Modell wird als fehlgeschlagen gewertet.");
        }

        /// <summary>
        /// Bildet den Dateinamen-Stamm für die Archivierung von Verlaufs- und Konfigurationsdateien (TODO-35).
        /// Format: <c>Gen{i}_Var{v}</c> bzw. <c>Gen{i}_Var{v}_{tag}</c>.
        /// </summary>
        public static string ArchiveBaseName(ModelRecord record, string? tag = null)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            return string.IsNullOrWhiteSpace(tag)
                ? $"Gen{record.Iteration}_Var{record.Variant}"
                : $"Gen{record.Iteration}_Var{record.Variant}_{tag.Trim()}";
        }

        private static readonly System.Text.RegularExpressions.Regex VersionBanner = new(
            @"Release\s+(\d+\.\d+(?:\.\d+)?)|SU2\s+v(\d+\.\d+(?:\.\d+)?)",
            System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Liest die SU2-Version aus dem Startbanner der Konsolenausgabe
        /// (<c>Release 7.5.1 "Blackbird"</c> bzw. <c>SU2 v7.5.1</c>); <c>null</c>, wenn keines vorkommt.
        /// </summary>
        public static string? TryParseVersion(string? consoleOutput)
        {
            if (string.IsNullOrEmpty(consoleOutput)) return null;
            var match = VersionBanner.Match(consoleOutput);
            if (!match.Success) return null;
            return match.Groups[1].Success ? match.Groups[1].Value : match.Groups[2].Value;
        }

        /// <summary>Tag aus dem Netznamen (<c>Model_Gen1_Var1_Kal5.su2</c> → <c>Kal5</c>), sonst leer.</summary>
        public static string ExtractTag(string meshPath, ModelRecord record)
        {
            string plain = ArchiveBaseName(record);
            string baseName = ArchiveBaseNameFromMesh(meshPath, record);
            return baseName.StartsWith(plain + "_", StringComparison.Ordinal) ? baseName.Substring(plain.Length + 1) : string.Empty;
        }

        /// <summary>
        /// Gehört der Tag zu einer Kontrollrechnung (Kalibrierstufen <c>Kal…</c>, Feinprüfung <c>Fein…</c>,
        /// <c>Recheck</c>)? Die Türsteher-Kontrolle (ohne Tag) zählt bewusst nicht dazu: sie wird mit normalen
        /// Varianten verglichen und muss dieselbe Genauigkeit haben (TODO-W4).
        /// </summary>
        public static bool IsControlRunTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            string t = tag.Trim();
            return t.StartsWith("Kal", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Fein", StringComparison.OrdinalIgnoreCase)
                || t.StartsWith("Recheck", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Schwingungsmaß für Warnung und CSV: bei langem Endfenster die Amplitude nach Abzug des
        /// Trends (ohne Drift), sonst die rohe Spannweite wie bisher.
        /// </summary>
        private static bool OscillationFor(Su2HistoryResult history, string column, out float value)
        {
            if (history.TailRows >= Su2ConvergenceJudge.MinTailRows
                && history.OscillationAmplitude.TryGetValue(column, out value))
                return true;
            return history.Oscillation.TryGetValue(column, out value);
        }

        /// <summary>
        /// Archivname aus dem Netznamen: <c>Model_Gen1_Var1_Kal5.su2</c> → <c>Gen1_Var1_Kal5</c>.
        /// Der Tag (Kalibrierstufe, Feinprüfung, Recheck) steckt nur im Netznamen. Passt der Name
        /// nicht zum Schema, gilt <see cref="ArchiveBaseName(ModelRecord, string?)"/> ohne Tag.
        /// </summary>
        public static string ArchiveBaseNameFromMesh(string meshPath, ModelRecord record)
        {
            string plain = ArchiveBaseName(record);
            string name = Path.GetFileNameWithoutExtension(meshPath ?? string.Empty);
            const string prefix = "Model_";
            if (!name.StartsWith(prefix, StringComparison.Ordinal)) return plain;

            string rest = name.Substring(prefix.Length);
            if (rest == plain) return plain;
            if (rest.StartsWith(plain + "_", StringComparison.Ordinal))
                return ArchiveBaseName(record, rest.Substring(plain.Length + 1));
            return plain;
        }

        private static void TryCopyArchiveFile(string source, string destination)
        {
            try
            {
                if (File.Exists(source))
                {
                    File.Copy(source, destination, overwrite: true);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WARNUNG] Datei konnte nicht archiviert werden ({Path.GetFileName(source)} -> {destination}): {ex.Message}");
            }
        }

        private (int ExitCode, string CombinedOutput) ExecuteSu2Process(string workingDirectory, SimulationConfig config)
        {
            if (RunSu2Override != null)
            {
                var (exitCode, stdOut, stdErr) = RunSu2Override(workingDirectory, config.MpiCores);
                string combined = string.Concat(
                    stdOut ?? string.Empty,
                    !string.IsNullOrEmpty(stdOut) && !string.IsNullOrEmpty(stdErr) ? Environment.NewLine : string.Empty,
                    stdErr ?? string.Empty);
                return (exitCode, combined);
            }

            using Process su2Process = new Process();
            string resolvedMpi = ExecutableResolver.Resolve(config.MpiRunPath, "mpi");
            string resolvedSu2 = ExecutableResolver.Resolve(config.Su2Path, "su2");
            su2Process.StartInfo.FileName = resolvedMpi;
            su2Process.StartInfo.Arguments = $"-n {config.MpiCores} {resolvedSu2} current_config.cfg";
            su2Process.StartInfo.WorkingDirectory = workingDirectory;
            su2Process.StartInfo.RedirectStandardOutput = true;
            su2Process.StartInfo.RedirectStandardError = true;

            var su2Output = new System.Text.StringBuilder();
            object outputLock = new object();

            su2Process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) lock (outputLock) { su2Output.AppendLine(e.Data); }
            };
            su2Process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data)) lock (outputLock) { su2Output.AppendLine(e.Data); }
            };

            su2Process.Start();
            su2Process.BeginOutputReadLine();
            su2Process.BeginErrorReadLine();
            su2Process.WaitForExit();

            return (su2Process.ExitCode, su2Output.ToString());
        }

        private static void CleanHistoryFiles(string workingDirectory, string historyPath)
        {
            if (File.Exists(historyPath)) File.Delete(historyPath);
            // Alte Neustart-Historien (history_00002.csv) würden sonst fälschlich übernommen (TODO-W7).
            foreach (string stale in Directory.GetFiles(workingDirectory, "history_*.csv"))
            {
                try { File.Delete(stale); } catch (IOException) { }
            }
        }

        private static void CleanSteadyRestartFiles(string workingDirectory)
        {
            foreach (string name in new[] { "restart.dat", "restart_flow.dat" })
            {
                string fullPath = Path.Combine(workingDirectory, name);
                if (File.Exists(fullPath))
                {
                    try { File.Delete(fullPath); } catch (IOException) { }
                }
            }
        }

        private static void ResolveBlockHistoryFile(string workingDirectory, string historyPath)
        {
            if (!File.Exists(historyPath))
            {
                string? restarted = Directory.GetFiles(workingDirectory, "history_*.csv")
                    .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
                if (restarted != null) File.Copy(restarted, historyPath, overwrite: true);
            }
        }

        private static bool TryCheckNeedsSteadyRestart(
            string[] lines,
            SimulationConfig originalEffectiveConfig,
            Dictionary<string, string>? wanted,
            out int totalIterations,
            out float worstNormalized)
        {
            totalIterations = 0;
            worstNormalized = 0f;

            if (wanted == null || wanted.Count == 0)
                return false;

            var metricCols = wanted.Values.Where(v => !string.IsNullOrWhiteSpace(v)).Distinct().ToList();
            if (metricCols.Count == 0)
                return false;

            var historyOptions = new Su2HistoryOptions(
                originalEffectiveConfig.Su2MaxIterations,
                originalEffectiveConfig.CauchyElements,
                metricCols);
            Su2HistoryResult historyResult = Su2HistoryAnalyzer.Evaluate(lines, historyOptions);
            totalIterations = historyResult.Iterations;

            if (totalIterations <= 0 || totalIterations >= originalEffectiveConfig.Su2MaxIterations)
                return false;

            Su2ConvergenceVerdict verdict = Su2ConvergenceJudge.Judge(
                historyResult,
                wanted,
                originalEffectiveConfig.IterativeToleranceFraction * originalEffectiveConfig.MeshCalibration.Tolerance,
                originalEffectiveConfig.IterativeToleranceFraction,
                originalEffectiveConfig.MeshCalibration.AbsoluteTolerances);

            if (!verdict.Assessed)
                return false;

            worstNormalized = verdict.WorstNormalized;
            if (verdict.Converged)
                return false;

            if (IsStationary(historyResult, wanted))
                return false;

            return true;
        }

        /// <summary>
        /// Sucht im Arbeitsverzeichnis nach einer vorhandenen stationären Restart-Lösung
        /// (<c>restart.dat</c> oder <c>restart_flow.dat</c>, Größe &gt; 0) und wählt bei Vorhandensein
        /// beider die jüngste Datei aus.
        /// </summary>
        internal static string? FindSteadyRestartFile(string workingDirectory)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
                return null;

            var candidates = new[] { "restart.dat", "restart_flow.dat" };
            FileInfo? best = null;
            foreach (string name in candidates)
            {
                string fullPath = Path.Combine(workingDirectory, name);
                if (!File.Exists(fullPath)) continue;
                var info = new FileInfo(fullPath);
                if (info.Length <= 0) continue;
                if (best == null || info.LastWriteTimeUtc > best.LastWriteTimeUtc)
                {
                    best = info;
                }
            }
            return best?.Name;
        }

        /// <summary>
        /// Verschärft die Cauchy-Toleranz für den nächsten Restart-Block um Faktor 10
        /// (Untergrenze <c>1e-7</c>).
        /// </summary>
        internal static string TightenTolerance(string? currentTolerance)
        {
            const double Floor = 1e-7;
            if (!double.TryParse(currentTolerance, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || !double.IsFinite(value)
                || value <= 0.0)
            {
                return "1e-6";
            }

            double tightened = Math.Max(value / 10.0, Floor);
            return tightened.ToString("0.###e-0", CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Fügt die CSV-Daten eines Restart-Blocks nahtlos an die bisherige kumulative Historie an
        /// und setzt die Iterationsspalte monoton fort, falls SU2 beim Neustart wieder kleiner gezählt hat.
        /// </summary>
        internal static string[] StitchHistoryCsv(string[] previousLines, string[] newLines)
        {
            string[] prevValid = previousLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();
            string[] newValid = newLines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray();

            if (prevValid.Length < 2) return newValid;
            if (newValid.Length < 2) return prevValid;

            string[] prevHeaders = SplitCsvLine(prevValid[0]);
            string[] newHeaders = SplitCsvLine(newValid[0]);

            int prevIterCol = FindIterationColumn(prevHeaders);
            int newIterCol = FindIterationColumn(newHeaders);

            int prevLastIter = prevValid.Length - 2;
            if (prevIterCol >= 0)
            {
                string[] prevLastFields = SplitCsvLine(prevValid[^1]);
                if (prevIterCol < prevLastFields.Length &&
                    int.TryParse(prevLastFields[prevIterCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedLast) &&
                    parsedLast >= 0)
                {
                    prevLastIter = parsedLast;
                }
            }

            int firstNewIter = 0;
            if (newIterCol >= 0)
            {
                string[] firstNewFields = SplitCsvLine(newValid[1]);
                if (newIterCol < firstNewFields.Length &&
                    int.TryParse(firstNewFields[newIterCol], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedFirst) &&
                    parsedFirst >= 0)
                {
                    firstNewIter = parsedFirst;
                }
            }

            int iterOffset = firstNewIter <= prevLastIter ? (prevLastIter + 1 - firstNewIter) : 0;

            var combined = new List<string>(prevValid.Length + newValid.Length - 1);
            combined.AddRange(prevValid);

            for (int i = 1; i < newValid.Length; i++)
            {
                string row = newValid[i];
                if (newIterCol >= 0 && iterOffset > 0)
                {
                    string[] rawFields = row.Split(',');
                    if (newIterCol < rawFields.Length)
                    {
                        string cleaned = rawFields[newIterCol].Replace("\"", "").Trim();
                        if (int.TryParse(cleaned, NumberStyles.Integer, CultureInfo.InvariantCulture, out int rowIter))
                        {
                            rawFields[newIterCol] = (rowIter + iterOffset).ToString(CultureInfo.InvariantCulture);
                            row = string.Join(", ", rawFields.Select(f => f.Trim()));
                        }
                    }
                }
                combined.Add(row);
            }

            return combined.ToArray();
        }

        private static int FindIterationColumn(string[] headers)
        {
            int col = FindColumn(headers, "Inner_Iter", out _);
            if (col < 0) col = FindColumn(headers, "ITER", out _);
            if (col < 0) col = FindColumn(headers, "Iter", out _);
            return col;
        }

        private string ResolveConvergenceColumn(Dictionary<string, string> wanted)
        {
            string convField = (_options.ConvergenceField ?? string.Empty).Trim();
            if (convField.Length > 0)
            {
                foreach (var entry in wanted)
                {
                    if (entry.Key.Equals(convField, StringComparison.OrdinalIgnoreCase) ||
                        entry.Value.Equals(convField, StringComparison.OrdinalIgnoreCase))
                    {
                        return entry.Value;
                    }
                }
            }

            return wanted.Values.FirstOrDefault() ?? "CD";
        }
    }
}
