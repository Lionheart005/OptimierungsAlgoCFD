using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text;
using System.Text.RegularExpressions;
using Moq;
using FluidForge.Core;
using FluidForge.Kernels.Planar2D;
using FluidForge.Solvers.Cfd;
using Xunit;

namespace FluidForge.Tests
{
    public class ArchitectureBoundaryTests : IDisposable
    {
        private readonly string _tempRoot;

        public ArchitectureBoundaryTests()
        {
            _tempRoot = Path.Combine(Path.GetTempPath(), "arch_boundary_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_tempRoot);
        }

        public void Dispose()
        {
            if (Directory.Exists(_tempRoot))
            {
                try { Directory.Delete(_tempRoot, recursive: true); } catch { }
            }
        }

        private static string RepoRoot
        {
            get
            {
                string? projectsRoot = ProjectPaths.ResolveProjectsRoot();
                Assert.True(projectsRoot != null, "Directory src/projects/ was not found.");
                return Path.GetDirectoryName(Path.GetDirectoryName(projectsRoot)!)!;
            }
        }

        private static IEnumerable<string> EnumerateCsFiles(string rootDir)
        {
            foreach (string file in Directory.EnumerateFiles(rootDir, "*.cs", SearchOption.AllDirectories))
            {
                string normalized = file.Replace('\\', '/');
                if (normalized.Contains("/bin/") || normalized.Contains("/obj/"))
                    continue;
                yield return file;
            }
        }

        /// <summary>
        /// Strips C# comments (// and /* ... */) while respecting string literals
        /// so that actual type and using references are reliably detected.
        /// </summary>
        private static string StripComments(string source)
        {
            var sb = new StringBuilder(source.Length);
            bool inLineComment = false;
            bool inBlockComment = false;
            bool inString = false;
            bool inVerbatimString = false;
            bool inChar = false;

            for (int i = 0; i < source.Length; i++)
            {
                char c = source[i];
                char next = i + 1 < source.Length ? source[i + 1] : '\0';

                if (inLineComment)
                {
                    if (c == '\r' || c == '\n')
                    {
                        inLineComment = false;
                        sb.Append(c);
                    }
                    continue;
                }

                if (inBlockComment)
                {
                    if (c == '*' && next == '/')
                    {
                        inBlockComment = false;
                        i++;
                    }
                    else if (c == '\r' || c == '\n')
                    {
                        sb.Append(c);
                    }
                    continue;
                }

                if (inVerbatimString)
                {
                    sb.Append(c);
                    if (c == '"')
                    {
                        if (next == '"')
                        {
                            sb.Append(next);
                            i++;
                        }
                        else
                        {
                            inVerbatimString = false;
                        }
                    }
                    continue;
                }

                if (inString)
                {
                    sb.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        sb.Append(next);
                        i++;
                    }
                    else if (c == '"')
                    {
                        inString = false;
                    }
                    continue;
                }

                if (inChar)
                {
                    sb.Append(c);
                    if (c == '\\' && next != '\0')
                    {
                        sb.Append(next);
                        i++;
                    }
                    else if (c == '\'')
                    {
                        inChar = false;
                    }
                    continue;
                }

                if (c == '/' && next == '/')
                {
                    inLineComment = true;
                    i++;
                    continue;
                }

                if (c == '/' && next == '*')
                {
                    inBlockComment = true;
                    i++;
                    continue;
                }

                if (c == '@' && next == '"')
                {
                    inVerbatimString = true;
                    sb.Append(c).Append(next);
                    i++;
                    continue;
                }

                if (c == '"')
                {
                    inString = true;
                    sb.Append(c);
                    continue;
                }

                if (c == '\'')
                {
                    inChar = true;
                    sb.Append(c);
                    continue;
                }

                sb.Append(c);
            }

            return sb.ToString();
        }

        [Fact]
        public void Core_Has_No_Dependency_On_Outer_Layers()
        {
            string coreDir = Path.Combine(RepoRoot, "src", "FluidForge", "Core");
            Assert.True(Directory.Exists(coreDir), $"Core directory not found: {coreDir}");

            var forbiddenPatterns = new[]
            {
                new Regex(@"\busing\s+(?:static\s+)?(?:FluidForge\.(?:Solvers|Kernels|Composition|Projects)|PicoGK)\b"),
                new Regex(@"\bFluidForge\.(?:Solvers|Kernels|Composition|Projects)\."),
                new Regex(@"\bPicoGK\.")
            };

            List<string> files = EnumerateCsFiles(coreDir).ToList();
            Assert.True(files.Count >= 30, $"Too few Core files found ({files.Count}).");

            var violations = new List<string>();
            foreach (string file in files)
            {
                string code = StripComments(File.ReadAllText(file));
                string[] lines = code.Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    foreach (Regex pattern in forbiddenPatterns)
                    {
                        if (pattern.IsMatch(lines[i]))
                        {
                            violations.Add($"{Path.GetRelativePath(RepoRoot, file)}:{i + 1}: {lines[i].Trim()}");
                        }
                    }
                }
            }

