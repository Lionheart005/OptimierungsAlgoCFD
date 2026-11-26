#!/usr/bin/env bash
#
# Sequential server-side benchmark chain runner for BIC_12 (tmux):
#   Runs FluidForge benchmarks sequentially via scripts/sim-runner.sh _foreground,
#   rotates/archives simulation.log before each project, copies each project's
#   simulation.log to results/<Project>/simulation_server.log, and writes
#   results/PHYSICS_BENCHMARKS_DONE (or PHYSICS_BENCHMARKS_FAILED on signal abort).
#
# Usage (detached in tmux on BIC_12):
#   bash scripts/run_physics_benchmarks_on_server.sh [Project1 Project2 ...]

set -euo pipefail

SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd -- "$SCRIPT_DIR/.." && pwd)"

RESULTS_DIR="$REPO_DIR/results"
LOG_FILE="$REPO_DIR/simulation.log"
LOG_ARCHIVE="$REPO_DIR/logs"
STATUS_LOG="$RESULTS_DIR/physics_benchmarks.log"
DONE_FILE="$RESULTS_DIR/PHYSICS_BENCHMARKS_DONE"
FAILED_FILE="$RESULTS_DIR/PHYSICS_BENCHMARKS_FAILED"

DEFAULT_PROJECTS=(
    "BenchmarkNaca0012"
    "BenchmarkPipeBend90"
    "BenchmarkSuboffOpenFoam"
    "BenchmarkSuboffSu2"
)

usage() {
    cat <<EOF
Usage: $(basename -- "${BASH_SOURCE[0]}") [Project ...]

Runs FluidForge benchmark projects sequentially via scripts/sim-runner.sh _foreground.
If no projects are specified, runs:
  ${DEFAULT_PROJECTS[*]}

Options:
  -h, --help    Show this help message.
EOF
}

log_msg() {
    local ts line
    ts="$(date '+%Y-%m-%d %H:%M:%S')"
    line="[$ts] $*"
    mkdir -p "$(dirname -- "$STATUS_LOG")"
    printf '%s\n' "$line" | tee -a "$STATUS_LOG"
}

write_status_file() {
    local target_file="$1"
    local overall_status="$2"
    local ts
    ts="$(date -Iseconds)"

    {
        printf 'status=%s\n' "$overall_status"
        printf 'timestamp=%s\n' "$ts"
        printf 'total_projects=%s\n' "${#PROJECTS[@]}"
        printf 'completed_projects=%s\n' "${#SUMMARY_ENTRIES[@]}"
        for entry in "${SUMMARY_ENTRIES[@]}"; do
            printf '%s\n' "$entry"
        done
    } >"$target_file"
}

rotate_simulation_log() {
    if [ -s "$LOG_FILE" ]; then
        mkdir -p "$LOG_ARCHIVE"
        mv -f "$LOG_FILE" "$LOG_ARCHIVE/simulation_$(date '+%Y%m%d_%H%M%S').log"
    fi
    : >"$LOG_FILE"
}

main() {
    if [ "$#" -gt 0 ]; then
        case "$1" in
            -h|--help)
                usage
                exit 0
                ;;
        esac
        PROJECTS=("$@")
    else
        PROJECTS=("${DEFAULT_PROJECTS[@]}")
    fi

    mkdir -p "$RESULTS_DIR"
    rm -f "$DONE_FILE" "$FAILED_FILE"

    SUMMARY_ENTRIES=()
    local overall_rc=0

    log_msg "=== Physics Benchmarks Sequential Runner ==="
    log_msg "Repo:     $REPO_DIR"
    log_msg "Projects: ${PROJECTS[*]}"

    for p in "${PROJECTS[@]}"; do
        rotate_simulation_log

        local start_ts end_ts dur rc
        start_ts="$(date +%s)"
        log_msg "[START] $p"

        rc=0
        SIM_PROJECT="$p" bash "$REPO_DIR/scripts/sim-runner.sh" _foreground || rc=$?

        if [ -f "$LOG_FILE" ]; then
            mkdir -p "$RESULTS_DIR/$p"
            cp -f "$LOG_FILE" "$RESULTS_DIR/$p/simulation_server.log"
        fi

        end_ts="$(date +%s)"
        dur=$((end_ts - start_ts))
        log_msg "[DONE] $p (rc=$rc, duration=${dur}s)"
        SUMMARY_ENTRIES+=("project_${p}=rc:${rc},duration:${dur}s")

        if [ "$rc" -ge 128 ]; then
            log_msg "[ABORT] Signal received (rc=$rc) during $p"
            write_status_file "$FAILED_FILE" "ABORTED"
            exit "$rc"
        elif [ "$rc" -ne 0 ]; then
            overall_rc=1
            log_msg "[WARN] $p failed with rc=$rc — continuing with remaining benchmarks"
        fi
    done

    local final_status="DONE"
    if [ "$overall_rc" -ne 0 ]; then
        final_status="DONE_WITH_FAILURES"
    fi

    write_status_file "$DONE_FILE" "$final_status"
    log_msg "=== Summary ($final_status, exit_code=$overall_rc) ==="
    for entry in "${SUMMARY_ENTRIES[@]}"; do
        log_msg "  $entry"
    done

    exit "$overall_rc"
}

main "$@"
