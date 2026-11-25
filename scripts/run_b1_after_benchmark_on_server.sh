#!/usr/bin/env bash
#
# Detached server-side chaining script for BIC_12 (tmux):
#   1. Waits for the currently running BenchmarkNaca0012 (FluidForge.dll) to finish.
#   2. Backs up existing results/Naca2D, clears it, builds FluidForge if needed,
#      and runs `Naca2D --calibrate`.
#   3. Reads SelectedFactor from results/Naca2D/calibration/latest_calibration.json
#      (using utf-8-sig for UTF-8 BOM safety) and archives calibration docs.
#   4. Runs scripts/run_naca2d_benchmark.sh --mesh-factor "$SELECTED_FACTOR" across
#      all 9 runs and writes ALL_B1_DONE or ALL_B1_FAILED.
#
# Usage (detached in tmux on BIC_12):
#   bash scripts/run_b1_after_benchmark_on_server.sh [options]

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"

APP_PROJECT="$REPO_DIR/src/FluidForge/FluidForge.csproj"
BIN_DIR="$REPO_DIR/src/FluidForge/bin/Debug/net9.0"
APP_DLL="$BIN_DIR/FluidForge.dll"
PICOGK_BUILD_DIR="${PICOGK_BUILD_DIR:-$HOME/software/PicoGK/build}"

PROCESS_PATTERN="FluidForge.dll"
BENCHMARK_0012_DIR="$REPO_DIR/results/BenchmarkNaca0012"
NACA2D_RESULT_DIR="$REPO_DIR/results/Naca2D"
NACA2D_CALIBRATION_DIR="$NACA2D_RESULT_DIR/calibration"
NACA2D_CALIBRATION_JSON="$NACA2D_CALIBRATION_DIR/latest_calibration.json"

POLL_SECONDS="${POLL_SECONDS:-30}"
OUT_DIR="${OUT_DIR:-$REPO_DIR/results/Naca2D_Benchmark}"
CHAIN_LOG=""

# ---------------------------------------------------------------------------
# Environment (identical to scripts/run_naca2d_benchmark.sh)
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
# Logging & Failure Helpers
# ---------------------------------------------------------------------------
log_msg() {
    local ts
    ts="$(date '+%Y-%m-%d %H:%M:%S')"
    local line="[$ts] $*"
    if [ -n "$CHAIN_LOG" ] && [ -d "$(dirname -- "$CHAIN_LOG")" ]; then
        printf '%s\n' "$line" | tee -a "$CHAIN_LOG"
    else
        printf '%s\n' "$line"
    fi
}

fail_chain() {
    local rc="$1"
    local reason="$2"
    local ts
    ts="$(date -Iseconds)"
    log_msg "[FAILED] $reason (rc=$rc)"
    mkdir -p "$OUT_DIR"
    {
        printf 'status=FAILED\n'
        printf 'timestamp=%s\n' "$ts"
        printf 'exit_code=%s\n' "$rc"
        printf 'reason=%s\n' "$reason"
    } >"$OUT_DIR/ALL_B1_FAILED"
    exit "$rc"
}

usage() {
    cat <<EOF
Usage: $(basename -- "${BASH_SOURCE[0]}") [options]

Options:
  --poll-seconds <n>   Polling interval in seconds while waiting for FluidForge.dll (default: 30).
  --out-dir <path>     Output benchmark directory (default: "\$REPO_DIR/results/Naca2D_Benchmark").
  -h, --help           Show this help message.
EOF
}

parse_args() {
    while [ "$#" -gt 0 ]; do
        case "$1" in
            --poll-seconds)
                [ "$#" -ge 2 ] || { printf 'ERROR: --poll-seconds requires a value.\n' >&2; exit 2; }
                POLL_SECONDS="$2"
                shift 2
                ;;
            --poll-seconds=*)
                POLL_SECONDS="${1#*=}"
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

    if ! [[ "$POLL_SECONDS" =~ ^[0-9]+$ ]] || [ "$POLL_SECONDS" -le 0 ]; then
        printf 'ERROR: Invalid --poll-seconds "%s" (must be a positive integer).\n' "$POLL_SECONDS" >&2
        exit 2
    fi
}

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

ensure_app_built() {
    if [ ! -f "$APP_DLL" ]; then
        log_msg "[BUILD] $APP_DLL not found; building FluidForge..."
        dotnet build "$APP_PROJECT" -c Debug --nologo
    else
        log_msg "[PRECHECK] Found $APP_DLL"
    fi
    restore_picogk
}

