#!/usr/bin/env python3
"""Select five generated scenes and record their MuJoCo dynamics on separate GPUs."""
import argparse
from concurrent.futures import ThreadPoolExecutor
import json
import os
from pathlib import Path
import subprocess
import time

TOOL = Path(__file__).resolve().parents[1]
PYTHON = os.environ.get('MUJOCO_PYTHON', '/data1/chh/dependency/miniconda3/envs/mujoco_sim/bin/python')


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('--scene-root', type=Path, default=Path('/data1/chh/dataset/rubble_dataset/demo/mujoco_train'))
    p.add_argument('--output-root', type=Path, default=Path('/data1/chh/dataset/rubble_dataset/demo/mujoco_train_videos'))
    p.add_argument('--count', type=int, default=5)
    p.add_argument('--seeds', type=int, nargs='+', help='Specific scene seeds instead of evenly spaced selection')
    p.add_argument('--gpus', type=int, nargs='+', default=list(range(5)))
    p.add_argument('--fps', type=int, default=24)
    p.add_argument('--duration', type=float, default=8.)
    p.add_argument('--width', type=int, default=960)
    p.add_argument('--height', type=int, default=540)
    p.add_argument('--orbit-speed', type=float, default=18.)
    p.add_argument('--impulse', type=float, default=2.)
    p.add_argument('--effect-at', type=float, default=1.)
    args = p.parse_args()
    if args.count < 1 or not args.gpus or len(set(args.gpus)) != len(args.gpus):
        p.error('Count and GPU list must be nonempty and unique')
    scene_root = args.scene_root.resolve()
    report = json.loads((scene_root / 'generation_report.json').read_text())
    if report['status'] != 'passed':
        raise RuntimeError('Generation batch has not passed; record only after all scenes succeed')
    available = [r['seed'] for r in report['scenes']]
    if args.seeds:
        seeds = args.seeds
        if any(seed not in available for seed in seeds):
            raise ValueError('Requested seed is not in the generated batch')
    else:
        count = min(args.count, len(available))
        seeds = [available[round(i * (len(available) - 1) / max(1, count - 1))] for i in range(count)]
    output_root = args.output_root.resolve()
    output_root.mkdir(parents=True, exist_ok=True)
    if any((output_root / f'seed{seed}_orbit.mp4').exists() for seed in seeds):
        raise FileExistsError('An orbit video already exists; choose another output directory')
    started = time.monotonic()

    def one(item):
        index, seed = item
        gpu = args.gpus[index % len(args.gpus)]
        scene = scene_root / f'seed{seed}'
        video = output_root / f'seed{seed}_orbit.mp4'
        env = dict(os.environ, MUJOCO_GL='egl', MUJOCO_EGL_DEVICE_ID=str(gpu), CUDA_VISIBLE_DEVICES=str(gpu))
        cmd = [PYTHON, str(TOOL / 'scripts/record_mujoco_orbit.py'), str(scene), str(video),
               '--fps', str(args.fps), '--duration', str(args.duration), '--width', str(args.width),
               '--height', str(args.height), '--orbit-speed', str(args.orbit_speed),
               '--impulse', str(args.impulse), '--effect-at', str(args.effect_at), '--gpu', str(gpu)]
        record_log = output_root / f'seed{seed}_record.log'
        with record_log.open('w') as log:
            subprocess.run(cmd, env=env, stdout=log, stderr=subprocess.STDOUT, check=True)
        record_log.unlink()
        print(f'[video] PASS seed={seed} gpu={gpu} -> {video}', flush=True)
        return str(video)

    with ThreadPoolExecutor(max_workers=min(len(args.gpus), len(seeds))) as pool:
        videos = list(pool.map(one, enumerate(seeds)))
    summary = dict(scenes=seeds, videos=videos, gpus=args.gpus[:min(len(args.gpus), len(seeds))],
                   wall_seconds=round(time.monotonic() - started, 3), fps=args.fps,
                   duration_seconds=args.duration, effect_at_seconds=args.effect_at)
    (output_root / 'recording_report.json').write_text(json.dumps(summary, indent=2) + '\n')
    print(f'[video] all {len(videos)} recordings passed in {summary["wall_seconds"]:.1f}s', flush=True)


if __name__ == '__main__':
    main()
