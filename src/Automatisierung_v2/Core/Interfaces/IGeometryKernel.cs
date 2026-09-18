using System;

namespace MyPicoGkProject
{
    /// <summary>
    /// Laufzeitumgebung des Geometrie-Kernels (Entscheidung 2).
    ///
    /// Manche Kernel — PicoGK zum Beispiel — müssen erst eine eigene Laufzeitumgebung
    /// hochfahren, bevor irgendeine Geometrie erzeugt werden darf; der eigentliche
    /// Programmablauf läuft dann *innerhalb* dieses Aufrufs. Damit
    /// <c>Program.cs</c> nicht <c>Library.Go(...)</c> und damit PicoGK kennen muss,
    /// steht dieser Start-Mechanismus hinter dieser Abstraktion.
    ///
    /// Bewusst getrennt von <see cref="IGeometryGenerator"/>: der Kernel ist
    /// projektunabhängig (mehrere Projekte teilen sich PicoGK), der Generator nicht.
    /// Ein Kernel ohne eigene Laufzeitumgebung nutzt <see cref="DirectGeometryKernel"/>.
    /// </summary>
    public interface IGeometryKernel
    {
        /// <summary>Name des Kernels für die Konsolen-Ausgabe.</summary>
        string Name { get; }

        /// <summary>
        /// Startet den Kernel und führt <paramref name="body"/> in dessen Laufzeitumgebung aus.
        /// Der Aufruf kehrt erst zurück, wenn <paramref name="body"/> fertig ist.
        /// </summary>
        /// <param name="voxelResolution">
        /// Arbeitsauflösung des Kernels (bei PicoGK die Voxelgröße). Kernel ohne
        /// Voxel-Modell dürfen den Wert ignorieren.
        /// </param>
        /// <param name="body">Der Programmablauf, der im Kernel laufen soll.</param>
        void RunHosted(float voxelResolution, Action body);
    }
}
