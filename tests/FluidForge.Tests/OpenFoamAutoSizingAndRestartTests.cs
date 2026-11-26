using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using FluidForge.Core;
using FluidForge.Solvers.OpenFoam;
using Moq;
using Xunit;

namespace FluidForge.Tests
{
    public sealed class OpenFoamAutoSizingAndRestartTests : TempDirectoryTest
    {
        private static IReadOnlyList<StlTriangle> CreateUnitBoxTriangles(Vector3 min, Vector3 max)
        {
            var p000 = new Vector3(min.X, min.Y, min.Z);
            var p100 = new Vector3(max.X, min.Y, min.Z);
            var p110 = new Vector3(max.X, max.Y, min.Z);
            var p010 = new Vector3(min.X, max.Y, min.Z);
            var p001 = new Vector3(min.X, min.Y, max.Z);
            var p101 = new Vector3(max.X, min.Y, max.Z);
            var p111 = new Vector3(max.X, max.Y, max.Z);
            var p011 = new Vector3(min.X, max.Y, max.Z);

            return new[]
            {
                new StlTriangle(p000, p011, p010),
                new StlTriangle(p000, p001, p011),
                new StlTriangle(p100, p110, p111),
                new StlTriangle(p100, p111, p101),
                new StlTriangle(p000, p100, p101),
                new StlTriangle(p000, p101, p001),
                new StlTriangle(p010, p111, p110),
                new StlTriangle(p010, p011, p111),
                new StlTriangle(p000, p010, p110),
                new StlTriangle(p000, p110, p100),
                new StlTriangle(p001, p111, p011),
                new StlTriangle(p001, p101, p111)
            };
        }

        private GeometryResult WriteSquarePipeGeometry(string dir, float diameterMm, float lengthMm)
        {
            Directory.CreateDirectory(dir);
            float half = 0.5f * diameterMm;
            IReadOnlyList<StlTriangle> all = CreateUnitBoxTriangles(
                new Vector3(0f, -half, -half),
                new Vector3(lengthMm, half, half));

            string combined = Path.Combine(dir, "pipe.stl");
            string inletPath = Path.Combine(dir, "pipe_inlet.stl");
            string outletPath = Path.Combine(dir, "pipe_outlet.stl");
            string wallPath = Path.Combine(dir, "pipe_wall.stl");

            StlWriter.WriteBinary(combined, all);
            StlWriter.WriteBinary(inletPath, all.Take(2).ToList());
            StlWriter.WriteBinary(outletPath, all.Skip(2).Take(2).ToList());
            StlWriter.WriteBinary(wallPath, all.Skip(4).ToList());

            var geo = new GeometryResult
            {
                Kind = GeometryKind.SurfaceStl,
                StlPath = combined,
                FluidSeedPoint = new Vector3(0.5f * lengthMm, 0f, 0f),
                MeshUnit = "mm"
            };
            geo.AddSurfacePatch("inlet", inletPath);
            geo.AddSurfacePatch("outlet", outletPath);
            geo.AddSurfacePatch("wall", wallPath);
            return geo;
        }

        private static void WriteMinimalCaseDictionaries(string caseDir, int endTime = 1000)
        {
            Directory.CreateDirectory(Path.Combine(caseDir, "system"));
            File.WriteAllText(Path.Combine(caseDir, "system", "controlDict"),
                $"application simpleFoam;\nstartFrom startTime;\nstartTime 0;\nendTime {endTime};\n");
            File.WriteAllText(Path.Combine(caseDir, "system", "fvSolution"),
                "solvers\n{\n    p\n    {\n        tolerance 1e-6;\n        relTol 0.01;\n    }\n}\n"
                + "SIMPLE\n{\n    residualControl\n    {\n        p 1e-4;\n        U 1e-4;\n    }\n}\n");
        }

        // ------------------------------------------------------------------
        // 1. OpenFoamSimpleSolver AutoRestartOnDrift Tests
        // ------------------------------------------------------------------

