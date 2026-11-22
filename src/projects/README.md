# Project & Component Guide (`src/projects/`)

> **Related Documentation:**
> - **Legacy User Manual & Full JSON Reference:** [`BEDIENHANDBUCH.md`](BEDIENHANDBUCH.md) *(currently still in German; will be replaced by modular English `docs/` in Phase 2)*
> - **3D Modeling with PicoGK:** [`src/FluidForge/Kernels/PicoGk/README.md`](../FluidForge/Kernels/PicoGk/README.md)
> - **2D Modeling with the `Planar2D` Kernel:** [`src/FluidForge/Kernels/Planar2D/README.md`](../FluidForge/Kernels/Planar2D/README.md)
> - **Framework Architecture & C# Development:** [`src/FluidForge/README.md`](../FluidForge/README.md)
> - **Linux Server Control, Deployment & Setup:** [`scripts/README.md`](../../scripts/README.md)
> - **Local Override Configuration (`*.local.json`):** [`config/README.md`](../../config/README.md)

A **project** is a component to be optimized: its geometry generator, its fitness function, and its configuration values. Each project is a single folder here.

```text
src/projects/
├── _Template3D/     3D project template (not a runnable project; see _Template3D/README.md)
├── _Template2D/     2D project template (not a runnable project; see _Template2D/README.md)
├── MantaAuv/        Manta-ray shaped AUV hull (3D)
└── Naca2D/          2D CFD NACA airfoil with NASA TMR validation
```

**The folder name is the project name.** Not a field in a JSON file, not an entry in a list — the folder itself. If the folder is named `Propeller`, you run it with:

```powershell
.\scripts\sim.ps1 run -Project Propeller
```

Projects live side by side without interfering with one another: separate numbers, separate code, separate outputs under `results/<Project>/`. Switching projects only changes the launch argument — nothing in [`src/FluidForge/`](../FluidForge/) is touched.

List available projects:

```powershell
dotnet run --project src\FluidForge -- --list-projects   # locally
.\scripts\sim.ps1 projects                                # on the server
```

---

## Creating a New Project

### 1. Create the Folder

```powershell
.\scripts\new-project.ps1 Propeller                 # 3D CFD (default)
.\scripts\new-project.ps1 MyAirfoil -Template 2D    # 2D CFD (direct 2D kernel)
```

This copies `_Template3D/` (or `_Template2D/`) to `Propeller/` and replaces the placeholder `MyProject` (or `MyProject2D`) everywhere — in filenames, class names, namespaces, and JSON comment headers. The name must be a valid C# identifier because it also becomes the class and namespace name.

The generated folder contains:

```text
Propeller/
├── project.json                     Initial values, bounds, optimization targets
├── simulation.json                  Framework settings for this project
├── solvers/su2.json                 Flow solver settings
├── solvers/gmsh.json                Mesher settings
├── PropellerProject.cs              Wires the project         (IProjectDefinition)
├── PropellerGeometryGenerator.cs    Builds the geometry       (IGeometryGenerator)
└── PropellerFitnessCalculator.cs    Scores the geometry       (IFitnessCalculator)
```

**No further registration is needed.** `ProjectRegistry` discovers the new class at startup via reflection: no `switch`, no list, no entry in `Program.cs`.

### 2. Define Parameters

In `project.json` — and with matching defaults in `CreateDefaults()` in `PropellerProject.cs`:

```jsonc
"BaseParameters":  { "Length": 50.0, "Width": 30.0 },   // initial values in mm
"MaxDeviations":   { "Length": 5.0,  "Width": 3.0  },   // initial mutation step (used by IdwBaseline)
"ParameterBounds": {
  "Length": { "Min": 20.0, "Max": 100.0 },              // hard bounds
  "Width":  { "Min": 10.0, "Max": 80.0  }
},
"DimensionalParameters": [ "Length", "Width" ]          // lengths ONLY!
```

The parameter names form the contract with the geometry generator. There is no fixed list of allowed parameters: whatever you define here exists.

> **`DimensionalParameters` is for lengths only.** `RubberBandScaler` scales the geometry to PicoGK's working size and scales results back. A ratio, angle, or count would be distorted if scaled. `MantaAuv` intentionally omits `TailTaper` for this reason.

> **Optimizing angle of attack:** If a parameter is named `"AngleOfAttackDeg"` (or `"AngleOfAttack"`), `Su2Solver` intercepts it automatically, sets the freestream angle of attack per variant directly in SU2, and writes it to `PassiveParameters["AngleOfAttackDeg"]`. The geometry generator does **not** need to rotate the airfoil! Never add it to `DimensionalParameters` (degrees, not a length).

### 3. Build the Geometry

> **Detailed Modeling Manuals:**
> - **3D projects (PicoGK `Lattice`, `Voxels`, `Mesh`):** [`src/FluidForge/Kernels/PicoGk/README.md`](../FluidForge/Kernels/PicoGk/README.md)
> - **2D projects (`Shape2D`, `Lattice2D`, `BBox2`):** [`src/FluidForge/Kernels/Planar2D/README.md`](../FluidForge/Kernels/Planar2D/README.md)

Edit `PropellerGeometryGenerator.cs`. The template builds a simple box; replace `BuildVoxelModel` (or `BuildProfileContour` in 2D) and report your metrics at the end:

```csharp
result.AddMetric("Volume", measurement.Volume, MetricScaling.Volume);
result.AddMetric("FrontalArea", measurement.GetFrontalArea(config.UseProjectedFrontalArea), MetricScaling.Area);
result.AddMetric("Span", measurement.Bounds.Size.Y, MetricScaling.Linear);
result.AddMetric("LengthToWidth", length / width, MetricScaling.None);
```

