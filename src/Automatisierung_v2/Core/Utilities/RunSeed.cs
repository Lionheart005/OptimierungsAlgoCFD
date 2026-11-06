using System;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Der Zufalls-Seed eines Laufs (TODO-50, F23). Alle Zufallsquellen leiten sich davon ab,
    /// damit ein Lauf exakt wiederholbar ist und die Wiederaufnahme per Wiedergabe (TODO-51)
    /// dieselben Parameter erneut erzeugt.
    /// </summary>
    public static class RunSeed
    {
        /// <summary>
        /// Der konfigurierte Wert, oder bei <c>0</c> ein frisch gezogener Wert ≠ 0.
        /// </summary>
        public static int Resolve(int configured)
            => configured != 0 ? configured : Random.Shared.Next(1, int.MaxValue);

        /// <summary>Zufallsquelle des Optimierers.</summary>
        public static Random ForOptimizer(int seed) => new Random(seed);

        /// <summary>
        /// Zufallsquelle des Türstehers. Bewusst ein anderer Strom als der des Optimierers:
        /// sonst hingen der Ruckler und die nächsten Kandidaten voneinander ab.
        /// </summary>
        public static Random ForValidator(int seed) => new Random(unchecked(seed + 1));

        /// <summary>Die Logzeile zum Seed, so dass sie sich direkt übernehmen lässt.</summary>
        public static string Describe(int seed, int configured)
            => configured != 0
                ? $"[SYSTEM] Zufalls-Seed: {seed} (aus simulation.json \"RandomSeed\")"
                : $"[SYSTEM] Zufalls-Seed: {seed} (automatisch gewählt; für eine exakte Wiederholung in simulation.json \"RandomSeed\": {seed} eintragen)";
    }
}
