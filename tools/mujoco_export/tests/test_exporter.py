"""Regression tests for handedness, COM velocities, principal inertia and sparse terrain clipping."""
import copy
import json
from pathlib import Path
import sys
import tempfile
import unittest
import numpy as np
import mujoco

sys.path.insert(0,str(Path(__file__).resolve().parents[1]/'scripts'))
from convert_scene import convert, clip_mesh, source_state, S
from validate_scene import validate, surface_height
from scene_loader import load, reset, step


def v(x=0,y=0,z=0):return dict(x=x,y=y,z=z)
def q(x=0,y=0,z=0,w=1):return dict(x=x,y=y,z=z,w=w)


def source():
    body=dict(id='b0',path='dynamic',position=v(0,3,0),rotation=q(0,0,np.sin(.2),np.cos(.2)),
              scale=v(1,1,1),active=True,hasRigidbody=True,isKinematic=False,useGravity=True,
              detectCollisions=True,sleeping=False,mass=3.5,drag=.2,angularDrag=.4,
              worldCenterOfMass=v(.13,3.21,-.08),inertiaTensor=v(.7,.9,1.1),
              inertiaTensorRotation=q(np.sin(.31),0,0,np.cos(.31)),velocity=v(.4,-.3,.2),
              angularVelocity=v(.7,.1,-.5),constraints=0)
    terrain=dict(id='ground',path='CoarsePerlinGround[0]',position=v(),rotation=q(),
                 scale=v(1,1,1),active=True,hasRigidbody=False)
    kinematic=copy.deepcopy(body);kinematic.update(id='robot',path='kinematic',isKinematic=True,
        position=v(4,3,4),worldCenterOfMass=v(4,3,4),useGravity=False)
    material=dict(enabled=True,active=True,trigger=False,convex=False,layer=0,direction=1,
                  center=v(),size=v(1,2,3),scale=v(1,1,1),rotation=q(),radius=.2,height=1.,
                  staticFriction=.7,dynamicFriction=.6,bounciness=0,contactOffset=.01,frictionCombine=0,bounceCombine=0)
    sphere=dict(material,id='sphere',path='dynamic',body='b0',kind='SphereCollider')
    capsule=dict(material,id='capsule',path='kinematic',body='robot',kind='CapsuleCollider',direction=2,
                 rotation=q(0,np.sin(.23),0,np.cos(.23)))
    floor=dict(material,id='floor',path='CoarsePerlinGround[0]',body='ground',kind='MeshCollider',mesh='terrain')
    mesh=dict(id='terrain',vertices=[v(-10,-1,-10),v(-10,-1,10),v(10,1,-10),v(10,1,10)],triangles=[0,1,2,2,1,3])
    return dict(schemaVersion=1,seed=77,unityVersion='test',scene='test',capturePhase='test',time=2.5,
                gravity=v(0,-9.81,0),generationCenter=v(),generationSize=v(3.5,10,5.5),
                fixedDeltaTime=.02,layerCollisionMasks=[-1]*32,unsupported=[],ignoredPairs=[],nodes=[],
                bodies=[body,terrain,kinematic],colliders=[sphere,capsule,floor],meshes=[mesh],visuals=[],cameras=[])


