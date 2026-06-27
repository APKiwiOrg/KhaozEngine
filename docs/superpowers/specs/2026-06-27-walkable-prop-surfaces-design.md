# Walkable prop/building surfaces design (stand on rocks + roofs)

Date: 2026-06-27
Status: approved design, ready for implementation plan (implementation HELD until sub-project A lands)
Area: engine (Render3D bake + Collision/Terrain query + Locomotion/NetWorld integration)

## Context

Static world collision shipped in 7.52.0/7.53.0: props/buildings are solid in the XZ plane (a capsule is pushed
out of their footprint, `WorldColliders` + mesh-derived `PropFootprint`). You cannot walk *through* a rock, but
you also cannot stand *on* one: Y is still a pure function of XZ (terrain height + capsule half-height).

The goal is vertical traversal: **jump onto and stand on rocks, logs, and (solid) buildings, walking over their
real top-surface contours.** This is the next overworld character-physics program, and it splits into two
sub-projects:

- **Sub-project A (vertical character physics)** - gravity, jump, grounded/fall - is being built concurrently in
  a separate chat. It adds vertical velocity + a grounded flag to the player state, a `Jump` input, gravity/jump
  tuning, and makes `CharacterMovement.Step` integrate vertical motion instead of clamping Y to the ground.
  Authoritative + client-predicted. **A is a hard dependency of this work and is not in scope here.**
- **Sub-project B (this spec) - walkable prop/building surfaces** - gives A something to land on: each
  rock/building exposes a baked top-surface height field, the player's support height becomes
  `max(terrain, prop surface)`, and the XZ blocking becomes height-aware so standing on a roof does not get you
  shoved off.

This spec is **B only**.

## Dependency on sub-project A (coordination contract)

B integrates at A's movement seam. B assumes A provides (exact names reconciled against A when it lands - this is
the one tight coupling point, flagged in Open items):

- Player state carries **vertical velocity** and a **grounded** flag (today `PlayerMoveState` is just
  `Vector3 Position`).
- `MoveCommand` carries a **`Jump`** bit; a jump fires only when grounded.
- `MoveTuning` carries **gravity + jump speed** (and B adds a **step height**; see below).
- `CharacterMovement.Step` computes a **support height** the player lands/rests on (today that is
  `groundHeight(x,z) + CapsuleHalfHeight`). **B's job is to make that support height include prop surfaces** and
  to make the existing XZ push-out height-aware.

Everything B builds that is *upstream* of that seam - the bake, the asset, `WorldSurfaces`, the query math, the
`FromScatter` builder - is **independent of A** and can be built and tested before A lands. Only the final
movement integration (section "Movement integration") waits for A.

## Components

### 1. Surface bake (offline, folded into kit ingest)

A prop kit is prepared offline today by a documented `gltf-transform` recipe (decompress meshopt, dequantize,
flatten textures - see `TerrainWalkSample/assets/props/CREDITS.md`). The surface bake is **one more output of
that ingest step**, so re-ingesting a kit re-bakes its surfaces and they cannot drift from the meshes.

- **Bake logic (Render3D):** `PropSurfaceBake.Bake(GltfMesh normalizedMesh, PropSurfaceOptions) -> PropSurface`.
  Mirrors `PropFootprint`: it operates on a `PropLoader`-normalized mesh (base at y=0, XZ centred on origin) and
  rasterizes a **top-down max-height grid** over the prop's footprint - for each grid cell `(i,j)` covering local
  `(x,z)`, the maximum mesh surface Y above that cell (single-valued top contour; no overhangs, matching the
  no-overhang contract). Cells with no mesh above them are marked empty (not covered). Grid resolution is an
  option (default cell size ~0.25 m, capped to a max grid dimension so a giant building stays a small grid).
- **Bake tool (`PackAsTool`, like `ke-sfxbake`/`ke-updater`):** `KhaozEngine.PropSurface.Tool` (`ke-propbake`,
  `IsPackable` tool). Reads a kit manifest, loads each prop glTF via the Render3D loader (offline tooling may
  depend on Render3D), bakes the height grid, writes a small **binary heightmap asset** per prop kind next to the
  glTF, and references it from the manifest (a `heightmap` field beside `collider`; the tool stamps/validates it).
  Run as the last ingest step.
- **Heightmap binary asset (render-free format).** A tiny binary file per prop kind (NOT inlined in the JSON
  manifest - a grid is far bigger than a collider radius). Layout: a magic/version header, grid dimensions
  `(w,h)`, local-space extent (origin + cell size), then `w*h` heights (a sentinel for empty cells; heights may be
  quantized to `ushort` over `[minY,maxY]` to keep the file small - decide in the plan). **Baked local + unscaled**
  (unit prop, no placement transform); scale + yaw are applied at query time.

