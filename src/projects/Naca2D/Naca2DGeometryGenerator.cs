using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;

using FluidForge.Core;
using FluidForge.Kernels.Planar2D;

namespace FluidForge.Projects.Naca2D
{
    /// <summary>
    /// Parametric 2D geometry generator for 4-digit NACA airfoils.
    /// Generates a planar contour using <see cref="Shape2D"/> (completely without PicoGK / Xvfb)
    /// and exports it as a JSON contour file for Gmsh2D as well as an SVG vector graphic.
    /// </summary>
    public sealed class Naca2DGeometryGenerator : IGeometryGenerator
    {
        private const float DefaultChordLength = 50.0f; // Chord length c in mm
        private const int ChordStations = 200;          // Stations per half-chord (upper/lower)

        /// <summary>
        /// Whether the trailing edge should be closed to a sharp point (default: true, coefficient -0.1036f),
        /// or left open/blunt per the original NACA formula (false, coefficient -0.1015f).
        /// </summary>
        public bool ClosedTrailingEdge { get; set; } = true;

        /// <summary>
        /// Chord length c in mm.
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

            // Compute dimensionless NACA ratios
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

            // Geometric metrics for 2D CFD
            result.AddMetric("Area", measurement.Area, MetricScaling.Area);
            result.AddMetric("Perimeter", measurement.Perimeter, MetricScaling.Linear);
            result.AddMetric("Chord", measurement.Chord, MetricScaling.Linear);
            result.AddMetric("Thickness", measurement.Thickness, MetricScaling.Linear);

            // FrontalArea maps to chord length in 2D so su2.json can use either
            // "ReferenceAreaMetric": "Chord" or "FrontalArea".
            result.AddMetric("FrontalArea", measurement.Chord, MetricScaling.Linear);

            return result;
        }

        /// <summary>
        /// Builds the closed contour point loop with cosine spacing.
        /// Starts at the trailing edge, runs along the lower surface to the leading edge,
        /// and returns along the upper surface.
        /// </summary>
        public static List<Vector2> BuildContourPoints(
            float c, float m, float p, float t,
            int stations = 200, bool closedTrailingEdge = true)
        {
            var upperPoints = new Vector2[stations + 1];
            var lowerPoints = new Vector2[stations + 1];

            for (int i = 0; i <= stations; i++)
            {
                // Cosine spacing for fine resolution at leading and trailing edges
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
                // Closed trailing edge: sharp point at (c, yc(1))
                Vector2 trailingEdge = new Vector2(c, upperPoints[stations].Y);
                points.Add(trailingEdge);

                // Lower surface from trailing edge to leading edge (i = stations - 1 down to 1)
                for (int i = stations - 1; i >= 1; i--)
                {
                    points.Add(lowerPoints[i]);
                }

                // Leading edge (nose) at xi = 0
                points.Add(new Vector2(0f, 0f));

                // Upper surface from leading edge to trailing edge (i = 1 to stations - 1)
                for (int i = 1; i <= stations - 1; i++)
                {
                    points.Add(upperPoints[i]);
                }
            }
            else
            {
                // Blunt trailing edge: two corners at (c, y_L) and (c, y_U)
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
        /// Evaluates the classical 4-digit NACA airfoil equations at relative chord position xi in [0, 1]
        /// (camber yc, half-thickness yt, camber slope dyc/dx, tangent angle theta, and upper/lower coordinates).
        /// </summary>
        /// <param name="xi">Relative chord position xi = x / c in [0, 1].</param>
        /// <param name="c">Chord length c in mm.</param>
        /// <param name="m">Maximum camber ratio m = MaxCamber / 100.</param>
        /// <param name="p">Location of maximum camber p = CamberPosition / 100.</param>
        /// <param name="t">Maximum thickness ratio t = MaxThickness / 100.</param>
        /// <param name="yc">Camber line Y-coordinate in mm.</param>
        /// <param name="yt">Half-thickness perpendicular to the camber line in mm.</param>
        /// <param name="theta">Camber line tangent angle in radians.</param>
        /// <param name="upperSurface">Upper surface coordinates (x_U, y_U) in mm.</param>
        /// <param name="lowerSurface">Lower surface coordinates (x_L, y_L) in mm.</param>
        /// <param name="closedTrailingEdge">Whether to use -0.1036f for a zero-thickness trailing edge.</param>
        public static void EvaluateNaca4Digit(
            float xi, float c, float m, float p, float t,
            out float yc, out float yt, out float theta,
            out Vector2 upperSurface, out Vector2 lowerSurface,
            bool closedTrailingEdge = true)
        {
            float xiClamped = Math.Clamp(xi, 0.0f, 1.0f);
            float x = xiClamped * c;

            // 1. NACA thickness distribution (half-thickness yt)
            // -0.1036f closes the trailing edge at xi = 1; -0.1015f retains the historical ~0.2% c gap.
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

            // 2. NACA camber line (yc) and derivative (dyc/dx)
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

            // 3. Camber line tangent angle
            theta = MathF.Atan(dycDx);

            // 4. Exact airfoil coordinates perpendicular to camber line (2D xy-plane)
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
                $"[GEOMETRY] Missing parameter '{name}'. Present: "
                + (parameters.Count > 0 ? string.Join(", ", parameters.Keys) : "(none)")
                + ". Add it to BaseParameters, MaxDeviations, and ParameterBounds in "
                + "project.json -- and to the same three dictionaries in CreateDefaults().");
        }
    }
}
