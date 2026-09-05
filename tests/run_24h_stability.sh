#!/usr/bin/env bash
# ==============================================================================
# S02 24-Hour Continuous Long-Run Stability & Soak Test Harness
# Conforming to LIBARIA2_TEST_PLAN.md §3.4, §4 (S02) & LIBARIA2_ARCHITECTURE_PLAN.md
# ==============================================================================
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "${SCRIPT_DIR}/.." && pwd)"
RESULTS_DIR="${REPO_ROOT}/tests/results"
mkdir -p "${RESULTS_DIR}"

LOG_FILE="${RESULTS_DIR}/S02_24H_STABILITY.log"
DURATION_HOURS="${1:-24}"
DURATION_SECS=$(( DURATION_HOURS * 3600 ))

echo "=========================================================="
echo " Starting S02 Long-Run Stability Harness (${DURATION_HOURS} Hours / ${DURATION_SECS} Seconds)"
echo " Log file: ${LOG_FILE}"
echo " Start:    $(date -u +"%Y-%m-%d %H:%M:%SZ")"
echo "=========================================================="

{
    echo "# S02 Stability & Resource Sampling Log"
    echo "# Target Duration: ${DURATION_HOURS} Hours (${DURATION_SECS} Seconds)"
    echo "# Started: $(date -u +"%Y-%m-%d %H:%M:%SZ")"
    echo "# Columns: Timestamp, ElapsedSec, RSS_MiB, Threads, OpenFDs, TargetStatus"
} > "${LOG_FILE}"

# Run S02 with high cycle target tailored for target duration
# 1 cycle takes ~120ms with 5 add/pause/resume/remove operations
CYCLES_PER_HOUR=25000
TOTAL_CYCLES=$(( DURATION_HOURS * CYCLES_PER_HOUR ))

export S02_CYCLES="${TOTAL_CYCLES}"
export S02_DURATION_HOURS="${DURATION_HOURS}"

START_TIME=$(date +%s)
END_TIME=$(( START_TIME + DURATION_SECS ))

echo "Executing test runner with S02_CYCLES=${TOTAL_CYCLES}..."
dotnet run --project "${REPO_ROOT}/tests/AriaUI.Tests/AriaUI.Tests.csproj" -- S02 2>&1 | tee -a "${LOG_FILE}"

echo "=========================================================="
echo " S02 Long-Run Stability Completed Successfully."
echo " End: $(date -u +"%Y-%m-%d %H:%M:%SZ")"
echo "=========================================================="
