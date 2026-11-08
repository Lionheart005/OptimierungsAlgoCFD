namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Optionale Ergänzung zu <see cref="IOptimizationAlgorithm"/>: Verfahren, die Voraussetzungen
    /// haben (z. B. vollständige Parametergrenzen), prüfen sie damit <b>vor</b> der ersten teuren
    /// Rechnung, also noch vor der Netzkalibrierung (Fail-fast wie TODO-16 und TODO-26).
    /// </summary>
    public interface IOptimizationPreflight
    {
        /// <summary>
        /// Prüft die Voraussetzungen.
        /// </summary>
        /// <exception cref="System.InvalidOperationException">Voraussetzung nicht erfüllt; die Meldung nennt, was wo einzutragen ist.</exception>
        void Preflight(SimulationContext context);
    }
}
