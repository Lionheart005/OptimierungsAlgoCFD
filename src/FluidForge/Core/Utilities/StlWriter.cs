using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Text;

namespace FluidForge.Core
{
    /// <summary>
    /// A triangle in world coordinates (mm). Vertex winding determines the outward normal
    /// via the right-hand rule for closed shells.
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
        /// Unit face normal. Degenerate zero-area triangles return <see cref="Vector3.Zero"/>,
        /// which is valid in STL and indicates deriving the normal from vertex winding.
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
    /// Writes and reads binary STL triangle lists without requiring a geometry kernel.
    /// </summary>
    public static class StlWriter
    {
        /// <summary>
        /// 80-byte file header. Must <b>not</b> start with "solid" — many readers (including Gmsh)
        /// treat that prefix as an ASCII STL and fail to parse binary files.
        /// </summary>
        public const string DefaultHeader = "FluidForge STL UNITS=mm";

        private const int HeaderLength = 80;

        /// <summary>
        /// Reads the bounding box from a binary STL without allocating a triangle list.
        /// Returns an empty <see cref="MeshBounds"/> if the file is empty, corrupt, or has zero triangles.
        /// </summary>
        public static MeshBounds ReadBounds(string stlPath)
        {
            if (string.IsNullOrEmpty(stlPath)) throw new ArgumentException("Path must not be empty.", nameof(stlPath));
            if (!File.Exists(stlPath)) throw new FileNotFoundException("STL file not found.", stlPath);

            using BinaryReader br = new BinaryReader(File.OpenRead(stlPath));
            if (br.BaseStream.Length < 84) return default;

            br.BaseStream.Seek(80, SeekOrigin.Begin);
            uint triCount = br.ReadUInt32();
            if (triCount == 0) return default;

            if (br.BaseStream.Length < 84 + (long)triCount * 50) return default;

            Vector3 min = new Vector3(float.MaxValue, float.MaxValue, float.MaxValue);
            Vector3 max = new Vector3(float.MinValue, float.MinValue, float.MinValue);

            for (uint i = 0; i < triCount; i++)
            {
                br.BaseStream.Seek(12, SeekOrigin.Current); // skip normal
                for (int v = 0; v < 3; v++)
                {
                    float x = br.ReadSingle();
                    float y = br.ReadSingle();
                    float z = br.ReadSingle();
                    Vector3 pt = new Vector3(x, y, z);
                    min = Vector3.Min(min, pt);
                    max = Vector3.Max(max, pt);
                }
                br.BaseStream.Seek(2, SeekOrigin.Current); // skip 2 attribute bytes
            }

            return new MeshBounds(min, max);
        }

        /// <summary>
        /// Reads all triangles from a binary STL.
        /// Returns an empty array if the file is empty, corrupt, or has zero triangles.
        /// </summary>
        public static IReadOnlyList<StlTriangle> ReadTriangles(string stlPath)
        {
            if (string.IsNullOrEmpty(stlPath)) throw new ArgumentException("Path must not be empty.", nameof(stlPath));
            if (!File.Exists(stlPath)) throw new FileNotFoundException("STL file not found.", stlPath);

            using BinaryReader br = new BinaryReader(File.OpenRead(stlPath));
            if (br.BaseStream.Length < 84) return Array.Empty<StlTriangle>();

            br.BaseStream.Seek(80, SeekOrigin.Begin);
            uint triCount = br.ReadUInt32();
            if (triCount == 0) return Array.Empty<StlTriangle>();

            if (br.BaseStream.Length < 84 + (long)triCount * 50) return Array.Empty<StlTriangle>();
            if (triCount > int.MaxValue) return Array.Empty<StlTriangle>();

            var triangles = new StlTriangle[triCount];
            for (int i = 0; i < triangles.Length; i++)
            {
                br.BaseStream.Seek(12, SeekOrigin.Current); // skip normal
                Vector3 a = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                Vector3 b = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                Vector3 c = new Vector3(br.ReadSingle(), br.ReadSingle(), br.ReadSingle());
                br.BaseStream.Seek(2, SeekOrigin.Current); // skip 2 attribute bytes
                triangles[i] = new StlTriangle(a, b, c);
            }

            return triangles;
        }

        /// <summary>Writes <paramref name="triangles"/> as a binary STL to <paramref name="path"/>.</summary>
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
                writer.Write((ushort)0);   // attribute byte count, ignored by SU2/Gmsh
            }
        }

        /// <summary>
        /// Writes multiple named surface patches into a single multi-solid ASCII STL file
        /// compatible with OpenFOAM (<c>solid &lt;name&gt;</c> ... <c>endsolid &lt;name&gt;</c>).
        /// </summary>
        public static void WriteAsciiMultiSolid(
            string path,
            IReadOnlyDictionary<string, IReadOnlyList<StlTriangle>> namedPatches)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            ArgumentNullException.ThrowIfNull(namedPatches);

            string? directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            foreach (var kvp in namedPatches)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(kvp.Key);
                ArgumentNullException.ThrowIfNull(kvp.Value);

                string solidName = kvp.Key.Trim();
                writer.WriteLine($"solid {solidName}");

                foreach (StlTriangle triangle in kvp.Value)
                {
                    Vector3 n = triangle.Normal;
                    writer.WriteLine(string.Format(
                        CultureInfo.InvariantCulture,
                        "  facet normal {0:G9} {1:G9} {2:G9}",
                        n.X, n.Y, n.Z));
                    writer.WriteLine("    outer loop");
                    WriteAsciiVertex(writer, triangle.A);
                    WriteAsciiVertex(writer, triangle.B);
                    WriteAsciiVertex(writer, triangle.C);
                    writer.WriteLine("    endloop");
                    writer.WriteLine("  endfacet");
                }

                writer.WriteLine($"endsolid {solidName}");
            }
        }

        /// <summary>
        /// Reads all <c>solid &lt;name&gt;</c> block names from an ASCII STL file in order of appearance.
        /// </summary>
        public static IReadOnlyList<string> ReadAsciiSolidNames(string path)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (!File.Exists(path)) throw new FileNotFoundException("STL file not found.", path);

            var names = new List<string>();
            foreach (string rawLine in File.ReadLines(path))
            {
                string trimmed = rawLine.Trim();
                if (trimmed.StartsWith("solid", StringComparison.OrdinalIgnoreCase) &&
                    (trimmed.Length == 5 || char.IsWhiteSpace(trimmed[5])))
                {
                    string name = trimmed.Length > 5 ? trimmed.Substring(5).Trim() : string.Empty;
                    names.Add(name);
                }
            }

            return names;
        }

        private static void WriteAsciiVertex(StreamWriter writer, Vector3 v)
        {
            writer.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "      vertex {0:G9} {1:G9} {2:G9}",
                v.X, v.Y, v.Z));
        }

        /// <summary>Pads or truncates the ASCII header to exactly 80 bytes.</summary>
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
