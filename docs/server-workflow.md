# Server Workflow (`scripts/sim.ps1` & `scripts/sim-runner.sh`)

> **See also:**
> - [Detailed Scripts & Server Setup Guide](../scripts/README.md)
> - [Local Machine Overrides (`config/*.local.json`)](../config/README.md)

FluidForge runs computations on a Linux server via SSH, `tmux`, and `xvfb-run` using `scripts/sim.ps1` from Windows (or `scripts/sim-runner.sh` directly on the server).

---

## 1. All `scripts/sim.ps1` Commands

| Command | Description |
|---|---|
| `doctor` | Checks server tools (`dotnet`, `tmux`, `xvfb-run`, `mpirun`, `SU2_CFD`, `gmsh`, `libpicogk.so`) against effective project paths (uploads code on first run without building). |
| `deploy` | Packs `src/`, `config/`, `scripts/` (excluding `bin/`, `obj/`, `*.local.json`, `*.md`, `*.ps1`, `*.pdf`), uploads if SHA-256 changed, and builds. Restarts active runs unless `-NoRestart` is passed; `-Force` forces upload. |
| `run` | Runs `deploy` and starts the optimization inside a detached `tmux` session (`sim`). |
| `resume` | Resumes an interrupted run from `results/<Project>/resume/journal.jsonl` using `--resume` and the original seed (never uploads or builds). |
| `calibrate` | Runs `deploy` and starts isolated mesh calibration (`--calibrate`) to determine the optimal `MeshRefinementFactor`. |
| `recheck` | Re-evaluates a single variant on a finer mesh (`--recheck[=Variant] --finer=N`), optionally as time-accurate URANS (`-Unsteady`). |
| `status` | Shows run state, elapsed time, CSV row count, ETA forecast, interrupted-run hints, and recent log lines. |
| `log` | Streams `simulation.log` live with colorization (`Ctrl+C` exits only the viewer; `-NoFollow` prints once; `-Lines N` sets tail count). |
| `stop` | Stops the `tmux` session and kills lingering `FluidForge.dll`, `SU2_CFD`, `mpirun`, and `xvfb-run` processes. |
| `fetch` | Downloads results into `server-results/<Server>_<Project>_<Timestamp>/`. Scopes: default (CSV, log, config, histories, calibration), `-Champion` (+champion/top-N VTUs), `-All` (entire results directory). |
| `build` | Runs `dotnet build` on the server and refreshes `libpicogk.so` next to `FluidForge.dll`. |
| `projects` | Lists runnable projects known to the server and existing result folders under `results/`. |
| `delete` | Removes all `results/`, `simulation.log`, `logs/`, `.last_exit`, and `.last_project` on the server after confirmation (`-Force` skips prompt). Preserves code, `bin/`, and `*.local.json`. |

---

## 2. Project Selection Rules

- Pass `-Project <Name>` explicitly for `run`, `deploy`, `resume`, `doctor`, `calibrate`, and `recheck` (if omitted, `sim.ps1` falls back to `MantaAuv` with a warning).
- `status` and `fetch` automatically query the server's `.last_project` when `-Project` is omitted.
- `sim-runner.sh` on Linux has **no** silent default and requires `SIM_PROJECT=<Name>` for project-scoped commands.

---

## 3. Typical Workflow

```powershell
# 1. Verify environment
.\scripts\sim.ps1 doctor -Project Naca2D

# 2. Deploy, build, and start optimization in tmux
.\scripts\sim.ps1 run -Project Naca2D

# 3. Monitor progress
.\scripts\sim.ps1 status
.\scripts\sim.ps1 log -Lines 60

# 4. Download results when complete (or anytime mid-run)
.\scripts\sim.ps1 fetch -Project Naca2D -Champion

# 5. Optional: URANS recheck of a specific variant
.\scripts\sim.ps1 recheck -Project Naca2D -Variant Gen4_Var2 -Finer 1 -Unsteady
```

---

## 4. Plotting Fetched Results

```powershell
python scripts/plot_optimization.py server-results/<folder>
python scripts/plot_history.py server-results/<folder> --compare
python scripts/plot_calibration.py server-results/<folder>
```
