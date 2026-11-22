using System;
using System.Collections.Generic;
using System.Numerics;

namespace FluidForge.Core
{
    /// <summary>
    /// Geometric measurements of one or more 2D contours (e.g., airfoil cross-section).
    /// </summary>
    public readonly struct ContourMeasurement2D
    {
        /// <summary>Net area in mm² (outer contours minus holes).</summary>
        public float Area { get; }

        /// <summary>Total perimeter across all contour loops in mm.</summary>
        public float Perimeter { get; }

        /// <summary>Bounding box of the contours.</summary>
        public BBox2 Bounds { get; }

        /// <summary>Chord length in mm (extent along X).</summary>
        public float Chord => Bounds.Size.X;

        /// <summary>Profile thickness in mm (extent along Y).</summary>
        public float Thickness => Bounds.Size.Y;

        /// <summary>Total number of vertices across all loops.</summary>
        public int PointCount { get; }

        public ContourMeasurement2D(float area, float perimeter, BBox2 bounds, int pointCount)
        {
            Area = area;
            Perimeter = perimeter;
            Bounds = bounds;
            PointCount = pointCount;
        }

        public override string ToString()
            => $"Contour2D [Area: {Area:F2} mm², Perimeter: {Perimeter:F2} mm, Chord: {Chord:F2} mm, Thickness: {Thickness:F2} mm, Points: {PointCount}]";
    }

    /// <summary>
    /// Computes geometric metrics for 2D contours (area via the shoelace formula,
    /// closed polygon perimeter, bounding box, chord, and thickness).
    /// </summary>
    public static class MeshMetrics2D
    {
        /// <summary>
        /// Measures a single closed 2D contour.
        /// </summary>
        public static ContourMeasurement2D Measure(IReadOnlyList<Vector2> singleContour)
        {
            if (singleContour == null || singleContour.Count == 0)
                return new ContourMeasurement2D(0f, 0f, BBox2.Empty, 0);

            return Measure(new[] { singleContour });
        }

        /// <summary>
        /// Measures a collection of closed 2D contours (outer contours and holes).
        /// Signed loop areas are summed so opposite-winding holes subtract automatically.
        /// </summary>
        public static ContourMeasurement2D Measure(IReadOnlyList<IReadOnlyList<Vector2>> contours)
        {
            if (contours == null || contours.Count == 0)
                return new ContourMeasurement2D(0f, 0f, BBox2.Empty, 0);

            double sumSignedArea = 0.0;
            double totalPerimeter = 0.0;
            int totalPoints = 0;
            BBox2 bounds = BBox2.Empty;

            foreach (var contour in contours)
            {
                if (contour == null || contour.Count == 0)
                    continue;

                int n = contour.Count;
                totalPoints += n;

                double contourSignedArea = 0.0;
                double contourPerimeter = 0.0;

                for (int i = 0; i < n; i++)
                {
                    Vector2 p0 = contour[i];
                    Vector2 p1 = contour[(i + 1) % n];

                    bounds = bounds.Include(p0);

                    // Shoelace formula: x_i * y_{i+1} - x_{i+1} * y_i
                    contourSignedArea += ((double)p0.X * p1.Y) - ((double)p1.X * p0.Y);

                    // Segment length
                    double dx = p1.X - p0.X;
                    double dy = p1.Y - p0.Y;
                    contourPerimeter += Math.Sqrt(dx * dx + dy * dy);
                }

                sumSignedArea += 0.5 * contourSignedArea;
                totalPerimeter += contourPerimeter;
            }

            float finalArea = (float)Math.Abs(sumSignedArea);
            float finalPerimeter = (float)totalPerimeter;

            return new ContourMeasurement2D(finalArea, finalPerimeter, bounds, totalPoints);
        }
    }
}
