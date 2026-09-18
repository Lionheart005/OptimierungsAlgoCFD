using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Text;
using Xunit;
using MyPicoGkProject;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// Der STL-Writer ersetzt <c>Mesh.SaveToStlFile</c> aus PicoGK (TODO-11). Er muss ein
    /// binäres STL schreiben, das Gmsh unverändert einliest.
    /// </summary>
    public class StlWriterTests
    {
        [Fact]
        public void Writes_A_Binary_Stl_With_The_Expected_Layout()
        {
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(
                new Vector3(600f, 300f, 300f), Vector3.Zero);

            string path = TempFile();
            try
            {
                StlWriter.WriteBinary(path, box);

                byte[] raw = File.ReadAllBytes(path);

                // 80 Byte Kopf + 4 Byte Anzahl + 50 Byte je Dreieck
                Assert.Equal(84 + 50 * box.Count, raw.Length);
                Assert.Equal((uint)box.Count, BitConverter.ToUInt32(raw, 80));
            }
            finally
            {
                Delete(path);
            }
        }

        /// <summary>
        /// Beginnt der Kopf mit "solid", halten Leser die Datei für ein ASCII-STL.
        /// </summary>
        [Fact]
        public void Header_Does_Not_Start_With_Solid()
        {
            string path = TempFile();
            try
            {
                StlWriter.WriteBinary(path, TunnelGeometry.CreateBox(Vector3.One, Vector3.Zero));

                string header = Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 80);

                Assert.False(header.TrimStart().StartsWith("solid", StringComparison.OrdinalIgnoreCase));
            }
            finally
            {
                Delete(path);
            }
        }

        [Fact]
        public void Overlong_Header_Is_Truncated_To_Eighty_Bytes()
        {
            string path = TempFile();
            try
            {
                StlWriter.WriteBinary(
                    path,
                    TunnelGeometry.CreateBox(Vector3.One, Vector3.Zero),
                    new string('X', 200));

                Assert.Equal(84 + 50 * 12, File.ReadAllBytes(path).Length);
            }
            finally
            {
                Delete(path);
            }
        }

        /// <summary>
        /// Die geschriebenen Ecken und Normalen müssen wieder herauskommen — sonst
        /// vernetzt Gmsh eine andere Domäne, als hier berechnet wurde.
        /// </summary>
        [Fact]
        public void Round_Trip_Keeps_Vertices_And_Normals()
        {
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(
                new Vector3(600f, 300f, 300f), Vector3.Zero);

            string path = TempFile();
            try
            {
                StlWriter.WriteBinary(path, box);

                List<(Vector3 Normal, StlTriangle Triangle)> read = ReadBinaryStl(path);

                Assert.Equal(box.Count, read.Count);

                for (int i = 0; i < box.Count; i++)
                {
                    Assert.Equal(box[i].A, read[i].Triangle.A);
                    Assert.Equal(box[i].B, read[i].Triangle.B);
                    Assert.Equal(box[i].C, read[i].Triangle.C);
                    Assert.Equal(box[i].Normal, read[i].Normal);
                }
            }
            finally
            {
                Delete(path);
            }
        }

        [Fact]
        public void Degenerate_Triangle_Gets_A_Zero_Normal_Instead_Of_NaN()
        {
            StlTriangle degenerate = new StlTriangle(Vector3.Zero, Vector3.Zero, Vector3.Zero);

            Assert.Equal(Vector3.Zero, degenerate.Normal);
        }

        // --- Hilfsfunktionen ------------------------------------------------

        internal static List<(Vector3 Normal, StlTriangle Triangle)> ReadBinaryStl(string path)
        {
            List<(Vector3, StlTriangle)> triangles = new List<(Vector3, StlTriangle)>();

            using BinaryReader reader = new BinaryReader(File.OpenRead(path));

            reader.BaseStream.Seek(80, SeekOrigin.Begin);
            uint count = reader.ReadUInt32();

            for (uint i = 0; i < count; i++)
            {
                Vector3 normal = ReadVector(reader);
                Vector3 a = ReadVector(reader);
                Vector3 b = ReadVector(reader);
                Vector3 c = ReadVector(reader);
                reader.ReadUInt16();

                triangles.Add((normal, new StlTriangle(a, b, c)));
            }

            return triangles;
        }

        private static Vector3 ReadVector(BinaryReader reader)
            => new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

        private static string TempFile()
            => Path.Combine(Path.GetTempPath(), "stl_" + Guid.NewGuid().ToString("N") + ".stl");

        private static void Delete(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
