#!/usr/bin/env python3
"""Offscreen MuJoCo orbit video: perturb two rubble bodies after a quiet opening."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

import mujoco
import numpy as np


def record(scene, output, *, fps, duration, width, height, orbit_speed, impulse, effect_at, gpu):
    scene = Path(scene).resolve()
    output = Path(output).resolve()
    output.parent.mkdir(parents=True, exist_ok=True)
    if output.exists():
        raise FileExistsError(output)
    model = mujoco.MjModel.from_xml_path(str(scene / 'scene.xml'))
    data = mujoco.MjData(model)
    mujoco.mj_forward(model, data)
    model.vis.global_.offwidth = max(model.vis.global_.offwidth, width)
    model.vis.global_.offheight = max(model.vis.global_.offheight, height)
    free = []
    for jid in range(model.njnt):
        if model.jnt_type[jid] == mujoco.mjtJoint.mjJNT_FREE:
            free.append((model.jnt_bodyid[jid], model.jnt_dofadr[jid]))
    if not free:
        raise ValueError('No dynamic rubble bodies in scene')
    positions = np.array([data.xpos[bid].copy() for bid, _ in free])
    center = np.median(positions, axis=0)
    center[2] = max(.35, center[2])
    radius = max(7., float(np.max(np.linalg.norm(positions[:, :2] - center[:2], axis=1))) * 3.2)
    ranked = sorted(free, key=lambda item: (data.xpos[item[0], 2],
                                          -np.linalg.norm(data.xpos[item[0], :2] - center[:2])), reverse=True)
    selected = ranked[:2]
    frames = round(duration * fps)
    temporary = output.with_name('.' + output.name + '.part')
    ffmpeg = ['ffmpeg', '-y', '-loglevel', 'error', '-f', 'rawvideo', '-pix_fmt', 'rgb24',
              '-s', f'{width}x{height}', '-r', str(fps), '-i', '-', '-an',
              '-c:v', 'libx264', '-preset', 'veryfast', '-crf', '21',
              '-pix_fmt', 'yuv420p', '-f', 'mp4', str(temporary)]
    activated = False
    max_contacts = 0
    try:
        with output.with_suffix('.ffmpeg.log').open('w') as log:
            encoder = subprocess.Popen(ffmpeg, stdin=subprocess.PIPE, stdout=subprocess.DEVNULL, stderr=log)
            try:
                with mujoco.Renderer(model, height=height, width=width) as renderer:
                    for frame in range(frames):
                        target_time = frame / fps
                        while data.time + 1e-9 < target_time:
                            mujoco.mj_step(model, data)
                            max_contacts = max(max_contacts, data.ncon)
                            if np.any(data.warning.number):
                                raise RuntimeError('MuJoCo reported instability during video')
                        if not activated and target_time >= effect_at:
                            for index, (_, dof) in enumerate(selected):
                                sign = 1. if index % 2 == 0 else -1.
                                data.qvel[dof:dof + 3] += [sign * impulse, .35 * impulse, .8 * impulse]
                            mujoco.mj_forward(model, data)
                            activated = True
                        camera = mujoco.MjvCamera()
                        mujoco.mjv_defaultCamera(camera)
                        camera.lookat[:] = center
                        camera.distance = radius
                        camera.azimuth = 40 + orbit_speed * target_time
                        camera.elevation = -27
                        renderer.update_scene(data, camera)
                        encoder.stdin.write(np.ascontiguousarray(renderer.render()).tobytes())
            finally:
                if encoder.stdin and not encoder.stdin.closed:
                    encoder.stdin.close()
                rc = encoder.wait()
                if rc:
                    raise RuntimeError(f'ffmpeg exited with {rc}; see {output.with_suffix(".ffmpeg.log")}')
        temporary.rename(output)
        output.with_suffix('.ffmpeg.log').unlink(missing_ok=True)
    except BaseException:
        temporary.unlink(missing_ok=True)
        raise
    metadata = dict(scene=str(scene), video=str(output), seed=json.loads((scene / 'metadata.json').read_text())['seed'],
                    gpu=gpu, fps=fps, duration_seconds=duration, width=width, height=height,
                    frames=frames, orbit_degrees_per_second=orbit_speed, effect_at_seconds=effect_at,
                    initial_speed='zero for every exported body',
                    visual_excitation='two upper slabs receive translational velocity in the video only',
                    impulse_m_per_s=impulse, excited_body_ids=[int(bid) for bid, _ in selected],
                    max_contacts=max_contacts, scene_sha256=hashlib.sha256((scene / 'scene.xml').read_bytes()).hexdigest())
    output.with_suffix('.json').write_text(json.dumps(metadata, indent=2) + '\n')
    return metadata


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('scene', type=Path)
    p.add_argument('output', type=Path)
    p.add_argument('--fps', type=int, default=24)
    p.add_argument('--duration', type=float, default=8.)
    p.add_argument('--width', type=int, default=960)
    p.add_argument('--height', type=int, default=540)
    p.add_argument('--orbit-speed', type=float, default=18.)
    p.add_argument('--impulse', type=float, default=2.)
    p.add_argument('--effect-at', type=float, default=1.)
    p.add_argument('--gpu', type=int, default=int(os.environ.get('MUJOCO_EGL_DEVICE_ID', '0')))
    args = p.parse_args()
    if args.fps < 1 or args.duration <= args.effect_at or args.effect_at < 0 or min(args.width, args.height) < 16:
        p.error('Invalid frame rate, duration, effect time or resolution')
    result = record(args.scene, args.output, fps=args.fps, duration=args.duration,
                    width=args.width, height=args.height, orbit_speed=args.orbit_speed,
                    impulse=args.impulse, effect_at=args.effect_at, gpu=args.gpu)
    print(json.dumps(result))


if __name__ == '__main__':
    main()
