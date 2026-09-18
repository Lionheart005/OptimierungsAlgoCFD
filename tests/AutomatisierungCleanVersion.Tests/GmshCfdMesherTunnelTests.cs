using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Xunit;
using MyPicoGkProject.Core;
using MyPicoGkProject.Projects.MantaAuv;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// <c>EnsureTunnel</c> ist der Teil des Meshers, der ohne Gmsh läuft — und seit
    /// TODO-11 auch ohne PicoGK. Diese Tests decken den Weg von den JSON-Optionen bis
    /// zur geschriebenen Datei ab.
    /// </summary>
    public class GmshCfdMesherTunnelTests : IDisposable
    {
        private readonly string _workingDirectory;

        public GmshCfdMesherTunnelTests()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), "tunnel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, true);
        }

        /// <summary>
        /// Der Standardfall muss der bisherige Quader bleiben: 600 × 300 × 300 mm,
        /// im Ursprung zentriert, 12 Dreiecke. Sonst ändert sich die Simulationsdomäne.
        /// </summary>
        [Fact]
        public void Default_Options_Write_The_Previous_600x300x300_Box()
        {
            string path = new GmshCfdMesher().EnsureTunnel(_workingDirectory);

            Assert.Equal(Path.Combine(_workingDirectory, "Static_Windtunnel.stl"), path);

            List<(Vector3 Normal, StlTriangle Triangle)> triangles = StlWriterTests.ReadBinaryStl(path);
            Assert.Equal(12, triangles.Count);

            (Vector3 min, Vector3 max) = BoundingBox(triangles);
            Assert.Equal(new Vector3(-300f, -150f, -150f), min);
            Assert.Equal(new Vector3(300f, 150f, 150f), max);
        }

        [Fact]
        public void Cylinder_Shape_Is_Written_When_Selected()
        {
            GmshCfdMesher mesher = new GmshCfdMesher(new GmshMesherOptions
            {
                TunnelShape = "cylinder",
                TunnelDiameter = 200f,
                TunnelLength = 800f,
                TunnelSegments = 16
            });

            List<(Vector3 Normal, StlTriangle Triangle)> triangles =
                StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory));

            Assert.Equal(4 * 16, triangles.Count);

            (Vector3 min, Vector3 max) = BoundingBox(triangles);
            Assert.Equal(-400f, min.X, 3);
            Assert.Equal(400f, max.X, 3);
            Assert.Equal(100f, max.Y, 3);
        }

        [Fact]
        public void Unknown_Shape_Falls_Back_To_The_Box()
        {
            GmshCfdMesher mesher = new GmshCfdMesher(new GmshMesherOptions { TunnelShape = "Sphere" });

            Assert.Equal(12, StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory)).Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("  BOX ")]
        public void Empty_Or_Differently_Spelled_Box_Is_Still_A_Box(string? shape)
        {
            GmshCfdMesher mesher = new GmshCfdMesher(new GmshMesherOptions { TunnelShape = shape! });

            Assert.Equal(12, StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory)).Count);
        }

        /// <summary>Der Tunnel wird einmal erzeugt und danach wiederverwendet.</summary>
        [Fact]
        public void Tunnel_Is_Cached_Across_Calls()
        {
            GmshCfdMesher mesher = new GmshCfdMesher();

            string first = mesher.EnsureTunnel(_workingDirectory);
            DateTime written = File.GetLastWriteTimeUtc(first);

            string second = mesher.EnsureTunnel(_workingDirectory);

            Assert.Equal(first, second);
            Assert.Equal(written, File.GetLastWriteTimeUtc(second));
        }

        private static (Vector3 Min, Vector3 Max) BoundingBox(
            List<(Vector3 Normal, StlTriangle Triangle)> triangles)
        {
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach ((Vector3 _, StlTriangle t) in triangles)
            {
                foreach (Vector3 v in new[] { t.A, t.B, t.C })
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }

            return (min, max);
        }
    }
}
