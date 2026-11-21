using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using OptimierungsAlgoCFD.Core;

namespace OptimierungsAlgoCFD.Solvers.Cfd
{
    /// <summary>
    /// Extrahiert Konvergenz-, History- und Oberflächendiagnostik (Aerodynamik, y+, Referenzgröße)
    /// aus SU2-Ausgabedateien in <see cref="ModelRecord"/>.
    /// </summary>
    internal sealed class Su2DiagnosticsExtractor
    {
        private const float SquareMmToSquareM = 0.000001f;
        private const float MmToM = 0.001f;
        private const float FallbackReferenceAreaMm2 = 1.0f;

        private readonly Su2SolverOptions _options;
        private bool _unknownResultAveragingReported;

        public Su2DiagnosticsExtractor(Su2SolverOptions options)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public AerodynamicDragBreakdown? EvaluateSurfaceAerodynamics(
            string workingDirectory, float refAreaOrChordM, float angleOfAttackDeg, int dimensions, ModelRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));
            if (refAreaOrChordM <= 0f || !float.IsFinite(refAreaOrChordM)) return null;

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

        public YPlusStatistics? EvaluateSurfaceYPlus(string surfaceCsvPath, ModelRecord record)
        {
            if (record == null) throw new ArgumentNullException(nameof(record));

            YPlusStatistics? stats = null;
            if (File.Exists(surfaceCsvPath))
            {
                try { stats = Su2SurfaceAnalyzer.EvaluateSurfaceCsv(File.ReadAllLines(surfaceCsvPath)); }
                catch { stats = null; }
            }

            if (stats == null)
            {
                string? dir = Path.GetDirectoryName(surfaceCsvPath);
                if (!string.IsNullOrEmpty(dir)) stats = Su2SurfaceAnalyzer.EvaluateFromDirectory(dir);
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

            SimulationConfig effectiveConfig = config ?? new SimulationConfig();
            var metricCols = wanted.Values.Distinct().ToList();
            var historyOptions = new Su2HistoryOptions(effectiveConfig.Su2MaxIterations, effectiveConfig.CauchyElements, metricCols);
            Su2HistoryResult historyResult = Su2HistoryAnalyzer.Evaluate(lines, historyOptions);

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
                RecordConvergenceDiagnostics(record, wanted, historyResult, verdict, converged);
            }

            // Instationär, aber stabil: die einfache Drift-Prüfung schlägt an, aber keiner der
            // Kennwerte zeigt einen statistisch gesicherten Trend -> Endfenstermittel zählt.
            bool unsteadyStationary = verdict.Assessed && !converged && IsStationary(historyResult, wanted);
            if (config != null && verdict.Assessed)
            {
                record.PassiveParameters["Su2_Unsteady"] = unsteadyStationary ? 1.0f : 0.0f;
            }

            LogConvergenceAndOscillations(record, wanted, effectiveConfig, historyResult, verdict, converged, unsteadyStationary);

            bool complete = ExtractWantedMetrics(record, wanted, headers, finalValues, historyResult, controlRun, unsteadyStationary);
            if (!complete) record.SimulationFailed = true;
        }

        private static void RecordConvergenceDiagnostics(
            ModelRecord record, Dictionary<string, string> wanted, Su2HistoryResult historyResult,
            Su2ConvergenceVerdict verdict, bool converged)
        {
            record.PassiveParameters["Su2_Iterations"] = historyResult.Iterations;
            record.PassiveParameters["Su2_Converged"] = converged ? 1.0f : 0.0f;

            foreach (KeyValuePair<string, string> entry in wanted)
            {
                string metricName = (entry.Key ?? string.Empty).Trim();
                string columnName = (entry.Value ?? string.Empty).Trim();
                if (metricName.Length == 0) continue;

                if (OscillationFor(historyResult, columnName, out float osc))
                    record.PassiveParameters[$"Su2_Osc_{metricName}"] = osc;
                if (verdict.Normalized.TryGetValue(metricName, out float iterErr))
                    record.PassiveParameters[$"Su2_IterErr_{metricName}"] = iterErr;
                if (historyResult.TailUncertainty.TryGetValue(columnName, out float uncertainty))
                    record.PassiveParameters[MetricUncertainty.KeyFor(metricName)] = uncertainty;
            }
        }

