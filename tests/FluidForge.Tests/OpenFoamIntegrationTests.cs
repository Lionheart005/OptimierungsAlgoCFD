using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.PicoGk;
using FluidForge.Kernels.PicoGk.InternalFlow;
using FluidForge.Solvers.OpenFoam;
using Xunit;
using Xunit.Abstractions;

namespace FluidForge.Tests.Integration
{
    /// <summary>
    /// Fact attribute that runs only on Linux when an ESI OpenFOAM installation
    /// (preferring <c>/usr/lib/openfoam/openfoam2506</c>) is present.
    /// </summary>
    public sealed class OpenFoamFactAttribute : FactAttribute
    {
        public OpenFoamFactAttribute()
        {
            if (!OperatingSystem.IsLinux())
            {
                Skip = "OpenFOAM ESI (openfoam2506) is not installed on this machine.";
                return;
            }

            string bashrc = OpenFoamProcessRunner.ResolveEffectiveBashrcPath(new OpenFoamOptions());
            if (!File.Exists(bashrc))
            {
                Skip = "OpenFOAM ESI (openfoam2506) is not installed on this machine.";
            }
        }
    }

    [Trait("Category", "Integration")]
    [Trait("Category", "OpenFoam")]
    [Collection(ProcessWideStateCollection.Name)]
    public sealed class OpenFoamIntegrationTests : IDisposable
    {
        private readonly ITestOutputHelper _output;
        private readonly string _workDir;

