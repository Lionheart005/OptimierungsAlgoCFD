# Configuration Key Reference

> **See also:**
> - [Physics Presets & Layer Precedence](physics-presets.md)
> - [Creating a New Project](creating-a-project.md)
> - JSON Schemas in [`schemas/`](../schemas/)

Every JSON configuration key is validated at startup by `ConfigSchema`. Any typo aborts immediately with Levenshtein-distance suggestions. Top-level `"$schema"` is allowed in all files for VS Code validation and autocomplete.

---

## 1. `simulation.json` (`SimulationConfig`)

### Preset & Tool Paths
| Key | Type | Default | Description |
|---|---|---|---|
| `Preset` | `string` | `""` | Curated Layer 1.5 preset (e.g. `"External3D:StandardRans"`, `"External2D:StandardRans"`, or `"Custom"` / `""` for none). |
| `MpiRunPath` | `string` | `"mpirun"` | MPI launcher path or executable name (on server, bare `mpirun`/`mpiexec` auto-prefers MPICH). |
| `Su2Path` | `string` | `"SU2_CFD"` | SU2 solver executable path or name. |
| `GmshPath` | `string` | `"gmsh"` | Gmsh mesher executable path or name. |

### Budget & Early Stopping
| Key | Type | Default | Description |
|---|---|---|---|
| `MaxIterations` | `int` | `10` | Maximum optimization rounds (`MaxIterations * VariantsPerIteration` = max evaluations). |
| `VariantsPerIteration` | `int` | `10` | Candidate geometries evaluated per optimization round. |
| `AutoStop` | `AutoStopConfig` | `{}` | Automatic early stopping settings (see section 1.1). |

#### 1.1 `AutoStop` (`AutoStopConfig`)
| Key | Type | Default | Description |
|---|---|---|---|
| `Mode` | `string` | `"Off"` | `"On"` or `"Off"`. |
| `MinImprovement` | `float` | `0.01` | Minimum relative improvement of best fitness over the trailing window (`0.01` = 1%). |
| `WindowVariants` | `int` | `20` | Number of trailing variants over which improvement is evaluated. |

### Geometry & Voxel Kernel (3D PicoGK)
| Key | Type | Default | Description |
|---|---|---|---|
| `BaseVoxelResolution` | `float` | `0.5` | PicoGK voxel size in scaled working units (mm). |
| `AutoVoxelResolution` | `bool` | `false` | Auto-refines effective voxel size at startup (down to 0.1 mm) if real-space voxels exceed 80% of target surface cell size. |
| `TargetPicoGkSize` | `float` | `50.0` | Target working size (mm) when `UseRubberBandScaler` is enabled. |
| `VoxelSmoothingIterations` | `int` | `40` | Number of Taubin/Laplace STL smoothing iterations. |
| `VoxelSmoothingPremeltingSteps` | `int` | `2` | Premelting steps before STL smoothing. |
| `UseRubberBandScaler` | `bool` | `false` | Opt-in scaler that normalizes `DimensionalParameters` to `TargetPicoGkSize` and scales reported geometry metrics back. When `false` (default), PicoGK operates directly in real millimeters with fixed reference dimensions and relative/dimensionless parameters. |
| `UseStlSmoothing` | `bool` | `false` | Opt-in STL smoother for coarse organic voxel hulls (3D only; never use on planar `Internal3D` multi-patch flanges). |
| `UseProjectedFrontalArea` | `bool` | `true` | Computes `FrontalArea` as the true projected silhouette in YZ instead of `Size.Y * Size.Z`. |

### CFD, Convergence & Parallelism
| Key | Type | Default | Description |
|---|---|---|---|
| `MachNumber` | `float` | `0.1` | Legacy Mach number used only when `FreestreamVelocity = 0` in `solvers/su2.json`. |
| `MeshRefinementFactor` | `float` | `1.0` | Global mesh size multiplier (`< 1` finer, `> 1` coarser). Forbidden in project JSON when `MeshCalibration.Mode = "Auto"`. |
| `Su2MaxIterations` | `int` | `3000` | Maximum SU2 iterations per normal variant. |
| `CauchyElements` | `int` | `300` | Trailing history window length for SU2 Cauchy convergence and drift analysis. |
| `CauchyTolerance` | `string` | `"1e-5"` | SU2 `CONV_CAUCHY_EPS` threshold. |
| `IterativeToleranceFraction` | `float` | `0.25` | Max allowed tail-window drift as a fraction of `MeshCalibration.Tolerance`. |
| `ControlRuns` | `ControlRunConfig` | `{}` | Strict solver profile for calibration, verification, and recheck (see section 1.2). |
| `MpiCores` | `int` | `12` | MPI ranks passed to `MpiRunPath` for `SU2_CFD`. |
| `GmshCores` | `int` | `12` | OpenMP threads passed to `gmsh`. |

