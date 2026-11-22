#!/usr/bin/env bash
#
# Server-side remote control: builds, starts, monitors, and stops the
# optimization run on the Linux machine (rootless, no sudo).
#
# Normally invoked via SSH from scripts/sim.ps1 (Windows), but can also be
# used directly when logged in via SSH:
#
#     cd ~/Documents/FluidForge
#     bash scripts/sim-runner.sh status
#
# Combines build, start, monitoring, and packaging commands. Initial server
# setup is handled by setup_server.sh -- this script assumes Miniconda, SU2,
# Gmsh, PicoGK, and .NET are already installed.

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"

SESSION="${SIM_SESSION:-sim}"

# NO silent default project. If SIM_PROJECT is unset or a typo fails to propagate
# into tmux, we must abort rather than silently computing the wrong project and
# overwriting its results.
#
# require_project decides which commands need a project. stop, log, delete,
# running, hash, projects, and lastproject do not need one.
PROJECT_NAME="${SIM_PROJECT:-}"

APP_PROJECT="$REPO_DIR/src/FluidForge/FluidForge.csproj"
BIN_DIR="$REPO_DIR/src/FluidForge/bin/Debug/net9.0"
APP_DLL="$BIN_DIR/FluidForge.dll"

PROJECTS_DIR="$REPO_DIR/src/projects"
CONFIG_DIR="$REPO_DIR/config"

LOG_FILE="$REPO_DIR/simulation.log"
LOG_ARCHIVE="$REPO_DIR/logs"
HASH_FILE="$REPO_DIR/.deploy_hash"
EXIT_FILE="$REPO_DIR/.last_exit"
LAST_PROJECT_FILE="$REPO_DIR/.last_project"

# Project-scoped paths are initialized by require_project after validating PROJECT_NAME.
PROJECT_DIR=""
RESULT_DIR=""
RESULT_CSV=""

PICOGK_BUILD_DIR="${PICOGK_BUILD_DIR:-$HOME/software/PicoGK/build}"

# Process pattern used to identify our own run. Intentionally matches the DLL
# rather than "dotnet" so pkill never kills unrelated .NET processes.
PROCESS_PATTERN="FluidForge.dll"

# ---------------------------------------------------------------------------
# Environment
# ---------------------------------------------------------------------------
# IMPORTANT: "ssh host command" starts a non-interactive bash shell where Ubuntu's
# ~/.bashrc exits early. Neither conda init nor MESA_* variables are loaded there,
# so every invocation constructs its own environment here.
load_env() {
    export PATH="$HOME/miniconda/bin:$HOME/.dotnet:$HOME/software/bin:$HOME/software/gmsh-4.11.1-Linux64/bin:$PATH"
    export LD_LIBRARY_PATH="$HOME/miniconda/lib:${LD_LIBRARY_PATH:-}"

    # Suppress OpenMPI hwloc warnings on virtualized machines
    export HWLOC_HIDE_ERRORS=1

    # PicoGK initializes OpenGL even in headless mode: force software rendering
    export LIBGL_ALWAYS_SOFTWARE=1
    export MESA_GL_VERSION_OVERRIDE=4.1
    export MESA_GLSL_VERSION_OVERRIDE=410

    export DOTNET_CLI_TELEMETRY_OPTOUT=1
    export DOTNET_NOLOGO=1
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
say()  { printf '%s\n' "$*"; }
ok()   { printf '  [ OK ]    %s\n' "$*"; }
fail() { printf '  [MISSING] %s\n' "$*"; }

is_running() {
    pgrep -u "$USER" -f "$PROCESS_PATTERN" >/dev/null 2>&1
}

running_pid() {
    pgrep -u "$USER" -f "$PROCESS_PATTERN" | head -n 1
}

has_session() {
    tmux has-session -t "$SESSION" >/dev/null 2>&1
}

# List project directories as seen by the application (excluding templates and build folders).
list_project_folders() {
    [ -d "$PROJECTS_DIR" ] || return 0

    find "$PROJECTS_DIR" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' 2>/dev/null \
        | grep -Ev '^(_Template3D|_Template2D|_TemplateInternal3D|bin|obj|\..*)$' | sort || true
}

# Abort if no project was specified, then derive project-specific paths.
require_project() {
    local command_name="$1"

    if [ -z "$PROJECT_NAME" ]; then
        say "[RUNNER] ERROR: No project specified, and there is no default."
        say "         From Windows:  .\\scripts\\sim.ps1 <command> -Project <Name>"
        say "         Manually:      SIM_PROJECT=<Name> bash scripts/sim-runner.sh $command_name"

        local folders
        folders="$(list_project_folders | tr '\n' ' ')"
        if [ -n "$folders" ]; then
            say "         Available:     $folders"
        else
            say "         No projects found under src/projects/."
        fi

        exit 2
    fi

    # Validate identifier characters because PROJECT_NAME is also a C# class/folder name
    # and is interpolated into the tmux command line.
    case "$PROJECT_NAME" in
        *[!A-Za-z0-9_]* | [!A-Za-z]*)
            say "[RUNNER] ERROR: '$PROJECT_NAME' is not a valid project name."
            say "         Must start with a letter followed by letters, digits, or underscores."
            exit 2
            ;;
    esac

    PROJECT_DIR="$PROJECTS_DIR/$PROJECT_NAME"
    RESULT_DIR="$REPO_DIR/results/$PROJECT_NAME"
    RESULT_CSV="$RESULT_DIR/results.csv"
}