        private void LogConvergenceAndOscillations(
            ModelRecord record, Dictionary<string, string> wanted, SimulationConfig effectiveConfig,
            Su2HistoryResult historyResult, Su2ConvergenceVerdict verdict, bool converged, bool unsteadyStationary)
        {
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
                    string worst = verdict.WorstMetric ?? Su2RestartController.ResolveConvergenceColumn(_options, wanted);
                    string worstColumn = wanted.TryGetValue(worst, out string? col) ? col : worst;
                    float driftPercent = historyResult.WindowDrift.TryGetValue(worstColumn, out float dr)
                        && historyResult.TailMeans.TryGetValue(worstColumn, out float tm)
                        ? dr / Math.Max(Math.Abs(tm), 1e-7f) * 100f : float.NaN;
                    SimulationLog.Detail($"[WARNUNG] SU2 nicht auskonvergiert nach {historyResult.Iterations} Iterationen: {worst} driftet im Endfenster "
                        + $"({historyResult.TailRows} Zeilen) um {driftPercent.ToString("0.00", CultureInfo.InvariantCulture)} % "
                        + $"({verdict.WorstNormalized.ToString("0.000", CultureInfo.InvariantCulture)}-fach der erlaubten Drift).");

                    if (_options.TreatUnconvergedAsFailed) record.SimulationFailed = true;
                }
                else if (historyResult.Iterations >= effectiveConfig.Su2MaxIterations)
                {
                    SimulationLog.Detail($"SU2 hat ITER={effectiveConfig.Su2MaxIterations} erreicht, die Kennwerte stehen aber (iterativer Fehler "
                        + $"{verdict.WorstNormalized.ToString("0.000", CultureInfo.InvariantCulture)}-fach der Grenze, Ergebnis = Mittel des Endfensters).");
                }
            }
            else if (!historyResult.Converged)
            {
                string convCol = Su2RestartController.ResolveConvergenceColumn(_options, wanted);
                SimulationLog.Detail($"[WARNUNG] SU2 hat ITER={effectiveConfig.Su2MaxIterations} erreicht, ohne dass das Kriterium erfüllt war – {convCol} ist der Stand bei Iteration {historyResult.Iterations}, kein konvergierter Wert.");

                if (_options.TreatUnconvergedAsFailed) record.SimulationFailed = true;
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
        }

        private bool ExtractWantedMetrics(
            ModelRecord record, Dictionary<string, string> wanted, string[] headers,
            string[] finalValues, Su2HistoryResult historyResult, bool controlRun, bool unsteadyStationary)
        {
            bool complete = true;

            foreach (KeyValuePair<string, string> entry in wanted)
            {
                string metricName = (entry.Key ?? string.Empty).Trim();
                string columnName = (entry.Value ?? string.Empty).Trim();

                if (metricName.Length == 0 || columnName.Length == 0)
                {
                    WarnAboutMetric(metricName, columnName, "Metrikname oder Spaltenname ist leer.", headers);
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

                // float.IsFinite fängt zusätzlich "nan" und "inf" ab, damit NaN keine Formel vergiftet.
                if (!float.TryParse(finalValues[index], NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                    || !float.IsFinite(value))
                {
                    WarnAboutMetric(metricName, columnName,
                        $"SU2 hat keinen gültigen Zahlenwert geschrieben: '{finalValues[index]}'.", headers);
                    complete = false;
                    continue;
                }

                float finalMetricValue = ResolveAveragedMetricValue(historyResult, columnName, value, controlRun, unsteadyStationary);
                record.PassiveParameters[metricName] = finalMetricValue;
                SimulationLog.Detail($"Analyse erfolgreich. {metricName} ({columnName}): {finalMetricValue:F5}");
            }

            return complete;
        }

        private float ResolveAveragedMetricValue(
            Su2HistoryResult historyResult, string columnName, float lastValue, bool controlRun, bool unsteadyStationary)
        {
            string averaging = (_options.ResultAveraging ?? string.Empty).Trim();
            // Kontrollrechnungen und instationär-stabile Rechnungen liefern immer das Endfenstermittel.
            if (controlRun || unsteadyStationary) averaging = "TailMean";

            if (averaging.Equals("TailMean", StringComparison.OrdinalIgnoreCase))
            {
                if (historyResult.TailRows >= 2 && historyResult.TailMeans.TryGetValue(columnName, out float tailVal) && float.IsFinite(tailVal))
                    return tailVal;
            }
            else if (averaging.Equals("CauchyWindowMean", StringComparison.OrdinalIgnoreCase))
            {
                if (historyResult.WindowMeans.TryGetValue(columnName, out float meanVal) && float.IsFinite(meanVal))
                    return meanVal;
            }
            else if (!averaging.Equals("Last", StringComparison.OrdinalIgnoreCase) && averaging.Length > 0 && !_unknownResultAveragingReported)
            {
                Console.WriteLine($"[WARNUNG] Unbekanntes ResultAveraging '{_options.ResultAveraging}' — nutze 'Last'.");
                _unknownResultAveragingReported = true;
            }

            return lastValue;
        }

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

        public static string[] SplitCsvLine(string line)
        {
            string[] fields = line.Split(',');
            for (int i = 0; i < fields.Length; i++)
                fields[i] = fields[i].Replace("\"", "").Trim();
            return fields;
        }

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

        private static bool OscillationFor(Su2HistoryResult history, string column, out float value)
        {
            if (history.TailRows >= Su2ConvergenceJudge.MinTailRows
                && history.OscillationAmplitude.TryGetValue(column, out value))
                return true;
            return history.Oscillation.TryGetValue(column, out value);
        }
    }
}
