using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using Xunit;

using MyPicoGkProject.Core;
using MyPicoGkProject.Solvers.Cfd;

namespace AutomatisierungCleanVersion.Tests
{
    /// <summary>
    /// TODO-29: Volumen, Oberfläche und Hüllquader aus dem Dreiecksnetz.
    ///
    /// <para>
    /// Geprüft wird gegen Körper, deren Maße man von Hand nachrechnen kann — der Quader aus
    /// <see cref="TunnelGeometry"/> liegt dafür schon im Framework. Das ist der eigentliche
    /// Gewinn dieser Klasse: die Rechnung steht einmal an einer Stelle, die man prüfen kann,
    /// statt in jedem Projekt neu.
    /// </para>
    /// </summary>
    public class MeshMetricsTests
    {
        private static readonly Vector3 BoxSize = new Vector3(600f, 300f, 300f);

        private static IReadOnlyList<StlTriangle> Box(Vector3? center = null)
            => TunnelGeometry.CreateBox(BoxSize, center ?? Vector3.Zero);

        /// <summary>600 × 300 × 300 = 54.000.000 mm³, mit zwölf Dreiecken.</summary>
        [Fact]
        public void The_Volume_Of_A_Box_Is_Its_Product_Of_Edges()
        {
            MeshMeasurement measurement = MeshMetrics.Measure(Box());

            Assert.Equal(12, measurement.TriangleCount);
            Assert.Equal(600f * 300f * 300f, measurement.Volume, 0);
        }

        /// <summary>2 · (600·300 + 600·300 + 300·300) = 900.000 mm².</summary>
        [Fact]
        public void The_Surface_Of_A_Box_Is_The_Sum_Of_Its_Six_Faces()
        {
            float expected = 2f * (600f * 300f + 600f * 300f + 300f * 300f);

            Assert.Equal(expected, MeshMetrics.SurfaceArea(Box()), 0);
        }

        /// <summary>
        /// Der Hüllquader eines im Ursprung zentrierten Quaders geht von -Größe/2 bis +Größe/2.
        /// Die Spannweite quer zur Strömung (X) ist damit <c>Size.Y</c>.
        /// </summary>
        [Fact]
        public void The_Bounds_Cover_The_Box()
        {
            MeshBounds bounds = MeshMetrics.Bounds(Box());

            Assert.False(bounds.IsEmpty);
            Assert.Equal(-300f, bounds.Min.X, 3);
            Assert.Equal(300f, bounds.Max.X, 3);
            Assert.Equal(BoxSize, bounds.Size);
            Assert.Equal(Vector3.Zero, bounds.Center);
            Assert.Equal(300f, bounds.Size.Y, 3);
        }

        /// <summary>
        /// Das Volumen hängt nicht davon ab, wo der Körper liegt — obwohl die Rechnung über
        /// Tetraeder mit der Spitze im Ursprung läuft. Genau das ist die Aussage des
        /// Divergenzsatzes, und wäre sie falsch, wären alle Volumina eines verschobenen
        /// Modells falsch (und zwar plausibel falsch).
        /// </summary>
        [Fact]
        public void The_Volume_Does_Not_Depend_On_Where_The_Body_Sits()
        {
            float atOrigin = MeshMetrics.Volume(Box());
            float movedFarAway = MeshMetrics.Volume(Box(new Vector3(5000f, -2000f, 750f)));

            // Ein Promille Toleranz: die Koordinaten sind float, und weit weg vom Ursprung
            // heben sich in der Summe grosse Zahlen gegeneinander weg.
            Assert.Equal(atOrigin, movedFarAway, 0.001f * atOrigin);
        }

        /// <summary>
        /// Die Fläche ist von der Lage vollständig unabhängig — sie rechnet nur mit
        /// Kantenvektoren.
        /// </summary>
        [Fact]
        public void The_Surface_Does_Not_Depend_On_Where_The_Body_Sits()
        {
            Assert.Equal(
                MeshMetrics.SurfaceArea(Box()),
                MeshMetrics.SurfaceArea(Box(new Vector3(5000f, -2000f, 750f))),
                0);
        }

