using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Text.RegularExpressions;
using Xunit;
using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Solvers.Cfd;

namespace OptimierungsAlgoCFD.Tests
{
    /// <summary>
    /// Tests für AP-2 (ScaleFactor-Konsistenz in <see cref="GmshCfdMesher"/>) und
    /// AP-3 (physikalischer Windkanal entkoppelt von <c>scaleFactor</c> + Mindestabstände &amp; Blockage).
    /// </summary>
    public class ScaleFactorAndTunnelGuaranteeTests : IDisposable
    {
        private readonly string _dir;

        public ScaleFactorAndTunnelGuaranteeTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "ap23_tests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_dir);
        }

        public void Dispose()
        {
            if (Directory.Exists(_dir))
            {
                try { Directory.Delete(_dir, recursive: true); } catch { }
            }
        }

        // ===================================================================
        //  AP-2: ScaleFactor-Konsistenz in .geo-Skripten (Prism, Hexa, Threshold)
        // ===================================================================

        [Theory]
        [InlineData("Prism", 1.0f)]
        [InlineData("Prism", 0.5f)]
        [InlineData("Prism", 0.35f)]
        [InlineData("Prism", 0.42f)]
        [InlineData("Prism", 2.0f)]
        [InlineData("Hexa", 1.0f)]
        [InlineData("Hexa", 0.5f)]
        [InlineData("Hexa", 0.35f)]
        [InlineData("Hexa", 2.0f)]
        [InlineData("Threshold", 1.0f)]
        [InlineData("Threshold", 0.5f)]
        [InlineData("Threshold", 0.35f)]
        [InlineData("Threshold", 0.42f)]
        [InlineData("Threshold", 2.0f)]
        public void WriteGeoScript_ExplicitSizes_Scale_Linearly_With_ScaleFactor_In_All_Modes(string mode, float scaleFactor)
        {
            string runDir = Path.Combine(_dir, $"{mode}_{scaleFactor.ToString(CultureInfo.InvariantCulture)}");
            Directory.CreateDirectory(runDir);

            // Realer Körper 120 x 40 x 20 mm, in den PicoGK-Arbeitsraum mit scaleFactor skaliert
            string modelPath = Path.Combine(runDir, "model.stl");
            IReadOnlyList<StlTriangle> realBody = TunnelGeometry.CreateBox(new Vector3(120f, 40f, 20f), Vector3.Zero);
            IReadOnlyList<StlTriangle> picoBody = ScaleTriangles(realBody, scaleFactor);
            StlWriter.WriteBinary(modelPath, picoBody);

            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = mode,
                BoundaryLayerSizeMin = 1.2f,
                BoundaryLayerSizeMax = 120.0f,
                BoundaryLayerDistMin = 3.0f,
                BoundaryLayerDistMax = 40.0f,
                SurfaceCellSize = 0.8f,
                WakeRefinement = true,
                WakeCellSize = 6.0f,
                DetailBox = true,
                DetailBoxCellSize = 2.0f,
            };
            float refinementFactor = 0.5f;
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var mesher = new GmshCfdMesher(options, flow);
            var config = SimulationConfig.CreateDefault();

            string geoPath = Path.Combine(runDir, "Meshing_Test.geo");
            mesher.WriteGeoScript(geoPath, modelPath, "tunnel.stl", config, scaleFactor, refinementFactor);

            string[] geoLines = File.ReadAllLines(geoPath);

            // Real erwartete Zielwerte (mm * refinementFactor für Zellgrößen, mm für Distanzen)
            float expectedSizeMax = 120.0f * refinementFactor;
            float expectedDistMin = 3.0f;
            float expectedDistMax = 40.0f;
            float expectedWakeIn = 6.0f * refinementFactor;
            float expectedDetailIn = 2.0f * refinementFactor;
            float expectedDetailThickness = Math.Max(expectedDetailIn * 5.0f, 1.0f);

