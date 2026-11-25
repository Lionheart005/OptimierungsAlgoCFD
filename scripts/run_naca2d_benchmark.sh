#!/usr/bin/env bash
#
# Detached server-side benchmark runner for the 3x3 Naca2D optimizer comparison
# (Bayes, IdwBaseline, BoTorch x 3 seeds) on BIC_12.
#
# Usage:
#   bash scripts/run_naca2d_benchmark.sh --mesh-factor <val> [options]
#
# Example (detached in tmux or nohup):
#   nohup bash scripts/run_naca2d_benchmark.sh --mesh-factor 1.25 > /dev/null 2>&1 &

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"

APP_PROJECT="$REPO_DIR/src/FluidForge/FluidForge.csproj"
BIN_DIR="$REPO_DIR/src/FluidForge/bin/Debug/net9.0"
APP_DLL="$BIN_DIR/FluidForge.dll"
PICOGK_BUILD_DIR="${PICOGK_BUILD_DIR:-$HOME/software/PicoGK/build}"

PROJECT_NAME="Naca2D"
PROJECT_DIR="$REPO_DIR/src/projects/$PROJECT_NAME"
LOCAL_CONFIG="$PROJECT_DIR/simulation.local.json"
RESULT_DIR="$REPO_DIR/results/$PROJECT_NAME"
CALIBRATION_DIR="$RESULT_DIR/calibration"
BOTORCH_PYTHON="$HOME/miniconda/envs/opt-py/bin/python"
BOTORCH_WORKER="$REPO_DIR/scripts/optimizers/botorch_worker.py"
PROCESS_PATTERN="FluidForge.dll"

# Defaults (can be overridden via env vars or CLI flags)
MESH_FACTOR="${MESH_FACTOR:-}"
ALGORITHMS="${ALGORITHMS:-Bayes IdwBaseline BoTorch}"
SEEDS="${SEEDS:-20261108 424242 777}"
OUT_DIR="${OUT_DIR:-$REPO_DIR/results/Naca2D_Benchmark}"
DRY_RUN=0

MASTER_LOG=""
LOCAL_CONFIG_CREATED=0

# ---------------------------------------------------------------------------
# Environment (identical to scripts/sim-runner.sh)
# ---------------------------------------------------------------------------
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
# Logging & Cleanup
# ---------------------------------------------------------------------------
log_msg() {
    local ts
    ts="$(date '+%Y-%m-%d %H:%M:%S')"
    local line="[$ts] $*"
    if [ -n "$MASTER_LOG" ] && [ -d "$(dirname -- "$MASTER_LOG")" ]; then
        printf '%s\n' "$line" | tee -a "$MASTER_LOG"
    else
        printf '%s\n' "$line"
    fi
}

cleanup() {
    local exit_rc=$?
    if [ "$LOCAL_CONFIG_CREATED" -eq 1 ] && [ -f "$LOCAL_CONFIG" ]; then
        rm -f "$LOCAL_CONFIG"
        LOCAL_CONFIG_CREATED=0
        log_msg "[CLEANUP] Removed $LOCAL_CONFIG"
    fi
    return "$exit_rc"
}

trap cleanup EXIT INT TERM

usage() {
    cat <<EOF
Usage: $(basename -- "${BASH_SOURCE[0]}") --mesh-factor <val> [options]

Options:
  --mesh-factor <val>     Required (or env MESH_FACTOR). Fixed MeshRefinementFactor for Manual mode.
  --algorithms "<list>"   Space-separated optimizer list (default: "Bayes IdwBaseline BoTorch").
  --seeds "<list>"        Space-separated seed list (default: "20261108 424242 777").
  --out-dir <path>        Output benchmark directory (default: "\$REPO_DIR/results/Naca2D_Benchmark").
  --dry-run               Validate preconditions and config generation without running simulations.
  -h, --help              Show this help message.
EOF
}

