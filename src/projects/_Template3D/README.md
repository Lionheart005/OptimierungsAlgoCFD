# `_Template3D` — 3D Project Template

> **Step-by-step guide for creating a project:** [`docs/creating-a-project.md`](../../../docs/creating-a-project.md) | [`src/projects/README.md`](../README.md)  
> **Physics Presets & Schemas:** [`docs/physics-presets.md`](../../../docs/physics-presets.md)  
> **3D Modeling Manual (PicoGK):** [`src/FluidForge/Kernels/PicoGk/README.md`](../../FluidForge/Kernels/PicoGk/README.md)  
> **Complete JSON Key Reference:** [`docs/config-reference.md`](../../../docs/config-reference.md)

This folder is **not a standalone project**, but the slim template for 3D CFD components (PicoGK → Gmsh 3D → SU2). Copy it via:

```powershell
.\scripts\new-project.ps1 MyComponent
```

This copies the template to `src/projects/MyComponent/` and automatically replaces the `MyProject` placeholder in all filenames, class names, namespaces, and JSON comment headers.

## Template Notes

- **Excluded from main app build, compiled by tests:**  
  `src/projects/_Template3D/` is excluded in `src/FluidForge/FluidForge.csproj` so that no placeholder `MyProject` definition exists without a matching folder (and does not appear in `--list-projects`). Instead, `tests/FluidForge.Tests/` compiles and validates it so interface changes fail immediately on `dotnet test`.
- **Single Source of Truth (`project.json`):**  
  Parameters, bounds, and dimensional keys are defined solely in `project.json` (no C# `CreateDefaults()` duplicate; `MaxDeviations` is auto-derived from `ParameterBounds` when omitted).
- **Physics Preset (`External3D:StandardRans`):**  
  `simulation.json` selects `"Preset": "External3D:StandardRans"` and sets a small initial budget (`MaxIterations = 5`, `VariantsPerIteration = 5`). `solvers/su2.json` overrides seawater fluid properties, while `solvers/gmsh.json` inherits all 3D prism boundary-layer and tunnel defaults from the preset. All JSON files reference `schemas/*.schema.json` for VS Code validation.
