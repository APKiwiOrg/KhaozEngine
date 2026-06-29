# Physics pipeline: how a move becomes a collision-resolved position

A high-level (container / "Level 2") map of the path from a character move command to a position that has
been resolved against the static world. The physics counterpart to [RENDER-PIPELINE.md](RENDER-PIPELINE.md):
the boxes name real types so you can jump into the code, but detail stops at the layer boundaries. For the
shared "seam + opt-in backend" rule this is one instance of, see [DEPENDENCY-SEAMS.md](DEPENDENCY-SEAMS.md).

The one idea, same as the GPU seam: **nothing above `KhaozEngine.Physics` touches BepuPhysics.** The seam is
`IPhysicsWorld` plus value-type shapes/poses/queries (only `System.Numerics`); the backend
(`KhaozEngine.Physics.Bepu`) is the sole assembly that references BepuPhysics, added explicitly like
`Netcode.LiteNetLib` or `WorldStore.Sqlite`. Shipped 8.0.0.

## The flow

```mermaid
flowchart TD
    subgraph game["Game / server code"]
        CC["CharacterController3D (local, Game.Render3D)<br/>jumps on Space, camera-relative move"]
        SRV["WorldServer / ShardedWorldServer<br/>PlayerMoveSimulator (authoritative)"]
        CLI["WorldClient<br/>ClientPrediction (predicted + reconciled)"]
    end

    subgraph loco["KhaozEngine.Locomotion - the consumer of the seam"]
        STEP["CharacterMovement.Step(in MoveState, cmd, dt,<br/>groundDelegate, tuning, groundNormal?, IPhysicsWorld?, clampXz?)<br/>horizontal core + vertical physics; resolves vs world"]
    end

    subgraph seam["KhaozEngine.Physics - the backend seam (nothing above touches BepuPhysics)"]
        IPW["IPhysicsWorld<br/>AddStatic / RemoveStatic / Step<br/>Raycast / SweepCapsule / ComputePenetration"]
        SHAPE["PhysicsShape (value types)<br/>Sphere / Capsule / Box / Cylinder /<br/>ConvexHull / TriangleMesh / Compound"]
        AUX["Pose / PhysicsMaterial / QueryFilter<br/>StaticHandle / RayHit / SweepHit"]
    end

    BEPU["KhaozEngine.Physics.Bepu - BepuPhysicsWorld : IPhysicsWorld<br/>(the only assembly referencing BepuPhysics)"]

    LIB["BepuPhysics v2 2.4.0<br/>pure-managed Simulation, no native libs"]

    CC --> STEP
    SRV --> STEP
    CLI --> STEP
    STEP -->|"sweep + support probe + depenetrate"| IPW
    IPW --- SHAPE
    IPW --- AUX
    IPW --> BEPU
    BEPU --> LIB
```

Note how `Raycast`/`SweepCapsule`/`ComputePenetration` live on the seam, not the backend: ledge detection,
jump targeting, line-of-sight, ground probes, and move-and-slide are all expressed in seam types, so a
different backend (a native Jolt/PhysX wrapper, say) is a new `KhaozEngine.Physics.<X>` package with **zero**
changes upstream. Unlike the GPU seam there is no auto-selector yet: a consumer constructs the backend
directly (`new BepuPhysicsWorld()`) and hands the `IPhysicsWorld` down.

## Two side-flows that feed it

**Static bodies (CPU collision shapes -> the physics world), baked offline, loaded as chunks stream:**

```mermaid
flowchart LR
    KIT["Prop kit glTF + manifest"] --> BAKE["ke-propbake (PropSurface.Tool)<br/>one CollisionShape per walkable-solid prop"]
    BAKE --> COLL[".coll per prop<br/>tree -> trunk Cylinder<br/>rock/solid -> base-aligned ConvexHull<br/>building -> concave TriangleMesh"]
    COLL --> LOAD["client: PropCollisionLoader.LoadAll(manifest) (Render3D)<br/>headless server: PropCollisionFormat.LoadDirectory (Physics, no Render3D)<br/>-> id -> PhysicsShape map"]
    LOAD --> SINK["Scene3DChunkSink.Load / Unload"]
    SINK --> STAT["ChunkStatics.AddAll / RemoveAll<br/>-> IPhysicsWorld.AddStatic / RemoveStatic"]
```

Per-prop scale is applied to the shape geometry at add time; each static's Y is the placement's baked terrain
height. Terrain height itself stays **analytic** (the `TerrainField`, not a physics body): the seam resolves
props/buildings, the field resolves ground. (Bepu recenters `ConvexHull` and `Cylinder` on their centroid, so
a base-placed prop is wrapped in a centroid-offset compound to avoid sinking; `TriangleMesh` is not recentered.)

