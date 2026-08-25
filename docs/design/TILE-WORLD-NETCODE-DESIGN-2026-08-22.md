# Tile-world netcode: click-to-walk on a 250 ms tick, server-authoritative, sharded by region (2026-08-22)

Status: R1 SHIPPED in engine 17.40.0, R2 next. **Section 5's step model was REVERSED in 18.1.0 by a playtest
ruling: a step commits its tile when it STARTS. Read section 5.1 before section 5.** The package is
`KhaozEngine.TileWorld.Netcode`. Where this
document and the shipped code disagree, the code's own type docs win: the tick that carries a command is a FULL
step tick, the run mode rides every command and applies at the next step start, a cross-plane command is dropped
whole, `TileReach` tests the step OUTWARD from the footprint, and the arrival turn happens in the simulator so both
heads predict it. Program issue: [#670](https://github.com/APKiwiOrg/KhaozEngine/issues/670). Sub-project 2
of the Grimhollow program, following sub-project 1 ([TILE-WORLD-DESIGN-2026-08-15.md](TILE-WORLD-DESIGN-2026-08-15.md),
[#629](https://github.com/APKiwiOrg/KhaozEngine/issues/629)), whose tile world, collision map, pathfinder and
renderer this design moves players across. Game-side issue:
[Grimhollow #6](https://github.com/APKiwiOrg/Grimhollow/issues/6). Written against engine 17.39.0 and Ruinborne main.

Two rounds, each with its own plan: R1 is the engine package `KhaozEngine.TileWorld.Netcode` with headless loopback
tests, R2 is the Grimhollow heads on it (server, auth, client player and camera).

## 1. Problem

The engine has one MMO movement stack and it is continuous: `KhaozEngine.NetWorld` replicates a float
`ReplicatedPosition` plus a `MovementState` of capsule physics (vertical velocity, grounded, swimming, climb rate),
predicts through `PlayerMoveSimulator` over `Locomotion.MoveState`, and persists a float `PlayerRecord`. The tile
world needs the opposite shape: a player IS a tile, a plane and a facing, movement is a discrete step every N ticks
along a deterministic route, the server resolves one step per tick and the client is allowed to show the walk
before the server confirms it. OSRS is the reference: click, walk at once, the server is right when they disagree.

What can and cannot be reused was verified against the code before any of this was decided (section 3). The short
version: the tick engine, prediction, replication, sharding, persistence store and identity are all generic and
reusable verbatim. The one thing that is not is `NetWorld`'s server and client, which construct the float
integrator internally and take terrain in their constructors.

## 2. Decisions taken in the brainstorm, with rationale

1. **A true tick, 250 ms (4 Hz).** OSRS runs 600 ms. The feel comes from tick-aligned actions, not from smooth
   motion, and 250 ms keeps that feel while making the game read as faster. Movement rates are tick counts: **run is
   one tile per 2 ticks (500 ms per tile), walk is one tile per 4 ticks (1 s per tile)**, diagonal steps cost the
   same as cardinal ones. The alternative, a 30 Hz sim with a 250 ms action cadence layered on it, was declined as
   two clocks to reason about for a smoother remote glide the presenter already provides.
2. **Client-predicted walking, server authoritative.** On click the client runs the same `FindPath` over the same
   baked collision map and starts walking on the next tick. The server runs the identical path. Every snapshot
   carries the tile at the acked tick and the client snaps only on mismatch. Declined: a server-authoritative display
   (one RTT plus a tick of nothing after every click, the thing OSRS visibly does not do).
3. **Sharded from day one, on `ShardHost` and `CellSim`.** A single-process server with a per-region sim as the
   unit was offered as the cheaper seam. The user chose the existing sharding stack, and the adaptation that makes it
   fit without fighting its float grid is to feed it tile coordinates as the plane (section 7).
4. **Discord identity end to end in SP2.** A guest-first client with the token gate in place was offered. The user
   chose the full path: the exchange service, the client OAuth flow through the engine's `IdentitySession`, and a
   whitelist store. All of it is engine plumbing already exercised by Ruinborne, the game-specific part is two files.
5. **Walk plus the interaction seam, no real actions.** Clicking an `Interactive` archetype routes to a reach tile,
   faces the target, and sends one `Interact`. The server validates reach on its tick and runs a one-deep action
   queue whose only action here is a logged no-op with an `OnInteract` event. Declined: walk only (the reach rules
   are where tile MMOs get subtle and they sit on the movement code), and a first real action (stairs as a plane
   change adds multi-plane presentation to a sub-project whose deliverable is walking).

Persistence and the wire protocol were not questions: `SqliteWorldStore` locally, `SqlServerWorldStore` hosted,
one env var picks, the tile record is additive, and the tile protocol is its own framing inside `Netcode`'s
transport.

## 3. What the engine already has, verified

Tick rate is a per-config float, never a constant: `WorldServerConfig.TickSeconds = 1f/30f` (`WorldServer.cs:16`),
`ShardedWorldServerConfig` (`:14`), `WorldClientConfig` (`:10`). `FixedTickHost(float tickSeconds)`
(`Simulation/FixedTickHost.cs:21`) accepts any positive value and sheds backlog past `maxTicksPerFrame = 8`
(`:55`, `:70`). At 250 ms the tick-relative knobs are already close: `InterpolationDelayTicks = 2f`
(`WorldClientConfig.cs:31`) is 500 ms, `DisconnectTimeoutSeconds = 3f` (`:35`) is twelve snapshots, and only
`MaxInputBacklog = 8` ticks (`WorldServer.cs:56`) wants lowering to 2. Snapshot cadence is welded to the sim tick
(`WorldServer.Tick`, `:470-500`), which at 4 Hz is exactly the cadence wanted.

Prediction is pluggable at `Netcode`: `ITickSimulator<TState,TCommand>.Step(in state, in command, float dt)`
(`ITickSimulator.cs:8`), `IPredictedState<TSelf>` (`IPredictedState.cs:10`, a `Vector2 Position`, `float Vertical`,
`uint TeleportEpoch`, `Vector2 FrameAnchor`, `WithPosition`, `WithRenderState`), `ClientPrediction<TState,TCommand>`
(`ClientPrediction.cs:24`, `Predict` `:259`, `Reconcile` `:292` replaying the unacked window through the same
simulator `:331`). `RenderedState` (`:132-144`) already lerps the previous tick to the current one over
`TickSeconds`, which is the tile glide. `RemoteCommandQueue<T>` (`RemoteCommandQueue.cs:18`) is generic and drains
one command per player per tick. **It is baked at `NetWorld`:** `PlayerMoveSimulator` is constructed inside
`WorldServer` (`:191`), `WorldClient` (`:134`) and `ShardedWorldServer.Frame.cs` (`:126`), and all three take
`Func<float,float,float> groundHeight` and `MoveTuning` as required constructor parameters. `PendingMove` and the
server's queue are `MoveCommand`-typed. The client-to-server demux keys the move frame on LENGTH 18
(`MoveProtocol.cs:243-248`), so any new protocol on that transport must not emit 18-byte data frames.

Replication is movement-agnostic: `ReplicationRegistry.Register<T>(typeId, write, read, lerp, channels,
discreteSample)` (`ReplicationRegistry.cs:51`), extension ids from 16 are length-prefixed and skippable (`:24`),
`discreteSample: true` gives nearest-sample with no blending, the right thing for integer tiles. The only float
touchpoint is `InterestGrid.Insert(netId, x, y)` (`InterestGrid.cs:28`), a plain spatial hash.

Sharding: a shard is a square cell `CellCoord(int X, int Y)` from `CellCoord.FromWorld(x, y, cellSize)`
(`CellCoord.cs:12`, `:34`). `ShardHost(cellSize, tickSeconds, registry, interestCellSize, ...)` (`ShardHost.cs:99`)
creates cells on demand. `CellSim` (`CellSim.cs:26`) owns an ECS world, a replicator, an interest grid, its own tick,
border ghosting (`ApplyGhostSnapshot` `:222`) and exactly-once handoff (`AdoptFromMigrate` `:479`,
`ReleaseMigrating` `:502`). `ShardedWorldServer` enforces `InterestRadius <= OverlapMargin` (`:81-84`).

Persistence: `IWorldStore` (`IWorldStore.cs:16`) is a keyed blob store. `PlayerRecord` (`PlayerRecord.cs:15`) is a
version, `float X, Y, Z` and an opaque `byte[]? Game`, tolerant JSON. `WorldPersistence` (`WorldPersistence.cs:200`)
owns the save interval, dirty pass, quarantine, guest policy and resume hints, and is driven through
`IWorldPersistenceHost`. Both store backends share one `world_store(key, data, updated_at)` table
(`SqliteWorldStore.cs:33`, `SqlServerWorldStore.cs:45`). Ruinborne picks by `RUINBORNE_SQL_CONNECTION` and has never
used the Sqlite backend (`Ruinborne.Server/Program.cs:161-165`).

Identity: `IIdentityValidator`, `VerifiedIdentity`, `IdentitySession`, `SessionToken.Mint/TryVerify` and the Discord
provider are engine. The connect gate is `IConnectionAuthenticator` (`Netcode/IConnectionAuthenticator.cs:12`) with
`HmacTokenAuthenticator` (`SignedToken.cs:229`) and `AllowAllAuthenticator`. Ruinborne's `ConnectionGate.Wrap(inner,
protocolVersion, worldHash, log)` (`Ruinborne.Server/Auth/ConnectionGate.cs`) and its two-file `Ruinborne.Auth`
exchange (`AuthExchange.cs:28-51`) are the only game-side pieces.

Interaction: nothing. `Simulation` is a tick host and two job schedulers, `Ecs` is a pure archetype ECS, and the
only proximity code is `WorldPickups`' float-radius auto-collect. `TileArchetype.Interactive`
(`TileWorldCatalogs.cs:72`) is an authored flag nothing consumes.

Tile world: `TileCollision.CanStep(map, x, z, plane, dir, agentSize)` (`TileCollision.cs:19`) and
`TilePathfinder.FindPath(map, plane, start, goal, agentSize, maxRadius)` (`TilePathfinder.cs:54`) are deterministic
and were written for server-authoritative replay (`:35-36`). The collision map is derived by
`TileCollisionBaker.Bake(doc, catalogs)` (`TileCollisionBaker.cs:14`) from a GPU-free `TileWorldFile.Load(directory)`
(`TileWorldFile.cs:125`), so both heads bake their own from the same files. `TileWorldHash` identifies a world
build. `FindPath` allocates its scratch per call, [#669](https://github.com/APKiwiOrg/KhaozEngine/issues/669).

## 4. Package plan

One new package, `KhaozEngine.TileWorld.Netcode`, GPU-free, referencing `TileWorld`, `Netcode`, `Replication`,
`Sharding`, `Simulation` and `WorldStore`. It does NOT reference `NetWorld`: the two movement stacks are siblings
over the same generic layers, and pulling `NetWorld` in would drag `Locomotion` into every tile server. It joins the
`Server` umbrella (server half) and `Game3D` (client half) the way `NetWorld` does, decided per type at the plan
stage by which head constructs it.

Two engine changes outside the package:

- `ConnectionGate` moves from Ruinborne into `Netcode` as `ConnectionGate(inner, protocolVersion, worldHash, log)`
  (engine-first rule: two games need the identical gate). Ruinborne's copy becomes a one-line alias until its next
  repin.
- `WorldPersistence`'s core becomes record-agnostic (section 8). `NetWorld.WorldPersistence` keeps its name, its
  public surface and its tests, as the float binding over that core.

## 5. State, commands and the simulator

> **Superseded in part by section 5.1.** Everything below is the model as designed and as shipped in 17.40.0. The
> commit MOMENT was reversed in 18.1.0: a step commits its tile when it STARTS, `TileMoveState` gained `StepFrom`,
> and the splice this section describes was deleted rather than adapted. The rest of the section still holds.

`TileMoveState : IPredictedState<TileMoveState>`: `TileCoord Tile` (x, z, plane), `TileDirection Facing`,
`TileMoveMode Mode` (Walk, Run), `byte StepTicks` (ticks spent in the current step), `TileRoute Route` (an immutable
tile array plus the index of the next tile, `TileRoute.None` when idle), `uint TeleportEpoch`, and the pending
interaction target. The contract members derive from those: `Position` is the tile position plus the step fraction
toward the next route tile (`StepTicks / TicksPerStep` along the step's delta), `Vertical` is the plane height,
`FrameAnchor` is zero (no frame anchoring on a tile world, the coordinates are small integers). The route is a
reference to an immutable array and is recomputed deterministically on replay, so carrying it in the state keeps the
state a value and the simulator pure.

`TileCommand`, sent once per tick exactly like today's move command, a dozen bytes at 4 Hz: `Kind` (None, WalkTo,
Interact), `TileCoord Goal`, `TileMoveMode Mode`, `long Target` (net id or object handle for Interact). `None` means
"keep doing what you are doing", which is the command the client sends while a route plays out, and what makes
`ClientPrediction`'s one-command-per-tick sequence model fit without a "walking" flag on the wire.

`TileMoveSimulator : ITickSimulator<TileMoveState, TileCommand>` owns the shared inputs (the collision map, the
document for footprints, the tick counts) and is the ONE stepper both heads run:

- `WalkTo`: `FindPath(map, plane, from, goal)` into a new `Route`, mode taken from the command, and the tick
  that carries the command COUNTS as the first tick of the first step, so a click never costs a tick of standing
  still. An unreachable goal walks to the nearest reachable tile, the OSRS rule `FindPath` already implements. A
  goal on another plane is dropped whole rather than pathed on the player's own plane. **`from` is not always
  `state.Tile`.** The step in progress is never abandoned, which is OSRS's own rule: a click arriving with
  `StepTicks` above zero keeps that progress, its total and the tile the step is entering, paths from THAT tile,
  and splices the result behind it, so the in-flight step commits exactly as it would have and the new walk
  continues from where the foot lands. Pathing from `state.Tile`, the tile being LEFT, drags the drawn position
  back toward it first, which is a visible stutter on every direction change while moving and is predicted on both
  heads, so no correction ever cleans it up. A click on a step BOUNDARY (progress at zero, standing included) has
  nothing in flight and starts from the tile stood on. The route cap counts the spliced step, so a re-click every
  step cannot ratchet a route past `MaxRouteSteps`.
- `Interact`: `TileReach.TryNearest(map, footprint, plane, from, ...)` picks the reach tile, routes to it, and
  records the target so arrival faces it and raises the action. `from` is the same tile `WalkTo` paths from, so a
  booth clicked while already walking splices the same way and the unreachable answer still finishes the step in
  flight before it stands. A target on ANOTHER plane is dropped whole, the same
  answer `WalkTo` gives a cross-plane goal, and the target is resolved BEFORE anything is written so the tick reads
  exactly as if no command had arrived. A target on the player's OWN plane with no reachable reach tile is the other
  answer: the route is dropped and the pending target cleared, which is the state a `CannotReach` accompanies. The
  arrival TURN is the simulator's too: on the tick the route empties with a target still pending, the player faces
  `TileReach.FacingToward` for the tile they landed on, guarded by `TileReach.Contains` because a re-path can leave a
  route that stops short of one, so a walk arriving from the side ends facing the booth rather than keeping the
  diagonal its last step left, and the whole outcome of a click is predicted instead of one attribute of it landing a
  snapshot late.
- `None`: take the mode from the command (it rides on every kind, and a change lands at the START of the next step
  rather than re-cutting the one under way), then advance `StepTicks`, which EVERY tick does, a tick carrying a
  command included. When it reaches `TicksPerStep(Mode)` (2 run, 4 walk), re-check `CanStep` from the
  current tile into the next route tile. Legal: move, face the step direction, reset `StepTicks`, advance the
  index. Blocked (a dynamic blocker appeared): re-path once from the current tile to the route's end, and if that
  also fails, drop the route and stand. Both heads run this identically, so a blocker only causes a mismatch when
  the two heads saw different blockers, which is exactly the case the reconcile snap is for.

Remote players replicate `TileMoveState` as an extension component with `discreteSample: true`, and the presenter
(section 9) glides them across a step over its tick count from the previous sample, so a remote at run speed is
seen stepping every second snapshot with motion in between.

## 5.1 The reversal: a step commits its tile when it STARTS (18.1.0)

Section 5 above is the model as designed and as shipped in 17.40.0, and it is kept because the reasoning behind
every other decision in it still holds. One thing in it was WRONG, and Grimhollow's playtests are what found it:
the tile committed at the END of a step, so `TileMoveState.Tile` named the tile being LEFT for the whole of that
step and the authoritative tile TRAILED the drawn body. It leads now.

**The ruling.** Fast, snappy gameplay under a slow tick comes from answering a click against where the player is
GOING, not where they are half off. That is OSRS's own rationale: a 250 ms tick is long enough that a step spans
several frames, so which end of the step the simulation is allowed to act on decides whether a click feels
immediate or feels like a wait. Trailing, a player who clicks a booth as they take the last step toward it waits
that whole step before anything happens, and everything the rules ask about them in the meantime is answered about
a square they have visibly left.

**What changed.** On the tick a step starts, `CanStep` is checked from the tile stood on, then `Tile` flips to the
step's target, `StepFrom` records the tile being left, `Facing` takes the step's direction and the route pops. The
remaining ticks of the step glide the DRAWN body from `StepFrom` into `Tile`. `Position` and `TilePresenter.Pose`
are that glide, and neither reads the route any more.

**The trade, stated plainly.** The simulation is AHEAD of the picture, by strictly less than one step: the commit
and the glide start on the same tick, so the lead is at most (StepTotal - 1) / StepTotal of a tile and it is zero
at the moment the body lands. That is a bounded, always-forward disagreement between the rules and the picture,
and it buys a step of responsiveness on every click. The alternative shapes were both worse. Committing at the end
(the old model) trails by the same amount in the direction that costs latency. Committing at the start but drawing
the body on the committed tile would teleport the avatar a tile per step, which is the thing the glide exists to
avoid.

**Why `StepFrom` is a field rather than a derivation.** The obvious saving is to read the glide's origin back off
`Facing`, since a step sets the facing to its own direction. It breaks exactly where it matters: `FaceTarget`
overwrites `Facing` toward an interaction target on the tick the LAST step starts, and under the lead commit that
tick is the START of a glide with its whole run still ahead of it. A derived origin would send the body off in the
direction of the booth instead of along the step. So the origin rides the state, and it rides the wire with it
(`WriteMove`, 33 bytes now), which turns out to be the bigger win: an observer's snapshot says where a remote's
body is outright, so the client stopped reconstructing a glide from the tile a remote was last seen on and stopped
paying a step of latency for it.

**Consequences that are the point.** An interaction resolves as the last step STARTS, so `OnInteract` fires a step
sooner and a same-plane target that cannot be reached at all is refused on the tick of the click. A cell handoff
follows the committed tile, so authority crosses at the start of the step over the boundary. A persisted record
names the committed tile. The re-click rule from earlier in 18.1.0 stopped being a special case: a route is always
pathed from `Tile`, which is the tile the step in flight is entering, so the splice and its second route builder
were deleted rather than adapted, and the route cap now counts the steps still to take from the committed tile.

**Consequence that is a trade, not a bug.** A blocker landing on a tile a step has ALREADY committed to does not
rewind that step. The map was asked before the tile flipped and it said yes. Rewinding would mean a tile the
simulation owns can be taken back from it, which is the whole property the reversal exists to establish.

**Determinism is untouched.** Both heads run the same stepper over the same map, the commit is a pure function of
state plus command, and the reconcile replay lands byte-identically from a basis taken a tick either side of the
commit (`A_replay_from_a_basis_taken_one_tick_either_side_lands_on_the_same_state`).

## 5.2 The invariant: a damped chase bounds visual-truth divergence (18.1.0)

The reversal above left the picture a whole step behind the rules, and the question of what the picture should do
about it took three rounds to settle. The first two are recorded here because the third is only defensible against
them.

**Round one, the full-step linear glide.** The body crosses the step at a constant speed over the step's whole
duration. Playtest verdict: it feels like OSRS, a body permanently sliding half a tile behind the truth. That is
the game the ruling explicitly did not want.

**Round two, the glide window.** Cross the whole step in a fixed number of SECONDS and then hold the body on its
tile for the rest of the step, with the seconds as the knob. Playtest verdict: correct as designed, and stuttery
when moving around. It is a metronome, and the flaw is STRUCTURAL rather than a tuning miss. Any window shorter
than the step finishes early and leaves a REST GAP before the next commit, and any window at or above the step is
round one again. There is no value of the knob between the two failures. Measured through the real client wiring
at a 1/6 s tick, `TileStepTicks(4, 2)` and 60 fps, a 0.1 s window spent 157 of a twelve tile run's 220 frames
drawing the body at a bit-identical position, in runs of 14 frames, once per commit: six moving frames then
fourteen dead ones, twelve times over.

**Round three, the damped chase, which is what ships.** The drawn body PURSUES its committed tile rather than
crossing to it on a schedule: every frame it closes the remaining gap by `2^(-dt / halfLife)`, so the gap halves
every half life and never reaches zero while the target keeps moving. `TileChase` is the type, one per body,
stepped with the frame's `dt`.

**Why the shape answers all four things the ruling asked for.** Continuous motion mid route, because there is no
schedule to finish: the body is still closing when the next tile commits, so no frame rests. A crisp attack,
because the velocity is proportional to the gap and the gap is largest immediately after a commit. A smooth settle
onto the final tile, because that same proportionality tapers the arrival instead of cutting it. And it is neither
of the rejected feels: not the constant slide (the speed varies by more than an order of magnitude within a step)
and not a hop (the speed is never zero and never discontinuous).

**No overshoot, structurally.** The gap is SCALED by a factor in (0, 1] rather than the position being lerped
toward the target, so the target is the expression's fixed point: the drawn point converges onto it and cannot
pass it, whatever the frame rate and whatever the target does. First order, one state variable, no velocity
carried, nothing that can ring.

**Frame-rate independent by construction.** The exponent is additive, so two frames of half a `dt` land exactly
where one frame of `dt` does. 30 fps and 144 fps draw the same body at the same wall-clock instants (pinned to
within 1e-4 tiles over a 60 sample route, each rate advancing in its own frames cut short at each sample instant).
A per-frame "close a fixed share of the gap" would be off by more than a tile over the same route, which is the
mistake this form exists to make impossible.

**THE INVARIANT, restated in the shape the chase gives it: steady-state lag plus settle.** While the body is
moving, it lags its committed tile by `speed * halfLife / ln 2` on average. That falls out of integrating the
decaying gap over one step period and is independent of the step SIZE, which is why one number in seconds means
the same thing to a walk and to a run. When the target stops, the body converges onto the tile: 2^-n of the gap
remains after n half lives, so three per cent after five and, once the residual falls under `TileChase.SettleTiles`
(a thousandth of a tile), exactly nothing.

Worked at the default `ChaseHalfLifeSeconds = 0.07` and a 1/6 s tick with `TileStepTicks(4, 2)`:

| | step | speed | steady-state lag | settle to 3% | exactly on the tile |
|---|---|---|---|---|---|
| walk | 0.667 s | 1.5 tiles/s | **0.151 tiles** | 0.35 s | about 0.7 s |
| run | 0.333 s | 3.0 tiles/s | **0.303 tiles** | 0.35 s | about 0.7 s |

Both are inside the 0.5 tiles a full-step linear glide averages, so the chase is a TIGHTER bound than the feel it
replaced as well as a better one. And the settle half matters as much as the lag half for design above it: a
STANDING player is drawn exactly on their tile rather than merely near it, so anything that reads a stationary
player's tile is reading what the viewer sees, exactly.

**State the second term, every time the first one is stated.** The lag above is the half a game controls, and it
is not the whole number for a REMOTE. A remote is drawn off the `InterpolationDelayTicks` delayed timeline, a
whole tick per delay tick and two of them by default: at a 1/6 s tick that is 0.33 s on top, which is MORE than
the chase's own term at run cadence. A boss telegraph built on the chase's lag alone and read against other
players' bodies is built against something narrower than what ships. The fix is to tighten `InterpolationDelayTicks`
alongside the half life, or to size the design against the sum. Section 5.2 is what Grimhollow's combat contract
cites, so the sum is what it states. The LOCAL player has no second term at all any more, which is a change from
the window: it is drawn from a chase whose target is the committed tile itself, with no correction offset folded
in (see below), so its divergence is exactly the lag plus the settle.

**Why the knob is a half life in SECONDS.** Same argument the window's seconds had, and it survives the change of
shape. A run is a shorter step than a walk, so a knob expressed as a share of the step would make the walking
catch-up take twice as long as the running one, and a player reads that as two different games. In seconds the
curve against the wall clock is the same one whatever the body is doing, which is also what makes the invariant
above checkable: the number a designer tunes is the number the bound is stated in.

**Why the default is 0.07 s rather than a sentinel that preserves the old glide.** The window defaulted to "no
window" on the argument that a knob should be invisible until a game reaches for it, and that argument does not
transfer. The window BOUNDED an existing divergence, so the widest bound was the shipped behaviour. The chase
REPLACES the drawing curve, and the curve it replaces is the one the ruling rejected, so a sentinel default would
ship the rejected feel and leave every consumer opted out of the answer. The number is sized against the RUN,
because run cadence is where the metronome was reported: 0.333 s is 4.8 half lives, so the gap is still 3.7 per
cent of its post-commit size when the next tile commits and there is no rest gap to read as a beat. A walking step
is twice that, so a walk arrives and plants inside its step, which is the right difference between the two gaits
rather than an accident. Zero is still available and still means the strictest reading of the invariant: the body
is on its committed tile the instant the tile commits.

**Where the state lives, and why the presenter went back to being pure.** A chase is STATEFUL, and the presenter
was not: it is a function from a tile point to a world position, callable from a render thread, with no device and
no history. So the chase lives per body on the client, exactly where each path already keeps its per-body
presentation state: the local player's beside the prediction layer, each remote's beside that remote's
interpolation entry. Both are constructed from the one `TileWorldClientConfig.ChaseHalfLifeSeconds`, so the local
body and every remote share one curve by construction rather than by two call sites agreeing. `TilePresenter`
gained `PoseAt`, which is the mapping a chased point is drawn through, and lost `LocalPose` and its window: a
head draws through `TileWorldClient.LocalPose` and `TryGetRemotePose`, and a presenter replaced when the document
loads can no longer silently lose the feel the way it could lose a window.

**Discontinuities RESET the chase rather than being pursued.** A teleport (an authoritative epoch advance), a hard
snap, the prediction seed, and a remote first seen or seen again more than one Chebyshev step from where it was
all place the body outright on the frame the snapshot lands. The remote test is `TileMoveState.IsStepOrigin`, the
same rule the wire decoder and `SetPlayerState` are held to, so a plane change and a reappearance across the map
are covered by the same predicate as a teleport. Chasing across one of those would slide the avatar over every
tile in the gap, and it would do it while the head's camera had already been warped by the teleport event.

**The composition with the prediction layer's correction, which is the one subtle thing here.** The local body's
chase target is the BARE committed tile. `ClientPrediction.RenderOffset` is deliberately not in it, and that is
the third of three candidate shapes rather than an oversight:

1. `chase(tile) + offset`. The offset jumps the whole correction into the drawn position in a single frame and
   then unwinds it. A pop followed by a reversal: the rubber band, exactly.
2. `chase(tile + offset)`. Looks like the careful fix, on the reasoning that at a rebase the tile's jump and the
   offset's jump cancel. They only cancel when the rebase moves the tile and the position by the same amount, and
   on a LATTICE it does not: the offset takes up the POSITION delta while the target moves by the TILE delta. The
   case that exposes it is the ordinary sub-tile correction, where the authority agrees about which tile the
   player owns and disagrees only about how far through the step the body is. The tile does not move, so the
   target should not move, but the offset does, and the body would be pushed a fraction of a tile PAST its
   committed tile, in the opposite direction to the correction, and then brought back.
3. `chase(tile)`. Neither failure. The target moves only when the tile does, and the chase smooths that by
   construction, so there is nothing to pop and no second decaying term to reverse.

Nothing is lost by dropping the offset, and that is the load-bearing claim. The offset exists to smooth a
correction, and the position it smooths is the step-fraction glide between `StepFrom` and `Tile`, which is the
curve the chase replaced and which nothing draws any more. The chase IS the smoother now, and it smooths the only
quantity being drawn. A correction big enough to matter changes the committed tile, which the chase then smooths
at its own half life. A correction big enough to CUT is a hard snap, which resets the chase outright. What is left
in between is sub-tile, and sub-tile is precisely the case where the right answer is to draw nothing at all
(`A_sub_tile_correction_glides_without_moving_the_drawn_body_at_all` pins it as 90 bit-identical frames). The
vertical is untouched by all of this and still rides the prediction layer's eased plane: a step never changes
plane, so the only thing that moves it is a teleport, which cuts on both axes together.

**`StepFrom` stays.** It is still on the state and still on the wire, because the simulator and the reconcile both
need it and because it is what makes a remote's snapshot say where the body is going. It is simply not what the
body is DRAWN between any more.

**Determinism is untouched, structurally rather than by care.** Presentation reads state and writes none, and no
part of the simulation path reads the half life, so two clients drawing at different half lives still replay
byte-identically. It remains the one movement number outside the client-server determinism contract.

## 6. Reach and the action seam

`TileReach` is a pure function over the collision map and a footprint. The reach set of an N x M footprint on a
plane is every tile cardinally adjacent to a footprint tile for which `CanStep` FROM the footprint tile OUT onto
that tile is legal, which asks three things: no wall on the footprint tile's edge, the candidate is not blocked,
and no mirrored wall on the candidate's own edge facing back. That one rule encodes OSRS's behaviour: a wall
between you and the booth denies reach, a diagonal never counts, a candidate nobody could stand on is not offered,
and a 2x2 object has up to eight reach tiles minus the denied ones. `TryNearest` scores them by the LENGTH of the
path that actually reaches each one, one `FindPath` per candidate because the pathfinder does not expose its
distance field, and breaks a tie by the same scan order, so both heads choose the same tile.

This was written INWARD in an earlier draft (`CanStep` from the candidate into the footprint tile) and inward is
wrong twice over, because a step never inspects the BLOCKED flag of the tile it leaves. Starting on the candidate
puts the blocked test on the TARGET, and every real target is blocked, being a booth or a rock, so the reach set of
anything worth clicking comes out empty. It also never tests the candidate for being blocked at all, so a solid
tile beside an unblocked target (a doorway, a ladder) is offered as somewhere to stand. Outward asks the same two
wall questions, the same pair either way round, and puts the blocked test on the candidate where reach needs it,
which is the form that stays correct when the target itself is solid. `TileReach`'s class doc carries the same
reasoning, so the direction is not flipped back later.

Server side, `TileActionQueue` holds at most one pending action per player: `(target, kind, issuedTick)`. On each
tick after movement, a pending action whose player is COMMITTED to a reach tile of the target, on its plane, is
validated and raised through `TileWorldServer.OnInteract(playerNetId, target)`, then cleared. Under 5.1's lead
commit that is the tick the walk's LAST step starts, so the handler runs while the avatar is still drawn walking
that tile in. The facing is already correct by then, written by the simulator on the same tick the step commits, so
the server never owns the turn. Reissuing a command
(another click) replaces the pending action, OSRS style, and an applied `WalkTo` CLEARS it, because the simulator
clears the state's own pending target on a walk and the queue and the state are two records of one intent. In SP2 the only consumer of `OnInteract` is a log line,
and the Grimhollow client shows a localized "nothing interesting happens". When `TileReach` returns no reachable
tile, the server sends a `CannotReach` game message, and the client, which has the same map, pre-checks on click
and shows it immediately without waiting.

## 7. Server

`TileWorldServer` is built on `ShardHost` and `CellSim` with **tile coordinates as the plane**: the host is
constructed with `cellSize = TileRegion.Size` (64) and every insert, query and `CellCoord.FromWorld` call takes
`(tileX, tileZ)` as the floats. A cell is then exactly a `RegionCoord`, floor division on integers with no negated-z
off-by-one at boundaries (`TileWorldSpace` negates z for rendering only and is never consulted here). Interest radius
is 15 tiles (OSRS's view distance) and the overlap margin 16, satisfying `InterestRadius <= OverlapMargin`. Planes do
not shard: a cell holds every plane of its region and interest is filtered by plane in the snapshot.

Per cell: the ECS world with `TileMoveState` (replicated, extension id assigned in `TileProtocol`), `TileIdentity`
(display name, replicated), a `RemoteCommandQueue<TileCommand>` per resident player, and the cell's own replicator
and interest grid serving AoI snapshots. The tick order inside a cell is: drain one command per player, step every
player through `TileMoveSimulator`, resolve the action queue, capture and replicate. Ghosting and exactly-once
handoff across a region boundary are `Sharding`'s as they stand, triggered when a step lands on a tile in another
region. `OnBeforeTick`, `OnGameMessage` and `OnInteract` are the game seams, drain (`BeginDrain(notice, grace)`) and
the ban store are the operational ones, copied in shape from `ShardedWorldServer`.

Known and accepted cost: `ShardedWorldServer`'s connection lifecycle, residency and rate-limiting plumbing is
re-implemented here for tiles rather than extracted into a generic core. Extracting it from Ruinborne's live server
is a refactor of a shipping MMO stack and is not this sub-project. It is filed as a follow-up the moment the two
servers are seen to converge.

## 8. Persistence

`WorldPersistence`'s behaviour is what a tile server needs and is too subtle to duplicate: save every N seconds,
save dirty records on a pass, quarantine a record that fails validation instead of overwriting it, do not persist
guests, resume hints for a player who reconnects mid-save. The core becomes generic over the state and record types
and lives beside `IWorldStore` (the plan decides the exact package, `WorldStore` is the natural home since it is the
seam both bindings share), and `NetWorld.WorldPersistence` keeps its name, its public surface, its config type and
its tests as the float binding. `TilePlayerRecord` is `Version`, `TileX`, `TileZ`, `Plane`, `Facing` and the opaque
`Game` blob, tolerant JSON through a source-generated context like `PlayerRecord`. The key stays
`player:{accountId}` where the account id is the verified token subject. `SqliteWorldStore` locally and in tests,
`SqlServerWorldStore` hosted, one env var picks, fail-closed when the hosted store is set without the token secret,
the Ruinborne rule.

## 9. Client

`TileWorldClient`: connects with the signed token through the gate, owns `ClientPrediction<TileMoveState,
TileCommand>` over the same `TileMoveSimulator` built from the same world files, sends one `TileCommand` per tick
from its own `FixedTickHost` phase-offset from the server's, applies snapshots through `ClientReplicationView` for
remotes and `Reconcile` for the local player, and raises `CannotReach`, `RefusedAtDoor(reason)` and `Disconnected`.
`TilePresenter` is the bridge to the view: from a `TileMoveState` plus the rendered step fraction it produces a world
position and yaw through `TileWorldSpace`, for the local player from `ClientPrediction.RenderedState` and for remotes
from the view's samples, gliding a remote across a step over that step's tick count. Nothing in the client touches
the GPU, so the whole of it is headless-testable, and the Grimhollow head only feeds it clicks and draws what the
presenter says.

## 10. Protocol and the connect gate

`TileProtocol` defines the wire: the connect payload (token bytes, protocol version, world hash), `TileCommand`
framing with the sequence number, the snapshot frame (`[localNetId][ackSeq][replication snapshot]`, the shape
`MoveProtocol.EncodeSnapshotFrame` uses), the game-message envelope (`kind` plus payload, length-capped), and the
extension component ids. It shares no bytes with `MoveProtocol` and is never demuxed on length. `ConnectionGate` in
`Netcode` refuses at the door with a reason token the client localizes: wrong protocol version, wrong world hash,
bad or expired token, banned.

## 11. Grimhollow heads (R2)

`Grimhollow.Shared`: port, protocol version, `TickSeconds = 0.25f`, `RunStepTicks = 2`, `WalkStepTicks = 4`.
`Grimhollow.Server`: the Ruinborne host shape, in order: session log, load the world headless, bake collision, shard
config, store by env var, token auth and gate, `TileWorldServer`, persistence, tick loop, drain on SIGINT and
SIGTERM, heartbeat, into the existing Dockerfile. `Grimhollow.Auth`: Ruinborne's two-file Discord exchange over
`IIdentityValidator`, plus a minimal accounts store (subject, display name, whitelisted, banned) on the same Sqlite
or SqlServer choice. Client: a login screen (Discord sign-in through `IdentitySession`, every string through the
catalog), the fly rig replaced by a player with an OSRS-style orbit camera, left click on the ground through
`TileRaycast.Pick` sends `WalkTo`, on an `Interactive` archetype sends `Interact`, a run toggle, remote players as a
placeholder kit piece with name plates, and the cannot-reach line. Presence ("in Hollowmere") is the existing `KhaozEngine.Social`
`SocialPresenceController` over `Social.Discord`'s `DiscordSocialProvider`, wired in R2 with no new engine code. Dev
loop: the fly camera stays reachable behind a dev toggle so world authoring does not lose its viewer.

## 12. Failure handling

- Unreachable goal: walk to the nearest reachable tile (the pathfinder's rule), never refuse a click.
- Click off the loaded regions or off the map: dropped client-side with a log line, never sent.
- Dynamic blocker mid-route: re-path once, then stand. A head that saw a different blocker snaps on reconcile.
- Malformed command (plane out of range, non-finite, unknown kind, goal outside the search radius): dropped and
  counted, the existing `RateLimiter` bounds the rate, repeated abuse is the ban store's business.
- Region handoff mid-step: the step completes in the new cell, the route survives (tile coordinates are global).
- Persistence: a record that fails validation is quarantined under the quarantine prefix, the player spawns at the
  world's spawn marker, and `OnRecordQuarantined` fires for the operator.
- Refused at the door: the client shows the localized reason and does not retry on its own.
- Server overload: `FixedTickHost` sheds backlog past eight ticks, and the tick is 250 ms, so shedding is visible
  in the heartbeat long before players feel it.

## 13. Test plan

Headless, in `KhaozEngine.TileWorld.Netcode.Tests` (a new per-area test project referencing only what it uses):

- Simulator: same inputs, same route, byte-identical state after N ticks on two instances. Run steps every 2 ticks
  and walk every 4, diagonals cost the same, a corner is never cut (the `CanStep` rule observed through movement).
- Reach: the reach sets of 1x1, 2x1 and 2x2 footprints with and without walls, `Nearest` tie-breaking matches scan
  order, a fully walled object has no reach tile.
- Prediction: a client and a server over an in-process transport walking a route, with the client's predict phase
  offset from the server tick (the loopback lesson from the Ruinborne harness). Zero corrections on a clean map.
  Then a server map with one extra blocker: exactly one snap, then agreement.
- Action queue: arrival on a reach tile raises `OnInteract` once, a second click replaces the pending action, a
  walled target yields `CannotReach`.
- Sharding: two cells, a player walking across the region boundary, the net id survives, the route survives, a
  watcher in the other cell sees the ghost before and the resident after.
- Persistence: round trip through a temp `SqliteWorldStore`, a corrupt record is quarantined and the player spawns
  at the marker, guests are not written.
- Protocol and gate: encode and decode every message, fuzz the decoders with truncated frames, and the four refusal
  reasons each refuse with the right token.
- The generic persistence core: `NetWorld`'s existing `WorldPersistence` tests pass unchanged against the binding.

Grimhollow: a capture of the player standing in Hollowmere through `TileWorldSnapshot`, and a windowed walk for the
user (click, run toggle, cannot-reach on a walled booth).

## 14. Release split

R1, engine: `TileWorld.Netcode` complete with its tests, `ConnectionGate` promoted, the persistence core made
generic, one minor version. R2, Grimhollow: the three projects and the client player, one game version. R1 has no
consumer until R2 and that is accepted: the package's consumer-visible value is proven by the loopback tests, which
exercise both heads end to end in one process.

## 15. Deferred, with the reason

- Real actions (bank, stairs as a plane change), NPCs, chat, combat: sub-project 3 onward. SP2 leaves `OnInteract`,
  the reach rules and the action queue as the seam they plug into.
- A generic server core extracted from `ShardedWorldServer` and `TileWorldServer`: after both exist and the
  duplication is visible rather than predicted.
- Multi-process shard deployment: the cell is the unit already, one process runs all of them until a world needs
  more.
- Pooled pathfinder scratch (#669): when a server paths enough agents for it to show in a profile.
- Character models: the placeholder kit piece until a character pipeline exists.
