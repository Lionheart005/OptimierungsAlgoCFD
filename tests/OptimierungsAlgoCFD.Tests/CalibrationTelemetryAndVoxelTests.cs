using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using Moq;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;
using Xunit;

namespace OptimierungsAlgoCFD.Tests
{
    public class CalibrationTelemetryAndVoxelTests : IDisposable
    {
        private readonly string _root;

        public CalibrationTelemetryAndVoxelTests()
        {
            _root = Path.Combine(Path.GetTempPath(), "ap6_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                try { Directory.Delete(_root, recursive: true); } catch { }
            }
        }

        private string Sub(string name)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static ProjectConfig CreateScaledProject(float lengthMm = 500f) => new()
        {
            ProjectName = "TestProject",
            BaseParameters = new Dictionary<string, float> { ["Length"] = lengthMm },
            MaxDeviations = new Dictionary<string, float> { ["Length"] = 100f },
            ParameterBounds = new Dictionary<string, (float Min, float Max)> { ["Length"] = (200f, 800f) },
            DimensionalParameters = new HashSet<string> { "Length" }
        };

        private static SolverStage[] Create3DStages(float surfaceCellSizeMm)
        {
            var mesher = new GmshCfdMesher(new GmshMesherOptions { SurfaceCellSize = surfaceCellSizeMm });
            var solver = new Mock<ISimulationSolver>().Object;
            return new[] { new SolverStage(mesher, solver) };
        }

        // =====================================================================
        // 1. VoxelResolutionAdvisor
        // =====================================================================

        [Fact]
        public void VoxelAdvisor_CoarseVoxelWithoutAutoRefine_EmitsWarningAndKeepsBase()
        {
            // Length=500mm, TargetPicoGkSize=50mm => typicalScale = 0.1
            // BaseVoxelResolution=0.5mm => realVoxelMm = 5.0mm > 0.8 * 2.0 = 1.6mm
            var sim = new SimulationConfig
            {
                BaseVoxelResolution = 0.5f,
                TargetPicoGkSize = 50.0f,
                UseRubberBandScaler = true,
                AutoVoxelResolution = false
            };
            var project = CreateScaledProject(500f);
            var stages = Create3DStages(2.0f);
            using var writer = new StringWriter();

            float effective = VoxelResolutionAdvisor.Resolve(sim, project, stages, writer);

            Assert.Equal(0.5f, effective);
            Assert.Equal(0.5f, sim.BaseVoxelResolution);
            Assert.Contains("[WARNUNG]", writer.ToString());
        }

        [Fact]
        public void VoxelAdvisor_AutoVoxelResolution_RefinesDownWithoutWarningAndMutatingConfig()
        {
            // Length=500mm, TargetPicoGkSize=50mm => typicalScale = 0.1
            // surfaceMm = 2.0mm => 0.75 * 2.0 * 0.1 = 0.15mm (>= 0.1mm floor, realVoxel=1.5mm <= 1.6mm)
            var sim = new SimulationConfig
            {
                BaseVoxelResolution = 0.5f,
                TargetPicoGkSize = 50.0f,
                UseRubberBandScaler = true,
                AutoVoxelResolution = true
            };
            var project = CreateScaledProject(500f);
            var stages = Create3DStages(2.0f);
            using var writer = new StringWriter();

            float effective = VoxelResolutionAdvisor.Resolve(sim, project, stages, writer);

            Assert.Equal(0.15f, effective, 4);
            Assert.Equal(0.5f, sim.BaseVoxelResolution);
            string log = writer.ToString();
            Assert.Contains("[INFO] AutoVoxelResolution:", log);
            Assert.DoesNotContain("[WARNUNG]", log);
        }

        [Fact]
        public void VoxelAdvisor_AutoVoxelResolution_RespectsSafetyFloorAndWarnsIfStillCoarse()
        {
            // Length=1000mm, TargetPicoGkSize=50mm => typicalScale = 0.05
            // surfaceMm = 1.0mm => 0.75 * 1.0 * 0.05 = 0.0375mm -> clamped to 0.1mm floor
            // effective/typicalScale = 0.1 / 0.05 = 2.0mm > 0.8 * 1.0 = 0.8mm => warns too
            var sim = new SimulationConfig
            {
                BaseVoxelResolution = 0.5f,
                TargetPicoGkSize = 50.0f,
                UseRubberBandScaler = true,
                AutoVoxelResolution = true
            };
            var project = CreateScaledProject(1000f);
            var stages = Create3DStages(1.0f);
            using var writer = new StringWriter();

            float effective = VoxelResolutionAdvisor.Resolve(sim, project, stages, writer);

            Assert.Equal(0.1f, effective, 5);
            string log = writer.ToString();
            Assert.Contains("[INFO] AutoVoxelResolution:", log);
            Assert.Contains("[WARNUNG]", log);
        }

