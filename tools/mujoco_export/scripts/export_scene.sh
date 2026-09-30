#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MUJOCO_PYTHON="${MUJOCO_PYTHON:-/data1/chh/dependency/miniconda3/envs/mujoco_sim/bin/python}"
if [[ ! -x "${MUJOCO_PYTHON}" ]]; then
  echo "Set MUJOCO_PYTHON to a Python interpreter with mujoco>=3.13 and numpy." >&2
  exit 2
fi
export MUJOCO_GL="${MUJOCO_GL:-egl}"
exec "${MUJOCO_PYTHON}" "${HERE}/export_scene.py" "$@"
