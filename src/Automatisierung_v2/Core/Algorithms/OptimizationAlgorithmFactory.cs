using System;

namespace MyPicoGkProject
{
    /// <summary>
    /// Erzeugt den Optimierungsalgorithmus anhand des Namens aus der Framework-Konfiguration
    /// (<see cref="SimulationConfig.OptimizationAlgorithm"/>, gesetzt in
    /// <c>config/simulation.json</c>). Vorher stand die Wahl auskommentiert
    /// in <c>Program.cs</c>.
    /// </summary>
    public static class OptimizationAlgorithmFactory
    {
        /// <summary>Verfahren, das bei fehlendem oder unbekanntem Namen benutzt wird.</summary>
        public const string DefaultAlgorithm = "Rsm";

        /// <summary>Gültige Werte, so wie sie in der Konfiguration stehen dürfen.</summary>
        public const string SupportedAlgorithms = "Rsm, Evolution";

        /// <summary>
        /// Baut den Algorithmus zum angegebenen Namen. Groß-/Kleinschreibung und
        /// umgebende Leerzeichen sind egal. Ein leerer Name bedeutet "nicht konfiguriert"
        /// und liefert stillschweigend <see cref="DefaultAlgorithm"/>; ein unbekannter
        /// Name (Tippfehler) liefert ihn ebenfalls, aber mit Warnung auf der Konsole.
        /// </summary>
        public static IOptimizationAlgorithm Create(string? name, IFitnessCalculator fitnessCalculator)
        {
            switch ((name ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "rsm":
                    return new RsmOptimizationAlgorithm(fitnessCalculator);

                case "evolution":
                    return new EvolutionaryAlgorithm(fitnessCalculator);

                case "":
                    return new RsmOptimizationAlgorithm(fitnessCalculator);

                default:
                    Console.WriteLine(
                        $"[WARNUNG] Unbekannter Optimierungsalgorithmus '{name}'. " +
                        $"Gueltig sind: {SupportedAlgorithms}. Nutze '{DefaultAlgorithm}'.");
                    return new RsmOptimizationAlgorithm(fitnessCalculator);
            }
        }
    }
}