class ExporterTests(unittest.TestCase):
    def test_axial_handedness(self):
        b=source()['bodies'][0];b['rotation']=q();b['worldCenterOfMass']=b['position']
        qp,qv=source_state(b)
        np.testing.assert_allclose(qv[:3],[.4,.2,-.3])
        np.testing.assert_allclose(qv[3:],[-.7,.5,-.1])
        np.testing.assert_allclose(qp[:3],[0,0,3])

    def test_nonzero_com_velocity_inertia_and_mocap(self):
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp);src=p/'input.json';src.write_text(json.dumps(source()))
            manifest=convert(src,p/'result')
            report=validate(p/'result',steps=0)
            self.assertEqual(report['status'],'passed')
            self.assertEqual(manifest['counts']['kinematic_bodies'],1)
            model,data,drag=load(p/'result')
            force=np.zeros_like(data.xfrc_applied);force[model.body('b0').id,0]=.25
            data.xfrc_applied[:]=force
            step(model,data,drag,3)
            np.testing.assert_array_equal(data.xfrc_applied,force)
            reset(model,data);self.assertAlmostEqual(data.time,2.5)

    def test_crop_preserves_existing_triangle_planes(self):
        verts=np.array([[-2,-2,0],[-2,2,1],[2,-2,-1],[2,2,2.]])
        faces=np.array([[0,2,1],[2,3,1]])
        clipped,indices=clip_mesh(verts,faces,[-.7,-1.2],[1.1,.6])
        np.testing.assert_allclose(clipped[:,:2].min(0),[-.7,-1.2])
        np.testing.assert_allclose(clipped[:,:2].max(0),[1.1,.6])
        for p in clipped[indices].mean(1):self.assertAlmostEqual(p[2],surface_height(verts,faces,p[:2]))

    def test_empty_crop_fails(self):
        with self.assertRaises(ValueError):clip_mesh(np.eye(3),np.array([[0,1,2]]),[10,10],[11,11])

    def test_unsupported_physics_is_never_silently_dropped(self):
        for field,value in [('unsupported',['HingeJoint']),('ignoredPairs',[dict(first='a',second='b')])]:
            with self.subTest(field=field),tempfile.TemporaryDirectory() as tmp:
                p=Path(tmp);s=source();s[field]=value;(p/'input.json').write_text(json.dumps(s))
                with self.assertRaises(ValueError):convert(p/'input.json',p/'result')

    def test_axis_constraints_fail_explicitly(self):
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp);s=source();s['bodies'][0]['constraints']=2;(p/'input.json').write_text(json.dumps(s))
            with self.assertRaisesRegex(ValueError,'constraints'):convert(p/'input.json',p/'result')

    def test_disabled_and_trigger_colliders(self):
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp);s=source()
            trigger=copy.deepcopy(s['colliders'][0]);trigger.update(id='trigger',trigger=True)
            disabled=copy.deepcopy(trigger);disabled.update(id='disabled',enabled=False)
            s['colliders'].extend([trigger,disabled]);(p/'input.json').write_text(json.dumps(s))
            manifest=convert(p/'input.json',p/'result');m,d,_=load(p/'result')
            self.assertEqual(m.geom('trigger').contype[0],0)
            self.assertNotIn('disabled',manifest['colliders'])
            self.assertIn('disabled',manifest['inactive_colliders'])

    def test_scattered_body_keeps_supporting_terrain(self):
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp);s=source();s['bodies'][0].update(position=v(6,3,0),worldCenterOfMass=v(6,3,0))
            (p/'input.json').write_text(json.dumps(s));manifest=convert(p/'input.json',p/'result')
            self.assertTrue(manifest['terrain_extended_for_scattered_bodies'])
            self.assertGreaterEqual(manifest['terrain_actual_bounds'][1][0],6.45)

    def test_pending_force_and_layer_override_fail_explicitly(self):
        for field,value in [('accumulatedForce',v(1,0,0)),('includeLayers',2)]:
            with self.subTest(field=field),tempfile.TemporaryDirectory() as tmp:
                p=Path(tmp);s=source();s['bodies'][0][field]=value
                (p/'input.json').write_text(json.dumps(s))
                with self.assertRaises(ValueError):convert(p/'input.json',p/'result')

    def test_sleeping_snapshot_restored(self):
        with tempfile.TemporaryDirectory() as tmp:
            p=Path(tmp);s=source();b=s['bodies'][0]
            b.update(sleeping=True,velocity=v(),angularVelocity=v(),position=v(0,.2,0),worldCenterOfMass=v(0,.2,0))
            (p/'input.json').write_text(json.dumps(s));convert(p/'input.json',p/'result')
            m,d,drag=load(p/'result');self.assertEqual(d.body_awake[m.body('b0').id],0)
            pose=d.qpos.copy();step(m,d,drag,5);np.testing.assert_array_equal(d.qpos,pose)


if __name__=='__main__':unittest.main(verbosity=2)