        [Fact]
        public void VoxelAdvisor_2DPipelineWithoutGmshCfdMesher_ReturnsUnchangedWithoutWarning()
        {
            var sim = new SimulationConfig
            {
                BaseVoxelResolution = 0.5f,
                AutoVoxelResolution = true
            };
            var project = CreateScaledProject(500f);
            var stages = new[]
            {
                new SolverStage(new Gmsh2DMesher(), new Mock<ISimulationSolver>().Object)
            };
            using var writer = new StringWriter();

            float effective = VoxelResolutionAdvisor.Resolve(sim, project, stages, writer);

            Assert.Equal(0.5f, effective);
            Assert.Equal(string.Empty, writer.ToString());
        }

        // =====================================================================
        // 2. RunJournal & MeshCalibration DurationSeconds across --resume
        // =====================================================================

        [Fact]
        public void RunJournal_LegacyEntriesWithoutDuration_AreReadableAndReturnNullDuration()
        {
            string dir = Sub("legacy_journal");
            string journalDir = RunJournal.DirectoryFor(dir);
            Directory.CreateDirectory(journalDir);
            string path = RunJournal.PathFor(dir);

            string headerLine = "{\"ZufallsSeed\":42,\"Start\":\"2026-11-10T10:00:00+01:00\"}";
            string entryLine = "{\"Index\":0,\"Iteration\":1,\"Variant\":1,\"Factor\":1,\"Tag\":\"Kal3\",\"Parameters\":{\"Length\":500},\"Record\":{\"Iteration\":1,\"Variant\":1,\"ActiveParameters\":{\"Length\":500},\"PassiveParameters\":{\"Drag\":0.25},\"Fitness\":1.0,\"SimulationFailed\":false}}";
            File.WriteAllLines(path, new[] { headerLine, entryLine });

            RunJournalContent? content = RunJournal.Read(path);
            Assert.NotNull(content);
            Assert.Single(content!.Entries);
            Assert.Null(content.Entries[0].DurationSeconds);

            RunJournal replay = RunJournal.Resume(dir, content);
            Assert.True(replay.TryReplay(1, 1, 1f, "Kal3", new Dictionary<string, float> { ["Length"] = 500f }, out ModelRecord? rec));
            Assert.NotNull(rec);
            Assert.Null(replay.LastReplayedDurationSeconds);
        }

        [Fact]
        public void MeshCalibration_Resume_PreservesDurationSecondsAndLeavesCsvBitIdentical()
        {
            string fullDir = Sub("calib_full");
            string resumeDir = Sub("calib_resume");

            var project = CreateScaledProject(500f);
            var sim = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Auto", Tolerance = 0.05f },
                MaxIterations = 1,
                VariantsPerIteration = 2
            };
            var context = new ProjectContext("Test", null, null, sim, project);

            static ModelRecord MakeCalibRecord(Dictionary<string, float> parameters, int iter, int varIdx, float factor)
            {
                var rec = new ModelRecord
                {
                    Iteration = iter,
                    Variant = varIdx,
                    ActiveParameters = parameters,
                    Fitness = 1.0f
                };
                rec.PassiveParameters["MeshRefinementFactor"] = factor;
                rec.PassiveParameters["MeshCells"] = (float)Math.Round(10000.0 / Math.Pow(factor, 3));
                rec.PassiveParameters["Drag"] = 0.2500f + 0.001f * factor;
                rec.PassiveParameters["YPlus_P95"] = 0.9f;
                rec.PassiveParameters["Su2_Converged"] = 1f;
                rec.PassiveParameters["TunnelBlockagePercent"] = 2.4f;
                rec.PassiveParameters["Su2_CD_p"] = 0.18f;
                rec.PassiveParameters["Su2_CD_v"] = 0.07f;
                return rec;
            }

