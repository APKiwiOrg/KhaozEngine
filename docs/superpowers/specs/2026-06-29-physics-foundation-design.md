# Physics foundation design (KhaozEngine.Physics seam + Bepu backend, controller on mesh collision)

Date: 2026-06-29
Status: approved design, ready for implementation plan
Area: engine (new `KhaozEngine.Physics` + `KhaozEngine.Physics.Bepu`, folding `Locomotion`/`NetWorld`/`Terrain`/`Render3D`)

## Context

Movement today is a kinematic character controller, not a physics engine. `CharacterMovement.Step` does
gravity + jump (coyote/buffer/terminal-clamp), ground-clamp onto a height delegate, and capsule push-out
versus **2D XZ static footprints** (`KhaozEngine.Collision`: `WorldColliders` cylinders/oriented-boxes,
`WorldSurfaces` point-sampled walkable tops). It has no notion of arbitrary 3D geometry, so the world
cannot answer the questions gameplay needs: standing on / jumping onto props and buildings, building
interiors (floors/walls/stairs), what is a ledge, where a character can jump up, where a jump lands,
line-of-sight.

The concrete wart this resolves (deferred 2026-06-28): a capsule standing on the rising flank of a domed
prop clips its body into the slope, because the collider is a 2D footprint and the walkable top is
point-sampled at the capsule centre, so nothing pushes the body out of the prop's real 3D surface (clean
at the peak, sinking on the side). The robust fix is real capsule-vs-surface resolution.

This is ROADMAP.md near-term item #2 (Physics engine). It runs as a small program of sub-projects, the
way terrain and netcode were staged. This spec is **sub-project 1**.

## Decisions taken (settled in brainstorming)

- **Scope target: tier 2** (robust character-vs-world collision + queries, plus genuinely simulated
  dynamic rigid bodies), architected so it can grow to tier 3 (constraints/joints/vehicles) later. Tier 2
  is split out to sub-project 2; this spec is the tier-1 foundation it sits on.
- **Determinism model: authoritative server + client prediction/reconcile (NOT lockstep).** The server is
  authoritative; the client predicts and reconciles. Minor floating-point divergence is corrected on the
  next snapshot, so cross-architecture bit-exactness is NOT required. This is the same contract the
  movement stack already holds, and it is what makes a stock float-based managed solver viable.
- **Build vs buy: a `KhaozEngine.Physics` seam over BepuPhysics v2 (Option A).** Building a robust tier-1
  controller-vs-mesh layer ourselves is feasible, but building a stable tier-2 rigid-body solver from
  scratch (contact manifolds, friction, restitution, sleeping, CCD, stable stacking) is a deep,
  easy-to-get-subtly-wrong undertaking that mature libraries exist to avoid. BepuPhysics v2 is **pure
  managed C# (Apache 2.0)**, among the fastest CPU physics engines either managed or native, with rigid
  bodies, a full constraint set, continuous collision, and raycast/sweep/overlap queries. Critically it
  carries **no native libraries**: it sits on the right side of the engine's MonoGame-free /
  no-C++ / no-per-RID-native-bundling / AOT-clean thesis, exactly like the managed NuGets the engine
  already depends on (Silk.NET, Veldrid, SharpGLTF, LiteNetLib, Microsoft.Data.Sqlite). Native engines
  (Jolt/Bullet/PhysX via bindings) were rejected for re-importing the native-bundling + AOT burden the
  engine deliberately eliminated; they remain a fallback only if managed perf is ever shown insufficient.
- **Driver / live testbed: Ruinborne** (the 3D MMO overworld where dome-clipping, interiors and ledges
  bite, paired with the sharded server work).

## Program decomposition

- **Sub-project 1 (this spec): physics foundation + character controller on real mesh collision.** The
  seam, the Bepu backend, a determinism + NativeAOT/iOS validation gate up front, static prop/building
  geometry as real 3D shapes, and the kinematic controller folded onto capsule-vs-mesh collide-and-slide
  (the dome fix + interiors + raycast/sweep/overlap queries for ledges, jump targeting, line-of-sight).
  Wired server-authoritative + client-predicted exactly as today, opening **no new replication surface**.
