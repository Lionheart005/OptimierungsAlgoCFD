using System;
using System.Collections.Generic;
using System.Linq;

namespace MyPicoGkProject.Core
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
        bool YPlusOk);

    /// <summary>
    /// Ergebnis der Grid Convergence Index (GCI) Berechnung nach Celik et al. (2008).
    /// </summary>
    public sealed record GciResult(
        float ObservedOrderP,
        float GciFinePercent,
        float GciSelectedPercent,
        bool IsMonotonic,
        string? Warning);

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
        string SummaryMessage);

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

            return new GciResult(p, gciFine, gciSelected, isMonotonic, warning);
        }

        /// <summary>
        /// Wertet eine Kalibrierungsfolge aus und wählt die gröbste Stufe, die innerhalb der Toleranz liegt.
        /// </summary>
        public static CalibrationDecision EvaluateCalibration(
            IReadOnlyList<CalibrationStageData> stages,
            IReadOnlyCollection<string> monitoredMetrics,
            float tolerance)
        {
            if (stages == null || stages.Count == 0)
                throw new InvalidOperationException("[KALIBRIERUNG] Keine Stufen zur Auswertung übergeben.");

            var metricsToMonitor = (monitoredMetrics != null && monitoredMetrics.Count > 0)
                ? monitoredMetrics.ToList()
                : new List<string> { "Drag" };

            // Referenzstufe ist die feinste gerechnete Stufe (kleinster Faktor)
            var refStage = stages.OrderBy(s => s.Factor).First();

            // Relative Abweichungen aller Stufen zur Referenz berechnen
            var deviations = new Dictionary<int, IReadOnlyDictionary<string, float>>();
            foreach (var stage in stages)
            {
                var stageDevs = new Dictionary<string, float>();
                foreach (string metric in metricsToMonitor)
                {
                    float refVal = refStage.Metrics.TryGetValue(metric, out float rv) ? rv : 0f;
                    float stageVal = stage.Metrics.TryGetValue(metric, out float sv) ? sv : 0f;
                    float dev = Math.Abs(stageVal - refVal) / Math.Max(Math.Abs(refVal), 1e-7f);
                    stageDevs[metric] = dev;
                }
                deviations[stage.LevelIndex] = stageDevs;
            }

            // GCI berechnen, falls mindestens 3 Stufen vorliegen
            GciResult? gci = null;
            if (stages.Count >= 3)
            {
                var sortedAsc = stages.OrderBy(s => s.Factor).ToList();
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
                    $"[KALIBRIERUNG] Kalibrierung fehlgeschlagen: {reason}");
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
                    if (deviations[candidate.LevelIndex].TryGetValue(metric, out float dev))
                    {
                        if (dev > tolerance)
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
                return new CalibrationDecision(true, selected, refStage, stages, deviations, gci, msg);
            }
            else
            {
                string msg = $"Keine Grobstufe erreicht die Toleranz von {tolerance * 100:0.0} % gegenüber der Referenzstufe {refStage.Factor:0.00}.";
                return new CalibrationDecision(false, null, refStage, stages, deviations, gci, msg);
            }
        }
    }
}