            WorkflowController CreateController(string workDir, RunJournal? journal, bool delayOnLive)
            {
                var simCtx = new SimulationContext(sim, project, workDir);
                simCtx.EnsureDirectories();

                var geo = new Mock<IGeometryGenerator>();
                geo.Setup(g => g.GenerateAndExport(It.IsAny<int>(), It.IsAny<int>(), It.IsAny<Dictionary<string, float>>(), It.IsAny<string>(), It.IsAny<SimulationConfig>()))
                   .Returns((int it, int v, Dictionary<string, float> p, string wd, SimulationConfig _) =>
                   {
                       var gr = new GeometryResult { StlPath = "dummy.stl" };
                       gr.AddMetric("Length", p["Length"], MetricScaling.None);
                       return gr;
                   });

                var mesher = new Mock<IMeshGenerator>();
                mesher.Setup(m => m.GenerateMesh(It.IsAny<MeshRequest>()))
                      .Returns("dummy.su2");

                var solver = new Mock<ISimulationSolver>();
                solver.SetupGet(s => s.Name).Returns("MockSolver");
                solver.Setup(s => s.Solve(It.IsAny<string>(), It.IsAny<ModelRecord>(), It.IsAny<SimulationConfig>(), It.IsAny<string>()))
                      .Callback((string _, ModelRecord r, SimulationConfig _, string _) =>
                      {
                          if (delayOnLive) System.Threading.Thread.Sleep(60);
                          r.PassiveParameters["Drag"] = 0.2500f + 0.001f * r.PassiveParameters["MeshRefinementFactor"];
                          r.PassiveParameters["MeshCells"] = (float)Math.Round(10000.0 / Math.Pow(r.PassiveParameters["MeshRefinementFactor"], 3));
                          r.PassiveParameters["YPlus_P95"] = 0.9f;
                          r.PassiveParameters["Su2_Converged"] = 1f;
                      });

                var validator = new Mock<IModelValidator>().Object;
                var opt = new Mock<IOptimizationAlgorithm>().Object;
                return new WorkflowController(
                    geo.Object,
                    new[] { new SolverStage(mesher.Object, solver.Object) },
                    validator,
                    opt,
                    simCtx,
                    new[] { "Drag" },
                    journal);
            }

            // 1. Live run with journal
            var header = new RunJournalHeader { ZufallsSeed = 123, Start = "2026-11-13T10:00:00+01:00" };
            RunJournal fullJournal = RunJournal.StartNew(fullDir, header);
            WorkflowController fullCtrl = CreateController(fullDir, fullJournal, delayOnLive: true);
            var fullSimCtx = new SimulationContext(sim, project, fullDir);
            var (fullDec, _) = MeshCalibration.Run(
                context,
                fullSimCtx,
                (f, tag) => fullCtrl.RunPipeline(project.BaseParameters, 1, 1, customRefinementFactor: f, customTag: tag),
                monitoredMetrics: new[] { "Drag" },
                durationProvider: () => fullCtrl.LastCallDurationSeconds);

            Assert.True(fullDec.AllStages.All(s => s.DurationSeconds >= 0.04));

            // 2. Copy truncated journal (first 2 calibration entries) and resume
            string[] fullJournalLines = File.ReadAllLines(RunJournal.PathFor(fullDir));
            Directory.CreateDirectory(RunJournal.DirectoryFor(resumeDir));
            File.WriteAllLines(RunJournal.PathFor(resumeDir), fullJournalLines.Take(3)); // header + 2 stages

            RunJournalContent? parsed = RunJournal.Read(RunJournal.PathFor(resumeDir));
            Assert.NotNull(parsed);
            Assert.Equal(2, parsed!.Entries.Count);
            Assert.All(parsed.Entries, e => Assert.True(e.DurationSeconds > 0.04));

            RunJournal resumeJournal = RunJournal.Resume(resumeDir, parsed);
            WorkflowController resumeCtrl = CreateController(resumeDir, resumeJournal, delayOnLive: true);
            var resumeSimCtx = new SimulationContext(sim, project, resumeDir);
            var (resumedDec, _) = MeshCalibration.Run(
                context,
                resumeSimCtx,
                (f, tag) => resumeCtrl.RunPipeline(project.BaseParameters, 1, 1, customRefinementFactor: f, customTag: tag),
                monitoredMetrics: new[] { "Drag" },
                durationProvider: () => resumeCtrl.LastCallDurationSeconds);

