# FluidForge — Automated CFD Shape Optimization

Inspired by various ideas online and in publications — not least by Leap71 — I wanted to explore what an optimization framework could look like that generates and optimizes complex geometries purely in code, independent of heavy CAD programs with cumbersome GUIs.

It was especially important to me to keep the pipeline completely independent of proprietary third-party software suites, so that everything can be scaled to arbitrary complexity. There are no limits on parameters, objective functions, model complexity, runtime, or algorithm choice. Long-term, the goal would be to connect a neural network that generates the C# code for PicoGK itself and evaluates n-dimensional optimization problems that become too complex for brute-force methods.

Much of this was built with AI assistance; I am not a professional software developer and therefore very open to feedback and criticism.

## How It Works

You describe the shape to be optimized in C# code and define active design variables that the optimizer may adjust (such as length and height, or more complex mathematical functions — anything you can express in code). You then define passive parameters that result indirectly from the shape, such as volume and surface area, and choose an objective function (fitness) for the optimizer to maximize. A classic example is `Fitness = Volume / Drag` (maximizing usable volume while minimizing drag).

You run your component through the optimization loop, and at the end the framework outputs the parameter values that achieved the best fitness score — along with the printable `.stl` model (in 3D) or `.svg`/contour model (in 2D).

The optimization algorithms and physics solvers are modular and interchangeable while the core pipeline remains the same. Available optimizers are **Bayesian Optimization** (`Bayes`, default) and an inverse-distance-weighted baseline (`IdwBaseline`). `Bayes` learns a surrogate model (Gaussian Process, implemented in pure C# with no pretraining and no GPU) including uncertainty from previous evaluations and proposes candidates where Expected Improvement is highest, saving expensive CFD runs while still simulating every proposed candidate with the real solver. Currently, 2D and 3D fluid simulations in a flow channel are supported using SU2 and Gmsh. The 3D geometry kernel is PicoGK; the 2D kernel (`Planar2D`) is custom-built to follow PicoGK-style conventions.

Feedback and contributions are always welcome!

~ Leonardo Pleißner

---

## Documentation & Manuals

Detailed documentation is organized by topic:

- **[Legacy User Manual (`src/projects/BEDIENHANDBUCH.md`)](src/projects/BEDIENHANDBUCH.md)**  
  Comprehensive reference for all JSON settings (`project.json`, `simulation.json`, `su2.json`, `gmsh.json`), CLI commands, and result files. *(Note: This legacy manual is currently still in German and will be replaced by modular English documentation under `docs/` in Phase 2.)*
- **[Creating a New Component / Project (`src/projects/README.md`)](src/projects/README.md)**  
  Step-by-step guide for creating custom 3D and 2D projects via `.\scripts\new-project.ps1`, implementing geometry (`IGeometryGenerator`) and fitness (`IFitnessCalculator`), and working with the templates [`_Template3D/`](src/projects/_Template3D/README.md) (3D) and [`_Template2D/`](src/projects/_Template2D/README.md) (2D).
- **[3D Geometry Manual: PicoGK (`src/FluidForge/Kernels/PicoGk/README.md`)](src/FluidForge/Kernels/PicoGk/README.md)**  
  Guide to 3D voxel modeling with PicoGK (`Lattice`, `Voxels`, `Mesh`, Boolean operations, `Offset` blending, STL smoothing, `PicoGkMeshMetrics`), including links to the official PicoGK documentation.
- **[2D Geometry Manual: `Planar2D` Kernel (`src/FluidForge/Kernels/Planar2D/README.md`)](src/FluidForge/Kernels/Planar2D/README.md)**  
  Guide to the analytical 2D geometry kernel (`Shape2D`, `Lattice2D`, `BBox2`, polygons, Catmull-Rom splines, `TripleOffset`, automatic corner detection for 2D CFD boundary layers, JSON & SVG export).
- **[Architecture & Developer Guide (`src/FluidForge/README.md`)](src/FluidForge/README.md)**  
  Technical documentation of the C# framework: repository and layer architecture (`Core`, `Composition`, `Kernels`, `Solvers`), interfaces, data models, algorithms, configuration loader, PicoGK/Gmsh/SU2 integration, and test architecture.
- **[Server, Deployment & Setup Guide (`scripts/README.md`)](scripts/README.md)**  
  Running and monitoring jobs on a Linux compute server from Windows (`sim.ps1`, `sim-runner.sh`, `resume`, `fetch`), convergence plotting (`plot_history.py`), and server setup without root/sudo privileges (`setup_server.sh`).
- **[Third-Party Notices (`THIRD_PARTY_NOTICES.md`)](THIRD_PARTY_NOTICES.md)**  
  Libraries and external tools used (PicoGK, Clipper2, SU2, Gmsh, MPICH), their licenses, and distribution notes.
- **[Local Override Configuration (`config/README.md`)](config/README.md)**  
  How gitignored `*.local.json` files work for machine-specific executable paths.
- **[Development Roadmap & Plans (`.plans/README.md`)](.plans/README.md)**  
  Overview of the current roadmap and phase plans.

---

## License

The code in this repository is licensed under the [MIT License](LICENSE). Third-party libraries and external tools (PicoGK, Clipper2, SU2, Gmsh, MPICH) are subject to their own licenses; see [`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md).