#### 1.2 `ControlRuns` (`ControlRunConfig`)
| Key | Type | Default | Description |
|---|---|---|---|
| `Su2MaxIterations` | `int` | `20000` | Maximum SU2 iterations for high-accuracy control runs. |
| `CauchyElements` | `int` | `500` | Convergence window length for control runs. |
| `CauchyTolerance` | `string` | `"1e-5"` | SU2 `CONV_CAUCHY_EPS` threshold for control runs. |
| `IterativeToleranceFraction` | `float` | `0.1` | Allowed iterative drift as a fraction of mesh tolerance (Celik et al. 2008). |

### Optimization Algorithms & Validation
| Key | Type | Default | Description |
|---|---|---|---|
| `OptimizationAlgorithm` | `string` | `"Bayes"` | Optimizer selection: `"Bayes"` (default), `"IdwBaseline"` (legacy alias `"Rsm"`), `"BoTorch"`, `"Pymoo"`, or `"ExternalPython"`. |
| `RandomSeed` | `int` | `0` | `0` draws a new seed at startup (recorded in `run-info.json`); non-zero makes proposals reproducible. |
| `IdwVirtualSimulations` | `int` | `50000` | Number of virtual candidate samples evaluated by `IdwBaseline`. |
| `IdwPower` | `float` | `3.0` | Inverse-distance weighting exponent in `IdwBaseline`. |
| `IdwExploitationRatio` | `float` | `0.7` | Fraction of local exploitation candidates in `IdwBaseline`. |
| `IdwExploitationSigma` | `float` | `0.1` | Local perturbation scale relative to `MaxDeviations` in `IdwBaseline`. |
| `Bayes` | `BayesConfig` | `{}` | Bayesian optimization settings (see section 1.3). |
| `ExternalOptimizer` | `ExternalOptimizerConfig` | `{}` | External Python optimizer bridge settings for `BoTorch`, `Pymoo`, and `ExternalPython` (see section 1.3a). |
| `MultiFidelity` | `MultiFidelityConfig` | `{}` | Two-stage multi-fidelity screening settings (see section 1.3b). |
| `BouncerTolerance` | `float` | `0.20` | Max allowed relative fitness deviation during champion re-simulation. |
| `BouncerJitter` | `float` | `0.01` | Relative parameter perturbation applied during champion validation. |

#### 1.3 `Bayes` (`BayesConfig`)
| Key | Type | Default | Description |
|---|---|---|---|
| `SurrogateTarget` | `string` | `"Auto"` | `"Auto"`, `"Fitness"`, or `"Metrics"`. `"Auto"` uses `"Metrics"` when `RequiredMetrics` is non-empty. |
| `InitialSamples` | `int` | `0` | Initial DoE sample count (`0` = automatic `max(VariantsPerIteration, 2 * d + 2)`). |

#### 1.3a `ExternalOptimizer` (`ExternalOptimizerConfig`)
| Key | Type | Default | Description |
|---|---|---|---|
| `PythonExecutable` | `string` | `""` | Python executable path (`""` auto-detects `~/miniconda/envs/opt-py/bin/python` on Linux or `python3`/`python`). |
| `WorkerScript` | `string` | `""` | Worker script path (`""` auto-selects `scripts/optimizers/botorch_worker.py` or `pymoo_worker.py`). |
| `Acquisition` | `string` | `"Auto"` | Acquisition or operator preset (`"Auto"`, `"qLogEI"`, `"qNEHVI"`, `"qMFKG"`, `"NSGA2"`, `"NSGA3"`). |
| `TimeoutSeconds` | `int` | `120` | Subprocess timeout in seconds per Ask call. |
| `FallbackToBuiltInBayes` | `bool` | `true` | Falls back to built-in C# `Bayes` with a warning if Python/`opt-py` is unavailable or fails. |
| `KeepExchangeFiles` | `bool` | `true` | Retains JSON request/response files under `analysis/external_opt/`. |

#### 1.3b `MultiFidelity` (`MultiFidelityConfig`)
| Key | Type | Default | Description |
|---|---|---|---|
| `Mode` | `string` | `"Off"` | `"Off"` (standard single-fidelity) or `"TwoStageFilter"` (coarse Stage 1 screening followed by full-fidelity promotion of top candidates). |
| `CoarseMeshFactor` | `float` | `1.5` | Multiplier (`> 1.0`) applied to `EffectiveMeshRefinementFactor` in Stage 1 to coarsen the mesh. |
| `CoarseMaxIterations` | `int` | `0` | Maximum solver iterations for Stage 1 coarse evaluations (`0` = uncapped). |
| `PromoteTopK` | `int` | `2` | Minimum number of top coarse candidates promoted to full fidelity per iteration. |
| `PromoteTopFraction` | `float` | `0.25` | Fraction of eligible coarse candidates promoted per iteration (combined via `max` with `PromoteTopK`). |