        public OpenFoamIntegrationTests(ITestOutputHelper output)
        {
            _output = output;
            _workDir = Path.Combine(Path.GetTempPath(), "openfoam_integration_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workDir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workDir))
            {
                try { Directory.Delete(_workDir, recursive: true); } catch { }
            }
        }

        [OpenFoamFact]
        public void StraightPipe_LaminarHagenPoiseuille_ValidatesMeshMassBalanceAndPressureDrop()
        {
            var sw = Stopwatch.StartNew();

            string geoDir = Path.Combine(_workDir, "geo");
            GeometryResult geo = WriteAnalyticalStraightPipe(geoDir, lengthMm: 100f, radiusMm: 10f, segments: 64);

            var options = new OpenFoamOptions
            {
                DomainMode = "Internal3D",
                TurbulenceModel = "laminar",
                Density = 998.2f,
                DynamicViscosity = 1.002e-3f,
                InletVelocity = 0.05f,
                BaseCellSizeMm = 2.5f,
                SurfaceRefinementLevelMin = 1,
                SurfaceRefinementLevelMax = 1,
                AddLayers = false,
                EndTime = 400,
                WriteInterval = 100,
                ResidualTarget = 1e-4f,
                MaxMassBalanceErrorPercent = 1.0f
            };

            var config = new SimulationConfig
            {
                MpiCores = 1
            };

            var mesher = new OpenFoamSnappyMesher(options);
            string caseDir = mesher.GenerateMesh(new MeshRequest(
                geo.GeometryPath,
                Iteration: 1,
                Variant: 1,
                Config: config,
                ScaleFactor: 1.0f,
                WorkingDirectory: _workDir,
                RefinementFactor: 1.0f,
                Geometry: geo));

            Assert.True(mesher.LastMeshMetrics.TryGetValue("MeshCells", out float meshCells), "MeshCells metric missing.");
            Assert.True(meshCells > 500f, $"Expected MeshCells > 500, got {meshCells}.");

            var record = new ModelRecord
            {
                Iteration = 1,
                Variant = 1,
                StlPath = geo.GeometryPath,
                MeshPath = caseDir
            };
            foreach (KeyValuePair<string, float> kvp in mesher.LastMeshMetrics)
                record.PassiveParameters[kvp.Key] = kvp.Value;

            var solver = new OpenFoamSimpleSolver(options);
            solver.Solve(caseDir, record, config, _workDir);
            sw.Stop();

            Assert.False(record.SimulationFailed, "OpenFOAM laminar straight-pipe simulation failed.");
            Assert.True(record.PassiveParameters.TryGetValue("MassBalanceError", out float massBalanceError), "MassBalanceError missing.");
            Assert.True(massBalanceError < 1.0f, $"Expected MassBalanceError < 1.0%, got {massBalanceError:F4}%.");

            Assert.True(record.PassiveParameters.TryGetValue("PressureDrop", out float pressureDrop), "PressureDrop missing.");
            Assert.True(pressureDrop > 0.2f && pressureDrop < 5.0f,
                $"Expected laminar straight-pipe PressureDrop in (0.2, 5.0) Pa, got {pressureDrop:F4} Pa.");

            float nonOrtho = record.PassiveParameters.GetValueOrDefault("MeshNonOrthoMax", float.NaN);
            float iters = record.PassiveParameters.GetValueOrDefault("OpenFoam_Iterations", float.NaN);

            _output.WriteLine(
                $"StraightPipe (laminar): MeshCells={meshCells:F0}, NonOrthoMax={nonOrtho:F2} deg, " +
                $"Iters={iters:F0}, PressureDrop={pressureDrop:F4} Pa, MassBalanceError={massBalanceError:F4}%, " +
                $"Elapsed={sw.Elapsed.TotalSeconds:F2}s");
        }

        [OpenFoamFact]
        public void PipeBend90_ShapeKernelAndKOmegaSstParallel_SolvesCleanly()
        {
            var sw = Stopwatch.StartNew();

            string geoDir = Path.Combine(_workDir, "geo_bend90");
            Directory.CreateDirectory(geoDir);

            InternalFlowExportResult? exported = null;
            var kernel = new PicoGkKernel();
            kernel.RunHosted(0.5f, () =>
            {
                BuiltFluidVolume built = ShapeKernelPipeBuilder.BuildPipeBend90(10f, 25f, 25f, 25f);
                try
                {
                    exported = MultiPatchFluidExporter.Export(
                        built.FluidVoxels,
                        built.Ports,
                        built.FluidSeedPoint,
                        geoDir,
                        "pipe_bend90");
                }
                finally
                {
                    built.FluidVoxels.Dispose();
                }
            });

            Assert.NotNull(exported);
            GeometryResult geo = exported!.ToGeometryResult();

            var options = new OpenFoamOptions
            {
                DomainMode = "Internal3D",
                TurbulenceModel = "kOmegaSST",
                Density = 998.2f,
                DynamicViscosity = 1.002e-3f,
                InletVelocity = 1.0f,
                BaseCellSizeMm = 2.5f,
                SurfaceRefinementLevelMin = 1,
                SurfaceRefinementLevelMax = 1,
                AddLayers = true,
                BoundaryLayerLayers = 2,
                MinLayerCoveragePercent = 20f,
                EndTime = 350,
                WriteInterval = 100,
                MaxMassBalanceErrorPercent = 1.0f
            };

            var config = new SimulationConfig
            {
                MpiCores = 4
            };

            var mesher = new OpenFoamSnappyMesher(options);
            string caseDir = mesher.GenerateMesh(new MeshRequest(
                geo.GeometryPath,
                Iteration: 1,
                Variant: 2,
                Config: config,
                ScaleFactor: 1.0f,
                WorkingDirectory: _workDir,
                RefinementFactor: 1.0f,
                Geometry: geo));

            Assert.True(mesher.LastMeshMetrics.TryGetValue("MeshCells", out float meshCells), "MeshCells metric missing.");
            Assert.True(meshCells > 1000f, $"Expected MeshCells > 1000, got {meshCells}.");

            var record = new ModelRecord
            {
                Iteration = 1,
                Variant = 2,
                StlPath = geo.GeometryPath,
                MeshPath = caseDir
            };
            foreach (KeyValuePair<string, float> kvp in mesher.LastMeshMetrics)
                record.PassiveParameters[kvp.Key] = kvp.Value;

            var solver = new OpenFoamSimpleSolver(options);
            solver.Solve(caseDir, record, config, _workDir);
            sw.Stop();

            Assert.False(record.SimulationFailed, "OpenFOAM parallel PipeBend90 simulation failed.");
            Assert.True(File.Exists(Path.Combine(caseDir, "case.foam")), "case.foam marker file is missing.");

            Assert.True(record.PassiveParameters.TryGetValue("MassBalanceError", out float massBalanceError), "MassBalanceError missing.");
            Assert.True(massBalanceError < 1.0f, $"Expected MassBalanceError < 1.0%, got {massBalanceError:F4}%.");

            Assert.True(record.PassiveParameters.TryGetValue("PressureDrop", out float pressureDrop), "PressureDrop missing.");
            Assert.True(pressureDrop > 20f && pressureDrop < 2500f,
                $"Expected PipeBend90 PressureDrop in (20, 2500) Pa, got {pressureDrop:F4} Pa.");

            Assert.True(record.PassiveParameters.TryGetValue("YPlus_Med", out float yPlusMed), "YPlus_Med missing.");
            Assert.True(yPlusMed > 0f, $"Expected YPlus_Med > 0, got {yPlusMed}.");

            float yPlusMax = record.PassiveParameters.GetValueOrDefault("YPlus_Max", float.NaN);
            float nonOrtho = record.PassiveParameters.GetValueOrDefault("MeshNonOrthoMax", float.NaN);
            float layerCov = record.PassiveParameters.GetValueOrDefault("BoundaryLayerCoveragePct", float.NaN);
            float iters = record.PassiveParameters.GetValueOrDefault("OpenFoam_Iterations", float.NaN);

            _output.WriteLine(
                $"PipeBend90 (kOmegaSST, np=4): MeshCells={meshCells:F0}, NonOrthoMax={nonOrtho:F2} deg, " +
                $"LayerCoverage={layerCov:F1}%, Iters={iters:F0}, PressureDrop={pressureDrop:F2} Pa, " +
                $"MassBalanceError={massBalanceError:F4}%, YPlus_Med={yPlusMed:F2}, YPlus_Max={yPlusMax:F2}, " +
                $"Elapsed={sw.Elapsed.TotalSeconds:F2}s");
        }

        private static GeometryResult WriteAnalyticalStraightPipe(
            string outputDirectory,
            float lengthMm,
            float radiusMm,
            int segments)
        {
            Directory.CreateDirectory(outputDirectory);

            var ringIn = new Vector3[segments];
            var ringOut = new Vector3[segments];
            for (int i = 0; i < segments; i++)
            {
                float angle = (2f * MathF.PI * i) / segments;
                float y = radiusMm * MathF.Cos(angle);
                float z = radiusMm * MathF.Sin(angle);
                ringIn[i] = new Vector3(0f, y, z);
                ringOut[i] = new Vector3(lengthMm, y, z);
            }

            var inletCenter = new Vector3(0f, 0f, 0f);
            var outletCenter = new Vector3(lengthMm, 0f, 0f);

            var inletTris = new List<StlTriangle>(segments);
            var outletTris = new List<StlTriangle>(segments);
            var wallTris = new List<StlTriangle>(segments * 2);

            for (int i = 0; i < segments; i++)
            {
                int next = (i + 1) % segments;

                // Inlet at x=0: outward normal points -X
                inletTris.Add(new StlTriangle(inletCenter, ringIn[next], ringIn[i]));

                // Outlet at x=L: outward normal points +X
                outletTris.Add(new StlTriangle(outletCenter, ringOut[i], ringOut[next]));

                // Wall: outward normal points radially outward
                wallTris.Add(new StlTriangle(ringIn[i], ringOut[next], ringOut[i]));
                wallTris.Add(new StlTriangle(ringIn[i], ringIn[next], ringOut[next]));
            }

            var allTris = new List<StlTriangle>(inletTris.Count + outletTris.Count + wallTris.Count);
            allTris.AddRange(inletTris);
            allTris.AddRange(outletTris);
            allTris.AddRange(wallTris);

            (int openEdges, int nonManifoldEdges) = MeshMetrics.CountBoundaryAndNonManifoldEdges(allTris);
            Assert.Equal(0, openEdges);
            Assert.Equal(0, nonManifoldEdges);

            MeshMeasurement measurement = MeshMetrics.Measure(allTris);
            Assert.True(measurement.SignedVolume > 0f, $"Expected positive SignedVolume, got {measurement.SignedVolume}.");

            string combinedPath = Path.Combine(outputDirectory, "straight_pipe.stl");
            string inletPath = Path.Combine(outputDirectory, "straight_pipe_inlet.stl");
            string outletPath = Path.Combine(outputDirectory, "straight_pipe_outlet.stl");
            string wallPath = Path.Combine(outputDirectory, "straight_pipe_wall.stl");

            StlWriter.WriteBinary(combinedPath, allTris);
            StlWriter.WriteBinary(inletPath, inletTris);
            StlWriter.WriteBinary(outletPath, outletTris);
            StlWriter.WriteBinary(wallPath, wallTris);

            var geo = new GeometryResult
            {
                Kind = GeometryKind.SurfaceStl,
                StlPath = combinedPath,
                FluidSeedPoint = new Vector3(lengthMm * 0.5f, 0f, 0f),
                MeshUnit = "mm"
            };
            geo.AddSurfacePatch("inlet", inletPath);
            geo.AddSurfacePatch("outlet", outletPath);
            geo.AddSurfacePatch("wall", wallPath);
            return geo;
        }
    }
}