# Query the application once for its effective configuration (KEY=VALUE lines).
# Runs before kernel initialization (no PicoGK / Xvfb required).
CONFIG_DUMP=""
CONFIG_ERROR=""

load_effective_config() {
    CONFIG_DUMP=""
    CONFIG_ERROR=""

    if [ ! -f "$APP_DLL" ]; then
        CONFIG_ERROR="application is not built yet"
        return 0
    fi

    if ! CONFIG_DUMP="$(dotnet "$APP_DLL" "$PROJECT_NAME" --print-config 2>&1)"; then
        CONFIG_ERROR="application exited with an error"
        return 0
    fi

    if ! printf '%s\n' "$CONFIG_DUMP" | grep -q '^MpiRunPath='; then
        CONFIG_ERROR="application does not recognize '--print-config' (outdated build?)"
    fi
}

config_value() {
    printf '%s\n' "$CONFIG_DUMP" | sed -n "s/^$1=//p" | head -n 1
}

resolve_tool() {
    local candidate="$1"
    [ -n "$candidate" ] || return 1

    if [ -x "$candidate" ]; then
        printf '%s' "$candidate"
        return 0
    fi

    command -v "$candidate" 2>/dev/null || return 1
}

# Ensure libpicogk.so sits next to the DLL. Must run after every dotnet build
# because MSBuild may clean bin/Debug/net9.0.
restore_picogk() {
    mkdir -p "$BIN_DIR"

    if [ -f "$BIN_DIR/libpicogk.so" ] && [ -f "$BIN_DIR/libpicogk.26.2.so" ]; then
        return 0
    fi

    local lib
    lib="$(find "$PICOGK_BUILD_DIR" -iname '*picogk.so' 2>/dev/null | head -n 1 || true)"

    if [ -z "$lib" ]; then
        say "[WARNING] No compiled PicoGK library found under $PICOGK_BUILD_DIR."
        say "          Runs will not start without libpicogk.so -- run setup_server.sh."
        return 1
    fi

    cp -f "$lib" "$BIN_DIR/libpicogk.so"
    cp -f "$lib" "$BIN_DIR/libpicogk.26.2.so"
    say "[RUNNER] Refreshed libpicogk.so (source: $lib)"
}

# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

