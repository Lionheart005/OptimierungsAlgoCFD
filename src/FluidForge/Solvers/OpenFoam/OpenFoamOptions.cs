using System;
using System.Collections.Generic;
using System.Linq;
using FluidForge.Core;

namespace FluidForge.Solvers.OpenFoam
{
    /// <summary>
    /// Configuration options for OpenFOAM meshing (<c>snappyHexMesh</c> / <c>cfMesh</c>)
    /// and steady-state CFD (<c>simpleFoam</c>), loaded from
    /// <c>src/projects/&lt;Name&gt;/solvers/openfoam.json</c>.
    /// </summary>
    public class OpenFoamOptions : ICalibrationSolverOptions
    {
        // --- Domain & backend ---

        /// <summary>Flow domain mode: <c>"Internal3D"</c> (default) or <c>"External3D"</c>.</summary>
        public string DomainMode { get; set; } = "Internal3D";

        /// <summary>Meshing backend: <c>"SnappyHexMesh"</c> (default) or <c>"CfMesh"</c>.</summary>
        public string MesherBackend { get; set; } = "SnappyHexMesh";

        /// <summary>When true, falls back to <c>cfMesh</c> (<c>cartesianMesh</c>) if <c>snappyHexMesh</c> fails.</summary>
        public bool FallbackToCfMesh { get; set; } = false;

        /// <summary>OpenFOAM solver application (default <c>"simpleFoam"</c>).</summary>
        public string Solver { get; set; } = "simpleFoam";

        /// <summary>
        /// Turbulence model: <c>"kOmegaSST"</c> (default), <c>"kOmegaSSTLM"</c>,
        /// <c>"kEpsilon"</c>, <c>"SpalartAllmaras"</c>, or <c>"laminar"</c>.
        /// </summary>
        public string TurbulenceModel { get; set; } = "kOmegaSST";

        /// <summary>Inlet turbulence intensity as a fraction (default 0.05 = 5%).</summary>
        public float TurbulenceIntensity { get; set; } = 0.05f;

        /// <summary>Hydraulic diameter in meters (0 = auto-derived from inlet patch area).</summary>
        public float HydraulicDiameter { get; set; } = 0.0f;

        // --- Fluid & boundary conditions ---

        /// <summary>Fluid density in kg/m^3 (default 998.2 = water at 20 C).</summary>
        public float Density { get; set; } = 998.2f;

        /// <summary>Dynamic viscosity in Pa*s (default 1.002e-3 = water at 20 C).</summary>
        public float DynamicViscosity { get; set; } = 1.002e-3f;

        /// <summary>
        /// Inlet boundary condition type: <c>"Velocity"</c> (default),
        /// <c>"VolumetricFlowRate"</c>, or <c>"MassFlowRate"</c>.
        /// </summary>
        public string InletCondition { get; set; } = "Velocity";

        /// <summary>Inlet flow speed in m/s when <see cref="InletCondition"/> is <c>"Velocity"</c>.</summary>
        public float InletVelocity { get; set; } = 1.0f;

        /// <summary>Inlet volumetric flow rate in m^3/s when <see cref="InletCondition"/> is <c>"VolumetricFlowRate"</c>.</summary>
        public float InletVolumetricFlowRate { get; set; } = 0.0f;

        /// <summary>Inlet mass flow rate in kg/s when <see cref="InletCondition"/> is <c>"MassFlowRate"</c>.</summary>
        public float InletMassFlowRate { get; set; } = 0.0f;

        /// <summary>
        /// Optional explicit unit/non-zero inlet velocity direction <c>[dx, dy, dz]</c>.
        /// Empty = auto-derived from negated area-weighted inlet STL normal (or AoA for External3D).
        /// </summary>
        public float[] InletDirection { get; set; } = Array.Empty<float>();

        /// <summary>Fixed kinematic outlet pressure <c>p/rho</c> in m^2/s^2 (default 0.0).</summary>
        public float OutletPressure { get; set; } = 0.0f;

        /// <summary>Angle of attack in degrees for <c>External3D</c> runs (rotated in the XZ plane).</summary>
        public float AngleOfAttackDeg { get; set; } = 0.0f;

        /// <summary>
        /// Symmetry plane for <c>External3D</c>: <c>"None"</c> (default), <c>"Y0"</c>, or <c>"Z0"</c>.
        /// </summary>
        public string SymmetryPlane { get; set; } = "None";

        // --- Reference quantities ---

        /// <summary>Reference length in meters for Reynolds number and <c>forceCoeffs</c>.</summary>
        public float ReferenceLength { get; set; } = 0.1f;

        /// <summary>Explicit reference area in m^2 for <c>forceCoeffs</c> (0 = read from <see cref="ReferenceAreaMetric"/>).</summary>
        public float ReferenceArea { get; set; } = 0.0f;

        /// <summary>Geometry metric name (in mm^2) used as fallback <c>Aref</c> for <c>External3D</c>.</summary>
        public string ReferenceAreaMetric { get; set; } = "FrontalArea";

        // --- External3D domain clearances ---