        /// <summary>
        /// Nach innen gedrehte Dreiecke geben ein NEGATIVES Volumen. Das ist der Zweck des
        /// Vorzeichens: eine umgedrehte Hülle rechnet Gmsh nicht oder falsch, und im Betrag
        /// ist der Fehler unsichtbar.
        /// </summary>
        [Fact]
        public void Inverted_Triangles_Give_A_Negative_Signed_Volume()
        {
            var inverted = Box().Select(t => new StlTriangle(t.A, t.C, t.B)).ToList();

            Assert.True(MeshMetrics.SignedVolume(Box()) > 0f, "Die Vorgabe-Hülle zeigt nach außen.");
            Assert.True(MeshMetrics.SignedVolume(inverted) < 0f);

            // Als Metrik zählt der Betrag -- sonst bekäme eine Fitness-Formel ein negatives
            // Volumen und damit eine Bewertung, die schlechter ist als jeder Fehlschlag.
            Assert.Equal(MeshMetrics.Volume(Box()), MeshMetrics.Volume(inverted), 0);
        }

        /// <summary>
        /// Ein Zylinder mit 64 Segmenten ist ein 64-eckiges Prisma: sein Volumen liegt
        /// systematisch knapp UNTER dem des echten Zylinders (das Vieleck liegt innerhalb des
        /// Kreises), hier um etwa 0,06 %. Ein halbes Prozent Toleranz deckt das ab und würde
        /// einen Faktor-2-Fehler trotzdem fangen.
        /// </summary>
        [Fact]
        public void A_Cylinder_Comes_Close_To_The_Analytic_Volume()
        {
            IReadOnlyList<StlTriangle> cylinder =
                TunnelGeometry.CreateCylinder(300f, 600f, Vector3.Zero, 64);

            double expected = Math.PI / 4.0 * 300.0 * 300.0 * 600.0;

            Assert.Equal((float)expected, MeshMetrics.Volume(cylinder), (float)(0.005 * expected));
        }

        /// <summary>
        /// Mantel (π·d·l) plus zwei Deckel (2 · π/4·d²) — wieder knapp darunter, weil das
        /// Vieleck kürzer ist als der Kreisumfang.
        /// </summary>
        [Fact]
        public void A_Cylinder_Comes_Close_To_The_Analytic_Surface()
        {
            IReadOnlyList<StlTriangle> cylinder =
                TunnelGeometry.CreateCylinder(300f, 600f, Vector3.Zero, 64);

            double expected = Math.PI * 300.0 * 600.0 + 2.0 * (Math.PI / 4.0 * 300.0 * 300.0);

            Assert.Equal((float)expected, MeshMetrics.SurfaceArea(cylinder), (float)(0.005 * expected));
        }

        /// <summary>
        /// Die angeströmte Fläche aus dem Hüllquader ist das umschließende Rechteck quer zur X-Achse.
        /// Beim achsparallelen Quader stimmt diese Hüllquader-Fläche exakt mit der wahren
        /// projizierten Stirnfläche überein.
        /// </summary>
        [Fact]
        public void The_Frontal_Box_Area_Is_The_Bounding_Rectangle()
        {
            MeshMeasurement box = MeshMetrics.Measure(Box());

            Assert.Equal(300f * 300f, box.FrontalBoxArea, 0);
            Assert.Equal(300f * 300f, box.FrontalProjectedArea, 0);
        }

        /// <summary>
        /// Ein Zylinder entlang X hat in der YZ-Projektion eine Kreisfläche (π/4 · d² ≈ 70.686 mm²),
        /// während das Hüllrechteck d × d = 90.000 mm² um 27 % zu groß liegt.
        /// </summary>
        [Fact]
        public void A_Cylinder_Has_A_Circular_Projected_Frontal_Area()
        {
            IReadOnlyList<StlTriangle> cylinder =
                TunnelGeometry.CreateCylinder(300f, 600f, Vector3.Zero, 64);

            MeshMeasurement measurement = MeshMetrics.Measure(cylinder);
            double expectedCircleArea = Math.PI / 4.0 * 300.0 * 300.0;

            Assert.Equal(300f * 300f, measurement.FrontalBoxArea, 0);
            Assert.Equal((float)expectedCircleArea, measurement.FrontalProjectedArea, (float)(0.005 * expectedCircleArea));
        }