- **Sub-project 2: dynamic rigid bodies + replication.** Server-authoritative dynamic bodies (pushable
  crates, debris, ragdolls, bouncing projectiles) simulated by Bepu, replicated to clients as transforms
  through the `Replication` layer with client interpolation/prediction. The genuinely netcode-heavy half,
  which is why it is kept out of SP1. Terrain-as-physics-geometry (so dynamic bodies can roll on the
  ground) lands here.
- **Sub-project 3 (deferred): constraints / joints / vehicles / articulation.** Tier 3, only when a game
  needs it. Bepu's constraint set is already present, so this is incremental on the seam.

## Architecture

### Packages and dependency graph

Mirrors the established opt-in-backend pattern (`Netcode.Abstractions` + `Netcode.LiteNetLib`;
`WorldStore` + `WorldStore.Sqlite`/`.SqlServer`):

- **`KhaozEngine.Physics`** (new, dependency-free seam). Defines `IPhysicsWorld`, the 3D shape
  descriptors, `Pose`, query result types, `PhysicsMaterial`, `QueryFilter`, and body handles. Depends
  only on `System.Numerics` (+ `Primitives` if shared math is wanted). A leaf, alongside `Collision`.
  Render-free, headless. Lands in the **`Foundation`** umbrella (a dependency-free leaf, like `Collision`),
  so `Locomotion` (also `Foundation`) can depend on it without an umbrella-layer inversion.
- **`KhaozEngine.Physics.Bepu`** (new, backend). Pulls the `BepuPhysics` NuGet and implements the seam.
  The only assembly in which Bepu types appear, the same containment as `Netcode.LiteNetLib`. Lands in the
  **`Server`** line (headless-authoritative); `Game3D` pulls it for client prediction.
- **`Locomotion`** shifts its resolution dependency from `Collision` (2D footprints) to the **`Physics`
  seam only** (never the Bepu backend), staying render-free and netcode-free. `CharacterMovement.Step`
  takes an `IPhysicsWorld?` where it currently takes `WorldColliders?`/`WorldSurfaces?`.
- **`NetWorld`** passes the same `IPhysicsWorld` instance to server and client; its
  `WorldServer`/`WorldClient`/`ShardedWorldServer` swap the `WorldColliders?`/`WorldSurfaces?` ctor
  parameters for `IPhysicsWorld?`.

Acyclic: seam is a leaf, Bepu backend is the opt-in edge, no consumer references Bepu directly.

### Seam API (the SP1 surface)

```
PhysicsWorld : IPhysicsWorld
  StaticHandle AddStatic(in PhysicsShape shape, in Pose pose, PhysicsMaterial? mat = null)
  void         RemoveStatic(StaticHandle h)
  void         Step(float dt)                       // near-trivial in SP1 (no dynamic bodies yet)
  bool Raycast(Vector3 origin, Vector3 dir, float maxDist, out RayHit hit, QueryFilter f = default)
  bool SweepCapsule(in CapsuleShape s, in Pose pose, Vector3 dir, float maxDist,
                    out SweepHit hit, QueryFilter f = default)
  bool ComputePenetration(in CapsuleShape s, in Pose pose, out Vector3 mtv)   // depenetration

PhysicsShape    = Sphere | Capsule | Box | Cylinder | ConvexHull | TriangleMesh | Compound
Pose            = Vector3 position + Quaternion orientation
RayHit / SweepHit = distance, point, normal, hit handle
PhysicsMaterial = friction, restitution            // mostly exercised in SP2
QueryFilter     = layer / mask
```

The controller resolves via `SweepCapsule` + `ComputePenetration` + `Raycast`. `Raycast`/`SweepCapsule`
are also the public primitives for ledge detection, jump targeting, line-of-sight, and AI. `Step` exists
now (so dynamic bodies in SP2 drop in without an API change) but does little until SP2 adds bodies.

### Static-world model: analytic terrain stays, props/buildings become 3D shapes

The domed-rock bug is a **prop** problem, not a terrain problem, so SP1 does **not** mesh terrain into
the physics world. Keep the proven, exact, deterministic analytic `TerrainCollision.GroundHeight` /
`GroundNormal` delegate for ground-follow exactly as today. Put only **props and buildings** into the
physics world as real 3D shapes:

- solid props (rocks, tree trunks) -> a **convex hull** baked from the mesh,
- buildings / interiors -> a **triangle mesh** (non-convex, so floors/walls/stairs and doorways work),
- streamed in/out on the same lifecycle that loads `WorldColliders`/`WorldSurfaces` today, which this
  supersedes.

