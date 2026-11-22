using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk;
using FluidForge.Kernels.PicoGk.InternalFlow;
using FluidForge.Solvers.Cfd;
using Moq;
using Xunit;
using Xunit.Abstractions;

namespace FluidForge.Tests
{
    public sealed class MultiPatchGeometryTests : IDisposable
    {
        private readonly string _tempDir;

        public MultiPatchGeometryTests()
        {
            _tempDir = Path.Combine(Path.GetTempPath(), "multipatch_unit_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, recursive: true); } catch { }
            }
        }

        [Fact]
        public void GeometryResult_MultiPatch_SeedPoint_And_Unit_Work_With_Backward_Compatibility()
        {
            var result = new GeometryResult { StlPath = "fluid.stl" };
            Assert.False(result.HasSurfacePatches);
            Assert.Equal("mm", result.MeshUnit);
            Assert.Null(result.FluidSeedPoint);

            result.AddSurfacePatch("inlet", "fluid_inlet.stl");
            result.AddSurfacePatch("OUTLET", "fluid_outlet.stl");
            result.FluidSeedPoint = new Vector3(10f, 2f, 0f);
            result.MeshUnit = "m";

            Assert.True(result.HasSurfacePatches);
            Assert.Equal("fluid_inlet.stl", result.SurfacePatches["INLET"]);
            Assert.Equal("fluid_outlet.stl", result.SurfacePatches["outlet"]);
            Assert.Equal(new Vector3(10f, 2f, 0f), result.FluidSeedPoint);
            Assert.Equal("m", result.MeshUnit);

            Assert.Throws<ArgumentException>(() => result.AddSurfacePatch("  ", "a.stl"));
            Assert.Throws<ArgumentException>(() => result.AddSurfacePatch("wall", ""));
        }

