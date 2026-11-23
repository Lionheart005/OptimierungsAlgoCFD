# Architecture & Developer Guide (`src/FluidForge/`)

> **Related Documentation:**
> - **Configuration Reference & Physics Presets:** [`docs/config-reference.md`](../../docs/config-reference.md) | [`docs/physics-presets.md`](../../docs/physics-presets.md)
> - **Creating a New Component / Project (3D & 2D):** [`src/projects/README.md`](../projects/README.md)
> - **3D Geometry Manual (PicoGK):** [`src/FluidForge/Kernels/PicoGk/README.md`](Kernels/PicoGk/README.md)
> - **2D Geometry Manual (`Planar2D` Kernel):** [`src/FluidForge/Kernels/Planar2D/README.md`](Kernels/Planar2D/README.md)
> - **Linux Server Control, Deployment & Setup:** [`scripts/README.md`](../../scripts/README.md)
> - **Local Override Configuration (`*.local.json`):** [`config/README.md`](../../config/README.md)

This document is aimed at developers and describes the software architecture of the C# framework, interface contracts, data model, PicoGK/Gmsh/SU2 coupling, and the test suite.

---

## 1. Repository Structure & Layer Separation

The repository strictly separates the reusable framework from individual component projects. The core (`Core`) has no knowledge of any concrete project, solver, or geometry kernel — this separation is enforced at compile time via namespaces (`Core/` has zero `using` directives referencing `Projects`, `Solvers`, or `Kernels`).

```text
FluidForge/
├── src/
│   ├── FluidForge/                    The framework — NEVER modified for a new project
│   │   ├── Program.cs                 FluidForge                  Composition Root & CLI
│   │   ├── Composition/               FluidForge.Composition      CfdProjectDefinition (3D SU2), Cfd2DProjectDefinition (2D SU2),
│   │   │                                                          OpenFoamProjectDefinition (3D OpenFOAM)
│   │   ├── Core/                      FluidForge.Core             Pure domain core
│   │   │   ├── Interfaces/            Central interface contracts (geometry, mesher, solver, objectives, constraints)
│   │   │   ├── Models/                ModelRecord, GeometryResult, SimulationContext, SolverStage, OptimizationOutcome
│   │   │   ├── Configuration/         JsonConfigLoader, SimulationPresets, ConfigSchema, ProjectRegistry, ProjectPaths
│   │   │   ├── Pipeline/              WorkflowController, ChampionValidator, MeshCalibrationService,
│   │   │   │                          ChampionVerification, MultiFidelitySelector, RunJournal, RunInfoWriter
│   │   │   ├── Algorithms/            BayesOptimizationAlgorithm, IdwBaselineAlgorithm, ExternalProcessOptimizer, Factory
│   │   │   ├── Optimization/          ExternalOptimizerProtocol (JSON Ask/Tell v1.0), ParetoFrontCalculator
│   │   │   └── Utilities/             RubberBandScaler, StlSmoother, StlWriter, MultiPatchMeshPartitioner,
│   │   │                              MeshMetrics, ExecutableResolver
│   │   ├── Kernels/PicoGk/            FluidForge.Kernels.PicoGk   PicoGkKernel, PicoGkMeshMetrics, InternalFlow/ (ShapeKernel)
│   │   ├── Kernels/Planar2D/          FluidForge.Kernels.Planar2D Shape2D, Lattice2D
│   │   ├── Solvers/Cfd/               FluidForge.Solvers.Cfd      GmshCfdMesher, Gmsh2DMesher,
│   │   │                                                          Su2Solver, Su2HistoryAnalyzer,
│   │   │                                                          BoundaryLayerCalculator, YPlusAnalyzer
│   │   └── Solvers/OpenFoam/          FluidForge.Solvers.OpenFoam OpenFoamSnappyMesher, OpenFoamSimpleSolver,
│   │                                                              OpenFoamCaseGenerator, OpenFoamConvergenceJudge
│   └── projects/                      All component projects (one folder = one project name)
│       ├── _Template3D/               FluidForge.Projects.MyProject         (3D external template, SU2)
│       ├── _Template2D/               FluidForge.Projects.MyProject2D       (2D external template, SU2)
│       ├── _TemplateInternal3D/       FluidForge.Projects.MyInternalProject (3D internal template, OpenFOAM)
│       ├── MantaAuv/                  FluidForge.Projects.MantaAuv          (3D AUV hull, SU2)
│       ├── Naca2D/                    FluidForge.Projects.Naca2D            (2D NACA airfoil, SU2)
│       └── PipeBend90/                FluidForge.Projects.PipeBend90        (3D internal pipe bend, OpenFOAM)
├── third_party/ShapeKernel/           Pinned Leap71.ShapeKernel (Apache-2.0)
├── schemas/                           Draft 2020-12 JSON Schemas for VS Code autocomplete
├── docs/                              Modular English user documentation
├── scripts/                           PowerShell, Bash & Python scripts (deploy, server, plots, BoTorch/pymoo workers)
├── tests/FluidForge.Tests/            980+ unit, architecture & real integration tests
├── config/                            Escape hatch for machine-specific *.local.json
└── results/                           Per-project simulation results (results/<Project>/)
```

