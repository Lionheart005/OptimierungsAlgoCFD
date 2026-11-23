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
| `UseRubberBandScaler` | `bool` | `true` | Scales `DimensionalParameters` to `TargetPicoGkSize` and scales reported geometry metrics back. |
| `UseStlSmoothing` | `bool` | `true` | Smooths the exported STL mesh (3D only). |
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
