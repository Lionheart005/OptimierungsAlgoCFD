# Physics Presets, Configuration Layers & Schemas

> **See also:**
> - [Complete Configuration Key Reference](config-reference.md)
> - [Creating a New Project](creating-a-project.md)

FluidForge uses **progressive disclosure**: new projects pick a curated preset in `simulation.json` and only override the settings they care about, while advanced users can inspect or override every underlying solver and mesher parameter.

---

## 1. Configuration Layer Precedence

Values are merged at the `JsonNode` level from general to specific (nested objects merge additively; scalars and arrays overwrite; unknown keys abort startup with typo suggestions):

| Layer | Source | Scope |
|---|---|---|
| **Layer 1** | Compiled code defaults (`SimulationConfig`, `Su2SolverOptions`, `GmshMesherOptions`, `Gmsh2DMesherOptions`, `OpenFoamOptions`) | Framework baseline |
| **Layer 1.5** | **`Preset`** in `simulation.json` (`SimulationPresets`) | Curated domain + accuracy overlay for `simulation.json` and `solvers/*.json` |
| **Layer 2** | `src/projects/<Name>/*.json` | Committed project overrides |
| **Layer 3** | `config/*.local.json` | Gitignored machine-wide escape hatch |
| **Layer 4** | `src/projects/<Name>/*.local.json` | Gitignored project+machine escape hatch |

Use `"Preset": ""` or `"Preset": "Custom"` to disable Layer 1.5 completely.

> **Auto Mesh Calibration & Presets:** In `"MeshCalibration": { "Mode": "Auto" }`, specifying `MeshRefinementFactor` in a project JSON aborts startup to avoid ambiguity. However, if you start from a `FastConcept` or `VerificationFine` preset (which sets `Manual` + `MeshRefinementFactor`) and override `"MeshCalibration": { "Mode": "Auto" }` in your project `simulation.json`, FluidForge ignores the preset's `MeshRefinementFactor` cleanly.

---

## 2. The 3 × 3 Preset Matrix

| Preset Name | Domain | Accuracy | Calibration & Mesh | SU2 Budget & $y^+$ | Verification |
|---|---|---|---|---|---|
| `External2D:FastConcept` | `External2D` | `FastConcept` | `Manual` (`1.5`), 200c farfield | 1500 iters, Cauchy `1e-4`, $y^+=1.5$, AutoStop On (12) | `Off` |
| `External2D:StandardRans` | `External2D` | `StandardRans` | `Auto` (5% tol), 500c farfield | 3000 iters, Cauchy `5e-5`, $y^+=1.0$, AutoStop On (20) | `FinalTopN` (3, 1 step) |
| `External2D:VerificationFine` | `External2D` | `VerificationFine` | `Manual` (`0.5`), 500c farfield, BL growth `1.15` | 10000 iters, Cauchy `5e-6` (floor `2e-6`), $y^+=0.8$, `SlopeLimiterFlow: VENKATAKRISHNAN`, `VenkatLimiterCoeff: 0.5`, `FreestreamNuFactor: 3.0`, AutoStop Off | `Off` |
| `External3D:FastConcept` | `External3D` | `FastConcept` | `Manual` (`1.5`), Prism, `DetailBox: false` *(OF: 60 cells/$L$, auto layers)* | 2000 iters, Cauchy `1e-4`, $y^+=2.0$ *(OF: $30$)*, AutoStop On (12) | `Off` |
| `External3D:StandardRans` | `External3D` | `StandardRans` | `Auto` (5% tol), Prism, `DetailBox: false` *(OF: 120 cells/$L$, auto layers)* | 3000 iters, Cauchy `5e-5`, $y^+=1.5$ *(OF: $5$)*, AutoStop On (20) | `FinalTopN` (3, 1 step) |
| `External3D:VerificationFine` | `External3D` | `VerificationFine` | `Manual` (`0.8`), Prism, `DetailBox: true` *(OF: 200 cells/$L$, auto layers)* | 8000 iters, Cauchy `1e-5` (floor `1e-6`), $y^+=1.0$, `VenkatLimiterCoeff: 0.05` *(OF: $5$, 2nd-order turb.)*, AutoStop Off | `Off` |
| `Internal3D:FastConcept` | `Internal3D` | `FastConcept` | `Manual` (`1.5`), `BaseCellSizeMm: 0` (10 cells/$D_h$), auto layers | `EndTime` 500, `ResidualTarget` `5e-4`, $y^+ \approx 30$, `PressureDrop`, AutoStop On (12) | `Off` |
| `Internal3D:StandardRans` | `Internal3D` | `StandardRans` | `Auto` (5% tol), `BaseCellSizeMm: 0` (20 cells/$D_h$), auto layers | `EndTime` 1000, `ResidualTarget` `1e-4`, $y^+ \approx 5$, `PressureDrop`, AutoStop On (20) | `FinalTopN` (3, 1 step) |
| `Internal3D:VerificationFine` | `Internal3D` | `VerificationFine` | `Manual` (`1.0`), `BaseCellSizeMm: 0` (32 cells/$D_h$), auto layers | `EndTime` 2000, `ResidualTarget` `5e-5`, $y^+ \approx 5$, 2nd-order turb., `PressureDrop`, AutoStop Off | `Off` |

