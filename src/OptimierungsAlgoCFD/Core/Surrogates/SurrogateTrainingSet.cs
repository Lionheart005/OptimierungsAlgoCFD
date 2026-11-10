using System;
using System.Collections.Generic;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Die zentrale Regel, welche Rechnungen ein Ersatzmodell lernen darf (TODO-53, F13).
    /// Eine Stelle statt verstreuter Filter.
    ///
    /// <para>
    /// Nutzbar ist ein Datensatz, wenn die Rechnung nicht fehlgeschlagen ist, der Türsteher sie nicht
    /// verworfen hat, die Fitness endlich ist, es keine Kontrollrechnung ist (Variante 99) und sie auf
    /// derselben Netzstufe wie der Lauf entstand (<c>MeshRefinementFactor</c>, Toleranz 1e-6).
    /// Datensätze ohne diesen Schlüssel werden angenommen (ältere Daten und Tests).
    /// Fehlgeschlagene und disqualifizierte Punkte werden getrennt geliefert: der Optimierer meidet
    /// ihre Umgebung (Abstoßung).
    /// </para>
    /// </summary>
    public sealed class SurrogateTrainingSet
    {
        /// <summary>Variantennummer der Kontrollrechnungen (Türsteher, Feinprüfung).</summary>
        public const int ControlRunVariant = 99;

        /// <summary>Nutzbare Datensätze in History-Reihenfolge.</summary>
        public IReadOnlyList<ModelRecord> Records { get; }

        /// <summary>Normierte Eingaben, parallel zu <see cref="Records"/>.</summary>
        public IReadOnlyList<double[]> Inputs { get; }

        /// <summary>Normierte Orte fehlgeschlagener oder disqualifizierter Rechnungen.</summary>
        public IReadOnlyList<double[]> ExcludedPoints { get; }

        /// <summary>
        /// Nur die echt fehlgeschlagenen Rechnungen (Vernetzung oder Solver abgebrochen), ohne die vom
        /// Türsteher verworfenen. Ein verworfener Gewinner liegt oft gerade am steilen Optimum; als
        /// „Absturz" gelernt, würde das Erfolgsmodell genau dort wegsteuern.
        /// </summary>
        public IReadOnlyList<double[]> FailedPoints { get; }

        private SurrogateTrainingSet(List<ModelRecord> records, List<double[]> inputs, List<double[]> excluded, List<double[]> failed)
        {
            Records = records;
            Inputs = inputs;
            ExcludedPoints = excluded;
            FailedPoints = failed;
        }

        public static SurrogateTrainingSet Build(SimulationContext context, ParameterSpace space)
        {
            var records = new List<ModelRecord>();
            var inputs = new List<double[]>();
            var excluded = new List<double[]>();
            var failed = new List<double[]>();

            foreach (ModelRecord record in context.History)
            {
                if (record.Variant == ControlRunVariant) continue;
                if (!SameMeshLevel(record, context.EffectiveMeshRefinementFactor)) continue;

                // Ersatznetz (Wandschicht überall verdünnt) rechnet mit einem anderen Wandnetz als die übrigen Varianten:
                // nicht als Lerndaten verwenden, aber auch nicht als Absturz werten.
                bool meshFallback = record.PassiveParameters.TryGetValue(ModelRecord.MeshFallbackKey, out float fb) && fb > 0.5f;

                if (record.SimulationFailed || record.Disqualified || meshFallback)
                {
                    double[] point = space.ToUnit(record.ActiveParameters);
                    excluded.Add(point);

                    // Ein Vernetzungsfehler ist kein Strömungsabsturz (Plan wandschicht_zellzahl.md, FZ7): er stößt die Suche
                    // nur in der engen Umgebung ab, schreckt aber nicht die ganze Formzone ab (Erfolgsmodell).
                    bool meshFailed = record.PassiveParameters.TryGetValue(ModelRecord.MeshFailedKey, out float mf) && mf > 0.5f;
                    if (record.SimulationFailed && !meshFailed) failed.Add(point);
                    continue;
                }

                if (!float.IsFinite(record.Fitness)) continue;

                records.Add(record);
                inputs.Add(space.ToUnit(record.ActiveParameters));
            }

            return new SurrogateTrainingSet(records, inputs, excluded, failed);
        }

        private static bool SameMeshLevel(ModelRecord record, float effectiveFactor)
        {
            if (!record.PassiveParameters.TryGetValue("MeshRefinementFactor", out float factor)) return true;
            return Math.Abs(factor - effectiveFactor) <= 1e-6f;
        }
    }
}