cmd_doctor() {
    require_project doctor
    load_env
    local problems=0

    say "=== Environment on $(hostname) ==="
    say "Repo:    $REPO_DIR"
    say "Project: $PROJECT_NAME"
    say ""

    say "--- Tools ---"
    local tool
    for tool in dotnet tmux xvfb-run; do
        if command -v "$tool" >/dev/null 2>&1; then
            ok "$tool -> $(command -v "$tool")"
        else
            fail "$tool is not in PATH"
            problems=$((problems + 1))
        fi
    done

    say ""
    say "--- Tool paths (effective project configuration) ---"

    local is_openfoam_project=0
    local has_su2_config=0
    local is_pure_openfoam=0
    [ -f "$PROJECT_DIR/solvers/su2.json" ] && has_su2_config=1
    [ -f "$PROJECT_DIR/solvers/openfoam.json" ] && is_openfoam_project=1

    load_effective_config

    if [ -z "$CONFIG_ERROR" ]; then
        local preset_val
        preset_val="$(config_value "Preset")"
        case "$preset_val" in
            Internal3D*) is_openfoam_project=1 ;;
        esac
    fi

    if [ "$is_openfoam_project" -eq 1 ] && [ "$has_su2_config" -eq 0 ]; then
        is_pure_openfoam=1
    fi

    if [ -n "$CONFIG_ERROR" ]; then
        fail "Could not query effective configuration: $CONFIG_ERROR"
        say  "          Command was:  dotnet <DLL> $PROJECT_NAME --print-config"

        if [ -n "$CONFIG_DUMP" ]; then
            say "          Application output:"
            printf '%s\n' "$CONFIG_DUMP" | tail -n 15 | while IFS= read -r line; do
                say "            $line"
            done
        fi

        say "          This is NOT a path error -- tool paths were not checked."
        say "          A fresh build usually fixes this:  .\\scripts\\sim.ps1 build"
        problems=$((problems + 1))
    else
        local entry
        for entry in MpiRunPath Su2Path GmshPath; do
            local configured resolved
            configured="$(config_value "$entry")"

            if [ -z "$configured" ]; then
                if [ "$is_pure_openfoam" -eq 1 ]; then
                    say "  [INFO]    $entry is not configured (not required for pure OpenFOAM project $PROJECT_NAME)"
                else
                    fail "$entry is missing from configuration"
                    problems=$((problems + 1))
                fi
                continue
            fi

            # Match application behavior: bare mpirun/mpiexec prefers the dedicated MPICH environment
            if [ "$entry" = "MpiRunPath" ]; then
                case "$configured" in
                    mpirun|mpiexec) [ -x "$HOME/miniconda/envs/mpich/bin/mpiexec" ] && configured="$HOME/miniconda/envs/mpich/bin/mpiexec" ;;
                esac
            fi

            if resolved="$(resolve_tool "$configured")"; then
                if [ "$resolved" = "$configured" ]; then
                    ok "$entry -> $resolved"
                else
                    ok "$entry -> $configured (via PATH: $resolved)"
                fi

                # Official SU2 binary is built with MPICH; OpenMPI launches independent serial copies
                if [ "$entry" = "MpiRunPath" ] && "$resolved" --version 2>&1 | grep -qiE "open mpi|openrte|prrte"; then
                    if [ "$is_pure_openfoam" -eq 1 ]; then
                        say "  [INFO]    $entry is OpenMPI (SU2 requires MPICH, but $PROJECT_NAME uses OpenFOAM)"
                    else
                        fail "$entry is OpenMPI; SU2 requires MPICH (otherwise each rank runs as a serial copy)"
                        say  "          Set: MpiRunPath = \$HOME/miniconda/envs/mpich/bin/mpiexec (see scripts/README.md)"
                        problems=$((problems + 1))
                    fi
                fi
            else
                if [ "$is_pure_openfoam" -eq 1 ]; then
                    say "  [INFO]    $entry -> $configured (not installed; not required for pure OpenFOAM project $PROJECT_NAME)"
                else
                    fail "$entry -> $configured (not a file and not in PATH)"
                    case "$configured" in
                        */*) say "          Path does not exist or is not executable." ;;
                        *)   say "          Searched PATH: $PATH" ;;
                    esac
                    say "          Configure '$entry' in src/projects/$PROJECT_NAME/simulation.json."
                    problems=$((problems + 1))
                fi
            fi
        done
    fi

    say ""
    say "--- OpenFOAM (isolated from Conda MPICH) ---"
    local of_bashrc=""
    if [ -f "/usr/lib/openfoam/openfoam2506/etc/bashrc" ]; then
        of_bashrc="/usr/lib/openfoam/openfoam2506/etc/bashrc"
    else
        local candidate
        for candidate in /usr/lib/openfoam/openfoam*/etc/bashrc; do
            if [ -f "$candidate" ]; then
                of_bashrc="$candidate"
            fi
        done
    fi
    if [ -z "$of_bashrc" ] && [ -f "$HOME/miniconda/envs/openfoam/etc/bashrc" ]; then
        of_bashrc="$HOME/miniconda/envs/openfoam/etc/bashrc"
    fi

    if [ -z "$of_bashrc" ]; then
        if [ "$is_openfoam_project" -eq 1 ]; then
            fail "OpenFOAM etc/bashrc not found (/usr/lib/openfoam/openfoam2506/etc/bashrc or fallback)"
            problems=$((problems + 1))
        else
            say "  [INFO]    OpenFOAM not found (not required for $PROJECT_NAME)"
        fi
    else
        ok "OpenFOAM bashrc -> $of_bashrc"
        local of_probe
        of_probe="$(
            OF_BASHRC="$of_bashrc" bash -c '
                export PATH="$(printf "%s" "${PATH:-}" | tr ":" "\n" | grep -viE "(miniconda|anaconda|conda|mpich)" | paste -sd: -)"
                export LD_LIBRARY_PATH="$(printf "%s" "${LD_LIBRARY_PATH:-}" | tr ":" "\n" | grep -viE "(miniconda|anaconda|conda|mpich)" | paste -sd: -)"
                export PATH="/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:$PATH"
                set +u
                source "$OF_BASHRC" >/dev/null 2>&1 || true
                set -u
                printf "VER|%s\n" "${WM_PROJECT_VERSION:-}"
                for t in simpleFoam snappyHexMesh checkMesh surfaceFeatureExtract cartesianMesh; do
                    p="$(command -v "$t" 2>/dev/null || true)"
                    printf "TOOL|%s|%s\n" "$t" "$p"
                done
                if [ -x /usr/bin/mpirun ]; then
                    mv="$(/usr/bin/mpirun --version 2>&1 | head -n 1 || true)"
                    printf "MPI|/usr/bin/mpirun|%s\n" "$mv"
                else
                    printf "MPI||\n"
                fi
            ' 2>/dev/null || true
        )"

        local kind arg1 arg2
        while IFS='|' read -r kind arg1 arg2; do
            case "$kind" in
                VER)
                    if [ -n "$arg1" ]; then
                        if printf '%s' "$arg1" | grep -qiE '^(v?2506|v?2[3-9][0-9]{2}|2[3-9][0-9]{2})'; then
                            ok "WM_PROJECT_VERSION -> $arg1"
                        else
                            say "  [INFO]    WM_PROJECT_VERSION -> $arg1 (expected ESI v2506 / 2312+)"
                        fi
                    else
                        if [ "$is_openfoam_project" -eq 1 ]; then
                            fail "WM_PROJECT_VERSION is empty after sourcing $of_bashrc"
                            problems=$((problems + 1))
                        else
                            say "  [INFO]    WM_PROJECT_VERSION is empty after sourcing $of_bashrc"
                        fi
                    fi
                    ;;
                TOOL)
                    if [ -n "$arg2" ]; then
                        ok "$arg1 -> $arg2"
                    else
                        if [ "$is_openfoam_project" -eq 1 ]; then
                            fail "$arg1 missing in isolated OpenFOAM environment"
                            problems=$((problems + 1))
                        else
                            say "  [INFO]    $arg1 missing (not required for $PROJECT_NAME)"
                        fi
                    fi
                    ;;
                MPI)
                    if [ -n "$arg1" ]; then
                        if printf '%s' "$arg2" | grep -qiE "open mpi|openmpi|openrte|prrte" && ! printf '%s' "$arg2" | grep -qiE "mpich|hydra"; then
                            ok "OpenFOAM MPI -> $arg1 ($arg2)"
                        else
                            if [ "$is_openfoam_project" -eq 1 ]; then
                                fail "OpenFOAM MPI -> $arg1 ($arg2) is not OpenMPI (OpenFOAM requires system OpenMPI, not MPICH)"
                                problems=$((problems + 1))
                            else
                                say "  [INFO]    OpenFOAM MPI -> $arg1 ($arg2)"
                            fi
                        fi
                    else
                        if [ "$is_openfoam_project" -eq 1 ]; then
                            fail "/usr/bin/mpirun missing (OpenFOAM requires system OpenMPI)"
                            problems=$((problems + 1))
                        else
                            say "  [INFO]    /usr/bin/mpirun not found (not required for $PROJECT_NAME)"
                        fi
                    fi
                    ;;
            esac
        done <<< "$of_probe"
    fi

    say ""
    say "--- Optional Python Optimizer Env (opt-py) ---"
    local opt_py="$HOME/miniconda/envs/opt-py/bin/python"
    if [ -x "$opt_py" ]; then
        local opt_ver
        if opt_ver="$("$opt_py" -c 'import torch, botorch, pymoo, matplotlib; print(f"torch={torch.__version__}, botorch={botorch.__version__}, pymoo={pymoo.__version__}, matplotlib={matplotlib.__version__}")' 2>/dev/null)"; then
            ok "opt-py -> $opt_py ($opt_ver)"
        else
            say "  [INFO]    opt-py found at $opt_py (optional packages torch/botorch/pymoo/matplotlib incomplete)"
        fi
    else
        say "  [INFO]    opt-py not installed at $opt_py (optional; C# optimizer runs natively)"
    fi

    say ""
    say "--- Project ---"
    if [ -f "$APP_PROJECT" ]; then ok "csproj present"; else fail "csproj missing: $APP_PROJECT"; problems=$((problems + 1)); fi

    if [ -d "$PROJECT_DIR" ]; then
        ok "Project folder present: src/projects/$PROJECT_NAME"
    else
        fail "Project folder missing: $PROJECT_DIR"
        say "          Available: $(list_project_folders | tr '\n' ' ')"
        say "          Note: Linux file paths are case-sensitive."
        problems=$((problems + 1))
    fi

    local local_files
    local_files="$(find "$CONFIG_DIR" "$PROJECTS_DIR" -name '*.local.json' 2>/dev/null || true)"
    if [ -n "$local_files" ]; then
        say "  [NOTICE]  Machine-specific override files found:"
        printf '%s\n' "$local_files" | while IFS= read -r found; do say "            $found"; done
        say "            Normally none should exist -- see config/README.md."
    fi

    if [ -f "$APP_DLL" ]; then ok "Built DLL present"; else say "  [INFO]    Not built yet (sim-runner.sh build)"; fi

    if [ -f "$BIN_DIR/libpicogk.so" ]; then
        ok "libpicogk.so present next to DLL"
    elif [ -n "$(find "$PICOGK_BUILD_DIR" -iname '*picogk.so' 2>/dev/null | head -n 1 || true)" ]; then
        say "  [INFO]    libpicogk.so not copied yet -- build step will copy it"
    else
        fail "libpicogk.so missing in bin folder and under $PICOGK_BUILD_DIR"
        problems=$((problems + 1))
    fi

    say ""
    if [ "$problems" -eq 0 ]; then
        say "VALIDATION SUCCEEDED -- ready to start."
        return 0
    fi

    say "VALIDATION FAILED: $problems issue(s). Do NOT start the run."
    return 1
}