> **Multi-Fidelity Tracking, Safeguards & Caution:** Every evaluation records its `FidelityLevel` (`"Coarse"`, `"High"`, or `"Verification"`) in `results.csv`, `RunJournal` (`resume/journal.jsonl`), and the external optimizer Ask/Tell JSON payloads. Stage-2 promoted runs use the `"Stage2"` tag so they run under the regular `SimulationConfig` iteration budget rather than `ControlRuns`. At startup, `MultiFidelitySelector.GetStartupWarnings` warns if SU2 is paired with a tight `CoarseMaxIterations` (`<= 2 * CauchyElements` or `< 1000`) or if built-in C# `Bayes` is used (which trains its objective GP only on Stage-2 high-fidelity records while still tracking Stage-1 `PreConstraint` and failed locations; set `OptimizationAlgorithm = "BoTorch"` for multi-fidelity surrogate modeling). After each iteration, `[MULTI-FIDELITY] Stage-1 vs Stage-2 delta:` logs per-candidate deltas and warns on rank reversals, `> 25%` relative delta, or Stage-2 failures. Keep `Mode = "Off"` (default) for standard calibrated single-fidelity runs, 2D problems, and fine-tuning. See [Physics Presets §6](physics-presets.md#6-geometry-best-practices--multi-fidelity-caveats).

### Mesh Calibration & Fine Verification
| Key | Type | Default | Description |
|---|---|---|---|
| `MeshCalibration` | `MeshCalibrationConfig` | `{}` | Automated mesh independence study settings (see section 1.4). |
| `ChampionVerification` | `ChampionVerificationConfig` | `{}` | Post-run fine-mesh verification settings (see section 1.5). |

#### 1.4 `MeshCalibration` (`MeshCalibrationConfig`)
| Key | Type | Default | Description |
|---|---|---|---|
| `Mode` | `string` | `"Auto"` | `"Auto"` (calibrate at run start) or `"Manual"` (use `MeshRefinementFactor`). |
| `Tolerance` | `float` | `0.05` | Max relative deviation vs. finest reference mesh (`0.05` = 5%). |
| `Use2DPrecalibration` | `bool` | `false` | Enables fast 2D precalibration for 3D projects implementing `IHasRepresentativeSection`. |
| `AbsoluteTolerances` | `Dictionary<string, float>` | `{}` | Per-metric minimum absolute tolerance overrides (`|Δ| <= Tolerance * |Ref| + Absolute`). |
| `MaxCells2D` | `long` | `2000000` | Maximum measured/extrapolated cell count allowed in 2D calibration. |
| `MaxCells3D` | `long` | `3000000` | Maximum measured/extrapolated cell count allowed in 3D calibration. |

#### 1.5 `ChampionVerification` (`ChampionVerificationConfig`)
| Key | Type | Default | Description |
|---|---|---|---|
| `Mode` | `string` | `"FinalTopN"` | `"Off"`, `"Final"`, `"FinalTopN"`, or `"EachIteration"`. |
| `TopN` | `int` | `3` | Number of top candidates verified when `Mode = "FinalTopN"`. |
| `RefinementSteps` | `int` | `1` | Number of $\div \sqrt{2}$ mesh refinement steps relative to the effective factor. |
| `Tolerance` | `float` | `0.05` | Relative deviation threshold above which a mesh-dependency warning is logged. |

---

## 2. `project.json` (`ProjectConfigDto`)

| Key | Type | Default | Description |
|---|---|---|---|
| `ProjectName` | `string` | `""` | Do **not** set in `project.json`; the folder name is the sole source of the project name. |
| `BaseParameters` | `Dictionary<string, float>` | `{}` | Initial active design variables (contract with `*GeometryGenerator.cs`). |
| `MaxDeviations` | `Dictionary<string, float>` | `{}` | Initial step sizes for `IdwBaseline`. If omitted, auto-derived as `max((Max - Min) * 0.1, 1e-4)` from `ParameterBounds`. |
| `ParameterBounds` | `Dictionary<string, {Min, Max}>` | `{}` | Hard parameter bounds (`Min`, `Max`). Setting `Min == Max` fixes a parameter. |
| `OptimizationTargets` | `Dictionary<string, float>` | `{}` | Named constants read by `*FitnessCalculator.cs`. |
| `DimensionalParameters` | `string[]` | `[]` | Parameter names carrying physical length units (mm) scaled by `RubberBandScaler`. |

---

## 3. `solvers/su2.json` (`Su2SolverOptions`)

