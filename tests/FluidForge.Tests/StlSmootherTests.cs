using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;
using Xunit;

namespace FluidForge.Tests
{
    /// <summary>
    /// TODO-19: the smoother operates on a small test STL. Key invariant:
    /// it rescales the smoothed hull back to the original bounding box —
    /// otherwise the geometry would shrink on every smoothing pass and all metrics
    /// (volume, frontal area) would drift.
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

        /// <summary>Box as test input — the same geometry as the wind tunnel.</summary>
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
                reader.BaseStream.Seek(12, SeekOrigin.Current);   // normal
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
        /// Zero iterations means "do not smooth": the file is simply copied. This exact
        /// path triggers when a project disables smoothing via configuration.
        /// </summary>
        [Fact]
        public void Zero_Iterations_Copies_The_File_Unchanged()
        {
            string input = WriteBox(new Vector3(20f, 10f, 10f), Vector3.Zero);
            string output = Path_("out.stl");

            StlSmoother.SmoothStl(input, output, iterations: 0, preMeltingSteps: 0);

            Assert.Equal(File.ReadAllBytes(input), File.ReadAllBytes(output));
        }

        /// <summary>Same input and output file with 0 iterations must not destroy the file.</summary>
        [Fact]
        public void Zero_Iterations_In_Place_Keeps_The_File()
        {
            string path = WriteBox(new Vector3(20f, 10f, 10f), Vector3.Zero);
            byte[] before = File.ReadAllBytes(path);

            StlSmoother.SmoothStl(path, path, iterations: 0, preMeltingSteps: 0);

            Assert.Equal(before, File.ReadAllBytes(path));
        }

        /// <summary>
        /// Output must remain a valid binary STL: 84-byte header plus 50 bytes
        /// per triangle, with no extra trailing bytes.
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
        /// Smoothing moves vertices: the vertex set after the pass differs from
        /// before. Otherwise the call would have done nothing.
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