### 2. `PropSurface` / `WorldSurface` / `WorldSurfaces` (render-free, in `KhaozEngine.Collision`)

Sibling to `ColliderShape` / `WorldCollider` / `WorldColliders` so the surface set mirrors the collider set:

- **`PropSurface`** - the unplaced, unit-scale top-surface height grid (dimensions, local extent, the height
  samples + empty-cell sentinel). Pure data. `float SampleLocal(float lx, float lz)` bilinearly samples the grid
  in local space, returning the top height or "no surface here". A render-free reader parses the binary asset into
  a `PropSurface` (so the headless server loads surfaces with no render dependency).
- **`WorldSurface`** - a placed surface: a `PropSurface` + world `Center`, `Scale`, `Yaw`, plus a broad-phase
  bounding radius. `float? SampleWorld(float x, float z)`: transform `(x,z)` into the prop's local frame (subtract
  centre, rotate by `-yaw`, divide by `scale`), `SampleLocal`, multiply the height by `scale`, add the placement
  base Y. Returns null when `(x,z)` is outside this prop's covered footprint. Same transform-at-query the
  colliders use, so it is identical on client and server.
- **`WorldSurfaces`** - a `SpatialHashGrid`-backed set of `WorldSurface`. `float? Query(float x, float z)` returns
  the **maximum** top height over all placed surfaces covering `(x,z)` (a player where two rocks overlap stands on
  the higher), or null when none. Broadphased like `WorldColliders`. Nullable/empty = no surfaces, support height
  is just the terrain (today's behaviour).

### 3. Walkable-solid vs thin-blocker classification

Not every prop is a walkable surface. A tree's top contour is its canopy (wide and ~10 m up, unreachable), so a
tree must stay a **thin blocker** (its 7.53.0 XZ trunk `WorldCollider`, unchanged) and contribute **no**
`WorldSurface`. A rock/log/building is a **walkable-solid**: it contributes a `WorldSurface` (you stand on it) and
still contributes its XZ blocker (its sides, now height-aware).

Classification is per prop kind, declared at bake time and recorded in the manifest (a `surface: true|false` or a
`kind` enum), with a sensible derived default reusing the 7.53.0 footprint geometry (short, or solid-with-a-flat-
top -> walkable; tall-and-thin with a much wider mid/upper spread -> blocker). The bake tool can default it and
let the manifest override.

### 4. `PropSurfaces.FromScatter` (build the set, in `KhaozEngine.Terrain`)

Mirrors `PropColliders.FromScatter`. Given the deterministic scatter placements + an `id -> PropSurface` lookup
(walkable-solid kinds only) + an explicit obstacle/building list, place each as a `WorldSurface` (centre `(x,z)`,
the placement `scale`/`yaw`, base Y from the placement) and return a `WorldSurfaces`. Streaming-consistent because
it shares the coordinate-hash scatter, exactly like the colliders.

### 5. Movement integration (depends on A)

In `CharacterMovement.Step`, once A's vertical model exists:

- **Support height = `max(terrain(x,z), WorldSurfaces.Query(x,z) ?? -inf)`.** A's gravity/landing uses this as the
  surface the capsule rests on, so falling onto a rock lands you on its top, and walking over the top follows the
  baked contour.
- **Height-aware XZ blocking (the crux).** Today `WorldColliders` push the capsule out of a footprint
  unconditionally; that would shove you off a roof you are standing on. Make the push-out **conditional on
  height**: resolve a prop's XZ overlap only while the capsule's feet are **below that prop's top surface** (you
  are hitting the side), and skip it once you are at/above the top (you are supported, not blocked). So sides
  block, tops carry.
