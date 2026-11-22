# `_Template2D` — 2D Project Template

> **Step-by-step guide for creating a project:** [`src/projects/README.md`](../README.md)  
> **2D Modeling Manual (`Planar2D` Kernel: `Shape2D`, `Lattice2D`):** [`src/FluidForge/Kernels/Planar2D/README.md`](../../FluidForge/Kernels/Planar2D/README.md)  
> **Legacy JSON Reference (incl. 2D meshing options):** [`src/projects/BEDIENHANDBUCH.md`](../BEDIENHANDBUCH.md) *(currently in German)*

This folder is **not a standalone project**, but the template for 2D CFD optimizations (`Shape2D` → `Gmsh2DMesher` → SU2 2D). Copy it via:

```powershell
.\scripts\new-project.ps1 MyAirfoil -Template 2D
```

This copies the template to `src/projects/MyAirfoil/` and automatically replaces `MyProject2D` everywhere. All further steps for customizing the contour, fitness function, and parameters are documented in [`src/projects/README.md`](../README.md).

## 2D CFD Project Notes

- **Direct geometry kernel (`DirectGeometryKernel`):** Operates directly on planar vector contours (`Shape2D`) without PicoGK voxel grids and without Xvfb/OpenGL.
- **2D mesh (`Gmsh2DMesher`):** Builds a circular farfield with a structured quad boundary layer ($y^+ \approx 1$), leading/trailing-edge refinement, detail box, and wake box.
- **SU2 2D:** Solves 2D RANS with `Dimensions = 2` and a fixed or actively optimized angle of attack `AngleOfAttackDeg`.
- **Build & test:** Like `_Template3D`, `_Template2D` is excluded from the main application build and compiled/tested by `tests/FluidForge.Tests/`.