            Assert.Equal(2, resumeJournal.ReplayedCount);
            var fullStage1 = fullDec.AllStages.Single(s => s.LevelIndex == 1);
            var resumedStage1 = resumedDec.AllStages.Single(s => s.LevelIndex == 1);
            Assert.Equal(fullStage1.DurationSeconds, resumedStage1.DurationSeconds, 6);

            // Check PassiveParameters does NOT contain DurationSeconds
            ModelRecord probe = MakeCalibRecord(project.BaseParameters, 1, 1, 1.0f);
            fullJournal.Append(2, 1, 1.0f, "Check", project.BaseParameters, probe, 12.34);
            Assert.False(probe.PassiveParameters.ContainsKey("DurationSeconds"));
        }

        // =====================================================================
        // 3. MeshMetrics.ReadSu2CellBreakdown (3D & 2D + ignoring Farfield/Fluid)
        // =====================================================================

        [Fact]
        public void ReadSu2CellBreakdown_Parses3DAndIgnoresFarfieldAndFluid()
        {
            string su2Path = Path.Combine(_root, "mesh3d.su2");
            File.WriteAllText(su2Path, string.Join("\n", new[]
            {
                "% Comment line",
                "NDIME= 3",
                "NELEM= 7",
                "10 0 1 2 3 0",
                "10 1 2 3 4 1",
                "12 0 1 2 3 4 5 6 7 2",
                "13 0 1 2 3 4 5 3",
                "13 0 1 2 3 4 5 4",
                "13 0 1 2 3 4 5 5",
                "14 0 1 2 3 4 6",
                "NPOIN= 8",
                "NMARK= 4",
                "MARKER_TAG= WallBody",
                "MARKER_ELEMS= 3",
                "5 0 1 2",
                "9 0 1 2 3",
                "9 1 2 3 4",
                "MARKER_TAG= Farfield",
                "MARKER_ELEMS= 2",
                "5 0 1 2",
                "9 0 1 2 3",
                "MARKER_TAG= fluid",
                "MARKER_ELEMS= 1",
                "5 0 1 2",
                "MARKER_TAG= Wing",
                "MARKER_ELEMS= 1",
                "5 2 3 4"
            }));

            Su2CellBreakdown? b = MeshMetrics.ReadSu2CellBreakdown(su2Path);

            Assert.NotNull(b);
            Assert.Equal(7, b!.TotalVolumeCells);
            Assert.Equal(2, b.Tetrahedra);
            Assert.Equal(1, b.Hexahedra);
            Assert.Equal(3, b.Prisms);
            Assert.Equal(1, b.Pyramids);
            Assert.Equal(2, b.SurfaceTriangles);
            Assert.Equal(2, b.SurfaceQuads);
            Assert.Equal(0, b.SurfaceLines);
        }

        [Fact]
        public void ReadSu2CellBreakdown_Parses2DElementsAndWallLines()
        {
            string su2Path = Path.Combine(_root, "mesh2d.su2");
            File.WriteAllText(su2Path, string.Join("\n", new[]
            {
                "NDIME= 2",
                "NELEM= 3",
                "5 0 1 2 0",
                "9 0 1 2 3 1",
                "5 1 2 3 2",
                "NMARK= 2",
                "MARKER_TAG= Airfoil",
                "MARKER_ELEMS= 2",
                "3 0 1",
                "3 1 2",
                "MARKER_TAG= FARFIELD",
                "MARKER_ELEMS= 2",
                "3 2 3",
                "3 3 0"
            }));

            Su2CellBreakdown? b = MeshMetrics.ReadSu2CellBreakdown(su2Path);

            Assert.NotNull(b);
            Assert.Equal(3, b!.TotalVolumeCells);
            Assert.Equal(0, b.Tetrahedra);
            Assert.Equal(0, b.SurfaceTriangles);
            Assert.Equal(0, b.SurfaceQuads);
            Assert.Equal(2, b.SurfaceLines);
        }

        // =====================================================================
        // 4. Su2SurfaceAnalyzer.EvaluateAerodynamicDragBreakdown
        // =====================================================================