cmd_build() {
    load_env
    say "[RUNNER] Building application ..."
    dotnet build "$APP_PROJECT" -c Debug --nologo
    restore_picogk
    say "[RUNNER] Build finished."
}

# Runs INSIDE the tmux session. Do not invoke manually.
cmd_foreground() {
    require_project start
    load_env
    cd "$REPO_DIR"

    {
        echo "==================================================================="
        echo " Start:    $(date '+%Y-%m-%d %H:%M:%S')"
        echo " Host:     $(hostname)"
        echo " Repo:     $REPO_DIR"
        echo " Project:  $PROJECT_NAME"
        echo " Hash:     $(cat "$HASH_FILE" 2>/dev/null || echo 'unknown')"
        echo "==================================================================="
    } >>"$LOG_FILE"

    local rc=0
    # xvfb-run -a picks a free display number automatically.
    # LP_NUM_THREADS=1 prevents software OpenGL (llvmpipe) from saturating all CPU cores.
    LP_NUM_THREADS=1 xvfb-run -a dotnet "$APP_DLL" "$PROJECT_NAME" "$@" >>"$LOG_FILE" 2>&1 || rc=$?

    echo "$rc" >"$EXIT_FILE"
    echo "$PROJECT_NAME" >"$LAST_PROJECT_FILE"
    {
        echo "==================================================================="
        echo " End:      $(date '+%Y-%m-%d %H:%M:%S')  (exit code $rc)"
        echo "==================================================================="
    } >>"$LOG_FILE"

    return "$rc"
}

