using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

namespace MyPicoGkProject
{
    /// <summary>
    /// Ein Dreieck in Weltkoordinaten (mm). Die Reihenfolge der Ecken bestimmt über die
    /// Rechte-Hand-Regel die Normale; für eine geschlossene Hülle zeigt sie nach außen.
    /// </summary>
    public readonly struct StlTriangle
    {
        public StlTriangle(Vector3 a, Vector3 b, Vector3 c)
        {
            A = a;
            B = b;
            C = c;
        }

        public Vector3 A { get; }
        public Vector3 B { get; }
        public Vector3 C { get; }

        /// <summary>
        /// Normierte Flächennormale. Entartete Dreiecke (Fläche 0) liefern den Nullvektor —
        /// das ist im STL erlaubt und bedeutet "Normale aus der Eckenreihenfolge ableiten".
        /// </summary>
        public Vector3 Normal
        {
            get
            {
                Vector3 normal = Vector3.Cross(B - A, C - A);
                return normal.LengthSquared() > 1e-12f ? Vector3.Normalize(normal) : Vector3.Zero;
            }
        }
    }

    /// <summary>
    /// Schreibt eine Dreiecksliste als binäres STL — ohne Geometrie-Kernel.
    ///
    /// Damit kommt die CFD-Schicht ohne PicoGK aus (TODO-11): sie muss den Windkanal nur
    /// als Datei an Gmsh übergeben, nicht als Voxelfeld rechnen.
    /// </summary>
    public static class StlWriter
    {
        /// <summary>
        /// 80-Byte-Kopf der Datei. Darf <b>nicht</b> mit "solid" beginnen — daran erkennen
        /// viele Leser (auch Gmsh) ein ASCII-STL und würden die Datei falsch interpretieren.
        /// </summary>
        public const string DefaultHeader = "Automatisierung STL UNITS=mm";

        private const int HeaderLength = 80;

        /// <summary>Schreibt <paramref name="triangles"/> als binäres STL nach <paramref name="path"/>.</summary>
        public static void WriteBinary(string path, IReadOnlyList<StlTriangle> triangles, string header = DefaultHeader)
        {
            if (triangles == null) throw new ArgumentNullException(nameof(triangles));

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using BinaryWriter writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write));

            writer.Write(BuildHeader(header));
            writer.Write((uint)triangles.Count);

            foreach (StlTriangle triangle in triangles)
            {
                Vector3 normal = triangle.Normal;
                Write(writer, normal);
                Write(writer, triangle.A);
                Write(writer, triangle.B);
                Write(writer, triangle.C);
                writer.Write((ushort)0);   // attribute byte count, von SU2/Gmsh ignoriert
            }
        }

        /// <summary>ASCII-Kopf auf genau 80 Byte gebracht: zu lang wird abgeschnitten, zu kurz aufgefüllt.</summary>
        private static byte[] BuildHeader(string header)
        {
            byte[] buffer = new byte[HeaderLength];
            for (int i = 0; i < HeaderLength; i++) buffer[i] = 0x20;

            byte[] text = System.Text.Encoding.ASCII.GetBytes(header ?? string.Empty);
            Array.Copy(text, buffer, Math.Min(text.Length, HeaderLength));

            return buffer;
        }

        private static void Write(BinaryWriter writer, Vector3 vector)
        {
            writer.Write(vector.X);
            writer.Write(vector.Y);
            writer.Write(vector.Z);
        }
    }
}