### Why `src/projects/` Lives Alongside the Framework
"Framework here, projects there" is immediately visible in the directory tree. Technically, `src/FluidForge/FluidForge.csproj` includes projects via `..\projects\**\*.cs`, while excluding **`_Template3D/` and `_Template2D/`** from the application build so that no placeholder definitions (`MyProject`, `MyProject2D`) exist without a matching project folder. Instead, both templates are compiled and verified by `tests/FluidForge.Tests/` — if a framework interface changes, `dotnet test` catches it immediately.

### PicoGK Encapsulation
`using PicoGK;` appears in only two places in the entire framework (`Kernels/PicoGk/PicoGkKernel.cs` and `Kernels/PicoGk/PicoGkMeshMetrics.cs`) plus the 3D geometry generators under `src/projects/`. Pure 2D projects (such as `Naca2D`) or analytical 3D projects require neither PicoGK nor Xvfb/OpenGL: they use `DirectGeometryKernel`.

---

## 2. Local Development Setup

For developing the framework or component geometries on Windows, a minimal setup is sufficient:

1. Install **.NET SDK 9.0** (or newer).
2. Install **Visual Studio Code** with the **C# Dev Kit** extension.
3. **Build & test (no CFD solvers required):**
   ```powershell
   dotnet build FluidForge.sln
   dotnet test FluidForge.sln
   ```
4. **Exporting standalone geometry with PicoGK (basic pattern):**
   Inside a 3D geometry generator, PicoGK converts a `Voxels` volume into a surface `Mesh` and writes it as an STL file:
   ```csharp
   Mesh exportMesh = new Mesh(finalVoxels);
   exportMesh.SaveToStlFile(stlFilePath);
   ```
   New `.cs` files in a project do not need manual registration anywhere — as long as the namespace is valid, the SDK compiles them automatically.

---

## 3. Interfaces (`Core/Interfaces/`)

| Interface | Responsibility | Implementations |
| --- | --- | --- |
| `IProjectDefinition` | Wire a project: name, code defaults, kernel, geometry, fitness, solver stages | `MantaProject` (via `CfdProjectDefinition`), `Naca2DProject` (via `Cfd2DProjectDefinition`) |
| `IGeometryGenerator` | Active parameters → STL export (3D) or `Shape2D` contour (2D) + named metrics | `MantaGeometryGenerator`, `Naca2DGeometryGenerator` |
| `IGeometryKernel` | Provide runtime environment for geometry generation | `PicoGkKernel` (starts `Library.Go(...)`), `DirectGeometryKernel` (direct invocation without OpenGL/PicoGK) |
| `IMeshGenerator` | Geometry → computational mesh (`.su2`) + mesh metrics (`MeshCells`, `BoundaryLayerHexShare`). Optionally also `IMeshMetricsSource`: boundary-layer diagnostics (`BoundaryLayerLayers`, `BoundaryLayerThicknessMm`, `BoundaryLayerStage`, etc.) copied into `PassiveParameters` by `WorkflowController`. Throws `MeshGenerationException` on meshing failure. | `GmshCfdMesher` (3D), `Gmsh2DMesher` (2D) |
| `ISimulationSolver` | Computational mesh → flow solution & metrics in `record.PassiveParameters` | `Su2Solver` |
| `IFitnessCalculator` | Metrics in `ModelRecord` → scalar `Fitness` (higher = better) | `MantaFitnessCalculator`, `Naca2DFitnessCalculator` |
| `IOptimizationAlgorithm` | Propose the next parameter generation from evaluation history | `BayesOptimizationAlgorithm` (default), `IdwBaselineAlgorithm` (IDW baseline) |
| `IModelValidator` | Re-evaluate iteration champion with parameter jitter ("bouncer") | `ChampionValidator` |
| `IHasRepresentativeSection` | Optional for 3D projects: supplies a representative 2D cross-section (`Shape2D`) for fast 2D mesh precalibration | (no active project yet; covered by stub in tests) |

