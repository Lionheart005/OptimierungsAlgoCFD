using System;

namespace MyPicoGkProject
{
    /// <summary>
    /// Kernel ohne eigene Laufzeitumgebung: führt den Programmablauf direkt aus.
    ///
    /// Für Projekte, deren Geometrie-Erzeugung keinen Start-Mechanismus braucht
    /// (reine Mesh-Bibliotheken, CAD-Exporte, vorgefertigte STL-Dateien) — und für
    /// Tests, die den <see cref="WorkflowController"/> ohne PicoGK durchlaufen lassen.
    /// </summary>
    public class DirectGeometryKernel : IGeometryKernel
    {
        public string Name => "Direkt (ohne eigene Laufzeitumgebung)";

        public void RunHosted(float voxelResolution, Action body) => body();
    }
}
