# Server, Deployment & Setup Guide (`scripts/`)

> **Related documentation:**
> - **Server Workflow Overview:** [`docs/server-workflow.md`](../docs/server-workflow.md)
> - **All JSON Settings & Physics Presets:** [`docs/config-reference.md`](../docs/config-reference.md) | [`docs/physics-presets.md`](../docs/physics-presets.md)
> - **Creating a New Project / Geometry (3D & 2D):** [`src/projects/README.md`](../src/projects/README.md)
> - **Framework Architecture & C# Development:** [`src/FluidForge/README.md`](../src/FluidForge/README.md)
> - **Local Override Configuration (`*.local.json`):** [`config/README.md`](../config/README.md)

Two scripts, one job: start and monitor optimization runs on the remote Linux server from Windows.

| File | Runs on | Purpose |
|---|---|---|
| `sim.ps1` | Windows | Package code, upload via SSH, dispatch commands, fetch results |
| `sim-runner.sh` | Linux | tmux, xvfb-run, paths, build, start/stop/status |
| `new-project.ps1` | Windows | Create a new project (copies `src/projects/_Template3D`, `_Template2D`, or `_TemplateInternal3D`) |
| `plot_history.py` | Windows / Linux | Inspect and plot SU2 convergence histories (`history.csv`) |
| `plot_calibration.py` | Windows / Linux | Analyze mesh calibration (`calibration.csv`) and generate GCI plots |
| `plot_optimization.py` | Windows / Linux | Analyze and plot optimization results (`results.csv`) |
| `plot_pareto.py` | Windows / Linux | Plot 2-objective Pareto fronts & hypervolume and optionally export Pareto STLs |
| `optimizers/` | Linux / Windows | Optional external Python optimizer workers (`botorch_worker.py`, `pymoo_worker.py`, `environment_opt.yml`) |

`new-project.ps1` is a local scaffolding helper kept in the same toolbox. See
[`src/projects/README.md`](../src/projects/README.md) for what to edit after creating a project.

`sim-runner.sh` is uploaded alongside the source code and can also be invoked directly when
logged in via SSH:

```bash
cd ~/Documents/FluidForge
bash scripts/sim-runner.sh status
```

## Prerequisites

- **On the server:** one-time `setup_server.sh` (Miniconda, SU2, Gmsh, PicoGK, .NET).
  The deployment scripts do not install these tools -- they assume the environment is ready.
- **On Windows:** `ssh`, `scp`, and `tar` (included in Windows 11), an entry in
  `~/.ssh/config`, and key-based SSH authentication. `BIC_12` is the default host alias.

### SSH Key Setup

A single `sim.ps1 run` issues five to six ssh/scp calls. Without key-based auth you would
be prompted for a password on every call, and quiet queries (`running`, `hash`) suppress
stderr so a password prompt would look like a hang.

**Required:** a key pair under `%USERPROFILE%\.ssh\` (standard name such as `id_ed25519` +
`id_ed25519.pub`), with the **public** key appended to `~/.ssh/authorized_keys` on the server.

**Verify key-based login:**

```powershell
ssh -o BatchMode=yes BIC_12 "echo ok"
```

`BatchMode=yes` disables password prompts. `ok` means the key works; `Permission denied`
means the public key is not yet on the server.

**Generate a key pair if needed:**

```powershell
ssh-keygen -t ed25519
```

**Install the public key on the server** (`ssh-copy-id` is not available on Windows):

```powershell
$key = (Get-Content "$env:USERPROFILE\.ssh\id_ed25519.pub" -Raw).Trim()
ssh BIC_12 "mkdir -p ~/.ssh && chmod 700 ~/.ssh && grep -qxF '$key' ~/.ssh/authorized_keys 2>/dev/null || echo '$key' >> ~/.ssh/authorized_keys; chmod 600 ~/.ssh/authorized_keys"
```

Do not use `type ...pub | ssh ...`: PowerShell emits CRLF line endings, and a `\r` in
`authorized_keys` breaks key matching.

## Daily Workflow

```powershell
.\scripts\sim.ps1 projects  # List projects known to the server and existing results
.\scripts\sim.ps1 doctor    # Check SU2, mpirun, gmsh, dotnet, tmux, libpicogk.so
                            # (uploads code on first call, does not build)
