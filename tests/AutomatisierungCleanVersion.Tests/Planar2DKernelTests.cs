using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using Clipper2Lib;
using MyPicoGkProject.Core;
using MyPicoGkProject.Kernels.Planar2D;
using Xunit;

namespace AutomatisierungCleanVersion.Tests
{
    public class Planar2DKernelTests
    {
        #region 1. Geometrie-Ergebnis & Rückwärtskompatibilität

        [Fact]
        public void GeometryResult_KindAndGeometryPath_AreBackwardsCompatible()
        {
            var res = new GeometryResult();
            Assert.Equal(GeometryKind.SurfaceStl, res.Kind);
            Assert.Equal("", res.StlPath);
            Assert.Equal("", res.GeometryPath);

            res.StlPath = "C:/tmp/test.stl";
            Assert.Equal("C:/tmp/test.stl", res.GeometryPath);

            res.GeometryPath = "C:/tmp/contour.json";
            Assert.Equal("C:/tmp/contour.json", res.StlPath);

            res.Kind = GeometryKind.Contour2D;
            Assert.Equal(GeometryKind.Contour2D, res.Kind);
        }

        #endregion

        #region 2. BBox2 und MeshMetrics2D

        [Fact]
        public void BBox2_CalculatesMinMaxCenterAndSizeCorrectly()
        {
            var empty = new BBox2();
            Assert.True(empty.IsEmpty);
            Assert.Equal(Vector2.Zero, empty.Size);
            Assert.Equal(Vector2.Zero, empty.Center);

            var box = new BBox2(new Vector2(10f, 20f), new Vector2(30f, 60f));
            Assert.False(box.IsEmpty);
            Assert.Equal(10f, box.Min.X);
            Assert.Equal(20f, box.Min.Y);
            Assert.Equal(30f, box.Max.X);
            Assert.Equal(60f, box.Max.Y);
            Assert.Equal(new Vector2(20f, 40f), box.Size);
            Assert.Equal(new Vector2(20f, 40f), box.Center);

            Assert.True(box.Contains(new Vector2(15f, 25f)));
            Assert.False(box.Contains(new Vector2(5f, 25f)));

            var expanded = box.Include(new Vector2(5f, 70f));
            Assert.Equal(5f, expanded.Min.X);
            Assert.Equal(70f, expanded.Max.Y);
        }

        [Fact]
        public void MeshMetrics2D_SingleRectangle_CalculatesExactAreaAndPerimeter()
        {
            var rect = new List<Vector2>
            {
                new(0f, 0f),
                new(20f, 0f),
                new(20f, 10f),
                new(0f, 10f)
            };

            var m = MeshMetrics2D.Measure(rect);
            Assert.Equal(200f, m.Area, 0.01f);
            Assert.Equal(60f, m.Perimeter, 0.01f);
            Assert.Equal(20f, m.Chord, 0.01f);
            Assert.Equal(10f, m.Thickness, 0.01f);
            Assert.Equal(4, m.PointCount);
        }

        #endregion

        #region 3. Boolesche Mengenoperationen

        [Fact]
        public void AddCircle_ApproximatesPiR2AnalyticalArea()
        {
            float r = 10f;
            var shape = new Shape2D().AddCircle(Vector2.Zero, r, segments: 128);
            var m = shape.Measure();

            float expectedArea = MathF.PI * r * r; // ~314.159
            // 128 Segmente haben ~99.96% der echten Kreisfläche
            Assert.InRange(m.Area, expectedArea * 0.99f, expectedArea * 1.01f);
            Assert.InRange(m.Chord, 19.8f, 20.2f);
            Assert.InRange(m.Thickness, 19.8f, 20.2f);
        }

