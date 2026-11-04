using System;
using System.Collections.Generic;
using System.Numerics;

namespace MyPicoGkProject.Core
{
    /// <summary>
    /// Messergebnis einer oder mehrerer 2D-Konturen (z.B. Profilschnitt).
    /// </summary>
    public readonly struct ContourMeasurement2D
    {
        /// <summary>Nettofläche in mm² (Außenkonturen minus Löcher).</summary>
        public float Area { get; }

        /// <summary>Gesamter Umfang aller Konturschleifen in mm.</summary>
        public float Perimeter { get; }

        /// <summary>Bounding Box der Konturen.</summary>
        public BBox2 Bounds { get; }

        /// <summary>Sehnenlänge in mm (Ausdehnung in X-Richtung).</summary>
        public float Chord => Bounds.Size.X;

        /// <summary>Profildicke in mm (Ausdehnung in Y-Richtung).</summary>
        public float Thickness => Bounds.Size.Y;

        /// <summary>Gesamtzahl der Stützpunkte aller Schleifen.</summary>
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
    /// Berechnet geometrische Metriken für 2D-Konturen (Fläche nach Gaußscher Trapezformel / Shoelace,
    /// Umfang geschlossener Polygone, Bounding Box, Sehne und Dicke).
    /// </summary>
    public static class MeshMetrics2D
    {
        /// <summary>
        /// Vermisst eine einzelne geschlossene 2D-Kontur.
        /// </summary>
        public static ContourMeasurement2D Measure(IReadOnlyList<Vector2> singleContour)
        {
            if (singleContour == null || singleContour.Count == 0)
                return new ContourMeasurement2D(0f, 0f, BBox2.Empty, 0);

            return Measure(new[] { singleContour });
        }

        /// <summary>
        /// Vermisst eine Menge geschlossener 2D-Konturen (Außenkonturen und Löcher).
        /// Die vorzeichenbehafteten Flächen der Schleifen werden summiert (Löcher subtrahieren sich
        /// automatisch bei gegenläufiger Orientierung).
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

                    // Gaußsche Trapezformel (Shoelace-Formel): x_i * y_{i+1} - x_{i+1} * y_i
                    contourSignedArea += ((double)p0.X * p1.Y) - ((double)p1.X * p0.Y);

                    // Segmentlänge
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
