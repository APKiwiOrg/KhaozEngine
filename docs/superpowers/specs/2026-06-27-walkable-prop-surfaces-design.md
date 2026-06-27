# Walkable prop/building surfaces design (stand on rocks + roofs)

Date: 2026-06-27
Status: approved design, ready for implementation plan. Sub-project A (vertical character physics) SHIPPED in
7.54.0, so B is unblocked; the dependency contract below is reconciled to A's real 7.54.0 API.
Area: engine (Render3D bake + Collision/Terrain query + Locomotion/NetWorld integration)

## Context

Static world collision shipped in 7.52.0/7.53.0: props/buildings are solid in the XZ plane (a capsule is pushed
out of their footprint, `WorldColliders` + mesh-derived `PropFootprint`). You cannot walk *through* a rock, but
you also cannot stand *on* one: Y is still a pure function of XZ (terrain height + capsule half-height).

The goal is vertical traversal: **jump onto and stand on rocks, logs, and (solid) buildings, walking over their
real top-surface contours.** This is the next overworld character-physics program, and it splits into two
sub-projects:

- **Sub-project A (vertical character physics)** - gravity, jump, grounded/fall - **shipped in 7.54.0**. It added
  the `Locomotion.MoveState` kinematic state (position + `VerticalVelocity` + `Grounded` + coyote/jump-buffer
  timers), a vertical `CharacterMovement.Step(in MoveState, ...) -> MoveState` overload (the old `Vector3` overload
  is unchanged), a `MoveCommand.Jump` bit, gravity/jump fields on `MoveTuning`, the replicated `MovementState`
  component, and the authoritative + client-predicted vertical reconciliation. **A is a hard dependency of this
  work and is not in scope here.**
- **Sub-project B (this spec) - walkable prop/building surfaces** - gives A something to land on: each
  rock/building exposes a baked top-surface height field, the player's support height becomes
  `max(terrain, prop surface)`, and the XZ blocking becomes height-aware so standing on a roof does not get you
  shoved off.

This spec is **B only**.

## Dependency on sub-project A (coordination contract)

B integrates at A's 7.54.0 movement seam (now concrete):

- A's vertical step is `CharacterMovement.Step(in MoveState state, in MoveCommand cmd, float dt,
  Func<float,float,float> groundHeight, in MoveTuning tuning, Func<float,float,Vector3>? groundNormal,
  WorldColliders? colliders, Func<float,float,Vector2>? clampXz) -> MoveState`. **Ground contact lands the capsule
  on `groundHeight(x,z) + tuning.CapsuleHalfHeight`** (the support height B must extend), and its shared
  `ResolveHorizontal` pushes the capsule out of `WorldColliders` **unconditionally** (the push-out B must make
  height-aware).
- `MoveState` carries `Position` / `VerticalVelocity` / `Grounded` (+ feel timers); `MoveCommand.Jump` is the jump
  bit; `MoveTuning` carries `Gravity`/`JumpSpeed`/`MaxFallSpeed`/`CoyoteTime`/`JumpBuffer`/`AirControl`/
  `GroundedEpsilon` (B adds a **`StepHeight`** field next to these).
- `NetWorld.PlayerMoveState` wraps a `MoveState`; the vertical axis rides the wire as the replicated
  `MovementState` component; `PlayerMoveSimulator`/`PlayerMovementSystem`/`WorldServer`/`ShardedWorldServer`/
  `WorldClient` already step + replicate + reconcile it. B threads `WorldSurfaces` through these the same way
  7.52.0 threaded `WorldColliders`.

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

### 5. Movement integration (against A's 7.54.0 seam)

B threads a nullable `WorldSurfaces?` through the vertical `CharacterMovement.Step(in MoveState, ...)` overload
(and `ResolveHorizontal`), exactly as 7.52.0 added `WorldColliders?`; null = today's terrain-only behaviour.

- **Support height** - A lands the capsule on `groundHeight(x,z) + CapsuleHalfHeight`. B makes the effective
  ground `max(terrain(x,z), WorldSurfaces.Query(x,z) ?? terrain)`, so falling onto a rock lands you on its top and
  walking over the top follows the baked contour. (Passing a composed `groundHeight` delegate also works, but a
  first-class `WorldSurfaces?` param keeps the height-aware blocking below in one place and matches the collider
  precedent.)
- **Height-aware XZ blocking (the crux).** `ResolveHorizontal` currently calls `colliders.Resolve(xz, radius)`
  unconditionally, which would shove you off a roof you stand on. Make it height-aware: a blocker is skipped once
  the capsule's feet are at/above that prop's top (you are supported, not blocked) and applied while below (you
  hit the side). Mechanism: each `WorldCollider` gains a **top height** (the prop's solid top = its baked surface
  max for a walkable-solid; its full height for a thin-blocker so a tree is never mounted), and `Resolve` takes
  the capsule foot Y, skipping colliders with `footY >= top - skin`. So sides block, tops carry; trees are
  unchanged (their top is unreachable).
- **Step-up.** A small upward support step (rise `<= MoveTuning.StepHeight`, default ~0.4 m) is taken
  automatically without a jump, so you can mount a low rock/curb/log by walking into it; a rise greater than the
  step height behaves as a wall (blocked unless jumped). New `StepHeight` field on `MoveTuning`, beside A's
  gravity/jump fields.
- **Grounded on a surface** counts as `MoveState.Grounded` for A's jump/coyote logic (you can jump again off a
  rock), since it flows from the same `groundHeight` contact test.

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

- ~~A's final API~~ - **resolved**: A shipped in 7.54.0; the dependency contract + integration sections above are
  reconciled to its real API (`MoveState`, the vertical `Step` overload, `MoveTuning` vertical fields,
  `MovementState`). No longer held.
- **Render-free server access to the kit index.** `AssetManifest` lives in `Render3D`; the headless server needs
  the `id -> PropSurface` / `id -> collider` mapping render-free. Options: a render-free kit-index model the
  server reads (the heightmaps are already render-free), or the server builds its surface set from the render-free
  scatter + render-free assets directly. Decide in the plan.
- **Heightmap format details** - grid resolution default + max dimension, height quantization (`ushort` vs `float`),
  empty-cell sentinel.
- **Classification default** - the exact derived rule (reuse 7.53.0 footprint geometry) vs always manifest-declared.
- **Step height + slope-gate interplay** - how the existing slope gate and the new step-up compose on a steep
  surface edge (feel-tuned in the demo).