### `IProjectDefinition` and `Composition/`
`IProjectDefinition` keeps `Program.cs` completely free of project-specific knowledge:

```csharp
string Name { get; }                                  // = folder name under src/projects/
ProjectConfig      CreateDefaults();                  // code baseline; project.json overrides it
IGeometryKernel    CreateKernel();
IGeometryGenerator CreateGeometry(ProjectContext c);
IFitnessCalculator CreateFitness(ProjectContext c);
SolverStage[]      CreateStages(ProjectContext c);
```

`ProjectContext` bundles everything a project needs during wiring: project name, project directory, loaded `SimulationConfig` and `ProjectConfig`, and the generic `LoadSolverOptions<T>("su2")` helper pointing at the project directory.

`Composition/` provides two base classes:
- **`CfdProjectDefinition`**: Wires the 3D pipeline `PicoGkKernel` → `GmshCfdMesher` → `Su2Solver`.
- **`Cfd2DProjectDefinition`**: Wires the 2D pipeline `DirectGeometryKernel` → `Gmsh2DMesher` → `Su2Solver` (`Dimensions = 2`).

---

## 4. Pipeline & Workflow Control (`Core/Pipeline/`)

[`WorkflowController.RunOptimization()`](Core/Pipeline/WorkflowController.cs) orchestrates the run. Each candidate passes through `RunPipeline(parameters, iteration, variant)`:

```text
RubberBandScaler.Create(...)          Scale active length parameters to TargetPicoGkSize
  -> _geometry.GenerateAndExport(...) Generate STL / Shape2D and report GeometryResult metrics
  -> ApplyGeometryMetrics(...)        Scale metrics back according to MetricScaling (Linear/Area/Volume)
  -> for each SolverStage:
       Mesher.GenerateMesh(...)       Generate mesh (mesh cache: same mesher instance = 1 mesh)
       Solver.Solve(...)              Write coefficients & diagnostics to record.PassiveParameters
```

### Error Handling and Required Metrics
If meshing or the solver fails, only `record.SimulationFailed = true` is set — the overall optimization continues. The core has **no knowledge of metric names**; how a failed model is scored (e.g. `Fitness = 0.0001f`) is decided solely by the project's `IFitnessCalculator`.

After the **very first successful variant**, the controller checks once whether every metric requested in `RequiredMetrics` is present in `record.PassiveParameters`. If any metric is missing (e.g. `Lift` is used in the fitness formula but omitted from `ResultMetrics` in `solvers/su2.json`), the run aborts immediately with a clear error instead of wasting hours computing fallback fitness values.

### Champion Validator (`ChampionValidator`)
_Oscillating solutions:_ The champion validator, fine verification, and mesh calibration compare values against the configured tolerance, or at least twice the combined uncertainty of both values (`MetricTolerance.WithUncertainty`, keys `Uncertainty_<Metric>`). Smoothly converged runs (uncertainty near 0) are unaffected; for vortex-shedding cases, pure oscillation noise is not mistaken for instability or mesh dependence. A calibration level marked as "unsteady but stable" (`Su2_Unsteady`) counts as converged.

At the end of each iteration, the top candidate is re-evaluated with slightly jittered parameters (`BouncerJitter`, relative to `ParameterBounds` span and clamped to `[Min, Max]`) as variant `99`. If **Fitness** deviates by more than `BouncerTolerance`, the candidate is disqualified as an unstable outlier and the next-best candidate of the iteration is checked.