### Fluid, Freestream & Reference Values
| Key | Type | Default | Description |
|---|---|---|---|
| `Density` | `float` | `1025.0` | Fluid density in $\text{kg/m}^3$ (seawater `1025.0`, ISA air `1.225`). |
| `DynamicViscosity` | `float` | `0.001001` | Dynamic viscosity in $\text{Pa}\cdot\text{s}$ (seawater `0.001001`, air `1.81e-5`). |
| `TurbulenceModel` | `string` | `"SA"` | RANS turbulence model: `"SA"` (Spalart-Allmaras) or `"SST"` (Menter $k$-$\omega$ SST). Unknown values abort with `[CONFIG]`. |
| `FreestreamNuFactor` | `float?` | `null` | Optional Spalart-Allmaras freestream viscosity ratio $\tilde{\nu}_\infty / \nu_\infty$ (`FREESTREAM_NU_FACTOR`). `null` omits the key (SU2 default applies); ignored when `TurbulenceModel != "SA"`. |
| `SpeedOfSound` | `float` | `343.2` | Speed of sound in m/s (only used when `FreestreamVelocity = 0`). |
| `FreestreamVelocity` | `float` | `2.0` | Inflow velocity in m/s (`0` falls back to `MachNumber * SpeedOfSound`). |
| `Dimensions` | `int` | `3` | Spatial dimension (`2` or `3`; `Cfd2DProjectDefinition` forces `2`). |
| `AngleOfAttackDeg` | `float` | `0.0` | Angle of attack $\alpha$ in degrees (overridden automatically if `"AngleOfAttackDeg"` is an active parameter or `OperatingPoints` is set). |
| `SymmetryPlane` | `string` | `"None"` | 3D half-model symmetry plane: `"None"`, `"Y0"` ($Y \ge 0$), or `"Z0"` ($Z \ge 0$). Emits `MARKER_SYM= ( Symmetry )` and halves `REF_AREA` when clipped from a full body. |
| `OperatingPoints` | `OperatingPointOptions[]` | `[]` | Optional multi-point evaluation list evaluated on the same mesh (see section 3.2). |
| `ReferenceLength` | `float` | `0.01` | SU2 `REF_LENGTH` in meters. |
| `ReferenceAreaMetric` | `string` | `"FrontalArea"` | Geometry metric name (in $\text{mm}^2$ for 3D, or chord mm for 2D) used to compute SU2 `REF_AREA`. |

> **Low-$Re$ Regime Warning:** When $0 < Re_L < 5 \times 10^5$, `Su2RegimeAdvisor` emits an explicit `[WARNING]` noting that fully turbulent RANS (`SA`/`SST`) ignores laminar separation bubbles and absolute $C_D$ may deviate from free-transition wind-tunnel measurements.

### Result Extraction, Convergence & CFL
| Key | Type | Default | Description |
|---|---|---|---|
| `ResultMetrics` | `Dictionary<string, string>` | `{"Drag":"CD"}` | Maps `PassiveParameters` names to SU2 `history.csv` column names (`CD`, `CL`, `CSF`, `CMx`, `CMy`, `CMz`). Merges additively. |
| `ConvergenceField` | `string` | `"DRAG"` | SU2 `CONV_FIELD` quantity monitored for Cauchy termination (`"DRAG"`, `"LIFT"`, etc.). |
| `HistoryOutput` | `string[]` | `["ITER","RMS_RES","AERO_COEFF"]` | SU2 `HISTORY_OUTPUT` column groups. |
| `OscillationWarnLimit` | `float` | `0.05` | Relative oscillation `(max - min) / |mean|` above which an unsteady flow warning is logged. |
| `TreatUnconvergedAsFailed` | `bool` | `true` | Marks runs with monotonic tail-window drift as `SimulationFailed = true` (pure stationary oscillation stays valid). |
| `AutoRestartOnDrift` | `bool` | `true` | Continues via `RESTART_SOL= YES` with tighter Cauchy tolerance if SU2 stops early while still drifting. |
| `MaxConvergenceRestarts` | `int` | `3` | Maximum automatic steady restart blocks per variant (`0` disables). |
| `ResultAveraging` | `string` | `"TailMean"` | Metric aggregation over `history.csv`: `"TailMean"`, `"Last"`, or `"CauchyWindowMean"`. |
| `SlopeLimiterFlow` | `string` | `"VENKATAKRISHNAN"` | Convective flow slope limiter (`SLOPE_LIMITER_FLOW`): `"VENKATAKRISHNAN"`, `"NONE"`, `"VENKATAKRISHNAN_WANG"`, `"BARTH_JESPERSEN"`, or `"VAN_ALBADA_EDGE"`. `VENKAT_LIMITER_COEFF= 0.05` is emitted only for Venkatakrishnan limiters. |
| `CflNumber` | `float` | `5.0` | Initial SU2 CFL number. |
| `CflAdapt` | `bool` | `true` | Enables adaptive CFL control (`CFL_ADAPT= YES`). |
| `CflAdaptFactorDown` | `float` | `0.5` | CFL reduction factor. |
| `CflAdaptFactorUp` | `float` | `1.2` | CFL growth factor. |
| `CflAdaptMin` | `float` | `1.0` | Minimum CFL bound. |
| `CflAdaptMax` | `float` | `50.0` | Maximum CFL bound. |
| `TargetYPlus` | `float` | `1.5` | Target $y^+$ (keep synchronized with `solvers/gmsh.json`). |
| `YPlusWarnFactor` | `float` | `3.0` | Hard calibration limit & warning multiplier: `YPlus_P95 <= TargetYPlus * YPlusWarnFactor`. |
| `TimeDomain` | `Su2TimeDomainOptions` | `{}` | Time-accurate URANS settings (see section 3.1). |

