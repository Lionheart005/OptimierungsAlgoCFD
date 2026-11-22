using System;
using System.Numerics;
using Clipper2Lib;

namespace FluidForge.Kernels.Planar2D
{
    /// <summary>
    /// Two-dimensional beam lattice (analogous to PicoGK's Lattice in 3D).
    /// Generates capsule and tapered beams with tangent transitions and circular end caps.
    /// </summary>
    public class Lattice2D
    {
        public PathsD Paths { get; private set; } = new();

        public Lattice2D()
        {
        }

        /// <summary>
        /// Adds a circular node.
        /// </summary>
        public void AddCircle(Vector2 center, float radius, int segments = 64)
        {
            if (radius <= 0f) return;
            if (segments < 3) segments = 3;

            var path = new PathD(segments);
            double step = 2.0 * Math.PI / segments;
            for (int i = 0; i < segments; i++)
            {
                double angle = i * step;
                path.Add(new PointD(
                    center.X + radius * Math.Cos(angle),
                    center.Y + radius * Math.Sin(angle)));
            }

            Paths = Clipper.Union(Paths, new PathsD { path }, FillRule.NonZero);
        }

        /// <summary>
        /// Adds a capsule or tapered beam between point a (radius r1) and point b (radius r2).
        /// Computes exact outer tangents and rounds both ends with circular arcs.
        /// </summary>
        public void AddBeam(Vector2 a, Vector2 b, float r1, float r2, int capSegments = 32)
        {
            if (r1 <= 0f && r2 <= 0f) return;
            if (r1 <= 0f) { AddCircle(b, r2, capSegments); return; }
            if (r2 <= 0f) { AddCircle(a, r1, capSegments); return; }

            Vector2 v = b - a;
            float d = v.Length();

            // Degenerate case 1: centers practically coincide
            if (d <= 1e-6f)
            {
                AddCircle(a, MathF.Max(r1, r2), capSegments);
                return;
            }

            // Degenerate case 2: one circle completely contains the other (|b - a| <= |r1 - r2|)
            if (d <= MathF.Abs(r1 - r2))
            {
                if (r1 >= r2)
                    AddCircle(a, r1, capSegments);
                else
                    AddCircle(b, r2, capSegments);
                return;
            }

            // Non-degenerate case: exact tapered beam / tangent capsule
            // Centerline direction angle
            double phi = Math.Atan2(v.Y, v.X);

            // Tangent half-angle: sin(alpha) = (r1 - r2) / d
            double sinAlpha = (r1 - r2) / d;
            sinAlpha = Math.Clamp(sinAlpha, -1.0, 1.0);
            double alpha = Math.Asin(sinAlpha);

            // Outer normals at the tangent contact points:
            // Upper contact point has angle thetaUpper = phi + PI/2 - alpha
            // Lower contact point has angle thetaLower = phi - PI/2 + alpha
            double thetaUpper = phi + (Math.PI * 0.5) - alpha;
            double thetaLower = phi - (Math.PI * 0.5) + alpha;

            var polygon = new PathD();

            int nCaps = Math.Max(4, capSegments / 2);

            // 1. Cap at circle b (around b, decreasing angle from thetaUpper to thetaLower)
            double spanB = Math.PI - (2.0 * alpha);
            for (int i = 0; i <= nCaps; i++)
            {
                double t = (double)i / nCaps;
                double angle = thetaUpper - (t * spanB);
                polygon.Add(new PointD(
                    b.X + (r2 * Math.Cos(angle)),
                    b.Y + (r2 * Math.Sin(angle))));
            }

            // 2. Cap at circle a (around a, decreasing angle from thetaLower to thetaUpper - 2*PI)
            double spanA = Math.PI + (2.0 * alpha);
            for (int i = 0; i <= nCaps; i++)
            {
                double t = (double)i / nCaps;
                double angle = thetaLower - (t * spanA);
                polygon.Add(new PointD(
                    a.X + (r1 * Math.Cos(angle)),
                    a.Y + (r1 * Math.Sin(angle))));
            }

            Paths = Clipper.Union(Paths, new PathsD { polygon }, FillRule.NonZero);
        }
    }
}
