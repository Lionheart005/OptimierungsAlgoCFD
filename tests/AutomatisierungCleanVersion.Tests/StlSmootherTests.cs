using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using MyPicoGkProject;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-19: der Glätter arbeitet auf einer kleinen Test-STL. Wichtigste Zusicherung:
    /// er rechnet die geglättete Hülle auf die ursprüngliche Bounding Box zurück —
    /// sonst würde die Geometrie bei jeder Glättung schrumpfen und alle Metriken
    /// (Volumen, Stirnfläche) driften.
    /// </summary>
    public class StlSmootherTests : IDisposable
    {
        private readonly string _directory;

        public StlSmootherTests()
        {
            _directory = Path.Combine(Path.GetTempPath(), "smoothtest_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }

        private string Path_(string name) => System.IO.Path.Combine(_directory, name);

        /// <summary>Quader als Test-Eingabe — dieselbe Geometrie wie der Windkanal.</summary>
        private string WriteBox(Vector3 size, Vector3 center, string name = "in.stl")
        {
            string path = Path_(name);
            StlWriter.WriteBinary(path, TunnelGeometry.CreateBox(size, center));
            return path;
        }

        private static (Vector3 Min, Vector3 Max, int Triangles) ReadStl(string path)
        {
            using BinaryReader reader = new BinaryReader(File.OpenRead(path));
            reader.BaseStream.Seek(80, SeekOrigin.Begin);
            uint count = reader.ReadUInt32();

            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            for (int i = 0; i < count; i++)
            {
                reader.BaseStream.Seek(12, SeekOrigin.Current);   // Normale
                for (int v = 0; v < 3; v++)
                {
                    var vertex = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    min = Vector3.Min(min, vertex);
                    max = Vector3.Max(max, vertex);
                }
                reader.BaseStream.Seek(2, SeekOrigin.Current);    // attribute byte count
            }

            return (min, max, (int)count);
        }

        [Fact]
        public void Bounding_Box_Survives_The_Smoothing()
        {
            var size = new Vector3(60f, 30f, 30f);
            var center = new Vector3(5f, -2f, 1f);
            string input = WriteBox(size, center);
            string output = Path_("out.stl");

            StlSmoother.SmoothStl(input, output, iterations: 20, preMeltingSteps: 2);

            var before = ReadStl(input);
            var after = ReadStl(output);

            Assert.Equal(before.Min.X, after.Min.X, 2);
            Assert.Equal(before.Min.Y, after.Min.Y, 2);
            Assert.Equal(before.Min.Z, after.Min.Z, 2);
            Assert.Equal(before.Max.X, after.Max.X, 2);
            Assert.Equal(before.Max.Y, after.Max.Y, 2);
            Assert.Equal(before.Max.Z, after.Max.Z, 2);
        }

        [Fact]
        public void Triangle_Count_Is_Preserved()
        {
            string input = WriteBox(new Vector3(20f, 20f, 20f), Vector3.Zero);
            string output = Path_("out.stl");

            StlSmoother.SmoothStl(input, output, iterations: 5, preMeltingSteps: 1);

            Assert.Equal(12, ReadStl(input).Triangles);
            Assert.Equal(12, ReadStl(output).Triangles);
        }

        /// <summary>
        /// Null Iterationen heißt "nicht glätten": die Datei wird nur kopiert. Genau dieser
        /// Pfad greift, wenn ein Projekt die Glättung über die Konfiguration abschaltet.
        /// </summary>
        [Fact]
        public void Zero_Iterations_Copies_The_File_Unchanged()
        {
            string input = WriteBox(new Vector3(20f, 10f, 10f), Vector3.Zero);
            string output = Path_("out.stl");

            StlSmoother.SmoothStl(input, output, iterations: 0, preMeltingSteps: 0);

            Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
        }

        /// <summary>Gleiche Ein- und Ausgabedatei bei 0 Iterationen darf die Datei nicht zerstören.</summary>
        [Fact]
        public void Zero_Iterations_In_Place_Keeps_The_File()
        {
            string path = WriteBox(new Vector3(20f, 10f, 10f), Vector3.Zero);
            byte[] before = File.ReadAllBytes(path);

            StlSmoother.SmoothStl(path, path, iterations: 0, preMeltingSteps: 0);

            Assert.Equal(before, File.ReadAllBytes(path));
        }

        /// <summary>
        /// Die Ausgabe muss ein lesbares binäres STL bleiben: 84 Byte Kopf plus 50 Byte
        /// je Dreieck, keine überzähligen Bytes.
        /// </summary>
        [Fact]
        public void Output_Has_A_Valid_Binary_Stl_Layout()
        {
            string input = WriteBox(new Vector3(30f, 30f, 30f), Vector3.Zero);
            string output = Path_("out.stl");

            StlSmoother.SmoothStl(input, output, iterations: 3, preMeltingSteps: 0);

            long expected = 80 + 4 + 12L * 50;
            Assert.Equal(expected, new FileInfo(output).Length);
        }

        /// <summary>
        /// Die Glättung verschiebt Ecken: die Eckpunktmenge ist nach dem Lauf eine andere
        /// als vorher. Sonst hätte der Aufruf gar nichts getan.
        /// </summary>
        [Fact]
        public void Smoothing_Actually_Moves_Vertices()
        {
            string input = WriteBox(new Vector3(40f, 20f, 20f), Vector3.Zero);
            string output = Path_("out.stl");

            StlSmoother.SmoothStl(input, output, iterations: 10, preMeltingSteps: 2);

            Assert.NotEqual(File.ReadAllBytes(input), File.ReadAllBytes(output));
        }
    }
}