So the controller becomes: vertical ground-follow against the terrain delegate (unchanged) +
capsule-vs-prop collide-and-slide against the physics world. The capsule rests on a rock's actual 3D
hull (the dome fix), and buildings gain real interiors. Terrain-as-physics-geometry (needed only when
SP2's dynamic crates must roll on the ground) is deferred to SP2.

## The determinism + AOT gate (first milestone, before any integration)

A spike proving BepuPhysics, gating the whole bet cheaply:

- (a) runs headless in net10 with no windowing/GPU,
- (b) with a fixed single-threaded `Step` plus the sweep/raycast queries, gives **run-to-run identical
  results on one binary** (replay determinism), enough that client prediction reconciles against the
  server running the same world,
- (c) builds under **NativeAOT** and on the **iOS** target with no reflection or JIT-stub blockers.

Pass -> proceed with the integration below. Fail -> fall back to Option B (hand-built capsule-vs-mesh
collide-and-slide + a static triangle BVH) **behind the same seam**, so the seam, controller integration,
authoring, streaming, netcode wiring, and tests are all unchanged and no integration work is wasted. The
seam is the insurance: consumers never see which backend answered.

## Character controller integration (collide-and-slide)

`CharacterMovement.Step` keeps every bit of feel logic (camera-relative move, walk/run, slope gate,
gravity/jump/coyote/buffer/terminal-clamp, air control, step-up) and only swaps its resolver:

1. depenetrate any initial overlap (`ComputePenetration`),
2. sweep the desired horizontal motion (`SweepCapsule`); on a hit, advance to the contact and slide the
   remaining motion along the contact tangent,
3. iterate a few times for corners,
4. vertical ground-follow stays the terrain delegate; step-up becomes a sweep probe (sweep up by
   `StepHeight`, forward, and down to test a mountable step).

The dome fix falls out for free: the capsule rests on the rock's real hull instead of point-sampling a
single top height. `MoveState` / `MoveCommand` / `MoveTuning` are unchanged in shape.

## Netcode wiring (contract unchanged)

Server and client share one `IPhysicsWorld`, built from the same streamed prop shapes (deterministic
per-area, like the scatter already is). `PlayerMoveSimulator.Step` runs the same `CharacterMovement.Step`
against that world on both sides. The reconciliation basis is unchanged (position + vertical velocity +
grounded; `MovementState` component type id 2 still rides the wire). No new replicated state in SP1.
Authoritative+reconcile absorbs any tiny query-order float drift, so a small server/client divergence is
corrected on the next snapshot rather than requiring bit-identical math.

## Authoring and streaming

- Extend the **`ke-propbake`** tool (already folded into kit ingest) to bake a 3D collision shape per
  prop into the asset kit: a convex hull for solid props, a triangle mesh for buildings/interiors.
  Classification reuses the existing `IsWalkableSolid` logic (rock/log/building vs thin-blocker tree).
  Re-ingest = re-bake, the same contract the `.surf` bake already has.
- `AssetEntry` gains an optional baked **collision-shape** reference (beside the existing `Collider`,
  `Surface`, `Heightmap`). A render-free loader reads it.
- The streamer / `Scene3DChunkSink` calls `AddStatic` / `RemoveStatic` on the physics world on the same
  load/unload it already runs for the visual chunk + `WorldColliders`. The headless authoritative server
  builds identical statics from the baked shapes with no rendering.

## Migration (breaking changes are fine; alpha)

Done properly rather than half-kept:

- `Locomotion`'s resolution backend cuts over from the legacy 2D `Collision`
  (`WorldColliders`/`WorldSurfaces` footprint push-out) to the seam's capsule-vs-mesh collide-and-slide.
  Its dependency shifts from `Collision` to `Physics`. The old 2D resolution path in the controller is
  **removed**, not run in parallel.
- `NetWorld` `WorldServer` / `WorldClient` / `ShardedWorldServer` ctors swap their
  `WorldColliders?` / `WorldSurfaces?` parameters for `IPhysicsWorld?`.
- Reusable bits of `Collision` survive, repurposed: `SpatialHashGrid` stays a general broadphase; the
  `.surf` / `PropFootprint` / `PropSurfaceBake` outputs become inputs to 3D shape baking rather than the
  query path. `Collision`'s 2D footprint resolution (`BoxCollision` MTV, `WorldColliders.Resolve`,
  `WorldSurfaces`) is retired from the movement path.
