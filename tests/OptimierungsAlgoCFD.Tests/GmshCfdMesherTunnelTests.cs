using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Xunit;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Projects.MantaAuv;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// <c>EnsureTunnel</c> ist der Teil des Meshers, der ohne Gmsh läuft — und seit
    /// TODO-11 auch ohne PicoGK. Diese Tests decken den Weg von den JSON-Optionen bis
    /// zur geschriebenen Datei ab.
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
        /// Standardfall seit 08.11.2026: Quader 1000 × 300 × 300 mm, Mitte 200 mm stromab
        /// (6 L davor, 14 L Nachlauf bei 50 mm Modell), 12 Dreiecke.
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

        /// <summary>Der frühere Quader (600 × 300 × 300, zentriert) bleibt über die Optionen erreichbar.</summary>
        [Fact]
        public void The_Previous_Centred_600_Box_Is_Still_Available()
        {
            var mesher = new GmshCfdMesher(new GmshMesherOptions { TunnelSizeX = 600f, TunnelCenterX = 0f });

            (Vector3 min, Vector3 max) = BoundingBox(StlWriterTests.ReadBinaryStl(mesher.EnsureTunnel(_workingDirectory)));
            Assert.Equal(new Vector3(-300f, -150f, -150f), min);
            Assert.Equal(new Vector3(300f, 150f, 150f), max);
        }

        /// <summary>Optionen, mit denen das .geo dem Stand vor dem 08.11.2026 entspricht (Regressions-Basis).</summary>
        private static GmshMesherOptions PreviousDefaults() => new GmshMesherOptions
        {
            BoundaryLayerMode = "Threshold",
            WakeRefinement = false,
            DetailBox = false
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

        /// <summary>Der Tunnel wird einmal erzeugt und danach wiederverwendet.</summary>
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
        /// TODO-39: Mit Faktor 1,0 und ausgeschalteten neuen Optionen ist das .geo byteweise
        /// identisch zur vorherigen Version (Regression). Seit 08.11.2026 sind Prism, Nachlauf- und
        /// Detailbox Standard; die Basis wird deshalb mit den früheren Werten explizit erzeugt.
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
                "// --- ALLGEMEINE NETZ-EINSTELLUNGEN ---",
                "Mesh.Optimize = 1;",
                "Mesh.OptimizeNetgen = 0;",
                "// A) Windkanal importieren",
                "Merge \"tunnel.stl\";",
                "tunnel_surfs[] = Surface{:};",
                "// B) Bauteil importieren",
                "Merge \"model.stl\";",
                "model_surfs[] = Surface{:};",
                "model_surfs[] -= tunnel_surfs[];",
                "// C) Hohlraum definieren",
                "Surface Loop(1) = {tunnel_surfs[]};",
                "Surface Loop(2) = {model_surfs[]};",
                "Volume(1) = {1, 2};",
                "// D) Physische SU2 Marker",
                "// Dieser Name ist der SU2-Marker: Farfield -> MARKER_FAR",
                "Physical Surface(\"Farfield\") = {tunnel_surfs[]};",
                "// Dieser Name ist der SU2-Marker: Wall -> MARKER_HEATFLUX, MARKER_MONITORING, MARKER_PLOTTING",
                "Physical Surface(\"Wall\") = {model_surfs[]};",
                "// Physical Volume(\"Fluid\") = {1};",
                "Mesh.SaveAll = 1;",
                "// ==========================================",
                "// E) DISTANZFELD FUER GRENZSCHICHT (WANDNAEHE)",
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
                "// H) Re-Skalierung",
                "Mesh.ScalingFactor = (1.0 / 1) * 0.001;"
            };

            Assert.Equal(expected, lines);
        }

        /// <summary>
        /// TODO-39: Netzverfeinerungsfaktor skaliert SizeMin und SizeMax sowie Wake-Box-Zellgrößen.
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
        /// TODO-39: Bei aktiver Wake-Refinement-Option werden Field[3] (Box) und Field[4] (Min) eingefügt.
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
        /// TODO-39: MeshSizeFromCurvature fügt Mesh.MeshSizeFromCurvature = 1 ein.
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
        /// TODO-39: MeshRequest bildet den BaseSuffix mit optionalem Tag.
        /// </summary>
        [Fact]
        public void MeshRequest_BaseSuffix_Formats_With_And_Without_Tag()
        {
            var config = SimulationConfig.CreateDefault();
            var reqWithoutTag = new MeshRequest("model.stl", 1, 2, config, 1.0f, _workingDirectory);
            Assert.Equal("Gen1_Var2", reqWithoutTag.BaseSuffix);

            var reqWithTag = new MeshRequest("model.stl", 1, 1, config, 1.0f, _workingDirectory, Tag: "Kal3");
            Assert.Equal("Gen1_Var1_Kal3", reqWithTag.BaseSuffix);

            var reqWithWhitespaceTag = new MeshRequest("model.stl", 2, 4, config, 1.0f, _workingDirectory, Tag: "  Verif  ");
            Assert.Equal("Gen2_Var4_Verif", reqWithWhitespaceTag.BaseSuffix);
        }

        /// <summary>
        /// TODO-39: MeshMetrics.ReadSu2CellCount liest NELEM aus SU2-Kopfzeilen.
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