        /// <summary>
        /// Ein langer dünner Stab (300 mm lang, 10 mm breit), der um 45° schräg in der YZ-Ebene liegt,
        /// spannt einen riesigen Hüllquader (~219 × 219 ≈ 48.000 mm²) auf, hat aber in Wahrheit nur
        /// eine Stirnfläche von 300 × 10 = 3.000 mm².
        /// </summary>
        [Fact]
        public void A_Diagonal_Thin_Rod_In_YZ_Gets_Its_True_Slender_Projected_Area()
        {
            // Dünner Stab: 20 mm tief in X, 300 mm lang in Y, 10 mm dick in Z, um 45° um die X-Achse gedreht
            IReadOnlyList<StlTriangle> axisAlignedRod =
                TunnelGeometry.CreateBox(new Vector3(20f, 300f, 10f), Vector3.Zero);

            Quaternion rot45AroundX = Quaternion.CreateFromAxisAngle(Vector3.UnitX, MathF.PI / 4.0f);
            List<StlTriangle> diagonalRod = axisAlignedRod
                .Select(t => new StlTriangle(
                    Vector3.Transform(t.A, rot45AroundX),
                    Vector3.Transform(t.B, rot45AroundX),
                    Vector3.Transform(t.C, rot45AroundX)))
                .ToList();

            MeshMeasurement measurement = MeshMetrics.Measure(diagonalRod);
            float expectedTrueArea = 300f * 10f; // 3.000 mm²

            // Hüllquader liegt bei ~(310 / √2)² ≈ 48.050 mm² (über 16x zu groß!)
            Assert.True(measurement.FrontalBoxArea > 45000f);

            // Wahre projizierte Silhouette trifft die 3.000 mm² auf < 1 % genau
            Assert.Equal(expectedTrueArea, measurement.FrontalProjectedArea, 0.01f * expectedTrueArea);

            // Der Schalter wählt zwischen beiden Methoden
            Assert.Equal(measurement.FrontalProjectedArea, measurement.GetFrontalArea(useProjected: true));
            Assert.Equal(measurement.FrontalBoxArea, measurement.GetFrontalArea(useProjected: false));
        }

        /// <summary>
        /// Zwei hintereinanderliegende Körper in Strömungsrichtung (X) werfen denselben Schatten
        /// in der YZ-Ebene — die projizierte Stirnfläche darf sich dadurch nicht verdoppeln.
        /// </summary>
        [Fact]
        public void Tandem_Bodies_Along_Flow_Direction_Do_Not_Double_Count_Projected_Area()
        {
            var frontBox = TunnelGeometry.CreateBox(new Vector3(100f, 200f, 150f), new Vector3(-200f, 0f, 0f));
            var rearBox = TunnelGeometry.CreateBox(new Vector3(100f, 200f, 150f), new Vector3(200f, 0f, 0f));
            var combined = frontBox.Concat(rearBox).ToList();

            float projected = MeshMetrics.FrontalProjectedArea(combined);

            Assert.Equal(200f * 150f, projected, 0);
        }

        /// <summary>
        /// Ein leeres Netz ist kein Körper der Größe 0, sondern gar keiner: der Hüllquader
        /// meldet <c>IsEmpty</c>. Ohne diese Unterscheidung sähe ein verlorenes Netz aus wie
        /// ein Modell, das im Ursprung zusammengeschrumpft ist.
        /// </summary>
        [Fact]
        public void An_Empty_Mesh_Has_No_Bounds()
        {
            MeshMeasurement measurement = MeshMetrics.Measure(Array.Empty<StlTriangle>());

            Assert.Equal(0, measurement.TriangleCount);
            Assert.Equal(0f, measurement.Volume);
            Assert.Equal(0f, measurement.SurfaceArea);
            Assert.True(measurement.Bounds.IsEmpty);
            Assert.Equal(Vector3.Zero, measurement.Bounds.Size);
        }

        /// <summary>Ein entartetes Dreieck hat keine Fläche und kein Volumen, aber einen Hüllquader.</summary>
        [Fact]
        public void A_Degenerate_Triangle_Has_No_Area()
        {
            var degenerate = new[]
            {
                new StlTriangle(Vector3.Zero, new Vector3(10f, 0f, 0f), new Vector3(20f, 0f, 0f))
            };

            MeshMeasurement measurement = MeshMetrics.Measure(degenerate);

            Assert.Equal(0f, measurement.SurfaceArea, 4);
            Assert.Equal(0f, measurement.Volume, 4);
            Assert.False(measurement.Bounds.IsEmpty);
            Assert.Equal(20f, measurement.Bounds.Size.X, 3);
        }

        /// <summary>
        /// <c>null</c> ist ein Programmierfehler und keine leere Menge — dafür gibt es
        /// <c>Array.Empty</c>.
        /// </summary>
        [Fact]
        public void Null_Is_Rejected()
        {
            Assert.Throws<ArgumentNullException>(() => MeshMetrics.Measure(null!));
        }
    }
}