            Assert.True(violations.Count == 0,
                "Core/ must not depend on Solvers, Kernels, Composition, Projects, or PicoGK:\n  "
                + string.Join("\n  ", violations));
        }

        [Fact]
        public void Kernels_And_Solvers_Declare_Matching_Namespaces()
        {
            string kernelsDir = Path.Combine(RepoRoot, "src", "FluidForge", "Kernels");
            string solversDir = Path.Combine(RepoRoot, "src", "FluidForge", "Solvers");
            Assert.True(Directory.Exists(kernelsDir));
            Assert.True(Directory.Exists(solversDir));

            var coreNamespaceRegex = new Regex(@"\bnamespace\s+FluidForge\.Core\b");
            var files = EnumerateCsFiles(kernelsDir).Concat(EnumerateCsFiles(solversDir)).ToList();
            Assert.NotEmpty(files);

            var violations = new List<string>();
            foreach (string file in files)
            {
                string code = StripComments(File.ReadAllText(file));
                if (coreNamespaceRegex.IsMatch(code))
                {
                    violations.Add(Path.GetRelativePath(RepoRoot, file));
                }
            }

            Assert.True(violations.Count == 0,
                "Files under Kernels/ or Solvers/ must not declare namespace FluidForge.Core:\n  "
                + string.Join("\n  ", violations));
        }

        private sealed class PureCoreResolutionTargetMesher : IMeshGenerator, IMeshResolutionTarget
        {
            public PureCoreResolutionTargetMesher(float targetSurfaceCellSizeMm)
            {
                TargetSurfaceCellSizeMm = targetSurfaceCellSizeMm;
            }

            public float TargetSurfaceCellSizeMm { get; }

            public string GenerateMesh(MeshRequest request) => "dummy.su2";

            public string GenerateMesh(
                string modelStlPath, int iteration, int variant, SimulationConfig config, float scaleFactor, string workingDirectory)
                => "dummy.su2";
        }

        [Fact]
        public void VoxelResolutionAdvisor_Works_With_Pure_Core_IMeshResolutionTarget()
        {
            var sim = new SimulationConfig
            {
                BaseVoxelResolution = 0.5f,
                TargetPicoGkSize = 50.0f,
                UseRubberBandScaler = true,
                AutoVoxelResolution = true
            };
            var project = new ProjectConfig
            {
                ProjectName = "PureCoreAdvisorTest",
                BaseParameters = new Dictionary<string, float> { ["Length"] = 500f },
                DimensionalParameters = new HashSet<string> { "Length" }
            };
            var stages = new[]
            {
                new SolverStage(new PureCoreResolutionTargetMesher(2.0f), new Mock<ISimulationSolver>().Object)
            };

            using var writer = new StringWriter();
            float effective = VoxelResolutionAdvisor.Resolve(sim, project, stages, writer);

            Assert.Equal(0.15f, effective, 4);
            Assert.Contains("[INFO] AutoVoxelResolution:", writer.ToString());
        }

        [Fact]
        public void Gmsh2DMesher_Implements_IMeshMetricsSource_And_Populates_CellMetrics()
        {
            string contourPath = Path.Combine(_tempRoot, "airfoil.json");
            var shape = new Shape2D().AddPolygon(new List<Vector2>
            {
                new(50f, 0f),
                new(25f, -3f),
                new(0f, 0f),
                new(25f, 4f)
            });
            shape.SaveContour(contourPath, cornerAngleDeg: 35.0f);

            string su2Content = string.Join("\n", new[]
            {
                "NDIME= 2",
                "NELEM= 3",
                "5 0 1 2 0",
                "9 0 1 2 3 1",
                "5 1 2 3 2",
                "NPOIN= 4",
                "0 0 0",
                "1 0 1",
                "1 1 2",
                "0 1 3",
                "NMARK= 3",
                "MARKER_TAG= Farfield",
                "MARKER_ELEMS= 1",
                "3 0 1",
                "MARKER_TAG= Wall",
                "MARKER_ELEMS= 2",
                "3 1 2",
                "3 2 3",
                "MARKER_TAG= Fluid",
                "MARKER_ELEMS= 1",
                "5 0 1 2"
            });

            string fakeGmsh = CreateFakeGmshScript(su2Content);

            var config = SimulationConfig.CreateDefault();
            config.GmshPath = fakeGmsh;

            var mesher = new Gmsh2DMesher(new Gmsh2DMesherOptions(), new FlowConditions(2.0f, 1025f, 1.001e-3f, "test"));
            IMeshMetricsSource metricsSource = Assert.IsAssignableFrom<IMeshMetricsSource>(mesher);

            var request = new MeshRequest(contourPath, 1, 1, config, 1.0f, _tempRoot);
            string meshPath = mesher.GenerateMesh(request);

            Assert.True(File.Exists(meshPath));
            Assert.True(metricsSource.LastMeshMetrics.TryGetValue("MeshCells", out float meshCells));
            Assert.True(meshCells > 0f);
            Assert.Equal(3f, meshCells);
            Assert.True(metricsSource.LastMeshMetrics.ContainsKey("MeshCells_Tri"));
            Assert.True(metricsSource.LastMeshMetrics.ContainsKey("MeshCells_Quad"));
            Assert.True(metricsSource.LastMeshMetrics.ContainsKey("MeshCells_Prism"));
            Assert.True(metricsSource.LastMeshMetrics.ContainsKey("MeshCells_Hexa"));
            Assert.True(metricsSource.LastMeshMetrics.ContainsKey("MeshCells_Tet"));
            Assert.True(metricsSource.LastMeshMetrics.ContainsKey("MeshCells_Pyramid"));
        }

