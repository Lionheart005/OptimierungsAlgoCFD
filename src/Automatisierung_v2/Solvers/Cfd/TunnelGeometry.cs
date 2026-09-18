using System;
using System.Collections.Generic;
using System.Numerics;

using MyPicoGkProject.Core;

namespace MyPicoGkProject.Solvers.Cfd
{
    /// <summary>
    /// Erzeugt die Hülle des Windkanals (Simulationsdomäne) als Dreiecksliste.
    ///
    /// Vorher kam der Quader aus <c>PicoGK.Utils.mshCreateCube</c>; seit TODO-11 rechnet
    /// diese Klasse ihn selbst, damit die CFD-Schicht nicht am Geometrie-Kernel hängt.
    /// Der Quader ist dabei <b>dreieckstreu</b> nachgebaut — gleiche 12 Dreiecke in gleicher
    /// Reihenfolge und Orientierung, damit Gmsh dieselbe Farfield-Topologie sieht wie bisher.
    /// </summary>
    public static class TunnelGeometry
    {
        /// <summary>
        /// Achsparalleler Quader mit Kantenlängen <paramref name="size"/> um
        /// <paramref name="center"/>. 12 Dreiecke, Normalen nach außen.
        /// </summary>
        public static IReadOnlyList<StlTriangle> CreateBox(Vector3 size, Vector3 center)
        {
            Vector3 min = center - size / 2.0f;
            Vector3 max = center + size / 2.0f;

            // Ecken: Bit 0 = X, Bit 1 = Y, Bit 2 = Z (0 = min, 1 = max)
            Vector3 v000 = new Vector3(min.X, min.Y, min.Z);
            Vector3 v001 = new Vector3(min.X, min.Y, max.Z);
            Vector3 v010 = new Vector3(min.X, max.Y, min.Z);
            Vector3 v011 = new Vector3(min.X, max.Y, max.Z);
            Vector3 v100 = new Vector3(max.X, min.Y, min.Z);
            Vector3 v101 = new Vector3(max.X, min.Y, max.Z);
            Vector3 v110 = new Vector3(max.X, max.Y, min.Z);
            Vector3 v111 = new Vector3(max.X, max.Y, max.Z);

            return new[]
            {
                new StlTriangle(v000, v001, v011),   // -X
                new StlTriangle(v000, v011, v010),
                new StlTriangle(v100, v110, v111),   // +X
                new StlTriangle(v100, v111, v101),
                new StlTriangle(v000, v010, v110),   // -Z
                new StlTriangle(v000, v110, v100),
                new StlTriangle(v001, v101, v111),   // +Z
                new StlTriangle(v001, v111, v011),
                new StlTriangle(v010, v011, v111),   // +Y
                new StlTriangle(v010, v111, v110),
                new StlTriangle(v000, v100, v101),   // -Y
                new StlTriangle(v000, v101, v001)
            };
        }

        /// <summary>
        /// Zylinder mit Achse entlang X (= Strömungsrichtung), zentriert auf
        /// <paramref name="center"/>. Mantel plus zwei Deckel, Normalen nach außen.
        /// Ergibt <c>4 × <paramref name="segments"/></c> Dreiecke.
        /// </summary>
        /// <param name="segments">Anzahl der Mantelsegmente über den Umfang, mindestens 3.</param>
        public static IReadOnlyList<StlTriangle> CreateCylinder(float diameter, float length, Vector3 center, int segments)
        {
            if (segments < 3)
                throw new ArgumentOutOfRangeException(nameof(segments), segments,
                    "Ein Zylinder braucht mindestens 3 Mantelsegmente.");
            if (diameter <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(diameter), diameter, "Der Durchmesser muss positiv sein.");
            if (length <= 0.0f)
                throw new ArgumentOutOfRangeException(nameof(length), length, "Die Länge muss positiv sein.");

            float radius = diameter / 2.0f;
            float xMin = center.X - length / 2.0f;
            float xMax = center.X + length / 2.0f;

            Vector3[] ringMin = new Vector3[segments];
            Vector3[] ringMax = new Vector3[segments];

            for (int i = 0; i < segments; i++)
            {
                double angle = 2.0 * Math.PI * i / segments;
                float y = center.Y + radius * (float)Math.Cos(angle);
                float z = center.Z + radius * (float)Math.Sin(angle);

                ringMin[i] = new Vector3(xMin, y, z);
                ringMax[i] = new Vector3(xMax, y, z);
            }

            Vector3 capCenterMin = new Vector3(xMin, center.Y, center.Z);
            Vector3 capCenterMax = new Vector3(xMax, center.Y, center.Z);

            List<StlTriangle> triangles = new List<StlTriangle>(4 * segments);

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;

                // Mantel: das Viereck zwischen beiden Ringen, in zwei Dreiecke geteilt.
                triangles.Add(new StlTriangle(ringMin[i], ringMin[next], ringMax[next]));
                triangles.Add(new StlTriangle(ringMin[i], ringMax[next], ringMax[i]));

                // Deckel als Dreiecksfächer um die Achsenmitte.
                triangles.Add(new StlTriangle(capCenterMin, ringMin[next], ringMin[i]));   // -X
                triangles.Add(new StlTriangle(capCenterMax, ringMax[i], ringMax[next]));   // +X
            }

            return triangles;
        }
    }
}