# ---------------------------------------------------------------------------
# Step 1: Wait for running BenchmarkNaca0012 (FluidForge.dll)
# ---------------------------------------------------------------------------
wait_for_benchmark_naca0012() {
    local start_wait now last_heartbeat pids
    start_wait="$(date +%s)"
    last_heartbeat="$start_wait"

    if pgrep -u "$USER" -f "$PROCESS_PATTERN" >/dev/null 2>&1; then
        pids="$(pgrep -u "$USER" -f "$PROCESS_PATTERN" | tr '\n' ' ' | sed 's/[[:space:]]*$//')"
        log_msg "[STEP 1] Waiting for active $PROCESS_PATTERN (PID(s): $pids) with poll=${POLL_SECONDS}s..."
        while pgrep -u "$USER" -f "$PROCESS_PATTERN" >/dev/null 2>&1; do
            sleep "$POLL_SECONDS"
            now="$(date +%s)"
            if [ $((now - last_heartbeat)) -ge 600 ]; then
                pids="$(pgrep -u "$USER" -f "$PROCESS_PATTERN" | tr '\n' ' ' | sed 's/[[:space:]]*$//' || true)"
                log_msg "[WAIT] Still waiting for $PROCESS_PATTERN (elapsed=$((now - start_wait))s, PID(s): ${pids:-none})"
                last_heartbeat="$now"
            fi
        done
        now="$(date +%s)"
        log_msg "[STEP 1] $PROCESS_PATTERN finished after $((now - start_wait))s."
    else
        log_msg "[STEP 1] No active $PROCESS_PATTERN process found; proceeding immediately."
    fi

    local exit_file found_exit=0
    for exit_file in "$REPO_DIR/.last_exit" "$BENCHMARK_0012_DIR/.last_exit"; do
        if [ -f "$exit_file" ]; then
            found_exit=1
            log_msg "[STEP 1] $exit_file: $(tr '\n' ' ' < "$exit_file" | sed 's/[[:space:]]*$//')"
        fi
    done
    if [ "$found_exit" -eq 0 ]; then
        log_msg "[STEP 1] No .last_exit file found in $REPO_DIR or $BENCHMARK_0012_DIR."
    fi

    if [ -f "$BENCHMARK_0012_DIR/results.csv" ]; then
        log_msg "[STEP 1] BenchmarkNaca0012 results.csv: present ($(wc -l < "$BENCHMARK_0012_DIR/results.csv" | tr -d '[:space:]') lines)"
    else
        log_msg "[STEP 1] BenchmarkNaca0012 results.csv: missing"
    fi

    if [ -f "$BENCHMARK_0012_DIR/calibration/latest_calibration.json" ]; then
        log_msg "[STEP 1] BenchmarkNaca0012 calibration/latest_calibration.json: present"
    else
        log_msg "[STEP 1] BenchmarkNaca0012 calibration/latest_calibration.json: missing"
    fi

    if [ -f "$REPO_DIR/simulation.log" ] && [ -d "$BENCHMARK_0012_DIR" ]; then
        cp -f "$REPO_DIR/simulation.log" "$BENCHMARK_0012_DIR/simulation_server.log"
        log_msg "[STEP 1] Archived $REPO_DIR/simulation.log to $BENCHMARK_0012_DIR/simulation_server.log"
    fi
}

# ---------------------------------------------------------------------------
# Step 2: Backup results/Naca2D, clean, build if needed, run Naca2D --calibrate
# ---------------------------------------------------------------------------
backup_and_calibrate_naca2d() {
    log_msg "[STEP 2] Preparing results/Naca2D and running Naca2D --calibrate..."

    if [ -d "$NACA2D_RESULT_DIR" ] && [ -n "$(ls -A "$NACA2D_RESULT_DIR" 2>/dev/null || true)" ]; then
        if compgen -G "$OUT_DIR/_previous_results_Naca2D_*" >/dev/null; then
            log_msg "[STEP 2] Existing _previous_results_Naca2D_* backup already present in $OUT_DIR; skipping backup."
        else
            local ts backup_dir
            ts="$(date '+%Y%m%d_%H%M%S')"
            backup_dir="$OUT_DIR/_previous_results_Naca2D_${ts}"
            mkdir -p "$backup_dir"
            cp -a "$NACA2D_RESULT_DIR/." "$backup_dir/"
            log_msg "[STEP 2] Backed up $NACA2D_RESULT_DIR to $backup_dir"
        fi
    else
        log_msg "[STEP 2] No non-empty $NACA2D_RESULT_DIR to back up."
    fi

    rm -rf "$NACA2D_RESULT_DIR"
    mkdir -p "$NACA2D_RESULT_DIR"
    log_msg "[STEP 2] Cleared and recreated $NACA2D_RESULT_DIR"

    ensure_app_built

    local cal_log="$OUT_DIR/calibrate_naca2d.log"
    local rc=0
    log_msg "[STEP 2] Running: LP_NUM_THREADS=1 xvfb-run -a dotnet \"$APP_DLL\" Naca2D --calibrate (log: $cal_log)"
    (
        cd "$REPO_DIR"
        LP_NUM_THREADS=1 xvfb-run -a dotnet "$APP_DLL" Naca2D --calibrate >"$cal_log" 2>&1
    ) || rc=$?

    if [ "$rc" -ne 0 ]; then
        fail_chain 1 "Naca2D --calibrate failed (see $cal_log)"
    fi
    log_msg "[STEP 2] Naca2D --calibrate completed successfully (rc=0)."
}

