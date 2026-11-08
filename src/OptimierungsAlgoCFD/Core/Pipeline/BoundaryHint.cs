using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Erkennt, ob der Gewinner an einer Parametergrenze liegt (<c>ParameterBounds</c> in <c>project.json</c>).
    ///
    /// <para>
    /// Liegt das Optimum am Rand des erlaubten Bereichs, ist das Ergebnis durch die Grenze bestimmt, und ein
    /// besserer Wert liegt möglicherweise dahinter. Das Framework erweitert Grenzen <b>nie</b> von selbst
    /// (Physik, Festigkeit und Baubarkeit kennt nur der Nutzer); es weist am Ende des Laufs sichtbar darauf hin.
    /// Anlass: Im 10 × 10-Vergleich vom 08.11.2026 lagen alle Bayes-Sieger bei der größten erlaubten Wölbung und
    /// der kleinsten erlaubten Dicke.
    /// </para>
    /// </summary>
    public static class BoundaryHint
    {
        /// <summary>Ein Parameter gilt als „an der Grenze“, wenn er höchstens diesen Bruchteil der Spanne davon entfernt ist.</summary>
        public const double RelativeMargin = 0.01;

        /// <summary>Ein Parameter des Gewinners, der an einer Grenze liegt.</summary>
        public sealed record Finding(string Parameter, float Value, float Bound, bool IsUpper)
        {
            public string Describe()
            {
                CultureInfo inv = CultureInfo.InvariantCulture;
                return $"{Parameter} = {Value.ToString("0.##", inv)} ({(IsUpper ? "obere" : "untere")} Grenze {Bound.ToString("0.##", inv)})";
            }
        }

        /// <summary>
        /// Alle aktiven Parameter des Gewinners, die an einer Grenze liegen. Feste Parameter (Min = Max) und
        /// Parameter ohne Grenzen zählen nicht.
        /// </summary>
        public static IReadOnlyList<Finding> Find(
            IReadOnlyDictionary<string, float> parameters,
            IReadOnlyDictionary<string, (float Min, float Max)>? bounds)
        {
            var findings = new List<Finding>();
            if (bounds == null) return findings;

            foreach (var (name, value) in parameters.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                if (!bounds.TryGetValue(name, out (float Min, float Max) limit)) continue;

                double span = (double)limit.Max - limit.Min;
                if (!(span > 0)) continue;

                double margin = RelativeMargin * span;
                if (value - limit.Min <= margin) findings.Add(new Finding(name, value, limit.Min, IsUpper: false));
                else if (limit.Max - value <= margin) findings.Add(new Finding(name, value, limit.Max, IsUpper: true));
            }

            return findings;
        }

        /// <summary>Die Zeilen der Ausgabe am Ende des Laufs; leer, wenn kein Parameter an einer Grenze liegt.</summary>
        public static IReadOnlyList<string> Lines(IReadOnlyList<Finding> findings)
        {
            if (findings.Count == 0) return Array.Empty<string>();

            var lines = new List<string> { "⚠ HINWEIS: Das Optimum liegt an der Grenze des Suchraums" };
            lines.AddRange(findings.Select(f => "   " + f.Describe()));
            lines.Add("   Das Ergebnis ist durch diese Grenze bestimmt, ein besserer Wert liegt möglicherweise dahinter.");
            lines.Add("   Grenzen werden nie automatisch erweitert. Bei Bedarf in project.json unter \"ParameterBounds\" anpassen und neu rechnen.");
            return lines;
        }
    }
}
