using System;
using System.Collections.Generic;
using System.Linq;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Daten einer einzelnen Stufe der Netzkalibrierung (TODO-42).
    /// </summary>
    public sealed record CalibrationStageData(
        int LevelIndex,
        float Factor,
        int MeshCells,
        double DurationSeconds,
        IReadOnlyDictionary<string, float> Metrics,
        float Fitness,
        bool Converged,
        float YPlusP95,
        bool YPlusOk,
        IReadOnlyDictionary<string, float>? Uncertainty = null,
        bool CellLimitExceeded = false,
        int BoundaryLayerLayers = 0,
        float BoundaryLayerThicknessMm = 0f,
        int BoundaryLayerStage = 0);

    /// <summary>
    /// Ergebnis der Grid Convergence Index (GCI) Berechnung nach Celik et al. (2008).
    /// </summary>
    public sealed record GciResult(
        float ObservedOrderP,
        float GciFinePercent,
        float GciSelectedPercent,
        bool IsMonotonic,
        string? Warning,
        float ExtrapolatedValue = float.NaN,
        float ExtrapolatedDeviationPercent = float.NaN);

    /// <summary>
    /// Entscheidung und vollständige Auswertung einer Netzkalibrierung (TODO-42).
    /// </summary>
    public sealed record CalibrationDecision(
        bool Success,
        CalibrationStageData? SelectedStage,
        CalibrationStageData ReferenceStage,
        IReadOnlyList<CalibrationStageData> AllStages,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, float>> RelativeDeviationsToRef,
        GciResult? Gci,
        string SummaryMessage,
        IReadOnlyDictionary<int, IReadOnlyDictionary<string, float>>? NormalizedDeviationsToRef = null,
        CalibrationStageData? FallbackStage = null);

    /// <summary>
    /// Reine Bewertungslogik für die Netzkalibrierung (Abbruchkriterium, GCI, Monotonie).
    /// Frei von I/O für optimale Testbarkeit.
    /// </summary>
    public static class CalibrationEvaluator
    {
        /// <summary>
        /// Die fünf Standard-Verfeinerungsstufen mit konstantem Verhältnis r = √2 ≈ 1.414.
        /// </summary>
        public static readonly float[] StandardFactors = { 2.0f, 1.41f, 1.0f, 0.71f, 0.5f };

        /// <summary>
        /// Berechnet den Grid Convergence Index (GCI) nach Celik et al. (2008), ASME J. Fluids Eng.
        /// </summary>
        /// <param name="f3Coarse">Ergebnisgröße auf dem groben Netz.</param>
        /// <param name="f2Medium">Ergebnisgröße auf dem mittleren Netz.</param>
        /// <param name="f1Fine">Ergebnisgröße auf dem feinen Referenznetz.</param>
        /// <param name="r">Verfeinerungsverhältnis (Standard √2 ≈ 1.41421356f).</param>
        /// <param name="safetyFactorFs">Sicherheitsfaktor (Standard 1.25 für 3 Gitterweiten).</param>
        public static GciResult ComputeGci(
            float f3Coarse,
            float f2Medium,
            float f1Fine,
            float r = 1.41421356f,
            float safetyFactorFs = 1.25f)
        {
            float eps32 = f3Coarse - f2Medium;
            float eps21 = f2Medium - f1Fine;

            // Monotonieprüfung: Wechseln die Differenzen das Vorzeichen, ist die Folge nicht monoton.
            bool isMonotonic = (eps32 * eps21 > 0f);
            string? warning = isMonotonic
                ? null
                : "Konvergenzfolge ist nicht monoton (Vorzeichenwechsel der Differenzen).";

            // Beobachtete Ordnung p: für konstantes Verfeinerungsverhältnis r = r21 = r32
            float p;
            if (Math.Abs(eps21) <= 1e-9f || Math.Abs(eps32) <= 1e-9f)
            {
                p = 1.0f;
            }
            else
            {
                float ratio = Math.Abs(eps32 / eps21);
                p = (ratio > 1e-9f) ? Math.Abs(MathF.Log(ratio) / MathF.Log(r)) : 1.0f;
                p = Math.Clamp(p, 0.5f, 8.0f);
            }

            // Relative scheinbare Fehler e_a
            float ea21 = Math.Abs(f1Fine - f2Medium) / Math.Max(Math.Abs(f1Fine), 1e-7f);
            float ea32 = Math.Abs(f2Medium - f3Coarse) / Math.Max(Math.Abs(f2Medium), 1e-7f);

            // GCI_fine und GCI_selected (in Prozent)
            float denom = MathF.Pow(r, p) - 1.0f;
            float gciFine = (denom > 1e-9f) ? (safetyFactorFs * ea21 / denom) * 100.0f : 0f;
            float gciSelected = (denom > 1e-9f) ? (safetyFactorFs * ea32 / denom) * 100.0f : 0f;

            // Richardson-Extrapolation: Schätzung des netzunabhängigen Werts aus den zwei feinsten Stufen
            // (nur bei monotoner Folge sinnvoll). Der Bericht zeigt, wie weit das feinste Netz davon entfernt ist.
            float extrapolated = float.NaN;
            float extrapolatedDeviation = float.NaN;
            if (isMonotonic && denom > 1e-9f)
            {
                extrapolated = f1Fine + (f1Fine - f2Medium) / denom;
                extrapolatedDeviation = Math.Abs(f1Fine - extrapolated) / Math.Max(Math.Abs(extrapolated), 1e-7f) * 100.0f;
            }

            return new GciResult(p, gciFine, gciSelected, isMonotonic, warning, extrapolated, extrapolatedDeviation);
        }

        /// <summary>
        /// Abweichungen aller Stufen zur Referenzstufe: relativ (für Berichte) und normiert nach
        /// <see cref="MetricTolerance"/> (für die Entscheidung; höchstens 1 = in Ordnung, TODO-W1).
        /// </summary>
        public static (Dictionary<int, IReadOnlyDictionary<string, float>> Relative,
                       Dictionary<int, IReadOnlyDictionary<string, float>> Normalized)
            ComputeDeviations(
                IReadOnlyList<CalibrationStageData> stages,
                CalibrationStageData refStage,
                IReadOnlyList<string> metrics,
                float tolerance,
                IReadOnlyDictionary<string, float>? absoluteTolerances)
        {
            var relative = new Dictionary<int, IReadOnlyDictionary<string, float>>();
            var normalized = new Dictionary<int, IReadOnlyDictionary<string, float>>();

            foreach (var stage in stages)
            {
                var rel = new Dictionary<string, float>();
                var norm = new Dictionary<string, float>();
                foreach (string metric in metrics)
                {
                    float refVal = refStage.Metrics.TryGetValue(metric, out float rv) ? rv : 0f;
                    float stageVal = stage.Metrics.TryGetValue(metric, out float sv) ? sv : 0f;
                    rel[metric] = MetricTolerance.Relative(stageVal, refVal);

                    // Schwankende Rechnungen (Wirbelablösung): die zulässige Abweichung wächst mit der Unsicherheit beider Stufen.
                    float uStage = 0f, uRef = 0f;
                    stage.Uncertainty?.TryGetValue(metric, out uStage);
                    refStage.Uncertainty?.TryGetValue(metric, out uRef);
                    norm[metric] = MetricTolerance.Normalized(
                        stageVal, refVal, MetricTolerance.WithUncertainty(tolerance, uStage, uRef),
                        MetricTolerance.AbsoluteFor(metric, absoluteTolerances));
                }
                relative[stage.LevelIndex] = rel;
                normalized[stage.LevelIndex] = norm;
            }

            return (relative, normalized);
        }

        /// <summary>
        /// Wertet eine Kalibrierungsfolge aus und wählt die gröbste Stufe, die innerhalb der Toleranz liegt.
        /// Zulässig ist <c>|Δ| ≤ Toleranz · |Referenz| + Mindestbetrag</c> (<see cref="MetricTolerance"/>).
        /// </summary>
        public static CalibrationDecision EvaluateCalibration(
            IReadOnlyList<CalibrationStageData> stages,
            IReadOnlyCollection<string> monitoredMetrics,
            float tolerance,
            IReadOnlyDictionary<string, float>? absoluteTolerances = null)
        {
            if (stages == null || stages.Count == 0)
                throw new InvalidOperationException("[KALIBRIERUNG] Keine Stufen zur Auswertung übergeben.");

            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : new List<string> { "Drag" };

            // Referenzstufe ist standardmäßig die feinste gerechnete Stufe (kleinster Faktor)
            var refStage = stages.OrderBy(s => s.Factor).First();

            // Falls die feinste Stufe ungültig ist, prüfe zuerst, ob in stages eine andere gültige Stufe existiert,
            // die feiner als die gröbste Stufe ist. Nur wenn weniger als 2 Stufen existieren oder die feinste Stufe
            // der 5 Grundstufen ungültig ist (ohne gültige Zusatzstufe), bleibt es beim Fehler "Referenzstufe fehlgeschlagen".
            if (!refStage.Converged || !refStage.YPlusOk)
            {
                float coarsestFactor = stages.Max(s => s.Factor);
                var alternativeRef = stages
                    .Where(s => s.Converged && s.YPlusOk && s.Factor < coarsestFactor)
                    .OrderBy(s => s.Factor)
                    .FirstOrDefault();

                bool hasValidZusatzstufe = stages.Any(s => s.Factor < 0.5f && s.Converged && s.YPlusOk);
                bool baseStage5Valid = stages.Any(s => Math.Abs(s.Factor - 0.5f) < 0.05f && s.Converged && s.YPlusOk);
                bool baseStage5InvalidWithoutZusatz = !baseStage5Valid && !hasValidZusatzstufe;

                if (stages.Count >= 2 && !baseStage5InvalidWithoutZusatz && alternativeRef != null)
                {
                    refStage = alternativeRef;
                }
            }

            // Abweichungen aller Stufen zur Referenz: relativ (Bericht) und normiert (Entscheidung)
            var (deviations, normalizedDeviations) = ComputeDeviations(stages, refStage, metricsToMonitor, tolerance, absoluteTolerances);

            // GCI berechnen, falls mindestens 3 Stufen vorliegen
            GciResult? gci = null;
            if (stages.Count >= 3)
            {
                var sortedAsc = stages.Where(s => s.Factor >= refStage.Factor).OrderBy(s => s.Factor).ToList();
                if (sortedAsc.Count < 3)
                {
                    sortedAsc = stages.OrderBy(s => s.Factor).ToList();
                }

                var sFine = sortedAsc[0];
                var sMed = sortedAsc[1];
                var sCoarse = sortedAsc[2];

                string primaryMetric = metricsToMonitor[0];
                if (sFine.Metrics.ContainsKey(primaryMetric)
                    && sMed.Metrics.ContainsKey(primaryMetric)
                    && sCoarse.Metrics.ContainsKey(primaryMetric))
                {
                    float ratio = sMed.Factor / Math.Max(sFine.Factor, 1e-5f);
                    gci = ComputeGci(
                        sCoarse.Metrics[primaryMetric],
                        sMed.Metrics[primaryMetric],
                        sFine.Metrics[primaryMetric],
                        ratio);
                }
            }

            // Gültigkeit der Referenzstufe: Nicht konvergiert oder y+ verletzt -> Kalibrierung fehlgeschlagen
            if (!refStage.Converged || !refStage.YPlusOk)
            {
                string reason = !refStage.Converged
                    ? "Referenzstufe (feinstes Netz) ist numerisch nicht konvergiert."
                    : "y+ der Referenzstufe liegt außerhalb der Toleranz.";

                return new CalibrationDecision(
                    false,
                    null,
                    refStage,
                    stages,
                    deviations,
                    gci,
                    $"[KALIBRIERUNG] Kalibrierung fehlgeschlagen: {reason}",
                    normalizedDeviations);
            }

            // Kandidatensuche von grob nach fein (unter Ausschluss der Referenzstufe selbst)
            CalibrationStageData? selected = null;
            var candidates = stages
                .Where(s => s.Factor > refStage.Factor)
                .OrderByDescending(s => s.Factor);

            foreach (var candidate in candidates)
            {
                if (!candidate.Converged || !candidate.YPlusOk) continue;

                bool withinTolerance = true;
                foreach (string metric in metricsToMonitor)
                {
                    if (normalizedDeviations[candidate.LevelIndex].TryGetValue(metric, out float dev))
                    {
                        if (dev > 1.0f)
                        {
                            withinTolerance = false;
                            break;
                        }
                    }
                }

                if (withinTolerance)
                {
                    selected = candidate;
                    break; // Gröbste Stufe gefunden, die alle Kriterien erfüllt!
                }
            }

            if (selected != null)
            {
                string msg = $"Stufe {selected.LevelIndex} (Faktor {selected.Factor:0.00}) erfüllt Toleranz von {tolerance * 100:0.0} % gegenüber Referenzstufe {refStage.Factor:0.00}.";
                return new CalibrationDecision(true, selected, refStage, stages, deviations, gci, msg, normalizedDeviations);
            }
            else
            {
                string msg = $"Keine Grobstufe erreicht die Toleranz von {tolerance * 100:0.0} % gegenüber der Referenzstufe {refStage.Factor:0.00}.";
                var fallback = stages
                    .Where(s => s.Factor > refStage.Factor && s.Converged && s.YPlusOk)
                    .OrderBy(s => s.Factor)
                    .FirstOrDefault() ?? refStage;
                return new CalibrationDecision(false, null, refStage, stages, deviations, gci, msg, normalizedDeviations, fallback);
            }
        }

        /// <summary>
        /// Berechnet die 3 Bestätigungsfaktoren für die 3D-Bestätigung um den 2D-Kalibrierungsfaktor f2D (TODO-48).
        /// Geordnet von grob nach fein: { f2D * √2, f2D, f2D / √2 }.
        /// </summary>
        public static float[] Build3DConfirmationFactors(float factor2D)
        {
            float coarse = (float)Math.Round(factor2D * Math.Sqrt(2.0), 2);
            float medium = (float)Math.Round(factor2D, 2);
            float fine = (float)Math.Round(factor2D / Math.Sqrt(2.0), 2);
            return new[] { coarse, medium, fine };
        }

        /// <summary>
        /// Wertet die dreistufige 3D-Bestätigung um f2D aus (TODO-48).
        /// Feinste der drei Stufen ist die 3D-Referenz.
        /// Erfüllt die grobe Stufe (f2D * √2) die Kriterien (Konvergiert, YPlusOk, innerhalb Toleranz),
        /// wird diese gewählt. Sonst f2D. Erfüllt keine die Toleranz, ist Success = false.
        /// </summary>
        public static CalibrationDecision EvaluateConfirmation3D(
            float factor2D,
            IReadOnlyList<CalibrationStageData> stages3D,
            IReadOnlyCollection<string> monitoredMetrics,
            float tolerance,
            IReadOnlyDictionary<string, float>? absoluteTolerances = null)
        {
            if (stages3D == null || stages3D.Count == 0)
                throw new InvalidOperationException("[KALIBRIERUNG 3D] Keine 3D-Stufen zur Auswertung übergeben.");

            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : new List<string> { "Drag" };

            // Sortierung aufsteigend nach Faktor: feinstes Netz zuerst
            var sortedAsc = stages3D.OrderBy(s => s.Factor).ToList();
            var refStage = sortedAsc[0]; // feinste Stufe (kleinster Faktor, ca. f2D / √2)

            var (deviations, normalizedDeviations) = ComputeDeviations(stages3D, refStage, metricsToMonitor, tolerance, absoluteTolerances);

            // GCI berechnen aus den 3 Stufen
            GciResult? gci = null;
            if (stages3D.Count >= 3)
            {
                var sFine = sortedAsc[0];
                var sMed = sortedAsc[1];
                var sCoarse = sortedAsc[2];

                string primaryMetric = metricsToMonitor[0];
                if (sFine.Metrics.ContainsKey(primaryMetric)
                    && sMed.Metrics.ContainsKey(primaryMetric)
                    && sCoarse.Metrics.ContainsKey(primaryMetric))
                {
                    float ratio = sMed.Factor / Math.Max(sFine.Factor, 1e-5f);
                    gci = ComputeGci(
                        sCoarse.Metrics[primaryMetric],
                        sMed.Metrics[primaryMetric],
                        sFine.Metrics[primaryMetric],
                        ratio);
                }
            }

            // Gültigkeit der 3D-Referenzstufe
            if (!refStage.Converged || !refStage.YPlusOk)
            {
                string reason = !refStage.Converged
                    ? "3D-Referenzstufe (feinstes Netz) ist numerisch nicht konvergiert."
                    : "y+ der 3D-Referenzstufe liegt außerhalb der Toleranz.";

                return new CalibrationDecision(
                    false,
                    null,
                    refStage,
                    stages3D,
                    deviations,
                    gci,
                    $"[KALIBRIERUNG 3D] 3D-Bestätigung fehlgeschlagen: {reason}",
                    normalizedDeviations);
            }

            // Stufen identifizieren: coarse (größter Faktor) und medium (mittlerer Faktor)
            // Kandidatensuche von grob nach fein
            CalibrationStageData? coarseStage = sortedAsc.Count >= 3 ? sortedAsc[^1] : null;
            CalibrationStageData? mediumStage = sortedAsc.Count >= 2 ? sortedAsc[^2] : null;

            bool StageMatches(CalibrationStageData c)
            {
                if (!c.Converged || !c.YPlusOk) return false;
                if (!normalizedDeviations.TryGetValue(c.LevelIndex, out var devDict)) return false;
                foreach (string metric in metricsToMonitor)
                {
                    if (devDict.TryGetValue(metric, out float d) && d > 1.0f)
                        return false;
                }
                return true;
            }

            if (coarseStage != null && StageMatches(coarseStage))
            {
                string msg = $"Grobe 3D-Stufe (Faktor {coarseStage.Factor:0.00}, Stufe {coarseStage.LevelIndex}) erfüllt Toleranz von {tolerance * 100:0.0} % gegenüber 3D-Referenzstufe {refStage.Factor:0.00}.";
                return new CalibrationDecision(true, coarseStage, refStage, stages3D, deviations, gci, msg, normalizedDeviations);
            }

            if (mediumStage != null && StageMatches(mediumStage))
            {
                string msg = $"2D-Stufe (Faktor {mediumStage.Factor:0.00}, Stufe {mediumStage.LevelIndex}) in 3D bestätigt: erfüllt Toleranz von {tolerance * 100:0.0} % gegenüber 3D-Referenzstufe {refStage.Factor:0.00}.";
                return new CalibrationDecision(true, mediumStage, refStage, stages3D, deviations, gci, msg, normalizedDeviations);
            }

            string failMsg = $"Weder die grobe 3D-Stufe noch der 2D-Faktor ({factor2D:0.00}) erreichen in 3D die Toleranz von {tolerance * 100:0.0} % gegenüber der 3D-Referenzstufe {refStage.Factor:0.00}.";
            var fallback3D = (mediumStage != null && mediumStage.Converged && mediumStage.YPlusOk) ? mediumStage : refStage;
            return new CalibrationDecision(false, null, refStage, stages3D, deviations, gci, failMsg, normalizedDeviations, fallback3D);
        }
    }
}
