using System;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Erzeugt den Optimierungsalgorithmus anhand des Namens aus der Framework-Konfiguration
    /// (<see cref="SimulationConfig.OptimizationAlgorithm"/>, gesetzt in
    /// <c>src/projects/&lt;Name&gt;/simulation.json</c>). Vorher stand die Wahl auskommentiert
    /// in <c>Program.cs</c>.
    /// </summary>
    public static class OptimizationAlgorithmFactory
    {
        /// <summary>
        /// Verfahren, das bei fehlendem oder unbekanntem Namen benutzt wird. Seit 08.11.2026 <c>"Bayes"</c>: im
        /// 10 × 10-Vergleich (Naca2D, 3 Seeds) war es bei 100 Rechnungen klar am besten (<c>.plans/vergleich_optimierer.md</c>);
        /// bei sehr kleinem Budget (bis etwa 25 Rechnungen) ist <c>"Rsm"</c> gleichwertig.
        /// </summary>
        public const string DefaultAlgorithm = "Bayes";

        /// <summary>Gültige Werte, so wie sie in der Konfiguration stehen dürfen.</summary>
        public const string SupportedAlgorithms = "Rsm, Evolution, Bayes";

        /// <summary>
        /// Baut den Algorithmus zum angegebenen Namen. Groß-/Kleinschreibung und
        /// umgebende Leerzeichen sind egal. Ein leerer Name bedeutet "nicht konfiguriert"
        /// und liefert stillschweigend <see cref="DefaultAlgorithm"/>; ein unbekannter
        /// Name (Tippfehler) liefert ihn ebenfalls, aber mit Warnung auf der Konsole.
        /// </summary>
        /// <param name="random">
        /// Zufallsquelle des Verfahrens. Im Lauf <c>new Random(seed)</c> aus
        /// <see cref="SimulationConfig.RandomSeed"/> (TODO-50), damit Läufe wiederholbar sind
        /// und die Wiederaufnahme per Wiedergabe greift. <c>null</c> = echter Zufall.
        /// </param>
        public static IOptimizationAlgorithm Create(string? name, IFitnessCalculator fitnessCalculator, Random? random = null)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "rsm":
                    return new RsmOptimizationAlgorithm(fitnessCalculator, random);

                case "evolution":
                    return new EvolutionaryAlgorithm(fitnessCalculator, random);

                case "bayes":
                    return new BayesOptimizationAlgorithm(fitnessCalculator, random: random);

                case "":
                    return new BayesOptimizationAlgorithm(fitnessCalculator, random: random);

                default:
                    Console.WriteLine(
                        $"[WARNUNG] Unbekannter Optimierungsalgorithmus '{name}'. " +
                        $"Gueltig sind: {SupportedAlgorithms}. Nutze '{DefaultAlgorithm}'.");
                    return new BayesOptimizationAlgorithm(fitnessCalculator, random: random);
            }
        }
    }
}
