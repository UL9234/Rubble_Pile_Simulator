#!/usr/bin/env bash
# RubbleSim demo-render harness (isolated tooling).
#
# Builds a standalone Linux player from the simulator project so demo clips can be rendered without
# touching the Editor UI. Runs under Xvfb with Vulkan so shader compilation sees a real GPU.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$(cd "${HERE}/../.." && pwd)"
UNITY="${UNITY:-/home/chh/.local/bin/unity2022}"
BUILD_OUT="${BUILD_OUT:-${PROJ}/Builds/Linux}"
LOG_DIR="${HERE}/logs"
LOG="${LOG_DIR}/build.log"
XDISP="${XDISP:-:99}"

mkdir -p "${BUILD_OUT}" "${LOG_DIR}"

# --- make sure an X server exists (Vulkan needs a surface even for an offscreen-only demo) ---
if ! pgrep -f "Xvfb ${XDISP}" >/dev/null 2>&1; then
  Xvfb "${XDISP}" -screen 0 1920x1080x24 >"${LOG_DIR}/xvfb.log" 2>&1 &
  sleep 2
fi

echo "[build] unity    : ${UNITY}"
echo "[build] project  : ${PROJ}"
echo "[build] output   : ${BUILD_OUT}"
echo "[build] log      : ${LOG}"

set +e
DISPLAY="${XDISP}" "${UNITY}" \
  -batchmode -quit -accept-apiupdate -force-vulkan \
  -projectPath "${PROJ}" \
  -executeMethod DemoBuild.PerformBuild \
  -buildout "${BUILD_OUT}" \
  -logFile "${LOG}"
rc=$?
set -e

echo "[build] unity exit=${rc}"
grep -E "\[DemoBuild\]|error CS|Build completed|Build Failed" "${LOG}" | tail -20 || true

if [[ ! -x "${BUILD_OUT}/RubbleSim.x86_64" ]]; then
  echo "[build] FAILED: player not produced (see ${LOG})" >&2
  exit 1
fi

echo "[build] OK -> ${BUILD_OUT}/RubbleSim.x86_64"
ls -la "${BUILD_OUT}" | head -20