.\scripts\sim.ps1 run       # Upload, build, start in tmux
.\scripts\sim.ps1 resume    # Resume an interrupted run (no upload, no build)
.\scripts\sim.ps1 calibrate # Start mesh calibration (determines optimal MeshRefinementFactor)
.\scripts\sim.ps1 recheck -Variant Gen3_Var2 -Finer 1 # Re-evaluate one variant on a finer mesh
.\scripts\sim.ps1 log       # Follow log live (Ctrl+C only exits the viewer)
.\scripts\sim.ps1 status    # Is anything running? Runtime? CSV rows? Forecast?
.\scripts\sim.ps1 fetch     # Download CSV + log + histories to .\server-results\
.\scripts\sim.ps1 stop      # Stop run and lingering solver processes
.\scripts\sim.ps1 delete    # Clean slate: delete results of all projects + logs
```

`delete` prepares a completely fresh run: it removes `results/` (all projects),
`simulation.log`, `logs/`, and `.last_exit` on the server. Code, `bin/`/`obj/`
(including `libpicogk.so`), `.deploy_hash`, and all `*.local.json` files are kept so
the next run starts without rebuilding. It lists targets first and asks for confirmation
(`-Force` skips the prompt), and refuses to run while a simulation is active.

`-Project <Name>` selects the project (folder name under `src/projects/`). For `deploy`,
`run`, `resume`, `doctor`, `calibrate`, and `recheck`, omitting `-Project` falls back to
`MantaAuv` with a warning. `fetch` and `status` automatically query `lastproject` from
the server.

### Resuming an Interrupted Run (`resume`)

`tmux` protects against closing the local console or dropping SSH, but not against a
server reboot or crash. Every run therefore writes a journal
(`results/<Project>/resume/journal.jsonl`), flushing one line per completed evaluation.

```powershell
.\scripts\sim.ps1 status -Project Naca2D   # reports interrupted run and saved count
.\scripts\sim.ps1 resume -Project Naca2D   # resumes from the journal
```

`resume` launches the application with `--resume`, replays completed evaluations from the
journal using the same random seed, and continues from there. At most the single variant
that was running at the time of interruption is lost.

- `resume` **never uploads or builds**. If local code differs from the server, it prints
  a warning (new code requires `run`).
- If project configuration changed, `resume` aborts and names the changed file.
- A fresh `run` (or restart via `deploy`) archives the previous journal as
  `journal_<timestamp>.jsonl`.

On the server directly, `sim-runner.sh` has **no** silent default project -- it requires
`SIM_PROJECT=<Name>` (except for `lastproject`, `projects`, `delete`, `stop`, `running`, `hash`).

## Fetching Results

Fetched archives are extracted into `.\server-results\<Server>_<Project>_<Timestamp>\`
(gitignored) so different projects and downloads never overwrite each other.

`sim.ps1 fetch` runs `sim-runner.sh pack <Project> <scope>` on the server to create a
single `.tar.gz`, downloads it in one `scp` call, and extracts it locally with `tar.exe`.

### Three Scopes: Standard, `-Champion`, `-All`

1. **Standard (`.\scripts\sim.ps1 fetch`)**
   Fetches `results.csv`, `effective-config.json`, `run-info.json`, `source-snapshot/`,
   `simulation.log`, `analysis/histories/`, `calibration/`, and `verification.csv` (each
   if present). Fails with an error and lists available server projects if `results.csv`
   is missing.

2. **Champion (`.\scripts\sim.ps1 fetch -Champion`)**
   Fetches standard files plus ParaView VTU files for the overall champion and top-N
   (`Surface_GenX_VarY.vtu`, `Volume_GenX_VarY.vtu`, and `*Champion*.vtu`).

3. **Full Archive (`.\scripts\sim.ps1 fetch -All`)**
   Downloads the entire `results/<Project>/` folder including all STL, `.su2`, and `.vtu` files.
   Note that a full 10 x 10 run can be 1.5-2 GB.

### Fetching Individual Files Directly

```powershell
# All files for a specific variant
scp BIC_12:Documents/FluidForge/results/MantaAuv/*Gen1_Var2* .\server-results\

# Geometry STL only
scp BIC_12:Documents/FluidForge/results/MantaAuv/Model_Gen1_Var2.stl .\server-results\

# ParaView surface and volume files for one variant
scp BIC_12:Documents/FluidForge/results/MantaAuv/analysis/*Gen1_Var2* .\server-results\

# All surface VTUs without large volume files
scp BIC_12:Documents/FluidForge/results/MantaAuv/analysis/Surface_* .\server-results\

# Error logs for failed evaluations
scp -r BIC_12:Documents/FluidForge/results/MantaAuv/error-logs .\server-results\
```

The best variant is listed in `results.csv` (highest `Fitness`) and at the end of
`simulation.log` under `OVERALL CHAMPION`.

### Result Directory Layout (`results/<Project>/`)

| File / Folder | Description |
|---|---|
| `results.csv` | Main optimization table (one row per variant, parameters, metrics, `MeshCells_*`, `TunnelBlockagePercent`, `Su2_CD_p`/`Su2_CD_v`) |
| `calibration/calibration.csv` | Mesh independence study & GCI report per level |
| `effective-config.json` | Effective configuration with provenance for every key |
| `run-info.json` | Run metadata (commit, branch, deploy hash, tool versions, seed, host, status) |
| `source-snapshot/` | Copy of `src/projects/<Project>/` (`*.cs`, `*.json`) at run start |
| `resume/journal.jsonl` | Resume journal (archived as `journal_<timestamp>.jsonl` on new run) |
| `analysis/histories/` | Saved convergence histories (`History_*.csv`) and solver configs (`Config_*.cfg`) |
| `analysis/Surface_GenX_VarY.vtu` | Surface field for ParaView |
| `analysis/Volume_GenX_VarY.vtu` | Volume flow field for ParaView |
| `Model_GenX_VarY.stl` | Generated geometry |
| `Model_GenX_VarY.su2` | Computational mesh |
| `Meshing_GenX_VarY.geo` | Gmsh script |
| `error-logs/` | Console output of failed SU2 runs |
| `restart.dat`, `surface.vtu`, `vol_solution.vtu`, `current_config.cfg`, `history.csv` | SU2 working files overwritten on each variant |

## Post-Processing & Plotting

The Python plotting scripts require `pandas`, `matplotlib`, and `numpy` (`pip install pandas matplotlib numpy`):

- **Optimization overview (`plot_optimization.py`):**
  ```powershell
  python scripts/plot_optimization.py server-results/<folder>
  ```
- **SU2 convergence histories (`plot_history.py`):**
  ```powershell
  python scripts/plot_history.py server-results/<folder>
  python scripts/plot_history.py server-results/<folder> --var Gen1_Var1
  python scripts/plot_history.py server-results/<folder> --compare
  ```
- **Mesh calibration & GCI (`plot_calibration.py`):**
  ```powershell
  python scripts/plot_calibration.py server-results/<folder>
  ```
- **Multi-objective Pareto front & STL export (`plot_pareto.py`):**
  ```powershell
  python scripts/plot_pareto.py server-results/<folder> --export-stls <output-dir>
  ```

## Monitoring Server Load via SSH

Use `-t` for interactive tools such as `top` or `htop`, and single quotes so `$USER` is
expanded on the server rather than locally in PowerShell:

```powershell
ssh -t BIC_12 'top -u $USER'
ssh -t BIC_12 'htop -u $USER'
ssh BIC_12 'top -b -n 1 -u $USER | head -n 12'
ssh BIC_12 'echo "Cores: $(nproc)"; uptime'
```

## How Deployment Works

1. `src/`, `config/`, and `scripts/` are packed, excluding `bin/`, `obj/`, `*.local.json`,
   `*.md`, `*.ps1`, and `*.pdf`.
2. A SHA-256 fingerprint over paths and contents is compared against `.deploy_hash` on the server.
3. **Unchanged** -> no upload; an active run is left untouched.
4. **Changed while running** -> stops the active run, uploads, builds, and restarts
   (unless `-NoRestart` is passed, which uploads without building).
5. `dotnet build` runs and `libpicogk.so` is copied alongside `FluidForge.dll`.
6. `.deploy_hash` and `deploy-info.json` are written only after a successful build.
7. Before extracting, orphaned files under `src/`, `scripts/`, and `config/` are pruned
   (preserving `*.local.json`, `bin/`, `obj/`, and `results/`).

## One-Time Server Setup (Rootless Linux)

For a fresh Linux server without `sudo`:

1. Copy [`src/FluidForge/setup_server.sh`](../src/FluidForge/setup_server.sh) to the server and run:
   ```bash
   chmod +x setup_server.sh
   ./setup_server.sh
   ```
2. If Conda asks to accept Anaconda Terms of Service, run:
   ```bash
   export PATH=$HOME/miniconda/bin:$HOME/.dotnet:$HOME/software/bin:$HOME/software/gmsh-4.11.1-Linux64/bin:$PATH
   export LD_LIBRARY_PATH=$HOME/miniconda/lib:$LD_LIBRARY_PATH
   conda tos accept --override-channels --channel https://repo.anaconda.com/pkgs/main
   conda tos accept --override-channels --channel https://repo.anaconda.com/pkgs/r
   ```
   and re-run `./setup_server.sh`.
3. Log out of SSH and back in so `~/.bashrc` updates take effect.

## Troubleshooting

- **SU2 runs 12 serial copies instead of MPI (`[MPI]` warning in log):**
  `MpiRunPath` points to OpenMPI instead of MPICH. Use `$HOME/miniconda/envs/mpich/bin/mpiexec`
  or keep the default `"mpirun"` when the `mpich` conda environment is installed.
- **`DllNotFoundException: Unable to load shared library 'libpicogk'`:**
  Ensure both `libpicogk.so` and `libpicogk.26.2.so` exist in `src/FluidForge/bin/Debug/net9.0/`.
  `sim-runner.sh build` copies them automatically.
- **CPU usage stuck at 100% (single-core):**
  Verify `MpiCores` and `MpiRunPath` in `src/projects/<Project>/simulation.json`.
- **`X connection broken` / `Display already in use`:**
  Always use `xvfb-run -a` (automatic display selection); clean up stale instances with `pkill -9 -f Xvfb` if needed.
