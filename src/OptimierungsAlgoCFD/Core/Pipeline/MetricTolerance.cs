using System;
using System.Collections.Generic;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Kombinierte Toleranz für Kennwertvergleiche (TODO-W1): relativer Anteil <b>plus</b> fester
    /// Mindestbetrag, wie <c>numpy.isclose</c>.
    ///
    /// <para>
    /// Ein Wert <c>q</c> gilt gegenüber der Referenz <c>q_ref</c> als „in Ordnung“, wenn
    /// <c>|q − q_ref| ≤ rtol · |q_ref| + atol</c>. Bei großen Werten entscheidet die relative
    /// Abweichung, bei Werten nahe 0 (Auftrieb eines symmetrischen Profils bei 0°) der feste
    /// Mindestbetrag. Vorher meldete die Kalibrierung für CL = 0,0001 gegen −0,0005 „1 727 %“
    /// Abweichung und scheiterte, obwohl beide Werte praktisch 0 sind.
    /// </para>
    ///
    /// <para>
    /// Angezeigt wird die <b>normierte Abweichung</b> <c>|Δ| / (rtol · |q_ref| + atol)</c>:
    /// höchstens 1 heißt in Ordnung, 2 heißt doppelt so weit daneben wie erlaubt.
    /// </para>
    /// </summary>
    public static class MetricTolerance
    {
        /// <summary>
        /// Vorgaben für den festen Mindestbetrag je Kennwert (dimensionslose Beiwerte):
        /// Auftrieb und Seitenkraft 0,005 (so auch das NASA-TMR-Kriterium bei α = 0°),
        /// Widerstand 0,0001 (ein „drag count“, die übliche Auflösungsgrenze), Momente 0,001.
        /// Unbekannte Kennwerte: 0 (rein relativ, wie bisher).
        /// </summary>
        public static readonly IReadOnlyDictionary<string, float> DefaultAbsolute =
            new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase)
            {
                ["Drag"] = 1e-4f,
                ["Lift"] = 5e-3f,
                ["CSF"] = 5e-3f,
                ["CMx"] = 1e-3f,
                ["CMy"] = 1e-3f,
                ["CMz"] = 1e-3f,
            };

        /// <summary>
        /// Mindestbetrag für <paramref name="metric"/>: Eintrag in <paramref name="overrides"/>
        /// (<c>MeshCalibration.AbsoluteTolerances</c>), sonst <see cref="DefaultAbsolute"/>, sonst 0.
        /// </summary>
        public static float AbsoluteFor(string metric, IReadOnlyDictionary<string, float>? overrides = null)
        {
            if (overrides != null)
            {
                foreach (var entry in overrides)
                {
                    if (string.Equals(entry.Key, metric, StringComparison.OrdinalIgnoreCase))
                        return Math.Max(0f, entry.Value);
                }
            }

            return DefaultAbsolute.TryGetValue(metric, out float value) ? value : 0f;
        }

        /// <summary>Klassische relative Abweichung <c>|q − q_ref| / |q_ref|</c> (für Berichte).</summary>
        public static float Relative(float value, float reference)
            => Math.Abs(value - reference) / Math.Max(Math.Abs(reference), 1e-7f);

        /// <summary>
        /// Normierte Abweichung <c>|q − q_ref| / (rtol · |q_ref| + atol)</c>; höchstens 1 = innerhalb der Toleranz.
        /// Sind rtol und atol 0, gilt jede Abweichung außer exakter Gleichheit als unendlich groß.
        /// </summary>
        public static float Normalized(float value, float reference, float relativeTolerance, float absoluteTolerance)
        {
            float allowed = Math.Max(relativeTolerance, 0f) * Math.Abs(reference) + Math.Max(absoluteTolerance, 0f);
            float delta = Math.Abs(value - reference);
            if (allowed <= 0f) return delta <= 0f ? 0f : float.PositiveInfinity;
            return delta / allowed;
        }

        /// <summary>
        /// Relative Toleranz unter Berücksichtigung der Schwankung beider verglichenen Rechnungen
        /// (<see cref="MetricUncertainty"/>): mindestens die doppelte kombinierte Standardunsicherheit. Bei ruhig
        /// auskonvergierten Rechnungen (Unsicherheit nahe 0) bleibt es bei der eingestellten Toleranz; bei
        /// Wirbelablösung verhindert es, dass reines Rauschen als Netzabhängigkeit oder Instabilität zählt.
        /// </summary>
        public static float WithUncertainty(float tolerance, double uncertaintyA, double uncertaintyB)
            => Math.Max(tolerance, (float)(2.0 * Math.Sqrt(uncertaintyA * uncertaintyA + uncertaintyB * uncertaintyB)));

        /// <summary>Wahr, wenn <paramref name="value"/> innerhalb der kombinierten Toleranz um <paramref name="reference"/> liegt.</summary>
        public static bool Within(float value, float reference, float relativeTolerance, float absoluteTolerance)
            => Normalized(value, reference, relativeTolerance, absoluteTolerance) <= 1.0f;
    }
}
