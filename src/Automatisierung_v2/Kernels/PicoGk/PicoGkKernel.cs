using System;
using System.Threading;
using PicoGK;

namespace MyPicoGkProject
{
    /// <summary>
    /// PicoGK als Geometrie-Kernel.
    ///
    /// <c>Library.Go(voxelSize, body)</c> fährt die PicoGK-Laufzeitumgebung hoch
    /// (Voxelfeld-Backend, Log-Datei) und ruft danach <c>body</c> auf. Ohne diesen
    /// Aufruf darf keine <c>Lattice</c>/<c>Voxels</c>-Instanz erzeugt werden.
    ///
    /// Diese Datei ist — neben <c>Projects/MantaAuv/</c> — die einzige Stelle
    /// außerhalb des Projektcodes, die <c>using PicoGK</c> haben darf.
    /// </summary>
    public class PicoGkKernel : IGeometryKernel
    {
        public string Name => "PicoGK";

        // Library.Go erwartet einen ThreadStart. Der ist signaturgleich zu Action;
        // die Umwandlung bleibt hier, damit der Framework-Kern mit dem neutralen
        // Action-Delegaten auskommt.
        public void RunHosted(float voxelResolution, Action body)
            => Library.Go(voxelResolution, new ThreadStart(body.Invoke));
    }
}
