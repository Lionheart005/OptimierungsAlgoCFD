using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using OptimierungsAlgoCFD.Core;
using OptimierungsAlgoCFD.Kernels.Planar2D;

namespace OptimierungsAlgoCFD.Projects.Naca2D
{
    /// <summary>
    /// Parametrischer 2D-Geometrie-Generator für 4-stellige NACA-Tragflügelprofile (TODO-47).
    /// Erzeugt eine planare Kontur mittels <see cref="Shape2D"/> (vollständig ohne PicoGK / Xvfb)
    /// und exportiert sie als JSON-Konturdatei für Gmsh2D sowie als SVG-Vektorgrafik.
    /// </summary>
    public sealed class Naca2DGeometryGenerator : IGeometryGenerator
    {
        private const float DefaultChordLength = 50.0f; // Sehnenlänge c in mm
        private const int ChordStations = 200;          // Stützstellen für halbe Sehne (oben/unten)

        /// <summary>
        /// Ob die Hinterkante spitz geschlossen werden soll (Standard: true, mit Beiwert -0.1036f),
        /// oder standardmäßig nach NACA-Formel offen/stumpf (false, mit Beiwert -0.1015f).
        /// </summary>
        public bool ClosedTrailingEdge { get; set; } = true;

        /// <summary>
        /// Sehnenlänge c in mm.
        /// </summary>
        public float ChordLength { get; set; } = DefaultChordLength;

        public Naca2DGeometryGenerator(bool closedTrailingEdge = true, float chordLength = DefaultChordLength)
        {
            ClosedTrailingEdge = closedTrailingEdge;
            ChordLength = chordLength;
        }

        /// <inheritdoc/>
        public GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            float maxCamberPercent = Read(parameters, "MaxCamber");
            float camberPositionPercent = Read(parameters, "CamberPosition");
            float maxThicknessPercent = Read(parameters, "MaxThickness");

            float c = ChordLength > 0f ? ChordLength : DefaultChordLength;

            // Dimensionslose Kennwerte berechnen
            float m = Math.Clamp(maxCamberPercent / 100.0f, 0.0f, 0.15f);
            float p = Math.Clamp(camberPositionPercent / 100.0f, 0.05f, 0.95f);
            float t = Math.Clamp(maxThicknessPercent / 100.0f, 0.02f, 0.40f);

            var contourPoints = BuildContourPoints(c, m, p, t, ChordStations, ClosedTrailingEdge);

            var shape = new Shape2D().AddPolygon(contourPoints);

            Directory.CreateDirectory(outputDirectory);

            string contourPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.json");
            shape.SaveContour(contourPath, cornerAngleDeg: 35.0f);

            string svgPath = Path.Combine(outputDirectory, $"Model_Gen{iteration}_Var{variant}.svg");
            shape.SaveSvg(svgPath);

            ContourMeasurement2D measurement = shape.Measure();

            var result = new GeometryResult
            {
                StlPath = contourPath,
                Kind = GeometryKind.Contour2D
            };

            // Geometrische Metriken für 2D-CFD
            result.AddMetric("Area", measurement.Area, MetricScaling.Area);
            result.AddMetric("Perimeter", measurement.Perimeter, MetricScaling.Linear);
            result.AddMetric("Chord", measurement.Chord, MetricScaling.Linear);
            result.AddMetric("Thickness", measurement.Thickness, MetricScaling.Linear);

            // FrontalArea entspricht in 2D der Bezugslänge / Sehne, damit su2.json sowohl
            // "ReferenceAreaMetric": "Chord" als auch "FrontalArea" problemlos auswerten kann.
            result.AddMetric("FrontalArea", measurement.Chord, MetricScaling.Linear);

            return result;
        }

        /// <summary>
        /// Baut die geschlossene Konturpunktschleife mit Kosinus-Verteilung auf.
        /// Startet an der Hinterkante, läuft über die Unterseite zur Nase und über die Oberseite zurück.
        /// </summary>
        public static List<Vector2> BuildContourPoints(
            float c, float m, float p, float t,
            int stations = 200, bool closedTrailingEdge = true)
        {
            var upperPoints = new Vector2[stations + 1];
            var lowerPoints = new Vector2[stations + 1];

            for (int i = 0; i <= stations; i++)
            {
                // Kosinus-Verteilung für feine Auflösung an Vorder- und Hinterkante
                float beta = (float)i / stations * MathF.PI;
                float xi = 0.5f * (1.0f - MathF.Cos(beta)); // xi in [0, 1]

                EvaluateNaca4Digit(xi, c, m, p, t, out _, out _, out _,
                    out Vector2 upper, out Vector2 lower, closedTrailingEdge);

                upperPoints[i] = upper;
                lowerPoints[i] = lower;
            }

            var points = new List<Vector2>(stations * 2 + 2);

            if (closedTrailingEdge)
            {
                // Geschlossene Hinterkante: Spitze bei (c, yc(1))
                Vector2 trailingEdge = new Vector2(c, upperPoints[stations].Y);
                points.Add(trailingEdge);

                // Unterseite von hinten nach vorne (i = stations - 1 herunter bis 1)
                for (int i = stations - 1; i >= 1; i--)
                {
                    points.Add(lowerPoints[i]);
                }

                // Vorderkante (Nase) bei xi = 0
                points.Add(new Vector2(0f, 0f));

                // Oberseite von vorne nach hinten (i = 1 bis stations - 1)
                for (int i = 1; i <= stations - 1; i++)
                {
                    points.Add(upperPoints[i]);
                }
            }
            else
            {
                // Stumpfe Hinterkante: zwei Ecken bei (c, y_L) und (c, y_U)
                points.Add(lowerPoints[stations]);

                for (int i = stations - 1; i >= 1; i--)
                {
                    points.Add(lowerPoints[i]);
                }

                points.Add(new Vector2(0f, 0f));

                for (int i = 1; i <= stations; i++)
                {
                    points.Add(upperPoints[i]);
                }
            }

            return points;
        }