#### 3.1 `TimeDomain` (`Su2TimeDomainOptions`)
| Key | Type | Default | Description |
|---|---|---|---|
| `Enabled` | `bool` | `false` | Enables dual time-stepping URANS simulation. |
| `TimeStep` | `float` | `0.0` | Physical time step in seconds (`0` = automatic from `StrouhalEstimate` and `StepsPerPeriod`). |
| `StepsPerPeriod` | `int` | `50` | Time steps per vortex-shedding period when `TimeStep = 0`. |
| `StrouhalEstimate` | `float` | `0.2` | Estimated Strouhal number $St = f L / U$ for automatic time-step sizing. |
| `TimeIterations` | `int` | `1000` | Number of physical time steps to execute. |
| `InnerIterations` | `int` | `30` | Pseudo-time iterations per physical time step (`INNER_ITER`). |
| `AveragingStartFraction` | `float` | `0.5` | Fraction of `TimeIterations` after which `WINDOW_START_ITER` averaging begins. |
| `RestartIteration` | `int` | `0` | Restart time level (`0` = freestream, `2` = restart from `restart_00000.dat`/`restart_00001.dat`). |

#### 3.2 `OperatingPoints` (`OperatingPointOptions[]`)
| Key | Type | Default | Description |
|---|---|---|---|
| `Name` | `string` | `""` | Optional display label for the operating point (e.g. `"CruiseNom"`). |
| `Velocity` | `float?` | `null` | Freestream velocity in m/s (`null` inherits `FreestreamVelocity`; boundary-layer $y^+$ uses max across all points). |
| `AngleOfAttackDeg` | `float` | `0.0` | Angle of attack $\alpha$ in degrees for this operating point. |
| `Weight` | `float` | `1.0` | Positive aggregation weight (must be $> 0$; normalized across all operating points). |

---

## 4. `solvers/gmsh.json` — 3D Mesher (`GmshMesherOptions`)