cmd_start() {
    require_project start
    load_env

    if is_running; then
        say "[RUNNER] A run is already active (PID $(running_pid)). Call 'stop' first."
        return 1
    fi

    if [ ! -f "$APP_DLL" ]; then
        say "[RUNNER] Not built yet -- building first."
        cmd_build
    else
        restore_picogk || true
    fi

    if has_session; then
        tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true
    fi

    if [ -s "$LOG_FILE" ]; then
        mkdir -p "$LOG_ARCHIVE"
        mv "$LOG_FILE" "$LOG_ARCHIVE/simulation_$(date '+%Y%m%d_%H%M%S').log"
    fi
    : >"$LOG_FILE"
    rm -f "$EXIT_FILE"
    echo "$PROJECT_NAME" >"$LAST_PROJECT_FILE"

    tmux new-session -d -s "$SESSION" \
        "SIM_PROJECT='$PROJECT_NAME' bash '$SCRIPT_DIR/sim-runner.sh' _foreground"

    sleep 3

    if is_running; then
        say "[RUNNER] Started in tmux session '$SESSION' (PID $(running_pid))."
        return 0
    fi

    say "[RUNNER] ERROR: Run exited immediately. Last log lines:"
    tail -n 25 "$LOG_FILE" 2>/dev/null || true
    return 1
}

cmd_resume() {
    require_project resume
    load_env

    if is_running; then
        say "[RUNNER] A run is already active (PID $(running_pid)). Call 'stop' first."
        return 1
    fi

    if [ ! -f "$APP_DLL" ]; then
        say "[RUNNER] Not built yet -- building first."
        cmd_build
    else
        restore_picogk || true
    fi

    if has_session; then
        tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true
    fi

    if [ -s "$LOG_FILE" ]; then
        mkdir -p "$LOG_ARCHIVE"
        mv "$LOG_FILE" "$LOG_ARCHIVE/simulation_$(date '+%Y%m%d_%H%M%S').log"
    fi
    : >"$LOG_FILE"
    rm -f "$EXIT_FILE"
    echo "$PROJECT_NAME" >"$LAST_PROJECT_FILE"

    tmux new-session -d -s "$SESSION" \
        "SIM_PROJECT='$PROJECT_NAME' bash '$SCRIPT_DIR/sim-runner.sh' _foreground --resume"

    sleep 3

    if is_running; then
        say "[RUNNER] Resume started in tmux session '$SESSION' (PID $(running_pid))."
        return 0
    fi

    say "[RUNNER] ERROR: Resume exited immediately. Last log lines:"
    tail -n 25 "$LOG_FILE" 2>/dev/null || true
    return 1
}

cmd_calibrate() {
    require_project calibrate
    load_env

    if is_running; then
        say "[RUNNER] A run is already active (PID $(running_pid)). Call 'stop' first."
        return 1
    fi

    if [ ! -f "$APP_DLL" ]; then
        say "[RUNNER] Not built yet -- building first."
        cmd_build
    else
        restore_picogk || true
    fi

    if has_session; then
        tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true
    fi

    if [ -s "$LOG_FILE" ]; then
        mkdir -p "$LOG_ARCHIVE"
        mv "$LOG_FILE" "$LOG_ARCHIVE/simulation_$(date '+%Y%m%d_%H%M%S').log"
    fi
    : >"$LOG_FILE"
    rm -f "$EXIT_FILE"

    tmux new-session -d -s "$SESSION" \
        "SIM_PROJECT='$PROJECT_NAME' bash '$SCRIPT_DIR/sim-runner.sh' _foreground --calibrate"

    sleep 3

    if is_running; then
        say "[RUNNER] Calibration started in tmux session '$SESSION' (PID $(running_pid))."
        return 0
    fi

    say "[RUNNER] ERROR: Calibration exited immediately. Last log lines:"
    tail -n 25 "$LOG_FILE" 2>/dev/null || true
    return 1
}

cmd_recheck() {
    require_project recheck
    load_env

    local var=""
    local finer="1"
    local unsteady=false
    local recheck_args="--recheck"

    for arg in "$@"; do
        if [ "$arg" = "--unsteady" ] || [ "$arg" = "-unsteady" ]; then
            unsteady=true
        elif [ -z "$var" ]; then
            var="$arg"
        else
            finer="$arg"
        fi
    done

    if [ -n "$var" ]; then
        recheck_args="--recheck=$var"
    fi
    if [ -n "$finer" ]; then
        recheck_args="$recheck_args --finer=$finer"
    fi
    if [ "$unsteady" = true ]; then
        recheck_args="$recheck_args --unsteady"
    fi

    if is_running; then
        say "[RUNNER] A run is already active (PID $(running_pid)). Call 'stop' first."
        return 1
    fi

    if [ ! -f "$APP_DLL" ]; then
        say "[RUNNER] Not built yet -- building first."
        cmd_build
    else
        restore_picogk || true
    fi

    if has_session; then
        tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true
    fi

    if [ -s "$LOG_FILE" ]; then
        mkdir -p "$LOG_ARCHIVE"
        mv "$LOG_FILE" "$LOG_ARCHIVE/simulation_$(date '+%Y%m%d_%H%M%S').log"
    fi
    : >"$LOG_FILE"
    rm -f "$EXIT_FILE"

    tmux new-session -d -s "$SESSION" \
        "SIM_PROJECT='$PROJECT_NAME' bash '$SCRIPT_DIR/sim-runner.sh' _foreground $recheck_args"

    sleep 3

    if is_running; then
        say "[RUNNER] Recheck started in tmux session '$SESSION' (PID $(running_pid))."
        return 0
    fi

    say "[RUNNER] ERROR: Recheck exited immediately. Last log lines:"
    tail -n 25 "$LOG_FILE" 2>/dev/null || true
    return 1
}

