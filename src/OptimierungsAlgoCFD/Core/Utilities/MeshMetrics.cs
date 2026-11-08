using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;

namespace OptimierungsAlgoCFD.Core
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
        public MeshMeasurement(
            float signedVolume,
            float surfaceArea,
            MeshBounds bounds,
            int triangleCount,
            float frontalProjectedArea = 0f)
        {
            SignedVolume = signedVolume;
            SurfaceArea = surfaceArea;
            Bounds = bounds;
            TriangleCount = triangleCount;
            FrontalProjectedArea = frontalProjectedArea;
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
        /// die einfache Hüllquader-Näherung: nicht die wahre projizierte Fläche, sondern das
        /// umschließende Rechteck (<c>Size.Y × Size.Z</c>).
        /// </summary>
        public float FrontalBoxArea => Bounds.Size.Y * Bounds.Size.Z;

        /// <summary>
        /// Wahre projizierte Stirnfläche / Silhouette (mm²) in der YZ-Ebene quer zur
        /// Anströmrichtung (X-Achse).
        /// <para>
        /// Anders als <see cref="FrontalBoxArea"/> wird hier der tatsächliche 2D-Schattenwurf
        /// des Netzes bestimmt: schräge dünne Stäbe, Flügel-Rumpf-Kombinationen oder
        /// hintereinanderliegende Bauteile erhalten ihre echte Querschnittsfläche statt des
        /// aufgespannten Hüllrechtecks.
        /// </para>
        /// </summary>
        public float FrontalProjectedArea { get; }

        /// <summary>
        /// Liefert je nach Schalter entweder die echte projizierte Stirnfläche
        /// (<see cref="FrontalProjectedArea"/>, Standard) oder das Hüllquader-Rechteck
        /// (<see cref="FrontalBoxArea"/>).
        /// </summary>
        public float GetFrontalArea(bool useProjected = true)
            => useProjected ? FrontalProjectedArea : FrontalBoxArea;
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

            MeshBounds bounds = new MeshBounds(min, max);
            float frontalProjectedArea = ComputeFrontalProjectedArea(triangles, bounds);

            return new MeshMeasurement(
                (float)volume, (float)area, bounds, triangles.Count, frontalProjectedArea);
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
        /// Wahre projizierte Stirnfläche (mm²) in der YZ-Ebene quer zur X-Achse.
        /// </summary>
        public static float FrontalProjectedArea(IReadOnlyList<StlTriangle> triangles)
            => Measure(triangles).FrontalProjectedArea;

        /// <summary>
        /// Liest die Anzahl der Volumenelemente (Zellen) aus dem Kopf einer SU2-Netzdatei (.su2).
        /// Sucht zeilenweise nach der ersten Zeile mit "NELEM=" und parst die Zahl dahinter.
        /// Gibt 0 zurück, wenn die Datei nicht existiert oder kein NELEM gefunden wurde.
        /// </summary>
        public static int ReadSu2CellCount(string su2MeshPath)
        {
            if (string.IsNullOrWhiteSpace(su2MeshPath) || !File.Exists(su2MeshPath))
                return 0;

            try
            {
                foreach (string line in File.ReadLines(su2MeshPath))
                {
                    string trimmed = line.Trim();
                    if (trimmed.StartsWith("NELEM", StringComparison.OrdinalIgnoreCase))
                    {
                        int eqIdx = trimmed.IndexOf('=');
                        if (eqIdx >= 0 && trimmed.Substring(0, eqIdx).Trim().Equals("NELEM", StringComparison.OrdinalIgnoreCase))
                        {
                            string valuePart = trimmed.Substring(eqIdx + 1).Trim();
                            if (int.TryParse(valuePart, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count))
                            {
                                return count;
                            }
                        }
                    }
                }
            }
            catch
            {
                // Bei Lesefehlern oder gesperrter Datei defensiv 0 zurückgeben
                return 0;
            }

            return 0;
        }

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

        /// <summary>
        /// Berechnet die wahre projizierte Stirnfläche (Silhouette in der YZ-Ebene quer zur X-Achse)
        /// über einen schnellen, lückenfreien 2D-Scanline-Schattenwurf auf einem 512×512-Bitgitter.
        /// <para>
        /// Warum ein 2D-Bitgitter statt reiner Normalensummation (<c>0.5 * Σ |n_x| A_i</c>):
        /// Bei nicht-konvexen Körpern, hintereinanderliegenden Flügeln/Streben oder konkaven Taschen
        /// würde die Normalensumme verdeckte Flächen mehrfach zählen. Das Bitgitter vereinigt
        /// überlappende Projektionen exakt per logischem ODER (<c>|=</c>) und braucht dafür nur 32 KB
        /// Speicher im L1-Cache.
        /// </para>
        /// </summary>
        private static float ComputeFrontalProjectedArea(IReadOnlyList<StlTriangle> triangles, MeshBounds bounds)
        {
            if (triangles.Count == 0 || bounds.IsEmpty)
                return 0f;

            double minY = bounds.Min.Y;
            double minZ = bounds.Min.Z;
            double sizeY = (double)bounds.Max.Y - minY;
            double sizeZ = (double)bounds.Max.Z - minZ;

            if (sizeY <= 1e-9 || sizeZ <= 1e-9)
                return 0f;

            const int GridResolution = 512;
            const int WordsPerRow = GridResolution / 64; // 8 ulongs je Zeile
            const double Eps = 1e-9;

            ulong[] grid = new ulong[GridResolution * WordsPerRow];

            double scaleY = GridResolution / sizeY;
            double scaleZ = GridResolution / sizeZ;

            foreach (StlTriangle tri in triangles)
            {
                double u0 = (tri.A.Y - minY) * scaleY;
                double v0 = (tri.A.Z - minZ) * scaleZ;
                double u1 = (tri.B.Y - minY) * scaleY;
                double v1 = (tri.B.Z - minZ) * scaleZ;
                double u2 = (tri.C.Y - minY) * scaleY;
                double v2 = (tri.C.Z - minZ) * scaleZ;

                // Entartete 2D-Projektionen (z.B. Seitenwände exakt parallel zur X-Achse) überspringen
                double cross2D = (u1 - u0) * (v2 - v0) - (v1 - v0) * (u2 - u0);
                if (Math.Abs(cross2D) <= 1e-12)
                    continue;

                double vMin = Math.Min(v0, Math.Min(v1, v2));
                double vMax = Math.Max(v0, Math.Max(v1, v2));

                int ivStart = Math.Max(0, (int)Math.Ceiling(vMin - 0.5 - Eps));
                int ivEnd = Math.Min(GridResolution - 1, (int)Math.Floor(vMax - 0.5 + Eps));

                if (ivStart > ivEnd)
                    continue;

                for (int iv = ivStart; iv <= ivEnd; iv++)
                {
                    double vc = iv + 0.5;
                    double uLeft = double.PositiveInfinity;
                    double uRight = double.NegativeInfinity;

                    IntersectEdge(u0, v0, u1, v1, vc, Eps, ref uLeft, ref uRight);
                    IntersectEdge(u1, v1, u2, v2, vc, Eps, ref uLeft, ref uRight);
                    IntersectEdge(u2, v2, u0, v0, vc, Eps, ref uLeft, ref uRight);

                    if (uLeft > uRight)
                        continue;

                    int iuStart = Math.Max(0, (int)Math.Ceiling(uLeft - 0.5 - Eps));
                    int iuEnd = Math.Min(GridResolution - 1, (int)Math.Floor(uRight - 0.5 + Eps));

                    if (iuStart > iuEnd)
                        continue;

                    int rowOffset = iv * WordsPerRow;
                    int startWord = iuStart >> 6;
                    int endWord = iuEnd >> 6;
                    ulong startMask = ulong.MaxValue << (iuStart & 63);
                    ulong endMask = ulong.MaxValue >> (63 - (iuEnd & 63));

                    if (startWord == endWord)
                    {
                        grid[rowOffset + startWord] |= (startMask & endMask);
                    }
                    else
                    {
                        grid[rowOffset + startWord] |= startMask;
                        for (int w = startWord + 1; w < endWord; w++)
                            grid[rowOffset + w] = ulong.MaxValue;
                        grid[rowOffset + endWord] |= endMask;
                    }
                }
            }

            long coveredCells = 0;
            for (int i = 0; i < grid.Length; i++)
            {
                if (grid[i] != 0UL)
                    coveredCells += BitOperations.PopCount(grid[i]);
            }

            double cellArea = (sizeY * sizeZ) / ((double)GridResolution * GridResolution);
            return (float)(coveredCells * cellArea);
        }

        private static void IntersectEdge(
            double ua, double va,
            double ub, double vb,
            double vc, double eps,
            ref double uLeft, ref double uRight)
        {
            double vMin = Math.Min(va, vb);
            double vMax = Math.Max(va, vb);
            if (vc < vMin - eps || vc > vMax + eps)
                return;

            double dv = vb - va;
            if (Math.Abs(dv) <= eps)
            {
                double uMin = Math.Min(ua, ub);
                double uMax = Math.Max(ua, ub);
                if (uMin < uLeft) uLeft = uMin;
                if (uMax > uRight) uRight = uMax;
                return;
            }

            double t = Math.Clamp((vc - va) / dv, 0.0, 1.0);
            double uInt = ua + t * (ub - ua);
            if (uInt < uLeft) uLeft = uInt;
            if (uInt > uRight) uRight = uInt;
        }
    }
}