**Shorthands:** `"External2D"` → `"External2D:StandardRans"`, `"External3D"` → `"External3D:StandardRans"`, `"Internal3D"` → `"Internal3D:StandardRans"`.

> **External3D OpenFOAM Overlay:** All `External3D:*` presets also carry a `solvers/openfoam.json` overlay (`DomainMode: "External3D"`, `Drag`/`Lift` metrics, scale-invariant auto-meshing with `CellsPerCharacteristicLength` = 60 / 120 / 200 and automatic `NearfieldBox`/`WakeBox` `refinementRegions`, `TargetYPlus` = 30 / 5 / 5, `SecondOrderTurbulence: true` in `VerificationFine`) so projects using `OpenFoamProjectDefinition` in external mode inherit calibrated defaults automatically.
>
> **Single-Fidelity Calibration Remains the Default:** None of the curated presets enables `MultiFidelity`. All `StandardRans` presets remain the automatic calibration default for optimizations (`MeshCalibration.Mode = "Auto"`, `ChampionVerification.Mode = "FinalTopN"`), evaluating every variant at full calibrated fidelity. By contrast, `VerificationFine` represents the manual high-detail reference tier without automatic mesh calibration or post-run verification (`MeshCalibration.Mode = "Manual"`, `ChampionVerification.Mode = "Off"`). See **Section 6** below before enabling `TwoStageFilter`.

### 2.1 Scale-Invariant OpenFOAM Auto-Meshing & 3D Cell Limits

- **Scale-invariant cell sizing (`BaseCellSizeMm = 0`):** All `Internal3D:*` and `External3D:*` OpenFOAM presets set `BaseCellSizeMm = 0` (Auto mode). Instead of a fixed millimeter background grid, cell sizes scale automatically from the characteristic geometry length:
  - **`Internal3D`:** Scales from hydraulic diameter $D_h$ using `CellsPerCharacteristicLength` (`FastConcept = 10`, `StandardRans = 20`, `VerificationFine = 32`).
  - **`External3D`:** Scales from body length $L$ using `CellsPerCharacteristicLength` (`FastConcept = 60`, `StandardRans = 120`, `VerificationFine = 200`) and automatically generates `NearfieldBox` and `WakeBox` `refinementRegions`.
  - **Backward compatibility:** Setting an explicit `BaseCellSizeMm > 0` in `solvers/openfoam.json` bypasses auto-sizing and keeps 100% backward-compatible fixed-mm behavior.
