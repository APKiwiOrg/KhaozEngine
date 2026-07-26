# Floating origin: precision at 100 km for rendering, physics, and movement

Design rationale. Issue: [#337](https://github.com/APKiwiOrg/KhaozEngine/issues/337). Consumer program:
[Ruinborne#242](https://github.com/APKiwiOrg/Ruinborne/issues/242), sub-project 5.

This is the **why**. When it ships, what shipped and how to use it go to `CHANGELOG.md`,
`docs/USING-KHAOZENGINE.md`, and the `KhaozEngine.Primitives` / `KhaozEngine.Physics` /
`KhaozEngine.NetWorld` / `KhaozEngine.Render3D` package READMEs.

Nothing here is implemented. Every API signature below is a proposal.

## 1. What the measurement forces

Ruinborne measured it on real production code
(`Ruinborne/docs/design/2026-07-26-world-scale-spike-findings.md` section 4,
`Ruinborne.Tests/FarFromOriginPrecisionTests.cs`). The same 600-tick movement run (20 s at 30 Hz) at the
origin versus at an offset, over identically shaped terrain:

| Offset | Divergence after 20 s | Grounded ticks | Budget | Result |
|---|---|---|---|---|
| 0 m | 0.000 mm | 600/600 vs 600/600 | 10 mm | pass |
| 50,000 m | 821.978 mm | 600/600 vs 600/600 | 10 mm | fail, 82x |
| 100,000 m | 1,724.296 mm | 600/600 vs 600/600 | 10 mm | fail, 172x |

Three things that measurement proves, and one it does not.

**It proves the error accumulates.** The single-tick float32 quantum at 100 km is one ULP of the binade
`[65536, 131072)`, which is `2^17 * 2^-24 = 7.8 mm`. The measured 20 s divergence is 220x that. The error is
not a fixed rounding floor, it grows with running time.

**It proves the degradation is proportional to distance.** 822 mm at 50 km and 1,724 mm at 100 km is a ratio
of 2.097 for a 2x offset. Fitting a line through the origin off the more conservative point gives
**0.01724 mm of 20 s divergence per metre of offset**. That constant sizes everything in section 2.

**It proves reconciliation gets a coarser quantum at range.** Any comparison of two positions carries the
error of the magnitude they are expressed at, so the further out a player runs, the blunter every
prediction-divergence measurement becomes.

**It does not prove a client/server desync.** Both heads run identical float32 arithmetic at identical
coordinates, and the grounded-tick counts agree exactly at every offset (600/600 both sides). This is
fidelity degradation, not disagreement. The design must not be justified as a desync fix, and a test that
claims to reproduce a desync at range is testing the wrong thing.

### The engine already recorded the same failure at 1.7 km

`docs/design/AIRBORNE-MOMENTUM-DESIGN-2026-07-26.md`, in its Deferred section, records an independent
measurement of the same physics. The airborne-momentum clip's undenied tolerance is a fraction of the
intended speed while the rounding it defends against is a fraction of the coordinate, so the two stop
lining up once one float step of the position exceeds 0.1 percent of a tick's travel, at roughly
`|coordinate| > speed * dt * 16800`. That is about **1.7 km at walk speed and 60 Hz**, 3.4 km at 30 Hz.
Measured: a 6 m/s arc at 5 km sheds 0.0096 m/s of carried velocity per ten-second flight, before and after
the fix, unchanged.

This is a second, independent, already-measured constraint from a different subsystem, and it is far tighter
than 100 km. It is load-bearing for section 2: the working radius has to be hundreds of metres, not
kilometres.

## 2. The consumer contract, and the number that falls out of it

One shared float32 world space, metres, origin-centred (Ruinborne axis 1 option C: continents and islands in
one coordinate space, seamless walking within a continent, teleports between continents). Authored
coordinates and persisted state stay in that space, unchanged, forever. What the design decides is where and
how a **runtime** rebase happens so that everything a player experiences at 100 km is as good as at the
origin.

Working radius, derived rather than picked:

- Divergence budget 10 mm over a 20 s window at 0.01724 mm per metre gives a ceiling of **580 m**.
- The airborne-momentum exact branch gives a ceiling of **1,680 m** at 60 Hz walk speed.
- 580 m binds. Target a comfortable fraction of it.

**`WorldFrame.Grid = 128 m`**, a power of two so the anchor is exactly representable and the rebase
arithmetic is exact (section 8). Anchor to the NEAREST grid point, not the containing cell, so the local
coordinate lives in `[-64, 64]` per axis at anchor time. Re-anchor when a local axis exceeds 96 m, a 32 m
hysteresis band that stops the anchor flapping at a boundary.

Worst case: 96 m per axis, planar magnitude 136 m. Extrapolated 20 s divergence **2.3 mm against a 10 mm
budget, 4.3x margin**, and 12x inside the airborne bound. A player sprinting at 6 m/s re-anchors roughly
every 20 s, so the accumulation window is bounded too, not only the magnitude.

The grid is a **constant, not a config knob**. Two peers with different grids decode different world
positions from the same wire bytes, silently, and the value is derived from a measured budget rather than
from anything a game authors. A game that never leaves the origin never re-anchors and pays nothing.

## 3. Decision 1: the rebasing model

### The criteria

Weighted 1 to 3, scored 0 to 5, maximum 70.

| # | Criterion | Weight | Why it is weighted there |
|---|---|---|---|
| 1 | Fixes the measured accumulation | 3 | The thing the measurement forced. A model that only fixes visuals fails the brief. |
| 2 | Server multi-player at 100 km | 3 | A shard server simulates MANY players spread over the whole world. This is the criterion that eliminates a candidate outright. |
| 3 | Determinism across peers | 3 | A rebase changes coordinates and therefore changes float results. Any model that lets two peers simulate one entity in different frames has replaced one bug with a worse one. |
| 4 | Wire and netcode blast radius | 2 | Cost, not correctness. Recoverable. |
| 5 | Implementation risk in the engine | 2 | Cost, not correctness. Recoverable. |
| 6 | Consumer adoption cost | 1 | Ruinborne adopts incrementally either way. |

### The candidates

**A. Camera-relative rendering only.** Subtract the eye before the matrix build. Nothing touches simulation.

**B. Periodic global origin shift.** Translate the whole world when the anchor strays past a threshold.
Classic floating origin.

**C. Region-local simulation.** Per-cell or per-region local frames, no camera-relative layer. Deepest
change, natural fit with `ShardHost`'s `CellCoord` grid.

**D. Layered.** Camera-relative rendering unconditionally, plus a sim-side frame whose anchor is a property
of the simulated entity, quantized to the grid, authored by the server and replicated.

### The scores

| Criterion | Weight | A | B | C | D |
|---|---|---|---|---|---|
| 1 Fixes the accumulation | 3 | 0 | 2 | 5 | 5 |
| 2 Server multi-player at 100 km | 3 | 0 | 0 | 5 | 5 |
| 3 Determinism across peers | 3 | 5 | 1 | 3 | 5 |
| 4 Wire and netcode blast radius | 2 | 5 | 2 | 2 | 3 |
| 5 Implementation risk | 2 | 5 | 2 | 1 | 3 |
| 6 Consumer adoption cost | 1 | 5 | 3 | 1 | 4 |
| **Weighted total** | | **40** | **20** | **46** | **61** |

### Why B scores zero on criterion 2, which is what kills it

A shard server simulating 200 players spread over 100 km has one process and, under B, one origin. Whatever
origin it picks, some player is 100 km from it, which is exactly the failure being fixed. A single global
shift cannot follow many anchors. This is structural, not a tuning problem, and no threshold policy repairs
it. B is a single-player and client-only model presented as a world model.

B also scores 1 on determinism for a related reason: the client shifts when its own player strays, the
server shifts on whatever policy it has, so the two heads simulate the same entity in different frames at
different times, and float32 results differ between frames even for a pure translation. Reconciliation then
compares two states that were never in the same space.

### Why C loses to D despite scoring well on the criteria that matter

C is right about the shape and wrong about where the frame is anchored. Anchoring to a `CellCoord` makes the
frame a property of the SERVER's spatial partition, which the client does not have. The client would have to
derive the same cell independently, from a position its prediction may have carried one tick past a boundary,
and a derived frame that disagrees for one tick is a 60 m discontinuity in a reconciliation compare. C also
scores 1 on implementation risk because it changes the sharded and non-sharded server heads and the client
simultaneously, with no useful intermediate state.

### Decision: D, layered

Two layers that ship separately and are useful separately.

**The render layer is unconditional and touches no simulation.** Everything drawn is expressed relative to a
quantized render origin before any matrix is built. This is A, kept whole inside D, and it is worth shipping
alone because it fixes every visual artifact at range with zero determinism risk.

**The sim layer makes the frame a property of the entity, not of the observer.** This is the decision that
makes the whole thing work, and it is the one thing C and B both get wrong. An entity carries its own anchor.
The server authors it. The wire replicates it. The client adopts it rather than deriving it. Two heads
simulating one entity are then in the same frame by construction rather than by agreement, which is what
turns determinism from criterion 3's hardest problem into a non-problem.

The per-entity frame collapses in practice. Only SIMULATED entities need one: on the client that is the
single local player, and on the server the entities inside one `CellSim`, which are within one cell of each
other by definition. So one anchor per simulation island covers everything, and a remote entity 100 km away
sits in its own frame and never contaminates the local player's.

## 4. Decision 2: who owns the origin state

`WorldFrame` lives in `KhaozEngine.Primitives`, the dependency-free bottom of the render and runtime stack.
This costs **zero new project references**: `Render3D`, `Locomotion` and `Simulation.Tests` reference
`Primitives` directly, `NetWorld` reaches it through `Locomotion`, and `Terrain.Render3D` through `Render3D`.
`KhaozEngine.Physics` stays dependency-free (it needs only a `Vector3` delta) and `KhaozEngine.Netcode`
stays on `Netcode.Abstractions` (it needs only a `Vector2` anchor to difference). No shared type is pushed
across the two stacks, because none is needed.

```csharp
namespace KhaozEngine.Primitives;

/// <summary>A quantized planar simulation/render frame: the anchor is <c>(X, 0, Z) * Grid</c> metres, always
/// exactly representable in float32. Y is NEVER framed (see the design doc, section 6). <c>default</c> is the
/// world origin, so a game that never leaves the origin is byte-identical to the pre-frame engine.</summary>
public readonly record struct WorldFrame(short X, short Z)
{
    /// <summary>Frame spacing in metres. A CONSTANT, not a knob: two peers on different grids silently decode
    /// different world positions from the same bytes. 128 is derived from the measured divergence budget.</summary>
    public const float Grid = 128f;

    /// <summary>Half-grid plus the 32 m hysteresis band: the local-axis magnitude that triggers a re-anchor.</summary>
    public const float ReanchorRadius = 96f;

    public static WorldFrame Origin => default;

    /// <summary>The frame's world-space anchor point. Exact in float32 for every representable X/Z.</summary>
    public Vector3 Anchor => new(X * Grid, 0f, Z * Grid);

    /// <summary>The frame whose anchor is NEAREST <paramref name="world"/> (round, not floor), so a freshly
    /// anchored local coordinate lies in [-64, 64] per axis.</summary>
    public static WorldFrame Nearest(Vector3 world);
    public static WorldFrame Nearest(float worldX, float worldZ);

    /// <summary>World -> frame-local. X and Z are shifted, Y passes through unchanged.</summary>
    public Vector3 ToLocal(Vector3 world);

    /// <summary>Frame-local -> world.</summary>
    public Vector3 ToWorld(Vector3 local);

    /// <summary>The EXACT translation that carries a local coordinate in this frame into
    /// <paramref name="target"/>. Both anchors are integer multiples of <see cref="Grid"/>, so the delta is an
    /// integer multiple of 128 and the addition is exact under the section 8 magnitude precondition.</summary>
    public Vector3 DeltaTo(WorldFrame target);

    /// <summary>True when <paramref name="local"/> has drifted past <see cref="ReanchorRadius"/> on either
    /// planar axis. Y is ignored.</summary>
    public static bool ShouldReanchor(Vector3 local);
}
```

**How a shift propagates: as data on the state, never as an event.** There is no `OriginShifted` event and no
per-frame origin parameter threaded through call chains. The anchor is a field on the replicated state, so it
arrives with the position it applies to and can never be reordered against it, dropped, or applied to the
wrong tick. An event would have all three failure modes. This is the same reasoning that put `TeleportEpoch`
on `MovementState` rather than in a message kind.

**Consumer-facing API, client head:** nothing, in the common case. `Scene3D` picks its own render origin,
`WorldClient` adopts the server's anchor, and `ReplicatedPosition.Value` still reads and writes absolute
world metres. A consumer that owns a physics world calls `IPhysicsWorld.Translate` from the
`WorldClient.FrameChanged` callback (section 5) and offsets its own streamed collider poses.

**Consumer-facing API, server head:** set `WorldServerConfig.FrameAnchoring` (default on for the sharded head,
off for the flat head, see section 7's limitation), and supply frame-local samplers if it wants the full fix
rather than the accumulation half (section 6).

## 5. Decision 3: physics

### Bepu feasibility, verified by compiling and running against BepuPhysics 2.4.0

The issue's survey said `IPhysicsWorld` has no pose setter and no bulk rebase, and that rebasing live bodies
needs new API or remove-and-re-add. The first half is right. The second half's pessimistic branch is wrong,
and the difference decides this section, so it was checked by building a probe against the real package
rather than by reading the XML docs.

| Probe | Result |
|---|---|
| `sim.Bodies[h].Pose.Position += delta` | Compiles. `BodyReference.Pose` is a ref-returning property. The write takes effect. |
| `sim.Statics[h].Pose.Position += delta` | Same. `StaticReference.Pose` is also ref-returning. |
| `BodyReference.UpdateBounds()`, `Statics.UpdateBounds(handle)` | Refit the broadphase for the new pose without waking anything. |
| Direct pose write on a SLEEPING body (set index 1, an inactive island) | Body stays asleep. `Awake` stays false across 60 further steps and it moves exactly 0.000000 m. |
| Enumeration | `Bodies.Sets[s].IndexToHandle` over every allocated set covers active AND sleeping bodies. `Statics.IndexToHandle` covers statics. |
| A settled 4-box contact stack translated 100 km mid-simulation | Survives. Max drift 0.365 mm on the top box after 60 steps, residual velocity 2.5e-5 m/s. No impulse, no explosion, no lost contacts. |

So a bulk translate is O(n) direct pose writes plus O(n) broadphase refits, with **no remove-and-re-add, no
shape rebuild, no lost sleep state, and no lost contacts**.

One implementation landmine the probe surfaced. `Statics.ApplyDescription` is the obvious API and it is the
wrong one: its own doc says it forces every sleeping body whose bounds overlap the old or new collidable
active. Using it for a rebase would wake the entire sleeping population of the world on every shift, which is
the single most expensive thing a rebase could possibly do. The direct pose write plus `Statics.UpdateBounds`
does not.

### Constraints are already translation-invariant

`ConstraintFactory` converts world poses into body-LOCAL offsets at build time
(`KhaozEngine.Physics.Bepu/ConstraintFactory.cs:191`, `Vector3.Transform(r.PoseB.Position - r.PoseA.Position,
invA)`), so a uniform translate of both ends preserves every joint exactly. World-space anchor ends are
shapeless kinematic BODIES created in `BepuPhysicsWorld.ResolveEnd`, which means a full sweep over
`Bodies.Sets` already covers them. Nothing constraint-specific is needed.

### Decision: bulk pose translate on the existing world

The alternatives, and why they lose:

- **Per-region physics worlds.** One `IPhysicsWorld` per cell. A character at a cell boundary sweeps into the
  neighbour's world and hits nothing, so every query needs multi-world fan-out and result merging. Deferred,
  not precluded: `CellSim` already owns one `World` each, so a consumer that wants one physics world per cell
  can already build it, and the `Translate` API works per-world regardless.
- **Remove and re-add.** Rebuilds every shape, destroys every contact cache, wakes every sleeper, tears down
  every constraint. The probe shows there is no reason to pay any of that.

```csharp
namespace KhaozEngine.Physics;

public interface IPhysicsWorld : IDisposable
{
    /// <summary>Whether this backend implements <see cref="Translate"/>. A backend that returns false (the
    /// default, including any consumer test double) cannot serve a world large enough to need rebasing.</summary>
    bool CanTranslate => false;

    /// <summary>Translate EVERY static, every dynamic body (awake and sleeping alike), and every world-space
    /// constraint anchor in this world by <paramref name="delta"/>. Velocities, sleep state, contacts and
    /// constraints are all preserved: this is a change of coordinate space, not a physical event, and nothing
    /// in the world can observe it.
    /// <para>This interface has no notion of "world" versus "local" space. It IS a coordinate space, and every
    /// query (<see cref="Raycast"/>, <see cref="SweepCapsule"/>, <see cref="ComputePenetration"/>) is in
    /// whatever space the contents currently sit in. The caller owns which space that is.</para>
    /// <para>Must be called BETWEEN steps, never during one.</para></summary>
    void Translate(Vector3 delta) => throw new NotSupportedException(
        "This IPhysicsWorld backend does not support Translate. Check CanTranslate first.");
}
```

Both members are default interface implementations, so this is an **additive minor**, not a breaking change.
Every existing consumer implementation of the seam, including headless test doubles, keeps compiling and
correctly reports that it cannot rebase. The engine already uses this exact pattern to evolve a public
interface: `IPredictedState<TSelf>` grew `Vertical`, `TeleportEpoch` and `StepDeltaY` as DIMs.

`BepuPhysicsWorld` overrides both. `CanTranslate => true`.

### Contact and sleeping behaviour across a shift, stated

- A sleeping body stays asleep, at its translated pose, and does not move on the next step. Verified.
- An awake body in contact keeps its contacts and its solver state. The stack probe drifted 0.365 mm at
  worst, and that drift is a float32 artifact of the DESTINATION magnitude (100 km), not of the shift itself.
  A shift into a 136 m frame has no such term.
- Nothing is woken. That is the whole point of not using `ApplyDescription`.
- A shift called mid-step is undefined. The caller sequences it between steps. The `PlayerMoveSimulator`
  owns that ordering for the shipped path.

### The gotcha the survey missed: baked world-space collision vertices defeat the rebase

`TerrainChunkCollision.Build` copies ABSOLUTE world-space vertex positions into the `TriangleMeshShape` and
`ChunkTerrainCollision.Add` registers it at `Pose.Identity`, because a Bepu mesh is not recentred
(`KhaozEngine.Terrain.Render3D/ChunkTerrainCollision.cs:29`). `Translate` moves the POSE, so the chunk does
move. But the mesh's own triangle vertices are still 100 km numbers, so every triangle test inside Bepu still
runs at 100 km magnitude, and the rebase buys terrain collision nothing.

The source is upstream, in `TerrainChunkBuilder.Build`
(`KhaozEngine.Terrain.Render3D/TerrainChunkBuilder.cs:34-40`), which writes
`new Vector3(region.OriginX + fraction * region.Size, h, ...)` into the vertex. At 100 km those grid
positions quantize to the 7.8 mm float32 lattice at BAKE time, in the render mesh and in the collision mesh
built from it.

Fix, in `TerrainChunkBuilder`: keep sampling the field at absolute `(x, z)` (the field is authored in world
space and must stay that way), but STORE the vertex chunk-local, `x - region.OriginX`, and carry
`region.OriginX/OriginZ` in the transform (render) and the static pose (collision). A chunk 100 km out then
has vertices of magnitude at most `Size` (60 m by default), exactly as precise as a chunk at the origin, and
is invariant under rebase because only its pose moves.

This is a prerequisite for the physics half of the fix, not an optimization, so it ships with it.

## 6. Decision 4: movement and locomotion, the hardest consistency question

### The step function needs no change at all

`CharacterMovement.Step` takes the position inside `MoveState` and reaches the world only through
caller-supplied delegates on planar coordinates: `groundHeight(x, z)`, `groundNormal(x, z)`,
`clampXz(x, z)`, `medium(x, z, feetY)`, plus `IPhysicsWorld` queries in the same space. Gravity acts on
velocity, not on absolute Y. There is no absolute world constant anywhere in the step.

So the step is **translation-invariant by construction**. Feed it a position in a frame and delegates that
sample in the SAME frame, and it produces results bit-identical to the origin-local case. Not approximately,
identically, because it is the same arithmetic on the same operands. `CharacterMovement` is not modified by
this program.

### The consistency question, and why the answer is "authoritative, not derived"

Both heads must step the same entity in the same frame, or a pure translation produces different float
results and reconciliation compares states from two spaces. The brief calls this the hardest question in the
spec and it is, but only because the tempting answer is wrong.

The tempting answer is that each head derives the frame from the position it holds. That fails at exactly the
moment it matters: the client's prediction may sit one tick past a re-anchor boundary that the server has not
crossed yet, so for one tick the two heads derive different anchors and every downstream comparison is off by
128 m.

The answer is that **the server authors the anchor and the client adopts it**. The anchor is authoritative
state, exactly like position, and it rides the same wire field (section 7). A client that computes its own
anchor is a bug, and section 10 has a test that asserts it.

This also resolves the "server and client have different anchors if the model is per-player" worry in the
brief. They do not, because the anchor is not per-observer. It is per-entity, and there is exactly one
authoritative value for it.

### `PlayerMoveSimulator` is the choke point, and it already is one

`PlayerMoveSimulator` holds the delegates and the `IPhysicsWorld`, and its `Step` is the shared
`ITickSimulator<PlayerMoveState, MoveCommand>` used identically by `WorldServer.Tick`,
`PlayerMovementSystem`, and `ClientPrediction.Predict`/`Reconcile`. One type, one place, both heads.

```csharp
namespace KhaozEngine.NetWorld;

/// <summary>The coordinate space the caller's sampler delegates read.</summary>
public enum SamplerSpace
{
    /// <summary>Samplers take ABSOLUTE world coordinates. The simulator adds the frame anchor back before
    /// calling them. Correct, and it fixes the ACCUMULATION half of the problem (the carried state is
    /// frame-local), but each sample coordinate is still evaluated at world magnitude, so the sampling
    /// quantum at 100 km is still 7.8 mm. The zero-work adoption step.</summary>
    World = 0,

    /// <summary>Samplers take FRAME-LOCAL coordinates and the simulator passes them straight through. The full
    /// fix. A consumer whose ground follow comes from a rebased IPhysicsWorld with chunk-local collision
    /// meshes (section 5) gets this for free.</summary>
    Frame = 1,
}

public sealed class PlayerMoveSimulator : ITickSimulator<PlayerMoveState, MoveCommand>
{
    public PlayerMoveSimulator(
        Func<float, float, float> groundHeight,
        MoveTuning tuning,
        Func<float, float, Vector3>? groundNormal = null,
        WorldBounds? bounds = null,
        IPhysicsWorld? physics = null,
        Func<float, float, float, MovementMedium>? medium = null,
        SamplerSpace samplerSpace = SamplerSpace.World);

    /// <summary>The frame this simulator currently steps in. Set by the owner (the server tick for an
    /// authoritative step, <c>WorldClient</c> for a predicted one) BEFORE the step, and matched by a
    /// <see cref="IPhysicsWorld.Translate"/> on <c>physics</c> when it changes. Default
    /// <see cref="WorldFrame.Origin"/> is byte-identical to the pre-frame simulator.</summary>
    public WorldFrame Frame { get; set; }
}
```

`SamplerSpace.World` defaulting to on is deliberate. It is the state every existing consumer is already in,
it is a genuine improvement over today (the accumulating carried state becomes frame-local, which is the
term the measurement showed growing), and it lets a consumer adopt the release without touching its terrain
sampling. `SamplerSpace.Frame` is the finish line, and Ruinborne reaches it when its collision meshes go
chunk-local.

### Server-side ordering

Per tick, per simulation island: set `Frame` on the simulator, step every player, then re-anchor. Re-anchoring
after the step rather than before means the anchor is a function of a settled position and the whole tick ran
in one frame. On the sharded head each `CellSim` is one island with one frame, which is why the sharded head
is the one that scales (section 7).

## 7. Decision 5: wire and prediction

### `ReplicatedPosition` goes frame-relative

The issue's survey is right that `MoveProtocol.cs:93-97` is the one registration site for position, and it is
the whole reason this is tractable.

```csharp
namespace KhaozEngine.NetWorld;

public struct ReplicatedPosition : IComponent
{
    /// <summary>The frame <see cref="Local"/> is expressed against. AUTHORITATIVE: written by the server,
    /// adopted by the client, never derived on the receiving side.</summary>
    public WorldFrame Frame;

    /// <summary>Position relative to <see cref="Frame"/>. X and Z are frame-local, Y is absolute world height
    /// (Y is never framed).</summary>
    public Vector3 Local;

    /// <summary>The absolute world position. Reads and writes exactly as the pre-frame field did, so every
    /// existing consumer compiles and behaves identically. The setter keeps the CURRENT frame and stores the
    /// difference: it never re-anchors, because a setter that quantized would make the anchor flap at every
    /// boundary and defeat the hysteresis.</summary>
    public Vector3 Value { readonly get; set; }

    /// <summary>Re-anchor to the frame nearest <see cref="Value"/> if <see cref="Local"/> has drifted past
    /// <see cref="WorldFrame.ReanchorRadius"/>. Returns true when the frame changed. Server-side only.</summary>
    public bool Reanchor();

    /// <summary>This position expressed against <paramref name="target"/>. Exact (section 8).</summary>
    public readonly ReplicatedPosition ToFrame(WorldFrame target);
}
```

`default(ReplicatedPosition)` is `Frame = Origin`, so a game at the origin is byte-identical to today.

Wire: `short FrameX, short FrameZ, float LocalX, float LocalY, float LocalZ`, 16 bytes against today's 12.
The 4 extra bytes buy a wire whose float payload is bounded at 136 m, where one ULP is 7.6 micrometres, so
**the wire itself stops being a quantizer**. Today at 100 km it quantizes every replicated position to 7.8
mm before anything downstream sees it.

`short` covers plus or minus 4,194 km of world. Ample, and a hard bound is better than a silent wrap.

The codec's `lerp` rebases into the newer frame FIRST and then interpolates locals:

```csharp
lerp: (a, b, t) =>
{
    ReplicatedPosition ar = a.ToFrame(b.Frame);   // exact when the frames differ, a no-op when they match
    return new ReplicatedPosition { Frame = b.Frame, Local = Vector3.Lerp(ar.Local, b.Local, t) };
}
```

Lerping the two locals directly without the rebase would interpolate between two different spaces and place
a remote a frame-width away. Decoding both to world and lerping there would be correct but would throw away
the precision the encoding just bought. This branch is both correct and precision-preserving.

### An origin shift does NOT get its own protocol message

It rides `ReplicatedPosition`. A separate shift message could arrive out of order relative to the position it
applies to, be dropped, or be applied on the wrong tick, and all three place an entity a frame-width from
where it is. Carrying the frame in the same component makes those failures unrepresentable.

### Prediction: a shift mid-replay must not manufacture a correction, and today it would

Two distinct failures exist in `ClientPrediction.Reconcile` as written, and both must be fixed in the same
change.

**The hard-snap gate fires.** `Reconcile` computes

```csharp
Vector2 planarError = oldPlanar - predictedState.Position;
```

where `oldPlanar` is the pre-rebase prediction and `predictedState.Position` is the post-replay state.
Across a re-anchor those are in different frames, so the difference is the 128 m anchor delta.
`PredictionSettings.HardSnapDistance` defaults to 100 m
(`KhaozEngine.Netcode/PredictionSettings.cs:22-27`), so the gate trips and the avatar hard-cuts on a shift
that is a no-op in world space.

**The render offset absorbs the whole anchor delta.** Even past the gate, the C1 branch re-anchors
`renderOffset = renderedPlanar - Vector2.Lerp(previousPredictedPosition, predictedState.Position, frac)`
with `renderedPlanar` still in the old frame, so the smoothing offset picks up 128 m and then decays it,
gliding the avatar a frame-width across the screen over the smoothing window. This is worse than the hard
snap because it looks like a physics bug rather than a teleport.

The fix is one place: at the TOP of `Reconcile`, convert the captured presentation state into the incoming
basis's frame before any existing math runs.

```csharp
namespace KhaozEngine.Netcode;

public interface IPredictedState<TSelf>
{
    /// <summary>The planar (XZ) anchor this state's <see cref="Position"/> is expressed against. Reconciliation
    /// differences two anchors to convert the pre-rebase presentation state into the incoming basis's frame, so
    /// a re-anchor measures as zero prediction error and glides nothing. Defaults to <c>Vector2.Zero</c>, so a
    /// state with no frame concept behaves exactly as before.</summary>
    Vector2 FrameAnchor => Vector2.Zero;
}
```

Then, before the replay loop:

```csharp
Vector2 frameDelta = predictedState.FrameAnchor - authoritativeBasis.FrameAnchor;
Vector2 oldPlanar = predictedState.Position + frameDelta;
float   oldVertical = predictedState.Vertical;              // Y is never framed
previousPredictedPosition += frameDelta;
// renderOffset is a DELTA and is frame-invariant, so it is untouched.
```

After that, `planarError` measures only real prediction divergence, `planarRebase` excludes the anchor delta,
and `renderOffset` re-anchors against a same-frame target. The shift becomes invisible, which is exactly what
it should be.

`Vector2` and a subtraction: no new dependency on `KhaozEngine.Netcode`, which stays on
`Netcode.Abstractions`.

### The pending-command buffer is already frame-invariant, which is the reason this is cheap

`MoveProtocol.EncodeMove` sends a 2D move axis, run/jump flags and a camera yaw, never a position. So the
buffer `Reconcile` replays holds INPUTS. Replaying inputs from a new-frame basis simply produces new-frame
results, with no per-command conversion and no risk of a half-converted buffer. Nothing about the replay loop
changes.

### `TeleportEpoch` is deliberately NOT reused for this

An epoch advance means "cut instantly, this is an intentional discontinuity". A re-anchor is the exact
opposite: a no-op in world space that must be invisible. Overloading the epoch would hard-cut the avatar on
every 128 m of travel. The frame is a separate channel because it carries the opposite meaning.

### `WorldClient` surfaces the change so a consumer can move its physics world

```csharp
namespace KhaozEngine.NetWorld;

public sealed partial class WorldClient
{
    /// <summary>Raised when the local player's authoritative frame changes, BEFORE the next predicted step.
    /// The argument is the EXACT translation to apply to anything the consumer holds in the old frame: its
    /// <see cref="IPhysicsWorld"/> (one <c>Translate</c> call), and any collider poses it registers itself.
    /// The engine's own state (predicted state, presentation, physics owned by the simulator) is already
    /// converted by the time this fires.</summary>
    public event Action<WorldFrame, WorldFrame, Vector3>? FrameChanged;   // from, to, delta
}
```

### The limitation, stated rather than buried

The flat `WorldServer` has one `World`, one flat player loop, and one physics world, so it has exactly one
frame, which is candidate B and does not scale to 100 km with spread players. **A 100 km world with players
spread across it requires the sharded head**, where `ShardHost` gives one `CellSim` (and therefore one
frame) per cell. Ruinborne is a sharded MMO, so this is the head it already uses, and it is what `ShardHost`
exists for. The flat head still gets the full benefit for a single-player or single-region game, and is not
regressed in any case.

## 8. Decision 8: determinism

### The rebase is exact, under a precondition that must be enforced

**Lemma.** Let `L` be a float32 local coordinate with `|L| < 128`, and let `k * 128` be an exact integer
multiple of the grid. If `|L + k * 128| <= |L|`, then `L + k * 128` is exactly representable and the addition
introduces no error.

*Why.* `|L| < 128` puts `L` in a binade no higher than `[64, 128)`, so `L` is an integer multiple of
`ULP(L) <= 2^-17`. `k * 128` is an integer, hence also a multiple of `2^-17`. The exact sum is therefore a
multiple of `2^-17`. If its magnitude does not exceed `|L|`, its binade does not exceed `L`'s, so its ULP does
not exceed `2^-17` and it is representable exactly.

**The precondition is why the anchor rounds to nearest rather than flooring.** Round-to-nearest gives a
freshly anchored local in `[-64, 64]`, and the re-anchor trigger is `|local| > 96`, so a re-anchor always
strictly reduces the per-axis magnitude and the lemma applies. Flooring to the containing cell would put the
local in `[0, 128)` and a re-anchor could carry `-32` to `+96`, growing the magnitude, at which point the
translation rounds. The error would be at most `2^-18` m, about 4 micrometres, harmless against a 10 mm
budget but fatal to the claim of bit-identity, which is the claim that makes cross-peer determinism provable
rather than merely likely.

Y is never framed, so it is untouched.

### Both peers rebase identically because neither derives anything

The anchor is authoritative and replicated (section 7). There is no derivation to disagree about. This is the
whole reason decision 1 chose an authored anchor over `CellCoord`.

### Relationship to `DeterministicFpScope` and #197: orthogonal, and both are needed

`DeterministicFp.SetCanonical` pins the FP control register: round-to-nearest-even, FTZ and DAZ off, traps
masked. Floating origin pins operand MAGNITUDE. They are independent axes of the same problem and neither
substitutes for the other. Two peers with pinned FP registers still diverge at 100 km. Two peers in the same
frame still diverge if one has FTZ on.

Two facts worth recording rather than assuming:

- `DeterministicFpScope` is used **nowhere in engine production code**. A repo-wide search finds it only in
  `KhaozEngine.Foundation.Tests/DeterministicFpTests.cs` and `DeterministicFpHarnessTests.cs`. No tick loop,
  no `ShardHost.Tick`, no `CellSim.Tick`, no netcode path enters it.
- [#197](https://github.com/APKiwiOrg/KhaozEngine/issues/197) is the specific consequence:
  `ThreadPoolJobScheduler.For` hands cell ticks to arbitrary `Parallel.For` pool threads whose FP register is
  whatever the pool last left it at.

This program does not fix #197 and must not be described as fixing it. A cross-peer bit-identity claim needs
both, and #197 should land before or alongside release 3, because release 3 is the first one whose
correctness argument depends on bit-identity across peers.

## 9. Decision 6: rendering

### Where the subtraction happens: inside `Scene3D`, at submission

The camera's `View` and every world-space position submitted for the frame must be expressed against the
same origin. `Scene3D` already owns both sides, so it does the subtraction and the public API keeps taking
absolute world coordinates. A consumer changes nothing.

```csharp
namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    /// <summary>The origin every world position submitted this frame is expressed against before it reaches the
    /// GPU. Defaults to <c>WorldFrame.Nearest(ActiveCamera.Eye).Anchor</c>, quantized so it does not jitter
    /// per frame (an unquantized eye-following origin makes goldens irreproducible). Set it explicitly to the
    /// sim frame's anchor when running a sim frame, so render and simulation share one space.
    /// <c>Vector3.Zero</c> reproduces the pre-floating-origin output exactly.</summary>
    public Vector3 RenderOrigin { get; set; }
}
```

### The camera

Each of the three cameras gains one field, and `View` subtracts it from BOTH eye and target. The default
`Vector3.Zero` is byte-identical to today, and because all twenty-odd consumers in `Scene3D` go through
`ActiveCamera.ViewProjection`, they are all fixed by this one change.

```csharp
// FollowCamera3D, FlyCamera3D, IsoCamera3D
/// <summary>The render origin subtracted from eye and target when building <see cref="View"/>. Set by
/// Scene3D each frame. Eye stays ABSOLUTE world (culling and the origin choice both need it).</summary>
public Vector3 RenderOrigin;

public Matrix4x4 View => Matrix4x4.CreateLookAt(Eye - RenderOrigin, EffectiveTarget - RenderOrigin, Vector3.UnitY);
```

**`WorldToScreen` and `ScreenToRay` must be fixed in the same commit or picking silently breaks.** Both take
or return absolute world points and both go through `ViewProjection`, which is now render-relative.
`WorldToScreen` subtracts `RenderOrigin` from its input, `ScreenToRay` adds it back to its output. Missing
this produces a picking error equal to the render origin, which at 100 km means picking simply does not work
and no golden catches it.

### `Transform3D`

```csharp
/// <summary>World matrix built against a render origin: identical to <see cref="ToMatrix()"/> with the
/// translation reduced by <paramref name="renderOrigin"/>. This is what kills the catastrophic cancellation
/// of concatenating a 100 km world translation with a 100 km view translation.</summary>
public Matrix4x4 ToMatrix(Vector3 renderOrigin);
```

The existing no-argument overload stays, delegating with `Vector3.Zero`. The binder passes the scene's
render origin.

### Everything else that carries a world position, and where it is handled

All of these are per-frame queues cleared in `Scene3D.Begin()`, so the subtraction happens once as the value
lands in its queue. None of them carries cross-frame state, so there is nothing to migrate on an origin
change.

| Path | Field | Handled at |
|---|---|---|
| Point lights | `ModelRenderer.PointLightData.PosRadius` (xyz) | `Scene3D.AddLight` |
| Ground decals | `GroundDecal.Center` | `Scene3D.DrawGroundDecal` |
| Shadow blobs | `ShadowBlob.Position` | `Scene3D.AddShadowBlob` |
| Water planes | `WaterPlane.CenterX`, `CenterZ` (`SurfaceY` is absolute Y, untouched) | `Scene3D.DrawWater` |
| Particle sprites | `ParticleSprite.Position` | `Scene3D.DrawParticle` and its span overload |
| Distortion sprites | `DistortionSprite.Position` | `Scene3D.DrawDistortion` and its span overload |
| Lines, fills, billboards, beams, trails | vertex positions | their submission entry points |

**Particles** need nothing beyond that. `ParticleSystem` integrates `p.Position += p.Velocity * dt` in
absolute world space and `ParticleAttractor.Target` is absolute, but a particle's lifetime is seconds and its
travel is metres, so its absolute magnitude is whatever its emitter's is. The subtraction at
`Scene3D.DrawParticle` fixes the render. The SIMULATION of a particle at 100 km still integrates at 100 km
magnitude, which quantizes its per-tick motion to 7.8 mm. That is visible as coarse, steppy motion on slow
particles and is worth a follow-up, but it is not in this program's critical path and is filed rather than
fixed.

**Terrain chunk meshes** are the exception, and camera-relative rendering does NOT fix them. Section 5's
finding applies identically on the render side: `TerrainChunkBuilder` bakes absolute world positions into the
vertex buffer, so the error is already in the buffer before any matrix is built. The chunk-local bake is the
fix on both sides at once.

**`TerrainStreamer` needs no change at all.** It holds no persistent world-space float state (no `Vector3`
or `float` fields), and every frame it re-derives chunk coordinates from the caller-supplied `playerPos` via
`ChunkGrid.CoordOf`. Load, unload and ring selection are integer chunk-distance math. Only the LOD tier pick
uses a metre distance, and a distance is frame-invariant. It keeps taking absolute world coordinates.

**Skybox and fog.** The skybox is direction-only and translation-invariant. Fog is distance-based and
likewise. Neither changes.

**Depth precision at range is a different problem and this program does not fix it.** Depth resolution is
governed by the near-to-far ratio, not by the origin, and the engine is not on reversed-Z. Camera-relative
rendering will not improve z-fighting at a 200 m far plane and nobody should expect it to. Out of scope,
filed separately.

## 10. Decision 7: what does NOT change

- **Authored content coordinates.** Absolute world metres, in `MapDoc` and everywhere else. A frame is a
  runtime artifact with no representation on disk.
- **Persisted positions.** `PlayerRecord`'s flattened X/Y/Z and every `WorldStore` backend keep absolute
  world metres. A save that carried a frame would break the moment the grid constant changed, and there is no
  benefit: a save is written once, not accumulated across ticks.
- **`CellCoord` and `ChunkCoord` keys.** Computed from the ABSOLUTE position, always, never from a local
  coordinate. Two entities in different frames with the same local coordinate are kilometres apart, so a key
  built from a local would collide across frames. The absolute position stays exactly recoverable
  (`Frame.Anchor` is exact, so `Anchor + Local` is as precise as `Local`), and `ReplicatedPosition.Value`
  is the single accessor every keying site already uses.
- **`Vector3`, `Pose`, `Transform3D`, `MoveState`, `Particle`.** All stay float32. **No double-precision or
  fixed-point position type is introduced anywhere.** The point of the design is to keep magnitudes small,
  not to widen the type.
- **`CharacterMovement`.** Translation-invariant already (section 6).
- **`TerrainStreamer`.** Section 9.
- **`Y`.** Never framed, on any path.
- **`InterestGrid` and `ShardHost` handoff.** Both consume absolute positions and keep doing so.

One thing DOES change that reads like it should not: the `ReplicatedPosition` wire encoding, which is a
breaking wire-generation bump. It is why release 3 is a major (section 11).

## 11. Decision 9: the release split

Three releases. Each is independently useful, independently testable, and independently adoptable.

### Release 1, minor: camera-relative rendering

`Scene3D.RenderOrigin`, the three cameras' `RenderOrigin` field and origin-aware `View`, the fixed
`WorldToScreen`/`ScreenToRay`, `Transform3D.ToMatrix(Vector3)`, and the subtraction at every `Scene3D`
submission entry point in the section 9 table.

**What a consumer gets:** every visual artifact from matrix concatenation at range disappears. Model
placement, decals, lights, particles, water and debug geometry are all as stable at 100 km as at the origin.

**Adoption:** none. `RenderOrigin` defaults to a quantized eye and the public API still takes absolute world
coordinates. A consumer repins and gets it. A consumer that wants the pre-release output exactly (a golden
it has not rebaked) sets `Scene3D.RenderOrigin = Vector3.Zero`.

**Not fixed by this release:** terrain chunk vertices (release 2) and anything about simulation.

### Release 2, minor: the sim frame, physics rebase, and chunk-local terrain

`WorldFrame` in `Primitives`. `IPhysicsWorld.CanTranslate`/`Translate` as DIMs plus the `BepuPhysicsWorld`
implementation. `TerrainChunkBuilder` chunk-local vertices with the placement in the transform and pose,
and `TerrainChunkCollision`/`ChunkTerrainCollision` following. `PlayerMoveSimulator.Frame` and
`SamplerSpace`.

**What a consumer gets:** a single-anchor precise simulation. The client's local player and its physics
world stay inside a 136 m frame no matter where in the world it is, so movement, collision, ground follow and
stair climbing behave at 100 km exactly as at the origin. A single-player or single-region game is finished
at this release. Terrain geometry and terrain collision are precise at range on both heads.

**Adoption:** set `SamplerSpace.Frame` if the game's ground follow comes from the physics world (Ruinborne's
does), otherwise leave the `World` default and take the accumulation half. Re-bake any terrain golden.

**Not fixed by this release:** many players spread over 100 km on one server, and the reconciliation quantum
on the wire.

### Release 3, MAJOR: the wire and multi-player

Frame-relative `ReplicatedPosition` with the frame in the same component, the wire-generation bump in
`MoveProtocol`, `IPredictedState.FrameAnchor`, the `ClientPrediction.Reconcile` frame conversion,
per-`CellSim` anchors on the sharded head, and `WorldClient.FrameChanged`.

**Why major:** the `ReplicatedPosition` encoding changes, so client and server must ship together.

**What a consumer gets:** the full guarantee. A shard server simulating players spread over 100 km keeps
every one of them locally precise, and the reconciliation quantum stops depending on distance from the
origin.

**Adoption:** repin client and server together, handle `WorldClient.FrameChanged` by translating any
consumer-owned physics world, and use the sharded server head.

**Sequencing note:** [#197](https://github.com/APKiwiOrg/KhaozEngine/issues/197) should land before or with
release 3, because release 3 is the first release whose correctness argument rests on cross-peer bit-identity.

## 12. Decision 10: the test plan

### The acceptance test for the whole feature

Mirrors Ruinborne's `FarFromOriginPrecisionTests` exactly: the same 600 ticks at 30 Hz, the same 10 mm
budget, the same shape of comparison. Lands in `KhaozEngine.Server.Tests` (it references `Locomotion`,
`Physics.Bepu` and `NetWorld`).

> Run `PlayerMoveSimulator` for 600 ticks with an identical command stream, once at the origin and once at a
> 100,000 m offset with frame anchoring ACTIVE, over identically shaped terrain reached through a shifted
> sampling delegate. Assert the two trajectories agree within **10 mm**, and that grounded-tick counts match
> exactly.

Also assert the same at 50,000 m, and assert that the SAME test with anchoring disabled still fails at the
measured magnitudes (roughly 822 mm and 1,724 mm). A precision test that passes with the feature turned off
is measuring the harness, and this one has a known failing baseline to prove it is not.

### Invariant tests, `KhaozEngine.Foundation.Tests` (references `Primitives`)

1. **Re-anchor is bit-exact.** For a swept set of locals and frame deltas satisfying the section 8
   precondition, `frame.DeltaTo(target)` applied to a local reproduces the world position with a bit-identical
   round trip. Compare raw bits, not an epsilon.
2. **Round-to-nearest never grows a local's magnitude.** The precondition the lemma needs, asserted directly,
   so a future change to floor anchoring fails a test instead of silently rounding.
3. **Anchors are exactly representable.** `X * WorldFrame.Grid` round-trips for the whole `short` range.
4. **Hysteresis does not flap.** An entity oscillating across a boundary re-anchors at most once.
5. **`WorldFrame.Origin` is `default`**, and the whole API at the origin is byte-identical to unframed math.

### Physics tests, `KhaozEngine.Server.Tests` (references `Physics.Bepu`)

6. **Rebase round trip.** Build a world, record every pose, `Translate(d)`, `Translate(-d)`, assert poses are
   bit-identical.
7. **Sleeping bodies stay asleep.** Settle a body, `Translate`, assert `IsAwake` is false and it has not
   moved after 60 further steps. This is the property the probe verified and the one `ApplyDescription`
   would destroy, so it must be a regression test, not a comment.
8. **Contacts survive.** A settled stack, `Translate`, 60 steps, assert per-body drift under 1 mm and no
   velocity spike.
9. **Constraints survive.** A hinge and a slider, one end a world anchor, `Translate`, assert the joint still
   holds and the anchor moved with it.
10. **Statics move.** Raycast down onto a static, `Translate`, raycast at the translated coordinate, assert
    the same hit distance.
11. **`CanTranslate` is false and `Translate` throws on a seam default.** The DIM contract.

### Netcode tests, `KhaozEngine.Server.Tests`

12. **Wire round trip.** `ReplicatedPosition` at 100 km encodes and decodes to a bit-identical `Value`, and
    the decoded `Local` magnitude is under `ReanchorRadius`.
13. **Cross-frame lerp.** Two snapshots in different frames interpolate along the straight world-space line,
    with no frame-width excursion at any `t`.
14. **A re-anchor manufactures no correction.** Reconcile across a frame change and assert
    `ReconciliationResult.PositionError` is unchanged from the same scenario without the frame change, that
    `HardSnapApplied` is false, and that `renderOffset` did not pick up the anchor delta. This is the test for
    the two bugs section 7 identifies.
15. **The client never derives its anchor.** Feed the client a basis whose frame is deliberately not the one
    the client would pick, and assert the client adopts the server's. This asserts the decision-1 property
    directly.
16. **Legacy shape.** A game entirely at the origin produces byte-identical wire output to the pre-frame
    encoding for the `Local` triple, so the change is provably inert at the origin.

### GPU goldens, `KhaozEngine.Render.Tests/Gpu`

17. **`RenderOrigin = Vector3.Zero` reproduces existing goldens exactly.** The cheapest possible proof that
    release 1 is inert by default, and it needs no new golden.
18. **A new far-from-origin golden.** The same scene rendered at the origin and at 100 km with camera-relative
    rendering on must produce the same image within the existing cross-backend tolerance. This is the test
    that proves the render half, because the failure mode is visual jitter and vertex swim that no numeric
    assertion describes.
19. **Picking at range.** `WorldToScreen` and `ScreenToRay` round-trip at 100 km. Headless, no golden needed,
    and it catches the section 9 landmine that a golden cannot.

**A new golden needs the D3D11 plus Vulkan CI bake before it lands.** Run `cross-platform-gpu.yml` via
`workflow_dispatch` with `bake = true`, which renders the legs with `KE_UPDATE_GOLDENS=1` and uploads the
per-backend grids. A golden baked only on the Metal dev machine turns `main` red on the other two legs.

## 13. Premises from #337 that turned out to be wrong or incomplete

Checked against the code rather than assumed. The issue's survey is accurate on the whole and these are the
exceptions.

1. **"Rebasing live bodies needs new API on `IPhysicsWorld` and `BepuPhysicsWorld`, or remove-and-re-add.
   This is the hard half."** The API half is right. The "hard half" framing is not. `BodyReference.Pose` and
   `StaticReference.Pose` are ref-returning in Bepu 2.4, `UpdateBounds` refits the broadphase without waking
   anything, and a probe compiled against the real package shows a sleeping body rebased in place stays
   asleep and motionless, and a settled contact stack survives a 100 km translate with 0.365 mm of drift. The
   implementation is a loop over `Bodies.Sets` and `Statics.IndexToHandle`, not a rebuild. **Physics is the
   easy half. The netcode and reconciliation half is harder.**

2. **`IPhysicsWorld` "has only `AddDynamic` and `GetDynamicPose`."** Understated. It also has
   `SetDynamicVelocity`, `IsAwake`, and a whole constraint surface (`AddConstraint`, `RemoveConstraint`,
   `SetConstraintTarget`) with world-space anchor ends. The core claim (no pose setter, no bulk rebase) holds,
   but constraints are part of the blast radius and the survey does not mention them. They turn out to be
   translation-invariant already, verified at `ConstraintFactory.cs:191`.

3. **The blast radius omits the biggest single item: baked world-space vertices.**
   `TerrainChunkBuilder.Build` writes absolute world positions into terrain vertices
   (`TerrainChunkBuilder.cs:34-40`), and `TerrainChunkCollision`/`ChunkTerrainCollision` reuse those exact
   vertices for the collision mesh at `Pose.Identity`. At 100 km every vertex is quantized to the 7.8 mm
   float32 lattice at bake time, in the geometry the player sees AND in the geometry the player walks on.
   **Camera-relative rendering cannot fix this and neither can a physics `Translate`,** because the error is
   in the buffer before either runs. This is a whole workstream the survey does not list.

4. **"The three camera `View` getters and `Transform3D.ToMatrix` cover most of rendering."** True for matrix
   construction and misleading about coverage. `Scene3D` submits about a dozen independent world-space
   payloads that never pass through either (lights, decals, shadow blobs, water planes, particle and
   distortion sprites, line/fill/billboard/beam/trail vertices), and each needs its own subtraction. The
   survey names three of them. `WorldToScreen`/`ScreenToRay` are also missing and are the one place where
   getting it wrong is invisible to every golden.

5. **`TerrainStreamer.Update(playerPos)` is listed as blast radius. It is not.** It holds no persistent
   world-space float state, has no `Vector3` or `float` fields at all, and re-derives everything each call
   from the supplied position. It needs no change.

6. **The determinism framing is incomplete in a way that matters.** The issue points at #197 for "the FP
   environment side of the same story". They are orthogonal axes, not two halves of one story, and worth
   separating: FP environment pins rounding mode, floating origin pins magnitude, and a peer can fail either
   independently. The load-bearing fact neither the issue nor #197 states plainly is that
   **`DeterministicFpScope` is used nowhere in engine production code today**, only in two Foundation test
   files.

7. **The issue does not mention that the engine already measured this failure at 1.7 km.**
   `AIRBORNE-MOMENTUM-DESIGN-2026-07-26.md`'s Deferred section records the airborne clip breaking down at
   `|coordinate| > speed * dt * 16800`. That constraint is 60x tighter than 100 km and it is what forces the
   working radius to hundreds of metres rather than kilometres.

## 14. Deferred, and filed rather than fixed

- **Particle simulation at range.** `ParticleSystem` integrates absolute positions, so a particle at 100 km
  moves in 7.8 mm steps. Release 1 fixes how it is drawn, not how it moves. A frame-local particle system is
  its own change.
- **Depth precision at range.** Reversed-Z or logarithmic depth. Governed by the near-to-far ratio, unrelated
  to the origin, and explicitly not fixed here so nobody expects it to be.
- **Per-region physics worlds.** One `IPhysicsWorld` per `CellSim` with cross-boundary query fan-out. Not
  needed for 100 km and not precluded by anything above.
- **The two duplicated grid conversions.** `Replication/InterestGrid.cs:72` privately re-implements
  `Sharding/CellCoord.cs:38`'s `floor(v / cellSize)` rather than calling it, and `Terrain/ChunkGrid.cs:15` is
  a third copy. Nothing in this program depends on unifying them, but a floating-origin change that had
  touched grid keys would have had to keep three copies in sync.
- **`WorldServer` (flat head) at 100 km with spread players.** Structurally single-frame. Section 7 states
  the limitation. Making the flat head multi-frame means giving it the island structure `ShardHost` already
  has, which is a reason to use `ShardHost`, not a reason to build it twice.