        [Theory]
        [InlineData(false, false)] // ASCII, outward winding
        [InlineData(false, true)]  // ASCII, inward winding (auto-oriented)
        [InlineData(true, false)]  // Appended raw binary UInt64, outward winding
        public void EvaluateAerodynamicDragBreakdown_3D_QuadAndTri_MatchesAnalytical(bool binaryAppended, bool flipWinding)
        {
            // Box [0,2] x [0,1] x [0,1]
            // Points 0..7:
            double[] points =
            {
                0, 0, 0, // 0
                2, 0, 0, // 1
                2, 1, 0, // 2
                0, 1, 0, // 3
                0, 0, 1, // 4
                2, 0, 1, // 5
                2, 1, 1, // 6
                0, 1, 1  // 7
            };

            // Quads on x=0 and x=2, Triangles on the 4 side faces (2 tris each = 8 tris)
            var quads = new[]
            {
                new[] { 0, 4, 7, 3 }, // x=0 outward (-1,0,0)
                new[] { 1, 2, 6, 5 }  // x=2 outward (+1,0,0)
            };
            var tris = new[]
            {
                new[] { 0, 1, 5 }, new[] { 0, 5, 4 }, // y=0: (1,0,0)x(2,0,1) -> (0,-1,0) outward
                new[] { 3, 7, 6 }, new[] { 3, 6, 2 }, // y=1: (0,0,1)x(2,0,1) -> (0,+1,0) outward
                new[] { 0, 2, 1 }, new[] { 0, 3, 2 }, // z=0: (2,1,0)x(2,0,0) -> (0,0,-1) outward
                new[] { 4, 5, 6 }, new[] { 4, 6, 7 }  // z=1: (2,0,0)x(2,1,0) -> (0,0,+1) outward
            };

            var connectivity = new List<int>();
            var offsets = new List<int>();
            var types = new List<byte>();

            foreach (int[] q in quads)
            {
                int[] ord = flipWinding ? q.Reverse().ToArray() : q;
                connectivity.AddRange(ord);
                offsets.Add(connectivity.Count);
                types.Add(9);
            }
            foreach (int[] t in tris)
            {
                int[] ord = flipWinding ? t.Reverse().ToArray() : t;
                connectivity.AddRange(ord);
                offsets.Add(connectivity.Count);
                types.Add(5);
            }

            // Cp(x,y,z) = 2 - x => front x=0 has Cp=2, back x=2 has Cp=0, side averages cancel
            double[] cp = new double[8];
            double[] cf = new double[8 * 3];
            for (int i = 0; i < 8; i++)
            {
                cp[i] = 2.0 - points[i * 3];
                cf[i * 3 + 0] = 0.01;
                cf[i * 3 + 1] = 0.00;
                cf[i * 3 + 2] = 0.02;
            }

            string vtuPath = Path.Combine(_root, $"box3d_{(binaryAppended ? "bin" : "ascii")}_{(flipWinding ? "flip" : "out")}.vtu");
            if (binaryAppended)
            {
                WriteAppendedBinaryVtu64(vtuPath, points, cp, cf, connectivity.ToArray(), offsets.ToArray(), types.ToArray(), points.Length / 3, offsets.Count);
            }
            else
            {
                WriteAsciiVtu(vtuPath, points, cp, cf, connectivity.ToArray(), offsets.ToArray(), types.ToArray(), points.Length / 3, offsets.Count);
            }

            float refAreaM2 = 0.5f;
            float aoaDeg = 30.0f;
            AerodynamicDragBreakdown? res = Su2SurfaceAnalyzer.EvaluateAerodynamicDragBreakdown(vtuPath, refAreaM2, aoaDeg, 3);

            Assert.NotNull(res);
            Assert.False(res!.Is2D);
            Assert.Equal(10, res.ElementCount);

            double rad = aoaDeg * Math.PI / 180.0;
            double expectedCdP = (2.0 * Math.Cos(rad)) / refAreaM2;
            double expectedCdV = (10.0 * (0.01 * Math.Cos(rad) + 0.02 * Math.Sin(rad))) / refAreaM2;

            Assert.Equal(expectedCdP, res.PressureDragCoeff, 6);
            Assert.Equal(expectedCdV, res.ViscousDragCoeff, 6);
            Assert.Equal(expectedCdP + expectedCdV, res.TotalDragCoeff, 6);
        }

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void EvaluateAerodynamicDragBreakdown_2D_LineElements_MatchesAnalytical(bool reverseContour)
        {
            // Unit square [0,1] x [0,1] in XY plane
            double[] points =
            {
                0, 0, 0,
                1, 0, 0,
                1, 1, 0,
                0, 1, 0
            };
            int[] conn = reverseContour
                ? new[] { 0, 3, 3, 2, 2, 1, 1, 0 }
                : new[] { 0, 1, 1, 2, 2, 3, 3, 0 };
            int[] offsets = { 2, 4, 6, 8 };
            byte[] types = { 3, 3, 3, 3 };

            double[] cp = new double[4];
            double[] cf = new double[4 * 3];
            for (int i = 0; i < 4; i++)
            {
                cp[i] = 1.0 - points[i * 3]; // 1 at x=0, 0 at x=1
                cf[i * 3 + 0] = 0.02;
                cf[i * 3 + 1] = 0.01;
                cf[i * 3 + 2] = 0.0;
            }

            string dir = Sub($"vtu2d_{(reverseContour ? "cw" : "ccw")}");
            string vtuPath = Path.Combine(dir, "surface_flow.vtu");
            WriteAsciiVtu(vtuPath, points, cp, cf, conn, offsets, types, 4, 4);

            float refChordM = 0.25f;
            float aoaDeg = 30.0f;
            AerodynamicDragBreakdown? res = Su2SurfaceAnalyzer.EvaluateDragBreakdownFromDirectory(dir, refChordM, aoaDeg, 2);

            Assert.NotNull(res);
            Assert.True(res!.Is2D);
            Assert.Equal(4, res.ElementCount);

            double rad = aoaDeg * Math.PI / 180.0;
            double expectedCdP = Math.Cos(rad) / refChordM;
            double expectedCdV = (4.0 * (0.02 * Math.Cos(rad) + 0.01 * Math.Sin(rad))) / refChordM;

            Assert.Equal(expectedCdP, res.PressureDragCoeff, 6);
            Assert.Equal(expectedCdV, res.ViscousDragCoeff, 6);
        }