| Key | Type | Default | Description |
|---|---|---|---|
| `TunnelShape` | `string` | `"Box"` | Domain shape: `"Box"` or `"Cylinder"`. |
| `TunnelSizeX` | `float` | `1000.0` | Box tunnel length along flow X (mm). |
| `TunnelSizeY` | `float` | `300.0` | Box tunnel width in Y (mm). |
| `TunnelSizeZ` | `float` | `300.0` | Box tunnel height in Z (mm). |
| `TunnelCenterX` | `float` | `200.0` | Tunnel center X (mm; positive shifts wake space downstream). |
| `TunnelCenterY` | `float` | `0.0` | Tunnel center Y (mm). |
| `TunnelCenterZ` | `float` | `0.0` | Tunnel center Z (mm). |
| `TunnelDiameter` | `float` | `300.0` | Cylinder tunnel diameter (mm, when `TunnelShape = "Cylinder"`). |
| `TunnelLength` | `float` | `1000.0` | Cylinder tunnel length along X (mm). |
| `TunnelSegments` | `int` | `64` | Circumferential segments for cylinder tunnel. |
| `TunnelMinUpstreamFactor` | `float` | `5.0` | Minimum upstream clearance as multiple of model length $L$. |
| `TunnelMinDownstreamFactor` | `float` | `15.0` | Minimum downstream wake clearance as multiple of $L$. |
| `TunnelMinLateralFactor` | `float` | `5.0` | Minimum lateral clearance as multiple of width/height. |
| `TunnelMaxBlockagePercent` | `float` | `0.3` | Maximum blockage ratio $A_{\text{frontal}} / A_{\text{tunnel}}$ (%) before auto-expansion (`<= 0` disables). |
| `BoundaryLayerSizeMin` | `float` | `1.2` | Minimum cell size at wall (mm) in distance field. |
| `BoundaryLayerSizeMax` | `float` | `120.0` | Maximum farfield cell size (mm). |
| `BoundaryLayerDistMin` | `float` | `3.0` | Distance from wall up to which `BoundaryLayerSizeMin` applies (mm). |
| `BoundaryLayerDistMax` | `float` | `40.0` | Distance beyond which `BoundaryLayerSizeMax` applies (mm). |
| `WakeRefinement` | `bool` | `true` | Enables downstream wake refinement box. |
| `WakeLengthFactor` | `float` | `2.0` | Wake box length behind model as multiple of `lenX`. |
| `WakeWidthFactor` | `float` | `0.5` | Wake box half-width padding as multiple of `lenY` / `lenZ`. |
| `WakeCellSize` | `float` | `0.0` | Cell size inside wake box (mm at factor 1.0; `0` = automatic `lenX / 6`). |
| `DetailBox` | `bool` | `true` | Enables fine near-wake detail box around and behind the model. |
| `DetailBoxLengthFactor` | `float` | `1.5` | Detail box length behind model as multiple of `lenX`. |
| `DetailBoxWidthFactor` | `float` | `0.25` | Detail box Y/Z padding as multiple of `lenY` / `lenZ`. |
| `DetailBoxMinHeightLengthFactor` | `float` | `0.15` | Minimum Z-padding as fraction of `lenX` (`<= 0` disables). |
| `DetailBoxCellSize` | `float` | `0.0` | Detail box cell size (mm at factor 1.0; `0` = `2.5 * surfaceSize`). |
| `MeshSizeFromCurvature` | `bool` | `true` | Enables curvature-based surface refinement in Gmsh. |
| `CurvatureElements` | `int` | `20` | Elements per $2\pi$ radians when `MeshSizeFromCurvature = true`. |
| `CurvatureMinSizeFactor` | `float` | `0.5` | Lower bound on curvature refinement as factor on `surfaceSize` (clamped to `[0.1, 1.0]`). |
| `BoundaryLayerMode` | `string` | `"Prism"` | `"Prism"` (structured prisms, $y^+ \approx 1$), `"Threshold"` (coarse unstructured), or `"Hexa"`. |
| `TargetYPlus` | `float` | `1.5` | Target $y^+$ for first wall-normal cell height $y_1$. |
| `BoundaryLayerMinLayers` | `int` | `4` | Minimum number of extruded boundary layers. |
| `BoundaryLayerGrowthRate` | `float` | `1.28` | Layer-to-layer growth ratio in the boundary layer. |
| `BoundaryLayerLayers` | `int` | `0` | Fixed layer count (`0` = computed automatically from boundary-layer thickness $\delta$). |
| `BoundaryLayerCoverage` | `float` | `1.0` | Multiplier on $\delta$ covered by structured layers when `BoundaryLayerLayers = 0`. |
| `SurfaceCellSize` | `float` | `0.0` | Wall surface cell size in mm at factor 1.0 (`0` uses `BoundaryLayerSizeMin`). |
| `ClassifyAngleDeg` | `float` | `40.0` | Angle threshold (degrees) for `ClassifySurfaces` when remeshing STL surfaces. |
| `SymmetryPlane` | `string` | `"None"` | Half-model symmetry plane: `"None"`, `"Y0"` ($Y \ge 0$), or `"Z0"` ($Z \ge 0$). Clips the STL, aligns prism extrusion onto the plane, emits `Symmetry` marker, and synchronizes with `solvers/su2.json`. |

---

## 5. `solvers/gmsh.json` — 2D Mesher (`Gmsh2DMesherOptions`)

| Key | Type | Default | Description |
|---|---|---|---|
| `FarfieldRadiusFactor` | `float` | `500.0` | Circular farfield radius as a multiple of chord $c$ (500 matches NASA TMR grids). |
| `FarfieldCircleSegments` | `int` | `64` | Number of segments along the circular farfield boundary. |
| `TargetYPlus` | `float` | `1.0` | Target $y^+$ for first wall-normal cell height $y_1$. |
| `BoundaryLayerGrowthRate` | `float` | `1.2` | Layer-to-layer growth ratio in the 2D quad boundary layer. |
| `BoundaryLayerMinLayers` | `int` | `4` | Minimum number of boundary layers. |
| `BoundaryLayerLayers` | `int` | `0` | Fixed layer count (`0` = automatic from $\delta$). |
| `SurfaceCellSizeFactor` | `float` | `0.01` | Contour cell size as a fraction of chord $L$ at factor 1.0. |
| `SurfaceCellSizeMm` | `float` | `0.0` | Absolute contour cell size in mm at factor 1.0 (`> 0` overrides `SurfaceCellSizeFactor * L`). |
| `NearfieldCellSizeFactor` | `float` | `0.04` | Nearfield cell size as a fraction of $L$ at factor 1.0. |
| `FarfieldCellSizeFactor` | `float` | `25.0` | Farfield cell size as a multiple of $L$ at factor 1.0. |
| `NearfieldGrowthDistanceFactor` | `float` | `0.0` | Transition distance ($\times L$) from nearfield to farfield (`0` = `0.4 * FarfieldRadiusFactor`). |
| `WakeRefinement` | `bool` | `true` | Enables downstream wake refinement box. |
| `WakeLengthFactor` | `float` | `3.0` | Wake box length behind body as multiple of $L$. |
| `WakeWidthFactor` | `float` | `0.6` | Wake box half-width in Y as multiple of $L$. |
| `WakeCellSizeFactor` | `float` | `0.04` | Wake box cell size as fraction of $L$ at factor 1.0. |
| `DetailBox` | `bool` | `true` | Enables fine near-wake detail box. |
| `DetailBoxLengthFactor` | `float` | `1.0` | Detail box length behind trailing edge as multiple of $L$. |
| `DetailBoxWidthFactor` | `float` | `0.25` | Detail box Y-padding as multiple of $L$. |
| `DetailBoxCellSizeFactor` | `float` | `0.01` | Detail box cell size as fraction of $L$ at factor 1.0. |
| `EdgeRefinementFactor` | `float` | `0.25` | Factor on wall cell size at detected leading/trailing-edge corners (`1.0` = off). |
| `EdgeRefinementDistanceFactor` | `float` | `0.1` | Fade-out distance around detected corners as multiple of $L$. |