- **Step-up.** A small upward support step (rise `<= MoveTuning.StepHeight`, default ~0.4 m) is taken
  automatically without a jump, so you can mount a low rock/curb/log by walking into it; a rise greater than the
  step height behaves as a wall (blocked unless jumped). New tuning field `StepHeight` on `MoveTuning` (B adds it
  next to A's gravity/jump fields).
- **Grounded on a surface** counts as grounded for A's jump (you can jump again off a rock).

### 6. Authoritative + client-predicted

`WorldSurfaces` is render-free data both the authoritative server and the client hold, and `Query(x,z)` is a
deterministic function of `(x,z)` like the terrain field - so server and predicted client compute identical
support heights and prediction/reconciliation stay clean (A owns the vertical reconciliation). `WorldSurfaces`
threads through `WorldServer` / `ShardedWorldServer` / the client movement exactly like `WorldColliders` did
(nullable; null = unchanged).

The headless server must obtain the surface data **render-free**. The heightmap asset reader is render-free
(section 2). The remaining gap: the server needs the `id -> PropSurface` (and already `id -> collider`) index, but
`AssetManifest` currently lives in `Render3D`. Resolving that (a render-free manifest/kit index the server can
read) is called out in Open items; the binary heightmaps themselves are render-free by construction.

### 7. Demo

`TerrainWalkSample` (and the networked sample, once wired): jump onto a nearby rock and walk across its bumpy top,
stand on top of a hand-placed solid building, walk off an edge and fall (A) back to the terrain.

## Testing (headless)

- **Bake:** a synthetic mesh (e.g. a tilted slab, a domed blob) bakes to the expected height grid (known cells ->
  known heights; covered vs empty cells correct; a tall-thin tree-like mesh classifies as blocker / bakes no
  walkable surface). Round-trip the binary asset (write -> read -> identical grid).
- **`WorldSurface` query:** transform-at-query is correct - a unit surface placed at a centre/scale/yaw samples
  the right world height (scale multiplies height, yaw rotates the lookup); a point outside the footprint returns
  null; `WorldSurfaces.Query` returns the max over two overlapping surfaces and the right broad-phase neighbours.
- **`FromScatter`:** built from scatter matches the scatter (per-area deterministic, union == whole), obstacle
  list included, walkable-solid kinds only.
- **Height-aware blocking:** a capsule below a prop's top is pushed out of the side; a capsule at/above the top is
  NOT pushed (it stands); a thin-blocker (tree) always blocks.
- **Step-up:** a rise <= step height is mounted; a rise > step height is blocked (unless jumped).
- **Integration with A (against A's real or a mocked vertical model):** land on a rock from above and rest on its
  top contour; walk over the bumps and the support height follows the grid; walk off the edge and A's gravity
  drops you to terrain; server and client compute identical support heights for the same `(x,z)`.

## Scope

### In scope

- The offline surface bake folded into kit ingest (`ke-propbake` tool) + the render-free binary heightmap asset +
  manifest `heightmap` reference.
- `PropSurface` / `WorldSurface` / `WorldSurfaces` (render-free) + the transform-at-query sampling + broadphase.
- Walkable-solid vs thin-blocker classification.
- `PropSurfaces.FromScatter`.
- Movement integration against A: support = `max(terrain, surface)`, height-aware XZ blocking, step-up; nullable.
- Authoritative + predicted threading; a render-free server path to the surface data.
- A demo where you stand on rocks/a building; headless tests; additive minor bump; docs.

### Out of scope (named)

- **Sub-project A itself** (gravity/jump/grounded/fall + vertical reconciliation) - built separately; B depends on
  it.
- **Overhangs / interiors / caves / ceilings** - the surface is a single-valued top contour; buildings are solid
  blocks. Walking *under* anything needs full-3D collision and is not this.
- **Full 3D capsule-vs-triangle-mesh collision** - the general physics-engine path; not needed for top surfaces.
- **Dynamic / moving surfaces, player-vs-player, fall damage, climbing/mantling, moving platforms.**
- **Streaming surfaces** beyond a fixed region around spawn (matches the 7.52 collider demo scope; streaming both
  colliders and surfaces is a later piece).

## Engine-first placement

- Bake logic in `Render3D` (`PropSurfaceBake`, needs the mesh); the `ke-propbake` tool is `PackAsTool`.
- Render-free surface data + query (`PropSurface`/`WorldSurface`/`WorldSurfaces` + the binary reader) in
  `KhaozEngine.Collision`, beside the collider types.
- `PropSurfaces.FromScatter` in `KhaozEngine.Terrain`, beside `PropColliders`.
- Movement integration in `Locomotion` (`CharacterMovement.Step` + `MoveTuning.StepHeight`) and the threading in
  `NetWorld`, coordinated with A.

## Open items to confirm during implementation

- **A's final API** (player-state vertical fields, `MoveCommand.Jump`, the support-height seam in
  `CharacterMovement.Step`) - reconcile B's integration against it when A lands; hold the integration task until
  then.
- **Render-free server access to the kit index.** `AssetManifest` lives in `Render3D`; the headless server needs
  the `id -> PropSurface` / `id -> collider` mapping render-free. Options: a render-free kit-index model the
  server reads (the heightmaps are already render-free), or the server builds its surface set from the render-free
  scatter + render-free assets directly. Decide in the plan.
- **Heightmap format details** - grid resolution default + max dimension, height quantization (`ushort` vs `float`),
  empty-cell sentinel.
- **Classification default** - the exact derived rule (reuse 7.53.0 footprint geometry) vs always manifest-declared.
- **Step height + slope-gate interplay** - how the existing slope gate and the new step-up compose on a steep
  surface edge (feel-tuned in the demo).
