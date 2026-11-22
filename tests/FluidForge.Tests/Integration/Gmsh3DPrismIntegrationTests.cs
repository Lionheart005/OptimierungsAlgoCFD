using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Solvers.Cfd;
using Xunit;
using Xunit.Abstractions;

namespace FluidForge.Tests.Integration
{
    [Trait("Category", "Integration")]
    public sealed class Gmsh3DPrismIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _workDir;

        public Gmsh3DPrismIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
            _workDir = Path.Combine(Path.GetTempPath(), "gmsh3d_prism_integration_" + Guid.NewGuid().ToString("N"));
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
        public void Gmsh3D_SharpEdged_DoubleWedge_Produces_Prisms_Without_Pyramids()
        {
            var sw = Stopwatch.StartNew();

            string stlPath = Path.Combine(_workDir, "double_wedge_3d.stl");
            IReadOnlyList<StlTriangle> wedgeTriangles = CreateDoubleWedgeTriangles(
                lengthX: 50.0f,
                widthY: 20.0f,
                thicknessZ: 12.0f);
            Assert.True(MeshMetrics.SignedVolume(wedgeTriangles) > 0f, "Test STL must have outward-facing normals.");
            StlWriter.WriteBinary(stlPath, wedgeTriangles);

            var config = new SimulationConfig
            {
                GmshCores = 4
            };

            var mesherOptions = new GmshMesherOptions
            {
                BoundaryLayerMode = "Prism",
                TargetYPlus = 30.0f,
                BoundaryLayerLayers = 3,
                BoundaryLayerMinLayers = 3,
                SurfaceCellSize = 5.0f,
                BoundaryLayerSizeMin = 5.0f,
                BoundaryLayerSizeMax = 40.0f,
                TunnelShape = "Box",
                TunnelSizeX = 400.0f,
                TunnelSizeY = 200.0f,
                TunnelSizeZ = 200.0f,
                TunnelCenterX = 100.0f,
                TunnelMinUpstreamFactor = 0.0f,
                TunnelMinDownstreamFactor = 0.0f,
                TunnelMinLateralFactor = 0.0f,
                TunnelMaxBlockagePercent = 0.0f,
                WakeRefinement = false,
                DetailBox = false
            };

            var flow = new FlowConditions(
                Velocity: 50.0f,
                Density: 1.225f,
                DynamicViscosity: 1.81e-5f,
                VelocitySource: "IntegrationTest");

            var mesher = new GmshCfdMesher(mesherOptions, flow);
            string meshPath = mesher.GenerateMesh(new MeshRequest(stlPath, 1, 1, config, 1.0f, _workDir));

            sw.Stop();

            Assert.True(File.Exists(meshPath), $"3D SU2 mesh was not created: {meshPath}");

            Su2CellBreakdown? breakdown = MeshMetrics.ReadSu2CellBreakdown(meshPath);
            Assert.NotNull(breakdown);

            _output.WriteLine(
                $"Gmsh 3D Prism: TotalCells={breakdown!.TotalVolumeCells}, Tet={breakdown.Tetrahedra}, " +
                $"Prism={breakdown.Prisms}, Pyramid={breakdown.Pyramids}, Hex={breakdown.Hexahedra}, " +
                $"MeshFallback={mesher.LastMeshMetrics.GetValueOrDefault("MeshFallback")}, " +
                $"Stage={mesher.LastMeshMetrics.GetValueOrDefault("BoundaryLayerStage")}, " +
                $"Elapsed={sw.Elapsed.TotalSeconds:F2}s");

            Assert.True(breakdown.TotalVolumeCells > 0, $"Expected TotalVolumeCells > 0, got {breakdown.TotalVolumeCells}.");
            Assert.True(breakdown.Prisms > 0, $"Expected Prisms > 0, got {breakdown.Prisms}.");
            Assert.Equal(0, breakdown.Pyramids);

            Assert.True(mesher.LastMeshMetrics.TryGetValue("MeshCells_Prism", out float metricPrisms) && metricPrisms > 0f,
                $"Expected MeshCells_Prism > 0, got {metricPrisms}.");
            Assert.True(mesher.LastMeshMetrics.TryGetValue("MeshCells_Pyramid", out float metricPyramids) && metricPyramids == 0f,
                $"Expected MeshCells_Pyramid == 0, got {metricPyramids}.");
            Assert.True(mesher.LastMeshMetrics.TryGetValue("MeshFallback", out float fallback) && fallback == 0f,
                $"Expected MeshFallback == 0 (no fallback to reduced-thickness mesh), got {fallback}.");
        }

        private static IReadOnlyList<StlTriangle> CreateDoubleWedgeTriangles(float lengthX, float widthY, float thicknessZ)
        {
            float halfL = lengthX * 0.5f;
            float halfW = widthY * 0.5f;
            float halfT = thicknessZ * 0.5f;

            Vector3 l0 = new(-halfL, -halfW, 0f);
            Vector3 t0 = new(0f, -halfW, halfT);
            Vector3 r0 = new(halfL, -halfW, 0f);
            Vector3 b0 = new(0f, -halfW, -halfT);

            Vector3 l1 = new(-halfL, halfW, 0f);
            Vector3 t1 = new(0f, halfW, halfT);
            Vector3 r1 = new(halfL, halfW, 0f);
            Vector3 b1 = new(0f, halfW, -halfT);

            return new[]
            {
                // -Y side cap
                new StlTriangle(l0, r0, t0),
                new StlTriangle(l0, b0, r0),

                // +Y side cap
                new StlTriangle(l1, t1, r1),
                new StlTriangle(l1, r1, b1),

                // Upper-front flank
                new StlTriangle(l0, t0, t1),
                new StlTriangle(l0, t1, l1),

                // Upper-rear flank
                new StlTriangle(t0, r0, r1),
                new StlTriangle(t0, r1, t1),

                // Lower-rear flank
                new StlTriangle(r0, b0, b1),
                new StlTriangle(r0, b1, r1),

                // Lower-front flank
                new StlTriangle(b0, l0, l1),
                new StlTriangle(b0, l1, b1)
            };
        }
    }
}