# ---------------------------------------------------------------------------
# CLI Argument Parsing
# ---------------------------------------------------------------------------
parse_args() {
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --mesh-factor)
                [ "$#" -ge 2 ] || { printf 'ERROR: --mesh-factor requires a value.\n' >&2; exit 2; }
                MESH_FACTOR="$2"
                shift 2
                ;;
            --mesh-factor=*)
                MESH_FACTOR="${1#*=}"
                shift
                ;;
            --algorithms)
                [ "$#" -ge 2 ] || { printf 'ERROR: --algorithms requires a value.\n' >&2; exit 2; }
                ALGORITHMS="$2"
                shift 2
                ;;
            --algorithms=*)
                ALGORITHMS="${1#*=}"
                shift
                ;;
            --seeds)
                [ "$#" -ge 2 ] || { printf 'ERROR: --seeds requires a value.\n' >&2; exit 2; }
                SEEDS="$2"
                shift 2
                ;;
            --seeds=*)
                SEEDS="${1#*=}"
                shift
                ;;
            --out-dir)
                [ "$#" -ge 2 ] || { printf 'ERROR: --out-dir requires a value.\n' >&2; exit 2; }
                OUT_DIR="$2"
                shift 2
                ;;
            --out-dir=*)
                OUT_DIR="${1#*=}"
                shift
                ;;
            --dry-run)
                DRY_RUN=1
                shift
                ;;
            -h|--help)
                usage
                exit 0
                ;;
            *)
                printf 'ERROR: Unknown argument: %s\n' "$1" >&2
                usage >&2
                exit 2
                ;;
        esac
    done
}

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
restore_picogk() {
    mkdir -p "$BIN_DIR"
    if [ -f "$BIN_DIR/libpicogk.so" ] && [ -f "$BIN_DIR/libpicogk.26.2.so" ]; then
        return 0
    fi

    local lib=""
    lib="$(find "$PICOGK_BUILD_DIR" -iname '*picogk.so' 2>/dev/null | head -n 1 || true)"
    if [ -z "$lib" ]; then
        log_msg "[WARNING] No compiled PicoGK library found under $PICOGK_BUILD_DIR (usually non-fatal for 2D)."
        return 0
    fi

    cp -f "$lib" "$BIN_DIR/libpicogk.so"
    cp -f "$lib" "$BIN_DIR/libpicogk.26.2.so"
    log_msg "[PRECHECK] Refreshed libpicogk.so (source: $lib)"
}

validate_mesh_factor() {
    if [ -z "$MESH_FACTOR" ]; then
        log_msg "[ERROR] --mesh-factor (or MESH_FACTOR env var) is required so Naca2D never runs uncalibrated."
        if [ -f "$CALIBRATION_DIR/latest_calibration.csv" ]; then
            log_msg "[INFO] Found calibration file: $CALIBRATION_DIR/latest_calibration.csv"
        elif [ -d "$CALIBRATION_DIR" ] && compgen -G "$CALIBRATION_DIR/*.csv" >/dev/null; then
            log_msg "[INFO] Found calibration CSV(s) under: $CALIBRATION_DIR"
        else
            log_msg "[INFO] No calibration CSV found under $CALIBRATION_DIR. Run calibration first."
        fi
        exit 2
    fi

    if ! [[ "$MESH_FACTOR" =~ ^[0-9]+([.][0-9]+)?$ ]] || ! awk "BEGIN { exit !($MESH_FACTOR > 0) }"; then
        log_msg "[ERROR] Invalid --mesh-factor '$MESH_FACTOR' (must be a positive number)."
        exit 2
    fi
}

has_algorithm() {
    local target="$1"
    local algo
    for algo in $ALGORITHMS; do
        if [ "$algo" = "$target" ]; then
            return 0
        fi
    done
    return 1
}

write_local_config() {
    local algo="$1"
    local seed="$2"
    cat >"$LOCAL_CONFIG" <<EOF
{
  "OptimizationAlgorithm": "$algo",
  "RandomSeed": $seed,
  "AutoStop": { "Mode": "Off" },
  "MeshCalibration": { "Mode": "Manual" },
  "MeshRefinementFactor": $MESH_FACTOR,
  "ExternalOptimizer": { "FallbackToBuiltInBayes": false },
  "ChampionVerification": { "Mode": "FinalTopN", "TopN": 3, "RefinementSteps": 1 }
}
EOF
    LOCAL_CONFIG_CREATED=1
}

