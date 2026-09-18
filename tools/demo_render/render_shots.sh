#!/usr/bin/env bash
# RubbleSim demo-render harness (isolated tooling).
#
# Renders the demo clips: runs the standalone player headless (Xvfb + Vulkan) with per-shot camera
# and simulation parameters, writes one PNG per virtual frame, then encodes 720p H.264 mp4 with
# ffmpeg and builds thumbnails/contact sheets for the browser gallery.
#
# Usage:
#   ./render_shots.sh                 # render every clip in SHOTS
#   ./render_shots.sh 03_orbit_settled 05_fpv_robot
#   KEEP_FRAMES=1 ./render_shots.sh   # keep the PNG sequences (large)
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
PROJ="$(cd "${HERE}/../.." && pwd)"
PLAYER="${PLAYER:-${PROJ}/Builds/Linux/RubbleSim.x86_64}"
DEST="${DEMO_OUT:-/data1/chh/dataset/rubble_dataset/demo}"
WORK="${DEST}/_frames"
THUMBS="${DEST}/thumbs"
LOG_DIR="${HERE}/logs"
XDISP="${XDISP:-:99}"

FPS="${FPS:-30}"
WIDTH="${WIDTH:-1280}"
HEIGHT="${HEIGHT:-720}"
CRF="${CRF:-16}"
SETTLE="${SETTLE:-22}"          # virtual seconds the pile needs before "settled" shots start
TIMESCALE="${TIMESCALE:-1}"     # force Time.timeScale: 1 = natural-speed debris rain (10 = the project's fast settle)
SHOT_TIMEOUT="${SHOT_TIMEOUT:-3600}"
KEEP_FRAMES="${KEEP_FRAMES:-0}"

mkdir -p "${DEST}" "${WORK}" "${THUMBS}" "${LOG_DIR}"

# name | camera preset | extra simulation + harness arguments
# Framing was calibrated by sweeping distance/elevation against the real scene:
#   * macro accumulation shots aim between the falling debris (y~10..20) and the pile on the ground,
#   * settled shots aim at the pile (y~2) with radius ~7 -> camera ~15 units out.
SHOTS=(
  "01_overview_accum|overview|-demostart 0 -demoduration 24 -demolook 0,8,0 -demoradius 9 -demodiag 1"
  "02_low_impact|low|-demostart 0 -demoduration 24 -demolook 0,4,0 -demoradius 9 -demoelevation 2 -demodist 1.9 -demofov 62 -numlayers 2 -numobjs 380 -spawnboundx 8 -spawnboundz 8 -fogdensity 0.30 -fogintensity 0.75 -randomseed 7"
  "03_orbit_settled|orbit|-demostart ${SETTLE} -demoduration 20 -demolook 0,2,0 -demoradius 7 -demoorbitspeed 12"
  "04_closeup_settled|approach|-demostart ${SETTLE} -demoduration 18 -demolook 0,2,0 -demoradius 7 -demodollyfrom 2.0 -demodollyto 1.0 -demofov 48 -fogdensity 0.22 -fogintensity 0.6 -lightintensity 3.2 -setlightrot 1 -lightrotx 55 -lightroty 40"
  "05_fpv_robot|fpv|-demostart ${SETTLE} -demoduration 20 -demoteleport 1 -demodrive forward -demodrivespeed 1.2"
  "06_sensor_rgb|sensor|-demostart ${SETTLE} -demoduration 20 -demoteleport 1 -demodrive forward -demodrivespeed 1.2 -demoridefovscale 0.5625 -randomseed 21 -lightintensity 1.3 -setlightrot 1 -lightrotx 25 -lightroty -120"
)

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
  local midfile
  midfile=$(printf "${sdir}/frames/frame_%05d.png" "${mid}")
  local luma="n/a"
  [[ -f "${midfile}" ]] && luma=$(mean_luma "${midfile}")

  # poster + contact sheet
  ffmpeg -y -v error -i "${out}" -vf "select=eq(n\\,$(( FPS * 3 )))" -frames:v 1 "${THUMBS}/${name}_poster.jpg" || true
  ffmpeg -y -v error -i "${out}" -vf "fps=1/2.5,scale=426:-1,tile=4x2" -frames:v 1 "${THUMBS}/${name}_sheet.jpg" || true

  local dur
  dur=$(ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "${out}")
  echo "[render] ${name}: ${nframes} frames, ${dur}s, mid-frame mean luma=${luma} -> $(basename "${out}")"
  if [[ "${luma}" != "n/a" ]] && python3 -c "import sys;sys.exit(0 if float('${luma}')<4 else 1)"; then
    echo "[render] WARNING ${name}: mid frame looks black (mean luma ${luma})" >&2
  fi

  if [[ "${KEEP_FRAMES}" != "1" ]]; then
    rm -rf "${sdir}/frames"
  fi
}

run_shot() {
  local spec="$1"
  local name preset extra
  name="${spec%%|*}"
  preset="$(echo "${spec}" | cut -d'|' -f2)"
  extra="$(echo "${spec}" | cut -d'|' -f3-)"
  local sdir="${WORK}/${name}"

  if [[ ! -x "${PLAYER}" ]]; then
    echo "[render] player not found: ${PLAYER} (run build_player.sh first)" >&2
    exit 1
  fi

  echo "[render] === ${name} (preset=${preset}) ==="
  rm -rf "${sdir}"
  mkdir -p "${sdir}"

  ensure_xvfb

  set +e
  # shellcheck disable=SC2086
  # -batchmode is REQUIRED here: with a real window under bare Xvfb (no window manager) Unity's
  # Vulkan present path deadlocks after the very first frame. In batchmode the GPU device is still
  # created and rendering into our own RenderTexture works, which is all a frame-exact recorder needs.
  timeout "${SHOT_TIMEOUT}" env DISPLAY="${XDISP}" "${PLAYER}" \
    -batchmode -force-vulkan -screen-width "${WIDTH}" -screen-height "${HEIGHT}" \
    -logFile "${sdir}/player.log" \
    -demoshot "${name}" -demopreset "${preset}" \
    -demoout "${DEST}" -demowork "${sdir}" \
    -demofps "${FPS}" -demowidth "${WIDTH}" -demoheight "${HEIGHT}" \
    -demotimescale "${TIMESCALE}" -demoquit 1 \
    ${extra}
  local rc=$?
  set -e
  echo "[render] player exit=${rc}"

  grep -E "\[DemoDirector\]" "${sdir}/player.log" 2>/dev/null | tail -8 || true
  encode_shot "${name}"
}

main() {
  local wanted=("$@")
  local ran=0
  for spec in "${SHOTS[@]}"; do
    local name="${spec%%|*}"
    if [[ ${#wanted[@]} -gt 0 ]]; then
      local match=0
      for w in "${wanted[@]}"; do [[ "${name}" == "${w}" ]] && match=1; done
      [[ "${match}" == "1" ]] || continue
    fi
    run_shot "${spec}"
    ran=$((ran + 1))
  done
  if [[ "${ran}" -eq 0 ]]; then
    echo "[render] no shots matched: ${wanted[*]}" >&2
    exit 2
  fi
  python3 "${HERE}/make_gallery.py" --dir "${DEST}" || true
  echo "[render] done: ${ran} shot(s) -> ${DEST}"
}

main "$@"