        [Fact]
        public void SimpleSolver_AutoRestarts_On_Drift_Patches_ControlDict_And_ResidualControl_Without_Redecompose()
        {
            string caseDir = Sub("solver_autorestart_parallel");
            WriteMinimalCaseDictionaries(caseDir, endTime: 1000);

            int solveCalls = 0;
            var commands = new List<string>();
            var runnerMock = new Mock<IOpenFoamCommandRunner>();
            runnerMock.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns((string dir, string cmd, OpenFoamOptions _, int _) =>
                {
                    commands.Add(cmd);
                    if (!cmd.Contains("simpleFoam", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "", "");

                    solveCalls++;
                    if (solveCalls == 1)
                    {
                        Directory.CreateDirectory(Path.Combine(dir, "processor0", "178"));
                        string forceDir0 = Path.Combine(dir, "postProcessing", "forceCoeffs", "0");
                        Directory.CreateDirectory(forceDir0);
                        var lines = new List<string> { "# Time Cd Cl" };
                        for (int i = 1; i <= 30; i++)
                        {
                            lines.Add(string.Create(CultureInfo.InvariantCulture, $"{i * 5} {0.10f + i * 0.01f} 0.0"));
                        }
                        File.WriteAllText(Path.Combine(forceDir0, "coefficient.dat"), string.Join("\n", lines));
                        return new OpenFoamCommandResult(0, "Time = 178\nSIMPLE solution converged in 178 iterations\n", "");
                    }

                    Directory.CreateDirectory(Path.Combine(dir, "processor0", "350"));
                    string forceDir178 = Path.Combine(dir, "postProcessing", "forceCoeffs", "178");
                    Directory.CreateDirectory(forceDir178);
                    var lines2 = new List<string> { "# Time Cd Cl" };
                    for (int i = 1; i <= 30; i++)
                    {
                        lines2.Add(string.Create(CultureInfo.InvariantCulture, $"{178 + i * 5} 0.42 0.0"));
                    }
                    File.WriteAllText(Path.Combine(forceDir178, "coefficient.dat"), string.Join("\n", lines2));
                    return new OpenFoamCommandResult(0, "Time = 350\nSIMPLE solution converged in 350 iterations\n", "");
                });

            var opts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                AutoRestartOnDrift = true,
                MaxConvergenceRestarts = 3,
                ResidualTarget = 1e-4f,
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };
            var solver = new OpenFoamSimpleSolver(opts, runnerMock.Object);
            var record = new ModelRecord();

            solver.Solve(caseDir, record, new SimulationConfig { MpiCores = 4 }, caseDir);

            Assert.False(record.SimulationFailed);
            Assert.Equal(1f, record.PassiveParameters["OpenFoam_Converged"]);
            Assert.Equal(1f, record.PassiveParameters["OpenFoam_Restarts"]);
            Assert.Equal(0f, record.PassiveParameters["OpenFoam_Unsteady"]);
            Assert.Equal(2, solveCalls);

            // decomposePar only once (never re-run on restart)
            Assert.Equal(1, commands.Count(c => c.StartsWith("decomposePar", StringComparison.Ordinal)));

            // controlDict patched to latestTime, fvSolution residualControl patched to 1e-5, solvers.*.tolerance untouched
            string controlDict = File.ReadAllText(Path.Combine(caseDir, "system", "controlDict"));
            Assert.Contains("startFrom latestTime;", controlDict);

            string fvSolution = File.ReadAllText(Path.Combine(caseDir, "system", "fvSolution"));
            Assert.Contains("tolerance 1e-6;", fvSolution);
            System.Text.RegularExpressions.Match pResMatch = Regex.Match(fvSolution, @"residualControl\s*\{[^}]*\bp\s+([^;\s]+);", RegexOptions.CultureInvariant);
            System.Text.RegularExpressions.Match uResMatch = Regex.Match(fvSolution, @"residualControl\s*\{[^}]*\bU\s+([^;\s]+);", RegexOptions.CultureInvariant);
            Assert.True(pResMatch.Success, "Expected patched p in residualControl.");
            Assert.True(uResMatch.Success, "Expected patched U in residualControl.");
            Assert.InRange(float.Parse(pResMatch.Groups[1].Value, CultureInfo.InvariantCulture), 0.9e-5f, 1.1e-5f);
            Assert.InRange(float.Parse(uResMatch.Groups[1].Value, CultureInfo.InvariantCulture), 0.9e-5f, 1.1e-5f);
        }

