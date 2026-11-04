using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using MyPicoGkProject.Core;
using MyPicoGkProject.Kernels.Planar2D;

namespace MyPicoGkProject.Projects.TestprojektNACA
{
    /// <summary>
    /// Repräsentativer 2D-Profilquerschnitt des Testprojekts Baars (NACA-Flügelprofil)
    /// für die 2D-Vorkalibrierung vor 3D-Läufen (TODO-48).
    /// </summary>
    public sealed class TestprojektNACASection2DGenerator : IGeometryGenerator
    {
        private const float DefaultChordLength = 50.0f;       // Sehnenlänge c in mm
        private const int ChordStations = 100;

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

            float c = DefaultChordLength;

            // Dimensionslose Kennwerte berechnen
            float m = Math.Clamp(maxCamberPercent / 100.0f, 0.0f, 0.15f);
            float p = Math.Clamp(camberPositionPercent / 100.0f, 0.05f, 0.95f);
            float t = Math.Clamp(maxThicknessPercent / 100.0f, 0.02f, 0.40f);

            // Stützstellen entlang der Sehne berechnen (Kosinus-Verteilung)
            var upperPoints = new List<Vector2>(ChordStations + 1);
            var lowerPoints = new List<Vector2>(ChordStations + 1);

            for (int i = 0; i <= ChordStations; i++)
            {
                float beta = (float)i / ChordStations * MathF.PI;
                float xi = 0.5f * (1.0f - MathF.Cos(beta)); // xi in [0, 1]

                TestprojektNACAGeometryGenerator.EvaluateNaca4Digit(
                    xi, c, m, p, t,
                    out _, out _, out _,
                    out Vector2 upper, out Vector2 lower);

                upperPoints.Add(upper);
                if (i > 0 && i < ChordStations)
                {
                    lowerPoints.Add(lower);
                }
            }

            // Geschlossene Konturschleife bilden: Oberseite vorwärts, Unterseite rückwärts
            var polygon = new List<Vector2>(upperPoints);
            for (int i = lowerPoints.Count - 1; i >= 0; i--)
            {
                polygon.Add(lowerPoints[i]);
            }

            var shape = new Shape2D().AddPolygon(polygon);

            string fileName = $"Airfoil2D_Gen{iteration}_Var{variant}.json";
            string jsonPath = Path.Combine(outputDirectory, fileName);
            shape.SaveContour(jsonPath, cornerAngleDeg: 35.0f);

            shape.CalculateProperties(out float area, out BBox2 bounds);
            float chord = bounds.Size.X;
            float thickness = bounds.Size.Y;

            var result = new GeometryResult
            {
                Kind = GeometryKind.Contour2D,
                StlPath = jsonPath
            };

            result.AddMetric("Chord", chord > 0f ? chord : c, MetricScaling.Linear);
            result.AddMetric("FrontalArea", thickness, MetricScaling.Linear);
            result.AddMetric("Area", area, MetricScaling.Area);

            return result;
        }

        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRIE 2D] Der Parameter '{name}' fehlt. Vorhanden ist: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(keiner)")
                + ".");
        }
    }
}