# ---------------------------------------------------------------------------
# Step 3: Parse SelectedFactor (utf-8-sig) and archive calibration folder
# ---------------------------------------------------------------------------
read_selected_factor() {
    log_msg "[STEP 3] Reading SelectedFactor from $NACA2D_CALIBRATION_JSON..."

    if [ ! -f "$NACA2D_CALIBRATION_JSON" ]; then
        fail_chain 1 "Calibration file not found: $NACA2D_CALIBRATION_JSON"
    fi

    local py_bin=""
    if command -v python3 >/dev/null 2>&1; then
        py_bin="python3"
    elif command -v python >/dev/null 2>&1; then
        py_bin="python"
    else
        fail_chain 1 "Neither python3 nor python found on PATH to parse $NACA2D_CALIBRATION_JSON"
    fi

    local factor=""
    local py_rc=0
    factor="$("$py_bin" - "$NACA2D_CALIBRATION_JSON" <<'PY'
import json
import sys

path = sys.argv[1]
with open(path, "r", encoding="utf-8-sig") as f:
    data = json.load(f)

if data.get("Success") is not True:
    sys.stderr.write(f"[WARNING] Calibration Success={data.get('Success')!r}, using fallback SelectedFactor.\n")

factor = data.get("SelectedFactor")
if not isinstance(factor, (int, float)) or isinstance(factor, bool) or factor <= 0:
    sys.stderr.write(f"Invalid SelectedFactor: {factor!r}\n")
    sys.exit(1)

print(factor)
PY
    )" || py_rc=$?

    if [ "$py_rc" -ne 0 ] || [ -z "$factor" ]; then
        fail_chain 1 "Failed to parse valid SelectedFactor from $NACA2D_CALIBRATION_JSON"
    fi

    SELECTED_FACTOR="$factor"
    log_msg "[STEP 3] Calibrated SelectedFactor=$SELECTED_FACTOR"

    if [ -d "$NACA2D_CALIBRATION_DIR" ]; then
        rm -rf "$OUT_DIR/_calibrated_naca2d"
        cp -a "$NACA2D_CALIBRATION_DIR" "$OUT_DIR/_calibrated_naca2d"
        log_msg "[STEP 3] Copied $NACA2D_CALIBRATION_DIR to $OUT_DIR/_calibrated_naca2d"
    else
        fail_chain 1 "Calibration directory missing: $NACA2D_CALIBRATION_DIR"
    fi
}

# ---------------------------------------------------------------------------
# Step 4: Run scripts/run_naca2d_benchmark.sh across all 9 runs
# ---------------------------------------------------------------------------
run_benchmark_suite() {
    local bench_script="$SCRIPT_DIR/run_naca2d_benchmark.sh"
    if [ ! -f "$bench_script" ]; then
        fail_chain 1 "Benchmark script not found: $bench_script"
    fi

    log_msg "[STEP 4] Starting $bench_script --mesh-factor $SELECTED_FACTOR --out-dir $OUT_DIR"
    local rc=0
    bash "$bench_script" --mesh-factor "$SELECTED_FACTOR" --out-dir "$OUT_DIR" || rc=$?

    if [ "$rc" -eq 0 ]; then
        local ts summary_line done_count failed_count
        ts="$(date -Iseconds)"
        done_count="$(find "$OUT_DIR" -mindepth 2 -maxdepth 2 -name "DONE" 2>/dev/null | wc -l | tr -d '[:space:]')"
        failed_count="$(find "$OUT_DIR" -mindepth 2 -maxdepth 2 -name "FAILED" 2>/dev/null | wc -l | tr -d '[:space:]')"
        summary_line=""
        if [ -f "$OUT_DIR/benchmark.log" ]; then
            summary_line="$(grep -E '=== Summary:' "$OUT_DIR/benchmark.log" | tail -n 1 || true)"
        fi
        {
            printf 'status=DONE\n'
            printf 'timestamp=%s\n' "$ts"
            printf 'selected_factor=%s\n' "$SELECTED_FACTOR"
            printf 'done_runs=%s\n' "$done_count"
            printf 'failed_runs=%s\n' "$failed_count"
            if [ -n "$summary_line" ]; then
                printf 'summary=%s\n' "$summary_line"
            fi
        } >"$OUT_DIR/ALL_B1_DONE"
        log_msg "[STEP 4] All B1 benchmark runs completed successfully. Wrote $OUT_DIR/ALL_B1_DONE"
    else
        fail_chain "$rc" "run_naca2d_benchmark.sh exited with rc=$rc"
    fi
}

# ---------------------------------------------------------------------------
# Main
# ---------------------------------------------------------------------------
main() {
    parse_args "$@"
    load_env

    mkdir -p "$OUT_DIR"
    rm -f "$OUT_DIR/ALL_B1_DONE" "$OUT_DIR/ALL_B1_FAILED"
    CHAIN_LOG="$OUT_DIR/b1_chain.log"

    log_msg "=== B1 Chain Runner ==="
    log_msg "Repo:        $REPO_DIR"
    log_msg "OutDir:      $OUT_DIR"
    log_msg "PollSeconds: $POLL_SECONDS"

    wait_for_benchmark_naca0012
    backup_and_calibrate_naca2d

    SELECTED_FACTOR=""
    read_selected_factor
    run_benchmark_suite
}

main "$@"
