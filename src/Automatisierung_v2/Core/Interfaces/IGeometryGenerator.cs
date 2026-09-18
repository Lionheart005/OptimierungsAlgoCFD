using System.Collections.Generic;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Erzeugt die 3D-Geometrie für ein Projekt und gibt eine STL + Metriken zurück.
    /// </summary>
    public interface IGeometryGenerator
    {
        /// <param name="parameters">Die (ggf. skalierten) Geometrie-Parameter.</param>
        /// <param name="outputDirectory">Zielverzeichnis für die STL-Datei.</param>
        /// <param name="config">
        /// Framework-Konfiguration — enthält u.a. die Glättungsparameter und den Schalter
        /// <see cref="SimulationConfig.UseStlSmoothing"/>. Wie in <see cref="IMeshGenerator"/>
        /// und <see cref="ISimulationSolver"/> wird die Konfiguration als Ganzes übergeben,
        /// statt einzelne Werte durchzureichen.
        /// </param>
        GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config);
    }
}
