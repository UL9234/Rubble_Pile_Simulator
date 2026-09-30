#!/usr/bin/env python3
"""Convert an unmodified Unity physics snapshot to portable MJCF and a reset keyframe.

Only stdlib, NumPy and MuJoCo are required. All lengths are metres, masses kg,
velocities m/s or rad/s. Source values remain in unity_snapshot.json.
"""
import argparse
import json
import math
from pathlib import Path
import shutil
import xml.etree.ElementTree as ET

import numpy as np
import mujoco

# A reflection changes handedness. Polar vectors use S; axial vectors use -S.
S = np.array([[1., 0, 0], [0, 0, 1.], [0, 1., 0]])


def vector(value):
    return np.array([value[k] for k in ('x', 'y', 'z')], dtype=float)


def quat(value):
    q = np.array([value['w'], -value['x'], -value['z'], -value['y']], dtype=float)
    return q / np.linalg.norm(q)


def rotation(q):
    w, x, y, z = np.asarray(q) / np.linalg.norm(q)
    return np.array([[1-2*(y*y+z*z), 2*(x*y-z*w), 2*(x*z+y*w)],
                     [2*(x*y+z*w), 1-2*(x*x+z*z), 2*(y*z-x*w)],
                     [2*(x*z-y*w), 2*(y*z+x*w), 1-2*(x*x+y*y)]])


def matrix_quat(r):
    out = np.zeros(4)
    mujoco.mju_mat2Quat(out, np.ascontiguousarray(r).ravel())
    return out


def fmt(value):
    return ' '.join(format(float(x), '.12g') for x in np.asarray(value).ravel())


def xyz(value):
    return S @ vector(value)


def signed32(value):
    return (int(value) + 2**31) % 2**32 - 2**31


def mesh_arrays(mesh):
    vertices = np.array([[p['x'], p['z'], p['y']] for p in mesh['vertices']], dtype=float)
    triangles = np.array(mesh['triangles'], dtype=int).reshape(-1, 3)[:, [0, 2, 1]]
    return vertices, triangles


def clip_mesh(vertices, faces, lower, upper):
    """Clip existing planar triangles to XY bounds; no noise resampling or interpolation change."""
    result, triangles = [], []
    for tri in vertices[faces]:
        poly = list(tri)
        for axis, edge, sign in [(0, lower[0], 1), (0, upper[0], -1),
                                 (1, lower[1], 1), (1, upper[1], -1)]:
            clipped = []
            if not poly:
                break
            for a, b in zip(poly, poly[1:] + poly[:1]):
                da, db = sign*(a[axis]-edge), sign*(b[axis]-edge)
                if da >= -1e-10:
                    clipped.append(a)
                if (da >= 0) != (db >= 0):
                    clipped.append(a + (b-a)*da/(da-db))
            poly = clipped
        for j in range(1, len(poly)-1):
            tri = np.array([poly[0], poly[j], poly[j+1]])
            if np.linalg.norm(np.cross(tri[1]-tri[0], tri[2]-tri[0])) < 1e-12:
                continue
            index = len(result)
            result.extend(tri)
            triangles.append([index, index+1, index+2])
    if not triangles:
        raise ValueError('Terrain crop is empty')
    return weld(np.array(result), np.array(triangles))


def weld(vertices, triangles):
    # Rigid flexes need shared vertices, not the duplicated flat-shaded render vertices.
    _, index, inverse = np.unique(np.round(vertices, 8), axis=0, return_index=True, return_inverse=True)
    return vertices[index], inverse[triangles]


def write_obj(path, vertices, faces):
    with path.open('w') as file:
        for v in vertices:
            file.write('v ' + fmt(v) + '\n')
        for f in faces:
            file.write('f ' + ' '.join(str(int(i)+1) for i in f) + '\n')


def inertia_body(body):
    iq = body['inertiaTensorRotation']
    ru = rotation([iq['w'], iq['x'], iq['y'], iq['z']])
    return S @ ru @ np.diag(vector(body['inertiaTensor'])) @ ru.T @ S.T


