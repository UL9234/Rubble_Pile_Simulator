#!/usr/bin/env bash
# RubbleSim demo-render harness — expose the render-only sources to Unity.
#
# Unity only compiles C# under Assets/, so this links (or copies) tools/demo_render into
# Assets/DemoRender. The harness contains render tooling only; all simulation/generation logic lives
# in the project itself (Assets/MITLL/...).
#
#   ./sync_scripts.sh symlink   # default: Assets/DemoRender -> ../tools/demo_render
#   ./sync_scripts.sh copy      # fallback: copy the .cs files (generated, not tracked)
#   ./sync_scripts.sh clean     # remove whatever we exposed
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${HERE}/.." && pwd)"
PROJ="$(cd "${HERE}/../../.." && pwd)"
DEST="${PROJ}/Assets/DemoRender"
MODE="${1:-symlink}"

case "${MODE}" in
  symlink)
    rm -rf "${DEST}" "${DEST}.meta"
    ln -s "../tools/demo_render" "${DEST}"
    echo "[sync] symlinked ${DEST} -> ../tools/demo_render"
    ;;
  copy)
    rm -rf "${DEST}" "${DEST}.meta"
    mkdir -p "${DEST}/runtime" "${DEST}/Editor"
    cp "${ROOT}/runtime"/*.cs "${DEST}/runtime/"
    cp "${ROOT}/Editor"/*.cs "${DEST}/Editor/"
    echo "[sync] copied harness sources into ${DEST}"
    ;;
  clean)
    rm -rf "${DEST}" "${DEST}.meta"
    echo "[sync] removed ${DEST}"
    ;;
  *)
    echo "usage: $0 [symlink|copy|clean]" >&2
    exit 2
    ;;
esac

find "${DEST}" -name "*.cs" | sort
