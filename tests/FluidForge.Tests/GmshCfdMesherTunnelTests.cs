using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Xunit;
using FluidForge.Core;
using FluidForge.Projects.MantaAuv;
using FluidForge.Solvers.Cfd;

namespace FluidForge.Tests
{
    /// <summary>
    /// <c>EnsureTunnel</c> is the part of the mesher that runs without Gmsh — and since
    /// TODO-11 also without PicoGK. These tests cover the path from JSON options to
    /// the written file.
    /// </summary>
    public class GmshCfdMesherTunnelTests : IDisposable
    {
        private readonly string _workingDirectory;

        public GmshCfdMesherTunnelTests()
        {
            _workingDirectory = Path.Combine(Path.GetTempPath(), "tunnel_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_workingDirectory);
        }

        public void Dispose()
        {
            if (Directory.Exists(_workingDirectory)) Directory.Delete(_workingDirectory, true);
        }

        /// <summary>
        /// Default case since 2026-11-08: box 1000 x 300 x 300 mm, center 200 mm downstream
        /// (6 L upstream, 14 L wake for 50 mm model), 12 triangles.
        /// </summary>
        [Fact]
        public void Default_Options_Write_The_1000x300x300_Box_Shifted_Downstream()
        {
            string path = new GmshCfdMesher().EnsureTunnel(_workingDirectory);

            Assert.Equal(Path.Combine(_workingDirectory, "Static_Windtunnel.stl"), path);

            List<(Vector3 Normal, StlTriangle Triangle)> triangles = StlWriterTests.ReadBinaryStl(path);
            Assert.Equal(12, triangles.Count);

            (Vector3 min, Vector3 max) = BoundingBox(triangles);
            Assert.Equal(new Vector3(-300f, -150f, -150f), min);
            Assert.Equal(new Vector3(700f, 150f, 150f), max);
        }

        /// <summary>The previous box (600 x 300 x 300, centered) remains available via options.</summary>
        [Fact]
        public void The_Previous_Centred_600_Box_Is_Still_Available()
        {
            var mesher = new GmshCfdMesher(new GmshMesherOptions { TunnelSizeX = 600f, TunnelCenterX = 0f });

            (Vector3 min, Vector3 max) = BoundingBox(StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory)));
            Assert.Equal(new Vector3(-300f, -150f, -150f), min);
            Assert.Equal(new Vector3(300f, 150f, 150f), max);
        }

        /// <summary>Options matching the pre-2026-11-08 .geo output (regression baseline).</summary>
        private static GmshMesherOptions PreviousDefaults() => new GmshMesherOptions
        {
            BoundaryLayerMode = "Threshold",
            WakeRefinement = false,
            DetailBox = false,
            MeshSizeFromCurvature = false
        };