        [Fact]
        public void SimpleSolver_Does_Not_Restart_On_Stationary_Oscillation_And_Writes_Uncertainty()
        {
            string caseDir = Sub("solver_unsteady_stationary");
            WriteMinimalCaseDictionaries(caseDir, endTime: 1000);

            int solveCalls = 0;
            var runnerMock = new Mock<IOpenFoamCommandRunner>();
            runnerMock.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns((string dir, string cmd, OpenFoamOptions _, int _) =>
                {
                    if (!cmd.Contains("simpleFoam", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "", "");

                    solveCalls++;
                    Directory.CreateDirectory(Path.Combine(dir, "200"));
                    string forceDir = Path.Combine(dir, "postProcessing", "forceCoeffs", "0");
                    Directory.CreateDirectory(forceDir);
                    var lines = new List<string> { "# Time Cd Cl" };
                    for (int i = 0; i < 60; i++)
                    {
                        float cd = 0.30f + 0.04f * MathF.Sin(2f * MathF.PI * i / 7f);
                        lines.Add(string.Create(CultureInfo.InvariantCulture, $"{i + 1} {cd} 0.0"));
                    }
                    File.WriteAllText(Path.Combine(forceDir, "coefficient.dat"), string.Join("\n", lines));
                    return new OpenFoamCommandResult(0, "Time = 60\n", "");
                });

            var opts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                AutoRestartOnDrift = true,
                TrailingWindowIterations = 60,
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };
            var solver = new OpenFoamSimpleSolver(opts, runnerMock.Object);
            var record = new ModelRecord();

            solver.Solve(caseDir, record, new SimulationConfig { MpiCores = 1 }, caseDir);

            Assert.False(record.SimulationFailed);
            Assert.Equal(1, solveCalls);
            Assert.Equal(1f, record.PassiveParameters["OpenFoam_Converged"]);
            Assert.Equal(1f, record.PassiveParameters["OpenFoam_Unsteady"]);
            Assert.Equal(0f, record.PassiveParameters["OpenFoam_Restarts"]);
            Assert.True(record.PassiveParameters.ContainsKey(MetricUncertainty.KeyFor("Drag")));
        }

