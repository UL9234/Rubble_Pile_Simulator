#!/usr/bin/env python3
"""Generate, export and validate Unity rubble scenes on a headless Linux server."""
import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile

from convert_scene import convert
from validate_scene import validate

ROOT = Path(__file__).resolve().parents[3]
TOOL = ROOT/'tools/mujoco_export'


def require_environment_only(snapshot):
    scene=json.loads(Path(snapshot).read_text())
    robots=[node['path'] for node in scene['nodes'] if 'VineController' in node['components']]
    if robots:
        raise RuntimeError(f'Robot objects remain in the environment snapshot: {robots}')
    if any(body['hasRigidbody'] and body['isKinematic'] for body in scene['bodies']):
        raise RuntimeError('Environment snapshot unexpectedly contains a kinematic rigid body')


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--seeds',nargs='+',type=int,default=[101])
    parser.add_argument('--output-root',type=Path,default=Path('/data1/chh/dataset/rubble_dataset/demo'))
    parser.add_argument('--padding',type=float,default=2.)
    parser.add_argument('--timestep',type=float,default=.002)
    parser.add_argument('--steps',type=int,default=1000)
    parser.add_argument('--spawn-size',type=float,nargs=2,default=[3.5,3.5],metavar=('X','Z'))
    parser.add_argument('--layers',type=int,default=2)
    parser.add_argument('--no-build',action='store_true',help='Use the existing player, which must include this exporter')
    parser.add_argument('--render',action='store_true',help='Additionally validate rendering through EGL, without an X display')
    parser.add_argument('--unity-args',nargs=argparse.REMAINDER,default=[])
    args=parser.parse_args()
    if args.steps<1 or args.layers<1 or min(args.spawn_size)<=0 or len(set(args.seeds))!=len(args.seeds):
        parser.error('Positive steps/layers/size and unique seeds required')
    args.output_root=args.output_root.resolve();args.output_root.mkdir(parents=True,exist_ok=True)
    targets=[args.output_root/f'mujoco_seed{seed}' for seed in args.seeds]
    for target in targets:
        if target.exists():raise FileExistsError(f'Refusing to overwrite existing export: {target}; choose another output root')
    link=ROOT/'Assets/MujocoExport'
    if not link.exists():link.symlink_to('../tools/mujoco_export',target_is_directory=True)
    if link.resolve()!=TOOL:raise RuntimeError('Assets/MujocoExport points to another directory')
    logs=TOOL/'.logs';logs.mkdir(exist_ok=True)
    if not args.no_build:
        env=dict(os.environ,LOGDIR=str(logs/'build'))
        subprocess.run(['bash',str(ROOT/'tools/demo_render/scripts/build_player.sh')],cwd=ROOT,env=env,check=True)
    player=ROOT/'Builds/Linux/RubbleSim.x86_64'
    if not player.is_file():raise FileNotFoundError(player)
    for seed,target in zip(args.seeds,targets):
        stage=Path(tempfile.mkdtemp(prefix=f'.mujoco_seed{seed}_',dir=args.output_root))
        try:
            # Same generation policy and parameters as the outer-orbit videos. No demo camera.
            values=dict(randomseed=seed,procdebris=1,numlayers=args.layers,spawnboundx=args.spawn_size[0],
                        spawnboundz=args.spawn_size[1],spawnposy=3,proccellsmin=8,proccellsmax=11,
                        procthicknessmin=.12,procthicknessmax=.18,proclayerspacing=3,procnoise=1,
                        proccorners=1,proccornerchance=1,procedgestep=.35,procnoisescale=2,procnoisefraction=.08,
                        pancakelayergap=4,pancakecatchfloor=1,pancakevictim=1,pancakevictimedge=0,
                        pancakevictimheight=1.7,pancakevictimyawspread=30,pancakevictimoffset=-.5667,
                        debrisdensity=2400,rebar=1,rebargrid=.30,rebarthickness=2.5,terrainground=1,
                        terrainstep=2,terrainamplitude=2.4,terrainfrequency=.12,terrainsize=80)
            cmd=[str(player),'-batchmode','-nographics','-logFile',str(stage/'unity_player.log'),'-mjexport',str(stage)]
            for name,value in values.items():cmd.extend(['-'+name,str(value)])
            cmd.extend(args.unity_args)
            print(f'[export] seed={seed}: capturing before the simulator removes rigid bodies',flush=True)
            with (stage/'unity_stdout.log').open('w') as log:
                subprocess.run(cmd,cwd=ROOT,stdout=log,stderr=subprocess.STDOUT,timeout=300,check=True)
            snapshot=stage/'unity_snapshot.json'
            if not snapshot.exists():raise RuntimeError('Player did not produce a snapshot; rebuild without --no-build')
            require_environment_only(snapshot)
            convert(snapshot,stage,args.padding,args.timestep)
            report=validate(stage,args.steps,args.render)
            # Write relative references; bundle remains usable after moving it to a different machine.
            (stage/'README.txt').write_text(
                'MuJoCo rubble scene export\n\n'
                'scene.xml: editable MJCF. scene.mjb: compiled for the recorded MuJoCo version.\n'
                'unity_snapshot.json: complete source physics values, geometry and scene inventory.\n'
                'initial_state.npz / keyframe unity_snapshot: portable initial state.\n'
                'scene_loader.py: load(), reset(), step() with source viscous drag; no training code.\n'
                'manifest.json: object mapping, crop parameters and cross-engine limitations.\n'
                'validation.json: source correspondence, reset, contacts and headless physics checks.\n\n'
                'import scene_loader\nmodel, data, drag = scene_loader.load()\n'
                'scene_loader.step(model, data, drag)\nscene_loader.reset(model, data)\n\n'
                'Bare MuJoCo usage: mj_resetDataKeyframe(model, data, model.key("unity_snapshot").id).\n'
                'Bare mj_step omits the source drag adapter. Solver trajectories are not identical to PhysX.\n'
            )
            # Atomic publication: existing demo videos are never touched.
            stage.rename(target)
            print(f"[export] PASS -> {target} ({report['counts']['dynamic_bodies']} dynamic bodies, {report['source_colliders_checked']} colliders, {args.steps} steps)",flush=True)
        except BaseException:
            print(f'[export] FAILED; diagnostics retained in {stage}',flush=True)
            raise


if __name__=='__main__':main()