        [Fact]
        public void BoolAdd_TwoOverlappingSquares_MatchesAnalyticalUnionArea()
        {
            // Quadrat 1: [0, 10] x [0, 10], Fläche = 100
            var s1 = new Shape2D().AddPolygon(new[]
            {
                new Vector2(0f, 0f),
                new Vector2(10f, 0f),
                new Vector2(10f, 10f),
                new Vector2(0f, 10f)
            });

            // Quadrat 2: [5, 15] x [0, 10], Fläche = 100
            var s2 = new Shape2D().AddPolygon(new[]
            {
                new Vector2(5f, 0f),
                new Vector2(15f, 0f),
                new Vector2(15f, 10f),
                new Vector2(5f, 10f)
            });

            // Überlappung ist [5, 10] x [0, 10] = 50. Vereinigung: 100 + 100 - 50 = 150.
            s1.BoolAdd(s2);
            var m = s1.Measure();

            Assert.Equal(150f, m.Area, 0.5f);
            Assert.Equal(15f, m.Chord, 0.1f);
            Assert.Equal(10f, m.Thickness, 0.1f);
        }

        [Fact]
        public void BoolSubtract_SquareMinusCircle_CreatesHoleAndMatchesAnalyticalArea()
        {
            // Quadrat: [-10, 10] x [-10, 10], Fläche = 400
            var square = new Shape2D().AddPolygon(new[]
            {
                new Vector2(-10f, -10f),
                new Vector2(10f, -10f),
                new Vector2(10f, 10f),
                new Vector2(-10f, 10f)
            });

            // Kreis: r = 5, Fläche = pi * 25 ~ 78.54
            var circle = new Shape2D().AddCircle(Vector2.Zero, 5f, segments: 64);
            float circleArea = circle.Measure().Area;

            square.BoolSubtract(circle);
            var m = square.Measure();

            float expectedArea = 400f - circleArea; // ~321.46
            Assert.InRange(m.Area, expectedArea - 1f, expectedArea + 1f);
            Assert.Equal(20f, m.Chord, 0.1f);
            Assert.Equal(20f, m.Thickness, 0.1f);
            // Muss 2 Konturen haben: Außenrand und Loch
            Assert.Equal(2, square.Paths.Count);
        }

        [Fact]
        public void BoolIntersect_OverlappingSquares_MatchesAnalyticalIntersectionArea()
        {
            var s1 = new Shape2D().AddPolygon(new[]
            {
                new Vector2(0f, 0f),
                new Vector2(10f, 0f),
                new Vector2(10f, 10f),
                new Vector2(0f, 10f)
            });

            var s2 = new Shape2D().AddPolygon(new[]
            {
                new Vector2(5f, 0f),
                new Vector2(15f, 0f),
                new Vector2(15f, 10f),
                new Vector2(5f, 10f)
            });

            s1.BoolIntersect(s2);
            var m = s1.Measure();

            // Schnittmenge: [5, 10] x [0, 10] = 50
            Assert.Equal(50f, m.Area, 0.5f);
            Assert.Equal(5f, m.Chord, 0.1f);
            Assert.Equal(10f, m.Thickness, 0.1f);
        }

        #endregion

        #region 4. Versatz (Offset, DoubleOffset, TripleOffset)

        [Fact]
        public void Offset_SquareByRadius_MatchesAnalyticalExpandedArea()
        {
            // Quadrat 20 x 20
            var shape = new Shape2D().AddPolygon(new[]
            {
                new Vector2(0f, 0f),
                new Vector2(20f, 0f),
                new Vector2(20f, 20f),
                new Vector2(0f, 20f)
            });

            // Offset +r = 5 mm
            // Analytische Fläche = 20^2 + 4 * (20 * 5) + pi * 5^2 = 400 + 400 + 78.54 = 878.54
            shape.Offset(5f);
            var m = shape.Measure();

            float expectedArea = 400f + 400f + (MathF.PI * 25f);
            Assert.InRange(m.Area, expectedArea * 0.99f, expectedArea * 1.01f);
            Assert.InRange(m.Chord, 29.8f, 30.2f);
            Assert.InRange(m.Thickness, 29.8f, 30.2f);
        }

        [Fact]
        public void TripleOffset_RoundsSquareCorners()
        {
            // Quadrat 20 x 20
            var shape = new Shape2D().AddPolygon(new[]
            {
                new Vector2(0f, 0f),
                new Vector2(20f, 0f),
                new Vector2(20f, 20f),
                new Vector2(0f, 20f)
            });

            float originalArea = shape.Measure().Area;
            shape.TripleOffset(2.0f);
            var m = shape.Measure();

            // TripleOffset rundet die 4 Ecken ab: Verliert 4 * (1 - pi/4) * r^2 = (4 - pi) * 4 ~ 3.43 mm²
            float expectedArea = 400f - ((4f - MathF.PI) * 4f);
            Assert.InRange(m.Area, expectedArea * 0.98f, expectedArea * 1.02f);
            Assert.True(m.Area < originalArea);
        }

