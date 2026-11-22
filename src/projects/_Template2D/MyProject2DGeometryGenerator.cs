using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using FluidForge.Core;
using FluidForge.Kernels.Planar2D;

namespace FluidForge.Projects.MyProject2D
{
    /// <summary>
    /// Geometry generator for a 2D CFD project.
    /// Builds a 2D contour (placeholder: airfoil-like body with chord and thickness)
    /// using <see cref="Shape2D"/> and reports geometric metrics.
    ///
    /// <para>
    /// <b>Edit here.</b> The placeholder is for smoke testing only; replace it with
    /// your own contour or airfoil parameterization (see e.g. <c>src/projects/Naca2D/</c>).
    /// </para>
    /// </summary>
    public sealed class MyProject2DGeometryGenerator : IGeometryGenerator
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

            // Report metrics to the framework:
            result.AddMetric("Area", measurement.Area, MetricScaling.Area);
            result.AddMetric("Perimeter", measurement.Perimeter, MetricScaling.Linear);
            result.AddMetric("Chord", measurement.Chord, MetricScaling.Linear);
            result.AddMetric("Thickness", measurement.Thickness, MetricScaling.Linear);

            // FrontalArea maps to chord length in 2D (for SU2 REF_AREA):
            result.AddMetric("FrontalArea", measurement.Chord, MetricScaling.Linear);

            return result;
        }

        /// <summary>
        /// Builds the placeholder contour: a 2D profile with a sharp trailing edge at (chord, 0)
        /// and a rounded leading edge at (0, 0).
        /// </summary>
        private static Shape2D BuildProfileContour(float chord, float thickness)
        {
            var points = new List<Vector2>
            {
                new(chord, 0f), // Trailing edge (sharp corner)
                new(chord * 0.75f, -thickness * 0.35f),
                new(chord * 0.50f, -thickness * 0.50f),
                new(chord * 0.25f, -thickness * 0.40f),
                new(0f, 0f),    // Leading edge / nose
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
                $"[GEOMETRY] Missing parameter '{name}'. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters and ParameterBounds in project.json.");
        }
    }
}