### Mesh Calibration (`MeshCalibrationService`) & Fine Verification (`ChampionVerification`)
- **Mesh calibration (`--calibrate` or `MeshCalibration.Mode = "Auto"`):** Evaluates the baseline geometry on successively finer meshes (factor $\div \sqrt{2}$ per step) using the strict `ControlRuns` profile, computes the **Grid Convergence Index (GCI)** per Celik et al. (2008), and selects the coarsest mesh level where all `ResultMetrics` stay within the combined relative and absolute tolerance (`Tolerance` + `AbsoluteTolerances`) relative to the finest reference level. Optionally supports fast 2D precalibration (`IHasRepresentativeSection`) before 3D confirmation.
- **Champion verification & recheck (`--recheck`):** Re-evaluates the overall champion (`"Final"`), the top-N (`"FinalTopN"`), or each iteration's winner (`"EachIteration"`) on a mesh refined by `RefinementSteps` (`verification.csv`). With `--unsteady`, a steady solution is first computed on the fine mesh, and then a time-accurate URANS simulation (2nd-order dual time stepping over 20 periods) is started from the generated restart files (`restart_00000.dat` / `restart_00001.dat`).

### Bayesian Optimizer & Surrogates (`Core/Algorithms/BayesOptimizationAlgorithm.cs`, `Core/Surrogates/`)
`"OptimizationAlgorithm": "Bayes"` learns a **Gaussian Process** surrogate online in every run (no pretraining, no Python or ML libraries, CPU-only) from previous evaluations and selects the next candidates via **Expected Improvement**: balancing local exploitation near the current best against exploration in high-uncertainty regions. Every proposed candidate is evaluated with the real CFD solver; the surrogate never replaces a simulation.

