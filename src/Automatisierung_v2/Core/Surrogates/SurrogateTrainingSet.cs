using System;
using System.Collections.Generic;

namespace MyPicoGkProject.Core
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

                if (record.SimulationFailed || record.Disqualified)
                {
                    double[] point = space.ToUnit(record.ActiveParameters);
                    excluded.Add(point);
                    if (record.SimulationFailed) failed.Add(point);
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
