#!/usr/bin/env python3
"""Generate settled, self-contained MuJoCo training scenes in parallel."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import queue
import subprocess
import threading
import time

ROOT = Path(__file__).resolve().parents[3]
TOOL = ROOT / 'tools/mujoco_export'
PLAYER = ROOT / 'Builds/Linux/RubbleSim.x86_64'
PYTHON = os.environ.get('MUJOCO_PYTHON', '/data1/chh/dependency/miniconda3/envs/mujoco_sim/bin/python')


def parameters(args, seed):
    return dict(randomseed=seed, procdebris=1, numlayers=args.layers,
                spawnboundx=args.spawn_size[0], spawnboundz=args.spawn_size[1], spawnposy=3,
                proccellsmin=args.cells[0], proccellsmax=args.cells[1],
                procthicknessmin=.12, procthicknessmax=.18, proclayerspacing=3,
                procnoise=1, proccorners=1, proccornerchance=1, procedgestep=.35,
                procnoisescale=2, procnoisefraction=.08, pancakelayergap=args.layer_gap,
                pancakecatchfloor=1, pancakevictim=1, pancakevictimedge=0,
                pancakevictimheight=1.7, pancakevictimyawspread=30,
                pancakevictimoffset=-.5667, debrisdensity=args.density,
                rebar=1, rebargrid=.30, rebarthickness=2.5,
                terrainground=1, terrainstep=args.terrain_step,
                terrainamplitude=args.terrain_amplitude,
                terrainfrequency=args.terrain_frequency, terrainsize=80,
                mjlinvel=args.linear_threshold, mjangvel=args.angular_threshold,
                mjdwell=args.dwell, mjtimeout=args.settle_timeout)


def run_attempt(args, seed, gpu, slot, attempt, work_root, output_root):
    stage = work_root / f'slot{slot:03d}_attempt{attempt:02d}_seed{seed}'
    stage.mkdir()
    config = dict(seed=seed, layers=args.layers, spawn_size_m=args.spawn_size,
                  debris_density=args.density, terrain_step_m=args.terrain_step,
                  terrain_amplitude_m=args.terrain_amplitude,
                  terrain_frequency=args.terrain_frequency,
                  gpu=gpu, unity_arguments=parameters(args, seed))
    (stage / 'generation_config.json').write_text(json.dumps(config, indent=2) + '\n')
    unity = [str(PLAYER), '-batchmode', '-nographics', '-logFile', str(stage / 'unity_player.log'),
             '-mjexport', str(stage)]
    for name, value in config['unity_arguments'].items():
        unity.extend(['-' + name, str(value)])
    env = dict(os.environ, MUJOCO_GL='egl', MUJOCO_EGL_DEVICE_ID=str(gpu), CUDA_VISIBLE_DEVICES=str(gpu))
    started = time.monotonic()
    with (stage / 'unity_stdout.log').open('w') as log:
        subprocess.run(unity, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                       timeout=args.unity_wall_timeout, check=True)
    if not (stage / 'unity_snapshot.json').is_file():
        raise RuntimeError('Unity quit without a settled snapshot; inspect unity_player.log')
    prepared = [PYTHON, str(TOOL / 'scripts/prepare_training_scene.py'), str(stage),
                '--padding', str(args.padding), '--timestep', str(args.timestep),
                '--steps', str(args.validation_steps), '--render']
    with (stage / 'preparation.log').open('w') as log:
        subprocess.run(prepared, cwd=ROOT, env=env, stdout=log, stderr=subprocess.STDOUT,
                       timeout=args.prepare_wall_timeout, check=True)
    metadata = json.loads((stage / 'metadata.json').read_text())
    target = output_root / f'seed{seed}'
    if target.exists():
        raise FileExistsError(target)
    stage.rename(target)
    return dict(seed=seed, path=str(target), gpu=gpu, slot=slot, attempts=attempt,
                wall_seconds=round(time.monotonic() - started, 3),
                unity_settle_seconds=metadata['settling']['elapsedSeconds'],
                dynamic_bodies=metadata['counts']['dynamic_bodies'])


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--output-root', type=Path, default=Path('/data1/chh/dataset/rubble_dataset/demo/mujoco_train'))
    p.add_argument('--count', type=int, default=20)
    p.add_argument('--seed-start', type=int, default=1000)
    p.add_argument('--seeds', type=int, nargs='+', help='Explicit initial seeds; count becomes their number')
    p.add_argument('--layers', type=int, default=2)
    p.add_argument('--spawn-size', type=float, nargs=2, default=[3.5, 3.5], metavar=('X', 'Z'))
    p.add_argument('--cells', type=int, nargs=2, default=[8, 11], metavar=('MIN', 'MAX'))
    p.add_argument('--density', type=float, default=2400.)
    p.add_argument('--layer-gap', type=float, default=4.)
    p.add_argument('--terrain-step', type=float, default=2.)
    p.add_argument('--terrain-amplitude', type=float, default=2.4)
    p.add_argument('--terrain-frequency', type=float, default=.12)
    p.add_argument('--padding', type=float, default=2.)
    p.add_argument('--timestep', type=float, default=.002)
    p.add_argument('--linear-threshold', type=float, default=.02)
    p.add_argument('--angular-threshold', type=float, default=.05)
    p.add_argument('--dwell', type=float, default=1.)
    p.add_argument('--settle-timeout', type=float, default=15.)
    p.add_argument('--validation-steps', type=int, default=1000)
    p.add_argument('--gpus', type=int, nargs='+', default=list(range(6)))
    p.add_argument('--workers', type=int, default=6)
    p.add_argument('--max-attempts-per-scene', type=int, default=4)
    p.add_argument('--unity-wall-timeout', type=float, default=240.)
    p.add_argument('--prepare-wall-timeout', type=float, default=180.)
    p.add_argument('--no-build', action='store_true')
    args = p.parse_args()
    seeds = args.seeds if args.seeds else list(range(args.seed_start, args.seed_start + args.count))
    if not seeds or len(seeds) != len(set(seeds)) or min(seeds) < 0:
        p.error('Seeds must be unique nonnegative integers')
    if (args.layers < 1 or min(args.spawn_size) <= 0 or args.cells[0] < 1 or args.cells[1] < args.cells[0]
            or args.density <= 0 or args.layer_gap <= 0 or args.padding < 0 or args.terrain_step <= 0
            or args.timestep <= 0 or args.linear_threshold <= 0 or args.angular_threshold <= 0
            or args.dwell <= 0 or args.settle_timeout < args.dwell or args.validation_steps < 1
            or args.workers < 1 or args.max_attempts_per_scene < 1 or len(set(args.gpus)) != len(args.gpus)):
        p.error('Invalid generation, settlement or worker parameters')
    lanes = args.gpus[:min(args.workers, len(args.gpus), len(seeds))]
    if not lanes:
        p.error('At least one GPU is required')
    output_root = args.output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    if (output_root / 'generation_report.json').exists() or any((output_root / f'seed{s}').exists() for s in seeds):
        raise FileExistsError('Output already contains this batch; choose another output root')
    link = ROOT / 'Assets/MujocoExport'
    if not link.exists():
        link.symlink_to('../tools/mujoco_export', target_is_directory=True)
    if link.resolve() != TOOL:
        raise RuntimeError('Assets/MujocoExport points to another directory')
    if not args.no_build:
        env = dict(os.environ, LOGDIR=str(TOOL / '.logs/build'))
        subprocess.run(['bash', str(ROOT / 'tools/demo_render/scripts/build_player.sh')],
                       cwd=ROOT, env=env, check=True)
    if not PLAYER.is_file():
        raise FileNotFoundError(PLAYER)

    work_root = TOOL / '.work' / ('training_' + datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ'))
    work_root.mkdir(parents=True)
    tasks = queue.Queue()
    for slot, seed in enumerate(seeds):
        tasks.put((slot, seed))
    lock = threading.Lock()
    replacement_seed = max(seeds) + 1
    successes, failures, terminal = [], [], []
    batch_start = time.monotonic()

    def lane(gpu):
        nonlocal replacement_seed
        while True:
            try:
                slot, seed = tasks.get_nowait()
            except queue.Empty:
                return
            try:
                for attempt in range(1, args.max_attempts_per_scene + 1):
                    try:
                        record = run_attempt(args, seed, gpu, slot, attempt, work_root, output_root)
                        with lock:
                            successes.append(record)
                        print(f"[scene] PASS seed={seed} gpu={gpu} wall={record['wall_seconds']:.1f}s "
                              f"settle={record['unity_settle_seconds']:.2f}s", flush=True)
                        break
                    except Exception as error:
                        with lock:
                            failures.append(dict(slot=slot, seed=seed, gpu=gpu, attempt=attempt,
                                                 error=f'{type(error).__name__}: {error}'))
                            replacement_seed += 1
                            next_seed = replacement_seed - 1
                        print(f'[scene] RETRY slot={slot} seed={seed} gpu={gpu}: {error}', flush=True)
                        seed = next_seed
                else:
                    with lock:
                        terminal.append(slot)
            finally:
                tasks.task_done()

    with ThreadPoolExecutor(max_workers=len(lanes)) as pool:
        list(pool.map(lane, lanes))
    successes.sort(key=lambda r: r['slot'])
    elapsed = time.monotonic() - batch_start
    report = dict(status='passed' if len(successes) == len(seeds) else 'failed',
                  requested_scenes=len(seeds), completed_scenes=len(successes),
                  gpus=lanes, failed_attempts=failures, exhausted_slots=terminal,
                  total_wall_seconds=round(elapsed, 3),
                  mean_successful_scene_seconds=round(sum(r['wall_seconds'] for r in successes) / len(successes), 3)
                    if successes else None,
                  wall_seconds_per_completed_scene=round(elapsed / len(successes), 3) if successes else None,
                  scenes=successes)
    (output_root / 'generation_report.json').write_text(json.dumps(report, indent=2) + '\n')
    (output_root / 'index.jsonl').write_text(''.join(json.dumps(r) + '\n' for r in successes))
    print(f"[batch] {report['status']}: {len(successes)}/{len(seeds)} scenes; "
          f"mean={report['mean_successful_scene_seconds']}s, wall/scene={report['wall_seconds_per_completed_scene']}s", flush=True)
    if terminal:
        raise SystemExit(1)


if __name__ == '__main__':
    main()