- **Physics-based boundary layers (`BoundaryLayerLayers = 0` & `TargetYPlus`):** When `BoundaryLayerLayers = 0` (Auto mode in all 6 presets), FluidForge computes the layer count automatically from `TargetYPlus` (`FastConcept = 30.0`, `StandardRans = 5.0`, `VerificationFine = 5.0`) and the local surface cell size, setting `firstLayerThickness = 2 * y_p` with `relativeSizes false`. OpenFOAM uses All-$y^+$ wall treatment (`nutUSpaldingWallFunction` + `kLowReWallFunction`). Setting `BoundaryLayerLayers > 0` keeps explicit manual layer counts.
- **Second-order turbulence (`SecondOrderTurbulence`):** Enabled (`true`) in `*:VerificationFine` (`bounded Gauss linearUpwind`) and `false` in `FastConcept` / `StandardRans`.
- **3D cell-count safeguards (`MaxCells3D` / `MaxGlobalCells` / `MaxLocalCells`):**
  - **Defaults:** `MeshCalibration.MaxCells3D = 15_000_000`, `OpenFoam.MaxGlobalCells = 15_000_000`, and `OpenFoam.MaxLocalCells = 4_000_000` (in serial runs with `MpiCores == 1`, `maxLocalCells` automatically scales up to `MaxGlobalCells`).
  - **Protection:** Prevents out-of-memory (RAM overflow) crashes and multi-hour miscalculations from accidentally over-refined 3D meshes.
  - **How to adjust:** Override per project in `src/projects/<Name>/simulation.json` (`MeshCalibration.MaxCells3D`) or `solvers/openfoam.json` (`MaxGlobalCells` / `MaxLocalCells`), or set machine-specific limits (e.g., workstation vs. HPC server) in `config/simulation.local.json` (survives every `sim.ps1 deploy`).
  - **How to disable:** Set `"MaxCells3D": 0` to disable the upper cell-count limit completely (in both optimization and calibration) when very large, high-detail meshes are required.

---

## 3. CLI Transparency (`--print-config` & `--dump-preset`)

- **Inspect the full preset catalog:**
  ```powershell
  dotnet run --project src\FluidForge -- --dump-preset
  ```
- **Materialize a preset's complete effective JSONs (to stdout or a folder):**
  ```powershell
  dotnet run --project src\FluidForge -- --dump-preset External3D:StandardRans
  dotnet run --project src\FluidForge -- --dump-preset External2D:StandardRans --out .\inspected-preset
  ```
- **Inspect effective configuration + provenance for any project:**
  ```powershell
  dotnet run --project src\FluidForge -- Naca2D --print-config
  ```
  Outputs flat `KEY=VALUE` lines for `SimulationConfig` (consumed by `scripts/sim-runner.sh`) followed by commented `# --- Provenance & Solver Options ---` sections showing every overridden key's origin (`preset:...`, `src/projects/...`, or `*.local.json`) and effective `solvers/*.json` values.

---

## 4. VS Code JSON Schemas (`schemas/`)

All configuration JSON files support a top-level `"$schema"` reference (ignored by `ConfigSchema` and `JsonConfigLoader` at runtime):

| Config File | Schema Path (from `src/projects/<Name>/`) |
|---|---|
| `simulation.json` | `"../../../schemas/simulation.schema.json"` |
| `project.json` | `"../../../schemas/project.schema.json"` |
| `solvers/su2.json` | `"../../../../schemas/su2.schema.json"` |
| `solvers/gmsh.json` (3D) | `"../../../../schemas/gmsh.schema.json"` |
| `solvers/gmsh.json` (2D) | `"../../../../schemas/gmsh2d.schema.json"` |
| `solvers/openfoam.json` | `"../../../../schemas/openfoam.schema.json"` |

Regenerate schemas after modifying configuration classes:
```powershell
dotnet run --project src\FluidForge -- --export-schemas schemas
```

---

## 5. Symmetry, Multi-Point & Turbulence Model Guidance

- **3D Half-Model Symmetry (`SymmetryPlane`):** Set `"SymmetryPlane": "Y0"` (keeps $Y \ge 0$) or `"Z0"` (keeps $Z \ge 0$) in `solvers/gmsh.json` and/or `solvers/su2.json`. FluidForge automatically synchronizes the plane between mesher and solver, clips full-body STLs cleanly at the symmetry plane, aligns boundary-layer extrusion vectors onto the plane, emits `MARKER_SYM= ( Symmetry )`, and halves `REF_AREA` so reported `CD`/`CL` remain exact full-body coefficients (alongside `SymmetryFactor = 2` and `DragForceN`).
- **Multi-Point Evaluation (`OperatingPoints`):** Configure a list of `{ "Name", "Velocity", "AngleOfAttackDeg", "Weight" }` entries in `solvers/su2.json` to evaluate multiple flight/operating points sequentially on the same mesh without re-meshing. Per-point results are recorded as `<Metric>_OP1`, `<Metric>_OP2`, ... alongside weighted top-level averages.
- **Turbulence Models & Low-$Re$ Transparency:** `TurbulenceModel` supports `"SA"` (Spalart-Allmaras) and `"SST"` (Menter $k$-$\omega$ SST). Whenever $0 < Re_L < 5 \times 10^5$, SU2 logs an explicit `[WARNING]` explaining that fully turbulent RANS ignores laminar separation bubbles.

