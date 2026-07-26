# Floating origin: precision at 100 km for rendering, physics, and movement

Design rationale. Issue: [#337](https://github.com/APKiwiOrg/KhaozEngine/issues/337). Consumer program:
[Ruinborne#242](https://github.com/APKiwiOrg/Ruinborne/issues/242), sub-project 5.

This is the **why**. When it ships, what shipped and how to use it go to `CHANGELOG.md`,
`docs/USING-KHAOZENGINE.md`, and the `KhaozEngine.Primitives` / `KhaozEngine.Physics` /
`KhaozEngine.NetWorld` / `KhaozEngine.Render3D` package READMEs.

Nothing here is implemented. Every API signature below is a proposal.

## 0. Revision after adversarial review

The first draft chose a per-ENTITY frame anchor. An adversarial review read the sharded head and the
render pipeline against that choice and returned a redesign verdict on the model, the movement chapter
and the release split. It was right, and the reason it was right is worth keeping at the top of the
document rather than buried in a decision section.

A frame is not a free property of an entity. **A frame is a property of a SPACE, and a physics world IS
a space.** The moment an entity queries a physics world, the entity and the world must be in the same
coordinates or the query answers about somewhere else. `ShardedWorldServer` hands ONE `IPhysicsWorld` to
every cell (`ShardedWorldServer.cs:133,157-158`), so per-entity anchors and one shared physics world are
mutually exclusive: two players whose anchors differ by one grid step query colliders 128 m from where
they are standing, which is falling through terrain and walking through walls, not a rounding artifact.
No ordering repairs it, because the cells step in the same tick.

Section 3 is re-scored with a criterion the first scoring did not have (physics-space coherence), and the
model changes. Sections 6, 9 and 11 follow it. Sections 2, 5, 7 and 12 are amended. The exactness lemma
(section 8), the Bepu probe (section 5) and the DIM analysis (section 5) survived review intact and are
unchanged except where they gained detail.

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

**It proves the degradation tracks the ULP of the coordinate, not the coordinate itself.** Section 2
derives the model. The short version is that both data points sit at about 215 ULPs of divergence per 20 s
window, which is a step function of the offset rather than a straight line through it.

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

### The binade model, not a linear fit

The first draft fitted a straight line through the two measured points and read a slope of 0.01724 mm of
20 s divergence per metre of offset. That number reproduces the data and describes the wrong mechanism, so
it extrapolates wrong. Float32 error is not proportional to the coordinate. It is proportional to the ULP of
the coordinate, and the ULP is a **step function** that doubles at every power of two.

Re-reading the same two points in ULPs:

| Offset | Binade | ULP | Measured 20 s divergence | Divergence in ULPs |
|---|---|---|---|---|
| 50,000 m | `[2^15, 2^16)` | `2^-8` = 3.906 mm | 821.978 mm | 210.4 |
| 100,000 m | `[2^16, 2^17)` | `2^-7` = 7.813 mm | 1,724.296 mm | 220.7 |

Two offsets a factor of two apart give the same constant to within 5 percent, which is what a scale-free
mechanism looks like. Take the conservative value:

> **D ≈ 215 x ULP(coordinate) of divergence per 20 s window.**

This is the model everything below is sized on. It differs from the linear fit in exactly the way that
matters: inside one binade the divergence is FLAT, and at every binade boundary it doubles. A ceiling
derived from a line is therefore wrong at the boundary, and the line's answer of 580 m happens to fall
inside a binade that fails.

### The working radius, re-derived

Budget: 10 mm of divergence per 20 s window. Walking the binades:

| Local magnitude | Binade | ULP | Predicted 20 s divergence | Against a 10 mm budget |
|---|---|---|---|---|
| up to 256 m | `[2^7, 2^8)` and below | `<= 2^-16` = 0.0153 mm | `<= 3.3 mm` | pass, 3.0x margin |
| 256 to 512 m | `[2^8, 2^9)` | `2^-15` = 0.0305 mm | 6.6 mm | pass, 1.5x margin |
| 512 to 1024 m | `[2^9, 2^10)` | `2^-14` = 0.0610 mm | 13.1 mm | **fail** |

**The ceiling is 512 m**, the top of the last binade that fits, not 580 m. The airborne-momentum exact
branch independently gives 1,680 m at 60 Hz walk speed, so the divergence budget binds and the airborne
bound is slack by 3.3x at the ceiling.

**`WorldFrame.Grid = 128 m`**, a power of two so the anchor is exactly representable and the rebase
arithmetic is exact (section 8). Anchor to the NEAREST grid point, not the containing cell, so the local
coordinate lives in `[-64, 64]` per axis at anchor time. Re-anchor when a local axis exceeds 96 m.

Worst case: 96 m per axis, planar magnitude 136 m. Both fall in binades at or below `[2^7, 2^8)`, so the
predicted 20 s divergence is **3.3 mm against a 10 mm budget, a 3.0x margin** (1.6 mm and 6.1x if the
per-axis magnitude is the right operand, which it usually is). 12x inside the airborne bound. The design
target sits four binades below the ceiling, which is the room the model says is there.

### What the 10 mm budget actually bounds, stated correctly

The first draft claimed "a player sprinting at 6 m/s re-anchors roughly every 20 s, so the accumulation
window is bounded too, not only the magnitude". **That is false and it has to be said plainly, because it
is the kind of claim that makes a reader stop worrying about the right thing.**

A re-anchor is an EXACT translation (section 8). Exact means it carries the accumulated divergence forward
completely unchanged. It resets nothing. What a re-anchor bounds is the size of the per-tick rounding
quantum from the next tick onward, never the total error already banked.

So the honest statement of the guarantee:

> The 10 mm figure is a **per-window** budget on the RATE at which divergence grows, not a steady-state
> bound on divergence. Today at 100 km that rate is 1,724 mm per 20 s. Under this design it is 3.3 mm per
> 20 s regardless of where in the world the player is, a **520x reduction in the growth rate**. Total
> divergence over a long unbroken session is unbounded in both designs. It is the rate that makes the
> difference between a player who visibly desyncs from the terrain in half a minute and one who does not.

This also corrects the acceptance test. The first draft compared a framed run at 100 km against an unframed
run at the origin and called the second one ground truth. It is not: an unframed run at the origin
accumulates its own float32 error as the player walks away from zero, so the comparison measures the
DIFFERENCE of two errors and can flatter or damn the feature by luck. Section 12's acceptance test now
carries a **double-precision reference trajectory** and measures both float32 runs against it.

### Hysteresis, relabelled

The first draft called the 96 m trigger "a 32 m hysteresis band". The band is real but it is stronger than
that label suggests, and the label understates it by a factor of two.

After a re-anchor at `local = 96.1`, round-to-nearest puts the new local at `-31.9`. To re-trigger, the
player must now travel **64 m back the way it came**, or 128 m onward. So the minimum travel between two
consecutive re-anchors is 64 m under a reversal and 128 m in a straight line, not 32 m. Call it what it is:
a **64 m minimum re-anchor separation**. This matters for the rebase cost budget in section 5, where the
question is how often a rebase runs, not how wide the band looks.

### The grid is a constant

`Grid` is a **constant, not a config knob**. Two peers with different grids decode different world positions
from the same wire bytes, silently, and the value is derived from a measured budget rather than from anything
a game authors. A game that never leaves the origin never re-anchors and pays nothing.

## 3. Decision 1: the rebasing model, re-scored

### The criterion the first scoring was missing

The first scoring had six criteria and the winning candidate scored full marks on the two that mattered
most. It still chose a model that cannot run on the head it was designed for, because none of the six
criteria asked the question that decides it:

> **Does every entity query the physics world in the space the physics world's contents currently sit in?**

That is criterion 3 below and it is weighted 3. It is not a nice-to-have. A model that fails it does not
degrade gracefully, it produces a character standing on nothing.

### The criteria

Weighted 1 to 3, scored 0 to 5, maximum 85.

| # | Criterion | Weight | Why it is weighted there |
|---|---|---|---|
| 1 | Fixes the measured accumulation | 3 | The thing the measurement forced. A model that only fixes visuals fails the brief. |
| 2 | Server multi-player at 100 km | 3 | A shard server simulates MANY players spread over the whole world. |
| 3 | **Physics-space coherence** | 3 | NEW. An entity and the physics world it queries must be in one space. Failing this is not imprecision, it is a wrong answer: falling through terrain, walking through walls, contacts resolving against nothing. |
| 4 | Determinism across peers | 3 | A rebase changes coordinates and therefore changes float results. Any model that lets two peers simulate one entity in different frames has replaced one bug with a worse one. |
| 5 | Wire and netcode blast radius | 2 | Cost, not correctness. Recoverable. |
| 6 | Implementation risk in the engine | 2 | Cost, not correctness. Recoverable. |
| 7 | Consumer adoption cost | 1 | Ruinborne adopts incrementally either way. |

### The candidates

Every candidate keeps the camera-relative render layer, which is orthogonal to all of them and ships alone
as release 1. What differs is the SIM frame.

**A. Camera-relative rendering only.** Subtract the render origin before the matrix build. Nothing touches
simulation.

**B. Periodic global origin shift.** Translate the whole world when the anchor strays past a threshold.
Classic floating origin. One origin per process.

**D. Entity-anchored, one shared physics world.** The first draft's choice. Each simulated entity carries
its own quantized anchor on its replicated state, authored by the server and adopted by the client. One
`IPhysicsWorld` for the whole head, as today.

**E. Entity-anchored, one physics world per cell.** As D, but the shared world is split per `CellSim` so
each cell's physics can sit in one frame.

**F. Island-anchored.** The frame belongs to the **simulation island**, where an island is exactly one
`(World, IPhysicsWorld)` pair. Every entity stepped in an island carries a STAMP of the island's frame on
its replicated state, which is what rides the wire and what the client adopts. On the flat `WorldServer` and
on the client there is one island, so there is one frame. On the sharded head there is one island per
`CellSim`, each with its own physics world.

### The scores

| Criterion | Weight | A | B | D | E | F |
|---|---|---|---|---|---|---|
| 1 Fixes the accumulation | 3 | 0 | 2 | 5 | 5 | 5 |
| 2 Server multi-player at 100 km | 3 | 0 | 0 | 0 | 5 | 5 |
| 3 Physics-space coherence | 3 | 5 | 5 | 0 | 1 | 5 |
| 4 Determinism across peers | 3 | 5 | 1 | 5 | 2 | 5 |
| 5 Wire and netcode blast radius | 2 | 5 | 2 | 3 | 3 | 3 |
| 6 Implementation risk | 2 | 5 | 2 | 3 | 1 | 2 |
| 7 Consumer adoption cost | 1 | 5 | 3 | 4 | 2 | 2 |
| **Weighted total** | | **55** | **35** | **46** | **49** | **72** |

### What changed from the first scoring, and why the first one missed it

Three numbers moved, and every one of them moved because of something read out of the code rather than
reasoned about from the shape of the design.

**D's criterion 2 went from 5 to 0.** The first scoring credited D with solving server multi-player because
each entity carries its own anchor, so 200 players spread over 100 km are each locally precise. That is true
of the POSITIONS and false of the simulation, because the simulation queries a physics world that can only be
in one space at a time. `ShardedWorldServer.cs:133` takes a single `IPhysicsWorld` and hands it to both
`PlayerMovementSystem` and `spawnClamp` at `:157-158`, and `CellSim` (`CellSim.cs:56-71`) owns an ECS `World`
and no physics at all. Under D, the first two players whose anchors differ are querying the same collider set
from two spaces 128 m apart. D does not scale to spread players. It does not work for two.

**D's criterion 3 is 0, and criterion 3 did not exist before.** This is the whole correction. Adding the
criterion is what makes the scoring able to see the failure at all.

**F's criterion 6 is 2, worse than D's 3.** Per-cell physics worlds are genuinely more work than one shared
world, and honest scoring has to say so rather than let the winner be cheap as well as correct. The cost is
detailed in section 5.

**A rose from 40 to 55** purely from the new criterion, where it scores 5 because it touches no simulation
and therefore cannot desynchronize one. That is not an argument for shipping A alone, it is a confirmation
that A is safe to ship FIRST, which is exactly what release 1 does.

**E scores 49 and loses to F on the criterion that separates them.** E puts the anchor on the entity and the
physics world on the cell, which looks like it should work because entities in one 60 m cell are close
together. It does not, and the reason is hysteresis. Two entities in the same cell that crossed the 96 m
trigger at different times sit in ADJACENT frames, legitimately, by design. The cell's one physics world can
only be in one of them. E fails the same coherence test as D, just less often and therefore more insidiously.

### Decision: F, island-anchored

The frame is a property of the space, and the space is `(World, IPhysicsWorld)`. Everything else follows.

**An island is one `(World, IPhysicsWorld)` pair, and it owns exactly one `WorldFrame`.**

- The client has one island: the local player, its predicted state, and the consumer's physics world.
- The flat `WorldServer` has one island: its single `World` and its single physics world.
- The sharded head has one island per `CellSim`: that cell's `World` and that cell's own physics world.

**The entity carries a stamp, not an authority.** `ReplicatedPosition.Frame` is a copy of the island's frame
at the tick the position was written. It is what rides the wire and what the client adopts. It is never
independently chosen per entity and never derived on the receiving side, so everything decision 1 originally
got right about authored-not-derived survives intact. What changes is that the authority is the island, not
the entity, so two entities in one island can never disagree with each other or with the physics world they
both query.

**On the sharded head the server never re-anchors.** A cell's frame is `WorldFrame.Nearest(cell centre)`,
fixed at cell creation and immutable for the cell's life. A cell is 60 m across by default, so
`|local| <= cellSize/2 + Grid/2 = 94 m` per axis for anything the cell owns, and `<= 118 m` including the
24 m ghost overlap. That is inside the design target with the full 3.0x margin, and it means the sharded
server performs **no runtime rebase at all**: no physics translate mid-run, no sleeping-body wake risk, no
rebase cost budget, no re-anchor ordering question. The frame changes for an ENTITY only at a cell handoff,
which `ShardHost.ProcessHandoffs` already handles as a discrete, exactly-once, ordered event.

This is the single biggest simplification the re-scoring bought, and the first draft missed it entirely
because a per-entity anchor has no cell to sit on.

**The single-island heads DO re-anchor**, on the section 2 hysteresis policy, because a single island has to
follow its player. That rebase is an island-level atomic operation (section 5), not a per-entity one.

**Why this is not candidate B.** B has one origin per PROCESS. F has one per island, and the sharded head has
one island per cell. B fails criterion 2 structurally. F satisfies it structurally, by giving the shard server
as many frames as it has cells.

**Why this is not candidate C from the first draft.** The first draft rejected anchoring to a `CellCoord`
because the client does not have the server's spatial partition and would have to derive the same cell
independently, one tick out of step, for a 60 m discontinuity. That objection was correct and it is fully
answered here: the client does not derive anything. It reads the stamp off the wire. The cell is where the
frame COMES FROM on the server, and it is not how the client finds it.

## 4. Decision 2: who owns the origin state

`WorldFrame` lives in `KhaozEngine.Primitives`, the dependency-free bottom of the render and runtime stack.
This costs **zero new project references**: `Render3D`, `Locomotion` and `Simulation.Tests` reference
`Primitives` directly, `NetWorld` reaches it through `Locomotion`, and `Terrain.Render3D` through `Render3D`.

`KhaozEngine.Physics` has NO project references at all (its csproj declares none, only `System.Numerics`),
and that is a property worth keeping, so the physics seam does not learn about `WorldFrame`. It learns about
a `Vector3` origin instead, which is all it needs (section 5). `KhaozEngine.Netcode` likewise stays on
`Netcode.Abstractions` and takes only a `Vector2` anchor to difference.

```csharp
namespace KhaozEngine.Primitives;

/// <summary>A quantized planar simulation/render frame: the anchor is <c>(X, 0, Z) * Grid</c> metres, always
/// exactly representable in float32. Y is NEVER framed (see the design doc, section 6). <c>default</c> is the
/// world origin, so a game that never leaves the origin is byte-identical to the pre-frame engine.
/// <para>A frame belongs to a SIMULATION ISLAND (one World plus one IPhysicsWorld), never to an individual
/// entity: an entity's stored frame is a stamp of its island's frame, not an independent choice.</para></summary>
public readonly record struct WorldFrame(short X, short Z)
{
    /// <summary>Frame spacing in metres. A CONSTANT, not a knob: two peers on different grids silently decode
    /// different world positions from the same bytes. 128 is derived from the measured divergence budget.</summary>
    public const float Grid = 128f;

    /// <summary>The local-axis magnitude that triggers a re-anchor on a single-island head. Guarantees a
    /// minimum of 64 m of travel between consecutive re-anchors (design doc section 2).</summary>
    public const float ReanchorRadius = 96f;

    /// <summary>The largest local magnitude the 10 mm per-window divergence budget tolerates: the top of the
    /// last float32 binade that fits (design doc section 2). Used to VALIDATE island sizing (a shard cell's
    /// half-diagonal plus its overlap margin plus half the grid must fit under this), never as a runtime bound.</summary>
    public const float MaxLocalRadius = 512f;

    public static WorldFrame Origin => default;

    /// <summary>The frame's world-space anchor point. Exact in float32 for every representable X/Z.</summary>
    public Vector3 Anchor => new(X * Grid, 0f, Z * Grid);

    /// <summary>The frame whose anchor is NEAREST <paramref name="world"/> (round, not floor), so a freshly
    /// anchored local coordinate lies in [-64, 64] per axis.</summary>
    public static WorldFrame Nearest(Vector3 world);
    public static WorldFrame Nearest(float worldX, float worldZ);

    /// <summary>World -> frame-local. X and Z are shifted, Y passes through unchanged.</summary>
    public Vector3 ToLocal(Vector3 world);
    public Vector2 ToLocalXz(float worldX, float worldZ);

    /// <summary>Frame-local -> world.</summary>
    public Vector3 ToWorld(Vector3 local);
    public Vector2 ToWorldXz(float localX, float localZ);

    /// <summary>The EXACT translation that carries a local coordinate in this frame into
    /// <paramref name="target"/>. Both anchors are integer multiples of <see cref="Grid"/>, so the delta is an
    /// integer multiple of 128 and the addition is exact under the section 8 magnitude precondition.</summary>
    public Vector3 DeltaTo(WorldFrame target);

    /// <summary>True when <paramref name="local"/> has drifted past <see cref="ReanchorRadius"/> on either
    /// planar axis. Y is ignored. The re-anchor POLICY for a single-island head, not a per-entity test.</summary>
    public static bool ShouldReanchor(Vector3 local);
}
```

**How a shift propagates: as data on the state, never as an event.** There is no `OriginShifted` event and no
per-frame origin parameter threaded through call chains. The stamp is a field on the replicated state, so it
arrives with the position it applies to and can never be reordered against it, dropped, or applied to the
wrong tick. An event would have all three failure modes. This is the same reasoning that put `TeleportEpoch`
on `MovementState` rather than in a message kind.

**Consumer-facing API, client head:** nothing, in the common case. `Scene3D` picks its own render origin,
`WorldClient` adopts the server's stamp, and `ReplicatedPosition.Value` still READS absolute world metres.
A consumer that owns colliders it registered itself (outside the engine's streaming sink) hooks
`WorldClient.FrameChanged` to fix its own bookkeeping. The engine's own physics world is rebased by the
island, not by the consumer.

**Consumer-facing API, server head:** set `ShardedWorldServerConfig.FrameAnchoring` / `WorldServerConfig.
FrameAnchoring` (section 11 states which release defaults it on), and supply frame-local samplers if it wants
the full fix rather than the accumulation half (section 6).

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

### What the probe did NOT test, and why it is the case that matters

The probe settled a stack of dynamic boxes and translated everything. The real terrain case is different in a
way the probe never exercised: a **sleeping dynamic body resting on a STATIC**, with both translated in the
same rebase. That is a crate asleep on a terrain collision mesh, which is the overwhelmingly common shape in
a streamed world, and it is exactly where a broadphase refit ordering bug would show up as the crate waking,
sinking or being ejected.

Two further gaps, stated as gaps rather than smuggled in as results:

- **The 136 m small-frame shift was asserted, not measured.** The claim "a shift into a 136 m frame has no
  such term" is a sound inference from the 100 km destination magnitude being the source of the 0.365 mm
  drift, but it was never run. Run it.
- **No cost budget was computed.** A rebase is O(statics + bodies) pose writes plus broadphase refits on the
  main thread between two steps.

All three become plan items in section 12 (tests 7a, 8a and the budget below).

### The rebase cost budget

Only single-island heads rebase, and only when their player crosses the 96 m trigger. Section 2 establishes a
**64 m minimum travel between consecutive re-anchors**, so at a 6 m/s sprint the floor is one rebase per 10.7
seconds and the straight-line case is one per 21.3 seconds.

The work per rebase is bounded by the resident collider population, which the streaming ring bounds directly.
For Ruinborne's shape (a 60 m chunk grid, a gameplay ring of radius 2 plus a decor ring, terrain collision on
the gameplay ring only) that is order 25 terrain statics, a few thousand prop statics and a few hundred
dynamics. At Bepu's measured refit cost that is single-digit milliseconds, once every ten seconds or more,
on a head that is already doing a full physics step every tick.

**The budget, stated as an acceptance condition rather than an estimate:** a rebase must cost less than one
tick's physics step on the same world. Section 12 test 8b measures it and fails if it does not. If a consumer
exceeds it, the two levers are bounding the ring (the streaming radius is already a config knob) or
amortizing the refit across ticks, and the second one is a real design change that is explicitly NOT in this
program (filed).

### Constraints are already translation-invariant

`ConstraintFactory` converts world poses into body-LOCAL offsets at build time
(`KhaozEngine.Physics.Bepu/ConstraintFactory.cs:191`, `Vector3.Transform(r.PoseB.Position - r.PoseA.Position,
invA)`), so a uniform translate of both ends preserves every joint exactly. World-space anchor ends are
shapeless kinematic BODIES created in `BepuPhysicsWorld.ResolveEnd`, which means a full sweep over
`Bodies.Sets` already covers them. Nothing constraint-specific is needed.

### Decision: the physics world carries its own origin, and rebasing targets an origin rather than a delta

This is a change from the first draft's `Translate(Vector3 delta)` and it is forced by the seam problem below.

```csharp
namespace KhaozEngine.Physics;

public interface IPhysicsWorld : IDisposable
{
    /// <summary>The world-space point this world's coordinates are expressed against. Every pose passed to
    /// <see cref="AddStatic"/>/<see cref="AddDynamic"/> and every query coordinate is relative to it, and
    /// every pose read back out of <see cref="GetDynamicPose"/> is too. <c>Vector3.Zero</c> (the default) means
    /// the world speaks absolute world coordinates, which is what every backend does today.
    /// <para>This is deliberately a plain <c>Vector3</c> and not a quantized frame type: the physics seam has no
    /// project references and keeps none. The caller quantizes.</para></summary>
    Vector3 Origin => Vector3.Zero;

    /// <summary>Whether this backend implements <see cref="Rebase"/>. A backend that returns false (the
    /// default, including any consumer test double) cannot serve a world large enough to need rebasing.</summary>
    bool CanRebase => false;

    /// <summary>Re-express this world against <paramref name="newOrigin"/>: translate EVERY static, every
    /// dynamic body (awake and sleeping alike), and every world-space constraint anchor by
    /// <c>Origin - newOrigin</c>, then set <see cref="Origin"/> to it. Velocities, sleep state, contacts and
    /// constraints are all preserved: this is a change of coordinate space, not a physical event, and nothing
    /// in the world can observe it.
    /// <para>It takes the TARGET origin rather than a delta on purpose. The contents and
    /// <see cref="Origin"/> then move as one atomic operation and can never be left describing different
    /// spaces, which a delta-taking API makes possible with one dropped call.</para>
    /// <para>Must be called BETWEEN steps, never during one.</para></summary>
    void Rebase(Vector3 newOrigin) => throw new NotSupportedException(
        "This IPhysicsWorld backend does not support Rebase. Check CanRebase first.");
}
```

All three members are default interface implementations, so this is an **additive minor**, not a breaking
change. The DIM analysis holds: `IPhysicsWorld` has one production implementer plus four sealed test doubles,
no structs, no explicit interface implementations, and no reflection or type tests anywhere against it. Every
existing consumer implementation keeps compiling and correctly reports that it cannot rebase. The engine
already uses this exact pattern to evolve a public interface: `IPredictedState<TSelf>` grew `Vertical`,
`TeleportEpoch` and `StepDeltaY` as DIMs.

`BepuPhysicsWorld` overrides all three. `CanRebase => true`.

### The seam this API exists to close: everything that speaks absolute to a rebased world

The first draft specified `Translate(delta)` and said nothing about the callers. That is the largest gap the
review found, because a translated world with absolute-speaking callers is worse than no rebase at all: it
fails silently and intermittently rather than loudly. Five real sites, all verified:

| Site | What it does today | Under a rebased world |
|---|---|---|
| `Terrain.Render3D/ChunkStatics.cs:37-41` | `physics.AddStatic(shape, new Pose(new Vector3(p.X, p.Y, p.Z), ...))` from absolute placements | Streaming continues after a rebase, so every newly streamed prop lands one anchor delta away |
| `Terrain.Render3D/ChunkDynamics.cs:43` | `physics.AddDynamic(s.Shape, s.Pose, ...)` from absolute spawns | Same |
| `Terrain.Render3D/ChunkTerrainCollision.cs:29` | `physics.AddStatic(mesh, Pose.Identity)` with absolute vertices baked in | Pose moves, vertices do not (see the bake fix below) |
| `Render3D/Camera/FollowCamera3D.cs:187` | `world.SweepCapsule(..., Pose.At(target), ...)` with an absolute target, from the RENDER layer | Camera occlusion silently stops finding anything after the first rebase |
| `NetWorld/WorldBounds.cs:19` | `Clamp(x, z)` returns the nearest point inside an ABSOLUTE play area, folded into the step at `PlayerMovementSystem.cs:41` | Fed frame-local coordinates, it yanks the player to the play-area boundary every tick |

**The fix is that `Origin` is readable, so every one of these converts locally with no plumbing.** An add
becomes `physics.AddStatic(shape, new Pose(absolute - physics.Origin, rot))`. The camera sweep becomes
`Pose.At(target - world.Origin)` with the hit distance unchanged (a distance is frame-invariant). No sink
learns a frame from a constructor parameter, no origin is threaded through a call chain, and a site that
forgets is a site that never read `Origin`, which is greppable.

`WorldBounds` is the one that cannot be fixed at the call site, and it is worth being precise about why. The
type is public and abstract, so a consumer CAN supply its own, but `Clamp(float x, float z)` carries no frame,
so a consumer-authored subclass has no more information than the engine's does. The bounds are authored
content and must stay absolute. So the STEP converts: `PlayerMovementSystem` and `PlayerMoveSimulator` wrap
the supplied bounds in a frame-adapting delegate,
`(x, z) => frame.ToLocalXz(bounds.Clamp(frame.ToWorldXz(x, z)))`, which is two exact adds and two exact
subtracts per call under the section 8 lemma. Same treatment for `groundHeight`, `groundNormal` and `medium`
under `SamplerSpace.World` (section 6).

### What per-cell physics on the sharded head actually costs

Section 3 chose F, and on the sharded head F means one `IPhysicsWorld` per `CellSim` rather than the single
shared world `ShardedWorldServer.cs:133` takes today. This is the real bill and it is why F scores 2 rather
than 4 on implementation risk.

- **The constructor seam changes.** `ShardedWorldServer` stops taking an `IPhysicsWorld` and starts taking a
  `Func<CellCoord, IPhysicsWorld>?` factory, invoked once per cell from the existing `ShardHost.CellCreated`
  hook. `CellSim` gains a `Physics` property beside its `World`, and a `Frame`.
- **`PlayerMovementSystem` is constructed per cell**, holding that cell's physics world and frame, instead of
  one stateless instance shared by all. Section 6 details why this is also what fixes the data race.
- **A query at a cell boundary sees only its own cell's colliders.** The overlap margin already mirrors
  entities across the border for replication (`OverlapMargin >= InterestRadius`, enforced in the ctor), and
  the same margin is what a per-cell physics world must use for STATICS: a cell registers the border statics
  of its neighbours as read-only duplicates within `OverlapMargin`. The streaming sink already knows a chunk's
  extent, so this is a routing question, not a new geometric one.
- **A dynamic body crossing a cell boundary hands off between physics worlds**, which is a remove and re-add,
  which loses its contact cache. This is a genuine residual cost with no clean fix inside this program. It is
  bounded (only dynamics, only at a boundary, only once per crossing) and it is the price of the model. Filed
  as a follow-up for a contact-preserving handoff if it ever bites.

Deferring per-cell physics is not an option the way the first draft framed it ("deferred, not precluded").
Without it the sharded head has no working sim frame at all, so it is a **release 3 prerequisite**.

### The gotcha the survey missed: baked world-space collision vertices defeat the rebase

`TerrainChunkCollision.Build` copies ABSOLUTE world-space vertex positions into the `TriangleMeshShape` and
`ChunkTerrainCollision.Add` registers it at `Pose.Identity`, because a Bepu mesh is not recentred
(`KhaozEngine.Terrain.Render3D/ChunkTerrainCollision.cs:29`). A rebase moves the POSE, so the chunk does
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

**This fix needs no sim frame and no physics rebase.** It works on both heads, at any distance, with
`FrameAnchoring` off, which is why section 11 makes it release 2's headline rather than an appendix to it.
Bepu transforms a query into a mesh's local space using the static's pose, so the triangle test runs at
`<= 60 m` magnitude while the caller keeps passing whatever coordinates the world speaks.

### The public API break the terrain bake causes, enumerated

The first draft called this a prerequisite and did not say what it breaks. It breaks five things, four of
them public:

1. **`TerrainScene3D.DrawTerrainChunk(this Scene3D, MeshHandle)`** (`TerrainScene3D.cs:18-19`) hard-codes
   `Matrix4x4.Identity` and its own doc asserts "Chunk vertices are already world-space, so the draw
   transform is identity". Both the code and the doc are wrong after the bake. It gains a placement overload,
   `DrawTerrainChunk(this Scene3D, MeshHandle, TerrainChunkRegion)` (or a `Vector3 origin`), and the
   parameterless one is marked obsolete rather than removed, since it stays correct for a chunk whose region
   origin is zero.
2. **`Scene3DChunkSink.ChunkLoad`** is public and carries no region. It gains one. *(Partial refutation of the
   review here, recorded because it changes the size of the fix: the sink's own `Draw` at
   `Scene3DChunkSink.cs:468-473` iterates `KeyValuePair<ChunkCoord, ChunkLoad>`, so the coord IS in hand at
   the draw site and `ChunkGrid.RegionOf(coord, _chunkSize)` reconstructs the region exactly as
   `Scene3DChunkSink.cs:285` already does. The internal draw path can be fixed with no field at all. The field
   is still worth adding, because `ChunkLoad` is public and a consumer holding one has no coord.)*
3. **`Showcase/RoomNet.cs:178-184,322`** builds chunks directly and draws them through
   `scene.DrawTerrainChunk(chunk)`, bypassing the sink entirely. After the bake it stacks a 5x5 grid of
   chunks all at the origin. It moves to the placement overload.
4. **`TerrainChunkBounds.FromPositions`** silently becomes chunk-local. Nothing reads it as world-space today,
   which is exactly why it is the dangerous one: it is a latent break that compiles.
5. **`Render.Tests/Terrain/TerrainChunkBuilderTests.cs:29-38`** asserts
   `field.SampleHeight(v.X, v.Z) == v.Y` over the built vertices, which is only true while the vertices are
   absolute. It becomes `field.SampleHeight(v.X + region.OriginX, v.Z + region.OriginZ)`.

### Contact and sleeping behaviour across a shift, stated

- A sleeping body stays asleep, at its translated pose, and does not move on the next step. Verified for a
  free sleeping body. NOT yet verified for a sleeping body resting on a translated static (test 7a).
- An awake body in contact keeps its contacts and its solver state. The stack probe drifted 0.365 mm at
  worst, and that drift is a float32 artifact of the DESTINATION magnitude (100 km), not of the shift itself.
  A shift into a 136 m frame should have no such term, and test 8a measures it rather than asserting it.
- Nothing is woken. That is the whole point of not using `ApplyDescription`.
- A shift called mid-step is undefined. The ISLAND sequences it between steps (section 6), which is why the
  first draft's answer of "the `PlayerMoveSimulator` owns that ordering" was wrong for the sharded head:
  `PlayerMoveSimulator` is not in that head's tick path at all.

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

### The choke point is `PlayerMovementSystem`, and the first draft named the wrong type

The first draft said `PlayerMoveSimulator` is "the shared `ITickSimulator` used identically by
`WorldServer.Tick`, `PlayerMovementSystem`, and `ClientPrediction.Predict`/`Reconcile`. One type, one place,
both heads." That is false for the head the whole design exists to serve.

`PlayerMovementSystem.Update` calls `CharacterMovement.Step` **directly**
(`PlayerMovementSystem.cs:84`). It does not go through `PlayerMoveSimulator`. On the sharded head
`PlayerMoveSimulator` appears only as `spawnClamp` (`ShardedWorldServer.cs:158,670`), a one-shot ground clamp
at spawn. `PlayerMoveSimulator` is the stepper for `WorldServer` (`WorldServer.cs:408`) and `WorldClient`
(`WorldClient.cs:210`) and nowhere else. A `PlayerMoveSimulator.Frame` property would never have reached the
sharded server at all.

There are therefore **two steppers, one per head shape**, and both need the island's frame:

- Single-island heads (`WorldServer`, `WorldClient`) step through `PlayerMoveSimulator`.
- The sharded head steps through `PlayerMovementSystem`, once per cell.

### Who stamps the frame, and how parallel cell ticks read it

`PlayerMovementSystem` is documented **stateless** precisely so one instance can fan across the scheduler
(`PlayerMovementSystem.cs:21-22`), and `ShardHost.Tick` does fan cells across `IJobScheduler`
(`ShardHost.cs:208`). A `Frame` property set before the step and read during it is a write-then-read on
shared mutable state across parallel cell ticks. It is a data race, and it is a race whose symptom is a
player in the wrong cell's coordinates for one tick, which is a 128 m teleport.

Two changes remove the race by construction rather than by locking.

**1. One `PlayerMovementSystem` per cell.** It is added to each `CellSim`'s `World` already, so constructing
one per cell instead of sharing one costs a few dozen bytes per cell and nothing else. Each instance holds
that cell's `IPhysicsWorld`, that cell's `WorldFrame`, and the frame-adapted `clampXz`. All three are
readonly for the instance's life. The class stays free of per-TICK mutable state, so the fan-out and its
scheduler-independence claim are unchanged. The doc comment changes from "one instance is shared across all
cells" to "one instance per cell, holding that cell's physics world and frame".

**2. The frame is read off the entity's own component inside the loop, not off the system.**

```csharp
world.ForEach<NetId, ReplicatedPosition, PendingMove, MovementState>((Entity e, ref NetId _,
    ref ReplicatedPosition pos, ref PendingMove move, ref MovementState ms) =>
{
    // ... existing Ghost / Migrating skip ...

    // Self-healing invariant: everything this cell owns must be stamped with this cell's frame. A missed
    // conversion at a cell-entry point (spawn, handoff, restore, teleport) is corrected here EXACTLY rather
    // than becoming a 128 m step. One comparison per entity per tick.
    if (pos.Frame != cellFrame) pos = pos.ToFrame(cellFrame);

    var state = new MoveState { Position = pos.Local, /* ... */ };
    state = CharacterMovement.Step(state, move.Command, dt, groundHeight, tuning, groundNormal,
                                   physics, clampXz, medium);
    pos = pos.WithLocal(state.Position);   // frame preserved by construction, never re-derived
    // ... existing MovementState write-back, unchanged ...
});
```

Nothing shared is written. Nothing is derived. The stamp on the component is the authority the entity carries
and the cell frame is the authority the island carries, and the one line above makes it impossible for them
to disagree for longer than the tick that noticed.

### Who converts an entity into a cell's frame, and when

The cell frame is `WorldFrame.Nearest(cell centre)`, fixed at `CellSim` construction. Entities enter a cell
through exactly four doors, and every one of them already exists and is already exactly-once:

| Door | Code | Conversion |
|---|---|---|
| Spawn | `ShardHost.SpawnOwned` via `ShardedWorldServer.cs:673-674` | The spawn position is authored ABSOLUTE, so `ReplicatedPosition.FromWorld(absolute, cell.Frame)` |
| Handoff | `ShardHost.ProcessHandoffs` (`ShardHost.cs:305-320`) | `pos = pos.ToFrame(destinationCell.Frame)`, exact |
| Persistence restore | `CellSim` restore via `ICellPersistenceHost` | Persisted positions are absolute (section 10), so `FromWorld` |
| Admin teleport / self-rescue | `ShardedWorldServer.SetPlayerState` (`:330`) | `FromWorld`, and it already advances `TeleportEpoch` |

`ProcessHandoffs` derives the destination cell from `CoordFor(x, y)` on the ABSOLUTE position
(`ShardHost.cs:305-307`), which keeps working unchanged because `ReplicatedPosition.Value` still reads
absolute. So does `ShardedWorldServer.PositionAccessor` (`:728-733`), which feeds the interest grid.

### Who re-anchors, and who calls it

**The sharded head never re-anchors.** A cell's frame is immutable, so `Reanchor()` has no caller there. The
first draft's unexplained "`Reanchor()` has no caller" is answered by deleting the method: a per-entity
re-anchor is the wrong primitive under the island model, because an entity cannot change frame without the
physics world it queries changing with it.

**Single-island heads re-anchor at the island level**, once per tick, between steps, after every entity in
the island has stepped. Re-anchoring after the step rather than before means the anchor is a function of a
settled position and the whole tick ran in one frame.

```csharp
// WorldServer.Tick and WorldClient, after the step pass, before the next one.
if (WorldFrame.ShouldReanchor(localPlayerLocal))
{
    WorldFrame target = WorldFrame.Nearest(pos.Value);      // absolute, exact
    physics?.Rebase(target.Anchor);                         // one call, atomic, contents + Origin together
    foreach (entity in island) pos = pos.ToFrame(target);   // exact
    islandFrame = target;
    FrameChanged?.Invoke(previous, target, previous.DeltaTo(target));
}
```

The ordering is fixed and it is the whole safety argument: the physics world and every entity in the island
move in the same gap between two steps, so no step ever observes a half-rebased island.

### `SamplerSpace`: what it governs and what it does not

```csharp
namespace KhaozEngine.NetWorld;

/// <summary>The coordinate space the caller's SAMPLER DELEGATES read. It says nothing about the physics
/// world: an island's IPhysicsWorld is always in the island's frame by definition (its Origin is the frame
/// anchor), because a physics world IS a coordinate space and cannot be in two.</summary>
public enum SamplerSpace
{
    /// <summary>Samplers take ABSOLUTE world coordinates. The stepper wraps each one in a frame-adapting
    /// delegate that adds the anchor back before the call and subtracts it from any returned coordinate.
    /// Correct, and it fixes the ACCUMULATION half of the problem (the carried state is frame-local), but each
    /// sample coordinate is still evaluated at world magnitude, so the sampling quantum at 100 km is still
    /// 7.8 mm. The zero-work adoption step. This is the mode WorldBounds always runs in (section 5).</summary>
    World = 0,

    /// <summary>Samplers take FRAME-LOCAL coordinates and the stepper passes them straight through. The full
    /// fix. A consumer whose ground follow comes from a rebased IPhysicsWorld with chunk-local collision
    /// meshes (section 5) gets this for free.</summary>
    Frame = 1,
}
```

`SamplerSpace.World` defaulting to on is deliberate. It is the state every existing consumer is already in,
it is a genuine improvement over today (the accumulating carried state becomes frame-local, which is the
term the measurement showed growing), and it lets a consumer adopt the release without touching its terrain
sampling. `SamplerSpace.Frame` is the finish line, and Ruinborne reaches it when its collision meshes go
chunk-local.

The first draft left a hole here that the review caught: `CharacterMovement.Step`'s physics queries take
`state.Position` regardless of `SamplerSpace` (`CharacterMovement.cs:283,394`, `Pose.At(pos)`), and nothing
said the physics world had to be in the frame. Under the island model that hole closes by definition rather
than by rule, because the island's physics world has `Origin == islandFrame.Anchor` at all times. There is no
mode in which the step queries a world in a different space from the state it is stepping.

### Consistency across heads: authoritative, not derived

Both heads must step the same entity in the same frame, or a pure translation produces different float
results and reconciliation compares states from two spaces.

The tempting answer is that each head derives the frame from the position it holds. That fails at exactly the
moment it matters: the client's prediction may sit one tick past a re-anchor boundary that the server has not
crossed yet, so for one tick the two heads derive different anchors and every downstream comparison is off by
128 m.

The answer is that **the server authors the stamp and the client adopts it**. The stamp is authoritative
state, exactly like position, and it rides the same wire field (section 7). A client that computes its own
anchor is a bug, and section 12 test 15 asserts it.

This is unchanged from the first draft and it is the part of decision 1 that survived review intact.

## 7. Decision 5: wire and prediction

### `ReplicatedPosition` goes frame-relative, and `Value` stops being settable

The issue's survey is right that `MoveProtocol.cs:93-97` is the one registration site for position, and it is
the whole reason this is tractable.

```csharp
namespace KhaozEngine.NetWorld;

public struct ReplicatedPosition : IComponent
{
    /// <summary>The frame <see cref="Local"/> is expressed against: a STAMP of the owning simulation island's
    /// frame, written by the server, adopted by the client, never derived on the receiving side.</summary>
    public WorldFrame Frame;

    /// <summary>Position relative to <see cref="Frame"/>. X and Z are frame-local, Y is absolute world height
    /// (Y is never framed).</summary>
    public Vector3 Local;

    /// <summary>The absolute world position, READ-ONLY. Every existing reader (interest grids, cell keying,
    /// persistence, handoff) keeps compiling and keeps getting exactly what it got before: <c>Frame.Anchor</c>
    /// is exact, so <c>Anchor + Local</c> is as precise as <c>Local</c>.
    /// <para>There is deliberately NO setter. See the design doc section 11 for why this compile break is the
    /// point of the major.</para></summary>
    public readonly Vector3 Value { get; }

    /// <summary>An ABSOLUTE world position converted into <paramref name="frame"/>. For a position arriving
    /// from outside the simulation: an authored spawn, a persisted record, an admin teleport.</summary>
    public static ReplicatedPosition FromWorld(Vector3 world, WorldFrame frame);

    /// <summary>A position ALREADY expressed in <paramref name="frame"/>. For a position coming out of the
    /// simulation or out of a physics world whose <c>Origin</c> is that frame's anchor.</summary>
    public static ReplicatedPosition InFrame(WorldFrame frame, Vector3 local);

    /// <summary>This position with a new local, same frame. The step's write-back.</summary>
    public readonly ReplicatedPosition WithLocal(Vector3 local);

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
lerp: (a, b, t) => ReplicatedPosition.InFrame(b.Frame,
    Vector3.Lerp(a.ToFrame(b.Frame).Local, b.Local, t)),   // exact when frames differ, a no-op when they match
```

Lerping the two locals directly without the rebase would interpolate between two different spaces and place
a remote a frame-width away. Decoding both to world and lerping there would be correct but would throw away
the precision the encoding just bought. This branch is both correct and precision-preserving.

### An origin shift does NOT get its own protocol message

It rides `ReplicatedPosition`. A separate shift message could arrive out of order relative to the position it
applies to, be dropped, or be applied on the wrong tick, and all three place an entity a frame-width from
where it is. Carrying the frame in the same component makes those failures unrepresentable.

### `DynamicBodyReplication` samples in the physics world's space, and today it would lie about it

`DynamicBodyReplication.Sample` (`DynamicBodyReplication.cs:103-105`) reads `physics.GetDynamicPose(handle)`
and writes it straight into `new ReplicatedPosition { Value = pose.Position }`, a field documented absolute.
Once its physics world is an island's world, that pose is FRAME-LOCAL and the write stamps it as absolute, so
every replicated crate teleports by the anchor delta the first time the island rebases.

The fix is one line and it is the reason `Origin` is readable on the seam:

```csharp
world.Set(t.Entity, ReplicatedPosition.InFrame(islandFrame, pose.Position));
```

`InFrame`, never `FromWorld`. `DynamicBodyReplication` already holds the `IPhysicsWorld` and the ECS `World`,
so it takes the island frame from the same owner that gave it those. Section 12 test 20 covers it.

### Prediction: a shift mid-replay must not manufacture a correction, and today it would

Two distinct failures exist in `ClientPrediction.Reconcile` as written, and both must be fixed in the same
change. Both confirmed at the cited lines.

**The hard-snap gate fires.** `Reconcile` computes `Vector2 planarError = oldPlanar - predictedState.Position`
(`ClientPrediction.cs:242`) where `oldPlanar` (`:221`) is the pre-rebase prediction and
`predictedState.Position` is the post-replay state. Across a re-anchor those are in different frames, so the
difference is the 128 m anchor delta. `PredictionSettings.HardSnapDistance` defaults to 100 m
(`PredictionSettings.cs:26`), so the gate at `:256` trips and the avatar hard-cuts on a shift that is a no-op
in world space.

**The render offset absorbs the whole anchor delta.** Even past the gate, the C1 branch re-anchors
`renderOffset` at `:293` against `renderedPlanar`, which was captured at `:223` in the OLD frame, so the
smoothing offset picks up 128 m and then decays it, gliding the avatar a frame-width across the screen over
the smoothing window. This is worse than the hard snap because it looks like a physics bug rather than a
teleport.

The fix is one place: at the TOP of `Reconcile`, **above line 221**, convert the captured presentation state
into the incoming basis's frame before any existing math runs. Placing it above `:221` rather than above
`:242` is what also fixes `renderedPlanar` at `:223` and `previousPredictedPosition`, so one insertion point
covers both bugs.

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

Then, at the very top of `Reconcile`:

```csharp
Vector2 frameDelta = predictedState.FrameAnchor - authoritativeBasis.FrameAnchor;
predictedState = predictedState.WithFrameAnchor(authoritativeBasis.FrameAnchor,
                                                predictedState.Position + frameDelta);
previousPredictedPosition += frameDelta;
// renderOffset is a DELTA and is frame-invariant, so it is untouched.
// Y is never framed, so oldVertical / verticalRenderOffset are untouched.
```

After that, `oldPlanar` at `:221` and `renderedPlanar` at `:223` are captured in the incoming basis's frame,
`planarError` at `:242` measures only real prediction divergence, `planarRebase` at `:289` excludes the anchor
delta, and `renderOffset` at `:293` re-anchors against a same-frame target. The shift becomes invisible, which
is exactly what it should be.

### Where `FrameAnchor` comes from, and the replay ordering

The first draft put a `Frame` on the simulator and a `FrameAnchor` on the state and never connected them.
Both halves are specified here.

**`Step` stamps the state.** `PlayerMoveSimulator.Step` writes the simulator's current island frame anchor
into the state it returns, exactly as it already writes `Position` and `Vertical`. `FrameAnchor` is an OUTPUT
of the step, not an input a caller has to remember to set. A state that comes out of a step therefore always
carries the frame it was stepped in, which is what makes the `Reconcile` conversion above well defined for
`predictedState`. For `authoritativeBasis` the anchor arrives off the wire on `ReplicatedPosition.Frame` and
`WorldClient` copies it into the basis state it hands to `Reconcile`.

**The simulator's frame must equal the basis's frame before the replay loop.** `Reconcile` replays pending
commands from `authoritativeBasis` at `:231-235`. Those steps must run in the basis's frame or the replayed
trajectory is stepped in one space from a start point in another. So the ordering is fixed:

1. Convert the presentation state (above).
2. `simulator.Frame = authoritativeBasis.FrameAnchor`, and if it changed, `physics.Rebase(newAnchor)` on the
   island's physics world. This is `WorldClient`'s adoption point and the only place the client's frame is
   ever written.
3. `FrameChanged` fires here, before the replay, so a consumer's own collider bookkeeping is correct for the
   replayed steps.
4. Drop acknowledged commands and replay.

`WorldClient` owns steps 2 and 3 because it owns the simulator and the frame adoption. `ClientPrediction`
owns 1 and 4. The seam between them is that `Reconcile` may assume the simulator is already in the basis's
frame, and it asserts it in debug rather than trusting it.

### The pending-command buffer is already frame-invariant, which is the reason this is cheap

`MoveProtocol.EncodeMove` sends a 2D move axis, run/jump flags and a camera yaw, never a position. So the
buffer `Reconcile` replays holds INPUTS. Replaying inputs from a new-frame basis simply produces new-frame
results, with no per-command conversion and no risk of a half-converted buffer. Nothing about the replay loop
itself changes.

One consequence for the test plan: during a long replay under packet loss the replayed local coordinate is
bounded by how far the player can travel in `MaxPendingCommands` ticks, which is 256 at
`PredictionSettings.cs:24`, or 4.27 s at 60 Hz, roughly 26 m at sprint. That is comfortably inside the frame,
but the bound is `ReanchorRadius + travel`, not `ReanchorRadius`, and section 12's test 12 is written to the
right bound.

### `MovementState` is safe against out-of-order anchor updates, and here is the one-line reason

`MovementState` is a separate component registered `discreteSample: true` (`MoveProtocol.cs:105-107`), so it
is fixed-delay nearest-SAMPLED rather than interpolated, it carries no planar position at all (only the
vertical axis, timers, quantized rates and flags), its `TeleportEpoch` is a monotonic counter rather than a
coordinate, and Y is never framed. Nothing in it is expressed against a frame, so an anchor update that
arrives on `ReplicatedPosition` in a different snapshot than a `MovementState` update cannot make the two
disagree.

### `TeleportEpoch` is deliberately NOT reused for this

An epoch advance means "cut instantly, this is an intentional discontinuity". A re-anchor is the exact
opposite: a no-op in world space that must be invisible. Overloading the epoch would hard-cut the avatar on
every re-anchor. The frame is a separate channel because it carries the opposite meaning.

### `WorldClient` surfaces the change for consumer-owned colliders

```csharp
namespace KhaozEngine.NetWorld;

public sealed partial class WorldClient
{
    /// <summary>Raised when the local player's authoritative frame changes, BEFORE the replay and before the
    /// next predicted step. The argument is the EXACT translation to apply to anything the consumer holds in
    /// the old frame: collider poses it registered itself, its own spatial indices, debug overlays.
    /// <para>The engine's own state is already converted by the time this fires, INCLUDING the island's
    /// IPhysicsWorld (the island rebases it, not the consumer) and everything the streaming sink registered
    /// (a sink add reads IPhysicsWorld.Origin, section 5). A consumer that only uses the engine's sink and
    /// the engine's physics world needs no handler at all.</para></summary>
    public event Action<WorldFrame, WorldFrame, Vector3>? FrameChanged;   // from, to, delta
}
```

This is a demotion from the first draft, where handling `FrameChanged` was mandatory and included calling
`Translate` on the physics world yourself. Under the island model the island owns its physics world's rebase,
so the event is informational for the tail of consumer-owned state, and the common case needs nothing.

### The flat head's limitation, restated correctly

The flat `WorldServer` has one `World`, one flat player loop, and one physics world, so it is one island with
one frame. It re-anchors, and that follows ONE player. **A 100 km world with players spread across it requires
the sharded head**, where each `CellSim` is its own island. Ruinborne is a sharded MMO, so this is the head it
already uses. The flat head still gets the full benefit for a single-player or single-region game, and is not
regressed in any case.

## 8. Decision 6: determinism

### The rebase is exact, under a precondition that must be enforced

**Lemma.** Let `L` be a float32 local coordinate with `|L| < 128`, and let `k * 128` be an exact integer
multiple of the grid. If `|L + k * 128| <= |L|`, then `L + k * 128` is exactly representable and the addition
introduces no error.

*Why.* `|L| < 128` puts `L` in a binade no higher than `[64, 128)`, so `L` is an integer multiple of
`ULP(L) <= 2^-17`. `k * 128` is an integer, hence also a multiple of `2^-17`. The exact sum is therefore a
multiple of `2^-17`. If its magnitude does not exceed `|L|`, its binade does not exceed `L`'s, so its ULP does
not exceed `2^-17` and it is representable exactly.

The lemma generalises: nothing in it is special to 128 or to `2^-17`. For any `|L| < 2^30` and any integer
multiple of the grid, the same argument holds with `ULP(L) <= 2^(e-23)` for `L`'s exponent `e`. 128 is chosen
for the divergence budget (section 2), not for the exactness, which comes free at any power-of-two grid.

**The precondition is why the anchor rounds to nearest rather than flooring.** Round-to-nearest gives a
freshly anchored local in `[-64, 64]`, and the re-anchor trigger is `|local| > 96`, so a re-anchor always
strictly reduces the per-axis magnitude and the lemma applies. Flooring to the containing cell would put the
local in `[0, 128)` and a re-anchor could carry `-32` to `+96`, growing the magnitude, at which point the
translation rounds. The error would be at most `2^-18` m, about 4 micrometres, harmless against a 10 mm
budget but fatal to the claim of bit-identity, which is the claim that makes cross-peer determinism provable
rather than merely likely.

Y is never framed, so it is untouched.

### Both peers rebase identically because neither derives anything

The stamp is authoritative and replicated (section 7). There is no derivation to disagree about. This is the
whole reason decision 1 chose an authored stamp over a derived `CellCoord`.

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
both, and #197 must land **before or alongside release 3**, because release 3 is the first one whose
correctness argument depends on bit-identity across peers.

The sequencing reason is sharper than the first draft's. Release 2 does not need #197 more than today for one
specific reason: it gives the CLIENT no frame at all (section 11), so no client derives one and no two heads
step the same tick in different frames. The moment a consumer derives a frame client-side, the two heads are
stepping 128 m apart and #197's unpinned FP register is compounding a divergence that did not exist. That is
not a reason to hurry #197, it is a second, independent reason release 2 must not ship a client-side frame.

## 9. Decision 7: rendering

The first draft's rendering section was correct about where the subtraction belongs and wrong about almost
everything downstream of it. Four blockers and three majors, all confirmed against the code. The redesign
below keeps the one decision that was right and rebuilds the rest around it.

### Where the subtraction happens: `Scene3D` owns the origin, and it subtracts at the matrix

`Scene3D` already owns both the camera and every submitted world position, so it does the subtraction and the
public API keeps taking absolute world coordinates. A consumer changes nothing.

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

    /// <summary>Whether the origin is actually in effect this frame. False when
    /// <see cref="RenderOrigin"/> is zero, and false when <see cref="ActiveCamera"/> does not implement
    /// <see cref="IRenderOriginAware"/> (see below), in which case the whole pipeline falls back to the
    /// pre-release absolute path rather than half-applying the origin.</summary>
    public bool RenderOriginActive { get; }
}
```

### Blocker 1: identity model matrices, and the fix that makes the API break disappear

Every `Scene3D` model entry point takes a caller-BUILT absolute matrix
(`Scene3D.cs:701,706,999,1002,1006,1015,1325`), there is no `Transform3D` overload on `Scene3D` at all,
`TerrainScene3D.DrawTerrainChunk` passes `Matrix4x4.Identity` (`TerrainScene3D.cs:18-19`), and
`Scene3DChunkSink`'s merged HLOD meshes pass `Matrix4x4.Identity` (`Scene3DChunkSink.cs:497,499`). With a
render-relative `ViewProjection` and unchanged model matrices, terrain and HLODs render displaced by the whole
render origin, which at 100 km means they are not on screen.

**Fix: `Scene3D` subtracts `RenderOrigin` from the TRANSLATION COLUMN of every submitted matrix**, at the one
place they all land (`_instances.Add` and the overlay/skinned equivalents):

```csharp
m.M41 -= RenderOrigin.X;  m.M42 -= RenderOrigin.Y;  m.M43 -= RenderOrigin.Z;
```

Why this is the right central point, justified rather than asserted:

- **It is exact for every matrix the engine can be handed.** A world matrix is affine (TRS, with a fourth row
  of `(0,0,0,1)`), and for an affine matrix the translation column IS the world position of the local origin,
  so subtracting the render origin from it is exactly `T(-O) * M` with no rounding beyond the subtraction
  itself, which is exact under the section 8 lemma when `O = Nearest(eye)` and the object is near the eye.
- **It costs three float subtractions per instance**, on a path that is already touching the matrix.
- **It keeps the entire public API absolute.** No `Transform3D` overload on `Scene3D`, no `TerrainScene3D`
  signature change forced by rendering, no `Scene3DChunkSink` change, no consumer change. `Transform3D` still
  gains its `ToMatrix(Vector3 renderOrigin)` overload as a convenience for a consumer that wants to build the
  reduced matrix itself, but nothing in the engine requires it.
- **It fixes the identity draws for free**, because an identity matrix's translation column is zero and
  becomes `-O`, which is exactly right for geometry whose vertices are absolute.

The one thing it cannot handle is a projective fourth row. Nothing in the engine submits one, and a debug
assert on `M14/M24/M34 == 0` documents the assumption where it lives.

### Blocker 2: `RenderOrigin` cannot reach `Scene3D.ActiveCamera` through `IIsoCamera3D`

`Scene3D.ActiveCamera` is typed `IIsoCamera3D` (`Scene3D.cs:190`), which declares five getters plus a DIM
`WorldToScreen` (`Camera/IIsoCamera3D.cs:7-59`). A settable `RenderOrigin` needs backing storage, so it
cannot be a DIM, so putting it on `IIsoCamera3D` is a **breaking interface change**, which directly
contradicts release 1's "minor, adoption none".

And the camera genuinely does need it. The tempting alternative is for `Scene3D` to compose
`Matrix4x4.CreateTranslation(-O) * ActiveCamera.ViewProjection`, which needs nothing from the camera. That is
wrong for the reason the whole program exists: `View`'s translation row is `-dot(axis, Eye)`, roughly `1e5` at
100 km, and prepending a translation computes `-dot(axis, Eye) + dot(axis, O)`, a difference of two large
nearly equal float32 values. The cancellation is exactly what release 1 is supposed to eliminate. The
subtraction has to happen on the EYE, before `CreateLookAt`, which only the camera can do.

**Fix: a new optional interface, implemented by the engine cameras, with a `Scene3D` fallback.**

```csharp
namespace KhaozEngine.Render3D;

/// <summary>A camera that can build its view against a render origin. Implemented by FollowCamera3D,
/// FlyCamera3D and IsoCamera3D. Scene3D sets <see cref="RenderOrigin"/> each frame when the active camera
/// implements this, and falls back to the absolute path (RenderOrigin = Zero everywhere, byte-identical to
/// the pre-release engine) when it does not, so a consumer's own IIsoCamera3D keeps working unchanged.</summary>
public interface IRenderOriginAware
{
    /// <summary>The render origin subtracted from eye and target when building <c>View</c>. Set by Scene3D
    /// each frame. <c>Eye</c> stays ABSOLUTE world: culling and the origin choice both need it.</summary>
    Vector3 RenderOrigin { get; set; }

    /// <summary>The pre-shift ViewProjection, i.e. what <c>ViewProjection</c> returns today. Scene3D uses it
    /// for every CPU-side spatial computation that runs against absolute bounds (frustum culling, shadow
    /// cascade fitting, caster classification), so those paths stay byte-identical to the pre-release engine.</summary>
    Matrix4x4 AbsoluteViewProjection { get; }
}
```

The three concrete cameras implement it. `View` becomes
`Matrix4x4.CreateLookAt(Eye - RenderOrigin, EffectiveTarget - RenderOrigin, Vector3.UnitY)` (all three already
use `Vector3.UnitY`, verified at `FollowCamera3D.cs:203`, `FlyCamera3D.cs:64`, `IsoCamera3D.cs:46`), with
`RenderOrigin` defaulting to `Vector3.Zero` and therefore byte-identical to today.

**The fallback is a whole-pipeline fallback, not a partial one.** When `ActiveCamera` is not
`IRenderOriginAware`, `Scene3D` uses an effective origin of `Vector3.Zero` for BOTH the matrix subtraction and
the view, so geometry and camera can never disagree. A consumer camera is then exactly as precise as it is
today, which is correct-but-imprecise-at-range, and `RenderOriginActive` tells the consumer so.

**`WorldToScreen` and `ScreenToRay` must be fixed in the same commit or picking silently breaks.** Both take
or return absolute world points and both go through the now render-relative `ViewProjection`. `WorldToScreen`
subtracts `RenderOrigin` from its input, `ScreenToRay` adds it back to its output. Missing this produces a
picking error equal to the render origin, which at 100 km means picking simply does not work and no golden
catches it. Section 12 test 19 catches it.

### Blocker 3: frustum culling mixes spaces

`FrustumPlanes.Extract(vp)` at `Scene3D.cs:1869` becomes render-relative while the bounds it is tested
against (`ComputeMainPassVisibility`, and `ClassifySkinnedVisibility` at `:1919-1920`) are absolute world
bounds. Everything culls, every frame, at any non-zero origin.

**Fix: cull in ABSOLUTE space, rasterize in relative space.** That is what `AbsoluteViewProjection` is for.
`FrustumPlanes.Extract(ActiveCamera.AbsoluteViewProjection)` against unchanged absolute bounds is
**byte-identical to today's culling**, which is exactly the property the inertness guarantee needs. Only the
matrices that reach the GPU are relative. The split is one word in the code and it removes an entire class of
space-mixing bug from the CPU side, because every CPU-side spatial computation stays in the space it was
written for.

### Blocker 4: the shadow-caster union and cascade fitting

Same disease, three sites:

- `ComputeShadowCascades` fits cascades from `ShadowMapMath.FrustumCornersWorld(ActiveCamera.ViewProjection)`
  (`Scene3D.cs:1661`) and reads `ActiveCamera.Eye`/`Forward` at `:1666-1667`.
- `_shadowFrustums[i] = FrustumPlanes.Extract(_cascadeCpuVps[i])` (`:1885-1886`) is the CPU caster-visibility
  test, run against absolute bounds.
- The same `_cascadeCpuVps` feed the GPU shadow depth pass, which must be relative.

**Fix: fit in absolute, extract casters in absolute, build a second relative VP for the GPU.** Cascade fitting
runs from `AbsoluteViewProjection` and absolute `Eye`, so the fit, the radii and the caster classification are
byte-identical to today. Then, per cascade, `BuildLightViewProj(lightDir, focus - RenderOrigin, radius,
resolution)` produces the render-relative matrix the depth pass uploads. The rotation is unchanged and only
the focus moves, so the ortho extents and the texel world size are unchanged.

**The residual artifact, stated rather than hidden.** `BuildLightViewProj` snaps the focus to a texel lattice
in light-view space (`ShadowMapMath.cs:199-206`), and the lattice step is `2*radius/resolution`, which does
not divide 128 and is not axis-aligned with the world. So on the frame the render origin steps, the snapped
focus lands on a different texel and every shadow edge in the scene jumps by up to one texel, once, for one
frame. It cannot be snapped away: an origin-invariant lattice would need `R * RenderOrigin` to be a texel
multiple, which it is not for a general light direction.

Accepted and documented, on three grounds. The origin steps at most once per re-anchor, which section 2 bounds
at one per 64 m of travel and typically one per 20 s. The jump is one texel, the same magnitude as the
sub-texel swim the snap exists to prevent in the first place. And it is a single frame with no persistent
state. Filed as a follow-up if a playtest finds it visible.

### Major: the eye handed to water, particle and distortion renderers

`Vector3 eye = ActiveCamera.Eye` (`Scene3D.cs:1848`) is absolute and is passed to `_water.Draw` (`:2250`),
`_particleRenderer.Draw` (`:2268`) and `_distortionRenderer.Draw` (`:2305`), whose shaders all compute
`eye - vWorldPos` for view vectors, soft fades and Fresnel. With `vWorldPos` render-relative and `eye`
absolute, every one of those differences is wrong by the whole render origin.

**Fix: `eye - RenderOrigin` at `:1848`.** Exact under the lemma, since `RenderOrigin = Nearest(eye).Anchor`.
Both operands are then render-relative and the difference is unchanged. `BillboardGeometry.CameraBasis` takes
`ActiveCamera.Forward`, which is a direction and is frame-invariant, so it is untouched.

### Major: world-anchored shading patterns slide on every origin change

Three shader families reconstruct or consume `vWorldPos` for something ANCHORED to the world rather than to
the camera, and those slide by the render origin every time it steps:

| Shader | Use | Site |
|---|---|---|
| Terrain | triplanar UVs from `vWorldPos.yz/.xz/.xy * tile` | `ShaderSources.Terrain.cs:177-179`, `vWorldPos` set at `:60-67` |
| Model | dissolve noise `dnoise(vWorldPos * 6.0)`, explicitly "World-space noise so the pattern is stable as instances move" | `ShaderSources.Model.cs:175,245,494` |
| Water | `vWorldPos = p` for ripple and foam anchoring | `ShaderSources.Water.cs:174,190` |

**Fix: pass `RenderOrigin` as a uniform and reconstruct `vWorldPos_abs = world.xyz + RenderOrigin` for
texturing and noise ONLY.** Lighting, eye vectors and depth stay render-relative, because those are
differences and the origin cancels.

Two constraints on the implementation, both from prior engine lessons:

- **It goes into each pipeline's EXISTING uniform buffer, as one more `vec4`.** Metal on this engine is
  one uniform buffer per pipeline, so a new UBO is not an option.
- **The reconstruction is not free of precision, and that is fine.** `world.xyz + RenderOrigin` at 100 km
  lands back on the 7.8 mm float32 lattice, so the triplanar UV precision at range is unchanged from today.
  That is the honest claim: the fix PRESERVES world-anchored shading rather than improving it, and section 11
  qualifies release 1's promise accordingly.

Notably, the terrain shader already applies a per-instance model matrix (`ShaderSources.Terrain.cs:60-67`), so
the geometry half of the terrain fix needs **no shader change at all**, and the UV/normal/splat vertex
attributes are position-independent and already safe. Only the `RenderOrigin` uniform is new.

### Terrain texturing at range: the qualification, and the decision

Release 2's chunk-local bake fixes terrain GEOMETRY and terrain COLLISION at range. It does not fix terrain
TEXTURING, and the first draft implied otherwise by listing terrain under "fixed".

The triplanar UV is derived from the absolute world position, which at 100 km has a UV magnitude around `1e4`,
where one float32 ULP is a visible fraction of a texel at any sensible tiling. The result is that texture
detail quantizes and can shimmer under camera motion at extreme range, independently of the geometry being
perfect.

**Decision: accept and document, do not fix in this program.** The alternative is a frame-local texturing
anchor, which means the UV becomes discontinuous at every anchor boundary, which means a visible seam in the
texture across a line in the world, which is a worse artifact than the one it fixes and needs its own design
(a blended anchor transition, or per-chunk texturing frames with matched noise). It is a real problem, it is
not this program's problem, and it is filed rather than hand-waved.

### Everything else that carries a world position, and where it is handled

All of these are per-frame queues cleared in `Scene3D.Begin()`, so the subtraction happens once as the value
lands in its queue. None of them carries cross-frame state, so there is nothing to migrate on an origin
change. (Confirmed by review: every submission queue is cleared in `Begin()`, so nothing cross-frame can
migrate.)

| Path | Field | Handled at |
|---|---|---|
| Point lights | `ModelRenderer.PointLightData.PosRadius` (xyz) | `Scene3D.AddLight` |
| Ground decals | `GroundDecal.Center` | `Scene3D.DrawGroundDecal` |
| Shadow blobs | `ShadowBlob.Position` | `Scene3D.AddShadowBlob` |
| Water planes | `WaterPlane.CenterX`, `CenterZ` (`SurfaceY` is absolute Y, untouched) | `Scene3D.DrawWater` |
| Particle sprites | `ParticleSprite.Position` | `Scene3D.DrawParticle` and its span overload |
| Distortion sprites | `DistortionSprite.Position` | `Scene3D.DrawDistortion` and its span overload |
| Lines, fills, billboards, beams, trails | vertex positions | their submission entry points |
| Model / mesh / overlay / skinned draws | the matrix translation column | the shared submission path (blocker 1) |

**Particles** need nothing beyond that. `ParticleSystem` integrates `p.Position += p.Velocity * dt` in
absolute world space and `ParticleAttractor.Target` is absolute, but a particle's lifetime is seconds and its
travel is metres, so its absolute magnitude is whatever its emitter's is. The subtraction at
`Scene3D.DrawParticle` fixes the render. The SIMULATION of a particle at 100 km still integrates at 100 km
magnitude, which quantizes its per-tick motion to 7.8 mm. That is visible as coarse, steppy motion on slow
particles and is worth a follow-up, but it is not in this program's critical path and is filed rather than
fixed.

**`TerrainStreamer` needs no change at all.** It holds no persistent world-space float state (no `Vector3`
or `float` fields), and every frame it re-derives chunk coordinates from the caller-supplied `playerPos` via
`ChunkGrid.CoordOf`. Load, unload and ring selection are integer chunk-distance math. Only the LOD tier pick
uses a metre distance, and a distance is frame-invariant. It keeps taking absolute world coordinates.

**Skybox and fog.** The skybox is direction-only and translation-invariant. There is no fog anywhere in the
engine (confirmed by a repo-wide search during review, so the first draft's "fog is distance-based and
likewise" was describing something that does not exist). Neither changes.

**Depth precision at range is a different problem and this program does not fix it.** Depth resolution is
governed by the near-to-far ratio, not by the origin, and the engine is not on reversed-Z. Camera-relative
rendering will not improve z-fighting at a 200 m far plane and nobody should expect it to. Out of scope,
filed separately.

### Precision: no doubles, and the reason is exactness, not tolerance

The first draft described the render-origin subtraction as killing "catastrophic cancellation". That wording
is wrong and it is worth correcting precisely, because the wrong word invites the wrong fix.

Catastrophic cancellation is when subtracting two nearly equal INEXACT values amplifies their pre-existing
relative error. That is what happens today INSIDE the matrix concatenation, where a 100 km world translation
meets a 100 km view translation and the surviving small difference carries the full rounding error of both
large operands.

The subtraction this design introduces is the opposite: `p - RenderOrigin` where `RenderOrigin` is an exact
multiple of 128 and the result is smaller in magnitude than `p`. By the section 8 lemma it is **EXACT**. It
introduces no error at all, and the small result then carries the error `p` already had, no more.

So: the fix is not a mitigation of cancellation, it is a **removal of the large operands** before they ever
meet. **No double-precision or fixed-point path is needed or wanted anywhere in the render pipeline**, and
this paragraph exists so that nobody later reads "cancellation" and reaches for doubles as the obvious
remedy.

## 10. Decision 8: what does NOT change

- **Authored content coordinates.** Absolute world metres, in `MapDoc` and everywhere else. A frame is a
  runtime artifact with no representation on disk.
- **Persisted positions.** `PlayerRecord`'s flattened X/Y/Z and every `WorldStore` backend keep absolute
  world metres. A save that carried a frame would break the moment the grid constant changed, and there is no
  benefit: a save is written once, not accumulated across ticks.
- **`CellCoord`, `ChunkCoord` and `MapTileCoord` keys.** All three grids (the 60 m shard cell, the 60 m
  terrain chunk, and the tiled-mapdoc program's 512 m document tile) are computed from the ABSOLUTE position,
  always, never from a local coordinate. Two entities in different frames with the same local coordinate are
  kilometres apart, so a key built from a local would collide across frames. The absolute position stays
  exactly recoverable (`Frame.Anchor` is exact, so `Anchor + Local` is as precise as `Local`), and
  `ReplicatedPosition.Value` is the single accessor every keying site already uses. `MapTileCoord` is named
  here explicitly because the tiled-mapdoc spec and this one were mutually silent about each other and the
  coherence is by design rather than by luck (section 12).
- **`Vector3`, `Pose`, `Transform3D`, `MoveState`, `Particle`.** All stay float32. **No double-precision or
  fixed-point position type is introduced anywhere.** The point of the design is to keep magnitudes small,
  not to widen the type.
- **`CharacterMovement`.** Translation-invariant already (section 6).
- **`TerrainStreamer`.** Section 9.
- **`Y`.** Never framed, on any path.
- **`InterestGrid` and `ShardHost` handoff.** Both consume absolute positions and keep doing so
  (`ShardedWorldServer.PositionAccessor:728-733`, `ShardHost.cs:305-307`).
- **`MovementState`.** No planar position, `discreteSample`, monotonic epoch, unframed Y (section 7).
- **Frustum culling, cascade fitting and shadow-caster classification.** All run in ABSOLUTE space against
  absolute bounds and are byte-identical to the pre-release engine (section 9).

Two things DO change that read like they should not. The `ReplicatedPosition` wire encoding is a breaking wire
generation bump, and `ReplicatedPosition.Value` loses its setter. Both are why release 3 is a major
(section 11).

## 11. Decision 9: the release split

Three releases. Each is independently useful, independently testable, and independently adoptable. The first
draft's split failed all three of those claims in different ways, so each release below states its guarantee
in a form that can be checked rather than asserted.

### Release 1, minor: camera-relative rendering

`Scene3D.RenderOrigin` and `RenderOriginActive`, the new `IRenderOriginAware` interface implemented by the
three engine cameras, the origin-aware `View` on each, `AbsoluteViewProjection` for every CPU-side spatial
path, the render-relative cascade VPs beside the absolute fitted ones, the matrix translation-column
subtraction at every model submission, the converted eye for water/particle/distortion, the `RenderOrigin`
shader uniform for world-anchored texturing and noise, the fixed `WorldToScreen`/`ScreenToRay`, and
`Transform3D.ToMatrix(Vector3)`.

**What a consumer gets:** every visual artifact from matrix concatenation at range disappears. Model
placement, decals, lights, particles, water and debug geometry are all as stable at 100 km as at the origin.

**What it explicitly does NOT fix:** terrain chunk vertices (release 2, and they are baked absolute so no
render change can touch them), terrain triplanar texture precision at range (section 9, not fixed by any
release in this program), depth precision at range, and anything about simulation.

**Adoption: none, and here is what that claim rests on.** `RenderOrigin` defaults to a quantized eye and the
public API still takes absolute world coordinates. A consumer camera that does not implement
`IRenderOriginAware` falls the WHOLE pipeline back to the absolute path rather than half-applying the origin,
so it is byte-identical to today. `IIsoCamera3D` is untouched, so there is no interface break. A consumer that
wants the pre-release output exactly (a golden it has not rebaked) sets `Scene3D.RenderOrigin = Vector3.Zero`.

**The inertness guarantee, stated so it can fail.** The first draft's guarantee was one test asserting that
`RenderOrigin = Zero` reproduces the existing goldens, which proves only that the opt-out works. That is the
weaker half. The guarantee is both halves:

> 1. **The FULL existing golden suite passes with the DEFAULT non-zero render origin**, under the existing
>    per-channel `GoldenCompare.Tolerance`. Not a subset, not new goldens, not rebaked ones. Any golden that
>    moves is the signal, and the response is to fix the pipeline, not to rebake the golden.
> 2. **`RenderOrigin = Vector3.Zero` reproduces the existing goldens bit-for-bit.** The opt-out escape hatch.
>
> Half 1 is the one that actually exercises the code, and the suite already has a case that will hit it:
> `WaterDistanceBandingProbe.cs:35,70` places the eye at `z = -300`, so its default `Nearest(eye)` origin is
> non-zero and the whole relative path runs. Most goldens sit near the origin and will pick `Nearest = 0`,
> which is why half 1 is necessary but not sufficient on its own and release 1 also ships the deliberate
> far-from-origin golden (section 13 test 18).

### Release 2, minor: terrain precision, the physics rebase API, and server-side frames behind a flag

`WorldFrame` in `Primitives`. `IPhysicsWorld.Origin`/`CanRebase`/`Rebase` as DIMs plus the
`BepuPhysicsWorld` implementation and the seam conversions (`ChunkStatics`, `ChunkDynamics`,
`ChunkTerrainCollision`, `FollowCamera3D`). `TerrainChunkBuilder` chunk-local vertices with the placement in
the transform and the static pose, and `TerrainChunkCollision`/`ChunkTerrainCollision`/`TerrainScene3D`/
`Scene3DChunkSink`/`RoomNet` following (the API break list in section 5). Island frames on the SERVER heads
with `SamplerSpace`, behind `FrameAnchoring`.

**The headline is the terrain bake, and it needs no frame at all.** Chunk-local vertices plus a per-chunk
model matrix (release 1 subtracts its translation column) give precise terrain GEOMETRY at 100 km. Chunk-local
collision vertices plus a chunk-origin static pose give precise terrain COLLISION at 100 km, because Bepu
transforms a query into the mesh's local space using the static's pose, so the triangle test runs at 60 m
magnitude while the caller keeps passing whatever coordinates the world speaks. **Both work on both heads,
with `FrameAnchoring` off, with no wire change, and with no client change.** That is a real, unconditional,
zero-risk improvement and it is why this release exists in this shape.

**`FrameAnchoring` defaults OFF, and the reason is a regression, not caution.** A server stepping in a 136 m
frame while its client predicts in absolute coordinates at 100 km produces two trajectories from two spaces,
so the reconciliation error GROWS. Today both heads are equally imprecise and therefore agree. Turning the
server's frame on without the wire field would make prediction worse at range, which is the opposite of the
point. So:

> `FrameAnchoring` is opt-in in release 2 and its doc says, in the API comment and not only here: **do not
> enable this against a client that is not on release 3.** It is for a single-player or single-region game
> with no reconciled client, and for testing.

**The client gets no frame in release 2, and is explicitly told not to derive one.** `WorldClient` keeps
running in `WorldFrame.Origin`, the wire keeps carrying absolute positions (the server encodes
`Frame.ToWorld(Local)` on the way out), and `ClientPrediction` never sees a non-zero `FrameAnchor`. So the two
`Reconcile` bugs cannot fire in release 2, which is what makes it safe for them to be fixed in release 3
rather than here. Section 8 records the second, independent reason: a client-derived frame would put the two
heads 128 m apart on the same tick, which is a NEW divergence and one that #197's unpinned FP register would
compound.

**What a consumer gets:** terrain geometry and terrain collision precise at 100 km on both heads,
unconditionally. A physics rebase API it can drive itself. A single-player or single-region game is finished
at this release with `FrameAnchoring` on.

**Adoption:** rebake any terrain golden (the vertices moved). Update any direct `DrawTerrainChunk` /
`ChunkLoad` / `TerrainChunkBounds` use per the section 5 break list. Optionally set `SamplerSpace.Frame` if
the game's ground follow comes from the physics world, and `FrameAnchoring` only if there is no networked
client.

**Not fixed by this release:** many players spread over 100 km on one server, the reconciliation quantum on
the wire, client-side precision, and terrain texturing at range.

### Release 3, MAJOR: the wire, the client frame, and per-cell physics

Frame-relative `ReplicatedPosition` with the stamp in the same component and `Value` read-only, the
wire-generation bump in `MoveProtocol`, `IPredictedState.FrameAnchor`, `Step` stamping it, the
`ClientPrediction.Reconcile` frame conversion and replay ordering, `DynamicBodyReplication` sampling in the
island frame, per-`CellSim` physics worlds and per-cell `PlayerMovementSystem` on the sharded head,
`WorldClient.FrameChanged`, and `FrameAnchoring` defaulting on.

**Why major, and it is two reasons not one.** The `ReplicatedPosition` encoding changes, so client and server
must ship together. And `ReplicatedPosition.Value` loses its setter, which is a source break.

**The construction-site migration rule.** `new ReplicatedPosition { Value = v }` is the shape that would
silently stop being frame-preserving, source-compatible, with no compiler signal, at ten sites in the engine
alone. Making `Value` read-only converts every one of them into a **build error**, which is exactly what a
major is for. The rule each site then applies is one question: **where did this position come from?**

| Site | Provenance | Becomes |
|---|---|---|
| `WorldServer.cs:270` `SetPlayerState` | admin/teleport/load, absolute | `FromWorld(next.Position, islandFrame)` |
| `WorldServer.cs:303` `SpawnEntity` | authored absolute | `FromWorld(new Vector3(x, 0, z), islandFrame)` |
| `WorldServer.cs:410` per-tick step | out of the simulator, already framed | `WithLocal(state.Position)` on the existing component |
| `WorldServer.cs:598` join spawn clamp | out of `spawnClamp` in `Origin` | `FromWorld(state.Position, islandFrame)` |
| `ShardedWorldServer.cs:330` `SetPlayerState` | admin/teleport, absolute | `FromWorld(next.Position, cell.Frame)` |
| `ShardedWorldServer.cs:364` `SpawnEntity` | authored absolute | `FromWorld(new Vector3(x, 0, z), cell.Frame)` |
| `ShardedWorldServer.cs:674` join spawn | out of `spawnClamp` in `Origin` | `FromWorld(state.Position, cell.Frame)` |
| `WorldPickups.cs:169` | authored absolute | `FromWorld(position, islandFrame)` |
| `DynamicBodyReplication.cs:105` | out of the physics world, already framed | `InFrame(islandFrame, pose.Position)` |
| `MoveProtocol.cs:96` codec read | off the wire, carries its own stamp | `InFrame(readFrame, readLocal)` |
| `MoveProtocol.cs:97` codec lerp | two stamped values | rebase into `b.Frame`, then lerp (section 7) |

The grep that finds every one of them, in the engine and in any consumer, is
`grep -rn 'new ReplicatedPosition' --include='*.cs'`. It currently returns 11 production sites plus 16 in the
engine's own tests. The rule for a consumer is the same one question, and the compiler asks it for them.

**One further migration the rule does not cover**, called out because it is the one that would compile and be
wrong: `ShardedWorldServer.cs:673` calls `host.SpawnOwned(state.Position.X, state.Position.Z, ...)` to pick
the owning cell. That must key on the ABSOLUTE position. Since `spawnClamp` runs in `WorldFrame.Origin`
(spawn positions are authored absolute and one idle ground-clamp step costs one tick's quantum, not an
accumulated one), `state.Position` IS absolute there and the line is already correct. It is listed so that a
future change to run `spawnClamp` in a frame does not silently break cell assignment.

**What a consumer gets:** the full guarantee. A shard server simulating players spread over 100 km keeps every
one of them locally precise, and the reconciliation quantum stops depending on distance from the origin.

**Adoption:** repin client and server together. Fix every `new ReplicatedPosition` per the table (the compiler
lists them). Supply a `Func<CellCoord, IPhysicsWorld>` factory instead of a single physics world to
`ShardedWorldServer`. Handle `WorldClient.FrameChanged` only for colliders the consumer registered itself
outside the engine's streaming sink. Use the sharded server head for a spread world.

**Sequencing:** [#197](https://github.com/APKiwiOrg/KhaozEngine/issues/197) must land before or with release 3
(section 8).

## 12. Cross-program: the tiled-mapdoc extraction

The tiled-mapdoc and residency program
(`docs/design/TILED-MAPDOC-AND-RESIDENCY-DESIGN-2026-07-27.md`, branch `feature/tiled-mapdoc`, itself in
revision) and this one were drafted concurrently and are mutually silent. They collide in one package and
touch two of the same grids, so the coordination goes here rather than in a chat.

**Merge ordering: floating-origin's terrain work rebases AFTER the mapdoc extraction lands.** The mapdoc
program's first release moves `TerrainStreamer`, `StreamerConfig`, `IChunkSink`, `IAsyncChunkSink`,
`ChunkCoord`, `ChunkGrid`, `ChunkRing`, `ChunkBuildScheduler` and `IChunkBuildDispatcher` out of
`KhaozEngine.Terrain.Render3D` into a new render-free `KhaozEngine.Terrain`, with type forwarders left behind.
Floating-origin's release 2 rewrites `TerrainChunkBuilder` and `Scene3DChunkSink`, which stay in
`Terrain.Render3D` but sit right beside the moved types and call into several of them. Same package, same
window, and the extraction is a whole-file move while the terrain bake is a line-level rewrite, so a
simultaneous merge is a conflict on files that moved. The extraction lands first and this program rebases onto
it. Nothing in this design depends on which assembly those types end up in.

**Coordinate coherence, by design rather than by luck.** The mapdoc program adds a THIRD grid,
`MapTileCoord` at a 512 m default tile, on top of the 60 m shard cell and the 60 m terrain chunk. Its
`MapTileGrid.CoordOf` delegates to `ChunkGrid.CoordOf`, and residency foci and placement rect queries all key
on absolute world coordinates. Floating origin keeps every grid key absolute (section 10), so the two programs
compose without either one knowing about the other. That is stated in section 10's what-does-not-change list
with `MapTileCoord` named, so a future change that tries to key a grid off a local coordinate has a written
reason not to.

**`CellSize` versus `WorldFrame.Grid`: a constraint that must be validated, not assumed.**
`ShardedWorldServerConfig.CellSize` defaults to 60 (`ShardedWorldServer.cs:20`) and `ShardHost` validates only
that it is positive (`ShardHost.cs:73-74`). Under the island model a cell's frame is fixed at its centre, so a
consumer that sets `CellSize = 512` (a plausible value once the mapdoc program makes 512 a familiar number in
this codebase) gets locals reaching `256 + OverlapMargin + 64`, which is 344 m: inside the 512 m binade ceiling
but with only a 1.5x margin instead of 3.0x, and one config change away from being outside it.

Release 3 adds the validation, with the derivation in the message:

```csharp
// ShardedWorldServer ctor, beside the existing InterestRadius <= OverlapMargin check.
float worstLocal = config.CellSize * 0.5f + config.OverlapMargin + WorldFrame.Grid * 0.5f;
if (worstLocal > WorldFrame.MaxLocalRadius)
    throw new ArgumentException(
        $"CellSize {config.CellSize} with OverlapMargin {config.OverlapMargin} puts a cell's worst frame-local "
      + $"coordinate at {worstLocal} m, past the {WorldFrame.MaxLocalRadius} m float32 divergence ceiling. "
      + "Reduce CellSize.", nameof(config));
```

## 13. Decision 10: the test plan

### The acceptance test for the whole feature

Mirrors Ruinborne's `FarFromOriginPrecisionTests` in shape, with one correction: it carries its own ground
truth. Lands in `KhaozEngine.Server.Tests` (it references `Locomotion`, `Physics.Bepu` and `NetWorld`).

> Run the same 600-tick command stream at 30 Hz three ways over identically shaped terrain: (a) a
> **double-precision reference** trajectory, the same step arithmetic widened to `double`, which is the ground
> truth, (b) float32 unframed at a 100,000 m offset, (c) float32 framed at the same offset with island
> anchoring ACTIVE and a shifted sampling delegate. Assert `|c - a| <= 10 mm` and that grounded-tick counts
> match `a` exactly. Assert `|b - a|` is at the measured magnitude (roughly 1,724 mm), so the harness has a
> known-failing baseline proving it can detect the failure it claims to prevent.

Repeat at 50,000 m against its own measured baseline (roughly 822 mm).

The first draft compared framed-at-100 km against unframed-at-origin and called the second ground truth. It is
not: an unframed origin run accumulates its own error as the player walks away from zero, so that comparison
measures the difference of two errors. The double reference removes the ambiguity, and it is cheap because
`CharacterMovement.Step` is pure arithmetic over a small state.

### Invariant tests, `KhaozEngine.Foundation.Tests` (references `Primitives`)

1. **Re-anchor is bit-exact.** For a swept set of locals and frame deltas satisfying the section 8
   precondition, `frame.DeltaTo(target)` applied to a local reproduces the world position with a bit-identical
   round trip. Compare raw bits, not an epsilon.
2. **Round-to-nearest never grows a local's magnitude.** The precondition the lemma needs, asserted directly,
   so a future change to floor anchoring fails a test instead of silently rounding.
3. **Anchors are exactly representable.** `X * WorldFrame.Grid` round-trips for the whole `short` range.
4. **Hysteresis gives at least 64 m of separation.** An entity oscillating across a boundary re-anchors at
   most once, AND a straight-line traversal re-anchors at most once per 128 m. Both bounds, since section 2
   relabelled the band and the reversal bound is the one that sizes the rebase budget.
5. **`WorldFrame.Origin` is `default`**, and the whole API at the origin is byte-identical to unframed math.
6. **The binade divergence model.** `MaxLocalRadius` is the top of the last binade whose `215 * ULP` fits the
   10 mm budget, asserted from the constants rather than from a comment, so a future budget change cannot
   leave the ceiling stale.

### Physics tests, `KhaozEngine.Server.Tests` (references `Physics.Bepu`)

7. **Rebase round trip.** Build a world, record every pose, `Rebase(o)`, `Rebase(Vector3.Zero)`, assert poses
   are bit-identical and `Origin` is back to zero.
7a. **A sleeping body on a translated STATIC stays asleep and does not drift.** The terrain case the probe
   never covered: settle a dynamic onto a static, let it sleep, `Rebase` so BOTH move, then assert `IsAwake`
   is false and the body has moved 0.000000 m relative to the static across 60 further steps. This is the one
   whose failure mode is a crate sinking into terrain after a rebase.
8. **Contacts survive.** A settled stack, `Rebase`, 60 steps, assert per-body drift under 1 mm and no
   velocity spike.
8a. **The small-frame shift has no drift term.** The probe measured 0.365 mm of drift rebasing a stack INTO a
   100 km destination and attributed it to the destination magnitude. Rebase the same stack into a 136 m
   frame and assert the drift is at least an order of magnitude smaller, which converts an inference into a
   measurement.
8b. **The rebase cost budget.** Build a world at Ruinborne's resident scale (order 25 terrain statics, 2,000
   prop statics, 200 dynamics), time one `Rebase` and one `Step`, assert `Rebase < Step`. The budget from
   section 5, as a test rather than an estimate.
9. **Constraints survive.** A hinge and a slider, one end a world anchor, `Rebase`, assert the joint still
   holds and the anchor moved with it.
10. **Statics move.** Raycast down onto a static, `Rebase`, raycast at the translated coordinate, assert the
    same hit distance.
10a. **A non-zero-origin terrain raycast.** Every terrain physics test today uses origin 0. Build a terrain
    collision chunk at a 100 km region origin with chunk-local vertices, raycast down at a point inside it,
    and assert the hit height matches `field.SampleHeight` at that absolute point within a millimetre. This is
    the test that proves the release 2 headline, and its absence is why the bake could have shipped broken.
11. **`CanRebase` is false and `Rebase` throws on a seam default.** The DIM contract.

### Netcode tests, `KhaozEngine.Server.Tests`

12. **Wire round trip.** `ReplicatedPosition` at 100 km encodes and decodes to a bit-identical `Value`, and
    the decoded `Local` magnitude is under `ReanchorRadius + MaxPendingCommands * TickSeconds * maxSpeed`.
    That bound, not `ReanchorRadius`: a replay under loss legitimately carries the local up to 256 ticks of
    travel past the trigger (`PredictionSettings.cs:24`, 4.27 s at 60 Hz), so a bound of `ReanchorRadius`
    alone fails on a correct implementation.
13. **Cross-frame lerp.** Two snapshots in different frames interpolate along the straight world-space line,
    with no frame-width excursion at any `t`.
14. **A re-anchor manufactures no correction.** Reconcile across a frame change and assert
    `ReconciliationResult.PositionError` is unchanged from the same scenario without the frame change, that
    `HardSnapApplied` is false, and that `renderOffset` did not pick up the anchor delta. This is the test for
    the two bugs section 7 identifies, and it must exercise BOTH, so it asserts the render offset explicitly
    rather than relying on the gate.
15. **The client never derives its anchor.** Feed the client a basis whose frame is deliberately not the one
    the client would pick, and assert the client adopts the server's. This asserts the decision-1 property
    directly.
16. **Legacy shape.** A game entirely at the origin produces byte-identical wire output to the pre-frame
    encoding for the `Local` triple, so the change is provably inert at the origin.
20. **`DynamicBodyReplication` samples in the island frame.** Track a body in a rebased physics world, call
    `Sample`, and assert the entity's `ReplicatedPosition.Value` is the body's ABSOLUTE world position, not
    its frame-local one. The section 7 bug, as a regression test.
21. **Per-cell frames on the sharded head.** Two players in cells whose frames differ, both stepping in the
    same tick, both querying their own cell's physics world, both landing on their own cell's terrain. The
    section 3 blocker, as a test: under the first draft's model this test cannot pass.

### GPU goldens, `KhaozEngine.Render.Tests/Gpu`

17. **The full existing golden suite passes with the DEFAULT non-zero render origin.** Not a new golden: a CI
    leg that runs the existing suite with the origin default in force. Release 1's inertness guarantee,
    half 1. `WaterDistanceBandingProbe` (eye at `z = -300`) already exercises a non-zero origin, so the leg
    has real coverage from day one.
18. **`RenderOrigin = Vector3.Zero` reproduces existing goldens exactly.** Half 2, the opt-out.
19. **A new far-from-origin golden.** The same scene rendered at the origin and at 100 km with camera-relative
    rendering on must produce the same image within the existing cross-backend tolerance. This is the test
    that proves the render half, because the failure mode is visual jitter and vertex swim that no numeric
    assertion describes.
19a. **Picking at range.** `WorldToScreen` and `ScreenToRay` round-trip at 100 km. Headless, no golden needed,
    and it catches the section 9 landmine that a golden cannot.
19b. **A consumer camera falls back cleanly.** A test double implementing `IIsoCamera3D` but NOT
    `IRenderOriginAware`, rendered through `Scene3D` with a non-zero `RenderOrigin` set, produces
    byte-identical output to the pre-release engine and reports `RenderOriginActive == false`. This is what
    makes release 1's "adoption: none" claim testable rather than asserted.

**A new golden needs the D3D11 plus Vulkan CI bake before it lands.** Run `cross-platform-gpu.yml` via
`workflow_dispatch` with `bake = true`, which renders the legs with `KE_UPDATE_GOLDENS=1` and uploads the
per-backend grids. A golden baked only on the Metal dev machine turns `main` red on the other two legs.

## 14. Premises from #337 that turned out to be wrong or incomplete

Checked against the code rather than assumed. The issue's survey is accurate on the whole and these are the
exceptions. Items 8 to 10 are premises of the FIRST DRAFT of this document rather than of the issue, kept
here because a design that was wrong once about its own head is worth recording.

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
   **Camera-relative rendering cannot fix this and neither can a physics rebase,** because the error is
   in the buffer before either runs. This is a whole workstream the survey does not list.

4. **"The three camera `View` getters and `Transform3D.ToMatrix` cover most of rendering."** True for matrix
   construction and misleading about coverage in two separate ways. `Scene3D` submits about a dozen
   independent world-space payloads that never pass through either (lights, decals, shadow blobs, water
   planes, particle and distortion sprites, line/fill/billboard/beam/trail vertices), and each needs its own
   subtraction. And the matrices themselves are CALLER-built and absolute at every `Scene3D` entry point, with
   `TerrainScene3D` and the HLOD path passing `Matrix4x4.Identity`, so "the camera covers it" is false for the
   largest geometry in the scene. `WorldToScreen`/`ScreenToRay` are also missing and are the one place where
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

8. **This document's own first draft: "`PlayerMoveSimulator` is the choke point, and it already is one."**
   False for the sharded head, which is the head the whole design exists for. `PlayerMovementSystem.Update`
   calls `CharacterMovement.Step` directly (`PlayerMovementSystem.cs:84`). `PlayerMoveSimulator` reaches the
   sharded head only as a one-shot spawn ground-clamp (`ShardedWorldServer.cs:158,670`). A property on the
   simulator would never have reached the server that needed it.

9. **This document's own first draft: a per-ENTITY anchor with one shared physics world.** Mutually
   exclusive, for the reason in section 0. The correction is section 3's re-score, and the generalisable
   lesson is the one at the top: a frame is a property of a space, and a physics world is a space.

10. **This document's own first draft: "the accumulation window is bounded too."** False. A re-anchor is an
    exact translation and therefore carries accumulated divergence forward unchanged. The design bounds the
    per-window growth RATE, not the total. Section 2 states it correctly, and the acceptance test now carries
    a double-precision reference because the old one compared two float32 error terms against each other.

## 15. Deferred, and filed rather than fixed

- **Terrain texturing at range.** The triplanar UV is derived from an absolute world position, so at 100 km
  the UV magnitude is around `1e4` and a float32 ULP is a visible fraction of a texel. Not fixed by the
  chunk-local bake (which fixes geometry and collision) and not fixed by camera-relative rendering (which
  reconstructs the absolute position for exactly this purpose). A frame-local texturing anchor introduces a
  visible seam at every anchor boundary and needs its own design. Section 9 states the decision.
- **Particle simulation at range.** `ParticleSystem` integrates absolute positions, so a particle at 100 km
  moves in 7.8 mm steps. Release 1 fixes how it is drawn, not how it moves. A frame-local particle system is
  its own change.
- **Depth precision at range.** Reversed-Z or logarithmic depth. Governed by the near-to-far ratio, unrelated
  to the origin, and explicitly not fixed here so nobody expects it to be.
- **One-frame shadow-edge jump on an origin step.** The light-space texel snap lattice is not origin-invariant
  (section 9). One texel, one frame, at most once per re-anchor. Filed in case a playtest finds it visible.
- **Contact-preserving dynamic-body handoff between per-cell physics worlds.** A dynamic crossing a cell
  boundary is removed from one world and added to another, losing its contact cache (section 5). Bounded and
  accepted for release 3.
- **Amortized rebase.** Section 5's budget requires one rebase to cost less than one physics step. If a
  consumer's resident collider population exceeds that, the refit needs to be spread across ticks, which is a
  real design change and is not in this program.
- **The two duplicated grid conversions.** `Replication/InterestGrid.cs:72` privately re-implements
  `Sharding/CellCoord.cs:38`'s `floor(v / cellSize)` rather than calling it, and `Terrain/ChunkGrid.cs:15` is
  a third copy. The tiled-mapdoc program's `MapTileGrid` deliberately delegates to `ChunkGrid.CoordOf` rather
  than adding a fourth. Nothing in this program depends on unifying them, but a floating-origin change that
  had touched grid keys would have had to keep three copies in sync.
- **`WorldServer` (flat head) at 100 km with spread players.** Structurally single-island. Section 7 states
  the limitation. Making the flat head multi-island means giving it the island structure `ShardHost` already
  has, which is a reason to use `ShardHost`, not a reason to build it twice.
