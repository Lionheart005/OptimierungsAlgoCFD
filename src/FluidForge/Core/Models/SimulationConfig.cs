namespace FluidForge.Core
{
    /// <summary>
    /// Framework-wide configuration (formerly GlobalConfig inside SimulationData).
    /// Contains settings that apply to ALL projects.
    /// </summary>
    public class SimulationConfig
    {
        /// <summary>
        /// Optional physics &amp; workflow preset (Layer 1.5), e.g. <c>"External3D:StandardRans"</c>,
        /// <c>"External2D:StandardRans"</c>, or <c>"Custom"</c> / empty for none.
        /// </summary>
        public string Preset { get; set; } = "";

        // --- EXTERNAL TOOL PATHS ---
        //
        // PATH-relative by default. .NET resolves bare executable names via PATH in
        // Process.StartInfo.FileName, and Su2Path is passed as an argument to mpirun.
        // Run `.\scripts\sim.ps1 doctor` to verify tool availability before a run.
        public string MpiRunPath { get; set; } = "mpirun";
        public string Su2Path { get; set; } = "SU2_CFD";
        public string GmshPath { get; set; } = "gmsh";

        // 1. Time & Budget
        public int MaxIterations { get; set; } = 10;
        public int VariantsPerIteration { get; set; } = 10;

        /// <summary>
        /// Automatic early stopping: ends the run early when the best fitness barely improves
        /// anymore. <see cref="MaxIterations"/> × <see cref="VariantsPerIteration"/> remains the upper bound.
        /// </summary>
        public AutoStopConfig AutoStop { get; set; } = new();

        public float BaseVoxelResolution { get; set; } = 0.5f;
        /// <summary>
        /// For 3D <c>GmshCfdMesher</c> runs, automatically refines the effective PicoGK starting
        /// voxel size (down to a 0.1 mm safety floor) if the real voxel size exceeds 80% of the
        /// target surface cell size. Does not mutate the saved configuration or fingerprint.
        /// </summary>
        public bool AutoVoxelResolution { get; set; } = false;
        public float TargetPicoGkSize { get; set; } = 50.0f;
        public int VoxelSmoothingIterations { get; set; } = 40;
        public int VoxelSmoothingPremeltingSteps { get; set; } = 2;

        // 1a. Toggleable Framework Building Blocks
        /// <summary>
        /// Scales geometry to <see cref="TargetPicoGkSize"/> and rescales metrics back.
        /// When disabled, the geometry kernel operates directly in real dimensions (ScaleFactor = 1).
        /// </summary>
        public bool UseRubberBandScaler { get; set; } = true;
        /// <summary>
        /// Smooths the exported STL. When disabled, the STL is exported directly from the voxel model.
        /// </summary>
        public bool UseStlSmoothing { get; set; } = true;
        /// <summary>
        /// Computes <c>FrontalArea</c> in 3D as the true projected silhouette (shadow area in the
        /// YZ plane perpendicular to flow direction X) instead of the bounding-box rectangle (<c>Size.Y × Size.Z</c>).
        /// </summary>
        public bool UseProjectedFrontalArea { get; set; } = true;

        // 2. CFD & SU2 Settings
        public float MachNumber { get; set; } = 0.1f;

        /// <summary>
        /// Global mesh refinement factor. Default is 1.0 (baseline sizes from gmsh.json).
        /// Values &lt; 1.0 refine the mesh (e.g., 0.5 halves all target cell sizes);
        /// values &gt; 1.0 coarsen it for fast preliminary runs.
        /// </summary>
        public float MeshRefinementFactor { get; set; } = 1.0f;

        /// <summary>
        /// Maximum SU2 iterations per normal variant. 3000 is sufficient for RANS with y+ ≈ 1
        /// in 2D and 3D; well-behaved runs stop earlier via the Cauchy criterion.
        /// </summary>
        public int Su2MaxIterations { get; set; } = 3000;
        public int CauchyElements { get; set; } = 300;
        public string CauchyTolerance { get; set; } = "1e-5";

        /// <summary>
        /// Maximum allowed iterative error of a normal variant as a fraction of the mesh tolerance
        /// (<see cref="MeshCalibrationConfig.Tolerance"/>) to be considered converged.
        /// 0.25 = one quarter (e.g., at 2% mesh tolerance, at most 0.5% drift in the trailing window).
        /// </summary>
        public float IterativeToleranceFraction { get; set; } = 0.25f;

        /// <summary>
        /// Strict profile for control runs (mesh calibration, fine verification, recheck).
        /// Normal variants and bouncer checks (variant 99) use the settings above.
        /// </summary>
        public ControlRunConfig ControlRuns { get; set; } = new();
        public int MpiCores { get; set; } = 12;
        public int GmshCores { get; set; } = 12;

        // 3. Optimization Algorithm Selection
        /// <summary>
        /// Optimization algorithm to run: <c>"Bayes"</c> (default), <c>"IdwBaseline"</c> (legacy alias <c>"Rsm"</c>),
        /// <c>"BoTorch"</c>, <c>"Pymoo"</c>, or <c>"ExternalPython"</c>.
        /// Resolved via <see cref="OptimizationAlgorithmFactory"/>; an unknown name logs a warning and falls back to default.
        /// </summary>
        public string OptimizationAlgorithm { get; set; } = OptimizationAlgorithmFactory.DefaultAlgorithm;

        /// <summary>
        /// Initial seed for all random sources in the run. <c>0</c> = draw automatically at startup;
        /// the drawn value is recorded in the log and <c>run-info.json</c>.
        /// A non-zero value makes the run exactly reproducible. Optimizers receive <c>new Random(seed)</c>,
        /// the bouncer receives <c>new Random(seed + 1)</c>.
        /// </summary>
        public int RandomSeed { get; set; } = 0;

        // 4. Algorithm IdwBaseline (IDW Interpolation Baseline)
        public int IdwVirtualSimulations { get; set; } = 50000;
        public float IdwPower { get; set; } = 3.0f;
        public float IdwExploitationRatio { get; set; } = 0.7f;
        public float IdwExploitationSigma { get; set; } = 0.1f;

        // 4a. Algorithm Bayes. All optional; defaults work for most projects.
        /// <summary>Bayesian optimizer settings; applies only when <c>"OptimizationAlgorithm": "Bayes"</c>.</summary>
        public BayesConfig Bayes { get; set; } = new();

        // 4b. External Python Optimizer Bridge (BoTorch, pymoo, or custom worker).
        /// <summary>External Python optimizer bridge settings; applies when <c>OptimizationAlgorithm</c> is <c>"BoTorch"</c>, <c>"Pymoo"</c>, or <c>"ExternalPython"</c>.</summary>
        public ExternalOptimizerConfig ExternalOptimizer { get; set; } = new();

        // 5. Champion Validation (Bouncer)
        /// <summary>Allowed relative fitness deviation during re-simulation (0.20 = 20%).</summary>
        public float BouncerTolerance { get; set; } = 0.20f;
        /// <summary>Relative perturbation applied to a randomly chosen parameter for re-simulation.</summary>
        public float BouncerJitter { get; set; } = 0.01f;

        // 6. Mesh Calibration & Champion Verification
        /// <summary>Automated mesh calibration before the run or via CLI command.</summary>
        public MeshCalibrationConfig MeshCalibration { get; set; } = new();

        /// <summary>Fine-mesh verification of top candidates.</summary>
        public ChampionVerificationConfig ChampionVerification { get; set; } = new();

        /// <summary>Two-stage multi-fidelity screening settings (coarse screening before high-fidelity promotion).</summary>
        public MultiFidelityConfig MultiFidelity { get; set; } = new();

        public static SimulationConfig CreateDefault() => new SimulationConfig();

        /// <summary>
        /// Returns a copy of this configuration with the strict profile for control runs
        /// (<see cref="ControlRuns"/>): more iterations, longer window, tighter drift limit.
        /// Never uses fewer iterations than a normal run.
        /// </summary>
        public SimulationConfig WithControlRunProfile()
        {
            var clone = (SimulationConfig)MemberwiseClone();
            clone.Su2MaxIterations = System.Math.Max(Su2MaxIterations, ControlRuns.Su2MaxIterations);
            clone.CauchyElements = System.Math.Max(CauchyElements, ControlRuns.CauchyElements);
            clone.CauchyTolerance = ControlRuns.CauchyTolerance;
            clone.IterativeToleranceFraction = ControlRuns.IterativeToleranceFraction;
            return clone;
        }

        /// <summary>
        /// Creates a shallow copy for an SU2 continuation block with adjusted remaining budget
        /// and tightened Cauchy tolerance.
        /// </summary>
        internal SimulationConfig WithSu2BlockOverrides(int su2MaxIterations, string cauchyTolerance)
        {
            var clone = (SimulationConfig)MemberwiseClone();
            clone.Su2MaxIterations = su2MaxIterations;
            clone.CauchyTolerance = cauchyTolerance;
            return clone;
        }

        /// <summary>
        /// Returns a shallow copy with <see cref="Su2MaxIterations"/> capped to <paramref name="maxIterations"/>
        /// when positive and lower than the configured budget.
        /// </summary>
        internal SimulationConfig WithMaxIterationsCap(int maxIterations)
        {
            if (maxIterations <= 0 || maxIterations >= Su2MaxIterations) return this;
            var clone = (SimulationConfig)MemberwiseClone();
            clone.Su2MaxIterations = maxIterations;
            return clone;
        }
    }

    /// <summary>
    /// Automatic early stopping of optimization (see <see cref="AutoStop"/>). After each iteration,
    /// if the best fitness within the last <see cref="WindowVariants"/> variants improved by less than
    /// <see cref="MinImprovement"/> (relative), the run stops.
    /// </summary>
    public class AutoStopConfig
    {
        /// <summary>"On" or "Off" (default: Off = run duration controlled solely by MaxIterations × VariantsPerIteration).</summary>
        public string Mode { get; set; } = "Off";

        /// <summary>Minimum relative improvement of best fitness over the window (0.01 = 1%).</summary>
        public float MinImprovement { get; set; } = 0.01f;

        /// <summary>Number of trailing variants over which improvement is measured.</summary>
        public int WindowVariants { get; set; } = 20;
    }

    /// <summary>
    /// Bayesian optimizer settings. All optional.
    /// </summary>
    public class BayesConfig
    {
        /// <summary>"Auto" (default), "Fitness", or "Metrics". Auto uses Metrics if the fitness calculator declares <c>RequiredMetrics</c>.</summary>
        public string SurrogateTarget { get; set; } = "Auto";

        /// <summary>Number of initial DoE samples. 0 = automatic: max(VariantsPerIteration, 2 × free parameters + 2).</summary>
        public int InitialSamples { get; set; } = 0;

    }

    /// <summary>
    /// Settings for the external Python optimizer bridge (<c>BoTorch</c>, <c>Pymoo</c>, <c>ExternalPython</c>).
    /// </summary>
    public class ExternalOptimizerConfig
    {
        /// <summary>Path or command for the Python interpreter. Empty = auto-detect (<c>~/miniconda/envs/opt-py/bin/python</c> on Linux, then <c>python3</c>/<c>python</c>).</summary>
        public string PythonExecutable { get; set; } = "";

        /// <summary>Path to the worker script. Empty = auto-detect (<c>scripts/optimizers/botorch_worker.py</c> or <c>pymoo_worker.py</c>).</summary>
        public string WorkerScript { get; set; } = "";

        /// <summary>Acquisition / operator preset (<c>"Auto"</c>, <c>"qLogEI"</c>, <c>"qNEHVI"</c>, <c>"qMFKG"</c>, <c>"NSGA2"</c>, <c>"NSGA3"</c>).</summary>
        public string Acquisition { get; set; } = "Auto";

        /// <summary>Maximum time in seconds to wait for a single worker Ask call.</summary>
        public int TimeoutSeconds { get; set; } = 120;

        /// <summary>If true, transparently falls back to built-in C# <c>Bayes</c> when Python/<c>opt-py</c> is unavailable or fails.</summary>
        public bool FallbackToBuiltInBayes { get; set; } = true;

        /// <summary>If true, preserves request/response JSON exchange files under <c>analysis/external_opt/</c>.</summary>
        public bool KeepExchangeFiles { get; set; } = true;
    }

    /// <summary>
    /// Internal tuning knobs for the Bayesian optimizer. Intentionally kept out of <c>simulation.json</c>
    /// to keep user configuration minimal; tests can override them via the constructor.
    /// </summary>
    public sealed class BayesTuning
    {
        /// <summary>Number of candidate points evaluated on the surrogate per proposal.</summary>
        public int CandidatePool { get; init; } = 5000;

        /// <summary>"Auto" (enabled for >= 10 free parameters), "On", or "Off": search within a trust region around the best point.</summary>
        public string TrustRegion { get; init; } = "Auto";

        /// <summary>Maximum training set size to bound GP fitting cost (scales as O(n³)).</summary>
        public int MaxTrainingPoints { get; init; } = 400;
    }

    /// <summary>
    /// Strict profile for the few high-accuracy control runs.
    /// </summary>
    public class ControlRunConfig
    {
        /// <summary>Maximum SU2 iterations per control run.</summary>
        public int Su2MaxIterations { get; set; } = 20000;

        /// <summary>Convergence window length (rows in history.csv).</summary>
        public int CauchyElements { get; set; } = 500;

        /// <summary>
        /// SU2 Cauchy threshold (<c>CONV_CAUCHY_EPS</c>). Defaults to <c>"1e-5"</c>; tighter values
        /// like <c>1e-6</c> are often unreachable in 3D with Venkatakrishnan limiter, causing SU2 to run
        /// all 20,000 iterations. Post-run drift checking determines convergence and triggers automatic restarts if needed.
        /// </summary>
        public string CauchyTolerance { get; set; } = "1e-5";

        /// <summary>
        /// Allowed iterative error as a fraction of the mesh tolerance (0.1 = one tenth, following
        /// Celik et al. 2008: iterative error should be at least an order of magnitude below discretization error).
        /// </summary>
        public float IterativeToleranceFraction { get; set; } = 0.1f;
    }

    /// <summary>
    /// Settings for automated mesh calibration.
    /// </summary>
    public class MeshCalibrationConfig
    {
        /// <summary>"Auto" (default: calibrate mesh factor at run start) or "Manual" (use MeshRefinementFactor).</summary>
        public string Mode { get; set; } = "Auto";

        /// <summary>Allowed relative deviation of all monitored metrics compared to the finest reference level (0.05 = 5%).</summary>
        public float Tolerance { get; set; } = 0.05f;

        /// <summary>Use 2D pre-calibration before 3D confirmation.</summary>
        public bool Use2DPrecalibration { get; set; } = false;

        /// <summary>
        /// Fixed minimum absolute tolerance per metric: allowed deviation is
        /// <c>|Δ| ≤ Tolerance · |Reference| + AbsoluteTolerance</c>, handling near-zero metrics
        /// (such as lift of a symmetric airfoil at 0°). Empty = use defaults from
        /// <see cref="MetricTolerance.DefaultAbsolute"/>. Entries here override per metric name.
        /// </summary>
        public System.Collections.Generic.Dictionary<string, float> AbsoluteTolerances { get; set; } = new();

        /// <summary>Upper limit on extrapolated cell count for the finest level in 2D.</summary>
        public long MaxCells2D { get; set; } = 2_000_000;

        /// <summary>Upper limit on extrapolated cell count for the finest level in 3D.</summary>
        public long MaxCells3D { get; set; } = 3_000_000;
    }

    /// <summary>
    /// Settings for fine-mesh verification of top candidates.
    /// </summary>
    public class ChampionVerificationConfig
    {
        /// <summary>"Off", "Final", "FinalTopN" (default: re-evaluate top N on fine mesh), or "EachIteration".</summary>
        public string Mode { get; set; } = "FinalTopN";

        /// <summary>Number of top variants to verify at the end when Mode = "FinalTopN".</summary>
        public int TopN { get; set; } = 3;

        /// <summary>Number of refinement steps relative to the effective factor (÷ √2 per step).</summary>
        public int RefinementSteps { get; set; } = 1;

        /// <summary>Relative deviation above which a mesh-dependency warning is emitted (0.05 = 5%).</summary>
        public float Tolerance { get; set; } = 0.05f;
    }

    /// <summary>
    /// Settings for two-stage multi-fidelity screening (<c>TwoStageFilter</c>):
    /// evaluates all candidates on a coarse mesh/iteration budget first, then promotes the top
    /// candidates to full fidelity.
    /// </summary>
    public class MultiFidelityConfig
    {
        /// <summary>"Off" (default) or "TwoStageFilter".</summary>
        public string Mode { get; set; } = "Off";

        /// <summary>Multiplier (&gt; 1.0) applied to <c>EffectiveMeshRefinementFactor</c> in Stage 1 (larger values coarsen the mesh).</summary>
        public float CoarseMeshFactor { get; set; } = 2.0f;

        /// <summary>Maximum solver iterations for Stage 1 coarse evaluations (0 = uncapped).</summary>
        public int CoarseMaxIterations { get; set; } = 500;

        /// <summary>Minimum number of top coarse candidates to promote to full fidelity per iteration.</summary>
        public int PromoteTopK { get; set; } = 2;

        /// <summary>Fraction of eligible coarse candidates to promote per iteration (combined via max with <see cref="PromoteTopK"/>).</summary>
        public float PromoteTopFraction { get; set; } = 0.25f;
    }
}
