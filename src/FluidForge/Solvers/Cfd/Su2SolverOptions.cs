using System;
using System.Collections.Generic;
using System.Linq;

using FluidForge.Core;

namespace FluidForge.Solvers.Cfd
{
    /// <summary>
    /// Configuration options for the SU2 solver: fluid properties, reference values, CFL control,
    /// and result metric mapping.
    /// Loaded from <c>src/projects/&lt;Name&gt;/solvers/su2.json</c> and injected via constructor.
    /// </summary>
    public class Su2SolverOptions : ICalibrationSolverOptions
    {
        // --- Fluid properties ---
        /// <summary>Fluid density in kg/m³ (1025 = seawater).</summary>
        public float Density { get; set; } = 1025.0f;
        /// <summary>Dynamic viscosity in Pa·s.</summary>
        public float DynamicViscosity { get; set; } = 1.001e-3f;
        /// <summary>Turbulence model, e.g. SA or SST.</summary>
        public string TurbulenceModel { get; set; } = "SA";
        /// <summary>
        /// Optional Spalart-Allmaras freestream viscosity ratio (<c>FREESTREAM_NU_FACTOR</c>,
        /// $\tilde{\nu}_\infty / \nu_\infty$). <c>null</c> (default) omits the key so SU2's built-in
        /// default applies; ignored when <see cref="TurbulenceModel"/> is not <c>"SA"</c>.
        /// </summary>
        public float? FreestreamNuFactor { get; set; } = null;
        /// <summary>Speed of sound in m/s — converts Mach number into a freestream velocity.</summary>
        public float SpeedOfSound { get; set; } = 343.2f;

        /// <summary>
        /// Freestream velocity in m/s. The solver is incompressible — Mach number has no physical
        /// meaning here and only served as an indirect way to specify velocity.
        /// <para>
        /// Default: <c>2.0</c> m/s, matching standard seawater (typical AUV cruise speed).
        /// When &gt; 0, this value is used directly and <c>MachNumber</c>/<c>SpeedOfSound</c> are ignored
        /// for freestream velocity. <c>0</c> = legacy path <c>MachNumber * SpeedOfSound</c>.
        /// </para>
        /// <para>
        /// Caution with the legacy path in water: <c>SpeedOfSound</c> defaults to the speed of sound
        /// in <b>air</b>; Mach 0.1 yields 34.3 m/s, which is unrealistic for an underwater vehicle.
        /// </para>
        /// </summary>
        public float FreestreamVelocity { get; set; } = 2.0f;

        // --- Dimensions & Angle of Attack ---
        /// <summary>
        /// Spatial dimension of the simulation: 3 (default, 3D volume mesh) or 2 (2D planar mesh).
        /// In 2D lift is along the y-axis; in 3D lift is along the z-axis.
        /// </summary>
        public int Dimensions { get; set; } = 3;

        /// <summary>
        /// Angle of attack in degrees (alpha).
        /// Default: 0.0°. For alpha != 0, the velocity vector in FREESTREAM_VELOCITY and
        /// INC_VELOCITY_INIT is rotated (in the xy-plane for 2D, xz-plane for 3D).
        /// </summary>
        public float AngleOfAttackDeg { get; set; } = 0.0f;

        /// <summary>
        /// Symmetry plane for 3D half-model simulations: <c>"None"</c> (default), <c>"Y0"</c>, or <c>"Z0"</c>.
        /// Emits <c>MARKER_SYM= ( Symmetry )</c> and halves <c>REF_AREA</c> when clipped from a full body.
        /// </summary>
        public string SymmetryPlane { get; set; } = "None";

        /// <summary>
        /// Optional multi-point operating conditions evaluated sequentially on the same mesh.
        /// When empty, the single-point <see cref="FreestreamVelocity"/> / <see cref="AngleOfAttackDeg"/> path runs.
        /// </summary>
        public List<OperatingPointOptions> OperatingPoints { get; set; } = new();

        // --- Reference values ---
        /// <summary>SU2 REF_LENGTH.</summary>
        public float ReferenceLength { get; set; } = 0.01f;

        /// <summary>
        /// Metric name in <c>ModelRecord.PassiveParameters</c> used to compute SU2 REF_AREA.
        /// The value is read in mm² and converted to m² (the entire framework uses mm).
        /// Projects using a different reference area name configure it here; if missing at runtime,
        /// <see cref="Su2Solver"/> logs a warning.
        /// </summary>
        public string ReferenceAreaMetric { get; set; } = "FrontalArea";

        // --- Result metrics ---

        /// <summary>Code default for <see cref="ConvergenceField"/>.</summary>
        public const string DefaultConvergenceField = "DRAG";

        /// <summary>Code default for <see cref="HistoryOutput"/>.</summary>
        public static readonly string[] DefaultHistoryOutput = { "ITER", "RMS_RES", "AERO_COEFF" };

