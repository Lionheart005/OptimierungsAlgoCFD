using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Projects.Naca2D;
using FluidForge.Solvers.Cfd;
using Xunit;
using Xunit.Abstractions;

namespace FluidForge.Tests.Integration
{
    [Trait("Category", "Integration")]
    public sealed class Symmetry3DIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _workDir;

        public Symmetry3DIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
            _workDir = Path.Combine(Path.GetTempPath(), "sym3d_integration_" + Guid.NewGuid().ToString("N"));
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
        public void DoubleWedge3D_Full_Vs_Y0_HalfModel_Matches_CD_Within_One_Percent_And_Halves_Cells()
        {
            var sw = Stopwatch.StartNew();

            string stlPath = Path.Combine(_workDir, "double_wedge_sym.stl");
            IReadOnlyList<StlTriangle> wedgeTriangles = CreateDoubleWedgeTriangles(
                lengthX: 50.0f,
                widthY: 20.0f,
                thicknessZ: 12.0f);
            MeshMeasurement measurement = MeshMetrics.Measure(wedgeTriangles);
            Assert.True(measurement.SignedVolume > 0f);
            StlWriter.WriteBinary(stlPath, wedgeTriangles);

            var config = new SimulationConfig
            {
                MpiRunPath = string.Empty,
                GmshCores = 1,
                MpiCores = 4,
                Su2MaxIterations = 1200,
                CauchyElements = 120,
                CauchyTolerance = "1e-5"
            };

            GmshMesherOptions CreateMesherOptions(string symmetryPlane) => new()
            {
                SymmetryPlane = symmetryPlane,
                BoundaryLayerMode = "Prism",
                ClassifyAngleDeg = 20.0f,
                TargetYPlus = 5.0f,
                BoundaryLayerLayers = 6,
                BoundaryLayerMinLayers = 6,
                SurfaceCellSize = 1.8f,
                BoundaryLayerSizeMin = 1.8f,
                BoundaryLayerSizeMax = 20.0f,
                TunnelShape = "Box",
                TunnelSizeX = 600.0f,
                TunnelSizeY = 200.0f,
                TunnelSizeZ = 200.0f,
                TunnelCenterX = 200.0f,
                TunnelMinUpstreamFactor = 0.0f,
                TunnelMinDownstreamFactor = 0.0f,
                TunnelMinLateralFactor = 0.0f,
                TunnelMaxBlockagePercent = 0.0f,
                WakeRefinement = true,
                WakeLengthFactor = 2.0f,
                WakeWidthFactor = 0.5f,
                WakeCellSize = 3.6f,
                DetailBox = true,
                DetailBoxLengthFactor = 1.2f,
                DetailBoxWidthFactor = 0.25f,
                DetailBoxCellSize = 2.5f
            };

            Su2SolverOptions CreateSu2Options(string symmetryPlane) => new()
            {
                Dimensions = 3,
                SymmetryPlane = symmetryPlane,
                Density = 1.225f,
                DynamicViscosity = 1.81e-5f,
                FreestreamVelocity = 50.0f,
                ReferenceLength = 0.05f,
                ReferenceAreaMetric = "FrontalArea",
                TurbulenceModel = "SA",
                CflNumber = 5.0f,
                CflAdaptMax = 15.0f,
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "CD" },
                TreatUnconvergedAsFailed = false,
                AutoRestartOnDrift = true
            };

            string fullDir = Path.Combine(_workDir, "full");
            string halfDir = Path.Combine(_workDir, "half");
            Directory.CreateDirectory(fullDir);
            Directory.CreateDirectory(halfDir);

            // 1. Full model
            var flow = FlowConditions.From(config, CreateSu2Options("None"));
            var fullMesher = new GmshCfdMesher(CreateMesherOptions("None"), flow);
            string fullMesh = fullMesher.GenerateMesh(new MeshRequest(stlPath, 1, 1, config, 1.0f, fullDir));
            int fullCells = MeshMetrics.ReadSu2CellCount(fullMesh);

            var fullRecord = new ModelRecord { Iteration = 1, Variant = 1, StlPath = stlPath, MeshPath = fullMesh };
            fullRecord.PassiveParameters["FrontalArea"] = measurement.FrontalProjectedArea;
            foreach (var kv in fullMesher.LastMeshMetrics) fullRecord.PassiveParameters[kv.Key] = kv.Value;

            new Su2Solver(CreateSu2Options("None"), flow).Solve(fullMesh, fullRecord, config, fullDir);
            Assert.False(fullRecord.SimulationFailed, "Full 3D SU2 simulation failed.");

            // 2. Y0 Half model
            var halfMesher = new GmshCfdMesher(CreateMesherOptions("Y0"), flow);
            string halfMesh = halfMesher.GenerateMesh(new MeshRequest(stlPath, 1, 2, config, 1.0f, halfDir));
            int halfCells = MeshMetrics.ReadSu2CellCount(halfMesh);

            string halfMeshText = File.ReadAllText(halfMesh);
            Assert.Contains("MARKER_TAG= Symmetry", halfMeshText);

            var halfRecord = new ModelRecord { Iteration = 1, Variant = 2, StlPath = stlPath, MeshPath = halfMesh };
            halfRecord.PassiveParameters["FrontalArea"] = measurement.FrontalProjectedArea;
            foreach (var kv in halfMesher.LastMeshMetrics) halfRecord.PassiveParameters[kv.Key] = kv.Value;

            new Su2Solver(CreateSu2Options("Y0"), flow).Solve(halfMesh, halfRecord, config, halfDir);
            Assert.False(halfRecord.SimulationFailed, "Half 3D SU2 simulation failed.");