backup_existing_results() {
    if [ ! -d "$RESULT_DIR" ]; then
        return 0
    fi

    if [ -z "$(ls -A "$RESULT_DIR" 2>/dev/null || true)" ]; then
        return 0
    fi

    if compgen -G "$OUT_DIR/_previous_results_Naca2D_*" >/dev/null; then
        log_msg "[PRECHECK] Previous Naca2D results backup already exists in $OUT_DIR; skipping backup."
        return 0
    fi

    local ts backup_dir
    ts="$(date '+%Y%m%d_%H%M%S')"
    backup_dir="$OUT_DIR/_previous_results_Naca2D_${ts}"

    if [ "$DRY_RUN" -eq 1 ]; then
        log_msg "[DRY-RUN] Would back up existing $RESULT_DIR to $backup_dir"
        return 0
    fi

    mkdir -p "$backup_dir"
    cp -a "$RESULT_DIR/." "$backup_dir/"
    log_msg "[PRECHECK] Backed up existing $RESULT_DIR to $backup_dir"
}

archive_run_outputs() {
    local run_dir="$1"
    local algo="$2"
    local seed="$3"
    local rc="$4"
    local start_iso="$5"
    local end_iso="$6"
    local duration_s="$7"

    local item
    for item in results.csv verification.csv effective-config.json run-info.json; do
        if [ -f "$RESULT_DIR/$item" ]; then
            cp -f "$RESULT_DIR/$item" "$run_dir/$item"
        fi
    done

    if [ -d "$RESULT_DIR/source-snapshot" ]; then
        rm -rf "$run_dir/source-snapshot"
        cp -a "$RESULT_DIR/source-snapshot" "$run_dir/source-snapshot"
    fi

    if [ -f "$RESULT_DIR/analysis/Bayes_History.csv" ]; then
        mkdir -p "$run_dir/analysis"
        cp -f "$RESULT_DIR/analysis/Bayes_History.csv" "$run_dir/analysis/Bayes_History.csv"
    fi

    if [ -d "$RESULT_DIR/analysis/external_opt" ]; then
        local ext_json
        while IFS= read -r ext_json; do
            [ -n "$ext_json" ] || continue
            local rel_path="${ext_json#"$RESULT_DIR"/}"
            mkdir -p "$run_dir/$(dirname -- "$rel_path")"
            cp -f "$ext_json" "$run_dir/$rel_path"
        done < <(find "$RESULT_DIR/analysis/external_opt" -type f -name "*.json" -size -2048k 2>/dev/null || true)
    fi

    local csv_lines=0
    if [ -f "$run_dir/results.csv" ]; then
        csv_lines="$(wc -l < "$run_dir/results.csv" | tr -d '[:space:]')"
    fi

    cat >"$run_dir/run_summary.json" <<EOF
{
  "algorithm": "$algo",
  "seed": $seed,
  "mesh_factor": $MESH_FACTOR,
  "exit_code": $rc,
  "start_time": "$start_iso",
  "end_time": "$end_iso",
  "duration_seconds": $duration_s,
  "results_csv_lines": $csv_lines
}
EOF
}

