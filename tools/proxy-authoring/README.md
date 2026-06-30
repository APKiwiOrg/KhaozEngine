# Building collision-proxy authoring (Blender headless)

Companion tooling for the collision-proxy bake pipeline (see `docs/PHYSICS-PIPELINE.md` and
`KhaozEngine.Render3D/Models/PropCollisionBake.cs`). A building collides as a SEPARATE simplified proxy: a
`CompoundShape` of convex boxes/wedges (solid walls, floor, stairs as a ramp, standable furniture), with thin
decoration (roof, eaves, dormers, windows, trim) dropped. Every collision solid is convex, so the capsule can
never wedge in cluttered detail.

These are Blender `bpy` scripts run headless (`blender --background --python <script> -- <args>`), so authoring is
deterministic and reproducible (a committed box spec re-bakes the same proxy). Requires a local Blender (5.x used).

## Workflow (per building)

1. **Analyze** the render `.glb` to get overall + per-material bounds and three orthographic renders:
   `blender -b --python analyze_building.py -- <building>.glb <outdir>`
2. **See the interior** (roof occludes top-down) by re-rendering with roof/window materials removed:
   `blender -b --python render_roofless.py -- <building>.glb <outdir> RoofTiles_Red,Windows`
   (`loose_parts.py` dumps per-connected-component bounds when you need precise part extents.)
3. **Author** a box/wedge spec JSON in the building's Blender frame (Z up), enveloping the substantial masses and
   dropping decoration. See `examples/blacksmith_spec.json`. Schema:
   ```json
   { "boxes":  [ { "name": "...", "min": [x,y,z], "max": [x,y,z] } ],
     "wedges": [ { "name": "...", "min": [x,y,z], "max": [x,y,z], "axis": "x|y", "dir": 1 } ] }
   ```
   A wedge is a right-triangular prism (a stair ramp), the sloped face rising along `axis` in `dir`.
4. **Build + verify** the proxy GLB and x-ray overlay renders (red proxy over the building):
   `blender -b --python build_proxy.py -- <building>.glb <spec>.json <out>_collision.glb <overlaydir>`
   Inspect `ov_top/front/right/persp.png`; adjust the spec until the proxy covers walls + furniture and drops the roof.
5. **Bake** `<id>_collision.glb` via `ke-propbake`: add `"collisionProxy": "<id>_collision.glb"` to the kit
   manifest entry and run `KhaozEngine.PropSurface.Tool`. It emits a `<id>.coll` of kind `compound`.

## Coordinate notes

- Author in the same Blender scene the render `.glb` imports into, then export `_yup`. The proxy round-trips into
  the building's glTF frame, and `PropCollisionBake.BakeProxy` applies the RENDER mesh's normalization to the
  proxy, so it overlays the building exactly regardless of the proxy's own bbox.
- Each Blender object becomes one convex hull child (`GltfLoader.LoadGroups` preserves object boundaries). Keep
  pieces as separate objects.

## The metric

A proxy is good when a capsule scanned over a grid of stand/jump/walk spots in and around it finds ~0 WEDGES (a
spot it can neither walk nor jump out of) while the body + furniture stay standable. See the engine test
`KhaozEngine.Tests/Physics/RealBuildingCollisionTests.cs` (the blacksmith proxy fixture).
