using System;
using System.Collections.Generic;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>Urteil über den iterativen Fehler einer SU2-Rechnung (TODO-W3).</summary>
    /// <param name="Assessed">
    /// Falsch, wenn das Endfenster für eine Beurteilung zu kurz war (weniger als
    /// <see cref="Su2ConvergenceJudge.MinTailRows"/> Zeilen). Dann gilt weiter die alte Regel
    /// „SU2 hat vor ITER aufgehört“.
    /// </param>
    /// <param name="Converged">Alle Kennwerte haben einen normierten iterativen Fehler von höchstens 1.</param>
    /// <param name="Normalized">Normierter iterativer Fehler je Kennwertname (≤ 1 = in Ordnung).</param>
    /// <param name="WorstMetric">Der Kennwert mit dem größten normierten Fehler.</param>
    public sealed record Su2ConvergenceVerdict(
        bool Assessed,
        bool Converged,
        IReadOnlyDictionary<string, float> Normalized,
        string? WorstMetric,
        float WorstNormalized);

    /// <summary>
    /// Beurteilt anhand des Endfensters der <c>history.csv</c>, ob ein Ergebnis auskonvergiert ist (TODO-W3).
    ///
    /// <para>
    /// Maß ist die <b>Drift</b> des Kennwerts im Endfenster (Unterschied der Mittelwerte von erster und
    /// zweiter Fensterhälfte), normiert mit der kombinierten Toleranz aus
    /// <see cref="MetricTolerance"/>: <c>drift / (rtol · |Mittelwert| + atol)</c>, ≤ 1 = in Ordnung.
    /// <c>rtol</c> und <c>atol</c> sind hier ein <i>Bruchteil</i> der Netz-Toleranz (Vorgabe
    /// ein Zehntel für Kontrollrechnungen): der iterative Fehler soll deutlich kleiner sein als der
    /// Diskretisierungsfehler, den die Kalibrierung misst (Celik et al. 2008: höchstens 1/10).
    /// </para>
    ///
    /// <para>
    /// Warum nicht das Residuum? SU2 (INC, Limiter) lässt die Residuen bei ~1e-7 stagnieren, obwohl
    /// die Beiwerte längst stehen; und das eingebaute Cauchy-Kriterium auf Iterationsdifferenzen
    /// übersieht ein langsames Weiterwandern (gemessen: Abbruch bei Iteration ~250 mit 3 % Drift).
    /// </para>
    /// </summary>
    public static class Su2ConvergenceJudge
    {
        /// <summary>Kürzestes Endfenster, das noch beurteilt wird (Zeilen).</summary>
        public const int MinTailRows = 20;

        /// <param name="history">Ausgewertete history.csv.</param>
        /// <param name="metricColumns">Kennwertname (z. B. "Drag") → history-Spalte (z. B. "CD").</param>
        /// <param name="relativeTolerance">Erlaubte relative Drift (Bruchteil der Netz-Toleranz).</param>
        /// <param name="absoluteScale">Bruchteil, mit dem die <c>atol</c>-Vorgaben des Kennwerts skaliert werden.</param>
        /// <param name="absoluteOverrides"><c>MeshCalibration.AbsoluteTolerances</c> (kann leer sein).</param>
        public static Su2ConvergenceVerdict Judge(
            Su2HistoryResult history,
            IReadOnlyDictionary<string, string> metricColumns,
            float relativeTolerance,
            float absoluteScale,
            IReadOnlyDictionary<string, float>? absoluteOverrides)
        {
            var normalized = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

            if (history.TailRows < MinTailRows)
                return new Su2ConvergenceVerdict(false, false, normalized, null, 0f);

            string? worst = null;
            float worstValue = 0f;

            foreach (KeyValuePair<string, string> entry in metricColumns)
            {
                string metric = (entry.Key ?? string.Empty).Trim();
                string column = (entry.Value ?? string.Empty).Trim();
                if (metric.Length == 0 || column.Length == 0) continue;

                if (!history.WindowDrift.TryGetValue(column, out float drift)
                    || !history.TailMeans.TryGetValue(column, out float mean))
                    continue;

                float rtol = Math.Max(relativeTolerance, 0f);
                float atol = MetricTolerance.AbsoluteFor(metric, absoluteOverrides) * Math.Max(absoluteScale, 0f);

                // Mittelwert als Referenz: die Drift wird an der Größe des Kennwerts gemessen.
                float value = MetricTolerance.Normalized(mean + drift, mean, rtol, atol);
                normalized[metric] = value;

                if (worst == null || value > worstValue)
                {
                    worst = metric;
                    worstValue = value;
                }
            }

            if (normalized.Count == 0)
                return new Su2ConvergenceVerdict(false, false, normalized, null, 0f);

            return new Su2ConvergenceVerdict(true, worstValue <= 1.0f, normalized, worst, worstValue);
        }
    }
}
