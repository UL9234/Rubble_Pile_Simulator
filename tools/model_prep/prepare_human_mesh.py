#!/usr/bin/env python3
"""Prepare the RubbleSim victim mesh (MakeHuman base body) as a project asset.

Two deterministic, documented transforms are applied to the upstream MakeHuman base mesh:

  1. Keep only the visible `body` geometry. The upstream file also carries MakeHuman's editing
     helpers and joint locators (helper-genital, helper-hair, eyes, eyelashes, teeth, tongue,
     helper-tights and joint-* spheres). The genital helper in particular is not wanted, and none of
     them are part of the body.
  2. Re-pose the arms alongside the body. The upstream base is in an A-pose with the arms hanging
     ~49 degrees out from the shoulder, so the hands stick out sideways. As a buried victim that is
     both wrong looking and physically wrong: Unity would collide the debris against the hands and
     the dummy would prop up a void under the rubble. The arms are rotated down about the shoulder
     (about Z, with a smooth falloff along the arm) so they lie along the torso.

Usage:
    python3 tools/model_prep/prepare_human_mesh.py \
        --in /path/to/makehuman/data/3dobjs/base.obj \
        --out Assets/MITLL/Models/Human/human-neutral.obj \
        --arm-deg 46

If --in does not exist it is downloaded from the MakeHuman repository (CC0 assets).
"""

import argparse
import math
import os
import urllib.request

UPSTREAM = ("https://raw.githubusercontent.com/makehumancommunity/makehuman/"
            "master/makehuman/data/3dobjs/base.obj")


def load_obj(path):
    verts, vts, vns, faces = [], [], [], []   # faces: (group, [token, ...])
    group = None
    for line in open(path, encoding="utf-8", errors="replace"):
        if line.startswith("v "):
            verts.append(tuple(float(x) for x in line.split()[1:4]))
        elif line.startswith("vt "):
            vts.append(line)
        elif line.startswith("vn "):
            vns.append(line)
        elif line.startswith(("g ", "o ")):
            group = line.split(None, 1)[1].strip() if len(line.split(None, 1)) > 1 else ""
        elif line.startswith("f "):
            faces.append((group, line.split()[1:]))
    return verts, vts, vns, faces


def keep_body(verts, vts, vns, faces):
    """Keep the `body` group only and drop now-unused vertices/UVs."""
    body = [(g, f) for g, f in faces if g == "body"]
    used_v, used_vt, used_vn = set(), set(), set()
    for _, f in body:
        for tok in f:
            p = tok.split("/")
            used_v.add(int(p[0]))
            if len(p) > 1 and p[1]:
                used_vt.add(int(p[1]))
            if len(p) > 2 and p[2]:
                used_vn.add(int(p[2]))
    mv = {o: i + 1 for i, o in enumerate(sorted(used_v))}
    mvt = {o: i + 1 for i, o in enumerate(sorted(used_vt))}
    mvn = {o: i + 1 for i, o in enumerate(sorted(used_vn))}
    out_v = [(i, verts[i - 1]) for i in sorted(used_v)]
    out_vt = [(i, vts[i - 1]) for i in sorted(used_vt)]
    out_vn = [(i, vns[i - 1]) for i in sorted(used_vn)]
    out_f = []
    for _, f in body:
        toks = []
        for tok in f:
            p = tok.split("/")
            s = str(mv[int(p[0])])
            if len(p) > 1:
                s += "/" + (str(mvt[int(p[1])]) if p[1] else "")
            if len(p) > 2 and p[2]:
                s += "/" + str(mvn[int(p[2])])
            toks.append(s)
        out_f.append(toks)
    return [v for _, v in out_v], out_vt, out_vn, out_f


def _cross(a, b):
    return (a[1] * b[2] - a[2] * b[1], a[2] * b[0] - a[0] * b[2], a[0] * b[1] - a[1] * b[0])


def _dot(a, b):
    return a[0] * b[0] + a[1] * b[1] + a[2] * b[2]


def _norm(a):
    n = math.sqrt(_dot(a, a))
    return (a[0] / n, a[1] / n, a[2] / n)


def _sub(a, b):
    return (a[0] - b[0], a[1] - b[1], a[2] - b[2])


def _rotate(p, axis, angle):
    """Rodrigues rotation of p about the unit `axis`."""
    c, s = math.cos(angle), math.sin(angle)
    cr = _cross(axis, p)
    d = _dot(axis, p)
    return tuple(p[i] * c + cr[i] * s + axis[i] * d * (1 - c) for i in range(3))


