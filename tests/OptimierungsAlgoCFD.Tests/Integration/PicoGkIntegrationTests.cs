using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.PicoGk;
using PicoGK;
using Xunit;
using Xunit.Abstractions;

namespace OptimierungsAlgoCFD.Tests.Integration
{
    [Trait("Category", "Integration")]
    [Collection(ProcessWideStateCollection.Name)]
    public sealed class PicoGkIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _workDir;

        public PicoGkIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
            _workDir = Path.Combine(Path.GetTempPath(), "picogk_integration_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workDir))
            {
                try { Directory.Delete(_workDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void PicoGk_Capsule_Smoothing_Produces_Watertight_Physically_Plausible_Stl()
        {
            const float capsuleLengthMm = 30.0f;
            const float capsuleRadiusMm = 6.0f;
            const float voxelResolutionMm = 0.5f;

            string rawStlPath = Path.Combine(_workDir, "capsule_raw.stl");
            string smoothedStlPath = Path.Combine(_workDir, "capsule_smoothed.stl");

            var sw = Stopwatch.StartNew();

            var kernel = new PicoGkKernel();
            kernel.RunHosted(voxelResolutionMm, () =>
            {
                var lattice = new Lattice();
                lattice.AddBeam(
                    new Vector3(0f, 0f, 0f),
                    new Vector3(capsuleLengthMm, 0f, 0f),
                    capsuleRadiusMm,
                    capsuleRadiusMm,
                    true);

                using var voxels = new Voxels(lattice);
                using var mesh = new Mesh(voxels);
                mesh.SaveToStlFile(rawStlPath);
            });

            Assert.True(File.Exists(rawStlPath), "Raw STL file was not created by PicoGK.");

            StlSmoother.SmoothStl(rawStlPath, smoothedStlPath, iterations: 15, preMeltingSteps: 1);
            Assert.True(File.Exists(smoothedStlPath), "Smoothed STL file was not created.");

            IReadOnlyList<StlTriangle> triangles = StlWriter.ReadTriangles(smoothedStlPath);
            sw.Stop();

            Assert.True(triangles.Count > 100, $"Expected > 100 triangles, got {triangles.Count}.");

            AssertWatertight(triangles);

            MeshMeasurement measurement = MeshMetrics.Measure(triangles);
            Assert.True(measurement.SignedVolume > 0f, $"Expected positive SignedVolume (outward normals), got {measurement.SignedVolume}.");

            double expectedVolume = Math.PI * capsuleRadiusMm * capsuleRadiusMm * capsuleLengthMm
                                    + (4.0 / 3.0) * Math.PI * Math.Pow(capsuleRadiusMm, 3);
            double expectedFrontalArea = Math.PI * capsuleRadiusMm * capsuleRadiusMm;

            double volumeRelDiff = Math.Abs(measurement.Volume - expectedVolume) / expectedVolume;
            double frontalRelDiff = Math.Abs(measurement.FrontalProjectedArea - expectedFrontalArea) / expectedFrontalArea;

            _output.WriteLine(
                $"PicoGK Capsule: Triangles={triangles.Count}, Volume={measurement.Volume:F1} mm3 (analytic={expectedVolume:F1}, diff={volumeRelDiff * 100:F2}%), " +
                $"FrontalArea={measurement.FrontalProjectedArea:F1} mm2 (analytic={expectedFrontalArea:F1}, diff={frontalRelDiff * 100:F2}%), " +
                $"Elapsed={sw.Elapsed.TotalSeconds:F2}s");

            Assert.True(measurement.Volume > 0f);
            Assert.True(volumeRelDiff <= 0.25, $"Volume {measurement.Volume:F1} deviates by {volumeRelDiff * 100:F1}% from analytic {expectedVolume:F1}.");

            Assert.True(measurement.FrontalProjectedArea > 0f);
            Assert.True(frontalRelDiff <= 0.25, $"FrontalProjectedArea {measurement.FrontalProjectedArea:F1} deviates by {frontalRelDiff * 100:F1}% from analytic {expectedFrontalArea:F1}.");
        }

        private static void AssertWatertight(IReadOnlyList<StlTriangle> triangles)
        {
            var edgeCounts = new Dictionary<(VertexKey, VertexKey), int>();
            int degenerateEdges = 0;

            foreach (StlTriangle tri in triangles)
            {
                VertexKey a = VertexKey.From(tri.A);
                VertexKey b = VertexKey.From(tri.B);
                VertexKey c = VertexKey.From(tri.C);

                CountEdge(a, b);
                CountEdge(b, c);
                CountEdge(c, a);
            }

            void CountEdge(VertexKey u, VertexKey v)
            {
                int cmp = u.CompareTo(v);
                if (cmp == 0)
                {
                    degenerateEdges++;
                    return;
                }

                (VertexKey, VertexKey) key = cmp < 0 ? (u, v) : (v, u);
                edgeCounts.TryGetValue(key, out int count);
                edgeCounts[key] = count + 1;
            }

            int openEdges = 0;
            int nonManifoldEdges = 0;
            foreach (int count in edgeCounts.Values)
            {
                if (count < 2) openEdges++;
                else if (count > 2) nonManifoldEdges++;
            }

            Assert.True(degenerateEdges == 0, $"Found {degenerateEdges} degenerate zero-length edges.");
            Assert.True(openEdges == 0 && nonManifoldEdges == 0,
                $"Mesh is not watertight: openEdges={openEdges}, nonManifoldEdges={nonManifoldEdges}, totalEdges={edgeCounts.Count}.");
        }

        private readonly record struct VertexKey(int XBits, int YBits, int ZBits) : IComparable<VertexKey>
        {
            public static VertexKey From(Vector3 v) => new(
                BitConverter.SingleToInt32Bits(v.X == 0f ? 0f : v.X),
                BitConverter.SingleToInt32Bits(v.Y == 0f ? 0f : v.Y),
                BitConverter.SingleToInt32Bits(v.Z == 0f ? 0f : v.Z));

            public int CompareTo(VertexKey other)
            {
                int c = XBits.CompareTo(other.XBits);
                if (c != 0) return c;
                c = YBits.CompareTo(other.YBits);
                return c != 0 ? c : ZBits.CompareTo(other.ZBits);
            }
        }
    }
}
