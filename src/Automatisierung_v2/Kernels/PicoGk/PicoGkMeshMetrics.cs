using System;
using System.Collections.Generic;
using System.Numerics;
using PicoGK;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Kernels.PicoGk
{
    /// <summary>
    /// Die Brücke zwischen einem PicoGK-Netz und <see cref="MeshMetrics"/> (TODO-29).
    ///
    /// <para>
    /// <b>Warum diese Klasse getrennt von <see cref="MeshMetrics"/> liegt:</b> sie braucht
    /// <c>using PicoGK</c>, und <c>Core/</c> hat keines — TODO-20 hat das nachgewiesen und
    /// lässt es vom Compiler erzwingen. Die Rechnung selbst steht deshalb im Kern, die
    /// Umwandlung hier in der Kernel-Schicht. Für ein Projekt ist das eine Zeile:
    /// </para>
    ///
    /// <code>
    /// MeshMeasurement measurement = PicoGkMeshMetrics.Measure(new Mesh(voxels));
    /// </code>
    ///
    /// <para>
    /// <b>Nicht getestet</b> — jeder Aufruf braucht eine laufende PicoGK-Umgebung
    /// (<c>Library.Go</c>) und die native <c>libpicogk</c>. Was sich ohne beides prüfen lässt,
    /// ist die Rechnung, und die steht in <see cref="MeshMetrics"/> und ist dort geprüft.
    /// Hier bleibt nur die Schleife über <c>nTriangleCount</c>/<c>GetTriangle</c>.
    /// </para>
    /// </summary>
    public static class PicoGkMeshMetrics
    {
        /// <summary>Volumen, Oberfläche und Hüllquader eines PicoGK-Netzes.</summary>
        /// <exception cref="ArgumentNullException"><paramref name="mesh"/> ist <c>null</c>.</exception>
        public static MeshMeasurement Measure(Mesh mesh) => MeshMetrics.Measure(ToTriangles(mesh));

        /// <summary>
        /// Ein PicoGK-Netz als framework-eigene Dreiecksliste. Damit lässt sich ein Netz auch
        /// über <see cref="StlWriter"/> schreiben oder in einem Test festhalten, ohne dass der
        /// aufrufende Code PicoGK-Typen weiterreichen muss.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="mesh"/> ist <c>null</c>.</exception>
        public static IReadOnlyList<StlTriangle> ToTriangles(Mesh mesh)
        {
            if (mesh == null) throw new ArgumentNullException(nameof(mesh));

            int count = mesh.nTriangleCount();
            var triangles = new List<StlTriangle>(count);

            for (int triangle = 0; triangle < count; triangle++)
            {
                mesh.GetTriangle(triangle, out Vector3 a, out Vector3 b, out Vector3 c);
                triangles.Add(new StlTriangle(a, b, c));
            }

            return triangles;
        }
    }
}