        /// <summary>
        /// Maps columns from SU2's <c>history.csv</c> into <see cref="ModelRecord"/>.
        /// <para>
        /// Keys are the names visible to the fitness function in <c>PassiveParameters</c>; values are
        /// column names in history.csv (<c>CD</c>, <c>CL</c>, <c>CSF</c>, <c>CMx</c>, <c>CMy</c>, <c>CMz</c>
        /// from <c>AERO_COEFF</c>). Adding lift is a single line in the project's su2.json.
        /// </para>
        /// <para>
        /// Default <c>{ "Drag" = "CD" }</c>. Nested JSON objects are merged <b>additively</b>:
        /// a project specifying only <c>{"Lift":"CL"}</c> still inherits <c>Drag</c> from the default.
        /// </para>
        /// </summary>
        public Dictionary<string, string> ResultMetrics { get; set; } = new() { ["Drag"] = "CD" };

        /// <inheritdoc />
        IReadOnlyCollection<string> ICalibrationSolverOptions.MonitoredMetricNames
            => (IReadOnlyCollection<string>?)ResultMetrics?.Keys ?? Array.Empty<string>();

        /// <summary>
        /// SU2 <c>CONV_FIELD</c> — the quantity SU2 monitors for convergence (e.g. <c>DRAG</c> or <c>LIFT</c>).
        /// Empty = unconfigured, falling back to <see cref="DefaultConvergenceField"/>.
        /// </summary>
        public string ConvergenceField { get; set; } = DefaultConvergenceField;

        /// <summary>
        /// SU2 <c>HISTORY_OUTPUT</c> — column groups written by SU2 into history.csv.
        /// <c>AERO_COEFF</c> provides all aerodynamic coefficients. Empty list = unconfigured,
        /// falling back to <see cref="DefaultHistoryOutput"/>.
        /// </summary>
        public string[] HistoryOutput { get; set; } = (string[])DefaultHistoryOutput.Clone();

        /// <summary>
        /// Threshold for relative oscillation of a result metric over the Cauchy window
        /// (default: 0.05 = 5%). If (max - min) / |mean| exceeds this limit, the solver warns
        /// about potential unsteady flow.
        /// </summary>
        public float OscillationWarnLimit { get; set; } = 0.05f;

        /// <summary>
        /// When <c>true</c> (default), a run whose metrics are still drifting monotonically in the tail
        /// window is marked as failed (<see cref="ModelRecord.SimulationFailed"/> = true). Runs that
        /// only oscillate (unsteady but statistically stationary) are not marked failed.
        /// </summary>
        public bool TreatUnconvergedAsFailed { get; set; } = true;

        /// <summary>
        /// When <c>true</c> (default), a steady SU2 run that stops via the Cauchy criterion before
        /// <c>Su2MaxIterations</c> while still drifting systematically is automatically continued via
        /// <c>RESTART_SOL= YES</c> with a tighter <c>CONV_CAUCHY_EPS</c>.
        /// </summary>
        public bool AutoRestartOnDrift { get; set; } = true;

        /// <summary>
        /// Maximum number of automatic steady restart blocks per variant (<c>0</c> = disabled).
        /// </summary>
        public int MaxConvergenceRestarts { get; set; } = 3;

        /// <summary>
        /// How result metrics are aggregated from <c>history.csv</c>:
        /// <list type="bullet">
        ///   <item><c>"TailMean"</c> (default): mean of the tail window excluding startup transient.</item>
        ///   <item><c>"Last"</c>: value of the final row.</item>
        ///   <item><c>"CauchyWindowMean"</c>: mean over the last <c>CauchyElements</c> rows.</item>
        /// </list>
        /// An unknown value logs a warning and falls back to <c>"Last"</c>.
        /// </summary>
        public string ResultAveraging { get; set; } = "TailMean";

        // --- CFL & numerical flux control ---
        /// <summary>
        /// Convective flow slope limiter for MUSCL reconstruction (<c>SLOPE_LIMITER_FLOW</c>).
        /// Default <c>"VENKATAKRISHNAN"</c>; e.g. <c>"NONE"</c>, <c>"VENKATAKRISHNAN"</c>,
        /// <c>"VENKATAKRISHNAN_WANG"</c>, <c>"BARTH_JESPERSEN"</c>, or <c>"VAN_ALBADA_EDGE"</c>.
        /// </summary>
        public string SlopeLimiterFlow { get; set; } = "VENKATAKRISHNAN";

        /// <summary>
        /// Venkatakrishnan limiter coefficient (<c>VENKAT_LIMITER_COEFF</c>) when
        /// <see cref="SlopeLimiterFlow"/> starts with <c>"VENKATAKRISHNAN"</c>.
        /// Must be finite and &gt; 0 (default <c>0.05f</c>).
        /// </summary>
        public float VenkatLimiterCoeff { get; set; } = 0.05f;