            if (mode == "Threshold")
            {
                float expectedSizeMin = 1.2f * refinementFactor;
                Assert.Equal(expectedSizeMin, ParseGeoValue(geoLines, "Field[2].SizeMin") / scaleFactor, 3);
            }
            else
            {
                float expectedSurface = 0.8f * refinementFactor;
                Assert.Equal(expectedSurface, ParseGeoValue(geoLines, "Field[2].SizeMin") / scaleFactor, 3);
                Assert.Equal(expectedSizeMax, ParseGeoValue(geoLines, "Mesh.MeshSizeMax") / scaleFactor, 2);

                string wallPath = GmshCfdMesher.WallScriptPath(geoPath);
                string[] wallLines = File.ReadAllLines(wallPath);
                Assert.Equal(expectedSurface, ParseGeoValue(wallLines, "Mesh.MeshSizeMax") / scaleFactor, 3);
                Assert.Equal(expectedSurface * 0.5f, ParseGeoValue(wallLines, "Mesh.MeshSizeMin") / scaleFactor, 3);

                // Extrude-Schichthöhen bleiben ebenfalls invariant in realen mm
                string extrudeLine = geoLines.Single(l => l.StartsWith("Extrude {"));
                float firstLayerGeo = ParseFirstExtrudeHeight(extrudeLine);
                BoundaryLayerSpec refSpec = BoundaryLayerCalculator.Calculate(
                    flow.Density, flow.Velocity, flow.DynamicViscosity, 120.0f,
                    options.TargetYPlus, options.BoundaryLayerGrowthRate,
                    options.BoundaryLayerMinLayers, options.BoundaryLayerLayers, options.BoundaryLayerCoverage);
                Assert.Equal(refSpec.FirstLayerHeightMm, firstLayerGeo / scaleFactor, 5);
            }

            Assert.Equal(expectedSizeMax, ParseGeoValue(geoLines, "Field[2].SizeMax") / scaleFactor, 2);
            Assert.Equal(expectedDistMin, ParseGeoValue(geoLines, "Field[2].DistMin") / scaleFactor, 3);
            Assert.Equal(expectedDistMax, ParseGeoValue(geoLines, "Field[2].DistMax") / scaleFactor, 2);
            Assert.Equal(expectedWakeIn, ParseGeoValue(geoLines, "Field[3].VIn") / scaleFactor, 3);
            Assert.Equal(expectedSizeMax, ParseGeoValue(geoLines, "Field[3].VOut") / scaleFactor, 2);
            Assert.Equal(expectedDetailIn, ParseGeoValue(geoLines, "Field[5].VIn") / scaleFactor, 3);
            Assert.Equal(expectedSizeMax, ParseGeoValue(geoLines, "Field[5].VOut") / scaleFactor, 2);
            Assert.Equal(expectedDetailThickness, ParseGeoValue(geoLines, "Field[5].Thickness") / scaleFactor, 3);

