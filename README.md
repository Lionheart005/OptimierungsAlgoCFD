# FluidForge — Automated CFD Shape Optimization

## Preface
[Currently under heavy development]

Inspired by various ideas across the internet and in publications — not least by LEAP 71 — I really wanted to explore what an optimization framework could look like that generates and optimizes complex shapes purely through code, completely independent of bulky CAD programs and cumbersome GUI menus.
What mattered most to me was keeping the optimization pipeline free from proprietary third-party black boxes so it can scale to arbitrary complexity. There are no artificial limits on the number of parameters, the objective function, model complexity, runtime, or choice of algorithm. Looking ahead, the long-term vision is to hook up a neural network / LLM that writes the C# geometry code for PicoGK itself, while taking over the evaluation of $n$-dimensional optimization problems that are far too complex for deterministic brute-force methods.

Of course, a lot of this was built with the help of AI — I'm not a software developer by trade, and that's why I'm very open to feedback and constructive criticism.

### How it works

You describe the shape you want to optimize in C# code and define the variables you want the optimizer to tune — for example length and height, or much more complex mathematical functions (in theory, you could just as well optimize a Mandelbrot set, for whatever reason you might want to do that). Next, you define passive parameters that aren't tuned directly but result from the generated shape, such as volume, surface area, or frontal area.
Then you decide what objective function (`Objectives` / `Fitness`) the algorithm should optimize for — plus optional constraints. Here you really have to think carefully about what you actually want. A classic example would be `Fitness = Volume / Drag` (i.e., maximizing volume while keeping drag as low as possible). Because squeezing everything into a single function can be quite unwieldy, an upcoming feature will let you evaluate all relevant parameters independently and then look for the best compromise.

Now you send your component through the optimization loop, and at the end the algorithm outputs the parameter values that achieve the best score (fitness) among all feasible designs:

`--> "Our component works best at 30 cm length and 5 cm height to achieve the optimal ratio of volume to drag."`
And you immediately get the printable `.stl` model right along with it!

The optimization algorithms and the physics solvers are modular and interchangeable, while the underlying principle stays the same. Right now, you can choose between **Bayesian Optimization** (`Bayes`, default) and an **Inverse-Distance-Weighting baseline** (`IdwBaseline`; the older evolutionary optimizer was retired after falling behind in benchmarks). `Bayes` learns a surrogate model from previous evaluations. You can calibrate your complexity level along the classic trade-off: **Speed vs. Accuracy**. Running at lower accuracy saves expensive CFD compute time while still evaluating every candidate with real physics and validating the best results at the end. I try to keep the core framework as problem-independent as possible; up to now, 2D and 3D fluid simulations in a virtual wind/water tunnel are implemented.

For all the technical details, check out the guides and `.md` files linked below. They cover everything you need to know about configuration and settings. (And hallelujah, there are a lot of them by now — I even lost track myself in a few places, which is why we now have physics presets like `External3D:StandardRans` so you can get started with just a handful of settings and only touch the deep knobs when you want to.)

For my work so far, I have used **SU2**, **Gmsh**, and **OpenFOAM** (`Internal3D` / `PipeBend90`) for meshing and CFD simulation. The 3D geometry kernel is **PicoGK** (paired with **`LEAP71_ShapeKernel`** for implicit pipes and ducts), the 2D kernel is **`Planar2D`**, and optimization supports both native C# (`Bayes`, `IdwBaseline`) and hybrid Python backends (**`BoTorch`** / **`pymoo`**)!

I'm happy about any ideas, feedback, and interest!

~ Leonardo Pleißner

---

## Technical Overview

FluidForge is a code-first optimization framework that couples parametric C# geometry generation (3D voxel modeling via PicoGK, implicit/lattice pipe & duct generation via `LEAP71_ShapeKernel`, and analytical 2D contours via `Planar2D`), automated meshing (`Gmsh`, `snappyHexMesh`, `cfMesh`), and CFD flow solving (`SU2` and `OpenFOAM` for `Internal3D` / `PipeBend90` and optional `External3D`) with online Bayesian optimization (`Bayes`), an inverse-distance-weighted baseline (`IdwBaseline`), hybrid Python optimizers (`BoTorch` `qLogEI`/`qNEHVI`, `pymoo` `NSGA-II`), and Two-Stage Multi-Fidelity screening (`TwoStageFilter`).

Every project lives in its own folder under `src/projects/<Name>/` with clean JSON configuration files, curated physics presets, and JSON Schema autocomplete for VS Code.

## 5-Minute Quickstart

