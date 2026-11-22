# `config/` — Escape Hatch for Machine-Specific Overrides (`*.local.json`)

> **Related Documentation:**
> - **Legacy User Manual & JSON Reference (Layers 1–4):** [`src/projects/BEDIENHANDBUCH.md`](../src/projects/BEDIENHANDBUCH.md)
> - **Configuration Loader Architecture:** [`src/FluidForge/README.md`](../src/FluidForge/README.md)
> - **Server Control & Deployment:** [`scripts/README.md`](../scripts/README.md)

**Under normal circumstances, this folder remains completely empty except for this README.** All regular project settings belong directly in the project folder under `src/projects/<Project>/` (`project.json`, `simulation.json`, `solvers/su2.json`, `solvers/gmsh.json`). An automated test (`ProjectLayoutTests.The_Config_Folder_Holds_No_Checked_In_Json`) ensures that no regular JSON files are ever accidentally committed here.

## Purpose

This directory is the location for gitignored **`*.local.json`** override files that apply exclusively on **this specific machine** across all projects (Layer 3 of the configuration hierarchy; a `src/projects/<Name>/….local.json` file is Layer 4 for a single project on this machine).

The deployment scripts (`scripts/sim.ps1`) intentionally **do not** upload `*.local.json` files and do not delete them during server cleanup.

## When (Not) to Use a `*.local.json` File

- **Target state is that no `*.local.json` exists anywhere.** Any active `*.local.json` emits a prominent `[WARNING]` block in the startup log listing every overridden key with its project and local values, and is recorded in `effective-config.json`.
- **Never abuse for quick test runs:** If you want to test `MaxIterations` or physics values, edit `src/projects/<Project>/simulation.json` directly so that what gets uploaded is what actually runs.
- **Only legitimate use case:** A machine with non-standard installation paths where `mpirun`, `SU2_CFD`, or `gmsh` cannot be found via `PATH` or default search locations:
  ```jsonc
  // config/simulation.local.json (gitignored)
  {
    "MpiRunPath": "/path/to/mpirun",
    "Su2Path": "/path/to/SU2_CFD",
    "GmshPath": "/path/to/gmsh"
  }
  ```

## Inspecting or Removing a `*.local.json` on the Server

Because `sim.ps1 deploy` never touches local override files on the server, inspect and remove them via SSH:

```powershell
ssh BIC_12 "cat ~/Documents/FluidForge/config/simulation.local.json"   # inspect contents
ssh BIC_12 "rm  ~/Documents/FluidForge/config/simulation.local.json"   # delete file
```
