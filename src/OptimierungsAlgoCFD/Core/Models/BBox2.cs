using System;
using System.Numerics;

namespace OptimierungsAlgoCFD.Core
{
    /// <summary>
    /// Zwei-dimensionale Bounding Box (AABB) in Millimetern.
    /// Ergonomisch gestaltet analog zu PicoGKs BBox3 (vecMin/vecMax) und MeshBounds (Min/Max).
    /// </summary>
    public readonly struct BBox2
    {
        public readonly Vector2 vecMin;
        public readonly Vector2 vecMax;
        public readonly bool IsEmpty;

        public Vector2 Min => vecMin;
        public Vector2 Max => vecMax;
        public Vector2 Size => IsEmpty ? Vector2.Zero : vecMax - vecMin;
        public Vector2 Center => IsEmpty ? Vector2.Zero : (vecMin + vecMax) * 0.5f;

        public static BBox2 Empty => new();

        public BBox2()
        {
            vecMin = new Vector2(float.PositiveInfinity, float.PositiveInfinity);
            vecMax = new Vector2(float.NegativeInfinity, float.NegativeInfinity);
            IsEmpty = true;
        }

        public BBox2(Vector2 min, Vector2 max)
        {
            vecMin = min;
            vecMax = max;
            IsEmpty = false;
        }

        public BBox2(float minX, float minY, float maxX, float maxY)
        {
            vecMin = new Vector2(minX, minY);
            vecMax = new Vector2(maxX, maxY);
            IsEmpty = false;
        }

        public BBox2 Include(Vector2 pt)
        {
            if (IsEmpty)
                return new BBox2(pt, pt);

            return new BBox2(
                new Vector2(MathF.Min(vecMin.X, pt.X), MathF.Min(vecMin.Y, pt.Y)),
                new Vector2(MathF.Max(vecMax.X, pt.X), MathF.Max(vecMax.Y, pt.Y))
            );
        }

        public BBox2 Include(BBox2 other)
        {
            if (other.IsEmpty) return this;
            if (IsEmpty) return other;

            return new BBox2(
                new Vector2(MathF.Min(vecMin.X, other.vecMin.X), MathF.Min(vecMin.Y, other.vecMin.Y)),
                new Vector2(MathF.Max(vecMax.X, other.vecMax.X), MathF.Max(vecMax.Y, other.vecMax.Y))
            );
        }

        public bool Contains(Vector2 pt)
        {
            if (IsEmpty) return false;
            return pt.X >= vecMin.X && pt.X <= vecMax.X &&
                   pt.Y >= vecMin.Y && pt.Y <= vecMax.Y;
        }

        public override string ToString()
            => IsEmpty ? "BBox2 [Empty]" : $"BBox2 [Min: {vecMin}, Max: {vecMax}, Size: {Size}]";
    }
}