cmd_stop() {
    load_env

    if ! is_running && ! has_session; then
        say "[RUNNER] Nothing is running."
        return 0
    fi

    say "[RUNNER] Stopping run ..."
    tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true

    pkill -9 -u "$USER" -f "$PROCESS_PATTERN" >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f SU2_CFD          >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f simpleFoam       >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f snappyHexMesh    >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f cartesianMesh    >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f mpirun           >/dev/null 2>&1 || true
    pkill -9 -u "$USER" -f xvfb-run         >/dev/null 2>&1 || true

    sleep 1

    if is_running; then
        say "[RUNNER] WARNING: Process is still running (PID $(running_pid))."
        return 1
    fi

    say "[RUNNER] Stopped."
}

cmd_status() {
    require_project status
    load_env

    say "=== Status on $(hostname) ==="
    say "Repo:    $REPO_DIR"
    say "Project: $PROJECT_NAME"
    say "Hash:    $(cat "$HASH_FILE" 2>/dev/null || echo 'unknown')"

    if is_running; then
        local pid
        pid="$(running_pid)"
        say "Run:     RUNNING (PID $pid, elapsed $(ps -o etime= -p "$pid" 2>/dev/null | tr -d ' '))"
    else
        if [ -f "$EXIT_FILE" ]; then
            say "Run:     stopped (last exit code: $(cat "$EXIT_FILE"))"
        else
            say "Run:     stopped"
        fi
    fi

    if has_session; then say "tmux:    session '$SESSION' active"; else say "tmux:    no session '$SESSION'"; fi

    local journal="$RESULT_DIR/resume/journal.jsonl"
    if ! is_running && [ -f "$journal" ] && ! tail -n 1 "$journal" | grep -qE '^\{"(End|Ende)"'; then
        local saved
        saved="$(grep -c '^{"Index"' "$journal" || true)"
        say "Interrupted run found: ${saved:-0} evaluation(s) saved. Resume with: .\\scripts\\sim.ps1 resume -Project $PROJECT_NAME"
    fi

    say "Results: results/$PROJECT_NAME/"

    if [ -f "$RESULT_CSV" ]; then
        local records
        records=$(($(wc -l <"$RESULT_CSV") - 1))
        say "CSV:     $records record(s)"
    else
        say "CSV:     no results yet"
    fi

    if [ -f "$LOG_FILE" ]; then
        local forecast
        forecast="$( {
            grep -aE '\[(FORECAST|PROGNOSE)\]' "$LOG_FILE" | tail -n 1
            grep -aE '(Forecast: Optimization|Prognose: Optimierung)' "$LOG_FILE" | tail -n 1
            grep -aE '(Forecast: afterwards|Prognose: danach)' "$LOG_FILE" | tail -n 1
        } | sed 's/\x1b\[[0-9;]*m//g' || true)"
        if [ -n "$forecast" ]; then
            say ""
            say "--- Forecast (from log) ---"
            printf '%s\n' "$forecast"
        fi

        say ""
        say "--- Recent log lines ---"
        tail -n "${1:-12}" "$LOG_FILE" | colorize_log
    fi
}

