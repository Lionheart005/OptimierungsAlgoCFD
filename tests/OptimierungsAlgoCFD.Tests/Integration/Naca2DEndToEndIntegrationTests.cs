using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Projects.Naca2D;
using OptimierungsAlgoCFD.Solvers.Cfd;
using Xunit;
using Xunit.Abstractions;

namespace OptimierungsAlgoCFD.Tests.Integration
{
    [Trait("Category", "Integration")]
    public sealed class Naca2DEndToEndIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _workDir;

        public Naca2DEndToEndIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
            _workDir = Path.Combine(Path.GetTempPath(), "naca2d_integration_" + Guid.NewGuid().ToString("N"));
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
        public void Naca0012_2D_Planar_Gmsh_And_Su2_Run_EndToEnd()
        {
            var sw = Stopwatch.StartNew();

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
                SpeedOfSound = 343.2f,
                ReferenceLength = 0.05f,
                ReferenceAreaMetric = "Chord",
                TurbulenceModel = "SA",
                ResultMetrics = new Dictionary<string, string>
                {
                    ["Drag"] = "CD",
                    ["Lift"] = "CL"
                },
                TreatUnconvergedAsFailed = false,
                AutoRestartOnDrift = false,
                HistoryOutput = new[] { "ITER", "RMS_RES", "AERO_COEFF" }
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

            // 1. 2D-Geometrie (NACA 0012, c = 50 mm)
            var generator = new Naca2DGeometryGenerator(closedTrailingEdge: true, chordLength: 50.0f);
            var parameters = new Dictionary<string, float>
            {
                ["MaxCamber"] = 0.0f,
                ["CamberPosition"] = 40.0f,
                ["MaxThickness"] = 12.0f
            };

            GeometryResult geom = generator.GenerateAndExport(1, 1, parameters, _workDir, config);
            Assert.Equal(GeometryKind.Contour2D, geom.Kind);
            Assert.True(File.Exists(geom.StlPath), $"Contour JSON missing: {geom.StlPath}");
            Assert.True(geom.Metrics.TryGetValue("Chord", out float chord));
            Assert.InRange(chord, 49.5f, 50.5f);

            // 2. 2D-Gmsh-Vernetzung
            var mesher = new Gmsh2DMesher(mesherOptions, flow);
            string meshPath = mesher.GenerateMesh(new MeshRequest(geom.StlPath, 1, 1, config, 1.0f, _workDir));
            Assert.True(File.Exists(meshPath), $"SU2 mesh missing: {meshPath}");
            Assert.True(mesher.LastMeshMetrics.TryGetValue("MeshCells", out float meshCells) && meshCells > 300f,
                $"Expected MeshCells > 300, got {meshCells}.");

            (int tri2DCount, int quad2DCount) = CountSu2DomainElements2D(meshPath);
            Assert.True(quad2DCount > 0, $"Expected 2D Quad boundary-layer cells (VTK type 9) > 0 in NELEM, got {quad2DCount}.");
            Assert.True(tri2DCount > 0, $"Expected 2D Triangle cells (VTK type 5) > 0 in NELEM, got {tri2DCount}.");

            // 3. SU2-2D-Simulation (OpenMP Direct Mode)
            var record = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                StlPath = geom.StlPath,
                MeshPath = meshPath
            };
            foreach (var kv in geom.Metrics)
            {
                record.PassiveParameters[kv.Key] = kv.Value;
            }
            foreach (var kv in mesher.LastMeshMetrics)
            {
                record.PassiveParameters[kv.Key] = kv.Value;
            }

            var solver = new Su2Solver(su2Options, flow);
            solver.Solve(meshPath, record, config, _workDir);

            sw.Stop();

            string historyPath = Path.Combine(_workDir, "history.csv");
            Assert.True(File.Exists(historyPath), "history.csv was not produced by SU2.");
            Assert.False(record.SimulationFailed, "SU2 simulation was marked as failed.");

            Assert.True(record.PassiveParameters.TryGetValue("Drag", out float drag), "Drag metric missing.");
            Assert.True(float.IsFinite(drag) && drag > 0f, $"Expected finite Drag > 0, got {drag}.");

            Assert.True(record.PassiveParameters.TryGetValue("Lift", out float lift), "Lift metric missing.");
            Assert.True(float.IsFinite(lift), $"Expected finite Lift, got {lift}.");

            Assert.True(record.PassiveParameters.TryGetValue("YPlus_Max", out float yPlusMax), "YPlus_Max missing.");
            Assert.True(record.PassiveParameters.TryGetValue("YPlus_Median", out float yPlusMedian), "YPlus_Median missing.");
            Assert.True(record.PassiveParameters.TryGetValue("YPlus_P95", out float yPlusP95), "YPlus_P95 missing.");
            Assert.True(float.IsFinite(yPlusMax) && yPlusMax > 0f, $"Expected finite YPlus_Max > 0, got {yPlusMax}.");
            Assert.True(float.IsFinite(yPlusMedian) && yPlusMedian > 0f, $"Expected finite YPlus_Median > 0, got {yPlusMedian}.");
            Assert.True(float.IsFinite(yPlusP95) && yPlusP95 > 0f, $"Expected finite YPlus_P95 > 0, got {yPlusP95}.");

            _output.WriteLine(
                $"NACA0012 2D E2E: Chord={chord:F2} mm, MeshCells={meshCells:F0} (Tri2D={tri2DCount}, Quad2D={quad2DCount}), " +
                $"CD={drag:F5}, CL={lift:F5}, y+_Median={yPlusMedian:F3}, y+_P95={yPlusP95:F3}, y+_Max={yPlusMax:F3}, " +
                $"Elapsed={sw.Elapsed.TotalSeconds:F2}s");

            Assert.True(sw.Elapsed.TotalSeconds < 60.0, $"End-to-end runtime {sw.Elapsed.TotalSeconds:F2}s exceeded 60s target.");
        }

        private static (int Triangles, int Quads) CountSu2DomainElements2D(string su2MeshPath)
        {
            using var reader = new StreamReader(su2MeshPath);
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                string trimmed = line.Trim();
                if (!trimmed.StartsWith("NELEM=", StringComparison.OrdinalIgnoreCase))
                    continue;

                int total = int.Parse(trimmed.Substring("NELEM=".Length).Trim(), System.Globalization.CultureInfo.InvariantCulture);
                int tris = 0;
                int quads = 0;
                for (int i = 0; i < total; i++)
                {
                    string? elem = reader.ReadLine();
                    if (elem == null) break;
                    string e = elem.TrimStart();
                    if (e.StartsWith("5 ", StringComparison.Ordinal) || e.StartsWith("5\t", StringComparison.Ordinal))
                        tris++;
                    else if (e.StartsWith("9 ", StringComparison.Ordinal) || e.StartsWith("9\t", StringComparison.Ordinal))
                        quads++;
                }
                return (tris, quads);
            }

            return (0, 0);
        }
    }
}
