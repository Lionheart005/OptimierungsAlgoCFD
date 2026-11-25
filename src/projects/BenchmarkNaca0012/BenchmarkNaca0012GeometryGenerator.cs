using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using FluidForge.Core;
using FluidForge.Kernels.Planar2D;
using FluidForge.Projects.Naca2D;

namespace FluidForge.Projects.BenchmarkNaca0012
{
    /// <summary>
    /// Analytical NACA 0012 contour generator for the NASA TMR benchmark (1.0 m chord, closed trailing edge).
    /// Uses fixed 40% camber position internally so symmetric airfoils avoid dead parameters.
    /// </summary>
    public sealed class BenchmarkNaca0012GeometryGenerator : IGeometryGenerator
    {
        public const float DefaultChordMm = 1000.0f;
        private const float FixedCamberPosition = 0.40f;
        private const int ChordStations = 400;

        private readonly float _chordMm;

        /// <summary>
        /// Initializes the generator, optionally reading <c>OptimizationTargets.ChordMm</c>.
        /// </summary>
        public BenchmarkNaca0012GeometryGenerator(ProjectConfig? project = null)
        {
            _chordMm = DefaultChordMm;
            if (project?.OptimizationTargets != null &&
                project.OptimizationTargets.TryGetValue("ChordMm", out float chordMm))
            {
                if (!float.IsFinite(chordMm) || chordMm <= 0f)
                {
                    throw new InvalidOperationException(
                        $"{OutputConventions.ConfigTag} BenchmarkNaca0012 OptimizationTargets.ChordMm must be finite and > 0 (got {chordMm}).");
                }
                _chordMm = chordMm;
            }
        }

        /// <inheritdoc />
        public GeometryResult GenerateAndExport(
            int iteration,
            int variant,
            Dictionary<string, float> parameters,
            string outputDirectory,
            SimulationConfig config)
        {
            ArgumentNullException.ThrowIfNull(parameters);
            ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);

            float maxThicknessPercent = Read(parameters, "MaxThickness");
            float maxCamberPercent = Read(parameters, "MaxCamber");

            float m = Math.Clamp(maxCamberPercent / 100.0f, 0.0f, 0.15f);
            float t = Math.Clamp(maxThicknessPercent / 100.0f, 0.02f, 0.40f);

            List<Vector2> contourPoints = Naca2DGeometryGenerator.BuildContourPoints(
                _chordMm,
                m,
                FixedCamberPosition,
                t,
                stations: ChordStations,
                closedTrailingEdge: true);

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

            result.AddMetric("Area", measurement.Area, MetricScaling.Area);
            result.AddMetric("Perimeter", measurement.Perimeter, MetricScaling.Linear);
            result.AddMetric("Chord", measurement.Chord, MetricScaling.Linear);
            result.AddMetric("Thickness", measurement.Thickness, MetricScaling.Linear);
            result.AddMetric("FrontalArea", measurement.Chord, MetricScaling.Linear);

            return result;
        }

        private static float Read(Dictionary<string, float> parameters, string name)
        {
            if (parameters.TryGetValue(name, out float value)) return value;

            throw new InvalidOperationException(
                $"[GEOMETRY] Missing parameter '{name}' for BenchmarkNaca0012. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters and ParameterBounds in project.json.");
        }
    }
}