---

## 6. `solvers/openfoam.json` (`OpenFoamOptions`)

### Domain, Backend & Turbulence
| Key | Type | Default | Description |
|---|---|---|---|
| `DomainMode` | `string` | `"Internal3D"` | Flow domain mode: `"Internal3D"` (multi-patch internal volume) or `"External3D"` (virtual wind/water tunnel). |
| `MesherBackend` | `string` | `"SnappyHexMesh"` | Meshing backend: `"SnappyHexMesh"` (default) or `"CfMesh"` (`cartesianMesh`). |
| `FallbackToCfMesh` | `bool` | `false` | Falls back to `cfMesh` if `snappyHexMesh` fails. |
| `Solver` | `string` | `"simpleFoam"` | OpenFOAM solver application (default `"simpleFoam"`). |
| `TurbulenceModel` | `string` | `"kOmegaSST"` | Turbulence model: `"kOmegaSST"`, `"kOmegaSSTLM"`, `"kEpsilon"`, `"SpalartAllmaras"`, or `"laminar"`. |
| `TurbulenceIntensity` | `float` | `0.05` | Inlet turbulence intensity as a fraction (`0.05` = 5%). |
| `HydraulicDiameter` | `float` | `0.0` | Hydraulic diameter in meters (`0` = auto-derived from inlet patch area). |

### Fluid, Boundary Conditions & Reference Quantities
| Key | Type | Default | Description |
|---|---|---|---|
| `Density` | `float` | `998.2` | Fluid density in $\text{kg/m}^3$ (`998.2` = water at 20 °C). |
| `DynamicViscosity` | `float` | `1.002e-3` | Dynamic viscosity in $\text{Pa}\cdot\text{s}$ (`1.002e-3` = water at 20 °C). |
| `InletCondition` | `string` | `"Velocity"` | Inlet boundary condition type: `"Velocity"`, `"VolumetricFlowRate"`, or `"MassFlowRate"`. |
| `InletVelocity` | `float` | `1.0` | Inlet flow speed in m/s when `InletCondition = "Velocity"`. |
| `InletVolumetricFlowRate` | `float` | `0.0` | Inlet volumetric flow rate in $\text{m}^3/\text{s}$ when `InletCondition = "VolumetricFlowRate"`. |
| `InletMassFlowRate` | `float` | `0.0` | Inlet mass flow rate in kg/s when `InletCondition = "MassFlowRate"`. |
| `InletDirection` | `float[]` | `[]` | Explicit inlet velocity direction `[dx, dy, dz]` (`[]` = auto-derived from negated area-weighted inlet STL normal, or `AngleOfAttackDeg` for `External3D`). |
| `OutletPressure` | `float` | `0.0` | Fixed kinematic outlet pressure $p/\rho$ in $\text{m}^2/\text{s}^2$. |
| `AngleOfAttackDeg` | `float` | `0.0` | Angle of attack in degrees for `External3D` runs (rotated in the XZ plane). |
| `SymmetryPlane` | `string` | `"None"` | Half-model symmetry plane for `External3D`: `"None"`, `"Y0"`, or `"Z0"`. |
| `ReferenceLength` | `float` | `0.1` | Reference length in meters for Reynolds number and `forceCoeffs`. |
| `ReferenceArea` | `float` | `0.0` | Explicit reference area in $\text{m}^2$ for `forceCoeffs` (`0` = read from `ReferenceAreaMetric`). |
| `ReferenceAreaMetric` | `string` | `"FrontalArea"` | Geometry metric name (in $\text{mm}^2$) used as fallback $A_{\text{ref}}$ for `External3D`. |

### External3D Domain Clearances & Patch Names
| Key | Type | Default | Description |
|---|---|---|---|
| `TunnelUpstreamFactor` | `float` | `5.0` | Upstream wind-tunnel clearance as a multiple of body length $L$ (`External3D`). |
| `TunnelDownstreamFactor` | `float` | `15.0` | Downstream wind-tunnel clearance as a multiple of body length $L$ (`External3D`). |
| `TunnelLateralFactor` | `float` | `5.0` | Lateral wind-tunnel clearance as a multiple of body width/height (`External3D`). |
| `InletPatchName` | `string` | `"inlet"` | Inlet patch name in multi-solid STL and OpenFOAM dictionaries. |
| `OutletPatchPrefix` | `string` | `"outlet"` | Prefix matching outlet patches (e.g. `outlet`, `outlet1`, `outlet2`). |
| `WallPatchName` | `string` | `"wall"` | Wall patch name in multi-solid STL and OpenFOAM dictionaries. |