        #endregion

        #region 5. Lattice2D (Kapsel und Konusbalken)

        [Fact]
        public void Lattice2D_AddBeam_EqualRadiiCapsule_MatchesAnalyticalArea()
        {
            var lattice = new Lattice2D();
            // Kapsel von (0, 0) nach (20, 0) mit r = 5
            lattice.AddBeam(new Vector2(0f, 0f), new Vector2(20f, 0f), 5f, 5f, capSegments: 64);

            var shape = new Shape2D(lattice);
            var m = shape.Measure();

            // Analytisch: Rechteck 20 x 10 = 200 + zwei Halbkreise r=5 (pi * 25 ~ 78.54) = 278.54
            float expectedArea = 200f + (MathF.PI * 25f);
            Assert.InRange(m.Area, expectedArea * 0.99f, expectedArea * 1.01f);
            Assert.InRange(m.Chord, 29.8f, 30.2f);
            Assert.InRange(m.Thickness, 9.8f, 10.2f);
        }

        [Fact]
        public void Lattice2D_AddBeam_TaperedConeBeam_GeneratesValidShape()
        {
            var lattice = new Lattice2D();
            // Konusbalken von (0, 0) mit r1 = 8 nach (30, 0) mit r2 = 2
            float d = 30f, r1 = 8f, r2 = 2f;
            lattice.AddBeam(new Vector2(0f, 0f), new Vector2(d, 0f), r1, r2, capSegments: 64);

            var shape = new Shape2D(lattice);
            var m = shape.Measure();

            // Exakte analytische Fläche:
            // Zwei Tangenten-Trapeze = (r1 + r2) * sqrt(d^2 - (r1 - r2)^2)
            // Plus Kreissektor r1 = 0.5 * r1^2 * (pi + 2*alpha)
            // Plus Kreissektor r2 = 0.5 * r2^2 * (pi - 2*alpha)
            float alpha = MathF.Asin((r1 - r2) / d);
            float lengthTangent = MathF.Sqrt((d * d) - ((r1 - r2) * (r1 - r2)));
            float expectedArea = ((r1 + r2) * lengthTangent)
                + (0.5f * r1 * r1 * (MathF.PI + (2f * alpha)))
                + (0.5f * r2 * r2 * (MathF.PI - (2f * alpha)));

            Assert.InRange(m.Area, expectedArea * 0.99f, expectedArea * 1.01f);
            // Ausdehnung in X von -8 bis 32 -> Chord ~ 40
            Assert.InRange(m.Chord, 39.5f, 40.5f);
            // Maximale Dicke bei Kreis 1 (Durchmesser 16)
            Assert.InRange(m.Thickness, 15.5f, 16.5f);
        }

        [Fact]
        public void Lattice2D_AddBeam_DegenerateCase_ContainedCircleHandled()
        {
            var lattice = new Lattice2D();
            // Kreis 1 bei (0, 0) r = 10, Kreis 2 bei (2, 0) r = 3 (Abstand 2 <= |10 - 3| = 7 -> Kreis 2 liegt komplett in Kreis 1)
            lattice.AddBeam(new Vector2(0f, 0f), new Vector2(2f, 0f), 10f, 3f);

            var shape = new Shape2D(lattice);
            var m = shape.Measure();

            float expectedArea = MathF.PI * 100f;
            Assert.InRange(m.Area, expectedArea * 0.98f, expectedArea * 1.02f);
        }

        #endregion

        #region 6. Spline, Knickpunkterkennung und Export (JSON/SVG)

