using System;
using System.Collections.Generic;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Konvention für die Unsicherheit eines Kennwerts (Paket B, 08.11.2026). Ein Solver darf neben
    /// <c>Drag</c> auch <c>Uncertainty_Drag</c> in <see cref="ModelRecord.PassiveParameters"/> schreiben:
    /// die <b>relative</b> Standardunsicherheit (1σ) des Werts, z. B. 0,02 für ±2 %. Bei SU2 kommt sie
    /// aus den Blockmitteln des Endfensters; eine ruhig auskonvergierte Rechnung hat fast 0, eine mit
    /// Wirbelablösung deutlich mehr.
    ///
    /// <para>
    /// Der Kern kennt keinen Solver: Er liest nur diese Schlüssel. Der Bayes-Optimierer nutzt sie als
    /// Rauschen je Rechnung, so dass schwankende Ergebnisse das Ersatzmodell weniger stark ziehen.
    /// Fehlt der Schlüssel, gilt die Unsicherheit als unbekannt (das Modell schätzt dann ein gemeinsames
    /// Rauschen wie bisher).
    /// </para>
    /// </summary>
    public static class MetricUncertainty
    {
        public const string Prefix = "Uncertainty_";

        /// <summary>Obergrenze, damit ein einzelner Ausreißer das Modell nicht aushebelt (±100 %).</summary>
        public const double MaximumRelative = 1.0;

        public static string KeyFor(string metric) => Prefix + metric;

        /// <summary>Relative Unsicherheit eines Kennwerts, falls vorhanden und endlich.</summary>
        public static bool TryGet(ModelRecord record, string metric, out double relative)
        {
            relative = 0;
            if (!record.PassiveParameters.TryGetValue(KeyFor(metric), out float value) || !float.IsFinite(value) || value < 0)
                return false;
            relative = Math.Min(value, MaximumRelative);
            return true;
        }

        /// <summary>
        /// Grobe relative Unsicherheit der Fitness: Wurzel aus der Summe der Quadrate der relativen
        /// Unsicherheiten der genannten Kennwerte (für Produkte und Quotienten wie CL/CD oder
        /// Volumen·Abstand/Drag³ in erster Näherung richtig, Exponenten bleiben unberücksichtigt).
        /// Ohne Namen werden alle vorhandenen Unsicherheiten des Datensatzes genommen.
        /// </summary>
        public static bool TryGetFitness(ModelRecord record, IReadOnlyCollection<string> metrics, out double relative)
        {
            double sum = 0;
            bool any = false;

            if (metrics.Count > 0)
            {
                foreach (string metric in metrics)
                {
                    if (TryGet(record, metric, out double u)) { sum += u * u; any = true; }
                }
            }
            else
            {
                foreach (var (key, value) in record.PassiveParameters)
                {
                    if (!key.StartsWith(Prefix, StringComparison.Ordinal) || !float.IsFinite(value) || value < 0) continue;
                    double u = Math.Min(value, MaximumRelative);
                    sum += u * u;
                    any = true;
                }
            }

            relative = Math.Min(Math.Sqrt(sum), MaximumRelative);
            return any;
        }
    }
}
