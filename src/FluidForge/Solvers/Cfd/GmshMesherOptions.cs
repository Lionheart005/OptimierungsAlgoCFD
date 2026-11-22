using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Configuration options for the Gmsh CFD mesher: wind-tunnel dimensions and
    /// boundary-layer field. Loaded from <c>src/projects/&lt;Name&gt;/solvers/gmsh.json</c> and injected
    /// via constructor. Defaults are tuned for accurate simulations
    /// (1000 × 300 × 300 mm tunnel with 14 L wake, prism boundary layer, wake and detail box).
    /// </summary>
    public class GmshMesherOptions : ICalibrationMesherOptions
    {
        // --- Wind tunnel (simulation domain) in mm ---

        /// <summary>
        /// Shape of the simulation domain: <c>"Box"</c> (default) or
        /// <c>"Cylinder"</c>. Case- and whitespace-insensitive; an unknown
        /// name logs a warning and falls back to <c>"Box"</c>.
        /// </summary>
        public string TunnelShape { get; set; } = "Box";

        /// <summary>Edge lengths of the box (only when <see cref="TunnelShape"/> = "Box").</summary>
        public float TunnelSizeX { get; set; } = 1000.0f;
        public float TunnelSizeY { get; set; } = 300.0f;
        public float TunnelSizeZ { get; set; } = 300.0f;

        /// <summary>
        /// Center of the wind tunnel; the model sits at the origin. Default X = 200 mm: for a 50 mm model
        /// in a 1000 mm tunnel this gives 300 mm = 6 L upstream and 700 mm = 14 L downstream.
        /// Recommendation for wake capture: shift the tunnel center downstream (positive X) so at least
        /// 10 body lengths (&gt;= 10 L) remain behind the model.
        /// </summary>
        public float TunnelCenterX { get; set; } = 200.0f;
        public float TunnelCenterY { get; set; } = 0.0f;
        public float TunnelCenterZ { get; set; } = 0.0f;

        // --- Cylindrical wind tunnel (only when TunnelShape = "Cylinder") ---

        /// <summary>Cross-flow diameter; matches the box cross-section.</summary>
        public float TunnelDiameter { get; set; } = 300.0f;

        /// <summary>Length along X (flow direction).</summary>
        public float TunnelLength { get; set; } = 1000.0f;

        /// <summary>Circumferential segments — controls lateral surface resolution.</summary>
        public int TunnelSegments { get; set; } = 64;

        // --- Physical domain guarantees (minimum clearances & blockage ratio) ---

        /// <summary>Minimum upstream clearance ahead of the model nose as a multiple of model length L (default 5.0).</summary>
        public float TunnelMinUpstreamFactor { get; set; } = 5.0f;

        /// <summary>Minimum downstream clearance behind the model tail as a multiple of model length L (default 15.0).</summary>
        public float TunnelMinDownstreamFactor { get; set; } = 15.0f;

        /// <summary>Minimum lateral clearance in ±Y and ±Z as a multiple of width B or height H (default 5.0).</summary>
        public float TunnelMinLateralFactor { get; set; } = 5.0f;

        /// <summary>
        /// Maximum allowed blockage ratio A_frontal / A_tunnel in percent (default 0.3%).
        /// Values &lt;= 0 disable automatic blockage expansion.
        /// </summary>
        public float TunnelMaxBlockagePercent { get; set; } = 0.3f;

        // --- Boundary-layer field (Gmsh Threshold Field) ---
        /// <summary>Smallest cell size directly at the wall.</summary>
        public float BoundaryLayerSizeMin { get; set; } = 1.2f;
        /// <summary>Largest cell size far from the model.</summary>
        public float BoundaryLayerSizeMax { get; set; } = 120.0f;
        /// <summary>Distance up to which <see cref="BoundaryLayerSizeMin"/> applies.</summary>
        public float BoundaryLayerDistMin { get; set; } = 3.0f;
        /// <summary>Distance beyond which <see cref="BoundaryLayerSizeMax"/> applies.</summary>
        public float BoundaryLayerDistMax { get; set; } = 40.0f;

        // --- Wake refinement (Wake Box) ---
        /// <summary>
        /// Enable/disable wake refinement (default true: wake resolution affects pressure drag).
        /// </summary>
        public bool WakeRefinement { get; set; } = true;

        /// <summary>Downstream wake-box length as a multiple of model length lenX.</summary>
        public float WakeLengthFactor { get; set; } = 2.0f;

        /// <summary>Half-width padding of the wake box in Y and Z as a multiple of lenY and lenZ.</summary>
        public float WakeWidthFactor { get; set; } = 0.5f;

        /// <summary>
        /// Cell size inside the wake box in mm at mesh factor 1.0; 0 = automatic lenX / 6.0 in the .geo script.
        /// </summary>
        public float WakeCellSize { get; set; } = 0.0f;

        // --- Detail box around and behind the model ---
        /// <summary>
        /// Fine box around and behind the model (near-wake and vortices). Default true;
        /// in 3D it adds significant cell count, so projects like MantaAuv may disable it.
        /// Cell size scales with the mesh refinement factor during calibration.
        /// </summary>
        public bool DetailBox { get; set; } = true;

        /// <summary>Detail-box length behind the model as a multiple of model length lenX (default 1.5).</summary>
        public float DetailBoxLengthFactor { get; set; } = 1.5f;

        /// <summary>Padding in Y and Z as a multiple of lenY and lenZ (default 0.25).</summary>
        public float DetailBoxWidthFactor { get; set; } = 0.25f;

        /// <summary>
        /// Minimum Z-padding of the detail box as a fraction of model length L (default 0.15) so
        /// slender/flat bodies are not starved by a tiny <c>DetailBoxWidthFactor * lenZ</c>.
        /// Values &lt;= 0 disable the minimum length padding.
        /// </summary>
        public float DetailBoxMinHeightLengthFactor { get; set; } = 0.15f;

        /// <summary>Cell size inside the detail box in mm at mesh factor 1.0; 0 = automatic 2.5 * surface cell size.</summary>
        public float DetailBoxCellSize { get; set; } = 0.0f;

        // --- Curvature-based mesh refinement ---
        /// <summary>
        /// Enable Mesh.MeshSizeFromCurvature (default true); refines curved surfaces and edges.
        /// </summary>
        public bool MeshSizeFromCurvature { get; set; } = true;

        /// <summary>
        /// Lower bound for curvature-based refinement as a factor on <c>surfaceSize</c> (default 0.5,
        /// clamped internally to [0.1, 1.0]) to prevent tiny voxel artifacts/fillets from exploding cell count.
        /// </summary>
        public float CurvatureMinSizeFactor { get; set; } = 0.5f;

        // --- Structured boundary layer ---
        /// <summary>
        /// Boundary-layer meshing mode:
        /// <list type="bullet">
        ///   <item><c>"Prism"</c> (default): Structured prism boundary layer extruded from surface triangles (y+ ≈ 1).</item>
        ///   <item><c>"Threshold"</c>: Unstructured tetrahedral mesh with Gmsh Threshold distance field (y+ ≈ 30–70, coarse for SA).</item>
        ///   <item><c>"Hexa"</c>: Structured hexahedral boundary layer extruded from recombined quads.</item>
        /// </list>
        /// </summary>
        public string BoundaryLayerMode { get; set; } = "Prism";

        /// <summary>Target y+ for the first wall-normal cell height (default 1.5).</summary>
        public float TargetYPlus { get; set; } = 1.5f;

        /// <inheritdoc />
        bool ICalibrationMesherOptions.Is3D => true;

        /// <summary>Minimum number of boundary layers (default 4).</summary>
        public int BoundaryLayerMinLayers { get; set; } = 4;

        /// <summary>Layer-to-layer growth rate in the boundary layer (default 1.28).</summary>
        public float BoundaryLayerGrowthRate { get; set; } = 1.28f;

        /// <summary>Fixed layer count; 0 = compute automatically from boundary-layer thickness delta.</summary>
        public int BoundaryLayerLayers { get; set; } = 0;

        /// <summary>
        /// Multiplier on boundary-layer thickness to cover: wall layer thickness targets <c>factor * δ</c> (default 1.0).
        /// Literature often recommends 1.2–1.5 so the full boundary layer stays inside the structured zone.
        /// Only applies when <see cref="BoundaryLayerLayers"/> = 0 (automatic layer count).
        /// </summary>
        public float BoundaryLayerCoverage { get; set; } = 1.0f;

        /// <summary>Surface cell size in mm at mesh factor 1.0; 0 = uses BoundaryLayerSizeMin.</summary>
        public float SurfaceCellSize { get; set; } = 0.0f;

        /// <summary>Elements per 2*Pi radians for curvature-based refinement (default 20).</summary>
        public int CurvatureElements { get; set; } = 20;

        /// <summary>Angle threshold in degrees for Gmsh ClassifySurfaces when remeshing the part surface (default 40.0).</summary>
        public float ClassifyAngleDeg { get; set; } = 40.0f;

        /// <summary>
        /// Symmetry plane for half-model 3D meshing: <c>"None"</c> (default), <c>"Y0"</c> (keeps Y &gt;= 0),
        /// or <c>"Z0"</c> (keeps Z &gt;= 0). Case-insensitive; synchronized with <c>solvers/su2.json</c>.
        /// </summary>
        public string SymmetryPlane { get; set; } = "None";
    }
}
