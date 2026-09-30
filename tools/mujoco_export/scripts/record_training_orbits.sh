#!/usr/bin/env bash
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
MUJOCO_PYTHON="${MUJOCO_PYTHON:-/data1/chh/dependency/miniconda3/envs/mujoco_sim/bin/python}"
export MUJOCO_PYTHON MUJOCO_GL=egl
exec "${MUJOCO_PYTHON}" "${HERE}/record_training_orbits.py" "$@"
