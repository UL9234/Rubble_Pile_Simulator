#!/usr/bin/env bash
# RubbleSim demo-render harness (isolated tooling).
#
# Unity only compiles C# that lives under Assets/, so this script exposes the harness (whose source of
# truth is tools/demo_render/) to the project. Two modes:
#
#   ./sync_scripts.sh symlink   # default: Assets/DemoRender -> ../tools/demo_render (single source of truth)
#   ./sync_scripts.sh copy      # fallback: copy the .cs files into Assets/DemoRender (generated, gitignored)
#   ./sync_scripts.sh clean     # remove whatever we exposed
#
# Nothing outside Assets/DemoRender/ is ever touched, so the simulator's own assets stay pristine.
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$(cd "${HERE}/../.." && pwd)"
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
    mkdir -p "${DEST}/Editor"
    cp "${HERE}"/*.cs "${DEST}/"
    cp "${HERE}/Editor"/*.cs "${DEST}/Editor/"
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

find "${DEST}" -maxdepth 2 -name "*.cs" | sort