The name you pass here is the exact name read by the fitness formula, referenced by `ReferenceAreaMetric` in `su2.json`, and written as a column in `results.csv`.

> **Get the scaling dimension right.** An incorrect `MetricScaling` will **not** throw an error: the value will simply be off by $s^1$, $s^2$, or $s^3$ while still looking plausible. `Volume` scales cubically, `Area` quadratically, `Linear` linearly, and `None` not at all.

`PicoGkMeshMetrics.Measure(mesh)` computes volume, surface area, and bounding box in a single pass — PicoGK's own `CalculateProperties` does not compute **surface area**.

Look at [`MantaAuv/MantaGeometryGenerator.cs`](MantaAuv/MantaGeometryGenerator.cs) for a real-world example: a `Lattice` combining fuselage and fin-ray wings, organically blended with two `Offset` steps.

### 4. Define the Fitness Function

Edit `PropellerFitnessCalculator.cs`. The template minimizes drag only:

```csharp
record.Fitness = 1.0f / safeDrag;
```

**The framework always maximizes fitness.** Anything that should be minimized belongs in the denominator; anything that should be maximized belongs in the numerator. `MantaAuv` computes `(Volume * SensorDistance) / Drag^DragBalanceFactor`.

Constants for the formula live in `OptimizationTargets` in `project.json`. List their names in `RequiredTargets` so a missing key fails during wiring rather than hours into a run. Do the same for solver/geometry metrics in `RequiredMetrics` — the controller verifies them after the first completed variant.

> **For Bayesian optimization (`"OptimizationAlgorithm": "Bayes"`):** When `RequiredMetrics` lists the metrics used by the formula, `"Metrics"` mode fits a separate surrogate model per metric and evaluates **your** formula over them, preserving penalties and thresholds accurately. This only works if the formula uses **exclusively** active parameters and metrics declared in `RequiredMetrics` (reading undeclared `PassiveParameters` triggers an automatic fallback to `"Fitness"` mode with a warning). Writing additional derived keys (like `GlideRatio` in `Naca2D`) is fine as long as they do not feed back into `Fitness`. `Bayes` also requires `ParameterBounds` for every active parameter.

> **Beware of "reward hacking".** Optimizers are relentless. Without a lower volume bound, the optimizer will shrink the component to dust because tiny bodies have the lowest drag. Hard bounds and penalties are essential.

### 5. Trim Configuration Noise

The template `simulation.json` and `solvers/*.json` list **every** key with its code default as a reference. In a real project, **delete everything you do not override** — keeping default values is noise that goes stale on framework updates.

Two settings intentionally differ from defaults in the templates: `MaxIterations = 5` and `VariantsPerIteration = 5` (instead of 10 × 10) to keep the first run small. `MeshRefinementFactor` is also omitted because `MeshCalibration` is set to `"Auto"`. In 3D, auto-calibration takes hours; for a quick smoke test ("does PicoGK → STL → Gmsh → SU2 → Fitness run end-to-end?"), set `Mode` to `"Manual"` and specify `MeshRefinementFactor`.

For details on every JSON key, see the legacy manual [`BEDIENHANDBUCH.md`](BEDIENHANDBUCH.md#4-alle-einstellungen-datei-für-datei).

### 6. Run

First verify the configuration chain locally (runs on Windows without PicoGK):

```powershell
dotnet build FluidForge.sln
dotnet run --project src\FluidForge --no-build -- Propeller --print-config
```

Then run on the server:

```powershell
.\scripts\sim.ps1 doctor -Project Propeller     # check tools and paths on server
.\scripts\sim.ps1 run    -Project Propeller     # deploy, build, start in tmux
.\scripts\sim.ps1 log                           # tail live log
.\scripts\sim.ps1 fetch  -Project Propeller     # fetch CSVs and effective-config.json
```

Results are stored under `results/Propeller/` (and fetched to `server-results/Propeller/`), including `effective-config.json` with the exact provenance of every key.

> **Note on 3D quality & telemetry:** At startup, `VoxelResolutionAdvisor` checks `BaseVoxelResolution` in real-world coordinates against the surface target cell size (`AutoVoxelResolution` refines automatically if enabled). `GmshCfdMesher` guarantees real-world wind-tunnel clearances (`>= 5 L` upstream, `>= 15 L` downstream, `>= 5 W/H` lateral, `<= 0.3%` blockage) independent of `RubberBandScaler`, and both `results.csv` and `calibration/calibration.csv` log cell-type counts as well as `Su2_CD_p` and `Su2_CD_v`.

---

## Troubleshooting

| Symptom | Cause |
|---|---|
| `Unknown project 'X'...` | Folder name differs from `Name` in the project definition, or typo |
| `No IProjectDefinition found for folder src/projects/X/` | `XProject.cs` is missing, not `public`, lacks a parameterless constructor, or its `Name` does not match |
| `Missing folder for definition XProject...` | Opposite case: class exists without a matching folder (or case mismatch — **Linux is case-sensitive**) |
| Unknown key error on startup | Typo in a JSON file. The error message suggests the closest valid key name |
| `[WARNING]` about machine-specific override | A `*.local.json` file is present. Move permanent settings into the project JSON and delete the local override |
| Run aborts after the first variant with missing metrics | `RequiredMetrics` lists a name provided by neither `AddMetric` nor `ResultMetrics` in `su2.json` |

A non-startable project still appears in `--list-projects` under folders without a project definition.