def source_state(body):
    pos, q = xyz(body['position']), quat(body['rotation'])
    angular = -xyz(body['angularVelocity'])
    com = xyz(body['worldCenterOfMass'])
    origin_velocity = xyz(body['velocity']) - np.cross(angular, com-pos)
    return np.r_[pos, q], np.r_[origin_velocity, rotation(q).T @ angular]


def convert(snapshot_path, output, padding=2.0, timestep=0.002, shell_radius=0.00001):
    output = Path(output)
    output.mkdir(parents=True, exist_ok=True)
    snapshot_path = Path(snapshot_path)
    src = json.loads(snapshot_path.read_text())
    if src['schemaVersion'] != 1:
        raise ValueError('Unsupported snapshot schema')
    if src['unsupported']:
        raise ValueError('Unsupported scene components: ' + '; '.join(src['unsupported']))
    if src['ignoredPairs']:
        raise ValueError('Per-collider ignore pairs require a collision callback; refusing a lossy export')
    if not (padding >= 0 and 0 < timestep <= .02 and shell_radius >= 0):
        raise ValueError('Invalid crop/timestep/shell radius')
    if tuple(map(int, mujoco.__version__.split('.')[:2])) < (3, 13):
        raise RuntimeError('MuJoCo >= 3.13 required for snapshot sleep-state mapping')
    source_target = output/'unity_snapshot.json'
    if snapshot_path.resolve() != source_target.resolve():
        shutil.copy2(snapshot_path, source_target)
    meshes = {m['id']: m for m in src['meshes']}
    all_bodies = {b['id']: b for b in src['bodies']}
    active_colliders = [g for g in src['colliders'] if g['enabled'] and g['active']]
    used = {g['body'] for g in active_colliders} | {v['body'] for v in src['visuals']}
    used |= {c['body'] for c in src['cameras']}
    used |= {b['id'] for b in src['bodies'] if b['hasRigidbody'] and b.get('active', True)}
    bodies = {key: b for key, b in all_bodies.items() if key in used}
    for body in bodies.values():
        if body.get('includeLayers',0) or body.get('excludeLayers',0):
            raise ValueError('Rigidbody layer overrides require explicit mapping: '+body['path'])
        for force in ('accumulatedForce','accumulatedTorque'):
            if force in body and np.linalg.norm(vector(body[force])) > 1e-10:
                raise ValueError('Pending force/torque requires a one-shot force adapter: '+body['path'])
        if body['hasRigidbody'] and body['constraints']:
            raise ValueError(f"Rigidbody axis constraints not supported: {body['path']}")
    for g in active_colliders:
        if g.get('includeLayers',0) or g.get('excludeLayers',0):
            raise ValueError('Collider layer overrides require explicit mapping: '+g['path'])
    center = xyz(src['generationCenter'])
    half = xyz(src['generationSize'])[:2]/2 + padding
    requested_bounds = (center[:2]-half, center[:2]+half)
    bounds = [requested_bounds[0].copy(), requested_bounds[1].copy()]
    # Some random collapses scatter outside the spawn rectangle. Keep those bodies AND
    # their supporting ground; never clip away the floor underneath an exported body.
    for g in active_colliders:
        body = bodies[g['body']]
        if not body['hasRigidbody'] or body['isKinematic'] or g['trigger']:
            continue
        r = rotation(quat(body['rotation'])); origin = xyz(body['position'])
        if g['kind'] == 'MeshCollider':
            points, _ = mesh_arrays(meshes[g['mesh']])
            points = points @ r.T + origin
            lower, upper = points[:,:2].min(0), points[:,:2].max(0)
        else:
            center_geom = origin + r @ xyz(g['center'])
            gr = r @ rotation(quat(g['rotation']))
            if g['kind'] == 'BoxCollider':
                extent = np.abs(gr) @ (xyz(g['size'])/2)
            elif g['kind'] == 'CapsuleCollider':
                axis = gr @ S[:,g['direction']]
                extent = np.abs(axis)*max(0,g['height']/2-g['radius']) + g['radius']
            else:
                extent = np.ones(3)*g['radius']
            lower, upper = (center_geom-extent)[:2], (center_geom+extent)[:2]
        bounds[0] = np.minimum(bounds[0],lower-.25)
        bounds[1] = np.maximum(bounds[1],upper+.25)
    root = ET.Element('mujoco', model=f"rubble_seed{src['seed']}")
    ET.SubElement(root, 'compiler', angle='radian', meshdir='assets', inertiafromgeom='false', fusestatic='false', autolimits='true')
    option = ET.SubElement(root, 'option', timestep=str(timestep), gravity=fmt(xyz(src['gravity'])),
                          integrator='implicitfast', solver='Newton', iterations='100', cone='elliptic', jacobian='sparse')
    # Solver warm starts are engine-private and cannot be imported from PhysX.
    ET.SubElement(option, 'flag', multiccd='enable', sleep='enable')
    ET.SubElement(root, 'size', memory='256M')
    asset = ET.SubElement(root, 'asset')
    world = ET.SubElement(root, 'worldbody')
    ET.SubElement(world, 'light', name='export_light', pos='0 0 12', dir='-.3 -.4 -1', directional='true')
    deformable = ET.SubElement(root, 'deformable')
    asset_dir = output/'assets'
    asset_dir.mkdir(exist_ok=True)
    nodes, body_map, collider_map, visual_map = {}, {}, {}, {}
    exceptions = [
        'PhysX and MuJoCo use different contact solvers: identical future trajectories are not claimed.',
        'Static/dynamic friction and PhysX combine modes are retained in the snapshot; MJCF uses dynamic sliding friction and native MuJoCo mixing.',
        'Bounciness, contact offset, sleep thresholds, CCD, solver iterations and maximum velocities are retained verbatim as source metadata; they have no exact cross-engine mapping.',
        'Rigid nonconvex mesh contacts use a %.9g m shell radius; triangle geometry is unchanged.' % shell_radius,
        'Mesh base colors are exported; Unity shaders, textures, particles, UI, ROS and C# controllers are listed in the snapshot but are not MuJoCo runtime components.',
        'Linear/angular drag is applied by scene_loader.step; bare mj_step has no PhysX drag callback.',
        'Kinematic bodies are mocap bodies; static colliders remain static; no bodies or actuators are invented.'
    ]
    for key, body in bodies.items():
        attrs = dict(name=key, pos=fmt(xyz(body['position'])), quat=fmt(quat(body['rotation'])))
        dynamic = body['hasRigidbody'] and not body['isKinematic']
        if dynamic:
            attrs['sleep'] = 'init' if body['sleeping'] else 'allowed'
        if body['hasRigidbody'] and body['isKinematic']:
            attrs['mocap'] = 'true'
        if body['hasRigidbody'] and not body['useGravity']:
            attrs['gravcomp'] = '1'
        node = ET.SubElement(world, 'body', **attrs)
        nodes[key] = node
        if dynamic:
            ET.SubElement(node, 'freejoint', name=key+'_free', align='false')
        if body['hasRigidbody']:
            tensor = inertia_body(body)
            if body['mass'] <= 0 or np.min(np.linalg.eigvalsh(tensor)) <= 0:
                raise ValueError('Invalid mass/inertia: '+body['path'])
            com = rotation(quat(body['rotation'])).T @ (xyz(body['worldCenterOfMass'])-xyz(body['position']))
            ET.SubElement(node, 'inertial', pos=fmt(com), mass=str(body['mass']),
                          quat=fmt(quat(body['inertiaTensorRotation'])), diaginertia=fmt(xyz(body['inertiaTensor'])))
        body_map[key] = dict(path=body['path'], mode='dynamic' if dynamic else 'kinematic' if body['hasRigidbody'] else 'static')

    def mesh_asset(name, vertices, faces):
        write_obj(asset_dir/(name+'.obj'), vertices, faces)
        ET.SubElement(asset, 'mesh', name=name, file=name+'.obj', inertia='shell')
        return name

    terrain_source, terrain_clipped = None, None
    for g in active_colliders:
        key = g['id']; body = bodies[g['body']]; node = nodes[g['body']]
        enabled = not g['trigger'] and (not body['hasRigidbody'] or body['detectCollisions'])
        ct = signed32(1 << g['layer']) if enabled else 0
        ca = src['layerCollisionMasks'][g['layer']] if enabled else 0
        common = dict(name=key, contype=str(ct), conaffinity=str(ca),
                      friction=fmt([g['dynamicFriction'], 0, 0]), condim='3',
                      solref='0.01 1', solimp='0.95 0.99 0.001', margin='0', gap='0')
        attrs = dict(common, group='3', rgba='0.5 0.5 0.5 0', mass='0')
        kind = g['kind']
        if kind in ('BoxCollider', 'SphereCollider', 'CapsuleCollider'):
            attrs['pos'] = fmt(xyz(g['center']))
            attrs['quat'] = fmt(quat(g['rotation']))
            if kind == 'BoxCollider':
                sizes = xyz(g['size'])/2
                if np.any(sizes <= 1e-9):
                    raise ValueError('Degenerate box collider: '+g['path'])
                attrs.update(type='box', size=fmt(sizes))
            elif kind == 'SphereCollider':
                attrs.update(type='sphere', size=str(g['radius']))
            else:
                axis = rotation(quat(g['rotation'])) @ S[:, g['direction']]
                origin = xyz(g['center'])
                length = max(0, g['height']/2-g['radius'])
                attrs.pop('pos'); attrs.pop('quat')
                if length < 1e-10:
                    attrs.update(type='sphere', size=str(g['radius']), pos=fmt(origin))
                else:
                    attrs.update(type='capsule', size=str(g['radius']), fromto=fmt(np.r_[origin-axis*length, origin+axis*length]))
            ET.SubElement(node, 'geom', **attrs)
            collider_map[key] = dict(kind='geom', name=key, body=g['body'], source_kind=kind)
        elif kind == 'MeshCollider':
            vertices, faces = mesh_arrays(meshes[g['mesh']])
            terrain = 'CoarsePerlinGround[' in g['path']
            if terrain:
                # Clip in world space so translated/rotated terrain owners are handled correctly.
                r = rotation(quat(body['rotation'])); p = xyz(body['position'])
                world_vertices = vertices @ r.T + p
                terrain_source = (world_vertices, faces)
                cropped, faces = clip_mesh(world_vertices, faces, *bounds)
                terrain_clipped = (cropped, faces)
                vertices = (cropped-p) @ r
            if g['convex']:
                name = mesh_asset(key, vertices, faces)
                ET.SubElement(node, 'geom', **attrs, type='mesh', mesh=name)
                collider_map[key] = dict(kind='geom', name=key, body=g['body'], source_kind=kind, convex=True)
            else:
                vertices, faces = weld(vertices, faces)
                flex = ET.SubElement(deformable, 'flex', name=key, body=g['body'], dim='2', radius=str(shell_radius),
                                     vertex=fmt(vertices), element=' '.join(map(str, faces.ravel())),
                                     rgba='0.48 0.43 0.35 1' if terrain else '0.5 0.5 0.5 0', group='0' if terrain else '3')
                common.pop('name')
                ET.SubElement(flex, 'contact', **common, selfcollide='none', internal='false')
                collider_map[key] = dict(kind='flex', name=key, body=g['body'], source_kind=kind,
                                        vertices=len(vertices), triangles=len(faces), terrain=terrain)
        else:
            raise ValueError('Unsupported collider: '+kind)
    for visual in src['visuals']:
        if 'CoarsePerlinGround[' in visual['path']:
            visual_map[visual['id']] = dict(kind='terrain_flex', source_mesh=visual['mesh'])
            continue
        vertices, faces = mesh_arrays(meshes[visual['mesh']])
        key = visual['id']
        mesh_asset(key, vertices, faces)
        color = [visual['color'][k] for k in ('r','g','b','a')]
        ET.SubElement(nodes[visual['body']], 'geom', name=key, type='mesh', mesh=key,
                      contype='0', conaffinity='0', mass='0', group='1', rgba=fmt(color))
        visual_map[key] = dict(kind='geom', name=key, source_mesh=visual['mesh'])
    for camera in src['cameras']:
        # MuJoCo camera looks along -Z, +Y up. Build axes directly from Unity's view basis.
        r = rotation(quat(camera['rotation']))
        up = r @ S[:,1]; forward = r @ S[:,2]
        cam_r = np.column_stack((np.cross(up, -forward), up, -forward))
        if camera['orthographic']:
            raise ValueError('Orthographic camera requires explicit conversion: '+camera['path'])
        ET.SubElement(nodes[camera['body']], 'camera', name=camera['id'], pos=fmt(xyz(camera['position'])),
                      quat=fmt(matrix_quat(cam_r)), fovy=str(camera['fov']))
    ET.indent(root)
    model_path = output/'scene.xml'
    ET.ElementTree(root).write(model_path, encoding='utf-8', xml_declaration=True)
    model = mujoco.MjModel.from_xml_path(str(model_path))
    data = mujoco.MjData(model)
    for key, body in bodies.items():
        if body_map[key]['mode'] != 'dynamic':
            continue
        joint = model.joint(key+'_free')
        qpos, qvel = source_state(body)
        data.qpos[joint.qposadr[0]:joint.qposadr[0]+7] = qpos
        data.qvel[joint.dofadr[0]:joint.dofadr[0]+6] = qvel
    data.time = src['time']
    keyframes = ET.SubElement(root, 'keyframe')
    attrs = dict(name='unity_snapshot', time=str(src['time']), qpos=fmt(data.qpos), qvel=fmt(data.qvel))
    if model.nmocap:
        attrs.update(mpos=fmt(data.mocap_pos), mquat=fmt(data.mocap_quat))
    ET.SubElement(keyframes, 'key', **attrs)
    ET.indent(root)
    ET.ElementTree(root).write(model_path, encoding='utf-8', xml_declaration=True)
    model = mujoco.MjModel.from_xml_path(str(model_path))
    data = mujoco.MjData(model)
    mujoco.mj_resetDataKeyframe(model, data, model.key('unity_snapshot').id)
    mujoco.mj_forward(model, data)
    np.savez_compressed(output/'initial_state.npz', qpos=data.qpos, qvel=data.qvel,
                        time=np.array(data.time), mocap_pos=data.mocap_pos, mocap_quat=data.mocap_quat)
    mujoco.mj_saveModel(model, str(output/'scene.mjb'), None)
    if terrain_clipped is not None:
        np.savez_compressed(output/'terrain_reference.npz', source_vertices=terrain_source[0],
                            source_faces=terrain_source[1], vertices=terrain_clipped[0], faces=terrain_clipped[1],
                            lower=bounds[0], upper=bounds[1])
    manifest = dict(schema_version=1, seed=src['seed'], mujoco_version=mujoco.__version__,
                    capture_phase=src['capturePhase'], coordinate_transform='(x,y,z)_Unity -> (x,z,y)_MuJoCo; angular velocity -> (-x,-z,-y)',
                    terrain_padding=padding, terrain_requested_bounds=[b.tolist() for b in requested_bounds],
                    terrain_actual_bounds=[b.tolist() for b in bounds],
                    terrain_extended_for_scattered_bodies=any(np.any(np.abs(a-b)>1e-8) for a,b in zip(bounds,requested_bounds)),
                    timestep=timestep, shell_radius=shell_radius,
                    bodies=body_map, colliders=collider_map, visuals=visual_map,
                    inactive_colliders=[g['id'] for g in src['colliders'] if not(g['enabled'] and g['active'])],
                    counts=dict(bodies=model.nbody-1, dynamic_bodies=sum(b['mode']=='dynamic' for b in body_map.values()),
                                kinematic_bodies=model.nmocap, geoms=model.ngeom, flexes=model.nflex, nq=model.nq, nv=model.nv,
                                source_colliders=len(src['colliders']), exported_colliders=len(collider_map), source_visuals=len(src['visuals'])),
                    conversion_notes=exceptions)
    (output/'manifest.json').write_text(json.dumps(manifest, indent=2)+'\n')
    shutil.copy2(Path(__file__).with_name('scene_loader.py'), output/'scene_loader.py')
    return manifest


def main():
    parser=argparse.ArgumentParser(description=__doc__)
    parser.add_argument('snapshot', type=Path); parser.add_argument('output', type=Path)
    parser.add_argument('--padding', type=float, default=2.0)
    parser.add_argument('--timestep', type=float, default=.002)
    parser.add_argument('--shell-radius', type=float, default=.00001)
    args=parser.parse_args()
    print(json.dumps(convert(args.snapshot,args.output,args.padding,args.timestep,args.shell_radius)['counts'],indent=2))


if __name__=='__main__': main()