        [Fact]
        public void WorkflowController_Passes_GeometryResult_To_MeshRequest_And_Records_Patch_Artifacts()
        {
            string stlPath = Path.Combine(_tempDir, "bend.stl");
            string inletPath = Path.Combine(_tempDir, "bend_inlet.stl");
            string outletPath = Path.Combine(_tempDir, "bend_outlet.stl");
            string wallPath = Path.Combine(_tempDir, "bend_wall.stl");

            var geoMock = new Mock<IGeometryGenerator>();
            geoMock.Setup(g => g.GenerateAndExport(
                    It.IsAny<int>(), It.IsAny<int>(),
                    It.IsAny<Dictionary<string, float>>(),
                    It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                .Returns(() =>
                {
                    var res = new GeometryResult
                    {
                        StlPath = stlPath,
                        FluidSeedPoint = new Vector3(15f, 5f, 0f),
                        MeshUnit = "mm"
                    };
                    res.AddSurfacePatch("inlet", inletPath);
                    res.AddSurfacePatch("outlet", outletPath);
                    res.AddSurfacePatch("wall", wallPath);
                    return res;
                });

            MeshRequest? capturedRequest = null;
            var mesherMock = new Mock<IMeshGenerator>();
            mesherMock.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                .Callback<MeshRequest>(req => capturedRequest = req)
                .Returns(Path.Combine(_tempDir, "mesh.su2"));

            var solverMock = new Mock<ISimulationSolver>();
            solverMock.SetupGet(s => s.Name).Returns("MockSolver");

            var optimizerMock = new Mock<IOptimizationAlgorithm>();
            optimizerMock.SetupGet(o => o.Name).Returns("MockOpt");
            optimizerMock.Setup(o => o.GenerateParameters(It.IsAny<SimulationContext>(), It.IsAny<int>(), It.IsAny<int>(), It.IsAny<int>()))
                .Returns(new Dictionary<string, float> { ["D"] = 20f });

            var config = new SimulationConfig
            {
                MaxIterations = 1,
                VariantsPerIteration = 1,
                UseRubberBandScaler = false,
                ChampionVerification = new ChampionVerificationConfig { Mode = "Off" }
            };
            var project = new ProjectConfig
            {
                ProjectName = "MultiPatchPipe",
                BaseParameters = new Dictionary<string, float> { ["D"] = 20f },
                MaxDeviations = new Dictionary<string, float> { ["D"] = 2f },
                ParameterBounds = new Dictionary<string, (float Min, float Max)> { ["D"] = (10f, 30f) }
            };
            var context = new SimulationContext(config, project, _tempDir);

            var controller = new WorkflowController(
                geoMock.Object,
                new[] { new SolverStage(mesherMock.Object, solverMock.Object) },
                new Mock<IModelValidator>().Object,
                optimizerMock.Object,
                context);

            ModelRecord record = controller.RunPipeline(new Dictionary<string, float> { ["D"] = 20f }, 1, 1);

            Assert.NotNull(capturedRequest);
            Assert.NotNull(capturedRequest!.Geometry);
            Assert.Equal(new Vector3(15f, 5f, 0f), capturedRequest.Geometry!.FluidSeedPoint);
            Assert.Equal(stlPath, record.StlPath);
            Assert.Equal(inletPath, record.Artifacts["Patch_inlet"]);
            Assert.Equal(outletPath, record.Artifacts["Patch_outlet"]);
            Assert.Equal(wallPath, record.Artifacts["Patch_wall"]);
        }

        [Fact]
        public void StlWriter_WriteAsciiMultiSolid_Writes_Named_Solids_And_Reads_Back_Names()
        {
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(new Vector3(10f, 4f, 4f), new Vector3(5f, 0f, 0f));
            var patches = new Dictionary<string, IReadOnlyList<StlTriangle>>
            {
                ["inlet"] = box.Take(2).ToList(),
                ["outlet"] = box.Skip(2).Take(2).ToList(),
                ["wall"] = box.Skip(4).ToList()
            };

            string multiSolidPath = Path.Combine(_tempDir, "multi.stl");
            StlWriter.WriteAsciiMultiSolid(multiSolidPath, patches);

            Assert.True(File.Exists(multiSolidPath));
            IReadOnlyList<string> solidNames = StlWriter.ReadAsciiSolidNames(multiSolidPath);
            Assert.Equal(new[] { "inlet", "outlet", "wall" }, solidNames);

            string text = File.ReadAllText(multiSolidPath);
            Assert.Contains("solid inlet", text);
            Assert.Contains("endsolid inlet", text);
            Assert.Contains("solid outlet", text);
            Assert.Contains("endsolid outlet", text);
            Assert.Contains("solid wall", text);
            Assert.Contains("endsolid wall", text);
            Assert.DoesNotContain(",", text); // InvariantCulture decimal separator
        }

        [Fact]
        public void MultiPatchMeshPartitioner_Partitions_Closed_Box_And_Checks_Point_Inside()
        {
            // Box spanning X:[0, 20], Y:[-5, 5], Z:[-5, 5]
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(new Vector3(20f, 10f, 10f), new Vector3(10f, 0f, 0f));

            var ports = new[]
            {
                new PatchPlaneSpec("inlet", new Vector3(0f, 0f, 0f), -Vector3.UnitX, CaptureRadius: 8f),
                new PatchPlaneSpec("outlet", new Vector3(20f, 0f, 0f), Vector3.UnitX, CaptureRadius: 8f)
            };

            PartitionResult result = MultiPatchMeshPartitioner.PartitionClosedMesh(box, ports);

            Assert.Equal(2, result.Patches["inlet"].Count);
            Assert.Equal(2, result.Patches["outlet"].Count);
            Assert.Equal(8, result.Patches["wall"].Count);
            Assert.Equal(box.Count, result.Patches.Values.Sum(list => list.Count));

            Assert.Equal(100f, result.PatchAreas["inlet"], 2);
            Assert.Equal(100f, result.PatchAreas["outlet"], 2);
            Assert.Equal(800f, result.PatchAreas["wall"], 2);

            Assert.True(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(box, new Vector3(10f, 0f, 0f)));
            Assert.True(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(box, new Vector3(1f, 4f, -4f)));
            Assert.False(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(box, new Vector3(-1f, 0f, 0f)));
            Assert.False(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(box, new Vector3(25f, 0f, 0f)));
            Assert.False(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(box, new Vector3(10f, 6f, 0f)));
        }

        [Fact]
        public void MultiPatchMeshPartitioner_Partitions_Closed_Cylinder_With_Exact_Area_And_Sum()
        {
            const float radius = 5f;
            const float length = 30f;
            IReadOnlyList<StlTriangle> cyl = CreateClosedCylinderAlongX(length, radius, segments: 72);

            Assert.Equal((0, 0), MeshMetrics.CountBoundaryAndNonManifoldEdges(cyl));

            var ports = new[]
            {
                new PatchPlaneSpec("inlet", Vector3.Zero, -Vector3.UnitX, CaptureRadius: radius + 1f),
                new PatchPlaneSpec("outlet", new Vector3(length, 0f, 0f), Vector3.UnitX, CaptureRadius: radius + 1f)
            };

            PartitionResult partition = MultiPatchMeshPartitioner.PartitionClosedMesh(cyl, ports);

            int totalAssigned = partition.Patches["inlet"].Count + partition.Patches["outlet"].Count + partition.Patches["wall"].Count;
            Assert.Equal(cyl.Count, totalAssigned);

            float expectedCapArea = MathF.PI * radius * radius;
            Assert.InRange(partition.PatchAreas["inlet"], expectedCapArea * 0.99f, expectedCapArea * 1.01f);
            Assert.InRange(partition.PatchAreas["outlet"], expectedCapArea * 0.99f, expectedCapArea * 1.01f);

            Assert.True(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(cyl, new Vector3(length * 0.5f, 0f, 0f)));
            Assert.False(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(cyl, new Vector3(length * 0.5f, radius + 1f, 0f)));
        }

        [Fact]
        public void MultiPatchMeshPartitioner_Throws_When_Declared_Port_Receives_Zero_Triangles()
        {
            IReadOnlyList<StlTriangle> box = TunnelGeometry.CreateBox(new Vector3(10f, 10f, 10f), new Vector3(5f, 0f, 0f));

            var ports = new[]
            {
                new PatchPlaneSpec("inlet", new Vector3(0f, 0f, 0f), -Vector3.UnitX, CaptureRadius: 8f),
                new PatchPlaneSpec("missing_port", new Vector3(999f, 0f, 0f), Vector3.UnitX, CaptureRadius: 8f)
            };

            var ex = Assert.Throws<InvalidOperationException>(() =>
                MultiPatchMeshPartitioner.PartitionClosedMesh(box, ports));

            Assert.Contains("missing_port", ex.Message);
        }

        private static IReadOnlyList<StlTriangle> CreateClosedCylinderAlongX(float length, float radius, int segments)
        {
            var triangles = new List<StlTriangle>(segments * 4);
            var startCenter = new Vector3(0f, 0f, 0f);
            var endCenter = new Vector3(length, 0f, 0f);

            var startRing = new Vector3[segments];
            var endRing = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = 2f * MathF.PI * i / segments;
                float y = radius * MathF.Cos(angle);
                float z = radius * MathF.Sin(angle);
                startRing[i] = new Vector3(0f, y, z);
                endRing[i] = new Vector3(length, y, z);
            }

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;
                Vector3 s0 = startRing[i];
                Vector3 s1 = startRing[next];
                Vector3 e0 = endRing[i];
                Vector3 e1 = endRing[next];

                // Inlet cap (normal -X)
                triangles.Add(new StlTriangle(startCenter, s1, s0));
                // Outlet cap (normal +X)
                triangles.Add(new StlTriangle(endCenter, e0, e1));
                // Lateral wall (outward radial)
                triangles.Add(new StlTriangle(s0, s1, e1));
                triangles.Add(new StlTriangle(s0, e1, e0));
            }

            return triangles;
        }
    }

