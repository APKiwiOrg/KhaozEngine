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
2b. **FIT the measurable geometry** (do this FIRST for bodies and roofs - eyeballed roofs drift badly):
   `blender -b --python fit_proxy.py -- fit <building>.glb <heightMeters> <placementScale> <draft.json> [roofFloorZ]`
   emits plane-fitted roof SLABS (+ flat caps) and z-sliced wall-story boxes. The roof floor auto-detects
   from the footprint taper; buildings with intersecting roofs or porch furniture that defeat it take the
   explicit `roofFloorZ` override (raw units, read off the analyze render). Merge the draft into the
   hand-authored capsule spec (steps, rails, furniture - the capsule rules below) with MERGE mode:
   `blender -b --python fit_proxy.py -- merge <building>.glb <heightMeters> <placementScale> <hand.json> <draft.json> <out.json>`
   (adopts the fitted roofs, clamps body/story boxes to the roof floor - `_keepTall` names exempt
   deliberate anti-pin fills, `_skipDraftPieces` rejects audited-bad fitted pieces), then
   **AUDIT the merged spec for pin traps** (a standable top under a ceiling with less than capsule headroom -
   the anvil-under-porch-roof class):
   `blender -b --python fit_proxy.py -- audit <building>.glb <heightMeters> <placementScale> <spec.json>`
   Fix every warning (fill the gap solid, trim the roof edge, or raise the ceiling) and re-audit to CLEAN.
3. **Author** a box/wedge/cylinder spec JSON in the building's Blender frame (Z up), enveloping the substantial
   masses and dropping decoration. See `examples/blacksmith_spec.json`. Schema:
   ```json
   { "boxes":     [ { "name": "...", "min": [x,y,z], "max": [x,y,z] } ],
     "wedges":    [ { "name": "...", "min": [x,y,z], "max": [x,y,z], "axis": "x|y", "dir": 1 } ],
     "cylinders": [ { "name": "...", "center": [x,y], "radius": 0.3, "z": [z0,z1], "segments": 8 } ] }
   ```
   A wedge is a right-triangular prism (a stair ramp), the sloped face rising along `axis` in `dir`. A cylinder
   is an n-gon prism for round masses (a well ring) - tighter than a box at the diagonals.
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

## Capsule geometry rules (hard-won, engine 8.11.0 movement + Bepu)

Author in WORLD metres and convert to the building's raw units via `heightMeters / rawHeight * placementScale`
(each building has its OWN scale, so identical raw geometry needs different specs per building). For the default
r=0.4 capsule, StepHeight 0.4, MaxSlope 40 deg, all limits below are world metres. Violating any of these read
as fine in an overlay render but fails live (measured on the Ruinborne town set):

- **Bodies**: hug the wall PLANES measured from the mesh (a z-band through the walls), never the full bbox
  (that wraps roof/eave/jetty overhangs into an invisible fat edge). Drop overhangs, porch posts, rails, and
  foot-level plinth relief. Put columns and the infill wall on ONE plane at the columns' face - separate column
  boxes leave concave pockets.
- **Entrance steps**: exactly ONE step-up-probe mount per approach, from flat ground, rise 0.2..0.32. Above
  ~0.35 the walker's rest equilibrium tangent-penetrates the step edge and every probe sweep returns Bepu's
  t=0 zero-normal degeneracy (dead). A SECOND probe mount from a tread also dies this way - later rises must be
  <= 0.08, which an edge contact classifies as walkable floor (strolled over, no probe). Rises 0.094..0.2 are a
  DEAD ZONE: too steep to be floor, too shallow-normal (|n.Y| > 0.5) to be a step-up riser - unclimbable.
- **Treads**: >= ~0.3 deep (shallower sheds a resting capsule - depenetration ratchets it off edge contacts)
  and >= ~0.3 TALL where the capsule rests against a door wall (the support sweep degenerates wall-tangent;
  under ~0.3 the terrain reclaims the capsule and it falls THROUGH the tread and wedges inside it). The top
  tread's front edge must sit >= ~0.45 from the door wall or the wall-stopped capsule (one radius out) never
  stands on it.
- **Ceilings**: nothing solid within the probe envelope (2.2 = raised feet 0.4 + 1.8 body) above any mount
  zone - including a jetty/upper-floor box's underside AND the body box's own front-top edge (cap the body
  well above head height or run it to full height; a low cap's corner corrupts the probe's sweeps).
- **Gaps**: no slot between solids narrower than the capsule diameter (0.8) - extend blocks flush to walls and
  to each other (a pinch slot wedges even with every solid convex).
- **Round masses**: use a `cylinders` n-gon prism, not a box (a box is ~40% fat at the diagonals).
- **Chimneys are substantial, not decoration**: every visible stack gets its own `chimney*` box hugging
  the MEASURED stone (cluster the Stone* faces above the roofline), rooted below the ridge so a
  roof-walking capsule collides with it. Hug means hug: a box proud of the stone reads as a fat air
  collider in the F2 overlay. `chimney*` names are exempt from the merge roof-floor clamp.
- **Roofs are `slabs`, never wedges**: a wedge is a right prism whose flat BOTTOM spans its whole footprint
  at the low-eave height - over open space (porch, awning, freestanding roof) that invisible underside
  hangs metres below the visible plane and pins capsules against whatever they stand on beneath it. A slab's
  underside follows the fitted plane. Wedges remain the primitive for solid ramps only.
- **Audit every final spec** (`fit_proxy.py audit`): any top a capsule can reach (walk, step, or jump
  onto) must have capsule-height clearance below every piece above it, or the capsule pins and wedges.
  Fixes that keep solids convex: trim the ceiling piece's low edge clear of walk envelopes, raise it, or
  (last resort) extend the standable piece UP into the ceiling piece. Prefer REAL TOPS under sloped slab
  ceilings: a pin needs opposing PARALLEL surfaces (flat wedge-prism bottoms), while a sloped slab
  underside sheds a squeezed capsule sideways - so a well ring / forge / woodpile can stop at its real
  top (ideally above jump reach, removing the landable top entirely) instead of filling to the roof,
  which reads as an ugly solid column in the F2 overlay. Record such audited exemptions in the
  `_lowHeadroomOk` name list (honoured only under sloped slabs; justify in the spec `_comment` and keep
  them covered by wedge-scan tests).

## The metric

A proxy is good when a capsule scanned over a grid of stand/jump/walk spots in and around it finds ~0 WEDGES (a
spot it can neither walk nor jump out of) while the body + furniture stay standable. See the engine test
`KhaozEngine.Tests/Physics/RealBuildingCollisionTests.cs` (the blacksmith proxy fixture).
