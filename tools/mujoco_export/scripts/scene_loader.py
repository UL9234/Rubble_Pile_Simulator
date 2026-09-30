"""Portable reset/physics adapter for an exported scene; no RL environment or training code.

load() restores the exported keyframe. step() adds source-body viscous drag before
MuJoCo integration. Using bare mj_step is supported but omits that drag.
"""
import json
from pathlib import Path
import numpy as np
import mujoco


def load(directory=None):
    directory = Path(directory) if directory else Path(__file__).resolve().parent
    model = mujoco.MjModel.from_xml_path(str(directory/'scene.xml'))
    data = mujoco.MjData(model)
    reset(model, data)
    source = json.loads((directory/'unity_snapshot.json').read_text())
    manifest = json.loads((directory/'manifest.json').read_text())
    drag = []
    for body in source['bodies']:
        entry = manifest['bodies'].get(body['id'])
        if entry and entry['mode'] == 'dynamic':
            drag.append((model.body(body['id']).id, body['mass'], body['drag'], body['angularDrag']))
    return model, data, drag


def reset(model, data):
    mujoco.mj_resetDataKeyframe(model, data, model.key('unity_snapshot').id)
    mujoco.mj_forward(model, data)


def step(model, data, drag, nstep=1):
    """Apply isotropic source drag at the COM without overwriting caller forces.

Continuous viscous drag reproduces the coefficients, not PhysX's discrete update
rule. Pending source forces, solver warm starts and C# callbacks are not portable.
"""
    velocity = np.zeros(6)
    for _ in range(nstep):
        applied = data.xfrc_applied.copy()
        try:
            for bid, mass, linear, angular in drag:
                if not linear and not angular:
                    continue
                # mjOBJ_XBODY returns the body frame; mjOBJ_BODY returns its inertial frame.
                mujoco.mj_objectVelocity(model, data, mujoco.mjtObj.mjOBJ_BODY, bid, velocity, 0)
                data.xfrc_applied[bid, :3] -= mass * linear * velocity[3:]
                r = data.ximat[bid].reshape(3, 3)
                inertia = r @ np.diag(model.body_inertia[bid]) @ r.T
                data.xfrc_applied[bid, 3:] -= angular * (inertia @ velocity[:3])
            mujoco.mj_step(model, data)
        finally:
            data.xfrc_applied[:] = applied
