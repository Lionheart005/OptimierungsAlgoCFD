# FluidForge — Automated CFD Shape Optimization

FluidForge is a code-first optimization framework that couples parametric C# geometry generation (3D voxel modeling via PicoGK and analytical 2D contours via `Planar2D`), automated meshing (`Gmsh`), and CFD flow solving (`SU2`) with online Bayesian optimization (`Bayes`) and an inverse-distance-weighted baseline (`IdwBaseline`).

Every project lives in its own folder under `src/projects/<Name>/` with clean JSON configuration files, curated physics presets, and JSON Schema autocomplete for VS Code.

## 5-Minute Quickstart

1. **Create a new project** from the 3D or 2D template:
   ```powershell
   .\scripts\new-project.ps1 MyComponent                  # 3D (PicoGK -> Gmsh 3D -> SU2)
   .\scripts\new-project.ps1 MyAirfoil -Template 2D       # 2D (Planar2D -> Gmsh 2D -> SU2)
   ```
2. **Edit geometry, fitness, and parameters:**
   - Parameters & bounds (`project.json`, Single Source of Truth)
   - Geometry (`<Name>GeometryGenerator.cs`)
   - Objective function (`<Name>FitnessCalculator.cs`, framework maximizes `Fitness`)
   - Preset & overrides (`simulation.json`, e.g. `"Preset": "External3D:StandardRans"`)
3. **Verify configuration & run unit tests locally (no CFD solvers needed):**
   ```powershell
   dotnet build FluidForge.sln
   dotnet test FluidForge.sln --filter "Category!=Integration"
   dotnet run --project src\FluidForge --no-build -- MyComponent --print-config
   dotnet run --project src\FluidForge --no-build -- MyComponent --smoke-test
   ```
4. **Deploy and run on the Linux compute server:**
   ```powershell
   .\scripts\sim.ps1 doctor -Project MyComponent
   .\scripts\sim.ps1 run    -Project MyComponent
   .\scripts\sim.ps1 log
   .\scripts\sim.ps1 fetch  -Project MyComponent
   ```

## Physics & Workflow Presets

Instead of tuning dozens of solver and meshing knobs manually, `simulation.json` can select a curated Layer 1.5 preset (`Domain:Accuracy`):

| Domain \ Accuracy | `FastConcept` | `StandardRans` | `VerificationFine` |
|---|---|---|---|
| `External2D` | 2D manual 1.5x mesh, $y^+ \approx 1.5$, 200c farfield | 2D auto-calibrated (5%), $y^+ \approx 1.0$, 500c farfield | 2D auto-calibrated (2%), $y^+ \approx 0.8$, 10k SU2 iters |
| `External3D` | 3D manual 1.5x mesh, prism $y^+ \approx 2.0$, fast budget | 3D auto-calibrated (5%), prism $y^+ \approx 1.5$, Top-3 verify | 3D auto-calibrated (2%), prism $y^+ \approx 1.0$, DetailBox on |
| `Internal3D` | Phase 3 OpenFOAM placeholder | Phase 3 OpenFOAM placeholder | Phase 3 OpenFOAM placeholder |

Inspect presets and effective values anytime via CLI:
```powershell
dotnet run --project src\FluidForge -- --dump-preset
dotnet run --project src\FluidForge -- --dump-preset External3D:StandardRans
```

## Documentation (`docs/` & Guides)

- **[Creating a New Project (`docs/creating-a-project.md`)](docs/creating-a-project.md)** — 2D/3D quickstart, `project.json` Single Source of Truth, geometry, fitness, and local checks.
- **[Physics Presets & Configuration Layers (`docs/physics-presets.md`)](docs/physics-presets.md)** — Preset matrix, Layer 1–4 precedence, `--print-config`, `--dump-preset`, and VS Code JSON Schemas (`schemas/`).
- **[Server Workflow (`docs/server-workflow.md`)](docs/server-workflow.md)** — Deploying, monitoring, fetching, resuming, calibrating, and rechecking with `scripts/sim.ps1`.
- **[Configuration Reference (`docs/config-reference.md`)](docs/config-reference.md)** — Exhaustive key-by-key reference for `simulation.json`, `project.json`, `solvers/su2.json`, and `solvers/gmsh.json`.
- **[Project Folder Overview (`src/projects/README.md`)](src/projects/README.md)** — Existing projects (`MantaAuv`, `Naca2D`) and templates (`_Template3D`, `_Template2D`).
- **[3D Geometry Kernel: PicoGK (`src/FluidForge/Kernels/PicoGk/README.md`)](src/FluidForge/Kernels/PicoGk/README.md)** — Voxel modeling (`Lattice`, `Voxels`, `Mesh`, `Offset`, `StlSmoother`).
- **[2D Geometry Kernel: `Planar2D` (`src/FluidForge/Kernels/Planar2D/README.md`)](src/FluidForge/Kernels/Planar2D/README.md)** — Analytical 2D contours (`Shape2D`, `Lattice2D`, corner detection).
- **[Architecture & Developer Guide (`src/FluidForge/README.md`)](src/FluidForge/README.md)** — Clean architecture, interfaces, pipeline internals, and test suite.
- **[Local Override Escape Hatch (`config/README.md`)](config/README.md)** — Machine-specific `*.local.json` rules.
- **[Third-Party Notices (`THIRD_PARTY_NOTICES.md`)](THIRD_PARTY_NOTICES.md)** — PicoGK, Clipper2, SU2, Gmsh, and MPICH licenses.

## License

Licensed under the [MIT License](LICENSE). See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for third-party components.
