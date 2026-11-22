# `_Template3D` — 3D Project Template

> **Step-by-step guide for creating a project:** [`src/projects/README.md`](../README.md)  
> **3D Modeling Manual (PicoGK):** [`src/FluidForge/Kernels/PicoGk/README.md`](../../FluidForge/Kernels/PicoGk/README.md)  
> **Legacy JSON Reference:** [`src/projects/BEDIENHANDBUCH.md`](../BEDIENHANDBUCH.md) *(currently in German)*

This folder is **not a standalone project**, but the template for 3D CFD components (PicoGK → Gmsh 3D → SU2). Copy it via:

```powershell
.\scripts\new-project.ps1 MyComponent
```

This copies the template to `src/projects/MyComponent/` and automatically replaces the `MyProject` placeholder in all filenames, class names, and namespaces. All further steps (parameters, geometry, fitness, trimming JSONs, running) are documented in [`src/projects/README.md`](../README.md).

## Template Notes

- **Excluded from main app build, compiled by tests:**  
  `src/projects/_Template3D/` is excluded in `src/FluidForge/FluidForge.csproj` so that no placeholder `MyProject` definition exists without a matching folder (and does not appear in `--list-projects`). Instead, `tests/FluidForge.Tests/` compiles and validates it so interface changes fail immediately on `dotnet test`.
- **Small initial run:**  
  `simulation.json` sets `MaxIterations = 5` and `VariantsPerIteration = 5` (framework default is 10 × 10). `MeshCalibration` is set to `"Auto"` (takes hours in 3D); for a quick smoke test of PicoGK → STL → Gmsh → SU2 → Fitness, switch to `"Manual"` and set `MeshRefinementFactor`. All remaining JSON keys show documented code defaults as a reference.
- **3D baseline settings:**  
  Uses $y^+ \approx 1.5$ (`TargetYPlus = 1.5`, `BoundaryLayerGrowthRate = 1.28`), curvature-based surface refinement (`MeshSizeFromCurvature = true`, `CurvatureElements = 20`, `CurvatureMinSizeFactor = 0.5`), automatic wind-tunnel clearances decoupled from `RubberBandScaler` ($\ge 5\,L$ upstream, $\ge 15\,L$ downstream, $\ge 5\,W/H$ lateral, $\le 0.3\%$ blockage), startup voxel check (`AutoVoxelResolution`, default `false`), and automatic SU2 restart on remaining drift (`AutoRestartOnDrift = true`, `CauchyTolerance = "1e-5"`).
