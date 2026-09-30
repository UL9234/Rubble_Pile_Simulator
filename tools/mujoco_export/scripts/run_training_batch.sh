#!/usr/bin/env bash
# Generate first, then record from the same output root if generation passed.
set -euo pipefail
HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCENE_ROOT="/data1/chh/dataset/rubble_dataset/demo/mujoco_train"
ARGS=("$@")
for ((i=0; i<${#ARGS[@]}; i++)); do
  if [[ "${ARGS[i]}" == "--output-root" ]]; then
    SCENE_ROOT="${ARGS[i+1]}"
  elif [[ "${ARGS[i]}" == --output-root=* ]]; then
    SCENE_ROOT="${ARGS[i]#--output-root=}"
  fi
done
bash "${HERE}/generate_training_scenes.sh" "$@"
bash "${HERE}/record_training_orbits.sh" --scene-root "${SCENE_ROOT}" --output-root "$(dirname "${SCENE_ROOT}")/mujoco_train_videos"
