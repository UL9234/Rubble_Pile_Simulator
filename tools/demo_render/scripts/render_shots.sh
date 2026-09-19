#!/usr/bin/env bash
# RubbleSim demo-render harness — render the pancake-collapse demo clips.
#
# 5 random seeds x 2 cameras = 10 clips (1280x720, 30 fps):
#   seedNN_high   elevated camera framing the debris generation volume + the pile + the victim
#   seedNN_orbit  low camera, one full 360 deg revolution around the generation centre
#
# The scenario itself (layer count, piece count, footprint, slab orientation, victim placement) is
# configured through the simulator's own command-line arguments; this script only drives cameras and
# frames. Old renders in the output folder are removed first so repeated runs do not pile up.
#
# Usage:
#   ./render_shots.sh                          # all 10 clips
#   ./render_shots.sh seed101_high             # a subset
#   SEEDS="7 8" HIGH_DUR=16 ./render_shots.sh  # remix
#   KEEP_FRAMES=1 ./render_shots.sh            # keep the PNG sequences
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT="$(cd "${HERE}/.." && pwd)"
PROJ="$(cd "${HERE}/../../.." && pwd)"
PLAYER="${PLAYER:-${PROJ}/Builds/Linux/RubbleSim.x86_64}"
DEST="${DEMO_OUT:-/data1/chh/dataset/rubble_dataset/demo}"
WORK="${ROOT}/.work"
LOG_DIR="${ROOT}/.logs"
XDISP="${XDISP:-:99}"

FPS="${FPS:-30}"
WIDTH="${WIDTH:-1280}"
HEIGHT="${HEIGHT:-720}"
CRF="${CRF:-16}"
SEEDS="${SEEDS:-101 202 303 404 505}"
HIGH_DUR="${HIGH_DUR:-20}"
ORBIT_DUR="${ORBIT_DUR:-30}"
ORBIT_SPEED="${ORBIT_SPEED:-12}"     # deg/s; 12 * 30 s = one full revolution
SHOT_TIMEOUT="${SHOT_TIMEOUT:-1800}"
KEEP_FRAMES="${KEEP_FRAMES:-0}"

# --- scenario (parsed by the simulator's own CustomArgs) ------------------------------------------
# 2 layers x 30 pieces = 10% of the simulator default (300), 3 x 3 m footprint, slabs dropped
# from 3 m with a +/-20 deg orientation perturbation, victim on the +x boundary (head outward).
# Slab scale: 80% of the earlier doubled run (1.6-4.4 -> 1.28-3.52). Override with PANC_SCALE_MIN/MAX.
PANC_SCALE_MIN="${PANC_SCALE_MIN:-1.28}"
PANC_SCALE_MAX="${PANC_SCALE_MAX:-3.52}"
SCENARIO_ARGS="-pancake 1 -numlayers 2 -numobjs 30 \
-spawnboundx 3.0 -spawnboundz 3.0 -spawnposy 3.0 -spawnboundy 1.6 \
-pancaketilt 20 -pancakescalemin ${PANC_SCALE_MIN} -pancakescalemax ${PANC_SCALE_MAX} \
-pancakelayergap 4 -pancakespawndelay 0.06 -pancakecatchfloor 1 \
-pancakevictim 1 -pancakevictimedge 0 -pancakevictimheight 1.7 -pancakevictimyawspread 30"

# --- cameras --------------------------------------------------------------------------------------
# high : sees the whole generation volume (drop band tops out at y = 3.8) down to the ground,
#        aimed between the pile centre and the victim so both stay in frame
HIGH_CAM="-demolook 0.4,1.4,0 -demoradius 2.8 -demodist 1.7 -demoelevation 45 -demofov 52"
# orbit: low angle, one full revolution around the generation centre
ORBIT_CAM="-demolook 0.35,0.8,0 -demoradius 2.8 -demoelevation 10 -demodist 2.0 -demofov 55 -demoorbitspeed ${ORBIT_SPEED}"

