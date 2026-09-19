#!/usr/bin/env bash
# RubbleSim demo-render harness — render the pancake-collapse demo clips.
#
# 10 random scenes x 1 camera = 10 clips (1280x720, 30 fps):
#   seedNN_orbit  low camera, one full 360 deg revolution around the generation centre
#
# The generation policy is fixed in code: layers are built one after another (a layer lands before the
# next is built) and, inside a layer, fragments are released nearest-the-centre first, one every
# sqrt(2 t / g) - the time a plate needs to fall clear of its own thickness. Together that keeps the
# collapse inside the original footprint (see Docs/procedural_debris_generation.md 5.1).
# CAMS="orbit high" adds the elevated camera back.
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
SEEDS="${SEEDS:-101 202 303 404 505 606 707 808 909 1010}"
HIGH_DUR="${HIGH_DUR:-20}"
ORBIT_DUR="${ORBIT_DUR:-30}"
ORBIT_SPEED="${ORBIT_SPEED:-12}"     # deg/s; 12 * 30 s = one full revolution
CAMS="${CAMS:-orbit}"
SHOT_TIMEOUT="${SHOT_TIMEOUT:-1800}"
KEEP_FRAMES="${KEEP_FRAMES:-0}"

# --- scenario (parsed by the simulator's own CustomArgs) ------------------------------------------
# Fully procedural concrete slabs (ProceduralSlabFactory): one Voronoi layer per -numlayers, each
# layer tiling the spawn area, layer spacing 3 m, whole-scene slab thickness 120-180 mm.
# No prefab debris and no random scaling: each piece is generated at its real size.
# The victim sits on the +x boundary, pulled 1/3 of its body height towards the spawn centre.
PANC_DENSITY="${PANC_DENSITY:-2400}"          # kg/m3 (reinforced concrete) -> mass = density * volume
PANC_VICTIM_OFFSET="${PANC_VICTIM_OFFSET:--0.5667}"   # -1/3 * 1.7 m, i.e. inwards
# horizontal edge noise, strong; the top outline uses noise seed + 1 so the break faces are not
# vertical planes. Fragments are separated by the crack geometry alone (no repulsion anywhere).
SCENARIO_ARGS="-procdebris 1 -numlayers 2 \
-spawnboundx 3.5 -spawnboundz 3.5 -spawnposy 3.0 \
-proccellsmin 8 -proccellsmax 11 -procthicknessmin 0.12 -procthicknessmax 0.18 -proclayerspacing 3.0 \
-procnoise 1 -proccorners 1 -proccornerchance 1.0 -procedgestep 0.35 -procnoisescale 2.0 -procnoisefraction 0.08 \
-pancakelayergap 4 -pancakecatchfloor 1 \
-pancakevictim 1 -pancakevictimedge 0 -pancakevictimheight 1.7 -pancakevictimyawspread 30 \
-pancakevictimoffset ${PANC_VICTIM_OFFSET} -debrisdensity ${PANC_DENSITY}"

# --- cameras --------------------------------------------------------------------------------------
# high : sees the whole generation volume (drop band tops out at y = 3.8) down to the ground,
#        aimed between the pile centre and the victim so both stay in frame
HIGH_CAM="-demolook 0.4,1.2,0 -demoradius 3.1 -demodist 1.7 -demoelevation 45 -demofov 52"
# orbit: low angle, one full revolution around the generation centre
ORBIT_CAM="-demolook 0.3,0.5,0 -demoradius 3.1 -demoelevation 10 -demodist 2.0 -demofov 55 -demoorbitspeed ${ORBIT_SPEED}"

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
    local specs=()
    for cam in ${CAMS}; do
      if [[ "${cam}" == "high" ]]; then
        specs+=("seed${seed}_high|overview|-randomseed ${seed} -demostart 0 -demoduration ${HIGH_DUR} ${HIGH_CAM} ${SCENARIO_ARGS}")
      else
        specs+=("seed${seed}_orbit|orbit|-randomseed ${seed} -demostart 0 -demoduration ${ORBIT_DUR} ${ORBIT_CAM} ${SCENARIO_ARGS}")
      fi
    done
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