        [Fact]
        public void Cylinder_Shape_Is_Written_When_Selected()
        {
            GmshCfdMesher mesher = new GmshCfdMesher(new GmshMesherOptions
            {
                TunnelShape = "cylinder",
                TunnelCenterX = 0f,
                TunnelDiameter = 200f,
                TunnelLength = 800f,
                TunnelSegments = 16
            });

            List<(Vector3 Normal, StlTriangle Triangle)> triangles =
                StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory));

            Assert.Equal(4 * 16, triangles.Count);

            (Vector3 min, Vector3 max) = BoundingBox(triangles);
            Assert.Equal(-400f, min.X, 3);
            Assert.Equal(400f, max.X, 3);
            Assert.Equal(100f, max.Y, 3);
        }

        [Fact]
        public void Unknown_Shape_Falls_Back_To_The_Box()
        {
            GmshCfdMesher mesher = new GmshCfdMesher(new GmshMesherOptions { TunnelShape = "Sphere" });

            Assert.Equal(12, StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory)).Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData(null)]
        [InlineData("  BOX ")]
        public void Empty_Or_Differently_Spelled_Box_Is_Still_A_Box(string? shape)
        {
            GmshCfdMesher mesher = new GmshCfdMesher(new GmshMesherOptions { TunnelShape = shape! });

            Assert.Equal(12, StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory)).Count);
        }

        /// <summary>The tunnel is generated once and reused afterwards.</summary>
        [Fact]
        public void Tunnel_Is_Cached_Across_Calls()
        {
            GmshCfdMesher mesher = new GmshCfdMesher();

            string first = mesher.EnsureTunnel(_workingDirectory);
            DateTime written = File.GetLastWriteTimeUtc(first);

            string second = mesher.EnsureTunnel(_workingDirectory);

            Assert.Equal(first, second);
            Assert.Equal(written, File.GetLastWriteTimeUtc(second));
        }

        /// <summary>
        /// TODO-39: With factor 1.0 and new options disabled, the .geo is byte-identical
        /// to the previous version (regression). Since 2026-11-08, prism, wake, and
        /// detail box are defaults; the baseline is therefore created explicitly with the previous values.
        /// </summary>
        [Fact]
        public void WriteGeoScript_Previous_Options_And_Factor_1_Produces_Exact_Regression_Lines()
        {
            string path = Path.Combine(_workingDirectory, "regression.geo");
            var mesher = new GmshCfdMesher(PreviousDefaults());
            var config = SimulationConfig.CreateDefault();
            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f);

            string[] lines = File.ReadAllLines(path);
            string[] expected = new[]
            {
                "// --- GENERAL MESH SETTINGS ---",
                "Mesh.Optimize = 1;",
                "Mesh.OptimizeNetgen = 0;",
                "// A) Import wind tunnel",
                "Merge \"tunnel.stl\";",
                "tunnel_surfs[] = Surface{:};",
                "// B) Import part",
                "Merge \"model.stl\";",
                "model_surfs[] = Surface{:};",
                "model_surfs[] -= tunnel_surfs[];",
                "// C) Define fluid cavity",
                "Surface Loop(1) = {tunnel_surfs[]};",
                "Surface Loop(2) = {model_surfs[]};",
                "Volume(1) = {1, 2};",
                "// D) Physical SU2 Markers",
                "// SU2 marker name: Farfield -> MARKER_FAR",
                "Physical Surface(\"Farfield\") = {tunnel_surfs[]};",
                "// SU2 marker name: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                "Physical Surface(\"Wall\") = {model_surfs[]};",
                "// Physical Volume(\"Fluid\") = {1};",
                "Mesh.SaveAll = 1;",
                "// ==========================================",
                "// E) DISTANCE FIELD FOR BOUNDARY LAYER (NEAR-WALL)",
                "// ==========================================",
                "Field[1] = Distance;",
                "Field[1].SurfacesList = {model_surfs[]};",
                "Field[2] = Threshold;",
                "Field[2].InField = 1;",
                "Field[2].SizeMin = 1.2;",
                "Field[2].SizeMax = 120;",
                "Field[2].DistMin = 3;",
                "Field[2].DistMax = 40;",
                "Background Field = 2;",
                "Mesh.CharacteristicLengthExtendFromBoundary = 0;",
                $"General.NumThreads = {config.GmshCores};",
                "// H) Rescaling (mm -> m)",
                "Mesh.ScalingFactor = (1.0 / 1) * 0.001;"
            };

            Assert.Equal(expected, lines);
        }

        /// <summary>
        /// TODO-39: Mesh refinement factor scales SizeMin and SizeMax as well as wake box cell sizes.
        /// </summary>
        [Fact]
        public void WriteGeoScript_RefinementFactor_Scales_Sizes()
        {
            string path = Path.Combine(_workingDirectory, "refined.geo");
            var options = new GmshMesherOptions
            {
                WakeRefinement = true,
                WakeCellSize = 6.0f
            };
            var mesher = new GmshCfdMesher(options);
            var config = SimulationConfig.CreateDefault();
            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f, refinementFactor: 0.5f);

            string[] lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.Trim() == "Field[2].SizeMin = 0.6;");
            Assert.Contains(lines, l => l.Trim() == "Field[2].SizeMax = 60;");
            Assert.Contains(lines, l => l.Trim() == "Field[3].VIn = 3;");
            Assert.Contains(lines, l => l.Trim() == "Field[3].VOut = 60;");
        }

        /// <summary>
        /// TODO-39: With WakeRefinement enabled, Field[3] (Box) and Field[4] (Min) are inserted.
        /// </summary>
        [Fact]
        public void WriteGeoScript_WakeRefinement_Generates_WakeBox_And_MinField()
        {
            string path = Path.Combine(_workingDirectory, "wake.geo");
            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = "Threshold",
                DetailBox = false,
                WakeRefinement = true,
                WakeLengthFactor = 3.5f,
                WakeWidthFactor = 0.75f,
                WakeCellSize = 0.0f
            };
            var mesher = new GmshCfdMesher(options);
            var config = SimulationConfig.CreateDefault();
            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f, refinementFactor: 1.0f);

            string[] lines = File.ReadAllLines(path);
            Assert.Contains(lines, l => l.Trim() == "Field[3] = Box;");
            Assert.Contains(lines, l => l.Trim() == "Field[3].XMax = maxX + (3.5 * lenX);");
            Assert.Contains(lines, l => l.Trim() == "Field[3].YMin = minY - (0.75 * lenY);");
            Assert.Contains(lines, l => l.Trim() == "Field[3].YMax = maxY + (0.75 * lenY);");
            Assert.Contains(lines, l => l.Trim() == "Field[3].ZMin = minZ - (0.75 * lenZ);");
            Assert.Contains(lines, l => l.Trim() == "Field[3].ZMax = maxZ + (0.75 * lenZ);");
            Assert.Contains(lines, l => l.Trim() == "Field[3].VIn = (lenX / 6.0) * 1;");
            Assert.Contains(lines, l => l.Trim() == "Field[4] = Min;");
            Assert.Contains(lines, l => l.Trim() == "Field[4].FieldsList = {2, 3};");
            Assert.Contains(lines, l => l.Trim() == "Background Field = 4;");
        }

        /// <summary>
        /// TODO-39: MeshSizeFromCurvature inserts Mesh.MeshSizeFromCurvature = 1.
        /// </summary>
        [Fact]
        public void WriteGeoScript_MeshSizeFromCurvature_Adds_Curvature_Option()
        {
            string path = Path.Combine(_workingDirectory, "curvature.geo");
            var options = PreviousDefaults();
            options.MeshSizeFromCurvature = true;
            var mesher = new GmshCfdMesher(options);
            var config = SimulationConfig.CreateDefault();
            mesher.WriteGeoScript(path, "model.stl", "tunnel.stl", config, 1.0f);

            string[] lines = File.ReadAllLines(path);
            int idx = Array.FindIndex(lines, l => l.Trim() == "Mesh.MeshSizeFromCurvature = 1;");
            Assert.True(idx >= 0);
            Assert.Equal("Mesh.CharacteristicLengthExtendFromBoundary = 0;", lines[idx - 1].Trim());
        }

        /// <summary>
        /// TODO-39: MeshRequest builds the BaseSuffix with an optional tag.
        /// </summary>
        [Fact]
        public void MeshRequest_BaseSuffix_Formats_With_And_Without_Tag()
        {
            var config = SimulationConfig.CreateDefault();
            var reqWithoutTag = new MeshRequest("model.stl", 1, 2, config, 1.0f, _workingDirectory);
            Assert.Equal("Gen1_Var2", reqWithoutTag.BaseSuffix);

            var reqWithTag = new MeshRequest("model.stl", 1, 1, config, 1.0f, _workingDirectory, Tag: "Cal3");
            Assert.Equal("Gen1_Var1_Cal3", reqWithTag.BaseSuffix);

            var reqWithWhitespaceTag = new MeshRequest("model.stl", 2, 4, config, 1.0f, _workingDirectory, Tag: "  Verif  ");
            Assert.Equal("Gen2_Var4_Verif", reqWithWhitespaceTag.BaseSuffix);
        }

        /// <summary>
        /// TODO-39: MeshMetrics.ReadSu2CellCount reads NELEM from SU2 headers.
        /// </summary>
        [Fact]
        public void MeshMetrics_ReadSu2CellCount_Parses_Cell_Count_From_Su2_Header()
        {
            string path = Path.Combine(_workingDirectory, "mesh.su2");
            File.WriteAllLines(path, new[]
            {
                "NDIME= 3",
                "NELEM= 48120",
                "NPOIN= 9200"
            });

            int cellCount = MeshMetrics.ReadSu2CellCount(path);
            Assert.Equal(48120, cellCount);
        }

        [Fact]
        public void MeshMetrics_ReadSu2CellCount_Returns_Zero_When_File_Missing_Or_Empty()
        {
            Assert.Equal(0, MeshMetrics.ReadSu2CellCount("non_existent_file.su2"));

            string emptyPath = Path.Combine(_workingDirectory, "empty.su2");
            File.WriteAllText(emptyPath, "");
            Assert.Equal(0, MeshMetrics.ReadSu2CellCount(emptyPath));
        }

        private static (Vector3 Min, Vector3 Max) BoundingBox(
            List<(Vector3 Normal, StlTriangle Triangle)> triangles)
        {
            Vector3 min = new Vector3(float.MaxValue);
            Vector3 max = new Vector3(float.MinValue);

            foreach ((Vector3 _, StlTriangle t) in triangles)
            {
                foreach (Vector3 v in new[] { t.A, t.B, t.C })
                {
                    min = Vector3.Min(min, v);
                    max = Vector3.Max(max, v);
                }
            }

            return (min, max);
        }
    }
}