---

## 6. Geometry Best Practices & Multi-Fidelity Caveats

### Recommended 3D Geometry Setup (Fixed Size, Relative Parameters, Smoothing Off)
- **Keep `UseStlSmoothing` disabled (`false`, default) unless explicitly needed:** `StlSmoother` applies Laplacian premelting, Taubin smoothing, and axis-wise bounding-box rescaling. This can subtly deform thin trailing edges, sharp features, or planar `Internal3D` inlet/outlet flanges (and is skipped by `MultiPatchFluidExporter`). Enable it intentionally (`true`) only for coarse organic voxel/`Lattice` hulls where voxel staircases interfere with Gmsh prism extrusion.
- **Prefer a fixed physical reference size + relative (dimensionless) shape parameters + `"UseRubberBandScaler": false` (default):**
  - Fixing a reference dimension (e.g., hull length $L$, chord $c$, or port diameter $D_{\text{in}}$) and varying dimensionless ratios (`WidthRatio`, `ThicknessRatio`, `BendRadiusRatio`, `TaperRatio`) keeps Reynolds number $Re_L$ comparable, prevents scale-based reward hacking, and avoids `DimensionalParameters`/`MetricScaling` mistakes.
  - With `UseRubberBandScaler = false`, PicoGK operates directly in real millimeters with a predictable `BaseVoxelResolution` (important for `Internal3D` parts such as `PipeBend90` so ports and wall thicknesses are never silently shrunk to `TargetPicoGkSize`). Keep `UseRubberBandScaler = true` only as opt-in when spanning large multi-meter scales without adjusting voxel size.

### Multi-Fidelity (`TwoStageFilter`) — When to Keep It `"Off"` & Operational Behavior
- **Default is `"Off"`:** Standard automatic mesh calibration (`MeshCalibration.Mode = "Auto"`) + single-fidelity evaluation remains the recommended default for physical reliability.
- **Conservative defaults & safeguards:** `TwoStageFilter` defaults to `CoarseMeshFactor = 1.5` and `CoarseMaxIterations = 0` (uncapped, so Stage-1 SU2 runs reach their Cauchy window without premature `TreatUnconvergedAsFailed` failures). Stage-2 promoted candidates use the `"Stage2"` tag and therefore run with the normal `SimulationConfig` iteration budget (rather than `ControlRuns` 20,000 iterations).
- **Risk of rank reversal on coarse meshes:** Even at `CoarseMeshFactor = 1.5` (and especially at `>= 2.0`), coarse screening can miss or invert subtle boundary-layer improvements (2–5% drag gains, thin trailing edges, local separation bubbles). FluidForge compares promoted Stage-1 vs. Stage-2 fitness after every iteration (`[MULTI-FIDELITY] Stage-1 vs Stage-2 delta:`) and logs a `[WARNING]` on rank reversals, `> 25%` relative delta, or Stage-2 failures.
- **Iteration cap (`CoarseMaxIterations`) differs sharply between solvers:**
  - In **OpenFOAM `simpleFoam` (`Internal3D`)** with wall functions, setting `CoarseMaxIterations` (e.g. `400`–`500`) can accelerate Stage-1 screening.
  - In **SU2 turbulent 3D external flows** (`y+ ≈ 1`, `CauchyElements = 300`), keep `CoarseMaxIterations = 0` (uncapped) or `>= 1500`; `MultiFidelitySelector.GetStartupWarnings` warns at startup if a tight SU2 cap is configured.
- **Surrogate modeling scope:**
  - Built-in C# `Bayes` trains its objective GP strictly on full-fidelity Stage-2 records (`SurrogateTrainingSet`, while still learning from Stage-1 `PreConstraint` boundaries and failed locations) and emits a startup warning when paired with `TwoStageFilter`.
  - For joint multi-fidelity objective and constraint surrogate modeling, set `"OptimizationAlgorithm": "BoTorch"`.
  - Keep `"MultiFidelity": { "Mode": "Off" }` for 2D runs, fast internal 3D runs, and fine-tuning; use `TwoStageFilter` only for expensive 3D exploratory runs after verifying coarse-vs-fine rank correlation.