# ---------------------------------------------------------------------------
# Preconditions
# ---------------------------------------------------------------------------
check_preconditions() {
    if [ ! -d "$PROJECT_DIR" ]; then
        log_msg "[ERROR] Project directory not found: $PROJECT_DIR"
        exit 1
    fi

    if pgrep -u "$USER" -f "$PROCESS_PATTERN" >/dev/null 2>&1; then
        local running_pids
        running_pids="$(pgrep -u "$USER" -f "$PROCESS_PATTERN" | tr '\n' ' ')"
        log_msg "[ERROR] Another FluidForge.dll process is already running (PID(s): $running_pids). Aborting."
        exit 1
    fi

    if has_algorithm "BoTorch"; then
        if [ ! -x "$BOTORCH_PYTHON" ]; then
            log_msg "[ERROR] BoTorch Python interpreter not found or not executable: $BOTORCH_PYTHON"
            exit 1
        fi
        if [ ! -f "$BOTORCH_WORKER" ]; then
            log_msg "[ERROR] BoTorch worker script not found: $BOTORCH_WORKER"
            exit 1
        fi
        log_msg "[PRECHECK] Running BoTorch worker check..."
        if ! "$BOTORCH_PYTHON" "$BOTORCH_WORKER" --check; then
            log_msg "[ERROR] BoTorch pre-check failed. Aborting."
            exit 1
        fi
        log_msg "[PRECHECK] BoTorch check passed."
    fi

    if [ ! -f "$APP_DLL" ]; then
        if [ "$DRY_RUN" -eq 1 ]; then
            log_msg "[DRY-RUN] $APP_DLL not found; would run: dotnet build \"$APP_PROJECT\" -c Debug --nologo"
        else
            log_msg "[PRECHECK] $APP_DLL not found; building FluidForge..."
            dotnet build "$APP_PROJECT" -c Debug --nologo
            restore_picogk
        fi
    else
        log_msg "[PRECHECK] Found $APP_DLL"
        if [ "$DRY_RUN" -eq 0 ]; then
            restore_picogk
        fi
    fi

    backup_existing_results
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
main() {
    parse_args "$@"
    load_env

    if [ "$DRY_RUN" -eq 0 ]; then
        mkdir -p "$OUT_DIR"
        MASTER_LOG="$OUT_DIR/benchmark.log"
    fi

    log_msg "=== Naca2D Benchmark Runner ==="
    log_msg "Repo:       $REPO_DIR"
    log_msg "OutDir:     $OUT_DIR"
    log_msg "Algorithms: $ALGORITHMS"
    log_msg "Seeds:      $SEEDS"
    log_msg "MeshFactor: ${MESH_FACTOR:-<unset>}"
    log_msg "DryRun:     $DRY_RUN"

    validate_mesh_factor
    check_preconditions

    local total=0 completed=0 skipped=0 failed=0
    local algo seed run_id run_dir run_log

    for algo in $ALGORITHMS; do
        for seed in $SEEDS; do
            total=$((total + 1))
            run_id="${algo}_seed${seed}"
            run_dir="$OUT_DIR/$run_id"
            run_log="$run_dir/simulation.log"

            if [ -f "$run_dir/DONE" ]; then
                log_msg "[SKIP] $run_id (DONE marker present)"
                skipped=$((skipped + 1))
                continue
            fi

            if [ "$DRY_RUN" -eq 1 ]; then
                log_msg "[DRY-RUN] Would run $run_id -> $run_dir"
                log_msg "[DRY-RUN] Config for $LOCAL_CONFIG: algo=$algo seed=$seed mesh_factor=$MESH_FACTOR"
                continue
            fi

            mkdir -p "$run_dir"
            rm -f "$run_dir/FAILED"

            log_msg "[START] $run_id"
            write_local_config "$algo" "$seed"

            rm -rf "$RESULT_DIR"
            mkdir -p "$RESULT_DIR"

            local start_epoch end_epoch duration_s start_iso end_iso rc
            start_epoch="$(date +%s)"
            start_iso="$(date -Iseconds)"
            rc=0

            (
                cd "$REPO_DIR"
                LP_NUM_THREADS=1 xvfb-run -a dotnet "$APP_DLL" "$PROJECT_NAME" >"$run_log" 2>&1
            ) || rc=$?

            end_epoch="$(date +%s)"
            end_iso="$(date -Iseconds)"
            duration_s=$((end_epoch - start_epoch))

            if [ -f "$LOCAL_CONFIG" ]; then
                rm -f "$LOCAL_CONFIG"
                LOCAL_CONFIG_CREATED=0
            fi

            archive_run_outputs "$run_dir" "$algo" "$seed" "$rc" "$start_iso" "$end_iso" "$duration_s"

            if [ "$rc" -eq 0 ] && [ -f "$run_dir/results.csv" ]; then
                printf 'ok rc=%s duration=%ss\n' "$rc" "$duration_s" >"$run_dir/DONE"
                completed=$((completed + 1))
                log_msg "[DONE] $run_id (rc=$rc, duration=${duration_s}s)"
            else
                printf 'failed rc=%s duration=%ss\n' "$rc" "$duration_s" >"$run_dir/FAILED"
                failed=$((failed + 1))
                log_msg "[FAILED] $run_id (rc=$rc, duration=${duration_s}s, results.csv=$( [ -f "$run_dir/results.csv" ] && echo present || echo missing ))"
            fi
        done
    done

    log_msg "=== Summary: total=$total completed=$completed skipped=$skipped failed=$failed ==="
    if [ "$failed" -gt 0 ]; then
        return 1
    fi
    return 0
}

main "$@"