    [Trait("Category", "Integration")]
    [Collection(ProcessWideStateCollection.Name)]
    public sealed class MultiPatchGeometryPicoGkIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _workDir;

        public MultiPatchGeometryPicoGkIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
            _workDir = Path.Combine(Path.GetTempPath(), "multipatch_picogk_" + Guid.NewGuid().ToString("N"));
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
        public void ShapeKernel_90Deg_PipeBend_Exports_Watertight_MultiPatch_Stls()
        {
            const float pipeRadiusMm = 10f;
            const float bendRadiusMm = 25f;
            const float inletStraightMm = 20f;
            const float outletStraightMm = 20f;
            const float voxelResolutionMm = 0.5f;

            InternalFlowExportResult? export = null;
            Exception? kernelException = null;
            var kernel = new PicoGkKernel();

            kernel.RunHosted(voxelResolutionMm, () =>
            {
                try
                {
                    BuiltFluidVolume built = ShapeKernelPipeBuilder.BuildPipeBend90(
                        pipeRadiusMm: pipeRadiusMm,
                        bendRadiusMm: bendRadiusMm,
                        inletStraightMm: inletStraightMm,
                        outletStraightMm: outletStraightMm);

                    using (built.FluidVoxels)
                    {
                        export = MultiPatchFluidExporter.Export(
                            built.FluidVoxels,
                            built.Ports,
                            built.FluidSeedPoint,
                            _workDir,
                            baseName: "bend90",
                            trimPorts: true,
                            applySmoothing: false);
                    }
                }
                catch (Exception ex)
                {
                    kernelException = ex;
                }
            });

            if (kernelException != null)
            {
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(kernelException).Throw();
            }

            Assert.NotNull(export);
            InternalFlowExportResult res = export!;

            Assert.True(File.Exists(res.CombinedStlPath), "Combined STL is missing.");
            Assert.True(File.Exists(res.MultiSolidStlPath), "Multi-solid ASCII STL is missing.");
            Assert.True(File.Exists(res.PatchPaths["inlet"]), "Inlet patch STL is missing.");
            Assert.True(File.Exists(res.PatchPaths["outlet"]), "Outlet patch STL is missing.");
            Assert.True(File.Exists(res.PatchPaths["wall"]), "Wall patch STL is missing.");

            IReadOnlyList<StlTriangle> combinedTriangles = StlWriter.ReadTriangles(res.CombinedStlPath);
            IReadOnlyList<StlTriangle> inletTriangles = StlWriter.ReadTriangles(res.PatchPaths["inlet"]);
            IReadOnlyList<StlTriangle> outletTriangles = StlWriter.ReadTriangles(res.PatchPaths["outlet"]);
            IReadOnlyList<StlTriangle> wallTriangles = StlWriter.ReadTriangles(res.PatchPaths["wall"]);

            Assert.Equal((0, 0), MeshMetrics.CountBoundaryAndNonManifoldEdges(combinedTriangles));

            int patchSum = inletTriangles.Count + outletTriangles.Count + wallTriangles.Count;
            Assert.Equal(combinedTriangles.Count, patchSum);

            Assert.True(MultiPatchMeshPartitioner.IsPointInsideClosedMesh(combinedTriangles, res.FluidSeedPoint));

            float analyticPortArea = MathF.PI * pipeRadiusMm * pipeRadiusMm;
            float inletArea = res.PatchAreas["inlet"];
            float outletArea = res.PatchAreas["outlet"];
            float wallArea = res.PatchAreas["wall"];

            float inletRelErr = MathF.Abs(inletArea - analyticPortArea) / analyticPortArea;
            float outletRelErr = MathF.Abs(outletArea - analyticPortArea) / analyticPortArea;

            _output.WriteLine(
                $"90-deg Bend: CombinedTris={combinedTriangles.Count}, InletTris={inletTriangles.Count}, OutletTris={outletTriangles.Count}, WallTris={wallTriangles.Count}");
            _output.WriteLine(
                $"Areas (mm2): Inlet={inletArea:F2} (err={inletRelErr * 100:F2}%), Outlet={outletArea:F2} (err={outletRelErr * 100:F2}%), Wall={wallArea:F2}, AnalyticPort={analyticPortArea:F2}");
            _output.WriteLine(
                $"Volume={res.Measurement.Volume:F2} mm3, SeedPoint=({res.FluidSeedPoint.X:F2}, {res.FluidSeedPoint.Y:F2}, {res.FluidSeedPoint.Z:F2})");

            Assert.True(inletRelErr < 0.08f, $"Inlet area {inletArea:F2} deviates by {inletRelErr * 100:F2}% from {analyticPortArea:F2}.");
            Assert.True(outletRelErr < 0.08f, $"Outlet area {outletArea:F2} deviates by {outletRelErr * 100:F2}% from {analyticPortArea:F2}.");

            IReadOnlyList<string> solidNames = StlWriter.ReadAsciiSolidNames(res.MultiSolidStlPath);
            Assert.Contains("inlet", solidNames);
            Assert.Contains("outlet", solidNames);
            Assert.Contains("wall", solidNames);

            GeometryResult geoResult = res.ToGeometryResult();
            Assert.True(geoResult.HasSurfacePatches);
            Assert.Equal(res.FluidSeedPoint, geoResult.FluidSeedPoint);
            Assert.Equal("mm", geoResult.MeshUnit);
            Assert.Equal(res.Measurement.Volume, geoResult.Metrics["Volume"], 2);
        }
    }
}