        /// <summary>
        /// Berechnet für eine relative Sehnenposition xi in [0, 1] die Geometrie nach der klassischen
        /// 4-stelligen NACA-Formel (Wölbung yc, Halbdicke yt, Skelettlinien-Steigung dyc/dx,
        /// Tangentenwinkel theta sowie die Koordinaten der Profilober- und -unterseite).
        /// </summary>
        /// <param name="xi">Relative Sehnenposition xi = x / c in [0, 1].</param>
        /// <param name="c">Sehnenlänge c in mm.</param>
        /// <param name="m">Maximale Wölbung als Verhältnis m = MaxCamber / 100.</param>
        /// <param name="p">Lage der maximalen Wölbung als Verhältnis p = CamberPosition / 100.</param>
        /// <param name="t">Maximale Profildicke als Verhältnis t = MaxThickness / 100.</param>
        /// <param name="yc">Y-Koordinate der Skelettlinie (Camber) in mm.</param>
        /// <param name="yt">Halbdicke des Profils senkrecht zur Skelettlinie in mm.</param>
        /// <param name="theta">Tangentenwinkel der Skelettlinie in Radiant.</param>
        /// <param name="upperSurface">Koordinaten (x_U, y_U) der Profiloberseite in mm.</param>
        /// <param name="lowerSurface">Koordinaten (x_L, y_L) der Profilunterseite in mm.</param>
        /// <param name="closedTrailingEdge">Ob der Beiwert -0.1036f für geschlossene Hinterkante verwendet wird.</param>
        public static void EvaluateNaca4Digit(
            float xi, float c, float m, float p, float t,
            out float yc, out float yt, out float theta,
            out Vector2 upperSurface, out Vector2 lowerSurface,
            bool closedTrailingEdge = true)
        {
            float xiClamped = Math.Clamp(xi, 0.0f, 1.0f);
            float x = xiClamped * c;

            // 1. NACA-Dickenverteilung (Halbdicke yt)
            // -0.1036f schließt die Hinterkante bei xi = 1 exakt; -0.1015f belässt die historische Öffnung (~0.2% c).
            float d4 = closedTrailingEdge ? -0.1036f : -0.1015f;
            float sqrtXi = MathF.Sqrt(xiClamped);
            yt = 5.0f * t * c * (0.2969f * sqrtXi
                               - 0.1260f * xiClamped
                               - 0.3516f * xiClamped * xiClamped
                               + 0.2843f * xiClamped * xiClamped * xiClamped
                               + d4 * xiClamped * xiClamped * xiClamped * xiClamped);

            if (closedTrailingEdge && xiClamped >= 0.99999f)
            {
                yt = 0.0f;
            }
            else
            {
                yt = Math.Max(yt, 0.0f);
            }

            // 2. NACA-Skelettlinie (yc) und Ableitung (dyc/dx)
            float dycDx;
            if (m <= 1e-5f)
            {
                yc = 0.0f;
                dycDx = 0.0f;
            }
            else if (xiClamped < p)
            {
                yc = (m * c / (p * p)) * (2.0f * p * xiClamped - xiClamped * xiClamped);
                dycDx = (2.0f * m / (p * p)) * (p - xiClamped);
            }
            else
            {
                yc = (m * c / ((1.0f - p) * (1.0f - p))) * ((1.0f - 2.0f * p) + 2.0f * p * xiClamped - xiClamped * xiClamped);
                dycDx = (2.0f * m / ((1.0f - p) * (1.0f - p))) * (p - xiClamped);
            }

            // 3. Tangentenwinkel der Skelettlinie
            theta = MathF.Atan(dycDx);

            // 4. Exakte Profilkoordinaten senkrecht zur Skelettlinie (in 2D xy-Ebene)
            float sinTheta = MathF.Sin(theta);
            float cosTheta = MathF.Cos(theta);

            float xU = x - yt * sinTheta;
            float yU = yc + yt * cosTheta;
            upperSurface = new Vector2(xU, yU);

            float xL = x + yt * sinTheta;
            float yL = yc - yt * cosTheta;
            lowerSurface = new Vector2(xL, yL);
        }

        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRIE] Der Parameter '{name}' fehlt. Vorhanden ist: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(keiner)")
                + ". Er gehört in BaseParameters, MaxDeviations und ParameterBounds der "
                + "project.json dieses Projekts — und in dieselben drei Listen in "
                + "CreateDefaults().");
        }
    }
}
