#!/usr/bin/env python3
"""Headless, source-to-MuJoCo validation. Any failed invariant exits nonzero."""
import argparse
import json
from pathlib import Path
import time
import xml.etree.ElementTree as ET
import numpy as np
import mujoco
from scene_loader import load, reset, step

S = np.array([[1.,0,0],[0,0,1.],[0,1.,0]])


def vec(p): return np.array([p['x'],p['y'],p['z']])


def rot(q):
    # Independent reference matrix via MuJoCo's quaternion implementation.
    out=np.zeros(9)
    quaternion=np.array([q['w'],q['x'],q['y'],q['z']],dtype=float)
    quaternion/=np.linalg.norm(quaternion)
    mujoco.mju_quat2Mat(out,quaternion)
    return out.reshape(3,3)


def near(a,b,atol=2e-6):
    np.testing.assert_allclose(a,b,atol=atol,rtol=2e-6)


def surface_height(vertices,faces,xy):
    triangle=vertices[faces]
    a=triangle[:,0,:2]; ab=triangle[:,1,:2]-a; ac=triangle[:,2,:2]-a
    det=ab[:,0]*ac[:,1]-ab[:,1]*ac[:,0]
    good=np.abs(det)>1e-12
    ap=np.asarray(xy)-a
    u=np.divide(ap[:,0]*ac[:,1]-ap[:,1]*ac[:,0],det,out=np.zeros(len(det)),where=good)
    v=np.divide(ab[:,0]*ap[:,1]-ab[:,1]*ap[:,0],det,out=np.zeros(len(det)),where=good)
    ix=np.where(good & (u>=-1e-8) & (v>=-1e-8) & (u+v<=1+1e-8))[0]
    if len(ix)==0: raise AssertionError('Point outside source triangle surface')
    i=ix[0]
    return triangle[i,0,2]+u[i]*(triangle[i,1,2]-triangle[i,0,2])+v[i]*(triangle[i,2,2]-triangle[i,0,2])