if [[ -z "${DEST}" || "${DEST}" != /* || "${DEST}" == "/" ]]; then
  echo "[render] refusing to clean unsafe DEMO_OUT='${DEST}'" >&2
  exit 2
fi

mkdir -p "${WORK}" "${LOG_DIR}"
echo "[render] clearing previous renders in ${DEST}"
rm -rf "${DEST}"
mkdir -p "${DEST}"

ensure_xvfb() {
  if ! pgrep -f "Xvfb ${XDISP}" >/dev/null 2>&1; then
    echo "[render] starting Xvfb ${XDISP}"
    Xvfb "${XDISP}" -screen 0 1920x1080x24 >"${LOG_DIR}/xvfb.log" 2>&1 &
    sleep 2
  fi
}

mean_luma() {  # rough "is this frame black?" check
  ffmpeg -v error -i "$1" -vf "scale=64:36,format=gray" -f rawvideo - 2>/dev/null \
    | python3 -c 'import sys;d=sys.stdin.buffer.read();print(round(sum(d)/max(1,len(d)),1))'
}

# run_shot <name> <camera preset> <extra args>
run_shot() {
  local name="$1" preset="$2" extra="$3"
  local sdir="${WORK}/${name}"

  if [[ ! -x "${PLAYER}" ]]; then
    echo "[render] player not found: ${PLAYER} (run scripts/build_player.sh first)" >&2
    exit 1
  fi

  echo "[render] === ${name} (preset=${preset}) ==="
  rm -rf "${sdir}"
  mkdir -p "${sdir}"
  ensure_xvfb

  set +e
  # shellcheck disable=SC2086
  # -batchmode is REQUIRED: with a real window under bare Xvfb (no window manager) Unity's Vulkan
  # present path deadlocks after the first frame. In batchmode the GPU device is still created and
  # rendering into our own RenderTexture works, which is all a frame-exact recorder needs.
  timeout "${SHOT_TIMEOUT}" env DISPLAY="${XDISP}" "${PLAYER}" \
    -batchmode -force-vulkan -screen-width "${WIDTH}" -screen-height "${HEIGHT}" \
    -logFile "${sdir}/player.log" \
    -demoshot "${name}" -demopreset "${preset}" \
    -demoout "${DEST}" -demowork "${sdir}" \
    -demofps "${FPS}" -demowidth "${WIDTH}" -demoheight "${HEIGHT}" \
    -demotimescale 1 -demoquit 1 \
    ${extra}
  local rc=$?
  set -e
  echo "[render] player exit=${rc}"
  grep -E "\[DebrisSpawner\]|\[DemoDirector\]" "${sdir}/player.log" 2>/dev/null | tail -6 || true

  encode_shot "${name}"
}

encode_shot() {
  local name="$1"
  local sdir="${WORK}/${name}"
  local out="${DEST}/${name}.mp4"
  local nframes
  nframes=$(find "${sdir}/frames" -name 'frame_*.png' 2>/dev/null | wc -l)
  if [[ "${nframes}" -lt 5 ]]; then
    echo "[render] ERROR ${name}: only ${nframes} frames captured; see ${sdir}/player.log" >&2
    return 1
  fi

  ffmpeg -y -v error -framerate "${FPS}" -i "${sdir}/frames/frame_%05d.png" \
    -c:v libx264 -preset slow -crf "${CRF}" -pix_fmt yuv420p -movflags +faststart \
    "${out}"

  local mid=$(( nframes / 2 ))
  local luma="n/a"
  local midfile
  midfile=$(printf "${sdir}/frames/frame_%05d.png" "${mid}")
  [[ -f "${midfile}" ]] && luma=$(mean_luma "${midfile}")

  local dur
  dur=$(ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "${out}")
  echo "[render] ${name}: ${nframes} frames, ${dur}s, mid-frame mean luma=${luma}"
  if [[ "${luma}" != "n/a" ]] && python3 -c "import sys;sys.exit(0 if float('${luma}')<4 else 1)"; then
    echo "[render] WARNING ${name}: mid frame looks black (mean luma ${luma})" >&2
  fi

  if [[ "${KEEP_FRAMES}" != "1" ]]; then
    rm -rf "${sdir}/frames"
  fi
}

main() {
  local wanted=("$@")
  local ran=0

  for seed in ${SEEDS}; do
    local specs=(
      "seed${seed}_high|overview|-randomseed ${seed} -demostart 0 -demoduration ${HIGH_DUR} ${HIGH_CAM} ${SCENARIO_ARGS}"
      "seed${seed}_orbit|orbit|-randomseed ${seed} -demostart 0 -demoduration ${ORBIT_DUR} ${ORBIT_CAM} ${SCENARIO_ARGS}"
    )
    for spec in "${specs[@]}"; do
      local name="${spec%%|*}"
      if [[ ${#wanted[@]} -gt 0 ]]; then
        local match=0
        for w in "${wanted[@]}"; do [[ "${name}" == "${w}" ]] && match=1; done
        [[ "${match}" == "1" ]] || continue
      fi
      run_shot "${name}" "$(echo "${spec}" | cut -d'|' -f2)" "$(echo "${spec}" | cut -d'|' -f3-)"
      ran=$((ran + 1))
    done
  done

  if [[ "${ran}" -eq 0 ]]; then
    echo "[render] no shots matched: ${wanted[*]}" >&2
    exit 2
  fi
  echo "[render] done: ${ran} clip(s) -> ${DEST}"
  ls -la "${DEST}"
}

main "$@"