            sw.Stop();

            float cdFull = fullRecord.PassiveParameters["Drag"];
            float cdHalf = halfRecord.PassiveParameters["Drag"];
            double cellRatio = (double)halfCells / fullCells;
            double relDiff = Math.Abs(cdHalf - cdFull) / Math.Max(Math.Abs(cdFull), 1e-6);

            _output.WriteLine(
                $"3D Symmetry Full vs Y0 Half: FullCells={fullCells}, HalfCells={halfCells} (ratio={cellRatio:F3}), "
                + $"CD_full={cdFull:F5} (CDp={fullRecord.PassiveParameters.GetValueOrDefault("Su2_CD_p"):F5}, CDv={fullRecord.PassiveParameters.GetValueOrDefault("Su2_CD_v"):F5}, iter={fullRecord.PassiveParameters.GetValueOrDefault("Su2_Iterations"):F0}), "
                + $"CD_half={cdHalf:F5} (CDp={halfRecord.PassiveParameters.GetValueOrDefault("Su2_CD_p"):F5}, CDv={halfRecord.PassiveParameters.GetValueOrDefault("Su2_CD_v"):F5}, iter={halfRecord.PassiveParameters.GetValueOrDefault("Su2_Iterations"):F0}), "
                + $"relDiff={relDiff * 100:F3}%, DragForceN_half={halfRecord.PassiveParameters["DragForceN"]:F4} N, Elapsed={sw.Elapsed.TotalSeconds:F2}s");

            Assert.InRange(cellRatio, 0.35, 0.65);
            Assert.True(relDiff < 0.01, $"Expected |CD_half - CD_full| / CD_full < 1%, got {relDiff:P3} (CD_full={cdFull:F5}, CD_half={cdHalf:F5}).");
            Assert.Equal(2.0f, halfRecord.PassiveParameters["SymmetryFactor"]);
            Assert.True(halfRecord.PassiveParameters.TryGetValue("DragForceN", out float dragForceN) && dragForceN > 0f);
        }

        [Fact]
        public void Naca0012_2D_SST_TurbulenceModel_Runs_Cleanly_EndToEnd()
        {
            string sstDir = Path.Combine(_workDir, "sst_2d");
            Directory.CreateDirectory(sstDir);

            var config = new SimulationConfig
            {
                MpiRunPath = string.Empty,
                MpiCores = 4,
                GmshCores = 4,
                Su2MaxIterations = 80,
                CauchyElements = 40
            };

            var su2Options = new Su2SolverOptions
            {
                Dimensions = 2,
                AngleOfAttackDeg = 2.0f,
                Density = 1.225f,
                DynamicViscosity = 1.81e-5f,
                FreestreamVelocity = 51.48f,
                ReferenceLength = 0.05f,
                ReferenceAreaMetric = "Chord",
                TurbulenceModel = "SST",
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "CD", ["Lift"] = "CL" },
                TreatUnconvergedAsFailed = false,
                AutoRestartOnDrift = false
            };

            var mesherOptions = new Gmsh2DMesherOptions
            {
                FarfieldRadiusFactor = 30.0f,
                FarfieldCircleSegments = 48,
                SurfaceCellSizeFactor = 0.02f,
                NearfieldCellSizeFactor = 0.08f,
                FarfieldCellSizeFactor = 2.5f,
                BoundaryLayerLayers = 8,
                BoundaryLayerMinLayers = 4,
                TargetYPlus = 1.0f,
                WakeRefinement = true,
                DetailBox = false
            };

            var flow = FlowConditions.From(config, su2Options);
            var geom = new Naca2DGeometryGenerator(closedTrailingEdge: true, chordLength: 50.0f)
                .GenerateAndExport(1, 1, new Dictionary<string, float>
                {
                    ["MaxCamber"] = 0f,
                    ["CamberPosition"] = 40f,
                    ["MaxThickness"] = 12f
                }, sstDir, config);

            var mesher = new Gmsh2DMesher(mesherOptions, flow);
            string meshPath = mesher.GenerateMesh(new MeshRequest(geom.StlPath, 1, 1, config, 1.0f, sstDir));

            var record = new ModelRecord { Iteration = 1, Variant = 1, StlPath = geom.StlPath, MeshPath = meshPath };
            foreach (var kv in geom.Metrics) record.PassiveParameters[kv.Key] = kv.Value;

            new Su2Solver(su2Options, flow).Solve(meshPath, record, config, sstDir);

            Assert.False(record.SimulationFailed, "2D SST SU2 simulation failed.");
            Assert.True(record.PassiveParameters.TryGetValue("Drag", out float drag) && float.IsFinite(drag) && drag > 0f);
            Assert.True(record.PassiveParameters.TryGetValue("Lift", out float lift) && float.IsFinite(lift));
            Assert.Contains("KIND_TURB_MODEL= SST", File.ReadAllText(Path.Combine(sstDir, "current_config.cfg")));

            _output.WriteLine($"2D SST NACA0012 (alpha=2°): CD={drag:F5}, CL={lift:F5}");
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
                new StlTriangle(l0, r0, t0),
                new StlTriangle(l0, b0, r0),
                new StlTriangle(l1, t1, r1),
                new StlTriangle(l1, r1, b1),
                new StlTriangle(l0, t0, t1),
                new StlTriangle(l0, t1, l1),
                new StlTriangle(t0, r0, r1),
                new StlTriangle(t0, r1, t1),
                new StlTriangle(r0, b0, b1),
                new StlTriangle(r0, b1, r1),
                new StlTriangle(b0, l0, l1),
                new StlTriangle(b0, l1, b1)
            };
        }
    }
}