        /// <summary>Upstream wind-tunnel clearance as a multiple of body length L (External3D).</summary>
        public float TunnelUpstreamFactor { get; set; } = 5.0f;

        /// <summary>Downstream wind-tunnel clearance as a multiple of body length L (External3D).</summary>
        public float TunnelDownstreamFactor { get; set; } = 15.0f;

        /// <summary>Lateral wind-tunnel clearance as a multiple of body width/height (External3D).</summary>
        public float TunnelLateralFactor { get; set; } = 5.0f;

        // --- Patch names ---

        /// <summary>Inlet patch name in multi-solid STL and OpenFOAM dictionaries.</summary>
        public string InletPatchName { get; set; } = "inlet";

        /// <summary>Prefix matching outlet patches (e.g. <c>outlet</c>, <c>outlet1</c>, <c>outlet2</c>).</summary>
        public string OutletPatchPrefix { get; set; } = "outlet";

        /// <summary>Wall patch name in multi-solid STL and OpenFOAM dictionaries.</summary>
        public string WallPatchName { get; set; } = "wall";

        // --- Meshing parameters ---

        /// <summary>Base background cell size in mm at refinement factor 1.0 (default 2.0 mm; 0 = auto-sized from characteristic geometry length).</summary>
        public float BaseCellSizeMm { get; set; } = 2.0f;

        /// <summary>
        /// Target surface cell count per characteristic length (<c>D_h</c> for Internal3D, <c>L</c> for External3D)
        /// when <see cref="BaseCellSizeMm"/> &lt;= 0 (0 = domain default: 20 for Internal3D, 120 for External3D).
        /// </summary>
        public float CellsPerCharacteristicLength { get; set; } = 0.0f;

        /// <summary>Minimum surface refinement level in <c>snappyHexMesh</c>.</summary>
        public int SurfaceRefinementLevelMin { get; set; } = 1;

        /// <summary>Maximum surface refinement level in <c>snappyHexMesh</c>.</summary>
        public int SurfaceRefinementLevelMax { get; set; } = 2;

        /// <summary>Feature edge angle threshold in degrees for <c>snappyHexMesh</c>.</summary>
        public float FeatureAngleDeg { get; set; } = 30.0f;

        /// <summary>Enable boundary-layer prism extrusion (<c>addLayers</c> / <c>cfMesh</c> boundaryLayers).</summary>
        public bool AddLayers { get; set; } = true;

        /// <summary>Use relative layer sizing in <c>snappyHexMesh</c> (true = <see cref="FinalLayerThickness"/>, false = <see cref="FirstLayerThickness"/>).</summary>
        public bool RelativeLayerSizes { get; set; } = true;

        /// <summary>Number of wall boundary layers (0 = auto-sized from <see cref="TargetYPlus"/> and surface cell size).</summary>
        public int BoundaryLayerLayers { get; set; } = 5;

        /// <summary>Layer-to-layer expansion ratio.</summary>
        public float BoundaryLayerExpansionRatio { get; set; } = 1.2f;

        /// <summary>First layer thickness (meters when <see cref="RelativeLayerSizes"/> is false).</summary>
        public float FirstLayerThickness { get; set; } = 0.05f;

        /// <summary>Final layer thickness ratio (when <see cref="RelativeLayerSizes"/> is true).</summary>
        public float FinalLayerThickness { get; set; } = 0.5f;

        /// <summary>Minimum allowed layer thickness (relative or meters).</summary>
        public float MinLayerThickness { get; set; } = 0.1f;

        /// <summary>Maximum cells per MPI rank during <c>snappyHexMesh</c>.</summary>
        public int MaxLocalCells { get; set; } = 4_000_000;

        /// <summary>Maximum global cells during <c>snappyHexMesh</c>.</summary>
        public int MaxGlobalCells { get; set; } = 15_000_000;

        /// <summary>Hard <c>checkMesh</c> quality gate on maximum non-orthogonality in degrees.</summary>
        public float MaxNonOrthogonality { get; set; } = 65.0f;

        /// <summary>Hard <c>checkMesh</c> quality gate on maximum skewness.</summary>
        public float MaxSkewness { get; set; } = 4.0f;

        /// <summary>Hard gate on minimum wall boundary-layer coverage percentage when <see cref="AddLayers"/> is true.</summary>
        public float MinLayerCoveragePercent { get; set; } = 50.0f;

        // --- Solver & convergence controls ---

        /// <summary>Maximum steady-state iterations (<c>endTime</c> in <c>controlDict</c>).</summary>
        public int EndTime { get; set; } = 1000;

        /// <summary>Field write interval (<c>writeInterval</c> in <c>controlDict</c>).</summary>
        public int WriteInterval { get; set; } = 200;

        /// <summary>SIMPLE residual control target for pressure and velocity/turbulence fields.</summary>
        public float ResidualTarget { get; set; } = 1e-4f;

        /// <summary>Use second-order bounded linearUpwind schemes for turbulence transport (<c>k</c>, <c>omega</c>, <c>epsilon</c>, <c>nuTilda</c>).</summary>
        public bool SecondOrderTurbulence { get; set; } = false;

