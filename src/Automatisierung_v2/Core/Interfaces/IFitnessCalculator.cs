using System;
using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Berechnet die Fitness eines Modells basierend auf seinen Metriken.
    /// Jedes Projekt definiert seine eigene Fitness-Formel.
    /// </summary>
    public interface IFitnessCalculator
    {
        void CalculateFitness(ModelRecord record, SimulationConfig config);

        /// <summary>
        /// Metriken in <see cref="ModelRecord.PassiveParameters"/>, ohne die sich die
        /// Fitness-Formel nicht sinnvoll rechnen lässt.
        ///
        /// <para>
        /// Pflicht-<b>Ziele</b> prüft ein Rechner schon im Konstruktor (TODO-16). Für
        /// <b>Metriken</b> geht das nicht: die entstehen erst mit der ersten Geometrie und
        /// dem ersten Solver-Lauf. Der <see cref="WorkflowController"/> prüft diese Liste
        /// deshalb nach der ersten erfolgreich gerechneten Variante und bricht dort ab,
        /// statt stundenlang mit einer Ersatz-Fitness weiterzurechnen (TODO-26).
        /// </para>
        ///
        /// <para>
        /// Standard ist leer — ein Rechner, der sich nicht festlegen will, muss nichts tun,
        /// und bestehende Implementierungen bleiben unverändert.
        /// </para>
        /// </summary>
        IReadOnlyCollection<string> RequiredMetrics => Array.Empty<string>();
    }
}