- **Building blocks in `Core/Surrogates/`:** `ISurrogateModel` (`Fit`, `Predict`, `AddPseudoObservation`, `ParameterRelevance`), `GaussianProcess` (Matérn-5/2 ARD kernel with per-parameter length scales, learned noise, hyperparameter optimization via log-marginal likelihood with analytical gradients and Adam), `DenseCholesky` (decomposition with jitter ladder, no NuGet dependency), `LatinHypercube`, `NormalDistribution`, `ParameterSpace` (normalization to `[0, 1]`, fixed parameters), `SurrogateTrainingSet` (single source of truth for training data filtering), and `TrustRegionState`.
- **Training data rule:** not failed (`SimulationFailed`), not disqualified by the validator (`ModelRecord.Disqualified`), finite fitness, not a validation re-run (variant 99), and matching the run's `MeshRefinementFactor`. Failed and disqualified points locally repel nearby proposals. **Meshing failures** (`MeshFailed = 1`, `MeshGenerationException`) and **fallback meshes** (`MeshFallback = 1`) do not count as flow solver crashes: they only repel locally and do not feed the failure-probability model, preventing a single meshing issue from blocking an entire region.
- **Modes (`Bayes.SurrogateTarget`):** `"Fitness"` models the scalar fitness directly. `"Metrics"` fits a separate GP for **each metric in `IFitnessCalculator.RequiredMetrics`** and evaluates the project's actual fitness formula via Monte Carlo Expected Improvement. The core knows neither the formula nor the metric names. A cross-check compares the formula output against the true fitness; if it reads undeclared metrics, the run safely falls back to `"Fitness"` with a warning. `"Auto"` selects `"Metrics"` when `RequiredMetrics` is non-empty.
- **High dimensions & long runs:** From 10 free parameters onward, the optimizer searches inside a TuRBO-style trust region around the best point; the box expands after 3 consecutive improvements and halves after `⌈max(4, d) / V⌉` failures, restarting on collapse. A 400-point cap bounds GP training cost ($O(n^3)$). Internal tuning parameters live in `BayesTuning` and are intentionally kept out of `simulation.json`; only `Bayes.SurrogateTarget` and `Bayes.InitialSamples` are user-configurable. The **explorer** (last variant of an iteration when `VariantsPerIteration >= 4`) continues to sample globally.
- **Converged-phase selection:** When Expected Improvement becomes negligible everywhere (`< 0.2%` in log-fitness space; typical when the incumbent is a high outlier or the optimum is reached), `PickByExpectedImprovement` refines near the highest *predicted* fitness (with duplicate repulsion). The explorer selects maximum uncertainty only among points whose optimistic estimate (`μ + 2.4σ` or best of 8 samples) reaches at least half of the current best fitness. Regression tests: `BayesRealDataReplayTests` and `BayesConvergedPhaseTests`.
- **Failure & uncertainty modeling:** True solver failures (`SimulationFailed`) are modeled by a dedicated GP (1 = success / 0 = failed) whose predicted success probability damps the acquisition function to steer clear of crash regions. Solver-reported `Uncertainty_<Metric>` (relative 1σ uncertainty, `Core/Models/MetricUncertainty.cs`) enters the GP as per-point observation noise. `Su2Solver` distinguishes three states: converged, unsteady-stable (block mean without significant trend, `Su2_Unsteady = 1`, window mean is valid), and true drift (which can be treated as failed via `TreatUnconvergedAsFailed`).
- **Transparency:** Columns `Bayes_PredictedFitness/Low/High/Explorer` in `results.csv`, `[BAYES]` log lines (prediction accuracy, parameter relevance), and `analysis/Bayes_History.csv`.
- **Reliability guarantees:** The surrogate never aborts a run (on numerical failure, the iteration falls back to Latin Hypercube samples). Given the same seed and results, proposals are bit-identical so `--resume` (`RunJournal`) stays deterministic; covered by `ReproducibilityAndResumeTests`. `EvaluateAndSelectBest` only updates fitness and prediction columns; state mutates only in `PrepareNextIteration`. `IOptimizationPreflight.Preflight` validates parameter bounds **before** mesh calibration begins.
- **Limitations:** Integer parameters are treated continuously (the generator rounds them). `MaxDeviations` has no effect in `Bayes` mode. Designed for roughly 5 to 20 parameters.
- **Extension points for external models (e.g. a partner's PINN):** either as a fast `ISimulationSolver` under `Solvers/Ml/` (file-based exchange) or as an `ISurrogateModel` serving as a prior for the Gaussian Process.

### Reproducibility, Resume & Voxel Advisor (`RunJournal`, `RunInfoWriter`, `VoxelResolutionAdvisor`)
Every run records its `RandomSeed`, Git/deploy hash, tool versions, and a source snapshot (`source-snapshot/`) in the results directory. At startup, `VoxelResolutionAdvisor` checks whether `BaseVoxelResolution` in real-world coordinates is fine enough for the target surface cell size, warns on coarse voxels, or refines the effective start voxel size automatically when `AutoVoxelResolution = true` (without altering the `--resume` fingerprint). After each completed variant, an entry is flushed to `resume/journal.jsonl`; with `--resume`, the run replays completed variants including their `DurationSeconds` in seconds and seamlessly continues from where it stopped (provided no configuration files changed).

---

## 5. Data Model & Geometry Utilities (`Core/Models/`, `Core/Utilities/`)

- **`ModelRecord`**: Represents one candidate and strictly separates:
  - `ActiveParameters`: Design variables set by the optimizer (e.g. `Length`, `Width`, optionally `AngleOfAttackDeg`).
  - `PassiveParameters`: Measured outputs from geometry (`Volume`, `FrontalArea`, …), mesher (`MeshCells`, `MeshCells_Tri`, `MeshCells_Quad`, `MeshCells_Prism`, `MeshCells_Hexa`, `MeshCells_Tet`, `MeshCells_Pyramid`, `BoundaryLayerHexShare`, `BoundaryLayerLayers`, `BoundaryLayerThicknessMm`, `BoundaryLayerFirstCellMm`, `BoundaryLayerStage`, `BoundaryLayerMaxTiltDeg`, `BoundaryLayerMinScale`, `BoundaryLayerThinnedShare`, `TunnelBlockagePercent`, `TunnelLengthMm`, `TunnelWidthMm`, `TunnelHeightMm`, `TunnelUpstreamL`, `TunnelDownstreamL`, `MeshFallback`, `MeshFailed`, `CellLimitExceeded`), and solver (`Drag`, `Lift`, `YPlus_P95`, `Su2_CD_p`, `Su2_CD_v`, `Su2_Iterations`, `Su2_Converged`, `Su2_Osc_*`, `Su2_IterErr_*`).
  - `Fitness`, `SimulationFailed`, `StlPath`, `MeshPath`, `FineVerified`, `FineDeviation`.
- **`GeometryResult` & `MetricScaling`**: The geometry generator reports each metric with its physical dimension:
  ```csharp
  result.AddMetric("Volume", volume, MetricScaling.Volume);       // scales back cubically (s³)
  result.AddMetric("FrontalArea", area, MetricScaling.Area);      // scales back quadratically (s²)
  result.AddMetric("Span", span, MetricScaling.Linear);           // scales back linearly (s¹)
  result.AddMetric("Ratio", ratio, MetricScaling.None);           // dimensionless (unchanged)
  ```
  The scaling dimension assigned to each metric is recorded in `effective-config.json`.
- **`MeshMetrics` & `PicoGkMeshMetrics`**: Computes volume, surface area, and bounding box of a triangle list in a single pass (PicoGK's built-in `CalculateProperties` does not provide surface area). Signed volume is available to immediately detect inverted triangle normals.
- **`SimulationContext`**: Strictly separates immutable configuration (`Config`, `Project`) from mutable runtime state (`CurrentBaseParameters`, `CurrentDeviations`, `History`). Algorithms never mutate `ProjectConfig` directly; `ResetRuntimeState()` restores the initial state at any time.
- **`SolverStage`**: A pair of `IMeshGenerator` and `ISimulationSolver`. The controller executes `SolverStage[]` sequentially. If two stages share the **same mesher instance** (reference equality), meshing runs only once.
- **`ExecutableResolver`**: Resolves tool paths (`mpirun`, `SU2_CFD`, `gmsh`) across platforms — if an absolute server path is unavailable on another machine, it automatically falls back to searching by executable name in `PATH` and standard/Conda directories.

---

## 6. Solver & Mesher Integration (`Solvers/Cfd/`)

### 6.1 Meshing with Gmsh (`GmshCfdMesher` for 3D, `Gmsh2DMesher` for 2D)
- **Consistent `scaleFactor` & guaranteed wind tunnel (`TunnelGeometry` + `StlWriter`):** `GmshCfdMesher` scales all configured mm cell sizes and tunnel clearances consistently with `scaleFactor` (so all dimensions act in real-world mm) and writes the 3D wind tunnel STL directly in C# without PicoGK. If the tunnel box violates physical minimum clearances (`TunnelMinUpstreamFactor = 5.0`, `TunnelMinDownstreamFactor = 15.0`, `TunnelMinLateralFactor = 5.0`) or exceeds `TunnelMaxBlockagePercent = 0.3`, the mesher automatically expands it outward.
- **Two-step 3D boundary-layer meshing (`BoundaryLayerMode = "Prism"` or `"Hexa"`):**
  1. `Wall_<Variant>.geo` re-parametrizes the STL surface via `ClassifySurfaces` and meshes it cleanly — including curvature-based refinement (`MeshSizeFromCurvature = true`, `CurvatureElements = 20`) with a lower bound (`CurvatureMinSizeFactor = 0.5`) to prevent cell explosions on voxel facets.
  2. `Meshing_<Variant>.geo` extrudes the boundary layer (3D defaults: `TargetYPlus = 1.5`, `BoundaryLayerGrowthRate = 1.28`, first cell height $y_1$ via `BoundaryLayerCalculator`), adds the wake detail box (`DetailBoxMinHeightLengthFactor = 0.15`, `DetailBoxLengthFactor = 1.5`), and fills the remaining domain with tetrahedra. `"Prism"` (triangular prisms without pyramid transition layers) is the robust choice for sharp voxel edges. If extrusion fails in tight fillets, the mesher progressively smooths growth directions and outer-layer thickness while preserving $y_1$ and layer count.
- **2D airfoil meshing (`Gmsh2DMesher`):** Generates a planar mesh from a `Shape2D` contour with a large circular farfield (`FarfieldRadiusFactor = 500`), structured quad boundary layer (`BoundaryLayer` field), leading/trailing-edge refinement (`EdgeRefinementFactor`), `DetailBox`, and `WakeRefinement`. At sharp trailing edges, `FanPoints` prevent boundary-layer self-intersection.

### 6.2 Flow Simulation with SU2 (`Su2Solver`, `Su2HistoryAnalyzer` & `Su2SurfaceAnalyzer`)
- **Physics model:** SU2 solves the incompressible Reynolds-averaged Navier-Stokes equations (`SOLVER= INC_RANS`) with constant density and viscosity and no energy equation (`SA` or `SST`), steady (implicit Euler with adaptive CFL) or time-accurate (`TIME_DOMAIN= YES`, `DUAL_TIME_STEPPING-2ND_ORDER`). On early Cauchy termination (`CauchyTolerance = "1e-5"`) with remaining drift, `AutoRestartOnDrift` (up to `MaxConvergenceRestarts = 3`) automatically continues via `RESTART_SOL= YES`.
- **Boundary conditions (coupled to Gmsh via `Physical Surface` / `Physical Curve`):**
  - `MARKER_FAR = ( Farfield )`: Farfield boundary on the outer domain (freestream in $+x$ direction, rotated by `AngleOfAttackDeg`).
  - `MARKER_HEATFLUX = ( Wall, 0.0 )`: Adiabatic no-slip wall ($\vec{v} = 0$) on the component surface.
  - `MARKER_MONITORING = ( Wall )` / `MARKER_PLOTTING = ( Wall )`: Integration surface for force/moment coefficients ($C_D, C_L, C_{SF}, C_{Mx}, C_{My}, C_{Mz}$) and output surface for `surface.vtu`.
- **Units:** The entire framework operates in **millimeters**; conversion of reference area (`ReferenceAreaMetric`, $\text{mm}^2 \to \text{m}^2$) and coordinates (`MESH_SCALE_CHANGE = 0.001`) for SU2 is handled automatically inside the solver.
- **Result extraction (`ResultMetrics`), convergence & surface analysis:**
  - `ResultMetrics` maps internal metric names (e.g. `"Lift"`) to columns in SU2's `history.csv` (e.g. `"CL"`). Matching is exact by column name (prefix matching only as a last resort with a clear warning, so `"CM"` never ambiguously matches `CMx`/`CMy`/`CMz`).
  - After each run, the solver archives `history.csv` and `current_config.cfg` under `analysis/histories/`. `Su2HistoryAnalyzer` checks **drift between the first and second half of the tail window** (`IterativeToleranceFraction`), computes detrended oscillation amplitude (`Su2_Osc_<Metric>`), and aggregates the result according to `ResultAveraging` (`Last`, `CauchyWindowMean`, `TailMean`).
  - `YPlusAnalyzer` and `Su2SurfaceAnalyzer` evaluate `surface.vtu` and record `YPlus_Median`, `YPlus_P95`, `YPlus_Max`, and pressure/viscous drag components `Su2_CD_p` and `Su2_CD_v` in `record.PassiveParameters`.

---

## 7. Configuration System Under the Hood (`Core/Configuration/`)

- **Four-layer merge (`JsonConfigLoader`):** Merges configuration at the `JsonNode` level:
  1. Code defaults (`SimulationConfig.CreateDefault()`, `CreateDefaults()`, solver option classes)
  2. `src/projects/<Project>/….json` (checked-in project configuration)
  3. `config/….local.json` (gitignored machine-wide escape hatch)
  4. `src/projects/<Project>/….local.json` (gitignored per-project machine escape hatch)
  Nested objects and dictionaries (such as `ResultMetrics` or `AbsoluteTolerances`) merge **additively**; scalar values and arrays are overwritten.
- **Typo protection (`ConfigSchema`):** Validates every JSON key against the target C# type's properties before deserialization (excluding free-form dictionaries such as parameter names under `BaseParameters`). Any unknown key aborts immediately and suggests the closest valid name by Levenshtein distance.
- **`ProjectConfigDto`:** Because `System.Text.Json` does not deserialize `ValueTuple<float, float>` in `ProjectConfig.ParameterBounds` directly from `{ "Min": ..., "Max": ... }`, `ProjectConfigDto` serves as the transfer object.
- **Provenance tracking (`EffectiveConfigWriter`):** Records which layer/file supplied every effective key, prints the startup warning block for active `*.local.json` files, and writes `results/<Project>/effective-config.json`.
- **Project discovery (`ProjectPaths` & `ProjectRegistry`):**
  - `ProjectPaths` searches for `src/projects/` in order: CLI argument → `SIM_PROJECTS_DIR` environment variable → walking up to 6 levels from the working directory. JSON files intentionally do **not** use `CopyToOutputDirectory` in `.csproj` so edits take effect immediately without rebuilding.
  - `ProjectRegistry` discovers all `IProjectDefinition` classes via reflection at startup and cross-checks them **bidirectionally** against subfolders in `src/projects/` (a definition without a folder or a folder without a definition fails immediately with a clear error).

---

## 8. Extending the Framework (Developer How-To)

> **Creating a new component / project (geometry, fitness, parameters):**  
> See **[`src/projects/README.md`](../projects/README.md)**. Zero lines under `src/FluidForge/` need to be changed.

### Adding a New Solver or Mesher
1. Implement `IMeshGenerator` or `ISimulationSolver` (e.g. under `Solvers/Fem/`).
2. Create an options class (automatically loaded from `solvers/<name>.json` across all four configuration layers via `c.LoadSolverOptions<T>("<name>")`).
3. Override `CreateStages(ProjectContext c)` in your project and append the additional `SolverStage`:
   ```csharp
   public override SolverStage[] CreateStages(ProjectContext c) => new[]
   {
       new SolverStage(
           new GmshCfdMesher(c.LoadSolverOptions<GmshMesherOptions>("gmsh")),
           new Su2Solver(c.LoadSolverOptions<Su2SolverOptions>("su2"))),
       new SolverStage(femMesher, femSolver) // second mesh + second solver
   };
   ```

### Adding a New Optimization Algorithm
1. Implement `IOptimizationAlgorithm` under `Core/Algorithms/` (keep `Random` injectable via constructor for deterministic tests).
2. Register the new algorithm name in [`OptimizationAlgorithmFactory.Create(...)`](Core/Algorithms/OptimizationAlgorithmFactory.cs).
3. Optionally implement `IOptimizationPreflight` if the algorithm has preconditions (e.g. complete `ParameterBounds`): `Preflight` runs in `Program` before mesh calibration and fails fast before the first expensive CFD run. If the algorithm must support `--resume`, ensure proposals are deterministic given the same seed and history (see `BayesOptimizationAlgorithm`).

---

## 9. Automated Tests & Architecture Guardrails (`tests/FluidForge.Tests/`)

```powershell
dotnet test FluidForge.sln
```

All 980+ unit tests run in seconds on Windows and Linux without external solvers installed (PicoGK, Gmsh, SU2, and OpenFOAM are replaced by lightweight mocks/stubs for unit tests; real solver tests carry `[Trait("Category", "Integration")]` or `[Trait("Category", "PythonOpt")]`). Alongside unit tests for algorithms, scaling, GCI calibration, convergence analysis, and journaling, dedicated test classes enforce repository-wide consistency:

- **`ProjectLayoutTests`**: Verifies that all config files live in their project folders, `config/` contains no checked-in JSONs, and default executable paths remain `PATH`-relative.
- **`TemplateProjectTests`**: Verifies that `_Template3D` (3D) and `_Template2D` (2D) are complete, have commented JSONs, and their code defaults match their template JSONs.
- **`HandbookCoverageTests`**: Uses reflection across all option classes and `scripts/sim.ps1` to verify that every JSON setting and script command is documented in [`docs/config-reference.md`](../../docs/config-reference.md) and [`docs/server-workflow.md`](../../docs/server-workflow.md).
- **`ScriptContractTests`**: Statically checks `scripts/sim.ps1` and `scripts/sim-runner.sh` for SSH timeouts, parameter forwarding (`-Project` → `SIM_PROJECT`), and protection against silent fallbacks.
- **`RepositoryHygieneTests`**: Scans all text files in `src/`, `scripts/`, and `config/` for accidentally committed secrets (private keys, API tokens, passwords).

---

## 10. Intentional Architectural Decisions & Roadmap

1. **The validator (`ChampionValidator`) checks fitness, not a single metric** — keeping the core free of domain terms like `"Drag"`.
2. **Failed simulations are excluded from IDW support points** — they would distort interpolation and do not count toward the minimum sample threshold. The same rule applies centrally in `SurrogateTrainingSet` for `Bayes`, which also excludes disqualified candidates (`Disqualified`).
3. **The core knows no metric names** — even `RequiredMetrics` is passed to `WorkflowController` purely as `IReadOnlyCollection<string>`.
4. **No silent project fallback in `Program.cs` or `sim-runner.sh`** — a typo in the project name aborts immediately rather than running the wrong component for hours.
5. **The folder name is the sole source of the project name** — there is no `"ProjectName"` field in `project.json`.

### Outlook & Next Milestones
Historical refactoring notes and current development plans are documented in **[`.plans/README.md`](../../.plans/README.md)** and **[`.plans/ROADMAP.md`](../../.plans/ROADMAP.md)**.
