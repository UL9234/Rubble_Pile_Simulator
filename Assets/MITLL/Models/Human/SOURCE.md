# Victim human model — source, license and edits

| item | value |
|---|---|
| Asset | **MakeHuman base mesh** (`base.obj`), the gender-neutral base body the MakeHuman project morphs into every character |
| Upstream file | [`makehuman/data/3dobjs/base.obj`](https://github.com/makehumancommunity/makehuman/blob/master/makehuman/data/3dobjs/base.obj) |
| Downloaded from | https://raw.githubusercontent.com/makehumancommunity/makehuman/master/makehuman/data/3dobjs/base.obj |
| License | **CC0 1.0 Universal** — the MakeHuman project releases all assets (base mesh, proxies, targets, textures) as CC0; see `LICENSE-CC0-MakeHuman.txt` (upstream `LICENSE.ASSETS.md`) |
| Local file | `human-neutral.obj` |

## Why this asset

The simulator needs a realistic, standard-proportioned human body that carries no obvious gender
characteristics and ships under a permissive licence. MakeHuman's `base.obj` is exactly that: a
neutral anatomical base body (same family of realistic human meshes as the CC0 "Antonia" figure that
was used first), and its assets are CC0.

## Edits applied to the downloaded file

1. Kept only the `body` geometry group. The upstream file also contains MakeHuman's editing helpers
   and joint markers (`helper-genital`, `helper-hair`, `helper-*-eye`, eyelashes, teeth, tongue,
   `helper-tights`, and `joint-*` locator spheres); none of them are part of the visible body and the
   genital helper in particular is not wanted in the demo.
2. Removed the now-unused vertices and UVs and re-indexed the faces (19158 -> 13380 vertices,
   13378 faces kept).
3. **Re-posed the arms alongside the body.** The upstream base is an A-pose whose arms reach out and
   *forward* (hand at Z = +2.7 dm). A buried victim in that pose is wrong twice over: it looks wrong,
   and physically the hands stick up and prop a void under the rubble instead of being buried. Each arm
   is measured (shoulder→hand direction) and rotated as a rigid chain to point down the body, slightly
   out (8°) and level with the back plane, so the arms rest flat on the ground when the body lies
   supine. Result: hand at (±2.80, 0.54, -0.82) instead of (±4.96, 1.24, +2.68); the arms' lowest point
   (-1.03) is level with the torso's back (-1.02). No other geometry is touched.
4. No scaling and no topology change; the mesh stays Y-up in decimetres.

Steps 1-3 are reproducible with `tools/model_prep/prepare_human_mesh.py` (see its `--help` for the
arm parameters). Nothing else was modified.

The model is Y-up, in decimetres, arms held slightly away from the body (MakeHuman's default pose).
`DebrisSpawner` normalises it at runtime (scaled to `-pancakevictimheight` metres, laid on its back)
so no fixed scale is baked into the file.

## Swapping in a different model

Drop another `.obj`/`.fbx` into this folder and point `VictimSetupEditor.ModelPath` at it, then run
`VictimSetupEditor.Wire` again (menu: RubbleSim ▸ Wire Victim Into Debris Spawner). The runtime
normalises any humanoid by its bounding box, so no manual scaling is needed.
