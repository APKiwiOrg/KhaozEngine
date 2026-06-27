# Static world collision design (capsule-vs-prop/building)

Date: 2026-06-27
Status: approved design, ready for implementation plan
Area: engine (Collision + Locomotion/NetWorld) — the next character-physics piece

## Context

Movement now has ground-follow + a working slope gate (7.51), but **props and buildings have no
collision** — you walk straight through trees and through the inn. A walkable town needs this. It is
NOT a physics engine: it's kinematic **capsule-vs-static-collider in the XZ plane**, authoritative,
the standard MMO character-controller approach. `KhaozEngine.Collision` already has the 2D primitives
(`CircleCollision`, `SpatialHashGrid`, `Segment2D`) to build on.

Independent of the in-flight Ruinborne content work and the outline change (different repo / files),
so this runs as a **concurrent** engine chat.

## Components

### Collider metadata on assets

`AssetEntry` (the prop manifest) gains an optional **collider**: `{ type: "cylinder", radius }` or
`{ type: "box", halfW, halfD }`, defaulting to a cylinder derived from the prop's footprint when
omitted. So a scattered tree carries a collider radius, a building carries a box. `PropScatter`
placements already have `{ id, x, z, scale, yaw }`; the collider is the prop's collider × scale at
`(x,z)` rotated by `yaw`.

### `WorldColliders` — render-free queryable set

A spatial set of static colliders (cylinders + oriented boxes, each position/yaw/scale), broad-phased
with the existing `SpatialHashGrid`. Built from (a) the deterministic per-area scatter placements (so
it's streaming-consistent, same coordinate-hash) and (b) an explicit **obstacle/building list** (the
hand-placed town). `Query(x, z, radius) -> nearby colliders`.

### Collision math — `KhaozEngine.Collision`

XZ-plane resolution: circle-vs-circle (push-out exists), add **circle-vs-AABB** and
**circle-vs-oriented-box**. Resolution = minimum-translation push-out, applied so the capsule **slides
along** the surface (project the blocked move onto the contact tangent) rather than dead-stopping.

### Movement integration (authoritative)

After `CharacterMovement.Step` computes the desired XZ (ground-clamped, slope-gated), resolve it
against `WorldColliders.Query` near the new position: push the capsule (its radius) out of any
overlap, iterate a couple of times for corners, slide. The **server sim and client prediction run the
identical resolution** (same `WorldColliders`, same math) so prediction stays consistent. Nullable /
empty collider set = today's behaviour.

## Testing (headless)

- **Math**: circle-vs-AABB and circle-vs-oriented-box push-out are correct (depth + direction); a
  glancing hit slides, a head-on hit stops.
- **WorldColliders**: built from scatter placements matches the scatter (per-area, deterministic);
  `Query` returns the right neighbours; the building list is included.
- **Movement**: a player walking into a tree/building is pushed out and cannot enter; walking along a
  wall slides; the server resolves identically to the client (authoritative + prediction-consistent);
  no collider = unchanged movement.

## Scope

### In scope

- Collider metadata on `AssetEntry` (+ default from footprint).
- `WorldColliders` (render-free) over `SpatialHashGrid`, from scatter placements + a building list.
- `circle-vs-AABB` + `circle-vs-oriented-box` resolution in `KhaozEngine.Collision`.
- Authoritative integration into the movement step (`CharacterController3D` local/prediction +
  `PlayerMoveSimulator` -> `WorldServer`/`ShardedWorldServer`); nullable.
- A demo (TerrainWalkSample / the bounded preset) where props are solid.
- Headless tests; additive **minor** bump; docs.

### Out of scope (named)

- **Dynamic / moving colliders**, **player-vs-player** collision — static world only.
- **Vertical / full-3D collision** — XZ plane (buildings are tall; walking is planar). Roofs/overhangs
  later.
- **Gravity / falling / jump / step-height** — the other character-physics sub-project.
- **A general physics engine** (rigid bodies, joints, ragdolls) — not needed.
- **Navmesh.**

## Engine-first placement

Math in `KhaozEngine.Collision`; `WorldColliders` + the collider metadata + movement integration in the
render-free movement/world layer (`Locomotion`/`NetWorld`/`Terrain` as the dependency edges dictate);
collider field on `AssetEntry` (Render3D/Content). Ruinborne consumes it so its town props/buildings
become solid. Independent of Ruinborne content + outline → concurrent.

## Open items to confirm during implementation

- Exact home of `WorldColliders` (Locomotion vs a small new render-free package) by dependency edge.
- Oriented-box vs AABB for buildings (oriented if they rotate; AABB is cheaper) — ship oriented if the
  town places rotated buildings.
- Push-out iteration count for corners (2-3 is usually enough); slide-vs-stop feel.
- How the building/obstacle list is authored (a Ruinborne content concern; the engine takes a list).
