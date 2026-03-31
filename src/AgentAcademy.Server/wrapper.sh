#!/usr/bin/env bash
# ═══════════════════════════════════════════════════════════════════
# Agent Academy — Supervised Process Wrapper
#
# Runs the .NET server and interprets exit codes:
#   0   → Clean shutdown  → stop
#   75  → Restart request → restart immediately
#   1+  → Crash/error     → restart with exponential backoff
#
# Usage:
#   ./wrapper.sh                           # uses default DLL path
#   ./wrapper.sh --urls http://0.0.0.0:5000
#
# Environment variables:
#   AA_DLL_PATH   — path to AgentAcademy.Server.dll (default: auto-detect)
#   AA_MAX_CRASH  — max crash restarts before giving up (default: 5)
#   AA_HEALTH_SEC — seconds to wait before considering startup healthy (default: 60)
# ═══════════════════════════════════════════════════════════════════
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
LOG_PREFIX="[wrapper]"

# Configurable
MAX_CRASH_RESTARTS="${AA_MAX_CRASH:-5}"
HEALTHY_AFTER_SEC="${AA_HEALTH_SEC:-60}"

# Exit code constants
EXIT_CLEAN=0
EXIT_RESTART=75

# State
crash_count=0

log() { echo "$LOG_PREFIX $(date '+%Y-%m-%d %H:%M:%S') $*"; }

find_dll() {
    if [[ -n "${AA_DLL_PATH:-}" ]]; then
        echo "$AA_DLL_PATH"
        return
    fi

    # Try common locations relative to the script
    local candidates=(
        "$SCRIPT_DIR/bin/Debug/net8.0/AgentAcademy.Server.dll"
        "$SCRIPT_DIR/bin/Release/net8.0/AgentAcademy.Server.dll"
        "$SCRIPT_DIR/AgentAcademy.Server.dll"
    )

    for candidate in "${candidates[@]}"; do
        if [[ -f "$candidate" ]]; then
            echo "$candidate"
            return
        fi
    done

    echo ""
}

DLL_PATH="$(find_dll)"

if [[ -z "$DLL_PATH" ]]; then
    log "ERROR: Cannot find AgentAcademy.Server.dll"
    log "Set AA_DLL_PATH or run from the server project directory."
    exit 1
fi

log "DLL path: $DLL_PATH"
log "Max crash restarts: $MAX_CRASH_RESTARTS"
log "Healthy-after threshold: ${HEALTHY_AFTER_SEC}s"

while true; do
    log "Starting server (crash count: $crash_count)..."
    start_time=$(date +%s)

    set +e
    dotnet "$DLL_PATH" "$@"
    exit_code=$?
    set -e

    elapsed=$(( $(date +%s) - start_time ))
    log "Server exited with code $exit_code (ran for ${elapsed}s)"

    case $exit_code in
        $EXIT_CLEAN)
            log "Clean shutdown — exiting wrapper."
            exit 0
            ;;

        $EXIT_RESTART)
            log "Restart requested (exit code 75) — restarting immediately."
            crash_count=0  # Intentional restart resets crash counter
            continue
            ;;

        *)
            crash_count=$((crash_count + 1))

            # If the process ran long enough, it's a new crash — reset counter
            if (( elapsed >= HEALTHY_AFTER_SEC )); then
                log "Process ran for ${elapsed}s (≥ ${HEALTHY_AFTER_SEC}s) — treating as new crash, resetting counter."
                crash_count=1
            fi

            if (( crash_count > MAX_CRASH_RESTARTS )); then
                log "ERROR: Exceeded max crash restarts ($MAX_CRASH_RESTARTS). Giving up."
                exit $exit_code
            fi

            # Exponential backoff: 2^(n-1) seconds, capped at 32s
            backoff=$(( 1 << (crash_count - 1) ))
            if (( backoff > 32 )); then
                backoff=32
            fi

            log "Crash restart $crash_count/$MAX_CRASH_RESTARTS — waiting ${backoff}s before restart..."
            sleep "$backoff"
            ;;
    esac
done