            // Rückskalierung auf echte Meter stimmt exakt
            string expectedScaleString = scaleFactor.ToString(CultureInfo.InvariantCulture);
            Assert.Contains(geoLines, l => l.Trim() == $"Mesh.ScalingFactor = (1.0 / {expectedScaleString}) * 0.001;");
        }

        [Theory]
        [InlineData("Prism")]
        [InlineData("Threshold")]
        public void WriteGeoScript_AutomaticFallbacks_Do_Not_Double_Scale(string mode)
        {
            // SurfaceCellSize = 0 (Fallback BoundaryLayerSizeMin), WakeCellSize = 0 (lenX/6), DetailBoxCellSize = 0 (2.5 * surfaceSize)
            // plus kleiner Zellwert (< 0.2), sodass der Thickness-Floor (1.0f * safeScale) greift.
            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = mode,
                BoundaryLayerSizeMin = 0.1f,
                BoundaryLayerSizeMax = 80.0f,
                SurfaceCellSize = 0.0f,
                WakeRefinement = true,
                WakeCellSize = 0.0f,
                DetailBox = true,
                DetailBoxCellSize = 0.0f
            };
            var flow = new FlowConditions(2.0f, 1025f, 0.001001f, "test");
            var mesher = new GmshCfdMesher(options, flow);
            var config = SimulationConfig.CreateDefault();

            string geo1 = Path.Combine(_dir, $"{mode}_1.geo");
            string geoScaled = Path.Combine(_dir, $"{mode}_scaled.geo");
            string stl1 = Path.Combine(_dir, $"{mode}_1.stl");
            string stlScaled = Path.Combine(_dir, $"{mode}_scaled.stl");

            IReadOnlyList<StlTriangle> realBody = TunnelGeometry.CreateBox(new Vector3(100f, 30f, 15f), Vector3.Zero);
            float scale = 0.42f;
            float factor = 0.5f;
            StlWriter.WriteBinary(stl1, realBody);
            StlWriter.WriteBinary(stlScaled, ScaleTriangles(realBody, scale));

            mesher.WriteGeoScript(geo1, stl1, "tunnel.stl", config, 1.0f, factor);
            mesher.WriteGeoScript(geoScaled, stlScaled, "tunnel.stl", config, scale, factor);

            string[] lines1 = File.ReadAllLines(geo1);
            string[] linesScaled = File.ReadAllLines(geoScaled);

            Assert.Equal(
                ParseGeoValue(lines1, "Field[2].SizeMin"),
                ParseGeoValue(linesScaled, "Field[2].SizeMin") / scale,
                4);
            Assert.Equal(
                ParseGeoValue(lines1, "Field[5].VIn"),
                ParseGeoValue(linesScaled, "Field[5].VIn") / scale,
                4);
            // DetailBox VIn = 2.5 * 0.1 * 0.5 = 0.125 mm -> 5 * 0.125 = 0.625 < 1.0 -> Floor 1.0 mm * scale!
            Assert.Equal(1.0f, ParseGeoValue(lines1, "Field[5].Thickness"), 4);
            Assert.Equal(1.0f, ParseGeoValue(linesScaled, "Field[5].Thickness") / scale, 4);

            // WakeCellSize == 0 verwendet (lenX / 6.0) * factor im PicoGK-Raum (kein Doppel-scale)
            Assert.Contains(linesScaled, l => l.Trim() == "Field[3].VIn = (lenX / 6.0) * 0.5;");
        }

        // ===================================================================
        //  AP-3: StlWriter.ReadTriangles Round-Trip & Robustheit
        // ===================================================================

        [Fact]
        public void StlWriter_ReadTriangles_RoundTrip_And_EdgeCases()
        {
            string stlPath = Path.Combine(_dir, "roundtrip.stl");
            IReadOnlyList<StlTriangle> expected = TunnelGeometry.CreateCylinder(40f, 120f, new Vector3(10f, -5f, 3f), 16);
            StlWriter.WriteBinary(stlPath, expected);

            IReadOnlyList<StlTriangle> actual = StlWriter.ReadTriangles(stlPath);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.Equal(expected[i].A, actual[i].A);
                Assert.Equal(expected[i].B, actual[i].B);
                Assert.Equal(expected[i].C, actual[i].C);
            }

            // Leeres STL -> leere Liste
            string emptyPath = Path.Combine(_dir, "empty.stl");
            StlWriter.WriteBinary(emptyPath, Array.Empty<StlTriangle>());
            Assert.Empty(StlWriter.ReadTriangles(emptyPath));

            // Beschädigte/abgeschnittene Datei -> leere Liste
            string corruptPath = Path.Combine(_dir, "corrupt.stl");
            File.WriteAllBytes(corruptPath, new byte[40]);
            Assert.Empty(StlWriter.ReadTriangles(corruptPath));

            // Null/leer oder fehlende Datei -> gleiche Exceptions wie ReadBounds
            Assert.Throws<ArgumentException>(() => StlWriter.ReadTriangles(""));
            Assert.Throws<FileNotFoundException>(() => StlWriter.ReadTriangles(Path.Combine(_dir, "missing.stl")));
        }

        // ===================================================================
        //  AP-3: Physikalischer Windkanal entkoppelt von scaleFactor + Garantien
        // ===================================================================

        [Theory]
        [InlineData("Box", 1.0f)]
        [InlineData("Box", 0.5f)]
        [InlineData("Box", 0.42f)]
        [InlineData("Box", 0.35f)]
        [InlineData("Box", 2.0f)]
        [InlineData("Cylinder", 1.0f)]
        [InlineData("Cylinder", 0.5f)]
        [InlineData("Cylinder", 0.35f)]
        [InlineData("Cylinder", 2.0f)]
        public void EnsureTunnel_WithModelBounds_Produces_Identical_Real_Dimensions_Independent_Of_ScaleFactor(
            string shape, float scaleFactor)
        {
            string runDir = Path.Combine(_dir, $"tunnel_{shape}_{scaleFactor.ToString(CultureInfo.InvariantCulture)}");
            Directory.CreateDirectory(runDir);

            // Manta-ähnlicher Körper in realen mm: L=118 mm, B=96 mm, H=24 mm, zentriert um (0,0,0)
            Vector3 realMin = new Vector3(-59f, -48f, -12f);
            Vector3 realMax = new Vector3(59f, 48f, 12f);
            float realFrontalMm2 = 1450.0f;

            var options = new GmshMesherOptions
            {
                TunnelShape = shape,
                TunnelSizeX = 1000f,
                TunnelSizeY = 300f,
                TunnelSizeZ = 300f,
                TunnelDiameter = 300f,
                TunnelLength = 1000f,
                TunnelCenterX = 200f,
                TunnelMinUpstreamFactor = 5.0f,
                TunnelMinDownstreamFactor = 15.0f,
                TunnelMinLateralFactor = 5.0f,
                TunnelMaxBlockagePercent = 0.3f,
            };

            var refMesher = new GmshCfdMesher(options);
            string refPath = refMesher.EnsureTunnel(runDir, new MeshBounds(realMin, realMax), 1.0f, realFrontalMm2);
            MeshBounds refTunnelReal = StlWriter.ReadBounds(refPath);

            var scaledMesher = new GmshCfdMesher(options);
            var scaledModelBounds = new MeshBounds(realMin * scaleFactor, realMax * scaleFactor);
            string scaledPath = scaledMesher.EnsureTunnel(runDir, scaledModelBounds, scaleFactor, realFrontalMm2);
            MeshBounds scaledTunnelPico = StlWriter.ReadBounds(scaledPath);

            Vector3 recoveredMin = scaledTunnelPico.Min / scaleFactor;
            Vector3 recoveredMax = scaledTunnelPico.Max / scaleFactor;

            Assert.Equal(refTunnelReal.Min.X, recoveredMin.X, 2);
            Assert.Equal(refTunnelReal.Min.Y, recoveredMin.Y, 2);
            Assert.Equal(refTunnelReal.Min.Z, recoveredMin.Z, 2);
            Assert.Equal(refTunnelReal.Max.X, recoveredMax.X, 2);
            Assert.Equal(refTunnelReal.Max.Y, recoveredMax.Y, 2);
            Assert.Equal(refTunnelReal.Max.Z, recoveredMax.Z, 2);

            // Telemetrie stimmt unabhängig vom scaleFactor überein
            Assert.Equal(refMesher.LastMeshMetrics["TunnelBlockagePercent"], scaledMesher.LastMeshMetrics["TunnelBlockagePercent"], 4);
            Assert.Equal(refMesher.LastMeshMetrics["TunnelLengthMm"], scaledMesher.LastMeshMetrics["TunnelLengthMm"], 2);
            Assert.Equal(refMesher.LastMeshMetrics["TunnelWidthMm"], scaledMesher.LastMeshMetrics["TunnelWidthMm"], 2);
            Assert.Equal(refMesher.LastMeshMetrics["TunnelHeightMm"], scaledMesher.LastMeshMetrics["TunnelHeightMm"], 2);
            Assert.Equal(refMesher.LastMeshMetrics["TunnelUpstreamL"], scaledMesher.LastMeshMetrics["TunnelUpstreamL"], 4);
            Assert.Equal(refMesher.LastMeshMetrics["TunnelDownstreamL"], scaledMesher.LastMeshMetrics["TunnelDownstreamL"], 4);
        }

        [Theory]
        [InlineData("Box")]
        [InlineData("Cylinder")]
        public void EnsureTunnel_AutoExpands_To_Guarantee_Clearances_And_MaxBlockage(string shape)
        {
            // Absichtlich viel zu kleiner konfigurierter Kanal: 200 x 80 x 80 mm
            var options = new GmshMesherOptions
            {
                TunnelShape = shape,
                TunnelSizeX = 200f,
                TunnelSizeY = 80f,
                TunnelSizeZ = 80f,
                TunnelDiameter = 80f,
                TunnelLength = 200f,
                TunnelCenterX = 0f,
                TunnelCenterY = 0f,
                TunnelCenterZ = 0f,
                TunnelSegments = 64,
                TunnelMinUpstreamFactor = 5.0f,
                TunnelMinDownstreamFactor = 15.0f,
                TunnelMinLateralFactor = 5.0f,
                TunnelMaxBlockagePercent = 0.3f,
            };

            // Asymmetrisch verschobener Körper: L=100, B=60, H=20
            Vector3 realMin = new Vector3(-20f, -30f, -10f);
            Vector3 realMax = new Vector3(80f, 30f, 10f);
            float scale = 0.5f;
            var modelBoundsPico = new MeshBounds(realMin * scale, realMax * scale);

            var mesher = new GmshCfdMesher(options);
            string tunnelPath = mesher.EnsureTunnel(_dir, modelBoundsPico, scale);

            MeshBounds tunnelPico = StlWriter.ReadBounds(tunnelPath);
            Vector3 tunnelMin = tunnelPico.Min / scale;
            Vector3 tunnelMax = tunnelPico.Max / scale;

            float modelL = 100f;
            float modelB = 60f;
            float modelH = 20f;

            // >= 5 L stromauf, >= 15 L stromab
            float upstreamClearanceL = (realMin.X - tunnelMin.X) / modelL;
            float downstreamClearanceL = (tunnelMax.X - realMax.X) / modelL;
            Assert.True(upstreamClearanceL >= 5.0f - 1e-4f, $"Upstream {upstreamClearanceL}L < 5L");
            Assert.True(downstreamClearanceL >= 15.0f - 1e-4f, $"Downstream {downstreamClearanceL}L < 15L");

            // >= 5 B in ±Y, >= 5 H in ±Z (auch bei Cylinder mindestens für alle Box-Ecken)
            float clearMinusY = (realMin.Y - tunnelMin.Y) / modelB;
            float clearPlusY = (tunnelMax.Y - realMax.Y) / modelB;
            float clearMinusZ = (realMin.Z - tunnelMin.Z) / modelH;
            float clearPlusZ = (tunnelMax.Z - realMax.Z) / modelH;
            Assert.True(clearMinusY >= 5.0f - 1e-4f, $"-Y {clearMinusY}B < 5B");
            Assert.True(clearPlusY >= 5.0f - 1e-4f, $"+Y {clearPlusY}B < 5B");
            Assert.True(clearMinusZ >= 5.0f - 1e-4f, $"-Z {clearMinusZ}H < 5H");
            Assert.True(clearPlusZ >= 5.0f - 1e-4f, $"+Z {clearPlusZ}H < 5H");

            if (shape == "Cylinder")
            {
                // Jede der 4 Ecken des erforderlichen Lateral-Rechtecks liegt innerhalb des Zylinderradius
                float radius = mesher.LastMeshMetrics["TunnelWidthMm"] * 0.5f;
                float reqMinY = realMin.Y - 5.0f * modelB;
                float reqMaxY = realMax.Y + 5.0f * modelB;
                float reqMinZ = realMin.Z - 5.0f * modelH;
                float reqMaxZ = realMax.Z + 5.0f * modelH;
                foreach (float y in new[] { reqMinY, reqMaxY })
                foreach (float z in new[] { reqMinZ, reqMaxZ })
                {
                    float dist = MathF.Sqrt(y * y + z * z);
                    Assert.True(dist <= radius + 1e-3f, $"Ecke ({y}, {z}) Abstand {dist} > Radius {radius}");
                }
            }

            // Blockage <= 0.3 %
            Assert.True(mesher.LastMeshMetrics["TunnelBlockagePercent"] <= 0.3f,
                $"Blockage {mesher.LastMeshMetrics["TunnelBlockagePercent"]} % > 0.3 %");
            Assert.True(mesher.LastMeshMetrics["TunnelUpstreamL"] >= 5.0f - 1e-4f);
            Assert.True(mesher.LastMeshMetrics["TunnelDownstreamL"] >= 15.0f - 1e-4f);

            // Alle Tunnel-Telemetrie-Schlüssel zählen als Netz-Metadaten (nicht als Geometrie-Kennwert)
            foreach (string key in new[]
            {
                "TunnelBlockagePercent", "TunnelLengthMm", "TunnelWidthMm",
                "TunnelHeightMm", "TunnelUpstreamL", "TunnelDownstreamL"
            })
            {
                Assert.True(mesher.LastMeshMetrics.ContainsKey(key), $"Fehlender Key {key}");
                Assert.True(ModelRecord.IsMeshMetadataKey(key), $"{key} muss IsMeshMetadataKey=true liefern.");
            }
        }

        [Fact]
        public void EnsureTunnel_LogTunnelSummary_Formats_Clearance_With_Decimal_Places()
        {
            var options = new GmshMesherOptions
            {
                TunnelShape = "Box",
                TunnelSizeX = 200f,
                TunnelSizeY = 80f,
                TunnelSizeZ = 80f,
                TunnelCenterX = 0f,
                TunnelCenterY = 0f,
                TunnelCenterZ = 0f,
                TunnelMinUpstreamFactor = 5.0f,
                TunnelMinDownstreamFactor = 15.0f,
                TunnelMinLateralFactor = 5.0f,
                TunnelMaxBlockagePercent = 0.3f,
            };

            float scaleFactor = 0.4f;
            var modelBoundsPico = new MeshBounds(new Vector3(-25f, -10f, -10f) * scaleFactor, new Vector3(25f, 10f, 10f) * scaleFactor);
            var mesher = new GmshCfdMesher(options);

            using var sw = new StringWriter();
            TextWriter originalOut = Console.Out;
            Console.SetOut(sw);
            try
            {
                mesher.EnsureTunnel(_dir, modelBoundsPico, scaleFactor: scaleFactor);
            }
            finally
            {
                Console.SetOut(originalOut);
            }

            string logOutput = sw.ToString();
            Assert.Contains("5.0L vor / 15.0L nach", logOutput);
        }

        [Theory]
        [InlineData("Box")]
        [InlineData("Cylinder")]
        public void EnsureTunnel_Never_Shrinks_An_Already_Larger_Tunnel(string shape)
        {
            var options = new GmshMesherOptions
            {
                TunnelShape = shape,
                TunnelSizeX = 5000f,
                TunnelSizeY = 3000f,
                TunnelSizeZ = 3000f,
                TunnelDiameter = 3000f,
                TunnelLength = 5000f,
                TunnelCenterX = 500f,
                TunnelCenterY = 0f,
                TunnelCenterZ = 0f,
                TunnelMinUpstreamFactor = 5.0f,
                TunnelMinDownstreamFactor = 15.0f,
                TunnelMinLateralFactor = 5.0f,
                TunnelMaxBlockagePercent = 0.3f,
            };

            // Kleines Modell (20 x 10 x 5 mm): Blockage < 0.001 %, Abstände > 100 L -> keine Aufweitung, keine Verkleinerung
            var modelBounds = new MeshBounds(new Vector3(-10f, -5f, -2.5f), new Vector3(10f, 5f, 2.5f));
            var mesher = new GmshCfdMesher(options);

            string path = mesher.EnsureTunnel(_dir, modelBounds, scaleFactor: 1.0f);
            Assert.Equal(Path.Combine(_dir, "Static_Windtunnel.stl"), path);

            MeshBounds actual = StlWriter.ReadBounds(path);
            Assert.Equal(-2000f, actual.Min.X, 2);
            Assert.Equal(3000f, actual.Max.X, 2);
            Assert.Equal(-1500f, actual.Min.Y, 2);
            Assert.Equal(1500f, actual.Max.Y, 2);
            Assert.Equal(5000f, mesher.LastMeshMetrics["TunnelLengthMm"], 2);
            Assert.Equal(3000f, mesher.LastMeshMetrics["TunnelWidthMm"], 2);
        }

        [Fact]
        public void EnsureTunnel_BlockageExpansion_Can_Be_Disabled_Via_NonPositive_Setting()
        {
            var options = new GmshMesherOptions
            {
                TunnelShape = "Box",
                TunnelSizeX = 1000f,
                TunnelSizeY = 300f,
                TunnelSizeZ = 300f,
                TunnelCenterX = 200f,
                TunnelMinUpstreamFactor = 5.0f,
                TunnelMinDownstreamFactor = 15.0f,
                TunnelMinLateralFactor = 5.0f,
                TunnelMaxBlockagePercent = 0.0f // deaktiviert
            };

            // Modell 50 x 40 x 40 mm: Lateral 5*40=200 je Seite (+40 = 440 mm), aber ohne Blockage-Aufweitung bliebe es bei 440x440 mm
            // (Blockage = 1600 / 193600 = 0.826 % > 0.3 %, wird wegen <= 0 nicht aufgeweitet)
            var modelBounds = new MeshBounds(new Vector3(-25f, -20f, -20f), new Vector3(25f, 20f, 20f));
            var mesher = new GmshCfdMesher(options);
            TunnelSpec spec = mesher.ResolveEffectiveTunnel(modelBounds, 1.0f);

            Assert.False(spec.WasExpandedForBlockage);
            Assert.Equal(440f, spec.SizeY, 2);
            Assert.Equal(440f, spec.SizeZ, 2);
            Assert.True(spec.BlockagePercent > 0.3f);
        }

        [Fact]
        public void EnsureTunnel_Caching_Uses_Deterministic_Hash_And_Reuses_Existing_File()
        {
            var options = new GmshMesherOptions { TunnelShape = "Box" };
            var mesher = new GmshCfdMesher(options);
            var modelBounds = new MeshBounds(new Vector3(-30f, -20f, -10f), new Vector3(30f, 20f, 10f));

            // scale != 1.0 -> Hash-Dateiname statt Static_Windtunnel.stl
            string first = mesher.EnsureTunnel(_dir, modelBounds, 0.5f, 1200f);
            Assert.NotEqual("Static_Windtunnel.stl", Path.GetFileName(first));
            Assert.StartsWith("Static_Windtunnel_", Path.GetFileName(first));
            DateTime firstWrite = File.GetLastWriteTimeUtc(first);

            string second = mesher.EnsureTunnel(_dir, modelBounds, 0.5f, 1200f);
            Assert.Equal(first, second);
            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(second));

            // Anderer scaleFactor erzeugt eigenen Hash-Dateinamen und überschreibt den ersten nicht
            string otherScale = mesher.EnsureTunnel(_dir, modelBounds, 0.35f, 1200f);
            Assert.NotEqual(first, otherScale);
            Assert.True(File.Exists(first));
            Assert.True(File.Exists(otherScale));
        }

        [Fact]
        public void GenerateMesh_Reads_ModelStl_For_Tunnel_And_Writes_Consistent_Geo_Before_Gmsh()
        {
            var options = new GmshMesherOptions
            {
                BoundaryLayerMode = "Threshold",
                BoundaryLayerSizeMin = 1.0f,
                BoundaryLayerSizeMax = 100.0f,
                WakeRefinement = true,
                WakeCellSize = 5.0f,
                DetailBox = true,
                DetailBoxCellSize = 2.0f
            };
            var mesher = new GmshCfdMesher(options);
            var config = SimulationConfig.CreateDefault();
            config.GmshPath = "non_existent_gmsh_binary_12345";

            // Reales Modell 100 x 50 x 30 mm, skaliert mit 0.5 -> 50 x 25 x 15 im STL
            float scale = 0.5f;
            string stlPath = Path.Combine(_dir, "scaled_body.stl");
            StlWriter.WriteBinary(stlPath, TunnelGeometry.CreateBox(new Vector3(50f, 25f, 15f), Vector3.Zero));

            var request = new MeshRequest(stlPath, 1, 1, config, scale, _dir, RefinementFactor: 1.0f);

            // GenerateMesh schlägt erst beim Aufruf der nicht existierenden Gmsh-Binary fehl;
            // davor wurden Tunnel-STL (mit Blockage <= 0.3 % & Abständen) und .geo geschrieben.
            Assert.ThrowsAny<Exception>(() => mesher.GenerateMesh(request));

            Assert.True(mesher.LastMeshMetrics["TunnelBlockagePercent"] <= 0.3f);
            Assert.True(mesher.LastMeshMetrics["TunnelUpstreamL"] >= 5.0f - 1e-4f);
            Assert.True(mesher.LastMeshMetrics["TunnelDownstreamL"] >= 15.0f - 1e-4f);

            string geoPath = Path.Combine(_dir, "Meshing_Gen1_Var1.geo");
            Assert.True(File.Exists(geoPath));
            string[] geoLines = File.ReadAllLines(geoPath);
            Assert.Equal(1.0f, ParseGeoValue(geoLines, "Field[2].SizeMin") / scale, 3);
            Assert.Equal(100.0f, ParseGeoValue(geoLines, "Field[2].SizeMax") / scale, 2);
            Assert.Equal(5.0f, ParseGeoValue(geoLines, "Field[3].VIn") / scale, 3);
            Assert.Equal(2.0f, ParseGeoValue(geoLines, "Field[5].VIn") / scale, 3);
        }

        // --- Hilfsfunktionen ---

        private static IReadOnlyList<StlTriangle> ScaleTriangles(IReadOnlyList<StlTriangle> tris, float scale)
            => tris.Select(t => new StlTriangle(t.A * scale, t.B * scale, t.C * scale)).ToList();

        private static float ParseGeoValue(string[] lines, string key)
        {
            string line = lines.First(l => l.TrimStart().StartsWith(key + " =", StringComparison.Ordinal));
            string raw = line.Split('=')[1].Trim().TrimEnd(';');
            return float.Parse(raw, CultureInfo.InvariantCulture);
        }

        private static float ParseFirstExtrudeHeight(string extrudeLine)
        {
            // Format: Extrude { Surface{wall[]}; Layers{ {1, 1, ...}, {h1, h2, ...} }; ... }
            Match m = Regex.Match(extrudeLine, @"Layers\{\s*\{[^}]+\},\s*\{([^,}]+)");
            Assert.True(m.Success, $"Konnte erste Schichthöhe nicht aus Extrude-Zeile lesen: {extrudeLine}");
            return float.Parse(m.Groups[1].Value.Trim(), CultureInfo.InvariantCulture);
        }
    }
}