        private string CreateFakeGmshScript(string su2Content)
        {
            if (OperatingSystem.IsWindows())
            {
                string psPath = Path.Combine(_tempRoot, "fake_gmsh_2d.ps1");
                File.WriteAllText(psPath, $@"
$outIdx = [Array]::IndexOf($args, '-o')
$outPath = $args[$outIdx + 1]
Set-Content -LiteralPath $outPath -Value @'
{su2Content}
'@ -Encoding ASCII
exit 0
");
                string cmdPath = Path.Combine(_tempRoot, "fake_gmsh_2d.cmd");
                File.WriteAllText(cmdPath, $"@echo off\r\npowershell -NoProfile -ExecutionPolicy Bypass -File \"{psPath}\" %*\r\n");
                return cmdPath;
            }
            else
            {
                string shPath = Path.Combine(_tempRoot, "fake_gmsh_2d.sh");
                File.WriteAllText(shPath, $@"#!/bin/sh
out=""""
prev=""""
for a in ""$@""; do
  if [ ""$prev"" = ""-o"" ]; then out=""$a""; fi
  prev=""$a""
done
cat <<'EOF' > ""$out""
{su2Content}
EOF
exit 0
");
                File.SetUnixFileMode(shPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                return shPath;
            }
        }

        [Fact]
        public void ModelRecord_Exposes_Artifacts_And_GeometryPath_Alias()
        {
            var record = new ModelRecord();
            Assert.NotNull(record.Artifacts);
            Assert.Empty(record.Artifacts);

            record.Artifacts["OpenFoamCase"] = "/tmp/case_1";
            Assert.Equal("/tmp/case_1", record.Artifacts["OpenFoamCase"]);

            record.GeometryPath = "geom/part.stl";
            Assert.Equal("geom/part.stl", record.StlPath);
            Assert.Equal("geom/part.stl", record.GeometryPath);

            record.StlPath = "geom/updated.stl";
            Assert.Equal("geom/updated.stl", record.GeometryPath);
        }

        [Fact]
        public void MeshCalibration_ComputeFingerprint_Detects_Arbitrary_Solver_Json_Files()
        {
            string projectDir = Path.Combine(_tempRoot, "GenericSolverFingerprint");
            string solversDir = Path.Combine(projectDir, "solvers");
            Directory.CreateDirectory(solversDir);
            File.WriteAllText(Path.Combine(solversDir, "su2.json"), @"{ ""TargetYPlus"": 1.5 }");

            var project = new ProjectConfig { BaseParameters = new Dictionary<string, float> { ["L"] = 100f } };
            var context = new ProjectContext("Test", projectDir, null, new SimulationConfig(), project);

            string fpInitial = MeshCalibration.ComputeFingerprint(context);

            string openFoamPath = Path.Combine(solversDir, "openfoam.json");
            File.WriteAllText(openFoamPath, @"{ ""Solver"": ""simpleFoam"", ""EndTime"": 500 }");
            string fpWithOpenFoam = MeshCalibration.ComputeFingerprint(context);
            Assert.NotEqual(fpInitial, fpWithOpenFoam);

            File.WriteAllText(openFoamPath, @"{ ""Solver"": ""simpleFoam"", ""EndTime"": 1000 }");
            string fpModifiedOpenFoam = MeshCalibration.ComputeFingerprint(context);
            Assert.NotEqual(fpWithOpenFoam, fpModifiedOpenFoam);
        }
    }
}
