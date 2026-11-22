using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using FluidForge.Cli;
using FluidForge.Composition;
using FluidForge.Core;
using FluidForge.Projects.PipeBend90;
using FluidForge.Solvers.OpenFoam;
using Moq;
using Xunit;

namespace FluidForge.Tests
{
    public sealed class OpenFoamSolverTests : TempDirectoryTest
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
                // -X face (outward normal -X)
                new StlTriangle(p000, p011, p010),
                new StlTriangle(p000, p001, p011),
                // +X face (outward normal +X)
                new StlTriangle(p100, p110, p111),
                new StlTriangle(p100, p111, p101),
                // -Y face (outward normal -Y)
                new StlTriangle(p000, p100, p101),
                new StlTriangle(p000, p101, p001),
                // +Y face (outward normal +Y)
                new StlTriangle(p010, p111, p110),
                new StlTriangle(p010, p011, p111),
                // -Z face (outward normal -Z)
                new StlTriangle(p000, p010, p110),
                new StlTriangle(p000, p110, p100),
                // +Z face (outward normal +Z)
                new StlTriangle(p001, p111, p011),
                new StlTriangle(p001, p101, p111)
            };
        }

        private GeometryResult WriteMultiPatchPipeGeometry(
            string dir,
            float scaleFactor = 1.0f,
            float pipeLengthMm = 100f,
            float halfSizeMm = 10f)
        {
            Directory.CreateDirectory(dir);
            IReadOnlyList<StlTriangle> all = CreateUnitBoxTriangles(
                new Vector3(0f, -halfSizeMm * scaleFactor, -halfSizeMm * scaleFactor),
                new Vector3(pipeLengthMm * scaleFactor, halfSizeMm * scaleFactor, halfSizeMm * scaleFactor));

            var inletTris = all.Take(2).ToList();       // -X face
            var outletTris = all.Skip(2).Take(2).ToList(); // +X face
            var wallTris = all.Skip(4).ToList();

            string combined = Path.Combine(dir, "pipe.stl");
            string inletPath = Path.Combine(dir, "pipe_inlet.stl");
            string outletPath = Path.Combine(dir, "pipe_outlet.stl");
            string wallPath = Path.Combine(dir, "pipe_wall.stl");

            StlWriter.WriteBinary(combined, all);
            StlWriter.WriteBinary(inletPath, inletTris);
            StlWriter.WriteBinary(outletPath, outletTris);
            StlWriter.WriteBinary(wallPath, wallTris);

            var geo = new GeometryResult
            {
                Kind = GeometryKind.SurfaceStl,
                StlPath = combined,
                FluidSeedPoint = new Vector3(0.5f * pipeLengthMm * scaleFactor, 0f, 0f),
                MeshUnit = "mm"
            };
            geo.AddSurfacePatch("inlet", inletPath);
            geo.AddSurfacePatch("outlet", outletPath);
            geo.AddSurfacePatch("wall", wallPath);
            return geo;
        }

        // ------------------------------------------------------------------
        // 1. Options & ICalibrationSolverOptions
        // ------------------------------------------------------------------

        [Fact]
        public void OpenFoamOptions_Implements_ICalibrationSolverOptions_And_Clones_Cleanly()
        {
            var opts = new OpenFoamOptions
            {
                TargetYPlus = 2.5f,
                YPlusWarnFactor = 4.0f,
                InletDirection = new[] { 1f, 0f, 0f },
                ResultMetrics = new Dictionary<string, string> { ["PressureDrop"] = "PressureDrop", ["Drag"] = "Drag" },
                CustomDictionaryOverrides = new Dictionary<string, Dictionary<string, string>>
                {
                    ["system/fvSolution"] = new() { ["nNonOrthogonalCorrectors"] = "2" }
                }
            };

            ICalibrationSolverOptions cal = opts;
            Assert.Equal(3, cal.Dimensions);
            Assert.Equal(2.5f, cal.TargetYPlus);
            Assert.Equal(4.0f, cal.YPlusWarnFactor);
            Assert.Contains("PressureDrop", cal.MonitoredMetricNames);
            Assert.Contains("Drag", cal.MonitoredMetricNames);

            OpenFoamOptions clone = opts.Clone();
            clone.InletDirection[0] = -1f;
            clone.ResultMetrics["Lift"] = "Lift";
            clone.CustomDictionaryOverrides["system/fvSolution"]["nNonOrthogonalCorrectors"] = "5";

            Assert.Equal(1f, opts.InletDirection[0]);
            Assert.False(opts.ResultMetrics.ContainsKey("Lift"));
            Assert.Equal("2", opts.CustomDictionaryOverrides["system/fvSolution"]["nNonOrthogonalCorrectors"]);
        }

        // ------------------------------------------------------------------
        // 2. MPI Isolation & Process Runner
        // ------------------------------------------------------------------

        [Fact]
        public void OpenFoamProcessRunner_Strips_Conda_And_Mpich_And_Builds_Isolated_Script()
        {
            string dirtyPath = "/home/user/miniconda/bin:/home/user/miniconda/envs/mpich/bin:/usr/local/bin:/opt/anaconda3/bin:/usr/bin";
            string cleaned = OpenFoamProcessRunner.StripCondaMpiFromPath(dirtyPath);

            Assert.Equal("/usr/local/bin:/usr/bin", cleaned);

            var options = new OpenFoamOptions
            {
                OpenFoamBashrcPath = "/usr/lib/openfoam/openfoam2506/etc/bashrc",
                MpiRunPath = "/usr/bin/mpirun"
            };

            string parallelCmd = OpenFoamProcessRunner.BuildParallelToolCommand("simpleFoam", null, options, 8);
            Assert.Equal("\"/usr/bin/mpirun\" -np 8 simpleFoam -parallel", parallelCmd);

            string serialCmd = OpenFoamProcessRunner.BuildParallelToolCommand("simpleFoam", null, options, 1);
            Assert.Equal("simpleFoam", serialCmd);

            // Conda MPICH path configured by mistake is rejected in favor of /usr/bin/mpirun
            var badMpiOpts = new OpenFoamOptions { MpiRunPath = "/home/u/miniconda/envs/mpich/bin/mpiexec" };
            Assert.Equal("/usr/bin/mpirun", OpenFoamProcessRunner.ResolveMpiRunPath(badMpiOpts));

            string script = OpenFoamProcessRunner.BuildIsolatedBashScript(parallelCmd, options, 8);
            Assert.Contains("grep -viE '(miniconda|anaconda|conda|mpich)'", script);
            Assert.Contains("export PATH=\"/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH\"", script);
            Assert.Contains("set +u", script);
            Assert.Contains("source \"/usr/lib/openfoam/openfoam2506/etc/bashrc\"", script);
            Assert.Contains("set -u", script);
            Assert.Contains("export HWLOC_HIDE_ERRORS=1", script);
            Assert.Contains("export OMPI_MCA_btl_vader_single_copy_mechanism=none", script);
            Assert.Contains("\"/usr/bin/mpirun\" -np 8 simpleFoam -parallel", script);
        }

        [Fact]
        public void ResolveEffectiveBashrcPath_Follows_Priority_Order_And_Detects_Version()
        {
            var opts = new OpenFoamOptions();

            string primary = OpenFoamProcessRunner.ResolveEffectiveBashrcPath(
                opts,
                homeOverride: "/home/tester",
                fileExists: p => p == "/usr/lib/openfoam/openfoam2506/etc/bashrc");
            Assert.Equal("/usr/lib/openfoam/openfoam2506/etc/bashrc", primary);

            string wildcard = OpenFoamProcessRunner.ResolveEffectiveBashrcPath(
                opts,
                homeOverride: "/home/tester",
                fileExists: p => p == "/usr/lib/openfoam/openfoam2412/etc/bashrc",
                directoryEnumerator: _ => new[] { "/usr/lib/openfoam/openfoam2412" });
            Assert.Equal("/usr/lib/openfoam/openfoam2412/etc/bashrc", wildcard);

            string conda = OpenFoamProcessRunner.ResolveEffectiveBashrcPath(
                opts,
                homeOverride: "/home/tester",
                fileExists: p => p == "/home/tester/miniconda/envs/openfoam/etc/bashrc",
                directoryEnumerator: _ => Array.Empty<string>());
            Assert.Equal("/home/tester/miniconda/envs/openfoam/etc/bashrc", conda);

            var runnerMock = new Mock<IOpenFoamCommandRunner>();
            runnerMock.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), 1))
                .Returns(new OpenFoamCommandResult(0, "v2506\n", ""));
            Assert.Equal("v2506", OpenFoamProcessRunner.DetectOpenFoamVersion(runnerMock.Object, opts, Root));
        }

        // ------------------------------------------------------------------
        // 3. Case Generation (Internal3D, External3D, Scaling, Overrides)
        // ------------------------------------------------------------------

        [Fact]
        public void CaseGenerator_Internal3D_Scales_To_Meters_Computes_Inlet_Normal_And_Writes_All_Dictionaries()
        {
            string geomDir = Sub("geom_internal");
            // ScaleFactor = 0.5 -> geometry coordinates are half physical mm; pipe is 100 mm physical -> 0.1 m
            GeometryResult geo = WriteMultiPatchPipeGeometry(geomDir, scaleFactor: 0.5f, pipeLengthMm: 100f, halfSizeMm: 10f);

            string caseDir = Sub("case_internal");
            var request = new MeshRequest(geo.StlPath, 1, 1, SimulationConfig.CreateDefault(), 0.5f, Root, Geometry: geo);
            var options = new OpenFoamOptions
            {
                DomainMode = "Internal3D",
                InletCondition = "Velocity",
                InletVelocity = 2.5f,
                TurbulenceModel = "kOmegaSST",
                MesherBackend = "CfMesh"
            };

            OpenFoamCaseInfo info = OpenFoamCaseGenerator.Generate(caseDir, request, options);

            // Seed point (25, 0, 0) in 0.5x PicoGK mm -> (50, 0, 0) real mm -> (0.05, 0, 0) m
            Assert.Equal(0.05f, info.LocationInMeshMeters.X, 4);
            Assert.Equal(0f, info.LocationInMeshMeters.Y, 4);
            Assert.Equal(0f, info.LocationInMeshMeters.Z, 4);

            // Inlet outward normal is -X, so inward flow direction is +X
            Assert.True(info.InletDirection.X > 0.99f);
            Assert.InRange(info.InletAreaM2, 0.00039f, 0.00041f); // 20mm x 20mm = 400 mm^2 = 0.0004 m^2

            // Multi-solid ASCII STL exists with all 3 patches
            IReadOnlyList<string> solids = StlWriter.ReadAsciiSolidNames(info.TriSurfaceStlPath);
            Assert.Contains("inlet", solids, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("outlet", solids, StringComparer.OrdinalIgnoreCase);
            Assert.Contains("wall", solids, StringComparer.OrdinalIgnoreCase);

            // 0/ fields & FunctionObjects & cfMesh meshDict
            string uText = File.ReadAllText(Path.Combine(caseDir, "0", "U"));
            Assert.Contains("uniform (2.5 0 0)", uText);
            Assert.True(File.Exists(Path.Combine(caseDir, "0", "p")));
            Assert.True(File.Exists(Path.Combine(caseDir, "0", "k")));
            Assert.True(File.Exists(Path.Combine(caseDir, "0", "omega")));
            Assert.True(File.Exists(Path.Combine(caseDir, "0", "nut")));

            string controlDict = File.ReadAllText(Path.Combine(caseDir, "system", "controlDict"));
            Assert.Contains("pressure_inlet", controlDict);
            Assert.Contains("pressure_outlet", controlDict);
            Assert.Contains("flow_inlet", controlDict);
            Assert.Contains("flow_outlet", controlDict);
            Assert.Contains("yPlus", controlDict);

            string snappyDict = File.ReadAllText(Path.Combine(caseDir, "system", "snappyHexMeshDict"));
            Assert.Contains("locationInMesh (0.05 0 0);", snappyDict);

            string meshDict = File.ReadAllText(Path.Combine(caseDir, "system", "meshDict"));
            Assert.Contains("surfaceFile     \"constant/triSurface/geometry.stl\";", meshDict);
            Assert.Contains("patchBoundaryLayers", meshDict);
        }

        [Fact]
        public void CaseGenerator_Supports_FlowRate_Inlet_CustomOverrides_And_External3D_Symmetry()
        {
            string geomDir = Sub("geom_external");
            IReadOnlyList<StlTriangle> bodyTris = CreateUnitBoxTriangles(new Vector3(0f, -10f, -5f), new Vector3(40f, 10f, 5f));
            string stlPath = Path.Combine(geomDir, "body.stl");
            StlWriter.WriteBinary(stlPath, bodyTris);

            string customTplDir = Sub("custom_tpl");
            Directory.CreateDirectory(Path.Combine(customTplDir, "system"));
            File.WriteAllText(Path.Combine(customTplDir, "system", "fvSchemes"), "// CUSTOM TEMPLATE\nddtSchemes { default steadyState; }\n");

            string caseExt = Sub("case_external");
            var reqExt = new MeshRequest(stlPath, 1, 1, SimulationConfig.CreateDefault(), 1.0f, Root);
            var optsExt = new OpenFoamOptions
            {
                DomainMode = "External3D",
                SymmetryPlane = "Y0",
                InletCondition = "VolumetricFlowRate",
                InletVolumetricFlowRate = 0.002f,
                TurbulenceModel = "laminar",
                CustomTemplateDirectory = customTplDir,
                CustomDictionaryOverrides = new Dictionary<string, Dictionary<string, string>>
                {
                    ["system/controlDict"] = new() { ["endTime"] = "777" }
                }
            };

            OpenFoamCaseGenerator.Generate(caseExt, reqExt, optsExt);

            string blockMesh = File.ReadAllText(Path.Combine(caseExt, "system", "blockMeshDict"));
            Assert.Contains("symmetry", blockMesh);
            Assert.Contains("type symmetry;", blockMesh);

            string controlDict = File.ReadAllText(Path.Combine(caseExt, "system", "controlDict"));
            Assert.Contains("forceCoeffs", controlDict);
            Assert.Contains("endTime 777;", controlDict);

            string uField = File.ReadAllText(Path.Combine(caseExt, "0", "U"));
            Assert.Contains("flowRateInletVelocity", uField);
            Assert.Contains("volumetricFlowRate constant 0.002;", uField);

            Assert.False(File.Exists(Path.Combine(caseExt, "0", "k")));
            Assert.Contains("// CUSTOM TEMPLATE", File.ReadAllText(Path.Combine(caseExt, "system", "fvSchemes")));
        }

        // ------------------------------------------------------------------
        // 4. Mesh Quality Parser & Hard Gates
        // ------------------------------------------------------------------

        [Fact]
        public void MeshQualityParser_Enforces_CheckMesh_NonOrtho_Skewness_And_LayerCollapse_Gates()
        {
            string goodCheckMesh = @"
Mesh stats
    cells:            125000
    hexahedra:        100000
    prisms:           20000
    tetrahedra:       1000
    polyhedra:        4000
Checking geometry...
    Mesh non-orthogonality Max: 48.2 average: 11.4
    Max skewness = 2.15 OK.
    Max aspect ratio = 12.4 OK.
Mesh OK.
";
            string goodSnappy = @"
patch           faces    layers   overall thickness [m]   [%]
wall            5000     5.0      0.0012                  92.5
";
            var opts = new OpenFoamOptions { MaxNonOrthogonality = 65f, MaxSkewness = 4f, MinLayerCoveragePercent = 50f };

            OpenFoamMeshQualityReport okReport = OpenFoamMeshQualityParser.Parse(goodCheckMesh, goodSnappy, "wall");
            Assert.Equal(125000, okReport.Cells);
            Assert.True(okReport.CheckMeshPassed);
            Assert.Equal(92.5f, okReport.LayerCoveragePercent, 2);
            Assert.True(OpenFoamMeshQualityParser.EvaluateGates(okReport, opts).Passed);

            // CheckMesh failure
            string failedChecks = goodCheckMesh.Replace("Mesh OK.", "Failed 2 mesh checks.");
            var failedReport = OpenFoamMeshQualityParser.Parse(failedChecks, goodSnappy, "wall");
            Assert.False(OpenFoamMeshQualityParser.EvaluateGates(failedReport, opts).Passed);

            // Non-orthogonality violation
            string badOrtho = goodCheckMesh.Replace("Max: 48.2", "Max: 74.5");
            var orthoReport = OpenFoamMeshQualityParser.Parse(badOrtho, goodSnappy, "wall");
            Assert.False(OpenFoamMeshQualityParser.EvaluateGates(orthoReport, opts).Passed);

            // Skewness violation
            string badSkew = goodCheckMesh.Replace("Max skewness = 2.15", "Max skewness = 5.20");
            var skewReport = OpenFoamMeshQualityParser.Parse(badSkew, goodSnappy, "wall");
            Assert.False(OpenFoamMeshQualityParser.EvaluateGates(skewReport, opts).Passed);

            // Layer collapse violation
            string collapsedLayers = goodSnappy.Replace("92.5", "18.0");
            var collapseReport = OpenFoamMeshQualityParser.Parse(goodCheckMesh, collapsedLayers, "wall");
            var collapseGate = OpenFoamMeshQualityParser.EvaluateGates(collapseReport, opts);
            Assert.False(collapseGate.Passed);
            Assert.Contains(collapseGate.Violations, v => v.Contains("layer collapse", StringComparison.OrdinalIgnoreCase));

            // ESI openfoam2506 6-column layer summary table (includes target column)
            string esi2506Snappy = @"
patch faces        layers        overall thickness
              target   mesh     [m]       [%]
----- -----    -----    ----     ---       ---
wall 3075     2        1.98     0.00111   97.3    
";
            var esiReport = OpenFoamMeshQualityParser.Parse(goodCheckMesh, esi2506Snappy, "wall");
            Assert.Equal(97.3f, esiReport.LayerCoveragePercent, 2);
            Assert.Equal(0.00111f, esiReport.AvgLayerThicknessM, 5);
        }

        // ------------------------------------------------------------------
        // 5. Post-Processing & Convergence Judge (incl. MassBalanceError > 1% gate)
        // ------------------------------------------------------------------

        [Fact]
        public void PostProcessor_Parses_Vector_Field_MultiOutlet_Imbalance_YPlus_And_ForceCoeffs()
        {
            string caseDir = Sub("post_multi_outlet");
            WriteSurfaceDat(caseDir, "pressure_inlet", "# Time areaAverage(p) areaAverage(U)\n500 0.25 (2.0 0.0 0.0)\n1000 0.25 (2.0 0.0 0.0)\n");
            WriteSurfaceDat(caseDir, "pressure_outlet1", "# Time areaAverage(p) areaAverage(U)\n500 0.0 (1.2 0.0 0.0)\n1000 0.0 (1.2 0.0 0.0)\n");
            WriteSurfaceDat(caseDir, "pressure_outlet2", "# Time areaAverage(p) areaAverage(U)\n500 0.0 (0.8 0.0 0.0)\n1000 0.0 (0.8 0.0 0.0)\n");

            WriteSurfaceDat(caseDir, "flow_inlet", "# Time sum(phi)\n500 -1.0\n1000 -1.0\n");
            WriteSurfaceDat(caseDir, "flow_outlet1", "# Time sum(phi)\n500 0.6\n1000 0.6\n");
            WriteSurfaceDat(caseDir, "flow_outlet2", "# Time sum(phi)\n500 0.4\n1000 0.4\n");

            string yPlusDir = Path.Combine(caseDir, "postProcessing", "yPlus", "0");
            Directory.CreateDirectory(yPlusDir);
            File.WriteAllText(Path.Combine(yPlusDir, "yPlus.dat"), "# Time patch min max average\n1000 wall 0.5 12.0 4.0\n");

            string forceDir = Path.Combine(caseDir, "postProcessing", "forceCoeffs", "0");
            Directory.CreateDirectory(forceDir);
            File.WriteAllText(Path.Combine(forceDir, "coefficient.dat"), "# Time Cd Cl\n1000 0.123 0.456\n");

            var opts = new OpenFoamOptions { Density = 1000f };
            OpenFoamPostProcessResult res = OpenFoamPostProcessor.Evaluate(caseDir, opts);

            // p_in_tot = 1000 * (0.25 + 0.5 * 4) = 2250 Pa
            // p_out1_tot = 1000 * (0 + 0.5 * 1.44) = 720 Pa (w = 0.6)
            // p_out2_tot = 1000 * (0 + 0.5 * 0.64) = 320 Pa (w = 0.4)
            // weighted p_out_tot = 0.6*720 + 0.4*320 = 432 + 128 = 560 Pa -> dp = 1690 Pa
            Assert.Equal(1690f, res.Metrics["PressureDrop"], 1);
            Assert.Equal(250f, res.Metrics["PressureDropStatic"], 1);
            Assert.Equal(0f, res.Metrics["MassBalanceError"], 3);
            Assert.Equal(40f, res.Metrics["FlowImbalance"], 2); // (0.6 - 0.4) / 0.5 * 100 = 40%
            Assert.Equal(4.0f, res.YPlusMed!.Value, 3);
            Assert.Equal(12.0f, res.YPlusMax!.Value, 3);
            Assert.Equal(9.2f, res.YPlusP95!.Value, 2); // min(12, 4 + 0.65 * 8) = 9.2
            Assert.Equal(0.123f, res.Metrics["Drag"], 4);
            Assert.Equal(0.456f, res.Metrics["Lift"], 4);
        }

        [Fact]
        public void ConvergenceJudge_And_Solver_Reject_MassBalanceError_Over_One_Percent()
        {
            string caseDir = Sub("bad_mass_balance");
            WriteSurfaceDat(caseDir, "pressure_inlet", "# Time areaAverage(p) areaAverage(U)\n100 0.2 (1.0 0 0)\n");
            WriteSurfaceDat(caseDir, "pressure_outlet", "# Time areaAverage(p) areaAverage(U)\n100 0.0 (1.0 0 0)\n");
            WriteSurfaceDat(caseDir, "flow_inlet", "# Time sum(phi)\n100 -1.0\n");
            WriteSurfaceDat(caseDir, "flow_outlet", "# Time sum(phi)\n100 0.95\n"); // 5% error > 1%

            var opts = new OpenFoamOptions { MaxMassBalanceErrorPercent = 1.0f };
            OpenFoamPostProcessResult post = OpenFoamPostProcessor.Evaluate(caseDir, opts);
            Assert.Equal(5.0f, post.MassBalanceErrorPercent!.Value, 2);

            var verdict = OpenFoamConvergenceJudge.Judge("Time = 100\nSIMPLE solution converged in 100 iterations\n", post, opts);
            Assert.False(verdict.MassBalanceOk);
            Assert.False(verdict.Converged);

            // Standard ESI OpenFOAM banner ("trapFpe: Floating point exception trapping enabled (FOAM_SIGFPE).")
            // must NOT trigger DivergedOrNaN.
            string bannerLog = "trapFpe: Floating point exception trapping enabled (FOAM_SIGFPE).\nTime = 100\nSIMPLE solution converged in 100 iterations\n";
            var bannerVerdict = OpenFoamConvergenceJudge.Judge(bannerLog, post, opts);
            Assert.False(bannerVerdict.DivergedOrNaN);

            // Early startup transient in a 100-iteration converged run must not contaminate
            // PressureDrop or trigger false trailing-window drift when TrailingWindowIterations=100.
            string fastCase = Sub("fast_converge");
            var pInLines = new List<string> { "# Time areaAverage(p) areaAverage(U)" };
            var pOutLines = new List<string> { "# Time areaAverage(p) areaAverage(U)" };
            var fInLines = new List<string> { "# Time sum(phi)" };
            var fOutLines = new List<string> { "# Time sum(phi)" };
            for (int t = 1; t <= 100; t++)
            {
                double pIn = t <= 5 ? 10.0 / t : 0.14;
                pInLines.Add(string.Create(CultureInfo.InvariantCulture, $"{t} {pIn} (1.0 0 0)"));
                pOutLines.Add(string.Create(CultureInfo.InvariantCulture, $"{t} 0.0 (1.0 0 0)"));
                fInLines.Add(string.Create(CultureInfo.InvariantCulture, $"{t} -1.0"));
                fOutLines.Add(string.Create(CultureInfo.InvariantCulture, $"{t} 1.0"));
            }
            WriteSurfaceDat(fastCase, "pressure_inlet", string.Join("\n", pInLines));
            WriteSurfaceDat(fastCase, "pressure_outlet", string.Join("\n", pOutLines));
            WriteSurfaceDat(fastCase, "flow_inlet", string.Join("\n", fInLines));
            WriteSurfaceDat(fastCase, "flow_outlet", string.Join("\n", fOutLines));

            var fastOpts = new OpenFoamOptions { Density = 1000f, TrailingWindowIterations = 100 };
            OpenFoamPostProcessResult fastPost = OpenFoamPostProcessor.Evaluate(fastCase, fastOpts);
            Assert.InRange(fastPost.Metrics["PressureDrop"], 139f, 141f);
            var fastVerdict = OpenFoamConvergenceJudge.Judge(
                "Time = 100\nSIMPLE solution converged in 100 iterations\n",
                fastPost,
                fastOpts);
            Assert.True(fastVerdict.Converged, fastVerdict.Summary);

            var runnerMock = new Mock<IOpenFoamCommandRunner>();
            runnerMock.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns(new OpenFoamCommandResult(0, "Time = 100\nSIMPLE solution converged in 100 iterations\n", ""));

            var solver = new OpenFoamSimpleSolver(opts, runnerMock.Object);
            var record = new ModelRecord();
            solver.Solve(caseDir, record, new SimulationConfig { MpiCores = 1 }, caseDir);

            Assert.True(record.SimulationFailed);
            Assert.False(record.Feasible);
            Assert.Equal(EvaluationFailureStage.Solver, record.FailedStage);
        }

        // ------------------------------------------------------------------
        // 6. Mocked Mesher + Solver End-to-End Test (Parallel + Fallback)
        // ------------------------------------------------------------------

        [Fact]
        public void Mesher_And_Solver_Execute_Parallel_Pipeline_End_To_End_With_Mocked_Runner()
        {
            string geomDir = Sub("e2e_geom");
            string workDir = Sub("e2e_work");
            Directory.CreateDirectory(workDir);
            GeometryResult geo = WriteMultiPatchPipeGeometry(geomDir);

            var executedCommands = new List<string>();
            var runnerMock = new Mock<IOpenFoamCommandRunner>();
            runnerMock.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns((string dir, string cmd, OpenFoamOptions _, int _) =>
                {
                    executedCommands.Add(cmd);

                    if (cmd.StartsWith("printf", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "v2506", "");

                    if (cmd.StartsWith("checkMesh", StringComparison.Ordinal))
                    {
                        return new OpenFoamCommandResult(0,
                            "cells: 42000\nhexahedra: 35000\nprisms: 7000\ntetrahedra: 0\npolyhedra: 0\n"
                            + "Mesh non-orthogonality Max: 38.0 average: 9.0\nMax skewness = 1.4\nMesh OK.\n",
                            "");
                    }

                    if (cmd.Contains("snappyHexMesh", StringComparison.Ordinal))
                    {
                        return new OpenFoamCommandResult(0, "wall 1200 5.0 0.001 95.0\n", "");
                    }

                    if (cmd.Contains("simpleFoam", StringComparison.Ordinal))
                    {
                        WriteSurfaceDat(dir, "pressure_inlet",
                            "# Time areaAverage(p) areaAverage(U)\n200 0.15 (1.5 0 0)\n400 0.15 (1.5 0 0)\n600 0.15 (1.5 0 0)\n800 0.15 (1.5 0 0)\n");
                        WriteSurfaceDat(dir, "pressure_outlet",
                            "# Time areaAverage(p) areaAverage(U)\n200 0.0 (1.5 0 0)\n400 0.0 (1.5 0 0)\n600 0.0 (1.5 0 0)\n800 0.0 (1.5 0 0)\n");
                        WriteSurfaceDat(dir, "flow_inlet",
                            "# Time sum(phi)\n200 -0.0006\n400 -0.0006\n600 -0.0006\n800 -0.0006\n");
                        WriteSurfaceDat(dir, "flow_outlet",
                            "# Time sum(phi)\n200 0.0006\n400 0.0006\n600 0.0006\n800 0.0006\n");
                        return new OpenFoamCommandResult(0,
                            "Time = 800\nSolving for p, Initial residual = 0.01, Final residual = 5e-5\nSIMPLE solution converged in 800 iterations\n",
                            "");
                    }

                    return new OpenFoamCommandResult(0, "", "");
                });

            var options = new OpenFoamOptions { Density = 1000f, ExportVtk = true };
            var simConfig = new SimulationConfig { MpiCores = 4 };
            var mesher = new OpenFoamSnappyMesher(options, runnerMock.Object);
            var solver = new OpenFoamSimpleSolver(options, runnerMock.Object);

            string caseDir = mesher.GenerateMesh(new MeshRequest(geo.StlPath, 1, 1, simConfig, 1.0f, workDir, Geometry: geo));
            Assert.True(File.Exists(Path.Combine(caseDir, "case.foam")));
            Assert.Equal(42000f, mesher.LastMeshMetrics["MeshCells"]);
            Assert.Equal(95f, mesher.LastMeshMetrics["BoundaryLayerCoveragePct"]);

            var record = new ModelRecord();
            foreach (var kvp in mesher.LastMeshMetrics)
                record.PassiveParameters[kvp.Key] = kvp.Value;

            solver.Solve(caseDir, record, simConfig, workDir);

            Assert.False(record.SimulationFailed);
            Assert.True(record.Feasible);
            Assert.Equal(EvaluationFailureStage.None, record.FailedStage);
            Assert.Equal(caseDir, record.Artifacts["OpenFoamCase"]);
            Assert.Equal(150f, record.PassiveParameters["PressureDrop"], 1);
            Assert.Equal(0f, record.PassiveParameters["MassBalanceError"], 3);
            Assert.Equal(800f, record.PassiveParameters["OpenFoam_Iterations"]);
            Assert.Equal(1f, record.PassiveParameters["OpenFoam_Converged"]);
            Assert.Contains(executedCommands, c => c.Contains("snappyHexMesh -overwrite -parallel"));
            Assert.Contains(executedCommands, c => c.Contains("simpleFoam -parallel"));
            Assert.Contains(executedCommands, c => c.Contains("reconstructPar"));
            Assert.Contains(executedCommands, c => c.Contains("foamToVTK"));
        }

        [Fact]
        public void Mesher_Falls_Back_To_CfMesh_When_SnappyHexMesh_Fails()
        {
            string geomDir = Sub("fb_geom");
            string workDir = Sub("fb_work");
            Directory.CreateDirectory(workDir);
            GeometryResult geo = WriteMultiPatchPipeGeometry(geomDir);

            var runnerMock = new Mock<IOpenFoamCommandRunner>();
            runnerMock.Setup(r => r.Run(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<OpenFoamOptions>(), It.IsAny<int>()))
                .Returns((string _, string cmd, OpenFoamOptions _, int _) =>
                {
                    if (cmd.Contains("snappyHexMesh", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(1, "FOAM FATAL ERROR: snappy failed", "");
                    if (cmd.StartsWith("checkMesh", StringComparison.Ordinal))
                        return new OpenFoamCommandResult(0, "cells: 15000\nMesh OK.\n", "");
                    return new OpenFoamCommandResult(0, "", "");
                });

            var options = new OpenFoamOptions { FallbackToCfMesh = true, AddLayers = false };
            var mesher = new OpenFoamSnappyMesher(options, runnerMock.Object);
            string caseDir = mesher.GenerateMesh(new MeshRequest(geo.StlPath, 1, 1, new SimulationConfig { MpiCores = 1 }, 1.0f, workDir, Geometry: geo));

            Assert.True(Directory.Exists(caseDir));
            Assert.Equal(1f, mesher.LastMeshMetrics[ModelRecord.MeshFallbackKey]);
            Assert.Equal(15000f, mesher.LastMeshMetrics["MeshCells"]);
        }

        // ------------------------------------------------------------------
        // 7. PipeBend90 Config, Constraints & Fitness
        // ------------------------------------------------------------------

        [Fact]
        public void PipeBend90_Config_Constraints_And_Fitness_Work_Correctly()
        {
            var projectDef = new PipeBend90Project();
            Assert.Equal("PipeBend90", projectDef.Name);

            ProjectContext ctx = OptimizeCommand.LoadConfiguration(projectDef, null);
            Assert.Equal("Internal3D:StandardRans", ctx.Simulation.Preset);
            SolverStage[] stages = projectDef.CreateStages(ctx);
            Assert.Single(stages);
            Assert.IsType<OpenFoamSnappyMesher>(stages[0].Mesher);
            Assert.IsType<OpenFoamSimpleSolver>(stages[0].Solver);

            // Key count <= 15 check for PipeBend90 JSONs
            string projDir = ProjectPaths.ResolveProjectDirectory("PipeBend90")!;
            int totalKeys = new[] { "project.json", "simulation.json", "solvers/openfoam.json" }
                .Sum(rel =>
                {
                    var node = System.Text.Json.Nodes.JsonNode.Parse(
                        File.ReadAllText(Path.Combine(projDir, rel)),
                        documentOptions: new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip })!.AsObject();
                    return node.Count(k => !k.Key.Equals("$schema", StringComparison.OrdinalIgnoreCase));
                });
            Assert.True(totalKeys <= 15, $"PipeBend90 has {totalKeys} keys (expected <= 15).");

            // Pre-CFD constraints
            var constraints = new PipeBend90GeometryConstraints();
            var okRecord = new ModelRecord
            {
                ActiveParameters = new Dictionary<string, float> { ["PipeRadiusMm"] = 12f, ["BendRadiusMm"] = 16f },
                PassiveParameters = new Dictionary<string, float> { ["Volume"] = 60000f }
            };
            Assert.All(constraints.EvaluateGeometryConstraints(okRecord, ctx.Project), c => Assert.True(c.IsSatisfied));

            var badBendRecord = new ModelRecord
            {
                ActiveParameters = new Dictionary<string, float> { ["PipeRadiusMm"] = 12f, ["BendRadiusMm"] = 14f }, // 14 < 1.25 * 12 = 15
                PassiveParameters = new Dictionary<string, float> { ["Volume"] = 60000f }
            };
            var badResults = constraints.EvaluateGeometryConstraints(badBendRecord, ctx.Project);
            Assert.Contains(badResults, c => c.Name == "BendRadiusToPipeRadius" && !c.IsSatisfied);

            // Fitness minimization
            var fitness = new PipeBend90FitnessCalculator(ctx.Project);
            var lowDp = new ModelRecord { PassiveParameters = { ["PressureDrop"] = 45f } };
            var highDp = new ModelRecord { PassiveParameters = { ["PressureDrop"] = 120f } };
            fitness.CalculateFitness(lowDp, ctx.Simulation);
            fitness.CalculateFitness(highDp, ctx.Simulation);
            Assert.True(lowDp.Fitness > highDp.Fitness);

            var failedRec = new ModelRecord { SimulationFailed = true };
            fitness.CalculateFitness(failedRec, ctx.Simulation);
            Assert.Equal(ObjectiveEvaluatorBase.DefaultFailedFitness, failedRec.Fitness);
        }

        private static void WriteSurfaceDat(string caseDir, string funcName, string content)
        {
            string dir = Path.Combine(caseDir, "postProcessing", funcName, "0");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "surfaceFieldValue.dat"), content);
        }
    }
}