def validate(directory, steps=1000, render=False):
    started=time.monotonic();directory=Path(directory)
    src=json.loads((directory/'unity_snapshot.json').read_text())
    manifest=json.loads((directory/'manifest.json').read_text())
    model,data,drag=load(directory)
    assert model.nu==0, 'Exporter must not invent a training/controller policy'
    assert model.nq>0
    bodies={b['id']:b for b in src['bodies']}
    meshes={m['id']:m for m in src['meshes']}
    active=[g for g in src['colliders'] if g['active'] and g['enabled']]
    assert set(manifest['colliders'])=={g['id'] for g in active}
    assert set(manifest['visuals'])=={v['id'] for v in src['visuals']}
    assert model.ncam==len(src['cameras'])
    assert not src['unsupported']
    linear_jac=np.zeros((3,model.nv));angular_jac=np.zeros((3,model.nv))
    for key,entry in manifest['bodies'].items():
        b=bodies[key];bid=model.body(key).id
        position=S@vec(b['position']); orientation=S@rot(b['rotation'])@S.T
        near(data.xpos[bid],position);near(data.xmat[bid].reshape(3,3),orientation)
        if b['hasRigidbody']:
            near(model.body_mass[bid],b['mass'])
            near(data.xipos[bid],S@vec(b['worldCenterOfMass']))
            iu=rot(b['inertiaTensorRotation'])@np.diag(vec(b['inertiaTensor']))@rot(b['inertiaTensorRotation']).T
            expected=orientation@(S@iu@S.T)@orientation.T
            ir=data.ximat[bid].reshape(3,3)
            near(ir@np.diag(model.body_inertia[bid])@ir.T,expected)
            near(model.body_gravcomp[bid],0 if b['useGravity'] else 1)
        if entry['mode']=='dynamic':
            assert model.body_jntnum[bid]==1
            mujoco.mj_jacBodyCom(model,data,linear_jac,angular_jac,bid)
            near(linear_jac@data.qvel,S@vec(b['velocity']))
            near(angular_jac@data.qvel,-S@vec(b['angularVelocity']))
            if b['sleeping']:
                assert data.body_awake[bid]==0, 'Source sleeping state not restored: '+key
        elif entry['mode']=='kinematic':
            assert model.body_mocapid[bid]>=0 and model.body_jntnum[bid]==0
        else:
            assert model.body_jntnum[bid]==0
    for g in active:
        mapping=manifest['colliders'][g['id']];b=bodies[g['body']]
        orientation=S@rot(b['rotation'])@S.T; position=S@vec(b['position'])
        collision=not g['trigger'] and (not b['hasRigidbody'] or b['detectCollisions'])
        if mapping['kind']=='geom':
            gid=model.geom(g['id']).id
            assert int(model.geom_contype[gid])!=0 if collision else int(model.geom_contype[gid])==0
            near(model.geom_friction[gid,0],g['dynamicFriction'])
            if g['kind'] in ('BoxCollider','SphereCollider','CapsuleCollider'):
                near(data.geom_xpos[gid],position+orientation@(S@vec(g['center'])))
            if g['kind']=='BoxCollider':near(model.geom_size[gid],S@vec(g['size'])/2)
            if g['kind'] in ('SphereCollider','CapsuleCollider'):near(model.geom_size[gid,0],g['radius'])
            if g['kind']=='CapsuleCollider':
                axis=orientation@S@rot(g['rotation'])[:,g['direction']]
                actual=data.geom_xmat[gid].reshape(3,3)[:,2]
                near(abs(np.dot(actual,axis)),1)
                near(model.geom_size[gid,1],max(0,g['height']/2-g['radius']))
        else:
            fid=mujoco.mj_name2id(model,mujoco.mjtObj.mjOBJ_FLEX,g['id'])
            adr=model.flex_vertadr[fid];count=model.flex_vertnum[fid]
            assert model.flex_rigid[fid]
            assert model.flex_elemnum[fid]==mapping['triangles']
            actual=data.flexvert_xpos[adr:adr+count]
            if mapping.get('terrain'):
                reference=np.load(directory/'terrain_reference.npz')
                expected=reference['vertices']
            else:
                raw=np.array([[v['x'],v['y'],v['z']] for v in meshes[g['mesh']]['vertices']])
                transformed=raw@S.T
                _,indices=np.unique(np.round(transformed,8),axis=0,return_index=True)
                expected=transformed[indices]@orientation.T+position
            near(actual,expected)
    # Compiled visual meshes are internally re-centered and rotated. Compare their world vertices.
    checked_vertices=0
    for visual in src['visuals']:
        entry=manifest['visuals'][visual['id']]
        if entry['kind']!='geom':continue
        gid=model.geom(visual['id']).id;mid=model.geom_dataid[gid]
        adr=model.mesh_vertadr[mid];n=model.mesh_vertnum[mid]
        actual=model.mesh_vert[adr:adr+n]@data.geom_xmat[gid].reshape(3,3).T+data.geom_xpos[gid]
        b=bodies[visual['body']];r=S@rot(b['rotation'])@S.T
        raw=np.array([[v['x'],v['y'],v['z']] for v in meshes[visual['mesh']]['vertices']])
        expected=raw@S.T@r.T+S@vec(b['position'])
        near(actual.min(0),expected.min(0),atol=3e-5);near(actual.max(0),expected.max(0),atol=3e-5)
        for v in expected[np.linspace(0,len(expected)-1,min(5,len(expected)),dtype=int)]:
            assert np.min(np.linalg.norm(actual-v,axis=1))<3e-5
            checked_vertices+=1
    terrain_checks=0
    if (directory/'terrain_reference.npz').exists():
        reference=np.load(directory/'terrain_reference.npz')
        vertices,faces=reference['vertices'],reference['faces']
        near(vertices[:,:2].min(0),reference['lower']);near(vertices[:,:2].max(0),reference['upper'])
        terrain_key=next(k for k,v in manifest['colliders'].items() if v.get('terrain'))
        fid=mujoco.mj_name2id(model,mujoco.mjtObj.mjOBJ_FLEX,terrain_key)
        # Every clipped triangle centroid must lie on the ORIGINAL Unity surface.
        for p in vertices[faces].mean(axis=1):
            h=surface_height(reference['source_vertices'],reference['source_faces'],p[:2])
            near(p[2],h,atol=2e-6)
            distance=mujoco.mj_rayFlex(model,data,0,False,False,True,False,fid,
                                     np.array([p[0],p[1],h+2]),np.array([0.,0.,-1.]))
            near(distance,2,atol=max(5e-5,manifest['shell_radius']*2))
            terrain_checks+=1
    qpos=data.qpos.copy();qvel=data.qvel.copy();mpos=data.mocap_pos.copy();mq=data.mocap_quat.copy()
    # Verify reset is truly repeatable even after an arbitrary changed state.
    data.qvel[:]=.1;data.time+=1
    reset(model,data);near(data.qpos,qpos);near(data.qvel,qvel);near(data.mocap_pos,mpos);near(data.mocap_quat,mq)
    near(data.time,src['time'])
    # Validate the compiled portable binary as well as the editable XML.
    binary=mujoco.MjModel.from_binary_path(str(directory/'scene.mjb'))
    assert (binary.nq,binary.nv,binary.ngeom,binary.nflex)==(model.nq,model.nv,model.ngeom,model.nflex)
    state=np.load(directory/'initial_state.npz');near(state['qpos'],qpos);near(state['qvel'],qvel)
    # Sleeping snapshots alone don't exercise collisions. Wake everything for a real physics soak.
    model.opt.enableflags &= ~int(mujoco.mjtEnableBit.mjENBL_SLEEP)
    mujoco.mj_forward(model,data)
    max_contacts=data.ncon;max_speed=0.;initial_time=data.time
    dynamic_ids=[model.body(k).id for k,v in manifest['bodies'].items() if v['mode']=='dynamic']
    for _ in range(steps):
        step(model,data,drag)
        assert np.all(np.isfinite(data.qpos)) and np.all(np.isfinite(data.qvel))
        assert np.all(data.warning.number==0), 'MuJoCo reported instability'
        max_contacts=max(max_contacts,data.ncon);max_speed=max(max_speed,float(np.max(np.abs(data.qvel))))
    near(data.time,initial_time+steps*model.opt.timestep,atol=1e-7)
    if (directory/'terrain_reference.npz').exists() and steps:
        minimum=float(reference['vertices'][:,2].min())
        assert np.min(data.xipos[dynamic_ids,2])>minimum-0.5, 'A rigid body fell below the terrain'
    assert max_speed<100, 'Unstable scene velocity'
    assert max_contacts>0 if steps else True
    # A downward probe on an independent slope verifies contact normals, not just ray geometry.
    contact_probe=validate_slope_contact()
    if render:
        reset(model,data)
        with mujoco.Renderer(model,height=360,width=640) as renderer:
            camera=mujoco.MjvCamera();mujoco.mjv_defaultCamera(camera)
            camera.lookat[:]=[0,0,.3];camera.distance=9;camera.azimuth=135;camera.elevation=-35
            renderer.update_scene(data,camera)
            rgb=renderer.render()
            assert rgb.std()>3, 'Offscreen render is blank'
            # PPM requires no pillow, GUI or graphics front end.
            (directory/'preview.ppm').write_bytes(b'P6\n640 360\n255\n'+rgb.tobytes())
    report=dict(status='passed',mujoco_version=mujoco.__version__,counts=manifest['counts'],
                source_bodies_checked=len(manifest['bodies']),source_colliders_checked=len(active),
                visual_vertex_probes=checked_vertices,terrain_triangle_rays=terrain_checks,
                mass_inertia_pose_velocity_sleep_checks='passed',keyframe_npz_binary_reset='passed',
                physics_steps=steps,physics_seconds=steps*model.opt.timestep,max_contacts=int(max_contacts),
                max_abs_qvel=max_speed,solver_warnings=data.warning.number.tolist(),slope_contact_probe=contact_probe,
                headless=True,offscreen_render=render,wall_seconds=time.monotonic()-started)
    (directory/'validation.json').write_text(json.dumps(report,indent=2)+'\n')
    return report


def validate_slope_contact():
    xml='''<mujoco><option timestep=".001"/><worldbody><body name="slope"/>
    <body pos="0 0 .3"><freejoint/><geom type="sphere" size=".05" mass="1"/></body></worldbody>
    <deformable><flex name="surface" body="slope" dim="2" radius=".00001"
    vertex="-1 -1 -.2 1 -1 .2 1 1 .2 -1 1 -.2" element="0 1 2 0 2 3">
    <contact friction="1 0 0" selfcollide="none"/></flex></deformable></mujoco>'''
    model=mujoco.MjModel.from_xml_string(xml);data=mujoco.MjData(model)
    contact=False
    for _ in range(700):
        mujoco.mj_step(model,data);contact|=data.ncon>0
    assert contact and data.qpos[2]>.2*data.qpos[0]+.035
    assert not np.any(data.warning.number)
    return 'passed'


def main():
    p=argparse.ArgumentParser(description=__doc__);p.add_argument('directory',type=Path)
    p.add_argument('--steps',type=int,default=1000);p.add_argument('--render',action='store_true')
    a=p.parse_args();print(json.dumps(validate(a.directory,a.steps,a.render),indent=2))


if __name__=='__main__':main()
