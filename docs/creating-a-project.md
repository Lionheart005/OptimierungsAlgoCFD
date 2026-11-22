# Creating a New Project (3D & 2D)

> **See also:**
> - [Physics Presets & Layer Overrides](physics-presets.md)
> - [Complete Configuration Key Reference](config-reference.md)
> - [Server Workflow (`sim.ps1`)](server-workflow.md)
> - [3D Modeling with PicoGK](../src/FluidForge/Kernels/PicoGk/README.md) | [2D Modeling with `Planar2D`](../src/FluidForge/Kernels/Planar2D/README.md)

Every component project lives in its own folder under `src/projects/<Name>/`. **The folder name is the project name** — `ProjectRegistry` discovers `IProjectDefinition` classes automatically via reflection with no manual registration.

---

## 1. Scaffold from a Template

```powershell
.\scripts\new-project.ps1 Propeller              # 3D CFD (PicoGK -> Gmsh 3D -> SU2)
.\scripts\new-project.ps1 MyAirfoil -Template 2D # 2D CFD (Planar2D -> Gmsh 2D -> SU2)
```

This creates `src/projects/<Name>/` containing:

| File | Responsibility |
|---|---|
| `project.json` | **Single Source of Truth** for parameters, bounds, targets, and dimensional keys |
| `simulation.json` | Preset selection (`Preset`), budget, cores, and framework overrides |
| `solvers/su2.json` | Fluid properties, freestream velocity, and result metric mappings |
| `solvers/gmsh.json` | Optional mesher overrides (defaults come from the active `Preset`) |
| `<Name>Project.cs` | Project wiring (`Name`, `CreateGeometry`, `CreateFitness`) |
| `<Name>GeometryGenerator.cs` | Parametric C# geometry builder (`IGeometryGenerator`) |
| `<Name>FitnessCalculator.cs` | Scalar objective formula (`IFitnessCalculator`) |

---

## 2. Configure Parameters (`project.json` — Single Source of Truth)

Templates do **not** duplicate parameters in C# (`CreateDefaults()` defaults to an empty `ProjectConfig` in `IProjectDefinition` / `CfdProjectDefinitionBase`). Define everything once in `project.json`:

```jsonc
{
  "$schema": "../../../schemas/project.schema.json",
  "BaseParameters": { "Length": 50.0, "Width": 30.0, "Height": 20.0 },
  "ParameterBounds": {
    "Length": { "Min": 20.0, "Max": 100.0 },
    "Width":  { "Min": 10.0, "Max": 80.0 },
    "Height": { "Min": 5.0,  "Max": 50.0 }
  },
  "DimensionalParameters": [ "Length", "Width", "Height" ]
}
```

- **`MaxDeviations` is optional:** If omitted for a parameter in `ParameterBounds`, `ProjectConfigDto` automatically derives `max((Max - Min) * 0.1, 1e-4)`.
- **`DimensionalParameters` is for physical lengths (mm) only:** `RubberBandScaler` scales them to `TargetPicoGkSize` and rescales geometry metrics back. Never list angles (e.g. `AngleOfAttackDeg`), counts, or dimensionless ratios here.
- **Optimizing angle of attack:** Add `"AngleOfAttackDeg"` to `BaseParameters` and `ParameterBounds` (not `DimensionalParameters`). `Su2Solver` intercepts it automatically and rotates the freestream vector.

---

## 3. Implement Geometry (`<Name>GeometryGenerator.cs`)

- **3D (PicoGK):** Build `Lattice` / `Voxels` / `Mesh`, export STL, optionally smooth via `StlSmoother`, measure via `PicoGkMeshMetrics.Measure(...)`, and register metrics on `GeometryResult`:
  ```csharp
  result.AddMetric("Volume", measurement.Volume, MetricScaling.Volume);
  result.AddMetric("FrontalArea", measurement.GetFrontalArea(config.UseProjectedFrontalArea), MetricScaling.Area);
  result.AddMetric("Span", measurement.Bounds.Size.Y, MetricScaling.Linear);
  result.AddMetric("AspectRatio", span / length, MetricScaling.None);
  ```
- **2D (`Planar2D`):** Build a `Shape2D`, export contour JSON (`shape.SaveContour(..., cornerAngleDeg: 35.0f)`) and optional SVG, measure via `shape.Measure()`, and return `GeometryKind.Contour2D`.

> **Get `MetricScaling` right:** `Volume` scales as $s^3$, `Area` as $s^2$, `Linear` as $s^1$, and `None` unscaled. An incorrect dimension silently mis-scales values without throwing an error.

---

## 4. Implement Fitness (`<Name>FitnessCalculator.cs`)

The optimizer **always maximizes `record.Fitness`**:
- Always check `record.SimulationFailed` first and assign a small positive fallback (e.g. `0.0001f`).
- Declare all metrics read by your formula in `RequiredMetrics` so `WorkflowController` aborts immediately after variant 1 if a key is missing, and so `Bayes` (`SurrogateTarget = "Metrics"`) can model each physical quantity separately.
- Constants belong in `OptimizationTargets` in `project.json`, validated in the calculator constructor.

---

## 5. Choose a Preset & Verify Locally

Select a preset in `simulation.json` (e.g. `"Preset": "External3D:StandardRans"` or `"External2D:StandardRans"`) and keep only your project-specific overrides in `simulation.json` and `solvers/*.json`.

Validate locally before deploying:

```powershell
dotnet build FluidForge.sln
dotnet test FluidForge.sln --filter "Category!=Integration"
dotnet run --project src\FluidForge --no-build -- <Name> --print-config
dotnet run --project src\FluidForge --no-build -- <Name> --smoke-test
```

`--smoke-test` generates geometry at 6 deterministic parameter probes (`Base`, `Min`, `Mid`, `Max`, plus 2 LHS points), verifies mesh watertightness / 2D contour validity, evaluates pre-CFD constraints, and checks ±5 % parameter sensitivity.