colorize_log() {
    local awk_opts=()
    if awk -W version 2>&1 | grep -qi "mawk"; then
        awk_opts=(-W interactive)
    fi
    awk "${awk_opts[@]}" '
    BEGIN {
        ESC = sprintf("%c", 27)
        RESET = ESC "[0m"
        CYAN = ESC "[1;36m"
        BLUE = ESC "[1;34m"
        LIGHT_CYAN = ESC "[36m"
        GREEN = ESC "[1;32m"
        RED = ESC "[1;31m"
        YELLOW = ESC "[33m"
        GRAY = ESC "[90m"
    }
    {
        line = $0
        color = ""

        if (line ~ /(\[ERROR\]|\[CRITICAL ERROR\]|\[REJECTED\]|\[FEHLER\]|\[KRITISCHER FEHLER\]|\[ABGELEHNT\]|✖)/) {
            color = RED
        } else if (line ~ /(\[WARNING\]|\[WARNUNG\])/) {
            color = YELLOW
        } else if (line ~ /(★|🏆|OVERALL CHAMPION|\[RESULT\]|Champion is|\[OK\] Stable|ABSOLUTER CHAMPION|\[ERGEBNIS\]|Champion ist|\[OK\] Stabil)/) {
            color = GREEN
        } else if (line ~ /(╔|║|╚|===|ITERATION|FINAL WINNER VERIFICATION|VERIFICATION RESULT|OPTIMIZATION SUCCEEDED|FINALE GEWINNER-VERIFIKATION|VERIFIKATIONSERGEBNIS|OPTIMIERUNG ERFOLGREICH)/) {
            color = CYAN
        } else if (line ~ /(┌─ \[Iteration|--- \[Iteration|--- Variant|--- Variante)/) {
            color = BLUE
        } else if (line ~ /(▸ Genes:|-> Genes:|▸ Gene:|-> Gene:)/) {
            color = LIGHT_CYAN
        } else if (line ~ /(· Diagnostics:|· Geometry:|Freestream|Flow analysis running|\[CONFIG\]|y\+|Boundary layer|Surface mesh generated)/) {
            color = GRAY
        }

        if (color != "") {
            print color line RESET
        } else {
            print line
        }
        fflush()
    }'
}

cmd_log() {
    local lines="${1:-40}" follow="${2:-yes}"
    if [ ! -f "$LOG_FILE" ]; then
        say "[RUNNER] No log file yet ($LOG_FILE)."
        return 1
    fi

    if [ "$follow" = "yes" ]; then
        # -F follows filename across log rotations in cmd_start
        tail -n "$lines" -F "$LOG_FILE" | colorize_log
    else
        tail -n "$lines" "$LOG_FILE" | colorize_log
    fi
}

# Clean slate: removes results across ALL projects, simulation.log, logs/, .last_exit,
# and .last_project while preserving source code, bin/, obj/, .deploy_hash, and *.local.json.
cmd_delete() {
    local confirm="${1:-}"

    if is_running; then
        say "[RUNNER] A run is currently active (PID $(running_pid)). Call 'stop' first."
        return 1
    fi

    local targets=() path
    for path in "$REPO_DIR/results" "$LOG_FILE" "$LOG_ARCHIVE" "$EXIT_FILE" "$LAST_PROJECT_FILE"; do
        if [ -e "$path" ]; then targets+=("$path"); fi
    done

    if [ "${#targets[@]}" -eq 0 ]; then
        say "[RUNNER] Nothing to delete -- no run artifacts found on the server."
        return 3
    fi

    if [ "$confirm" != "yes" ]; then
        say "Would delete on $(hostname):"
        for path in "${targets[@]}"; do
            say "  ${path#"$REPO_DIR"/}  ($(du -sh "$path" 2>/dev/null | cut -f1))"
        done
        if [ -d "$REPO_DIR/results" ]; then
            say "  projects inside: $(find "$REPO_DIR/results" -mindepth 1 -maxdepth 1 -type d -printf '%f ' 2>/dev/null)"
        fi
        return 0
    fi

    rm -rf -- "${targets[@]}"

    if has_session; then
        tmux kill-session -t "$SESSION" >/dev/null 2>&1 || true
    fi

    say "[RUNNER] Deleted. The next run will start with an empty results folder and fresh log."
}

cmd_results() {
    require_project results

    if [ ! -f "$RESULT_CSV" ]; then
        say "[RUNNER] No results yet ($RESULT_CSV)."
        return 1
    fi
    head -n 1 "$RESULT_CSV"
    tail -n "${1:-10}" "$RESULT_CSV"
}

cmd_projects() {
    load_env

    say "=== Projects on $(hostname) ==="
    say "Repo: $REPO_DIR"
    say ""

    if [ -f "$APP_DLL" ]; then
        dotnet "$APP_DLL" --list-projects
    else
        say "[INFO] Not built yet. Listing folders under src/projects/:"

        local folders
        folders="$(list_project_folders)"

        if [ -n "$folders" ]; then
            printf '%s\n' "$folders" | while IFS= read -r name; do say "  $name"; done
        else
            say "  (none)"
        fi
    fi

    say ""
    say "--- Results (results/<Project>/) ---"

    local results csv
    results="$(find "$REPO_DIR/results" -mindepth 1 -maxdepth 1 -type d -printf '%f\n' 2>/dev/null | sort || true)"

    if [ -z "$results" ]; then
        say "  (none yet)"
        return 0
    fi

    printf '%s\n' "$results" | while IFS= read -r name; do
        csv="$REPO_DIR/results/$name/results.csv"
        if [ -f "$csv" ]; then
            say "  $name  ($(($(wc -l <"$csv") - 1)) record(s))"
        else
            say "  $name  (no CSV yet)"
        fi
    done
}

cmd_lastproject() {
    if [ -s "$LAST_PROJECT_FILE" ]; then
        cat "$LAST_PROJECT_FILE"
    else
        return 1
    fi
}

# Packages results of the specified project into a .tar.gz archive and prints
# the archive path as the last line on stdout.
#
# Scopes:
#   standard: results.csv, effective-config.json, run-info.json, source-snapshot/,
#             simulation.log, analysis/histories/, analysis/Bayes_History.csv,
#             calibration/, verification.csv (each if present)
#   champion: standard + champion/top-N VTU files
#   all:      entire results/<Project>/ folder plus simulation.log
cmd_pack() {
    local proj="${1:-}"
    local scope="${2:-standard}"

    if [ "$proj" = "standard" ] || [ "$proj" = "champion" ] || [ "$proj" = "all" ]; then
        if [ -n "$PROJECT_NAME" ]; then
            scope="$proj"
            proj="$PROJECT_NAME"
        fi
    fi

    if [ -n "$proj" ]; then
        PROJECT_NAME="$proj"
    fi

    require_project pack

    case "$scope" in
        standard|champion|all) ;;
        *)
            say "[RUNNER] ERROR: Unknown scope '$scope'. Allowed: standard, champion, all"
            exit 2
            ;;
    esac

    local archive_dir="${TMPDIR:-/tmp}"
    local archive="$archive_dir/sim_${PROJECT_NAME}_${scope}_$$.tar.gz"
    rm -f "$archive"

    local tar_args=(-czf "$archive")
    local have_files=0

    if [ "$scope" = "all" ]; then
        if [ -d "$RESULT_DIR" ]; then
            tar_args+=(-C "$RESULT_DIR" .)
            have_files=1
        fi
    else
        local std_items=(
            "results.csv"
            "effective-config.json"
            "run-info.json"
            "source-snapshot"
            "analysis/histories"
            "analysis/Bayes_History.csv"
            "calibration"
            "verification.csv"
        )
        local result_files=()
        for item in "${std_items[@]}"; do
            if [ -e "$RESULT_DIR/$item" ]; then
                result_files+=("$item")
            fi
        done

        if [ "$scope" = "champion" ]; then
            while IFS= read -r vtu; do
                if [ -n "$vtu" ]; then
                    result_files+=("${vtu#"$RESULT_DIR"/}")
                fi
            done < <(find "$RESULT_DIR" -type f \( -name "*Champion*.vtu" -o -name "*champion*.vtu" \) 2>/dev/null || true)

            if [ -f "$LOG_FILE" ]; then
                local champ_line
                champ_line="$(grep -E "OVERALL CHAMPION: Iteration [0-9]+ \| Variant [0-9]+" "$LOG_FILE" | tail -n 1 || true)"
                if [ -n "$champ_line" ]; then
                    local c_iter c_var
                    c_iter="$(echo "$champ_line" | sed -n 's/.*Iteration \([0-9]\+\) | Variant \([0-9]\+\).*/\1/p')"
                    c_var="$(echo "$champ_line" | sed -n 's/.*Iteration \([0-9]\+\) | Variant \([0-9]\+\).*/\2/p')"
                    if [ -n "$c_iter" ] && [ -n "$c_var" ]; then
                        for vtu in "analysis/Surface_Gen${c_iter}_Var${c_var}.vtu" \
                                   "analysis/Volume_Gen${c_iter}_Var${c_var}.vtu"; do
                            if [ -f "$RESULT_DIR/$vtu" ]; then
                                result_files+=("$vtu")
                            fi
                        done
                    fi
                fi
            fi
        fi

        if [ "${#result_files[@]}" -gt 0 ]; then
            local unique_files=()
            while IFS= read -r f; do
                [ -n "$f" ] && unique_files+=("$f")
            done < <(printf '%s\n' "${result_files[@]}" | sort -u)

            tar_args+=(-C "$RESULT_DIR" "${unique_files[@]}")
            have_files=1
        fi
    fi

    if [ -f "$LOG_FILE" ]; then
        tar_args+=(-C "$REPO_DIR" "simulation.log")
        have_files=1
    fi

    if [ "$have_files" -eq 1 ]; then
        tar "${tar_args[@]}"
    else
        tar -czf "$archive" -T /dev/null
    fi

    printf '%s\n' "$archive"
}