- Consumers (Ruinborne, and any sample using the old collider/surface ctors) update to pass an
  `IPhysicsWorld`. Acceptable: everything is alpha.

## Testing (headless)

`KhaozEngine.Tests`, construct a `PhysicsWorld` with known statics and feed `CharacterMovement.Step`
frames:

- a capsule rests on a **domed hull's flank** with no penetration (the regression that motivated this),
- a capsule walks **through a building doorway** and **cannot cross walls** (triangle-mesh interior),
- a glancing wall hit **slides**, a head-on hit **stops**,
- `Raycast` / `SweepCapsule` ledge + line-of-sight queries return the expected hit distance and normal,
- **determinism**: the same inputs against the same world twice produce an identical trajectory,
- **server == client**: the authoritative sim and the predicted client resolve identically against the
  shared world (reconcile is a no-op for identical inputs).

Plus seam-conformance tests for the Bepu backend (each shape type, each query) and the standalone
determinism/AOT gate harness from the first milestone.

## Scope

### In scope

- `KhaozEngine.Physics` seam (`IPhysicsWorld`, shapes, poses, queries, material, filter, handles).
- `KhaozEngine.Physics.Bepu` backend over BepuPhysics v2; the determinism + NativeAOT/iOS gate.
- Static prop/building geometry as 3D convex hull / triangle mesh; `ke-propbake` shape bake; `AssetEntry`
  shape reference; render-free loader; streamer `AddStatic`/`RemoveStatic`.
- `CharacterMovement.Step` folded onto capsule-vs-mesh collide-and-slide via the seam; sweep-based step-up.
- Netcode wiring through `PlayerMoveSimulator` -> `WorldServer`/`WorldClient`/`ShardedWorldServer` with
  `IPhysicsWorld` replacing `WorldColliders`/`WorldSurfaces`.
- Migration / retirement of the 2D footprint resolution path; consumer ctor updates.
- Headless tests; additive packages + a **major** bump (breaking the movement/world ctors); full doc sweep.

### Out of scope (named)

- **Dynamic / loose rigid bodies** (crates, debris, ragdolls, projectiles) and their **replication** ->
  sub-project 2.
- **Terrain as physics geometry** (terrain triangle mesh / heightfield in the world) -> sub-project 2,
  when dynamic bodies must collide with the ground. SP1 keeps the analytic ground delegate.
- **Constraints / joints / vehicles / articulation** -> sub-project 3.
- **Player-vs-player collision**, **navmesh / pathfinding** (AI consumes the raycast/sweep queries, but
  navmesh is its own concern).
- **Lockstep bit-exact cross-platform determinism** (authoritative+reconcile only).

## Engine-first placement

The seam is a render-free dependency-free leaf (`Foundation`, alongside `Collision`); the backend is a
headless-first engine package (`Server` line; `Game3D` pulls it for prediction), consistent with the
default-centralize rule: physics is a generic domain every
game wants. Ruinborne is the live testbed (its town props/buildings become real 3D collision, its overworld
rocks stop clipping). No game-specific physics is built; consumers wire the engine seam.

## Open items to confirm during implementation

- Exact home of the controller's collide-and-slide loop (inside `CharacterMovement.Step` vs a thin helper
  in `Locomotion`) by readability once the seam types exist.
- Convex-hull simplification budget per prop (hull vertex cap) vs fidelity; tree-trunk vs full-canopy hull
  (reuse the tall-prop trunk-slice heuristic the footprint derivation already uses).
- Sweep iteration count + skin width for the collide-and-slide (2-3 iterations is usually enough);
  slide-vs-stop feel parity with the current controller.
- Bepu `Step` threading/config knobs needed to hold replay determinism (single-threaded dispatcher, fixed
  solver iteration counts); confirmed by the gate harness.
- Whether the seam needs a `Compound` shape in SP1 or only Sphere/Capsule/Box/Cylinder/ConvexHull/
  TriangleMesh (add `Compound` only if a prop needs multiple disjoint hulls).
- Umbrella final placement confirmed once the dependency edges are wired (seam expected in `Foundation`
  beside `Collision`, backend in `Server` + pulled by `Game3D`).