1. **Create a new project** from the 3D, 2D, or Internal3D template:
   ```powershell
   .\scripts\new-project.ps1 MyComponent                      # External 3D (PicoGK -> Gmsh 3D -> SU2)
   .\scripts\new-project.ps1 MyAirfoil -Template 2D         # External 2D (Planar2D -> Gmsh 2D -> SU2)
   .\scripts\new-project.ps1 MyPipe -Template Internal3D    # Internal 3D (PicoGK / ShapeKernel -> OpenFOAM)
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
   - **First-Time Server Setup:** Run [`src/FluidForge/setup_server.sh`](src/FluidForge/setup_server.sh) once on a fresh Ubuntu server (rootless, installs Miniforge3 into `~/miniconda`, `conda-forge` packages, SU2, MPICH, Gmsh, PicoGK, .NET 9, and `opt-py`). Configure an SSH host alias + key in `~/.ssh/config` and pass `-Server <Alias>` (e.g., `-Server BIC_09`, default `BIC_12`). See [`scripts/README.md`](scripts/README.md) for details.
   ```powershell
   .\scripts\sim.ps1 doctor -Server BIC_09 -Project MyComponent
   .\scripts\sim.ps1 run    -Server BIC_09 -Project MyComponent
   .\scripts\sim.ps1 log    -Server BIC_09
   .\scripts\sim.ps1 fetch  -Server BIC_09 -Project MyComponent
   ```

## Physics & Workflow Presets

Instead of tuning dozens of solver and meshing knobs manually, `simulation.json` can select a curated Layer 1.5 preset (`Domain:Accuracy`):

| Domain \ Accuracy | `FastConcept` | `StandardRans` | `VerificationFine` |
|---|---|---|---|
| `External2D` | 2D manual 1.5x mesh, $y^+ \approx 1.5$, 200c farfield | 2D auto-calibrated (5%), $y^+ \approx 1.0$, 500c farfield | 2D auto-calibrated (2%), $y^+ \approx 0.8$, 10k SU2 iters |
| `External3D` | 3D manual 1.5x mesh, prism $y^+ \approx 2.0$, fast budget | 3D auto-calibrated (5%), prism $y^+ \approx 1.5$, Top-3 verify | 3D auto-calibrated (2%), prism $y^+ \approx 1.0$, DetailBox on |
| `Internal3D` | OpenFOAM auto-sized ($D_h$, 10 cells/$D_h$), auto layers, $y^+ \approx 30$, 500 iters | OpenFOAM auto-calibrated (5%, 20 cells/$D_h$), auto layers, $y^+ \approx 5$ | OpenFOAM 32 cells/$D_h$, auto layers, $y^+ \approx 5$, 2nd-order turb., 2k iters |

> **3D Cell-Count Safeguards (`MaxCells3D` / `MaxGlobalCells` / `MaxLocalCells`):**
> To protect against out-of-memory (RAM overflow) crashes and multi-hour miscalculations from accidentally over-refined 3D meshes, FluidForge enforces default cell caps of `MeshCalibration.MaxCells3D = 15_000_000`, `OpenFoam.MaxGlobalCells = 15_000_000`, and `OpenFoam.MaxLocalCells = 4_000_000` (in serial runs with `MpiCores == 1`, `maxLocalCells` automatically scales up to `MaxGlobalCells`).
> - **Adjust per project:** Override `MeshCalibration.MaxCells3D` in `src/projects/<Name>/simulation.json` or `MaxGlobalCells` / `MaxLocalCells` in `solvers/openfoam.json`.
> - **Adjust per machine:** Set `MeshCalibration.MaxCells3D` in `config/simulation.local.json` (e.g., workstation vs. HPC server — survives every `sim.ps1 deploy`).
> - **Disable completely:** Set `"MaxCells3D": 0` to turn off the upper cell-count limit in both optimization and calibration when very large detail meshes are needed.

Inspect presets and effective values anytime via CLI:
```powershell
dotnet run --project src\FluidForge -- --dump-preset
dotnet run --project src\FluidForge -- --dump-preset External3D:StandardRans
```

## Documentation (`docs/` & Guides)

- **[Creating a New Project (`docs/creating-a-project.md`)](docs/creating-a-project.md)** — 2D/3D quickstart, `project.json` Single Source of Truth, geometry, fitness, and local checks.
- **[Physics Presets & Configuration Layers (`docs/physics-presets.md`)](docs/physics-presets.md)** — Preset matrix, Layer 1–4 precedence, `--print-config`, `--dump-preset`, and VS Code JSON Schemas (`schemas/`).
- **[Server Workflow (`docs/server-workflow.md`)](docs/server-workflow.md)** — Deploying, monitoring, fetching, resuming, calibrating, and rechecking with `scripts/sim.ps1`.
- **[Server, Deployment & Setup Guide (`scripts/README.md`)](scripts/README.md)** — SSH key setup, one-time rootless `setup_server.sh`, deployment internals, and troubleshooting.
- **[Configuration Reference (`docs/config-reference.md`)](docs/config-reference.md)** — Exhaustive key-by-key reference for `simulation.json`, `project.json`, `solvers/su2.json`, `solvers/gmsh.json`, and `solvers/openfoam.json`.
- **[Project Folder Overview (`src/projects/README.md`)](src/projects/README.md)** — Existing projects (`MantaAuv`, `Naca2D`, `PipeBend90`) and templates (`_Template3D`, `_Template2D`, `_TemplateInternal3D`).
- **[3D Geometry Kernel: PicoGK (`src/FluidForge/Kernels/PicoGk/README.md`)](src/FluidForge/Kernels/PicoGk/README.md)** — Voxel modeling (`Lattice`, `Voxels`, `Mesh`, `Offset`, `StlSmoother`).
- **[2D Geometry Kernel: `Planar2D` (`src/FluidForge/Kernels/Planar2D/README.md`)](src/FluidForge/Kernels/Planar2D/README.md)** — Analytical 2D contours (`Shape2D`, `Lattice2D`, corner detection).
- **[Architecture & Developer Guide (`src/FluidForge/README.md`)](src/FluidForge/README.md)** — Clean architecture, interfaces, pipeline internals, and test suite.
- **[Local Override Escape Hatch (`config/README.md`)](config/README.md)** — Machine-specific `*.local.json` rules.
- **[Third-Party Notices (`THIRD_PARTY_NOTICES.md`)](THIRD_PARTY_NOTICES.md)** — PicoGK, Clipper2, SU2, Gmsh, and MPICH licenses.

## License

Licensed under the [MIT License](LICENSE). See [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) for third-party components.
