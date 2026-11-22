using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Configuration options for the 2D Gmsh CFD mesher: farfield radius, boundary-layer resolution
    /// (y+, growth rate, minimum layers), contour and field cell sizes, and wake box.
    /// Loaded from <c>solvers/gmsh.json</c> and injected via constructor.
    /// </summary>
    public class Gmsh2DMesherOptions : ICalibrationMesherOptions
    {
        // --- Farfield (simulation domain) ---

        /// <summary>
        /// Circular farfield radius as a multiple of chord length c (default 500, matching NASA TMR grids;
        /// in 2D circulation decays only as 1/r, so too small a farfield distorts lift).
        /// Cells grow outward (<see cref="NearfieldGrowthDistanceFactor"/>), so the large farfield
        /// adds only a few thousand cells.
        /// </summary>
        public float FarfieldRadiusFactor { get; set; } = 500.0f;

        /// <summary>Number of segments for the farfield circular arcs.</summary>
        public int FarfieldCircleSegments { get; set; } = 64;

        // --- Boundary-layer resolution (BoundaryLayer Field) ---

        /// <summary>Target y+ for the first wall-normal cell height (default 1.0).</summary>
        public float TargetYPlus { get; set; } = 1.0f;

        /// <inheritdoc />
        string? ICalibrationMesherOptions.BoundaryLayerMode => "Quad";

        /// <inheritdoc />
        bool ICalibrationMesherOptions.Is3D => false;

        /// <summary>Layer-to-layer growth rate in the boundary layer (default 1.2).</summary>
        public float BoundaryLayerGrowthRate { get; set; } = 1.2f;

        /// <summary>Minimum number of boundary layers (default 4).</summary>
        public int BoundaryLayerMinLayers { get; set; } = 4;

        /// <summary>Fixed layer count; 0 = compute automatically from boundary-layer thickness delta.</summary>
        public int BoundaryLayerLayers { get; set; } = 0;

        // --- Cell sizes relative to chord length L at mesh factor 1.0 ---

        /// <summary>
        /// Contour cell size as a fraction of reference length/chord L at mesh factor 1.0 (e.g. 0.01 = L/100).
        /// </summary>
        public float SurfaceCellSizeFactor { get; set; } = 0.01f;

        /// <summary>
        /// Contour cell size in mm at mesh factor 1.0; &gt; 0 overrides SurfaceCellSizeFactor * L.
        /// </summary>
        public float SurfaceCellSizeMm { get; set; } = 0.0f;

        /// <summary>Nearfield cell size as a fraction of L at mesh factor 1.0 (e.g. 0.04 = L/25).</summary>
        public float NearfieldCellSizeFactor { get; set; } = 0.04f;

        /// <summary>
        /// Farfield cell size as a multiple of L at mesh factor 1.0 (default 25; at radius 500 c this yields
        /// ~1.3k farfield cells). Rule of thumb: radius / 20.
        /// </summary>
        public float FarfieldCellSizeFactor { get; set; } = 25.0f;

        /// <summary>
        /// Distance from the body (as a multiple of L) over which cell size grows from nearfield to
        /// farfield size. 0 = automatic 0.4 * <see cref="FarfieldRadiusFactor"/>. A long, smooth
        /// transition avoids skewed cells and saves cell count.
        /// </summary>
        public float NearfieldGrowthDistanceFactor { get; set; } = 0.0f;

        // --- Wake refinement (Wake Box) ---

        /// <summary>Enable/disable wake refinement box (default true in 2D).</summary>
        public bool WakeRefinement { get; set; } = true;

        /// <summary>Downstream wake-box length as a multiple of chord L (default 3.0).</summary>
        public float WakeLengthFactor { get; set; } = 3.0f;

        /// <summary>Half-width padding of the wake box in Y as a multiple of chord L (default 0.6).</summary>
        public float WakeWidthFactor { get; set; } = 0.6f;

        /// <summary>Cell size inside the wake box as a fraction of L at mesh factor 1.0 (default 0.04).</summary>
        public float WakeCellSizeFactor { get; set; } = 0.04f;

        // --- Detail box around and behind the model ---

        /// <summary>
        /// Very fine box around and behind the profile to resolve near-wake and vortices.
        /// Applies to all runs (no special-casing of angle of attack). Scales with the mesh
        /// refinement factor during calibration. Default true.
        /// </summary>
        public bool DetailBox { get; set; } = true;

        /// <summary>Detail-box length behind the trailing edge as a multiple of L (default 1.0).</summary>
        public float DetailBoxLengthFactor { get; set; } = 1.0f;

        /// <summary>Detail-box padding above and below the profile as a multiple of L (default 0.25).</summary>
        public float DetailBoxWidthFactor { get; set; } = 0.25f;

        /// <summary>Cell size inside the detail box as a fraction of L at mesh factor 1.0 (default 0.01 = L/100).</summary>
        public float DetailBoxCellSizeFactor { get; set; } = 0.01f;

        // --- Leading and trailing edges ---

        /// <summary>
        /// Factor on wall cell size at leading and trailing edges (default 0.25 = one quarter of
        /// contour cell size). Leading-edge suction peak and trailing-edge resolution dominate lift
        /// and drag accuracy (per NASA TMR). 1 = no extra edge refinement.
        /// </summary>
        public float EdgeRefinementFactor { get; set; } = 0.25f;

        /// <summary>Distance around leading and trailing edges (as a multiple of L) over which refinement fades out (default 0.1).</summary>
        public float EdgeRefinementDistanceFactor { get; set; } = 0.1f;
    }
}