        [Fact]
        public void DetectCorners_TrailingEdgeDetected_AndCircleHasZeroCorners()
        {
            // Glatter Kreis: keine Knickpunkte (> 35 Grad)
            var circlePoints = new List<Vector2>();
            for (int i = 0; i < 64; i++)
            {
                float a = i * (2f * MathF.PI / 64f);
                circlePoints.Add(new Vector2(MathF.Cos(a) * 20f, MathF.Sin(a) * 20f));
            }
            var circleCorners = Shape2D.DetectCorners(circlePoints, 35.0f);
            Assert.Empty(circleCorners);

            // Tragflächenprofil mit spitzer Hinterkante bei (100, 0)
            var airfoilPoints = new List<Vector2>
            {
                new(100f, 0f),   // Index 0: Hinterkante
                new(75f, -3f),
                new(50f, -5f),
                new(25f, -4f),
                new(0f, 0f),     // Nasenleiste (rund)
                new(25f, 6f),
                new(50f, 8f),
                new(75f, 5f)
            };
            var airfoilCorners = Shape2D.DetectCorners(airfoilPoints, 35.0f);

            // Index 0 (Hinterkante) muss als scharfer Knickpunkt erkannt werden
            Assert.Contains(0, airfoilCorners);
        }

        [Fact]
        public void AddSpline_ThroughPoints_CreatesClosedSmoothContour()
        {
            var controlPoints = new List<Vector2>
            {
                new(0f, 0f),
                new(50f, 20f),
                new(100f, 0f),
                new(50f, -20f)
            };

            var shape = new Shape2D().AddSpline(controlPoints, samplesPerSegment: 16, closed: true);
            var m = shape.Measure();

            Assert.True(m.Area > 500f);
            Assert.InRange(m.Chord, 95f, 105f);
            Assert.InRange(m.Thickness, 35f, 45f);
        }

        [Fact]
        public void SaveContour_And_LoadContour_RoundTripPreservesGeometry()
        {
            string tempDir = Path.Combine(Path.GetTempPath(), "Planar2DTests_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            string jsonPath = Path.Combine(tempDir, "profile.json");
            string svgPath = Path.Combine(tempDir, "profile.svg");

            try
            {
                // Quadrat mit quadratischem Loch und 45° Dreieck
                var shape = new Shape2D().AddPolygon(new[]
                {
                    new Vector2(0f, 0f),
                    new Vector2(40f, 0f),
                    new Vector2(40f, 40f),
                    new Vector2(0f, 40f)
                });

                var hole = new Shape2D().AddPolygon(new[]
                {
                    new Vector2(10f, 10f),
                    new Vector2(30f, 10f),
                    new Vector2(30f, 30f),
                    new Vector2(10f, 30f)
                });
                shape.BoolSubtract(hole);

                float origArea = shape.Measure().Area; // 1600 - 400 = 1200

                // 1. Speichern als JSON
                shape.SaveContour(jsonPath);
                Assert.True(File.Exists(jsonPath));

                // 2. Laden aus JSON DTO
                var loadedDto = Shape2D.LoadContour(jsonPath);
                Assert.Equal(2, loadedDto.Contours.Count);

                var outer = loadedDto.Contours.Find(c => !c.IsHole);
                var inner = loadedDto.Contours.Find(c => c.IsHole);
                Assert.NotNull(outer);
                Assert.NotNull(inner);

                // Quadrat hat 4 Ecken mit 90° Turn
                Assert.Equal(4, outer.CornerIndices.Count);
                Assert.Equal(4, inner.CornerIndices.Count);

                // 3. Rekonstruktion als Shape2D
                var restoredShape = Shape2D.FromContourFile(jsonPath);
                var restoredM = restoredShape.Measure();
                Assert.Equal(origArea, restoredM.Area, 0.5f);
                Assert.Equal(40f, restoredM.Chord, 0.1f);
                Assert.Equal(40f, restoredM.Thickness, 0.1f);

                // 4. Speichern als SVG
                shape.SaveSvg(svgPath);
                Assert.True(File.Exists(svgPath));

                string svgContent = File.ReadAllText(svgPath);
                Assert.Contains("<svg", svgContent);
                Assert.Contains("viewBox=", svgContent);
                Assert.Contains("<path", svgContent);
                Assert.Contains("<circle", svgContent); // Markierte Knickpunkte
                Assert.Contains("</svg>", svgContent);
            }
            finally
            {
                if (Directory.Exists(tempDir))
                {
                    try { Directory.Delete(tempDir, recursive: true); } catch { }
                }
            }
        }

        #endregion
    }
}
