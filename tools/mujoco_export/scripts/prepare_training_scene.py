#!/usr/bin/env python3
"""Validate a settled Unity snapshot and publish only MuJoCo training inputs."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

import mujoco
import numpy as np

from convert_scene import convert
from validate_scene import validate


def prepare(stage, padding, timestep, steps, render):
    stage = Path(stage)
    snapshot_path = stage / 'unity_snapshot.json'
    source = json.loads(snapshot_path.read_text())
    settling = source.get('settling')
    if not settling or not settling['passed'] or settling['elapsedSeconds'] > settling['timeoutSeconds'] + .001:
        raise ValueError('Unity did not report a successful, bounded settlement')
    if any('VineController' in n['components'] for n in source['nodes']):
        raise ValueError('Robot found in environment-only snapshot')
    if any(b['hasRigidbody'] and b['isKinematic'] for b in source['bodies']):
        raise ValueError('Kinematic rigid body found in environment-only snapshot')
    config = json.loads((stage / 'generation_config.json').read_text())
    source_hash = hashlib.sha256(snapshot_path.read_bytes()).hexdigest()
    manifest = convert(snapshot_path, stage, padding, timestep)
    full_report = validate(stage, steps, render)

    # The settled pose is already encoded in each body's XML transform.  Bake it into
    # qpos0, discard Unity's timestamp/velocities, and let MuJoCo own all later motion.
    xml_path = stage / 'scene.xml'
    tree = ET.parse(xml_path)
    root = tree.getroot()
    keyframes = root.find('keyframe')
    if keyframes is not None:
        root.remove(keyframes)
    flag = root.find('option/flag')
    if flag is not None:
        flag.set('sleep', 'disable')
    for body in root.findall('.//body'):
        body.attrib.pop('sleep', None)
    ET.indent(tree)
    tree.write(xml_path, encoding='utf-8', xml_declaration=True)
    model = mujoco.MjModel.from_xml_path(str(xml_path))
    data = mujoco.MjData(model)
    if model.nmocap or model.nu or not model.nq:
        raise AssertionError('Training scene must contain free debris, no robot or control')
    np.testing.assert_allclose(data.qpos, model.qpos0, atol=1e-10)
    if np.any(data.qvel) or data.time:
        raise AssertionError('Training scene must start at time zero with zero velocity')
    mujoco.mj_forward(model, data)
    if np.any(data.warning.number):
        raise AssertionError('MuJoCo forward pass reported an instability')

    density = float(config['debris_density'])
    source_only_bodies = {}
    for body in source['bodies']:
        if body['id'] not in manifest['bodies'] or not body['hasRigidbody']:
            continue
        record = dict(linear_drag=body['drag'], angular_drag=body['angularDrag'],
                      collision_detection_mode=body['collisionDetectionMode'],
                      interpolation=body['interpolation'], sleep_threshold=body['sleepThreshold'],
                      max_linear_velocity=body['maxLinearVelocity'],
                      max_angular_velocity=body['maxAngularVelocity'])
        if density > 0 and manifest['bodies'][body['id']]['mode'] == 'dynamic':
            record['source_mass_volume_m3'] = body['mass'] / density
        source_only_bodies[body['id']] = record
    source_only_colliders = {}
    for collider in source['colliders']:
        if collider['id'] not in manifest['colliders']:
            continue
        source_only_colliders[collider['id']] = dict(
            static_friction=collider['staticFriction'], restitution=collider['bounciness'],
            friction_combine=collider['frictionCombine'], bounce_combine=collider['bounceCombine'],
            contact_offset=collider['contactOffset'], physics_material=collider['physicsMaterial'])
    metadata = dict(schema_version=1, seed=source['seed'], generator=config,
                    source_snapshot_sha256=source_hash, unity_version=source['unityVersion'],
                    mujoco_version=mujoco.__version__, coordinate_system='MuJoCo: X right, Y forward, Z up; SI units',
                    initial_state='settled body poses in scene.xml; time and all velocities zero',
                    settling=settling, terrain=dict(requested_bounds=manifest['terrain_requested_bounds'],
                        actual_bounds=manifest['terrain_actual_bounds'], padding_m=padding),
                    counts=manifest['counts'],
                    density_kg_m3=density,
                    source_volume_note='Procedural slab mass uses slab volume; source_mass_volume_m3 is mass/density. '
                        'For nonprocedural mesh prefabs the Unity generator may instead use mesh bounds volume.',
                    source_only_physics=dict(bodies=source_only_bodies, colliders=source_only_colliders),
                    validation=dict(source_correspondence=full_report['status'],
                                    full_physics_steps=full_report['physics_steps'],
                                    max_contacts=full_report['max_contacts'],
                                    headless_render=full_report['offscreen_render'],
                                    initial_qvel_zero=True, compiled_model_loaded=True),
                    engine_limitations=manifest['conversion_notes'])
    (stage / 'metadata.json').write_text(json.dumps(metadata, ensure_ascii=False, separators=(',', ':')) + '\n')
    for entry in stage.iterdir():
        if entry.name not in {'scene.xml', 'assets', 'metadata.json'}:
            if entry.is_dir():
                shutil.rmtree(entry)
            else:
                entry.unlink()
    return metadata


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument('stage', type=Path)
    p.add_argument('--padding', type=float, default=2.)
    p.add_argument('--timestep', type=float, default=.002)
    p.add_argument('--steps', type=int, default=1000)
    p.add_argument('--render', action='store_true')
    args = p.parse_args()
    result = prepare(args.stage, args.padding, args.timestep, args.steps, args.render)
    print(json.dumps(dict(seed=result['seed'], settling=result['settling'], counts=result['counts'])))


if __name__ == '__main__':
    main()