        private static void WriteAsciiVtu(
            string path, double[] points, double[] cp, double[] cf,
            int[] conn, int[] offsets, byte[] types, int numPoints, int numCells)
        {
            string JoinDoubles(double[] arr) => string.Join(" ", arr.Select(v => v.ToString("R", CultureInfo.InvariantCulture)));
            string JoinInts(int[] arr) => string.Join(" ", arr.Select(v => v.ToString(CultureInfo.InvariantCulture)));
            string JoinBytes(byte[] arr) => string.Join(" ", arr.Select(v => v.ToString(CultureInfo.InvariantCulture)));

            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\"?>");
            xml.AppendLine("<VTKFile type=\"UnstructuredGrid\" version=\"0.1\" byte_order=\"LittleEndian\">");
            xml.AppendLine("  <UnstructuredGrid>");
            xml.AppendLine($"    <Piece NumberOfPoints=\"{numPoints}\" NumberOfCells=\"{numCells}\">");
            xml.AppendLine("      <PointData>");
            xml.AppendLine($"        <DataArray type=\"Float64\" Name=\"Pressure_Coefficient\" format=\"ascii\">{JoinDoubles(cp)}</DataArray>");
            xml.AppendLine($"        <DataArray type=\"Float64\" Name=\"Skin_Friction_Coefficient\" NumberOfComponents=\"3\" format=\"ascii\">{JoinDoubles(cf)}</DataArray>");
            xml.AppendLine("      </PointData>");
            xml.AppendLine("      <Points>");
            xml.AppendLine($"        <DataArray type=\"Float64\" Name=\"Points\" NumberOfComponents=\"3\" format=\"ascii\">{JoinDoubles(points)}</DataArray>");
            xml.AppendLine("      </Points>");
            xml.AppendLine("      <Cells>");
            xml.AppendLine($"        <DataArray type=\"Int32\" Name=\"connectivity\" format=\"ascii\">{JoinInts(conn)}</DataArray>");
            xml.AppendLine($"        <DataArray type=\"Int32\" Name=\"offsets\" format=\"ascii\">{JoinInts(offsets)}</DataArray>");
            xml.AppendLine($"        <DataArray type=\"UInt8\" Name=\"types\" format=\"ascii\">{JoinBytes(types)}</DataArray>");
            xml.AppendLine("      </Cells>");
            xml.AppendLine("    </Piece>");
            xml.AppendLine("  </UnstructuredGrid>");
            xml.AppendLine("</VTKFile>");

