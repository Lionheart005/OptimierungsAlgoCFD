using System.Collections.Generic;

namespace FluidForge.Core
{
    /// <summary>
    /// Kind of generated geometry. Defaults to 3D surface mesh (SurfaceStl);
    /// planar contours for 2D simulations use Contour2D.
    /// </summary>
    public enum GeometryKind
    {
        SurfaceStl,
        Contour2D
    }

    /// <summary>
    /// How a geometry metric is scaled back from the kernel's working size to physical size.
    /// Declared explicitly by the project so the core never guesses from metric names.
    /// </summary>
    public enum MetricScaling
    {
        /// <summary>Dimensionless (e.g., ratios, counts) — keep unchanged.</summary>
        None,
        /// <summary>Length (mm) — multiply by 1 / ShrinkFactor.</summary>
        Linear,
        /// <summary>Area (mm²) — scale quadratically.</summary>
        Area,
        /// <summary>Volume (mm³) — scale cubically.</summary>
        Volume
    }

    /// <summary>
    /// Flexible output of geometry generation.
    /// Each project returns the metrics it needs along with how each metric scales.
    /// </summary>
    public class GeometryResult
    {
        /// <summary>
        /// Kind of geometry (3D STL or 2D contour).
        /// </summary>
        public GeometryKind Kind { get; set; } = GeometryKind.SurfaceStl;

        public string StlPath { get; set; } = "";

        /// <summary>
        /// Path to the geometry file (STL for 3D, JSON/SVG for 2D).
        /// Alias for <see cref="StlPath"/> for full backward compatibility.
        /// </summary>
        public string GeometryPath { get => StlPath; set => StlPath = value; }

        public Dictionary<string, float> Metrics { get; set; } = new();
        // e.g., {"Volume": 4200, "FrontalArea": 850, "SensorDistance": 120}
        // or {"Volume": 3000, "SurfaceArea": 1200, "WallThickness": 2.5}

        /// <summary>
        /// Rescaling dimension per metric. Missing entries default to
        /// <see cref="MetricScaling.None"/> (kept unchanged).
        /// </summary>
        public Dictionary<string, MetricScaling> MetricScalings { get; set; } = new();

        /// <summary>
        /// Adds a metric along with its scaling dimension — preferred API for projects.
        /// </summary>
        public void AddMetric(string name, float value, MetricScaling scaling = MetricScaling.None)
        {
            Metrics[name] = value;
            MetricScalings[name] = scaling;
        }

        /// <summary>
        /// Declared scaling dimension of a metric; returns <see cref="MetricScaling.None"/> if unspecified.
        /// </summary>
        public MetricScaling ScalingFor(string name)
            => MetricScalings.TryGetValue(name, out var scaling) ? scaling : MetricScaling.None;
    }
}
