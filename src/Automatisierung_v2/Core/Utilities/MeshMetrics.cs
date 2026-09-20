using System;
using System.Collections.Generic;
using System.Numerics;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Der Hüllquader eines Dreiecksnetzes (mm). Aus <see cref="Size"/> kommt die
    /// Spannweite: das Framework strömt entlang X an, quer dazu ist also <c>Size.Y</c>.
    /// </summary>
    public readonly struct MeshBounds
    {
        private readonly bool _hasPoints;

        public MeshBounds(Vector3 min, Vector3 max)
        {
            Min = min;
            Max = max;
            _hasPoints = true;
        }

        /// <summary>Kleinste Ecke.</summary>
        public Vector3 Min { get; }

        /// <summary>Größte Ecke.</summary>
        public Vector3 Max { get; }

        /// <summary>
        /// Kein einziger Punkt — der Quader ist nicht bloß klein, sondern <b>nicht
        /// vorhanden</b>. <see cref="Min"/>, <see cref="Max"/> und <see cref="Size"/> sind
        /// dann Null, was sich von einem entarteten Netz nicht unterscheiden ließe; deshalb
        /// dieses Feld.
        /// </summary>
        public bool IsEmpty => !_hasPoints;

        /// <summary>Kantenlängen des Quaders.</summary>
        public Vector3 Size => _hasPoints ? Max - Min : Vector3.Zero;

        /// <summary>Mittelpunkt des Quaders.</summary>
        public Vector3 Center => _hasPoints ? (Min + Max) * 0.5f : Vector3.Zero;
    }

    /// <summary>
    /// Alles, was sich in einem Durchgang über ein Netz sagen lässt.
    /// </summary>
    public readonly struct MeshMeasurement
    {
        public MeshMeasurement(float signedVolume, float surfaceArea, MeshBounds bounds, int triangleCount)
        {
            SignedVolume = signedVolume;
            SurfaceArea = surfaceArea;
            Bounds = bounds;
            TriangleCount = triangleCount;
        }

        /// <summary>
        /// Volumen mit Vorzeichen (mm³). <b>Negativ heißt: die Dreiecke sind nach innen
        /// gedreht.</b> Das ist keine Spielerei — eine umgedrehte Hülle rechnet Gmsh
        /// entweder gar nicht oder falsch, und im Betrag sieht man den Fehler nicht.
        /// </summary>
        public float SignedVolume { get; }

        /// <summary>Volumen als Betrag (mm³) — der Wert für eine Metrik.</summary>
        public float Volume => Math.Abs(SignedVolume);

        /// <summary>Oberfläche (mm²).</summary>
        public float SurfaceArea { get; }

        /// <summary>Hüllquader; daraus die Spannweite.</summary>
        public MeshBounds Bounds { get; }

        /// <summary>Anzahl der Dreiecke.</summary>
        public int TriangleCount { get; }

        /// <summary>
        /// Angeströmte Fläche als Rechteck aus dem Hüllquader (mm²), quer zur X-Achse. Das ist
        /// dieselbe Näherung, mit der MantaAuv seine <c>FrontalArea</c> bildet: nicht die
        /// wahre projizierte Fläche, sondern das umschließende Rechteck. Für <c>REF_AREA</c>
        /// reicht das, solange es <b>durchgängig</b> dieselbe Definition ist — die Beiwerte
        /// aller Varianten sind dann untereinander vergleichbar.
        /// </summary>
        public float FrontalBoxArea => Bounds.Size.Y * Bounds.Size.Z;
    }

    /// <summary>
    /// Maße eines Dreiecksnetzes: Volumen, Oberfläche, Hüllquader (TODO-29).
    ///
    /// <para>
    /// <b>Warum das im Framework steht:</b> PicoGKs <c>CalculateProperties</c> liefert Volumen
    /// und Hüllquader, die <b>Oberfläche nicht</b> — die muss aus dem Dreiecksnetz summiert
    /// werden. Diese Schleife wäre in jedem Projekt dieselbe, und die Fehler darin wären in
    /// jedem Projekt dieselben (Faktor 2 vergessen, Kreuzprodukt falsch herum).
    /// </para>
    ///
    /// <para>
    /// <b>Rein additiv:</b> kein bestehendes Projekt muss das benutzen. MantaAuv rechnet
    /// weiterhin mit <c>CalculateProperties</c> und bekommt damit exakt die Zahlen wie vorher.
    /// Der Unterschied ist echt, wenn auch klein: PicoGK misst das <b>Voxelfeld</b>, diese
    /// Klasse das daraus gewonnene <b>Netz</b>.
    /// </para>
    ///
    /// <para>
    /// Bewusst auf <see cref="StlTriangle"/> und nicht auf einem Kernel-Typ: <c>Core/</c> hat
    /// kein <c>using PicoGK</c> und soll keines bekommen (TODO-20). Wer ein PicoGK-Netz
    /// messen will, nimmt <c>Kernels/PicoGk/PicoGkMeshMetrics</c> — das ist die Schicht, die
    /// PicoGK kennen darf.
    /// </para>
    ///
    /// <para>
    /// Alle Summen laufen in <c>double</c> und werden erst am Ende auf <c>float</c> gebracht.
    /// Ein Netz mit 100.000 Dreiecken und Volumen in der Größenordnung 1e7 mm³ verliert in
    /// <c>float</c> (etwa sieben gültige Stellen) sonst sichtbar Genauigkeit.
    /// </para>
    /// </summary>
    public static class MeshMetrics
    {
        /// <summary>
        /// Alles in einem Durchgang — der übliche Weg, weil ein Projekt meist mehrere
        /// Metriken meldet.
        /// </summary>
        /// <exception cref="ArgumentNullException"><paramref name="triangles"/> ist <c>null</c>.</exception>
        public static MeshMeasurement Measure(IReadOnlyList<StlTriangle> triangles)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));

            if (triangles.Count == 0)
                return new MeshMeasurement(0f, 0f, default, 0);

            double volume = 0.0;
            double area = 0.0;

            Vector3 min = triangles[0].A;
            Vector3 max = triangles[0].A;

            foreach (StlTriangle triangle in triangles)
            {
                volume += TetrahedronVolume(triangle);
                area += TriangleArea(triangle);

                Grow(ref min, ref max, triangle.A);
                Grow(ref min, ref max, triangle.B);
                Grow(ref min, ref max, triangle.C);
            }

            return new MeshMeasurement(
                (float)volume, (float)area, new MeshBounds(min, max), triangles.Count);
        }

        /// <summary>Oberfläche (mm²) als Summe der Dreiecksflächen.</summary>
        public static float SurfaceArea(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).SurfaceArea;

        /// <summary>
        /// Volumen (mm³) als Betrag. Setzt eine <b>geschlossene</b> Hülle voraus: bei einem
        /// Netz mit Löchern ist das Ergebnis eine Zahl, aber keine Aussage.
        /// </summary>
        public static float Volume(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).Volume;

        /// <summary>
        /// Volumen mit Vorzeichen (mm³) — negativ bei nach innen gedrehten Dreiecken.
        /// </summary>
        public static float SignedVolume(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).SignedVolume;

        /// <summary>Hüllquader des Netzes.</summary>
        public static MeshBounds Bounds(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).Bounds;

        /// <summary>
        /// Das mit Vorzeichen versehene Volumen des Tetraeders Ursprung–A–B–C.
        ///
        /// <para>
        /// Über alle Dreiecke einer geschlossenen Hülle summiert ergibt das deren Volumen
        /// (Divergenzsatz) — die Beiträge außerhalb des Körpers heben sich paarweise auf.
        /// Deshalb ist der Bezugspunkt frei wählbar, und der Ursprung ist der bequemste.
        /// </para>
        /// </summary>
        private static double TetrahedronVolume(StlTriangle triangle)
        {
            Vector3 a = triangle.A, b = triangle.B, c = triangle.C;

            double x = (double)b.Y * c.Z - (double)b.Z * c.Y;
            double y = (double)b.Z * c.X - (double)b.X * c.Z;
            double z = (double)b.X * c.Y - (double)b.Y * c.X;

            return (a.X * x + a.Y * y + a.Z * z) / 6.0;
        }

        /// <summary>Fläche eines Dreiecks: halbe Länge des Kreuzprodukts zweier Kantenvektoren.</summary>
        private static double TriangleArea(StlTriangle triangle)
        {
            Vector3 u = triangle.B - triangle.A;
            Vector3 v = triangle.C - triangle.A;

            double x = (double)u.Y * v.Z - (double)u.Z * v.Y;
            double y = (double)u.Z * v.X - (double)u.X * v.Z;
            double z = (double)u.X * v.Y - (double)u.Y * v.X;

            return Math.Sqrt(x * x + y * y + z * z) * 0.5;
        }

        private static void Grow(ref Vector3 min, ref Vector3 max, Vector3 point)
        {
            min = Vector3.Min(min, point);
            max = Vector3.Max(max, point);
        }
    }
}
