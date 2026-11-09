using System;
using System.Collections.Generic;
using System.Globalization;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>Ergebnis einer Prüfung des automatischen Endes.</summary>
    /// <param name="Stop">Der Lauf soll nach dieser Iteration enden.</param>
    /// <param name="Improvement">Gemessene relative Verbesserung im Fenster; <c>null</c>, wenn (noch) nicht bestimmbar.</param>
    /// <param name="Message">Klartext für das Log.</param>
    public sealed record AutoStopDecision(bool Stop, double? Improvement, string Message);

    /// <summary>
    /// Entscheidet über das automatische Ende der Optimierung (<see cref="AutoStopConfig"/>, 09.11.2026).
    /// Reine Funktion über die History, damit sie ohne Pipeline prüfbar ist und bei einer Wiederaufnahme
    /// (gleiche History aus dem Journal) an derselben Stelle endet.
    ///
    /// <para>
    /// Regel: <c>vorher</c> = Bestwert aller Varianten vor den letzten <c>WindowVariants</c> Varianten,
    /// <c>jetzt</c> = Bestwert aller Varianten. Ist <c>(jetzt − vorher) / |vorher|</c> kleiner als
    /// <c>MinImprovement</c>, endet der Lauf. Fehlgeschlagene Varianten zählen im Fenster mit,
    /// liefern aber keinen Bestwert.
    /// </para>
    /// </summary>
    public static class AutoStop
    {
        /// <summary>Unterhalb dieses Betrags gilt der Bestwert als 0; eine relative Verbesserung ist dann nicht bestimmbar.</summary>
        private const double ZeroThreshold = 1e-9;

        /// <summary>
        /// Ist das automatische Ende eingeschaltet? Prüft dabei alle Werte und bricht bei Unsinn
        /// mit einer Konfigurationsmeldung ab — vor der ersten Rechnung, nicht nach Stunden.
        /// </summary>
        public static bool IsEnabled(AutoStopConfig? config)
        {
            if (config == null) return false;

            bool enabled;
            switch ((config.Mode ?? "Off").Trim().ToLowerInvariant())
            {
                case "on": enabled = true; break;
                case "off": enabled = false; break;
                default:
                    throw new InvalidOperationException(
                        $"[KONFIGURATION] AutoStop.Mode = \"{config.Mode}\" ist unbekannt. Erlaubt: \"On\" oder \"Off\".");
            }

            if (!enabled) return false;

            if (float.IsNaN(config.MinImprovement) || config.MinImprovement < 0f)
                throw new InvalidOperationException(
                    "[KONFIGURATION] AutoStop.MinImprovement darf nicht negativ sein (0.01 = 1 %).");
            if (config.WindowVariants < 1)
                throw new InvalidOperationException(
                    "[KONFIGURATION] AutoStop.WindowVariants muss mindestens 1 sein.");

            return true;
        }

        /// <summary>Prüft, ob der Lauf nach dem aktuellen Stand der History enden soll.</summary>
        public static AutoStopDecision Evaluate(IReadOnlyList<ModelRecord> history, AutoStopConfig config)
        {
            int window = Math.Max(1, config.WindowVariants);
            int splitIndex = history.Count - window;
            string threshold = Percent(config.MinImprovement);

            if (splitIndex < 1)
                return new AutoStopDecision(false, null,
                    $"noch zu wenige Varianten ({history.Count}, gemessen wird ab {window + 1}).");

            double? before = BestFitness(history, 0, splitIndex);
            double? now = BestFitness(history, 0, history.Count);

            if (before == null || now == null)
                return new AutoStopDecision(false, null,
                    "vor dem Messfenster gibt es noch keine gültige Variante.");

            if (Math.Abs(before.Value) < ZeroThreshold)
                return new AutoStopDecision(false, null,
                    "der Bestwert vor dem Messfenster ist 0, eine Verbesserung in Prozent ist nicht bestimmbar.");

            double improvement = (now.Value - before.Value) / Math.Abs(before.Value);
            string measured = $"Verbesserung des Bestwerts in den letzten {window} Varianten: {Percent(improvement)} (Schwelle {threshold})";

            return improvement < config.MinImprovement
                ? new AutoStopDecision(true, improvement, measured + " – Optimierung wird beendet.")
                : new AutoStopDecision(false, improvement, measured + " – weiter.");
        }

        private static double? BestFitness(IReadOnlyList<ModelRecord> history, int from, int to)
        {
            double? best = null;
            for (int i = from; i < to; i++)
            {
                ModelRecord record = history[i];
                if (record.SimulationFailed) continue;
                if (best == null || record.Fitness > best.Value) best = record.Fitness;
            }
            return best;
        }

        private static string Percent(double fraction)
            => (fraction * 100.0).ToString("0.##", CultureInfo.GetCultureInfo("de-DE")) + " %";
    }
}