### Meshing & Quality Gates
| Key | Type | Default | Description |
|---|---|---|---|
| `BaseCellSizeMm` | `float` | `2.0` | Base background cell size in mm at refinement factor `1.0`. |
| `SurfaceRefinementLevelMin` | `int` | `1` | Minimum surface refinement level in `snappyHexMesh`. |
| `SurfaceRefinementLevelMax` | `int` | `2` | Maximum surface refinement level in `snappyHexMesh`. |
| `FeatureAngleDeg` | `float` | `30.0` | Feature edge angle threshold in degrees for `snappyHexMesh`. |
| `AddLayers` | `bool` | `true` | Enables boundary-layer prism extrusion (`addLayers` / `cfMesh` `boundaryLayers`). |
| `RelativeLayerSizes` | `bool` | `true` | Uses relative layer sizing in `snappyHexMesh` (`true` = `FinalLayerThickness`, `false` = `FirstLayerThickness`). |
| `BoundaryLayerLayers` | `int` | `5` | Number of extruded wall boundary layers. |
| `BoundaryLayerExpansionRatio` | `float` | `1.2` | Layer-to-layer expansion ratio. |
| `FirstLayerThickness` | `float` | `0.05` | First layer thickness (meters when `RelativeLayerSizes = false`). |
| `FinalLayerThickness` | `float` | `0.5` | Final layer thickness ratio (when `RelativeLayerSizes = true`). |
| `MinLayerThickness` | `float` | `0.1` | Minimum allowed layer thickness (relative or meters). |
| `MaxLocalCells` | `int` | `2000000` | Maximum cells per MPI rank during `snappyHexMesh`. |
| `MaxGlobalCells` | `int` | `8000000` | Maximum global cells during `snappyHexMesh`. |
| `MaxNonOrthogonality` | `float` | `65.0` | Hard `checkMesh` quality gate on maximum non-orthogonality in degrees. |
| `MaxSkewness` | `float` | `4.0` | Hard `checkMesh` quality gate on maximum skewness. |
| `MinLayerCoveragePercent` | `float` | `50.0` | Hard gate on minimum wall boundary-layer coverage percentage when `AddLayers = true`. |

### Solver, Convergence & Execution
| Key | Type | Default | Description |
|---|---|---|---|
| `EndTime` | `int` | `1000` | Maximum steady-state iterations (`endTime` in `controlDict`). |
| `WriteInterval` | `int` | `200` | Field write interval (`writeInterval` in `controlDict`). |
| `ResidualTarget` | `float` | `1e-4` | SIMPLE residual control target for pressure and velocity/turbulence fields. |
| `UsePotentialFoamInit` | `bool` | `false` | Runs `potentialFoam` prior to `simpleFoam` for velocity initialization. |
| `MaxMassBalanceErrorPercent` | `float` | `1.0` | Hard verification gate: maximum allowed mass-balance error in percent. |
| `TrailingWindowIterations` | `int` | `100` | Trailing window length (iterations/samples) for drift and metric averaging. |
| `RelativeDriftTolerance` | `float` | `0.005` | Maximum allowed relative drift across the trailing window. |
| `TreatUnconvergedAsFailed` | `bool` | `true` | Marks unconverged runs as failed (`SimulationFailed = true`). |
| `TargetYPlus` | `float` | `5.0` | Target wall $y^+$ for calibration and post-solve warnings. |
| `YPlusWarnFactor` | `float` | `3.0` | Warning multiplier on `TargetYPlus`. |
| `ExportVtk` | `bool` | `false` | Runs `foamToVTK` after solving to export VTK post-processing files. |
| `OpenFoamBashrcPath` | `string` | `""` | Explicit path to OpenFOAM's `etc/bashrc` (`""` auto-detects `/usr/lib/openfoam/openfoam2506/etc/bashrc` or fallback paths). |
| `MpiRunPath` | `string` | `"/usr/bin/mpirun"` | MPI launcher used for OpenFOAM parallel runs (isolated from Conda/MPICH). |
| `CustomTemplateDirectory` | `string` | `""` | Optional directory of custom OpenFOAM template files overriding generated dictionaries. |
| `CustomDictionaryOverrides` | `Dictionary<string, Dictionary<string, string>>` | `{}` | Per-dictionary key overrides (e.g. `{"system/fvSolution": {"nNonOrthogonalCorrectors": "2"}}`). |
| `ResultMetrics` | `Dictionary<string, string>` | `{"PressureDrop": "PressureDrop"}` | Maps output metric names in `PassiveParameters` to post-processed keys (e.g. `PressureDrop` or `Drag`/`Lift`). |