**The same move resolved twice (authoritative + predicted), against the same seam:**

```mermaid
flowchart LR
    INPUT["MoveCommand (axes + yaw + jump bit)"] --> SSTEP["server: PlayerMoveSimulator -> CharacterMovement.Step(IPhysicsWorld)"]
    INPUT --> CSTEP["client: ClientPrediction -> CharacterMovement.Step(same IPhysicsWorld config)"]
    SSTEP --> WIRE["replicated position + MovementState"]
    WIRE --> RECON["client reconcile: replay unacked cmds"]
    CSTEP --> RECON
    RECON --> NOOP["match -> no-op (no rubber-band)"]
```

Because client prediction resolves against the same `IPhysicsWorld` (and same optional `WorldBounds`) as the
server, it predicts around solid props and clamps at the wall, so reconciliation stays a no-op instead of
snapping the player back.

## The same path in words

1. A `MoveCommand` (move axes, yaw, jump bit) reaches `CharacterMovement.Step` from the local
   `CharacterController3D`, the server's `PlayerMoveSimulator`, or the client's `ClientPrediction`.
2. `Step` runs the horizontal core (camera-relative move, slope gate via the `groundNormal` delegate) and the
   vertical physics (gravity terminal-clamp, jump with coyote/buffer, air control), then resolves against the
   world: a capsule **sweep** along the move, a downward **support probe** so the character stands on prop
   tops, and a **depenetration** push-out (`ComputePenetration`, iterated) to collide-and-slide. A `null`
   world means terrain-only (the analytic field still clamps Y).
3. Everything in step 2 is expressed in `KhaozEngine.Physics` seam types (`IPhysicsWorld`, `CapsuleShape`,
   `Pose`, `RayHit`/`SweepHit`). Nothing in `Locomotion`, `NetWorld`, or the game references BepuPhysics.
4. `BepuPhysicsWorld` (the backend) answers those queries over a single-threaded deterministic BepuPhysics
   `Simulation`. It is the only assembly that pulls the `BepuPhysics` package; consumers add it explicitly,
   exactly like a netcode transport or a WorldStore backend.
5. Static bodies got into that simulation from the bake side-flow: `ke-propbake` wrote a `.coll` shape per
   prop, `PropCollisionLoader` read them into an id -> `PhysicsShape` map, and `Scene3DChunkSink` (via
   `ChunkStatics`) called `AddStatic`/`RemoveStatic` as chunks streamed in and out. The `.coll` decode itself is
   the render-free `PropCollisionFormat` in `KhaozEngine.Physics`, so a headless authoritative server (no
   Render3D/Gpu/Windowing) loads the same shapes via `PropCollisionFormat.LoadDirectory`/`Load` and builds an
   identical `BepuPhysicsWorld` to predict against; the client's `PropCollisionLoader.LoadAll(manifest)` decodes
   through the same code, so the shapes - and therefore the queries - are byte-identical.

## Where to look in the code

| Box | Type / file |
|---|---|
| The backend seam | [`KhaozEngine.Physics/IPhysicsWorld.cs`](../KhaozEngine.Physics/IPhysicsWorld.cs), `PhysicsShape.cs`, `Pose.cs`, `Queries.cs`, `Handles.cs` |
| BepuPhysics binding | [`KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs`](../KhaozEngine.Physics.Bepu/BepuPhysicsWorld.cs), `ShapeFactory.cs`, `HitHandlers.cs` |
| Movement that resolves vs the seam | [`KhaozEngine.Locomotion/CharacterMovement.cs`](../KhaozEngine.Locomotion/CharacterMovement.cs) (`Step`) |
| Local controller / server sim / client prediction | `KhaozEngine.Game.Render3D/CharacterController3D.cs`, `KhaozEngine.NetWorld/PlayerMoveSimulator.cs`, `WorldClient.cs` |
| Shape bake (offline) | [`KhaozEngine.PropSurface.Tool/Program.cs`](../KhaozEngine.PropSurface.Tool/Program.cs) (`ke-propbake`), `KhaozEngine.Render3D/Models/PropCollisionBake.cs` |
| Shape load + chunk statics | `KhaozEngine.Render3D/Models/PropCollisionLoader.cs` (client/manifest), [`KhaozEngine.Physics/PropCollisionFormat.cs`](../KhaozEngine.Physics/PropCollisionFormat.cs) (render-free format + headless loaders), [`KhaozEngine.Terrain.Render3D/ChunkStatics.cs`](../KhaozEngine.Terrain.Render3D/ChunkStatics.cs) |
| Design archive | `docs/superpowers/specs/` + `plans/` (the 8.0.0 physics-foundation feature) |
