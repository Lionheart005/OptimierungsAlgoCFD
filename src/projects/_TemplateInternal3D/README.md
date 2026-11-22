# `_TemplateInternal3D` — 3D Internal-Flow (OpenFOAM) Project Template

Template for 3D internal-flow CFD optimization projects (PicoGK + ShapeKernel → `MultiPatchFluidExporter` → `snappyHexMesh` / `cfMesh` → `simpleFoam`).

Copy via:

```powershell
.\scripts\new-project.ps1 MyInternalPipe -Template Internal3D
```

## Notes

- **Excluded from main app build, compiled by tests:** `src/projects/_TemplateInternal3D/` is excluded in `src/FluidForge/FluidForge.csproj` and compiled by `tests/FluidForge.Tests/FluidForge.Tests.csproj`.
- **Preset (`Internal3D:StandardRans`):** `simulation.json` uses `Internal3D:StandardRans` with automatic mesh calibration (`Auto`).
- **OpenFOAM configuration:** `solvers/openfoam.json` overrides fluid and inlet velocity while inheriting meshing and convergence settings from the preset (`schemas/openfoam.schema.json`).