        /// <summary>Automatically restart <c>simpleFoam</c> from <c>latestTime</c> with tightened <c>residualControl</c> when trailing-window drift exceeds tolerance.</summary>
        public bool AutoRestartOnDrift { get; set; } = true;

        /// <summary>Maximum number of automatic convergence restarts when <see cref="AutoRestartOnDrift"/> is true.</summary>
        public int MaxConvergenceRestarts { get; set; } = 3;

        /// <summary>Lower floor for automatic <c>residualControl</c> tightening during drift restarts.</summary>
        public float ResidualTargetFloor { get; set; } = 1e-7f;

        /// <summary>Run <c>potentialFoam</c> prior to <c>simpleFoam</c> for velocity initialization.</summary>
        public bool UsePotentialFoamInit { get; set; } = false;

        /// <summary>Hard verification gate: maximum allowed mass-balance error in percent (default 1.0%).</summary>
        public float MaxMassBalanceErrorPercent { get; set; } = 1.0f;

        /// <summary>Trailing window length (iterations/samples) for drift and metric averaging.</summary>
        public int TrailingWindowIterations { get; set; } = 100;

        /// <summary>Maximum allowed relative drift across the trailing window.</summary>
        public float RelativeDriftTolerance { get; set; } = 0.005f;

        /// <summary>
        /// Optional per-metric absolute drift floor overrides (see <see cref="MetricTolerance"/>).
        /// Empty = use <see cref="MetricTolerance.DefaultAbsolute"/>.
        /// </summary>
        public Dictionary<string, float> AbsoluteTolerances { get; set; } = new();

        /// <summary>Mark unconverged runs as failed (<see cref="ModelRecord.SimulationFailed"/> = true).</summary>
        public bool TreatUnconvergedAsFailed { get; set; } = true;

        /// <summary>Target wall y+ for calibration and post-solve warnings.</summary>
        public float TargetYPlus { get; set; } = 5.0f;

        /// <summary>Warning multiplier on <see cref="TargetYPlus"/>.</summary>
        public float YPlusWarnFactor { get; set; } = 3.0f;

        /// <summary>Run <c>foamToVTK</c> after solving to export VTK post-processing files.</summary>
        public bool ExportVtk { get; set; } = false;

        // --- Execution & customization ---

        /// <summary>
        /// Explicit path to OpenFOAM's <c>etc/bashrc</c> (empty = auto-detect
        /// <c>/usr/lib/openfoam/openfoam2506/etc/bashrc</c> or fallback paths).
        /// </summary>
        public string OpenFoamBashrcPath { get; set; } = "";

        /// <summary>
        /// MPI launcher used for OpenFOAM parallel runs (default <c>/usr/bin/mpirun</c>,
        /// isolated from Conda/MPICH).
        /// </summary>
        public string MpiRunPath { get; set; } = "/usr/bin/mpirun";

        /// <summary>Optional directory of custom OpenFOAM template files overriding generated dictionaries.</summary>
        public string CustomTemplateDirectory { get; set; } = "";

        /// <summary>
        /// Per-dictionary key overrides, e.g. <c>{ "system/fvSolution": { "nNonOrthogonalCorrectors": "2" } }</c>.
        /// </summary>
        public Dictionary<string, Dictionary<string, string>> CustomDictionaryOverrides { get; set; } = new();

        /// <summary>
        /// Maps output metric names in <see cref="ModelRecord.PassiveParameters"/> to post-processed keys
        /// (e.g. <c>{ "PressureDrop": "PressureDrop" }</c> or <c>{ "Drag": "Drag", "Lift": "Lift" }</c>).
        /// </summary>
        public Dictionary<string, string> ResultMetrics { get; set; } = new() { ["PressureDrop"] = "PressureDrop" };

        // --- ICalibrationSolverOptions ---

        /// <inheritdoc />
        int ICalibrationSolverOptions.Dimensions => 3;

        /// <inheritdoc />
        IReadOnlyCollection<string> ICalibrationSolverOptions.MonitoredMetricNames
            => (IReadOnlyCollection<string>?)ResultMetrics?.Keys ?? Array.Empty<string>();

        /// <summary>Creates a deep copy of this <see cref="OpenFoamOptions"/> instance.</summary>
        public OpenFoamOptions Clone()
        {
            var copy = (OpenFoamOptions)MemberwiseClone();
            copy.InletDirection = InletDirection != null ? (float[])InletDirection.Clone() : Array.Empty<float>();
            copy.ResultMetrics = ResultMetrics != null
                ? new Dictionary<string, string>(ResultMetrics, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            copy.AbsoluteTolerances = AbsoluteTolerances != null
                ? new Dictionary<string, float>(AbsoluteTolerances, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            copy.CustomDictionaryOverrides = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
            if (CustomDictionaryOverrides != null)
            {
                foreach (var kvp in CustomDictionaryOverrides)
                {
                    copy.CustomDictionaryOverrides[kvp.Key] = kvp.Value != null
                        ? new Dictionary<string, string>(kvp.Value, StringComparer.OrdinalIgnoreCase)
                        : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                }
            }
            return copy;
        }
    }
}