def repose_arms(verts, arm_out_deg, arm_back, torso_x, y_lo, y_hi, radius):
    """Swing each arm from the A-pose to lying flat alongside the torso.

    The upstream A-pose has the arms out (X), down (Y) *and forward* (Z). Only rotating about Z
    would leave the hands 27 cm in front of the body, i.e. raised in the air once the body lies on
    its back - the debris would be propped up by the hands. So the arm is rotated as a rigid chain
    from its measured 3D direction to a target direction that points down the body, slightly out and
    slightly back (towards the back plane, which is the ground when supine).
    """
    verts = list(verts)
    info = []
    for sign in (-1, 1):
        cand = [v for v in verts if sign * v[0] > torso_x and y_lo < v[1] < y_hi]
        if len(cand) < 50:
            raise RuntimeError("arm cluster not found on side %+d" % sign)
        inner = min(cand, key=lambda v: sign * v[0])
        outer = max(cand, key=lambda v: sign * v[0])
        a = _norm(_sub(outer, inner))                     # current arm direction (3D)
        t = _norm((sign * math.sin(math.radians(arm_out_deg)),
                   -math.cos(math.radians(arm_out_deg)),
                   -arm_back))                            # target: down, slightly out and back
        axis = _cross(a, t)
        sn = math.sqrt(_dot(axis, axis))
        if sn < 1e-9:
            info.append((sign, inner, outer, a, t, 0))
            continue
        axis = tuple(c / sn for c in axis)
        angle = math.atan2(sn, _dot(a, t))

        pivot = tuple(inner[i] - a[i] * 0.6 for i in range(3))   # just above the real shoulder
        span = math.sqrt(_dot(_sub(outer, pivot), _sub(outer, pivot)))

        n_moved = 0
        for i, v in enumerate(verts):
            # Only geometry laterally outside the torso can be arm. This per-vertex test is essential:
            # the sanity radius is deliberately generous (the arm is bent, so a tight cylinder misses
            # the upper arm), and without this test the cylinder reaches into the chest and deforms it.
            if sign * v[0] <= torso_x:
                continue
            r = _sub(v, pivot)
            s = _dot(r, a) / span
            if s <= -0.10 or s >= 1.25:
                continue
            perp = math.sqrt(max(0.0, _dot(r, r) - _dot(r, a) ** 2))
            if perp > radius:
                continue
            w = min(1.0, max(0.0, (s - 0.12) / 0.38))        # 0 at the shoulder, 1 along the arm
            w = w * w * (3 - 2 * w)                          # smoothstep
            verts[i] = tuple(pivot[j] + _rotate(r, axis, angle * w)[j] for j in range(3))
            n_moved += 1
        info.append((sign, inner, outer, a, t, n_moved))
    return verts, info


def write_obj(path, verts, vts, vns, faces, header):
    with open(path, "w", encoding="utf-8") as w:
        for line in header:
            w.write("# " + line + "\n")
        for x, y, z in verts:
            w.write("v %.6f %.6f %.6f\n" % (x, y, z))
        for _, line in vts:
            w.write(line)
        for _, line in vns:
            w.write(line)
        w.write("g body\n")
        for f in faces:
            w.write("f " + " ".join(f) + "\n")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--in", dest="src", default="/tmp/mh_base.obj")
    ap.add_argument("--out", dest="dst",
                    default="Assets/MITLL/Models/Human/human-neutral.obj")
    ap.add_argument("--arm-out-deg", type=float, default=8.0,
                    help="final angle of each arm from vertical, opened outwards (degrees)")
    ap.add_argument("--arm-back", type=float, default=0.12,
                    help="how far back (Z) the arms end up, as a fraction of arm length; the back is "
                         "the ground when the body lies supine")
    ap.add_argument("--torso-x", type=float, default=2.2,
                    help="|X| beyond which vertices can belong to an arm (decimetres)")
    ap.add_argument("--y-lo", type=float, default=0.8)
    ap.add_argument("--y-hi", type=float, default=5.2)
    ap.add_argument("--arm-radius", type=float, default=3.0,
                    help="sanity bound on the distance from the straight shoulder-hand axis; the arm "
                         "is bent (elbow well behind the hand) so a tight cylinder misses part of it")
    args = ap.parse_args()

    if not os.path.exists(args.src):
        print("downloading upstream mesh ->", args.src)
        urllib.request.urlretrieve(UPSTREAM, args.src)

    verts, vts, vns, faces = load_obj(args.src)
    verts, vts, vns, faces = keep_body(verts, vts, vns, faces)
    print("body: %d vertices, %d faces" % (len(verts), len(faces)))

    verts, info = repose_arms(verts, args.arm_out_deg, args.arm_back, args.torso_x, args.y_lo,
                              args.y_hi, args.arm_radius)
    for sign, inner, outer, a, t, n in info:
        print("side %+d: inner=(%.2f,%.2f,%.2f) outer=(%.2f,%.2f,%.2f)" % ((sign,) + inner + outer))
        print("        dir (%.2f,%.2f,%.2f) -> (%.2f,%.2f,%.2f), moved %d verts" % (a + t + (n,)))

    os.makedirs(os.path.dirname(args.dst) or ".", exist_ok=True)
    write_obj(args.dst, verts, vts, vns, faces, [
        "RubbleSim victim mesh - derive with tools/model_prep/prepare_human_mesh.py",
        "source: MakeHuman base.obj (CC0 1.0), body group only, arms re-posed alongside the body",
    ])
    print("wrote", args.dst)


if __name__ == "__main__":
    main()