usage() {
    cat <<'TEXT'
Usage: bash scripts/sim-runner.sh <command> [argument]

  doctor              Check tools and paths before starting a run            [Project]
  build               dotnet build + copy libpicogk.so next to DLL
  start               Start run in a tmux session (builds if needed)         [Project]
  resume              Resume an interrupted run from journal                 [Project]
  calibrate           Start isolated mesh calibration in tmux                [Project]
  recheck [var] [fin] [--unsteady] Re-evaluate a variant on finer mesh       [Project]
  stop                Stop run, tmux session, and lingering child processes
  status [lines]      Is anything running? Runtime? Recent log lines         [Project]
  log [lines] [no]    Show log; "no" as second argument = do not follow
  results [lines]     Header and last lines of results.csv                   [Project]
  projects            List known projects and existing results on server
  lastproject         Print the name of the last run project
  pack [scope]        Pack results as tar.gz (standard|champion|all)         [Project]
  delete [yes]        Delete results of ALL projects and logs; without "yes"
                      only lists targets
  running             Exit code 0 if a run is active (for scripts)
  hash                Print hash of the last deployed code

Environment variables: SIM_SESSION (tmux name, default "sim"),
                       SIM_PROJECT (project name, NO default)

Commands marked [Project] require SIM_PROJECT. There is intentionally no silent
default so a typo cannot overwrite another project's results.

    SIM_PROJECT=MantaAuv bash scripts/sim-runner.sh status
TEXT
}

case "${1:-}" in
    doctor)      cmd_doctor ;;
    build)       cmd_build ;;
    projects)    cmd_projects ;;
    start)       cmd_start ;;
    resume)      cmd_resume ;;
    stop)        cmd_stop ;;
    status)      cmd_status "${2:-12}" ;;
    log)         cmd_log "${2:-40}" "${3:-yes}" ;;
    results)     cmd_results "${2:-10}" ;;
    delete)      cmd_delete "${2:-}" ;;
    running)     is_running ;;
    hash)        cat "$HASH_FILE" 2>/dev/null || echo "unknown" ;;
    lastproject) cmd_lastproject ;;
    pack)        cmd_pack "${2:-}" "${3:-standard}" ;;
    calibrate)   cmd_calibrate ;;
    recheck)     cmd_recheck "${@:2}" ;;
    _foreground) shift; cmd_foreground "$@" ;;
    ""|-h|--help|help) usage ;;
    *)           say "Unknown command: $1"; say ""; usage; exit 2 ;;
esac