        [Fact]
        public void SimpleSolver_Respects_MaxConvergenceRestarts_And_EndTime_Budget()
        {
            // 1. Respects MaxConvergenceRestarts = 1
            string caseDir1 = Sub("solver_max_restarts");
            WriteMinimalCaseDictionaries(caseDir1, endTime: 1000);
            int calls1 = 0;
            var runner1 = new Mock<IOpenFoamCommandRunner>();
            runner1.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns((string dir, string cmd, OpenFoamOptions _, int _) =>
                {
                    if (!cmd.Contains("simpleFoam", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "", "");
                    calls1++;
                    int t = calls1 * 100;
                    Directory.CreateDirectory(Path.Combine(dir, t.ToString(CultureInfo.InvariantCulture)));
                    string forceDir = Path.Combine(dir, "postProcessing", "forceCoeffs", "0");
                    Directory.CreateDirectory(forceDir);
                    var lines = Enumerable.Range(1, 30)
                        .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i} {0.1f + i * 0.02f} 0.0"));
                    File.WriteAllText(Path.Combine(forceDir, "coefficient.dat"), "# Time Cd Cl\n" + string.Join("\n", lines));
                    return new OpenFoamCommandResult(0, $"Time = {t}\nSIMPLE solution converged in {t} iterations\n", "");
                });

            var opts1 = new OpenFoamOptions
            {
                DomainMode = "External3D",
                AutoRestartOnDrift = true,
                MaxConvergenceRestarts = 1,
                TreatUnconvergedAsFailed = false,
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };
            var rec1 = new ModelRecord();
            new OpenFoamSimpleSolver(opts1, runner1.Object).Solve(caseDir1, rec1, new SimulationConfig { MpiCores = 1 }, caseDir1);
            Assert.Equal(2, calls1);
            Assert.Equal(1f, rec1.PassiveParameters["OpenFoam_Restarts"]);

            // 2. Respects endTime budget (iterations >= endTime -> 0 restarts)
            string caseDir2 = Sub("solver_endtime_budget");
            WriteMinimalCaseDictionaries(caseDir2, endTime: 200);
            int calls2 = 0;
            var runner2 = new Mock<IOpenFoamCommandRunner>();
            runner2.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns((string dir, string cmd, OpenFoamOptions _, int _) =>
                {
                    if (!cmd.Contains("simpleFoam", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "", "");
                    calls2++;
                    Directory.CreateDirectory(Path.Combine(dir, "200"));
                    string forceDir = Path.Combine(dir, "postProcessing", "forceCoeffs", "0");
                    Directory.CreateDirectory(forceDir);
                    var lines = Enumerable.Range(1, 30)
                        .Select(i => string.Create(CultureInfo.InvariantCulture, $"{i} {0.1f + i * 0.02f} 0.0"));
                    File.WriteAllText(Path.Combine(forceDir, "coefficient.dat"), "# Time Cd Cl\n" + string.Join("\n", lines));
                    return new OpenFoamCommandResult(0, "Time = 200\n", "");
                });

            var opts2 = new OpenFoamOptions
            {
                DomainMode = "External3D",
                AutoRestartOnDrift = true,
                MaxConvergenceRestarts = 3,
                TreatUnconvergedAsFailed = false,
                ResultMetrics = new Dictionary<string, string> { ["Drag"] = "Drag" }
            };
            var rec2 = new ModelRecord();
            new OpenFoamSimpleSolver(opts2, runner2.Object).Solve(caseDir2, rec2, new SimulationConfig { MpiCores = 1 }, caseDir2);
            Assert.Equal(1, calls2);
            Assert.Equal(0f, rec2.PassiveParameters["OpenFoam_Restarts"]);
        }

        // ------------------------------------------------------------------
        // 2. OpenFoamCaseGenerator & OpenFoamSnappyMesher Auto-Sizing Tests
        // ------------------------------------------------------------------

        [Theory]
        [InlineData(4f)]
        [InlineData(30f)]
        [InlineData(200f)]
        public void CaseGenerator_Internal3D_AutoSizing_Is_ScaleInvariant_Across_Diameters(float diameterMm)
        {
            string geomDir = Sub($"pipe_scale_{diameterMm}");
            GeometryResult geo = WriteSquarePipeGeometry(geomDir, diameterMm, lengthMm: diameterMm * 5f);
            string caseDir = Sub($"case_scale_{diameterMm}");

            var opts = new OpenFoamOptions
            {
                DomainMode = "Internal3D",
                BaseCellSizeMm = 0f,
                BoundaryLayerLayers = 0,
                CellsPerCharacteristicLength = 20f,
                TargetYPlus = 5f
            };
            var req = new MeshRequest(geo.StlPath, 1, 1, SimulationConfig.CreateDefault(), 1.0f, Root, Geometry: geo);
            OpenFoamCaseInfo info = OpenFoamCaseGenerator.Generate(caseDir, req, opts);

            Assert.NotNull(info.MeshPlan);
            OpenFoamMeshPlan plan = info.MeshPlan!;
            Assert.True(plan.IsAutoSized);
            Assert.False(plan.HasRefinementRegions);

            float dh = plan.CharacteristicLengthM;
            Assert.Equal(1f / 20f, plan.TargetSurfaceCellSizeM / dh, 4);
            Assert.True(plan.BaseCellSizeM <= dh / 5.0f + 1e-6f);
        }

        [Fact]
        public void CaseGenerator_External3D_AutoSizing_Creates_Clipped_WakeBoxes_And_Physics_Layers()
        {
            string geomDir = Sub("ext_autosize_geom");
            Directory.CreateDirectory(geomDir);
            // Body from (0, -25, -25) to (400, 25, 25) mm -> 0.4 m long
            IReadOnlyList<StlTriangle> body = CreateUnitBoxTriangles(
                new Vector3(0f, -25f, -25f),
                new Vector3(400f, 25f, 25f));
            string stlPath = Path.Combine(geomDir, "body.stl");
            StlWriter.WriteBinary(stlPath, body);

            string caseDir = Sub("ext_autosize_case");
            var sim = new SimulationConfig { MpiCores = 1 };
            var opts = new OpenFoamOptions
            {
                DomainMode = "External3D",
                SymmetryPlane = "Y0",
                BaseCellSizeMm = 0f,
                BoundaryLayerLayers = 0,
                CellsPerCharacteristicLength = 120f,
                TargetYPlus = 5f,
                TurbulenceModel = "kOmegaSST",
                SecondOrderTurbulence = true,
                MaxGlobalCells = 15_000_000,
                MaxLocalCells = 4_000_000
            };

            OpenFoamCaseInfo info = OpenFoamCaseGenerator.Generate(
                caseDir,
                new MeshRequest(stlPath, 1, 1, sim, 1.0f, Root),
                opts);

            Assert.NotNull(info.MeshPlan);
            OpenFoamMeshPlan plan = info.MeshPlan!;
            Assert.True(plan.IsAutoSized);
            Assert.True(plan.HasRefinementRegions);
            Assert.False(plan.UseRelativeLayerSizes);
            Assert.InRange(plan.EffectiveLayers, 3, 28);
            Assert.True(plan.MinLayerThicknessM < 0.1f && plan.MinLayerThicknessM > 0f);

            string snappyDict = File.ReadAllText(Path.Combine(caseDir, "system", "snappyHexMeshDict"));
            Assert.Contains("relativeSizes   false;", snappyDict);
            Assert.Contains("firstLayerThickness ", snappyDict);
            Assert.Contains("minVol          1e-18;", snappyDict);
            Assert.Contains("wakeBox", snappyDict);
            Assert.Contains("nearfieldBox", snappyDict);

            // Refinement levels in refinementRegions must be >= 1
            foreach (System.Text.RegularExpressions.Match m in Regex.Matches(snappyDict, @"levels\s*\(\(1E15\s+(\d+)\)\)", RegexOptions.CultureInvariant))
            {
                int lvl = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                Assert.True(lvl >= 1, $"Expected refinement region level >= 1, got {lvl}.");
            }

            // wakeBox min.Y must be clipped to >= 0 because SymmetryPlane = Y0
            System.Text.RegularExpressions.Match wakeMinMatch = Regex.Match(
                snappyDict,
                @"wakeBox\s*\{\s*type searchableBox;\s*min\s*\(([^\s]+)\s+([^\s]+)\s+([^\s]+)\);",
                RegexOptions.CultureInvariant);
            Assert.True(wakeMinMatch.Success);
            float wakeMinY = float.Parse(wakeMinMatch.Groups[2].Value, CultureInfo.InvariantCulture);
            Assert.True(wakeMinY >= 0f, $"wakeBox min.Y ({wakeMinY}) must be clipped to SymmetryPlane Y0 (>= 0).");

            // Serial guard (MpiCores == 1): maxLocalCells >= MaxGlobalCells (15_000_000)
            System.Text.RegularExpressions.Match maxLocalMatch = Regex.Match(snappyDict, @"maxLocalCells\s+(\d+);", RegexOptions.CultureInvariant);
            Assert.True(maxLocalMatch.Success);
            int maxLocalCells = int.Parse(maxLocalMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            Assert.True(maxLocalCells >= 15_000_000);

            // Wall functions & 2nd-order turbulence
            Assert.Contains("nutUSpaldingWallFunction", File.ReadAllText(Path.Combine(caseDir, "0", "nut")));
            Assert.Contains("kLowReWallFunction", File.ReadAllText(Path.Combine(caseDir, "0", "k")));
            string fvSchemes = File.ReadAllText(Path.Combine(caseDir, "system", "fvSchemes"));
            Assert.Contains("bounded Gauss linearUpwind grad(k);", fvSchemes);
            Assert.Contains("grad(k)", fvSchemes);
            Assert.Contains("cellLimited Gauss linear 1;", fvSchemes);

            // TargetYPlus > 5 switches 0/k wall function to kqRWallFunction
            string caseDirHighYp = Sub("ext_high_yplus");
            var optsHighYp = opts.Clone();
            optsHighYp.TargetYPlus = 30f;
            optsHighYp.SecondOrderTurbulence = false;
            OpenFoamCaseGenerator.Generate(caseDirHighYp, new MeshRequest(stlPath, 1, 1, sim, 1.0f, Root), optsHighYp);
            Assert.Contains("kqRWallFunction", File.ReadAllText(Path.Combine(caseDirHighYp, "0", "k")));
            Assert.DoesNotContain("bounded Gauss linearUpwind grad(k);", File.ReadAllText(Path.Combine(caseDirHighYp, "system", "fvSchemes")));
        }

        [Fact]
        public void CaseGenerator_Preserves_Explicit_Sizing_Backward_Compatibility()
        {
            string geomDir = Sub("explicit_geom");
            GeometryResult geo = WriteSquarePipeGeometry(geomDir, diameterMm: 20f, lengthMm: 100f);
            string caseDir = Sub("explicit_case");

            var opts = new OpenFoamOptions
            {
                DomainMode = "Internal3D",
                BaseCellSizeMm = 2.0f,
                BoundaryLayerLayers = 5,
                RelativeLayerSizes = true
            };

            OpenFoamCaseInfo info = OpenFoamCaseGenerator.Generate(
                caseDir,
                new MeshRequest(geo.StlPath, 1, 1, SimulationConfig.CreateDefault(), 1.0f, Root, Geometry: geo),
                opts);

            Assert.NotNull(info.MeshPlan);
            Assert.False(info.MeshPlan!.IsAutoSized);
            Assert.True(info.MeshPlan.UseRelativeLayerSizes);
            Assert.False(info.MeshPlan.HasRefinementRegions);

            string snappyDict = File.ReadAllText(Path.Combine(caseDir, "system", "snappyHexMeshDict"));
            Assert.Contains("relativeSizes   true;", snappyDict);
            Assert.Contains("refinementRegions {}", snappyDict);
        }

        [Fact]
        public void SnappyMesher_Exposes_Zero_TargetSurfaceCellSizeMm_In_AutoMode_And_Records_EffectiveLayers()
        {
            string geomDir = Sub("mesher_auto_geom");
            string workDir = Sub("mesher_auto_work");
            Directory.CreateDirectory(workDir);
            GeometryResult geo = WriteSquarePipeGeometry(geomDir, diameterMm: 20f, lengthMm: 100f);

            var runnerMock = new Mock<IOpenFoamCommandRunner>();
            runnerMock.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns((string _, string cmd, OpenFoamOptions _, int _) =>
                {
                    if (cmd.StartsWith("checkMesh", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "cells: 25000\nMesh OK.\n", "");
                    if (cmd.Contains("snappyHexMesh", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "wall 800 8.0 0.0005 90.0\n", "");
                    return new OpenFoamCommandResult(0, "", "");
                });

            var opts = new OpenFoamOptions
            {
                DomainMode = "Internal3D",
                BaseCellSizeMm = 0f,
                BoundaryLayerLayers = 0,
                TargetYPlus = 5f
            };
            var mesher = new OpenFoamSnappyMesher(opts, runnerMock.Object);
            Assert.Equal(0f, mesher.TargetSurfaceCellSizeMm);

            string caseDir = mesher.GenerateMesh(new MeshRequest(geo.StlPath, 1, 1, new SimulationConfig { MpiCores = 1 }, 1.0f, workDir, Geometry: geo));
            Assert.True(Directory.Exists(caseDir));
            Assert.InRange(mesher.LastMeshMetrics["BoundaryLayerLayers"], 3f, 28f);
        }

        [Theory]
        [InlineData(2)]
        [InlineData(3)]
        public void MeshCalibration_Allows_Unlimited_Cells_When_MaxCells_Is_Zero(int dimensions)
        {
            var sim = SimulationConfig.CreateDefault();
            sim.MeshCalibration.Mode = "Auto";
            sim.MeshCalibration.MaxCells2D = 0;
            sim.MeshCalibration.MaxCells3D = 0;

            var project = new ProjectConfig
            {
                BaseParameters = new Dictionary<string, float> { ["L"] = 100f }
            };
            var ctx = new ProjectContext("UnlimitedCells", null, null, sim, project);
            string workDir = Sub($"cal_unlimited_{dimensions}d");
            Directory.CreateDirectory(workDir);
            var simCtx = new SimulationContext(sim, project, workDir);

            long observedLimit = -1;
            var (decision, _) = MeshCalibration.Run(
                ctx,
                simCtx,
                (factor, _) =>
                {
                    observedLimit = simCtx.CellLimit;
                    return new ModelRecord
                    {
                        PassiveParameters =
                        {
                            ["Drag"] = 0.25f,
                            ["MeshCells"] = 25_000_000f
                        }
                    };
                },
                monitoredMetrics: new[] { "Drag" },
                dimensions: dimensions);

            Assert.Equal(0L, observedLimit);
            Assert.NotNull(decision);
        }
    }
}