        public float CflNumber { get; set; } = 5.0f;
        public bool CflAdapt { get; set; } = true;
        /// <summary>CFL_ADAPT_PARAM: reduction factor.</summary>
        public float CflAdaptFactorDown { get; set; } = 0.5f;
        /// <summary>CFL_ADAPT_PARAM: growth factor.</summary>
        public float CflAdaptFactorUp { get; set; } = 1.2f;
        /// <summary>CFL_ADAPT_PARAM: lower bound.</summary>
        public float CflAdaptMin { get; set; } = 1.0f;
        /// <summary>CFL_ADAPT_PARAM: upper bound.</summary>
        public float CflAdaptMax { get; set; } = 50.0f;

        // --- y+ target and boundary-layer check ---
        /// <summary>
        /// Target y+ for wall boundary-layer resolution (default 1.5 in 3D).
        /// </summary>
        public float TargetYPlus { get; set; } = 1.5f;

        /// <summary>
        /// Warning multiplier for post-simulation y+ check (default 3.0).
        /// If the 95th percentile (P95) exceeds TargetYPlus * YPlusWarnFactor, the solver logs a warning.
        /// </summary>
        public float YPlusWarnFactor { get; set; } = 3.0f;

        // --- Time-accurate mode / URANS ---
        /// <summary>
        /// Settings for time-accurate simulations (URANS with dual time-stepping).
        /// </summary>
        public Su2TimeDomainOptions TimeDomain { get; set; } = new();

        /// <summary>
        /// Creates a deep copy of the options (especially for temporary overrides such as angle of attack).
        /// </summary>
        public Su2SolverOptions Clone()
        {
            var copy = (Su2SolverOptions)MemberwiseClone();
            copy.ResultMetrics = new Dictionary<string, string>(ResultMetrics);
            copy.HistoryOutput = (string[])HistoryOutput.Clone();
            copy.OperatingPoints = OperatingPoints == null
                ? new List<OperatingPointOptions>()
                : OperatingPoints.Select(op => op == null ? new OperatingPointOptions() : new OperatingPointOptions
                {
                    Name = op.Name,
                    Velocity = op.Velocity,
                    AngleOfAttackDeg = op.AngleOfAttackDeg,
                    Weight = op.Weight
                }).ToList();
            return copy;
        }
    }

    /// <summary>
    /// Single operating point in a multi-point SU2 evaluation (<see cref="Su2SolverOptions.OperatingPoints"/>).
    /// </summary>
    public sealed class OperatingPointOptions
    {
        /// <summary>Optional display label (e.g. <c>"CruiseLow"</c>).</summary>
        public string Name { get; set; } = "";

        /// <summary>Optional freestream velocity in m/s; <c>null</c> inherits <see cref="Su2SolverOptions.FreestreamVelocity"/>.</summary>
        public float? Velocity { get; set; }

        /// <summary>Angle of attack in degrees for this operating point.</summary>
        public float AngleOfAttackDeg { get; set; } = 0.0f;

        /// <summary>Positive aggregation weight for objective/metric averaging (normalized automatically).</summary>
        public float Weight { get; set; } = 1.0f;
    }

    /// <summary>
    /// Options for time-accurate CFD runs with SU2 (URANS, dual time-stepping).
    /// </summary>
    public class Su2TimeDomainOptions
    {
        /// <summary>Enable time-accurate simulation (URANS, dual time-stepping).</summary>
        public bool Enabled { get; set; } = false;

        /// <summary>Physical time step in seconds (0 = automatic from StrouhalEstimate and StepsPerPeriod).</summary>
        public float TimeStep { get; set; } = 0.0f;

        /// <summary>
        /// Time steps per vortex-shedding period when TimeStep = 0 (default 50).
        /// </summary>
        public int StepsPerPeriod { get; set; } = 50;

        /// <summary>Estimated Strouhal number f = St * U / L for automatic time-step sizing.</summary>
        public float StrouhalEstimate { get; set; } = 0.2f;

        /// <summary>
        /// Number of physical time steps to execute (counted in addition to <see cref="RestartIteration"/>
        /// when restarting from a steady solution). Default 1000 = 20 periods at 50 steps per period.
        /// </summary>
        public int TimeIterations { get; set; } = 1000;

        /// <summary>Number of inner pseudo-time iterations per physical time step (INNER_ITER).</summary>
        public int InnerIterations { get; set; } = 30;

        /// <summary>
        /// Fraction of time steps after which time-averaging starts (WINDOW_START_ITER). 0.5 over 20 periods =
        /// average over the final 10 periods.
        /// </summary>
        public float AveragingStartFraction { get; set; } = 0.5f;

        /// <summary>
        /// Restart from a steady solution: 0 = start from freestream, 2 = start from
        /// <c>restart_00000.dat</c> and <c>restart_00001.dat</c> (SU2 requires two time levels for 2nd order).
        /// Recheck prepares these files from the steady run and sets this value automatically.
        /// </summary>
        public int RestartIteration { get; set; } = 0;
    }
}