            File.WriteAllText(path, xml.ToString(), new UTF8Encoding(false));
        }

        private static void WriteAppendedBinaryVtu64(
            string path, double[] points, double[] cp, double[] cf,
            int[] conn, int[] offsets, byte[] types, int numPoints, int numCells)
        {
            using var bin = new MemoryStream();

            long WriteBlock(byte[] raw)
            {
                long offset = bin.Position;
                byte[] header = BitConverter.GetBytes((ulong)raw.Length);
                bin.Write(header, 0, header.Length);
                bin.Write(raw, 0, raw.Length);
                return offset;
            }

            byte[] ToBytesDouble(double[] arr)
            {
                var buf = new byte[arr.Length * 8];
                Buffer.BlockCopy(arr, 0, buf, 0, buf.Length);
                return buf;
            }

            byte[] ToBytesInt(int[] arr)
            {
                var buf = new byte[arr.Length * 4];
                Buffer.BlockCopy(arr, 0, buf, 0, buf.Length);
                return buf;
            }

            long offCp = WriteBlock(ToBytesDouble(cp));
            long offCf = WriteBlock(ToBytesDouble(cf));
            long offPts = WriteBlock(ToBytesDouble(points));
            long offConn = WriteBlock(ToBytesInt(conn));
            long offOff = WriteBlock(ToBytesInt(offsets));
            long offTyp = WriteBlock(types);

            var xml = new StringBuilder();
            xml.AppendLine("<?xml version=\"1.0\"?>");
            xml.AppendLine("<VTKFile type=\"UnstructuredGrid\" version=\"1.0\" byte_order=\"LittleEndian\" header_type=\"UInt64\">");
            xml.AppendLine("  <UnstructuredGrid>");
            xml.AppendLine($"    <Piece NumberOfPoints=\"{numPoints}\" NumberOfCells=\"{numCells}\">");
            xml.AppendLine("      <PointData>");
            xml.AppendLine($"        <DataArray type=\"Float64\" Name=\"Pressure_Coefficient\" format=\"appended\" offset=\"{offCp}\"/>");
            xml.AppendLine($"        <DataArray type=\"Float64\" Name=\"Skin_Friction_Coefficient\" NumberOfComponents=\"3\" format=\"appended\" offset=\"{offCf}\"/>");
            xml.AppendLine("      </PointData>");
            xml.AppendLine("      <Points>");
            xml.AppendLine($"        <DataArray type=\"Float64\" Name=\"Points\" NumberOfComponents=\"3\" format=\"appended\" offset=\"{offPts}\"/>");
            xml.AppendLine("      </Points>");
            xml.AppendLine("      <Cells>");
            xml.AppendLine($"        <DataArray type=\"Int32\" Name=\"connectivity\" format=\"appended\" offset=\"{offConn}\"/>");
            xml.AppendLine($"        <DataArray type=\"Int32\" Name=\"offsets\" format=\"appended\" offset=\"{offOff}\"/>");
            xml.AppendLine($"        <DataArray type=\"UInt8\" Name=\"types\" format=\"appended\" offset=\"{offTyp}\"/>");
            xml.AppendLine("      </Cells>");
            xml.AppendLine("    </Piece>");
            xml.AppendLine("  </UnstructuredGrid>");
            xml.Append("  <AppendedData encoding=\"raw\">\n_");

            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write);
            byte[] headerBytes = new UTF8Encoding(false).GetBytes(xml.ToString());
            fs.Write(headerBytes, 0, headerBytes.Length);
            byte[] appData = bin.ToArray();
            fs.Write(appData, 0, appData.Length);
            byte[] footer = new UTF8Encoding(false).GetBytes("\n  </AppendedData>\n</VTKFile>\n");
            fs.Write(footer, 0, footer.Length);
        }

        // =====================================================================
        // 5. Calibration CSV / JSON / PrintSummary Telemetry & ModelRecord Metadata
        // =====================================================================

        [Fact]
        public void MeshCalibration_Reports_IncludeCellBreakdownBlockageAndDragBreakdown()
        {
            Assert.True(ModelRecord.IsMeshMetadataKey("MeshCells_Tri"));
            Assert.True(ModelRecord.IsMeshMetadataKey("MeshCells_Prism"));

            string workDir = Sub("calib_reports");
            var project = CreateScaledProject(500f);
            var sim = new SimulationConfig
            {
                MeshCalibration = new MeshCalibrationConfig { Mode = "Auto", Tolerance = 0.05f }
            };
            var context = new ProjectContext("Test", null, null, sim, project);
            var simCtx = new SimulationContext(sim, project, workDir);
            simCtx.EnsureDirectories();

            var (decision, reportRelPath) = MeshCalibration.Run(
                context,
                simCtx,
                (factor, tag) =>
                {
                    var rec = new ModelRecord
                    {
                        Iteration = 1,
                        Variant = 1,
                        ActiveParameters = project.BaseParameters,
                        Fitness = 1.0f
                    };
                    rec.PassiveParameters["MeshCells"] = 10000f;
                    rec.PassiveParameters["Drag"] = 0.20f + 0.001f * factor;
                    rec.PassiveParameters["YPlus_P95"] = 0.8f;
                    rec.PassiveParameters["Su2_Converged"] = 1f;
                    rec.PassiveParameters["MeshCells_Tri"] = 120f;
                    rec.PassiveParameters["MeshCells_Quad"] = 80f;
                    rec.PassiveParameters["MeshCells_Prism"] = 3000f;
                    rec.PassiveParameters["MeshCells_Hexa"] = 2000f;
                    rec.PassiveParameters["MeshCells_Tet"] = 4900f;
                    rec.PassiveParameters["MeshCells_Pyramid"] = 100f;
                    // Omit Blockage/CD_p/CD_v on Stufe 2 to verify finite-only JSON omission
                    if (Math.Abs(factor - 1.41f) > 0.02f)
                    {
                        rec.PassiveParameters["TunnelBlockagePercent"] = 2.75f;
                        rec.PassiveParameters["Su2_CD_p"] = 0.1400f;
                        rec.PassiveParameters["Su2_CD_v"] = 0.0600f;
                    }
                    return rec;
                },
                monitoredMetrics: new[] { "Drag" });

            string csvPath = Path.Combine(workDir, reportRelPath, "calibration.csv");
            string jsonPath = Path.Combine(workDir, reportRelPath, "calibration.json");
            Assert.True(File.Exists(csvPath));
            Assert.True(File.Exists(jsonPath));

            string[] csvLines = File.ReadAllLines(csvPath);
            Assert.Contains(";Tri;Quad;Prism;Hexa;Tet;Pyramiden;Blockage_%;CD_p;CD_v", csvLines[0]);
            Assert.Contains(csvLines, l => l.Contains(";120;80;3000;2000;4900;100;2.75;0.14000;0.06000"));

            JsonObject jsonRoot = JsonNode.Parse(File.ReadAllText(jsonPath))!.AsObject();
            JsonArray stages = jsonRoot["Stages"]!.AsArray();
            JsonObject stageWithFinite = stages.First(s => s!["Factor"]!.GetValue<float>() > 1.9f)!.AsObject();
            Assert.Equal(120, stageWithFinite["SurfaceTriangles"]!.GetValue<int>());
            Assert.Equal(80, stageWithFinite["SurfaceQuads"]!.GetValue<int>());
            Assert.Equal(3000, stageWithFinite["VolumePrisms"]!.GetValue<int>());
            Assert.Equal(2000, stageWithFinite["VolumeHexahedra"]!.GetValue<int>());
            Assert.Equal(4900, stageWithFinite["VolumeTetrahedra"]!.GetValue<int>());
            Assert.Equal(100, stageWithFinite["VolumePyramids"]!.GetValue<int>());
            Assert.Equal(2.75, stageWithFinite["TunnelBlockagePercent"]!.GetValue<double>(), 2);
            Assert.Equal(0.14, stageWithFinite["CD_p"]!.GetValue<double>(), 4);
            Assert.Equal(0.06, stageWithFinite["CD_v"]!.GetValue<double>(), 4);

            JsonObject stageWithoutFinite = stages.First(s => Math.Abs(s!["Factor"]!.GetValue<float>() - 1.41f) < 0.02f)!.AsObject();
            Assert.Null(stageWithoutFinite["TunnelBlockagePercent"]);
            Assert.Null(stageWithoutFinite["CD_p"]);
            Assert.Null(stageWithoutFinite["CD_v"]);

            using var summaryCapture = new StringWriter();
            MeshCalibration.PrintSummary(decision, summaryCapture);
            string summaryText = summaryCapture.ToString();
            Assert.Contains("Blockage=2.75%", summaryText);
            Assert.Contains("CD_p=0.14, CD_v=0.06", summaryText);
            Assert.Contains("Tri=", summaryText);
        }
    }
}
