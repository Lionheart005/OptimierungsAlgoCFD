# `_Template2D` — 2D Project Template

> **Step-by-step guide for creating a project:** [`docs/creating-a-project.md`](../../../docs/creating-a-project.md) | [`src/projects/README.md`](../README.md)  
> **Physics Presets & Schemas:** [`docs/physics-presets.md`](../../../docs/physics-presets.md)  
> **2D Modeling Manual (`Planar2D` Kernel: `Shape2D`, `Lattice2D`):** [`src/FluidForge/Kernels/Planar2D/README.md`](../../FluidForge/Kernels/Planar2D/README.md)  
> **Complete JSON Key Reference:** [`docs/config-reference.md`](../../../docs/config-reference.md)

This folder is **not a standalone project**, but the slim template for 2D CFD optimizations (`Shape2D` → `Gmsh2DMesher` → SU2 2D). Copy it via:

```powershell
.\scripts\new-project.ps1 MyAirfoil -Template 2D
```

This copies the template to `src/projects/MyAirfoil/` and automatically replaces `MyProject2D` everywhere.

## 2D CFD Project Notes

- **Direct geometry kernel (`DirectGeometryKernel`):** Operates directly on planar vector contours (`Shape2D`) without PicoGK voxel grids and without Xvfb/OpenGL.
- **Single Source of Truth (`project.json`):** Parameters, bounds, and dimensional keys live solely in `project.json` (no C# `CreateDefaults()` duplicate; `MaxDeviations` is auto-derived from `ParameterBounds` when omitted).
- **Physics Preset (`External2D:StandardRans`):** `simulation.json` selects `"Preset": "External2D:StandardRans"` with a 5×5 starter budget. `solvers/su2.json` configures air properties, `AngleOfAttackDeg`, `ReferenceAreaMetric: "Chord"`, and `ResultMetrics` (`Drag`, `Lift`), while `solvers/gmsh.json` inherits 2D structured quad boundary-layer and 500c farfield defaults from the preset. All JSON files reference `schemas/*.schema.json` for VS Code validation.
- **Build & test:** Like `_Template3D`, `_Template2D` is excluded from the main application build and compiled/tested by `tests/FluidForge.Tests/`.
