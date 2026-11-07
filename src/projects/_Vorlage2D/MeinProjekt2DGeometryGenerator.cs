using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using MyPicoGkProject.Core;
using MyPicoGkProject.Kernels.Planar2D;

namespace MyPicoGkProject.Projects.MeinProjekt2D
{
    /// <summary>
    /// Die Geometrie eines zweidimensionalen CFD-Projekts (TODO-47).
    /// Baut eine 2D-Kontur (Platzhalter: profilähnlicher Körper mit Sehne und Dicke)
    /// mittels <see cref="Shape2D"/> und meldet Messwerte.
    ///
    /// <para>
    /// <b>Hier arbeitest du.</b> Der Platzhalter steht nur für den ersten Rauchtest;
    /// ersetze ihn durch deine Form oder Profilformel (siehe z.B. <c>src/projects/Naca2D/</c>).
    /// </para>
    /// </summary>
    public sealed class MeinProjekt2DGeometryGenerator : IGeometryGenerator
    {
        /// <inheritdoc/>
        public GeometryResult GenerateAndExport(
            int iteration, int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            float chord = Read(parameters, "Chord");
            float thickness = Read(parameters, "Thickness");

            Shape2D shape = BuildProfileContour(chord, thickness);

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

            // Metriken an das Framework melden:
            result.AddMetric("Area", measurement.Area, MetricScaling.Area);
            result.AddMetric("Perimeter", measurement.Perimeter, MetricScaling.Linear);
            result.AddMetric("Chord", measurement.Chord, MetricScaling.Linear);
            result.AddMetric("Thickness", measurement.Thickness, MetricScaling.Linear);

            // FrontalArea entspricht in 2D der Bezugslänge / Sehne (für SU2 REF_AREA):
            result.AddMetric("FrontalArea", measurement.Chord, MetricScaling.Linear);

            return result;
        }

        /// <summary>
        /// Erzeugt die Platzhalter-Kontur: ein 2D-Profil mit scharfer Hinterkante bei (chord, 0)
        /// und abgerundeter Vorderkante bei (0, 0).
        /// </summary>
        private static Shape2D BuildProfileContour(float chord, float thickness)
        {
            var points = new List<Vector2>
            {
                new(chord, 0f), // Hinterkante (scharfer Knickpunkt)
                new(chord * 0.75f, -thickness * 0.35f),
                new(chord * 0.50f, -thickness * 0.50f),
                new(chord * 0.25f, -thickness * 0.40f),
                new(0f, 0f),    // Vorderkante / Nase
                new(chord * 0.25f, thickness * 0.40f),
                new(chord * 0.50f, thickness * 0.50f),
                new(chord * 0.75f, thickness * 0.35f)
            };

            return new Shape2D().AddPolygon(points);
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
