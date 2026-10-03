# NPC movement fixes: swimming routes, route pace and low lips

Status: implemented and verified through the [implementation plan](../superpowers/plans/2026-10-03-npc-movement-fixes.md)
on `feature/grimhollow-npc-movement`, with rulings M1 to M21 recorded in the plan's Outcome. Swimming routes (#1256),
route carry (#1257) and low prop support (#1253) are staged for 20.20.0. Smooth-slope bake edges (#1265) are deferred
by ruling M20. No release or tag is claimed.

Consumer: Grimhollow P5 creature host on engine 20.18.0, branch `feature/p5-lane-n`.
Engine issues: [#1256](https://github.com/APKiwiOrg/KhaozEngine/issues/1256) (swimming body cannot be steered),
[#1257](https://github.com/APKiwiOrg/KhaozEngine/issues/1257) (route pace loses a quarter tick per cell) and
[#1253](https://github.com/APKiwiOrg/KhaozEngine/issues/1253) (bake refuses a 2.5 cm lip, `confidence/lead`).

Base: engine main `fefb78876`. Newest tag `v20.19.0`. Main stages `20.20.0` untagged, carrying the
[nav profile bake](NAV-PROFILE-BAKE-DESIGN-2026-10-03.md) and the route-free `DirectMoveToRange`. This round rides that
staged version, and per ruling M1 the `20.20.0` tag waits for it. Line citations are at main unless marked v20.18.0.

Prerequisite reading: the [round 2 design](CONTINUOUS-HOST-ROUND-2-DESIGN-2026-10-02.md) section 6, and Plan D's
Outcome rulings in [round 2 D](../superpowers/plans/2026-10-02-round2-d-movement-drivers.md), especially D0.2
(`Suspended` before `InRange` for airborne or committed automation) and D1.1 (follower accept radius clamped to
1e-5 m so a broad tolerance cannot consume a mandatory turn).

## Problem and measured facts

### Swimming bodies (#1256)

Grimhollow ducks run on a baked ground profile whose area covers the river. The river is deeper than a duck's swim
threshold (radius 0.2 m, half height 0.25 m), so every duck swims on every tick and moved 0 m over 1,800 ticks.

- `MoveToRange.Tick` holds any body that is not grounded as `Suspended` (`MoveToRange.cs:49`).
  `DirectMoveToRange.Tick` does the same (`DirectMoveToRange.cs:42`). `SwimStep` returns `Grounded = false` and
  `Swimming = true` (`CharacterMovement.Fluid.cs:123-126`).
- Step admission requires a grounded, non-swimming prediction (`MoveToRange.Approach.cs:24-26`,
  `DirectMoveToRange.cs:97-98`).
- `BuildProfile` proves nodes and edges with a dry walk on the bed (`PhysicsNavBake.Profiles.cs:17-75`,
  `GroundTraversalProbe.cs:28`). A water column becomes a node at bed height only when the bed is walkable dry for the
  capsule. Steep banks cut the river off.
- `GroundNavigation.Resolve` accepts feet within `max(StepHeight, 1 mm)` of a cell height (`GroundNavigation.cs:77-96`).
  A floating duck's feet sit at the surface minus `0.6 x 0.5 m`, so in deep water the planner returns `Unreachable`
  and `AllowsSegment` is false.
- `TravelBound` uses walk or run speed (`RangeApproachCore.cs:23-32`). The default `SwimSpeed` of 2.5 m/s exceeds a
  2 m/s walk, so a swim tick overshoots its waypoint and, with the 1e-5 m accept radius, the follower never advances.
- `SwimStep` does no physics sweep, only the XZ clamp and a terrain floor (`CharacterMovement.Fluid.cs:59-99`). A
  swimmer does not collide with props. The floor reads the context's ground height function
  (`CharacterMovement.Fluid.cs:98`), never physics. Entering water ends an active commitment as `Aborted` with
  `EnteredWater`, so a swimming body never carries an active commitment.
- Default fractions: `SwimEnterDepthFraction` 0.65, `SwimExitDepthFraction` 0.55,
  `SwimSurfaceSubmersionFraction` 0.6 (`MoveTuning.cs:31-34`). A body starts swimming at chest depth and floats with
  its feet 0.6 body heights below the surface.

### Route pace (#1257)

A creature on a baked route at 2 m/s and 30 Hz covers 1.875 m/s, measured by Grimhollow at 93.9 percent of walk
speed. `MoveToRange.Tick` caps the command at the active waypoint (`MoveToRange.cs:66-67`, `BoundedDirection` at
`RangeApproachCore.cs:34-40`) and discards the leftover travel. With the 1e-5 m accept radius every 0.25 m cell centre
is landed on exactly, so one tick in four ends short: 0.0667, 0.0667, 0.0667, then 0.05 m. The follower advances
only by proximity (`PathFollower.Region.cs:129-146`). The route builder drops the start cell when the first step is a
grid step (`GridPathPlanner.Traversal.cs:62-63`), so waypoint 0 is the first cell after the body's own.

### Low lip (#1253)

Grimhollow's creature bake refuses the edge from the bank onto the Hollowmere bridge deck at both ends for a 0.3 m
radius, 0.75 m half height capsule. The bank is at 0 m and the deck at 0.025 m. Not reproduced in isolation. The lead
is `GroundTraversalProbe`'s 1 mm straight-line arrival tolerance (`GroundTraversalProbe.cs:10`, `:39`, `:54`,
`:62-66`) against the collide and slide `SkinWidth` of 0.01 m (`CharacterMovement.Collision.cs:22`). A cell centre
0.125 m from the deck edge puts the capsule's bottom cap a few millimetres above the deck corner, inside the skin.

## Goals

1. An aquatic profile routes a swimming body through water that the movement medium reports, independent of the bed
   shape, with nodes and edges proven at the height the body actually floats.
2. `MoveToRange` steers a swimming body on such a profile, opt-in per driver, with the same reach, stop ring, guard
   and status rules as a grounded body.
3. Airborne bodies and committed bodies stay `Suspended` exactly as today, including D0.2's precedence over
   `InRange`.
4. `MoveToRange` holds full pace along straight runs of a route, opt-in. A mandatory turn still stops at the corner.
5. A minimal engine fixture settles #1253, and the bake then accepts a lip the live core can mount.
6. Every new behaviour is opt-in. Capture output, runtime behaviour and public APIs are unchanged by default. Per
   ruling M6 the unreleased KENB v1 layout is not part of that promise.

## Non-goals

- Diving, underwater routes, swim at any height other than the resting float line.
- Swim steering in `DirectMoveToRange` (ruling M3).
- Collision for swimming bodies in the movement core. The graph is the swimmer's obstacle guard.
- Water that the medium does not report, or more than one water surface per column.
- Wire, `NetWorld` or `MoveState` changes. `MoveState.Swimming` is already on the wire.
- Any Grimhollow file change, version bump or tag.

## Decisions

Scores are design judgments from 1 to 10, higher is better.

### D1. How a swimming body meets the route graph (#1256)

**A. Bed projection** (the investigator's proposal). A driver flag lets a swimming body be steered, and the planner,
follower and segment guard receive its feet projected down to the highest graph surface at or below them. The bake
and format do not change.

Pros:

- No bake or format change. One driver option and about 60 to 90 production lines.
- Reuses the existing dry proofs unchanged.

Cons:

- A route exists only where the bed is a connected dry walk for the capsule. Steep or rocky channels and water with
  no captured bed have no route. Whether Hollowmere's river bed is connected for a duck capsule is unchecked.
- Arrival is judged at bed height. `NavGoalRegion.Contains` sees projected feet, so the follower can report
  `Arrived` while the floating body is out of range. `MoveToRange` then holds `Following` with zero input
  (`MoveToRange.cs:58`) and the body never arrives.
- Surface obstacles are invisible. A deck low over the water is above the bed's headroom check, so the bed graph
  routes under it. The swimmer has no collision, so it passes through the deck.
- Two definitions of where a body is, the real feet and the projected feet, leak into every guard and test.

**B. Surface water layer in the bake.** An aquatic profile replaces each swim-deep surface with a float surface at
the height the body actually rests while swimming. Its nodes are proven by a swim hold and its edges by swim steps
through the live medium, with a static clearance check, because the core does not collide a swimmer.

Pros:

- Nodes, edges, arrival and the segment guard all use the body's real feet. No projection.
- Independent of the bed shape. Water over a steep channel or with no captured bed still routes.
- Surface obstacles are refused at bake time by the clearance check, so a low deck blocks the float layer.
- Shore transitions are proven through the same mixed walk and swim steps the live core takes.
- One rule for any capsule and any game whose medium reports water.

Cons:

- Changes the unreleased KENB v1 payload and identity, so the `20.20.0` tag waits for it (ruling M1).
- About 400 production lines across capture, profile build, a swim probe, format and drivers.
- A game opts in to capture-time water sampling (ruling M2).

| Criterion | A. Bed projection | B. Surface layer |
| --- | ---: | ---: |
| Correctness of arrival and collision | 3 | 8 |
| Generality for a second game | 4 | 8 |
| Bake format and API surface | 9 | 5 |
| Implementation size and risk | 7 | 4 |
| Test clarity | 5 | 8 |
| **Total** | **28** | **33** |

Select B. A answers the immediate duck symptom only where the bed happens to be walkable, and its arrival and deck
failures would surface later as silent livelocks and swimmers passing through props. B is larger but every rule it
adds reads the body's real position. Ruling M1 holds the `20.20.0` tag for it. B scores 8 rather than 10 on
correctness because the core still does not collide a swimmer at runtime, and a penetration query reports only its
deepest contact (ruling M5).

Rejected alternative: A, for the arrival livelock, the invisible surface obstacles and the unchecked bed
connectivity.

### D2. Where the swim opt-in lives

| Option | Opt-in clarity | Identity safety | API size | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Profile flag in the bake plus a driver option | 9 | 10 | 7 | 26 |
| B. Driver option only, profile always carries float nodes | 5 | 6 | 9 | 20 |
| C. Profile flag only, drivers steer swimmers on any aquatic profile | 7 | 10 | 9 | 26 |

Select A. Float nodes change the graph, so the bake identity must say whether a profile has them, which rules out B.
C ties on score but makes a driver behave differently because of how a file was baked, while the round 2 contract
keeps steering policy at the driver. A keeps both explicit, and a driver that asks to steer swimmers on a profile
without float nodes is refused at construction. The profile flag is `Aquatic`, carried by a named options property
(ruling M10).

### D3. Capture-time water sampling (ruling M2)

| Option | Defaults unchanged | Misconfiguration caught | Surface | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Capture samples the medium whenever the context carries one | 5 | 8 | 10 | 23 |
| B. An explicit `PhysicsNavBakeOptions.SampleWater` init property, in the identity | 10 | 10 | 7 | 27 |
| C. A separate game water delegate | 9 | 5 | 5 | 19 |

Select B. A changes capture output, medium calls and bake bytes for every game that already bakes with a medium
context, which breaks the opt-in rule. `SampleWater` is an init property, default false, because the positional
record shipped in v20.19.0. It joins the bake identity (ruling M7), so a bake with water sampled differently is
refused as stale. `Capture` refuses `SampleWater` without a medium. An aquatic profile is refused when its capture
has no sampled water. A medium context without the opt-in captures byte-identical output to today. C duplicates the
medium, which is already the single both-heads source of water.

### D4. Carrying travel past a waypoint (#1257)

| Option | Turn safety | Pace | Surface | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Steer at the end of a straight run, follower consumes passed collinear waypoints | 10 | 9 | 8 | 27 |
| B. Split the leftover travel across the next segment in the driver | 6 | 10 | 7 | 23 |
| C. Widen the accept radius to one tick of travel | 3 | 8 | 10 | 21 |

Select A. On a straight run, aiming at the run end and aiming at the next waypoint are the same direction, so the
body travels the full bound and passes intermediate cell centres. The follower must then consume a waypoint the body
passed rather than landed on. Restricting both rules to collinear waypoints keeps D1.1's guarantee: a mandatory turn
is never consumed early. B bends a single tick's command around a corner, which the round 2 design forbids. C is the
broad tolerance D1.1 removed.

### D5. Where a #1253 fix lands (ruling M4)

| Option | Blast radius | Fidelity to runtime | Total |
| --- | ---: | ---: | ---: |
| A. Movement's `GroundTraversalProbe` arrival judgment, when the live core mounts the lip | 9 | 9 | 18 |
| B. Locomotion's step-up rules, when the live core cannot mount the lip | 4 | 9 | 13 |

The fixture decides. If the live core mounts the lip and the bake refuses it, A applies. If the live core cannot
mount it, the bake is telling the truth and the fault is in Locomotion. Ruling M4 authorizes a targeted step-up fix in
this round for that case, because the same core moves players and a lip that stops a capsule is a player-facing
kernel defect. B is guarded by the red fixture, the whole Locomotion suite, the whole Movement suite and the Server
NetWorld suite.

### D6. Swim steering in `DirectMoveToRange` (ruling M3)

Excluded. The direct driver has no graph guard, and a swimmer has no prop collision, so a direct swim approach would
pass through any prop at the waterline. Its documented refusal of steps that start swimming stays, and the living
docs state this limit and its reason.

## 1. Package and dependency edges

No project reference changes. Movement keeps exactly Locomotion, Navigation and Physics. The swim clearance check
uses `IPhysicsWorld.ComputePenetration`, already in Physics. Navigation gains a route geometry query and a follower
option with no new reference. No wire, `NetWorld` or persisted payload change outside the unreleased KENB file.
A Locomotion change happens only through D5 B.

## 2. Surface water layer (#1256)

### Capture

`PhysicsNavBakeOptions.SampleWater` defaults to false and capture is then exactly today's. When it is true,
`PhysicsNavBake.Capture` throws `ArgumentException` if `context.Medium` is null. Otherwise it samples the medium once
per in-bounds column after the physics probe. The sample feet height `y0` is the lowest captured surface height, or
`ProbeHeight - ProbeRange` when the column has no surface. A sample with `InWater` true and a finite `WaterSurfaceY`
above `y0` records one water entry: the cell, the surface Y and the area bits the classifier returns for
`(x, WaterSurfaceY, z)`. The classifier receives the water surface point for a water entry.

Limits: one water surface per column, found from the lowest surface. A pool on a deck above dry ground is not seen.
The medium must be the same provider the runtime context uses, and the game's source digests must cover it, which
the nav bake design already requires.

The context's ground height function must report, in every water column, a height at or below the captured bed. The
swim step floors the capsule at `groundHeight(x, z) + CapsuleHalfHeight` (`CharacterMovement.Fluid.cs:98`), so a
ground height above the float line lifts a swimmer off it and the float hold fails. An analytic terrain that models
the bed satisfies this. A flat analytic ground at the water surface does not.

### Aquatic columns

A profile baked with `Aquatic` true reads a derived column view instead of the captured columns. For each column with
a water entry at height `W`, with body height `H = 2 x CapsuleHalfHeight`:

1. Let `s` be the highest captured surface below `W`, if any, and `u` the lowest captured surface at or above `W`, if
   any.
2. The column is swim-deep when there is no `s`, or `(W - s.Height) / H >= SwimEnterDepthFraction`, and the float
   height `f = W - SwimSurfaceSubmersionFraction x H` lies above `s.Height` and below `W` (ruling M17). A zero
   submersion fraction puts `f` at `W`, where it would share its height with a kept surface at `W` and claim that dry
   node as a float, so no float is emitted.
3. A swim-deep column replaces every captured surface below `W` with one float surface at `f`, flagged as a float.
   Its areas are the water entry's areas. Its headroom is `max(0, s.Headroom - (f - s.Height))` when `s` exists. A
   submerged overhang between `s` and `f` therefore yields zero, which the candidate filter refuses. When `s` does
   not exist, the headroom is `u.Height - f` when `u` exists, an upper bound because a deck's underside is not
   captured, and positive infinity otherwise. The float hold's clearance check refuses a capsule that meets the deck's
   underside. Surfaces at or above `W` are kept unchanged.
4. Every other column is unchanged. A column between the exit and enter fractions keeps its bed, where a wading body
   stands and a swimming body floats within `0.05 x H` of it at default tuning.

An aquatic profile requires `SwimExitDepthFraction <= SwimSurfaceSubmersionFraction <= SwimEnterDepthFraction`.
Otherwise a body at rest on the float line would leave swimming at once, or float below the bed it entered from.
`BuildProfile` throws `ArgumentException` for a tuning outside that order.

All arithmetic is IEEE single precision in this order, so a load derives the same bits as a fresh build. The
footprint, the candidate columns and `Resolve` all read the derived view, so the existing guards apply unchanged to
float surfaces. A ground profile reads the captured columns exactly as today.

The derived view keeps each surface's float flag. After layering, `BuildProfile` treats a node as a float node when
its column's derived surface with a height bit-equal to the node's layer height is flagged as a float. Layering
copies candidate heights unchanged, so the lookup is exact.

Shore continuity follows from the tuning. At the enter threshold the bed sits `0.65 x H` below the surface and the
float line `0.6 x H`, so on a gently sloping bed the layered bake joins float cells to wading cells within the step
height. Cell sampling on a steep bed can widen that gap. A bank more than one step above the float line is a drop. A
body cannot step off it into the water, which matches the runtime rule that an airborne step is refused.

### Proofs

A swim-deep candidate is held by a swim hold. Every edge or Stair link with a float endpoint is proven by a new
medium probe. Edges between two non-float nodes keep the existing dry proof, so an aquatic profile's dry graph equals
the ground profile's dry graph over the same cells.

The medium probe runs through the bake's live context, with its medium, not `DryContext`. It uses the profile tuning
with unit walk and run pace and no air momentum, as the dry probe does.

- A float start is `Swimming = true`, `Grounded = false`, at `f + CapsuleHalfHeight`. Any other start is grounded and
  not swimming, as today.
- The first slice holds with zero input and must stay in the start's mode and near the start. A swimming hold slice
  also passes the clearance check, so the float node itself is clear.
- Each later slice requests `BoundedDirection(target - feet, bound)`, where `bound` is the smaller of the swim-aware
  travel bound of section 3 for this slice with `run` false and the capsule radius. The radius cap keeps consecutive
  clearance samples overlapping. A slice whose bound is zero fails the proof.
- A slice is admitted when the result is finite, the footprint accepts its feet, and it is either grounded and not
  swimming or swimming. A swimming slice must also pass the clearance check.
- A grounded slice arrives under the shared ground arrival rule that Task 4 of the plan owns in
  `GroundTraversalProbe`, today a straight-line distance within `ArrivalTolerance` (ruling M8). A swimming slice
  arrives when its feet are within `ArrivalTolerance` horizontally and within `max(StepHeight, ArrivalTolerance)`
  vertically, the same band `Resolve` accepts at runtime. Buoyancy, not geometry, sets a swimmer's height.

Clearance check. The core does not sweep a swimmer, so each swimming slice asks
`(MovementQueries ?? Physics).ComputePenetration` for the profile capsule at its pose. It passes when there is no
overlap, or when the minimum translation points up within the walkable slope, `mtv.Y >= |mtv| x cos(MaxSlopeRadians)`,
and `|mtv| <= StepHeight`. That admits a float capsule grazing the bed near the shore and refuses a deck above, a
post beside and a steep bank wall. A context without physics passes.

Edge budget (ruling M17). Every proof shares the capture's `MaxEdgeProbeSteps`, and an edge that runs past it is
refused, never partly accepted. A bank edge between a wading node and a float node walks at unit pace slowed by the
wade ramp and the medium's zone scale, then swims. It needs about
`wading length / (dt x walk speed x WadeMinSpeedScale x zone scale)` steps while it wades, plus
`swimming length / min(CapsuleRadius, dt x SwimSpeed x zone scale)` steps once it swims, plus a few steps of final
approach. The swim term needs `SwimSpeed x zone scale` above zero, and a zero swim pace refuses every float edge. At
the default 64 steps of 1/30 s and a zone scale of 1 that covers banks between 0.25 m cells. Larger cells or slow
zones must raise `MaxEdgeProbeSteps`, or the float layer is cut off from the land. The budget applies to each directed
edge, so in a slow zone a shoreward swim can fit while the wade out does not, which leaves a one-way exit from the
water rather than a route into it. Dry bank edges on a smooth sloped physics shoreline are also subject to
https://github.com/APKiwiOrg/KhaozEngine/issues/1265, deferred by ruling M20, so the facts use 2 cm terraces.

Deepest-contact limit (ruling M5). The query reports the deepest contact only, so a shallow side contact under a
deeper bed contact can pass a slice. Deep water has no bed contact, so the limit applies only within about one step
of the bed, where a duck route may clip a bank. The limit is accepted and documented in the Movement README. Root
files the follow-up issue "Swim traversal proof: capsule sweep per slice instead of deepest-contact penetration"
with `kind/backlog` and `confidence/authored`. Creature bakes run at server startup in about 6.4 s today, and a sweep
per slice multiplies that cost.

### Bake format, still version 1

KENB v1 is unreleased, so the layout changes in place and `FormatVersion` stays 1. Ruling M1 holds the `20.20.0` tag
until these land. Ruling M6 accepts that land-only bakes also change bytes, because the options gain a field and the
payload gains a water count, and that the golden fingerprint is re-recorded. No consumer holds a 20.20.0 bake yet.

- Identity options: `SampleWater` is encoded as one `uint8`, 0 or 1, after `MaxEdgeProbeSteps`. The option count the
  identity covers becomes 14. Any other byte is non-canonical and `Corrupt`. `OptionsChanged` names `SampleWater`.
- Identity profiles: each profile entry gains one `uint8` aquatic flag, 0 or 1, after `Excluded` and before the
  tuning. Any other value is `Corrupt`. `ProfilesChanged` names `Aquatic` as the first differing field when only the
  flag differs.
- Payload capture section: after the surfaces, a water entry count `W` as `int32`, then `W` entries in ascending cell
  order, each the cell index as `int32`, the surface Y as float bits and the areas as `uint32`. With `SampleWater`
  false, `W` must be 0.
- Reader invariants: `0 <= W <= C`, strictly ascending cells below `C`, each cell centre inside the bounds, finite
  surface Y above the column's lowest surface, or above `ProbeHeight - ProbeRange` for an empty column.
- Payload bound: the capture term gains `4 + 12 x C`.
- An aquatic profile's layers, exits and links are stored exactly as a ground profile's. Load rebuilds its derived
  column view from the captured columns, the water entries and the stored tuning before it builds the footprint.
- Ground profiles of one set still share one column instance. Each aquatic profile owns its derived view.
- `MoveTuning` stays covered as today. The three swim fractions and `SwimSpeed` are already among the 27 retained
  fields.

### Runtime profile

`GroundNavigation.Aquatic` reports the flag. An aquatic profile's `ValidateTuning` also requires the runtime
`SwimEnterDepthFraction`, `SwimExitDepthFraction` and `SwimSurfaceSubmersionFraction` to equal the baked values, since
they place float nodes. `Resolve`, `AllowsSegment`, `Planner` and `NavSpace.LayerAt` need no change, because a
floating body's real feet sit on a float node.

## 3. Swim steering in `MoveToRange`

`RouteApproachOptions.SteerWhileSwimming`, default false. With it false, every behaviour is today's. With it true:

1. Validation and reach evaluation run first, as today.
2. A body is `Suspended` when its commitment is active, or when it is neither grounded nor swimming. An airborne,
   non-swimming body and a committed body therefore return `Suspended` before `InRange`, as D0.2 rules.
3. A swimming body is also `Suspended`, before `InRange`, while it is still settling: when the medium sampled at its
   feet is not in water, or its feet are farther than `max(StepHeight, ArrivalTolerance)` from its float line
   `WaterSurfaceY - SwimSurfaceSubmersionFraction x H`. A body that fell into deep water dips below the float line
   and returns `Suspended` until buoyancy brings it back into the band, as an airborne body does until it lands.
4. A settled swimming body proceeds. Step admission accepts a prediction that is grounded and not swimming, or
   swimming, and finite, and whose segment the guard allows from the current feet. A prediction that is airborne and
   not swimming is refused.
5. The travel bound becomes swim-aware. When `CharacterMovement.ResolveSwimming(body.Swimming, medium, feetY, tuning)`
   says this tick swims, the bound is `SwimSpeed x max(0, medium.WadeSpeedScale) x SpeedScale x dt`, with the medium
   sampled at the body's feet through the context. Otherwise it is today's walk or run bound. A non-finite bound
   throws as today.
6. Feet, region membership, `InRange`, the near-field approach and the stop ring use the body's real position, as
   for a grounded body.

The constructor that takes a `GroundNavigation` throws `ArgumentException` when `SteerWhileSwimming` is true and the
profile's `Aquatic` is false. The planner constructor trusts the caller's space and guard. `NpcGroundMovement` and
`PlayerPathMovement` are unchanged. A swimming hold steps the core with zero input, which settles buoyancy.

## 4. Route pace (#1257)

### Collinear pass-through

`NavPath.IsCollinearPassThrough(int index)` is true when the waypoint has a predecessor and a successor, the
waypoint and its successor are `Walk`, all three share one layer, and in double precision the incoming and outgoing
XZ directions `a` and `b` are nonzero with `dot(a, b) > 0` and `|cross(a, b)| <= 1e-4 x |a| x |b|`. Cell routes turn by
45 or 90 degrees, so the tolerance never admits a grid corner.

Index 0 is never a pass-through, because the route builder drops the body's own cell and waypoint 0 has no
predecessor in the path. Each plan therefore lands exactly on waypoint 0, costing at most one partial tick per plan.
A static target plans once. A moving target replans on drift, at most once per replan cooldown.

### Follower consumption

`PathFollowConfig.ConsumePassedCollinearWaypoints`, default false. When true, the follower's waypoint advance also
consumes the active waypoint `w` with successor `n` when `IsCollinearPassThrough` holds, the agent's layer equals
`w`'s layer when the follower has a space, and the feet are past `w` on the outgoing line: in double precision
`dot(feet - w, n - w) >= 0` and the lateral distance from the line through `w` and `n` is at most
`AcceptRadius + 4 x u`, where `u` is the float spacing at the largest absolute coordinate among `w`, `n` and the
feet. The allowance exists because at Hollowmere's 128 to 192 m coordinates one float step is about 1.5e-5 m, above
the 1e-5 m accept radius. The final waypoint of a route has no successor, so region arrival keeps requiring
membership. Proximity consumption is unchanged.

### Driver carry

`RouteApproachOptions.CarryThroughStraightRuns`, default false. When true, `MoveToRange` builds its strict follower
config with `ConsumePassedCollinearWaypoints` true. When false it copies the caller's value. On a route tick with the
option true, when the remaining distance to the active waypoint is below the travel bound and the active waypoint is
a collinear pass-through, the driver aims at the run end instead: the first later waypoint that is not a collinear
pass-through. The command is `BoundedDirection(runEnd - position, bound)`. If the prediction fails step admission,
the driver falls back to today's command capped at the active waypoint and admits that as today. A corner is never a
pass-through, so the body still stops on it. The near-field approach and the stop ring are unchanged.

Without carry, 1.875 m/s is the steady rate on a straight 0.25 m cell route, not the first second's travel. From
rest the first tick plans synchronously and moves, and there is no ground acceleration, so 28 ticks cover seven cells
(1.75 m) and two more ticks add 0.133 m, about 1.883 m in the first second. With carry, from rest on a cell centre
with one partial tick at waypoint 0, the first second covers about 1.98 m on a straight route and about 1.95 m on a
diagonal route. Every tick between waypoint 0 and the run end travels the full bound.

## 5. Low lip (#1253)

A fixture reproduces the reported geometry with local coordinates. A Bepu ground plane, a deck box with its west edge
at x = 0 and its top at 0.025 m, and the issue's capsule: radius 0.3 m, half height 0.75 m, step 0.4 m. Capture bounds
run from x = -1.5 to 1.5 and z = -1 to 1 at 0.25 m cells. The footprint refuses feet within one radius of the bounds
(`NavAreaFootprint.cs:50`), so nodes exist from x = -1.125 to 1.125. Cell centres sit at -0.125 and 0.125 beside the
edge. At x = -0.125 the capsule's bottom cap is `0.3 - sqrt(0.3^2 - 0.125^2) - 0.025`, about 2.28 mm, above the deck
corner (ruling M11). The implementer records the measured contact in the plan Outcome.

The fixture asserts:

1. The bake accepts both directed edges between x = -0.375 and -0.125, and between -0.125 and 0.125.
2. A route from x = -1.125 to 1.125 is `Complete`.
3. A live body driven by `NpcGroundMovement` at walk pace from x = -1.125 crosses onto the deck grounded, with feet
   within 1 mm of 0.025 m.

Outcomes:

- Assertion 3 passes and 1 or 2 fails: the probe is wrong. Plan Task 4 changes only the shared ground arrival rule in
  `GroundTraversalProbe`. The rule is written into this section before GREEN and must keep
  `ThinWallBetweenPassableEndpointsCannotBeCrossed` and every existing probe and profile test green.
- Assertion 3 fails: the core cannot mount the lip. Plan Task 4b makes a targeted Locomotion step-up fix (ruling M4),
  with assertion 3 as its failing test, then reruns assertions 1 and 2.
- All pass: repeat on a TileWorld bridge fixture with a 2.5 cm drawn deck. If that also passes, stop and report that
  the lead does not reproduce, so the orchestrator can relabel the issue.

### Cause and rule (Task 4b, rulings M13 and M14)

Task 3 classified a core fault. The fixture's ground height callback reads 0 under the deck as well as the bank.
`PropSupportFloor` (`CharacterMovement.Collision.cs`) runs its downward prop sweep only when the body was airborne,
stepped up, or started more than `OnPropSkin` (0.05 m) above that callback. A grounded body at the callback height
therefore never takes a lower prop top as support. The swept move lets the capsule into the prop, because the
bottom cap meets the lip with a walkable normal and passes through. The ground snap in `StepCore` then seats the
body at the callback height inside the prop. A body set on the 2.5 cm deck drops to 0 in one tick. Task 3's variant
table shows the same sinking for lips up to 0.05 m at 1 m/s and up to 0.1 m at 9 m/s. With the callback lowered
under the deck, the unchanged core and probe pass every fact.

Rule. When that gate is closed, the same downward sweep also runs, with its walkable and under-footprint guards. Its
surface becomes support only when both of these hold:

1. The surface normal is at least `LipLandingFlatNormalY` (0.9), the flat tread test the lip band step-up already
   applies. A curb, deck or doorstep top passes. A steep convex flank does not.
2. The resting centre lies above the support found so far and at most `LowPropRise` (0.1 m) above the centre the
   body started the tick at. The band comes from the evidence, which covers lips of 0.1 m and below. It is not
   "any prop top within StepHeight".

Once the body stands more than `OnPropSkin` above the callback, the existing gate takes over and follows the prop as
before.

Measured guarantee (ruling M16). The rule does not draw a clean line at the 0.9 normal. Task 4b fix round 1 walked
player and NPC capsules at spheres standing out of flat terrain and measured three bands:

1. A flank steeper than the walking slope limit is never raised. The radius 2 test dome meets the terrain at a 0.5
   normal and blocks the body at its base with its feet at the terrain.
2. A near flat top, normal at least 0.9, within 0.1 m above the body's start is support. Lips, curbs, deck tops and
   a gentle mound that meets the terrain at 0.925 are mounted without sinking.
3. A walkable flank between those is entered as the swept move allows, because the cap passes through a walkable
   contact. The body is then seated once its footprint reaches a near flat part, or it sinks into the flank as it
   did before this change. A mound meeting the terrain at 0.85 is mounted at player walk pace after sinking about
   3 cm. Flanks at 0.8 and 0.7 are still entered and sunk into by about 0.1 to 0.29 m, depending on capsule and pace,
   and an NPC at walk pace is not raised onto them at all. That sinking is older than this rule and is filed as
   https://github.com/APKiwiOrg/KhaozEngine/issues/1260.

A code rule that refuses seating after a steeper flank would make the 0.9 line exact, but it would also make
moderate mounds unwalkable, so this round does not add one (ruling M16).

Facts pin the bands: `LowPropSupportTests.CapsuleIsNeverRaisedUpASteepDome` (band 1),
`CapsuleWalksUpAGentleMound` (band 2) and `CapsuleWalksUpAModerateMoundAtWalkPace` (band 3, the 0.85 case). These
existing tests in `KhaozEngine.Game.Tests/Physics`, outside the Locomotion filter, must stay green, so Task 4b also
runs `FullyQualifiedName~KhaozEngine.Tests.Physics`: `ControllerOnPhysicsTests.Capsule_BlockedAtDomeBase_DoesNotPenetrate`,
`Capsule_RestsOnDomeFlank_WithoutPenetrating`, `Capsule_MountsDomeFromSide_ByJumping`,
`GroundedCapsule_WalksOffLedge_ReleasesAndFalls` and `PhysicsFeelTests.DomedRockTop_SettlesAndMoves`.

Costs. A grounded tick at terrain height in a world with physics pays one more capsule sweep, the sweep an elevated
body already pays. If the movement queries contain a ground mesh at the callback height, the sweep meets it at the
current support and changes nothing. A mesh that sits up to 0.1 m above the callback now supports the body.
TileWorld's movement query view already excludes its own ground mesh. The rule clears the 0.06 m stall at 1 m/s, and
`LowLipTraversalTests.LiveBodyMountsLowLipsUpToATenthOfAMetre` pins it.

After Task 4 or 4b merges, root reruns the focused filters of every lane that bakes through the movement core
(ruling M9).

## 6. Public API

All additive. Types new in the unreleased `20.20.0` gain members in place.

```csharp
namespace KhaozEngine.Navigation;

public sealed class PathFollowConfig
{
    public bool ConsumePassedCollinearWaypoints { get; init; }  // default false
}

public sealed class NavPath
{
    public bool IsCollinearPassThrough(int index);              // throws ArgumentOutOfRangeException off the list
}

namespace KhaozEngine.Movement;

public sealed record PhysicsNavBakeOptions(/* unchanged positional parameters */)
{
    public bool SampleWater { get; init; }                      // default false, in the bake identity
}

public sealed record GroundProfileOptions
{
    public static GroundProfileOptions Default { get; }
    public bool Aquatic { get; init; }                          // default false
}

public sealed record RouteApproachOptions
{
    public static RouteApproachOptions Default { get; }
    public bool SteerWhileSwimming { get; init; }
    public bool CarryThroughStraightRuns { get; init; }
}

public sealed partial class MoveToRange
{
    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow, RouteApproachOptions options);
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow, RouteApproachOptions options);
}

public sealed partial class PhysicsNavBake
{
    public GroundNavigation BuildProfile(in MoveTuning tuning, NavAreaFilter areas, GroundProfileOptions options);
}

public sealed class GroundNavigation
{
    public bool Aquatic { get; }
}

public sealed record NavBakeProfile(string Name, MoveTuning Tuning, NavAreaFilter Areas)
{
    public bool Aquatic { get; init; }                          // default false
}
```

The existing `MoveToRange` constructors and `BuildProfile(tuning, areas)` delegate with default options.
`BuildProfile` with `Aquatic` true throws `ArgumentException` when the capture has no sampled water or the swim
fractions are out of order. `GroundNavigationBake.Create` passes each profile's `Aquatic`.

## 7. Test strategy

| Proof | Scenarios | Home |
| --- | --- | --- |
| Pass-through geometry | Straight, diagonal, 45 and 90 degree corners, reversal, layer change, Hop successor, index 0, ends of the list | Game.Tests Navigation |
| Follower consumption | Opt-in default off, passed collinear consumed after the start waypoint, corner never consumed early, lateral miss kept, layer mismatch kept, large coordinate allowance, final region waypoint still needs membership | Game.Tests Navigation |
| Driver carry | Full bound every tick of a straight and a diagonal run, first-second pace, stop at a corner, fallback when the carried step is refused, default unchanged, large coordinates | Movement.Tests |
| Low lip | Section 5 fixture, then the gated probe or core fix | Movement.Tests, Game.Tests Locomotion, TileWorld.Physics.Tests if needed |
| Water capture | Opt-in default off, byte-identical output for a medium context without the opt-in, refusal without a medium, lowest surface sampling, empty column, deck over water, classifier point | Movement.Tests |
| Aquatic profile | Deep pool with a steep or missing bed routes, a narrow deck between cell centres refused by clearance, shore edges proven both ways, ground profile unchanged, tuning mismatch and fraction order refused | Movement.Tests |
| Format | Aquatic round trip equivalence, `SampleWater` and `Aquatic` identity and corruption, water section invariants and truncation, golden fingerprint re-recorded | Movement.Tests |
| Swim steering | Airborne, committed and settling bodies suspended with the option, swimming body suspended without it, swim bound at swim speed, airborne exit refused, real duck crosses deep water into range and routes around a deck | Movement.Tests |

No test needs Grimhollow assets. Focused runs per task and one full Release verification at the finish. No local
repetition or stress runs.

## 8. Version and release

Additive public API rides the staged `20.20.0` entry. No new version. Ruling M1 holds the `20.20.0` tag until this
round lands, because the KENB layout is free to change only before the tag. Grimhollow adopts a released pin.

## Rulings

Recorded on 2026-10-03 by the orchestrator. They replace the first draft's open questions Q1 to Q5.

- M1 (Q1): the `20.20.0` tag waits for this round's KENB change.
- M2 (Q2): water sampling is an explicit capture opt-in, default off. Section 2 Capture and D3.
- M3 (Q3): `DirectMoveToRange` stays out of swim steering and its docs state why. D6.
- M4 (Q4): a targeted Locomotion step-up fix is authorized if the live core cannot mount the lip. D5 and section 5.
- M5 (Q5): the deepest-contact limit is accepted, documented and followed by a sweep issue. Section 2 Proofs.
- M6: land-only bakes change KENB v1 bytes and the golden fingerprint. Section 2 format.
- M7: `SampleWater` joins the bake identity, 13 options become 14.
- M8: the plan's Task 4 owns one shared ground arrival helper, and the swim probe consumes it after Task 4 merges.
- M9: after Task 4 or 4b merges, root reruns the focused filters of every lane that bakes through the movement core.
- M10: names `RouteApproachOptions`, `CarryThroughStraightRuns`, `ConsumePassedCollinearWaypoints`, `Aquatic`, the
  `GroundProfileOptions` property for `BuildProfile`, and `PhysicsNavBakeOptions.SampleWater`.
- M11: the lip clearance at the cell centre beside the deck edge is 2.28 mm, from the formula in section 5. Settled.

No open question remains.

## Out of scope

- Grimhollow files, engine tags and consumer pin moves.
- Swimmer collision in the core, diving and multi-level water.
- Swim steering in `DirectMoveToRange`.
- Locomotion changes other than the targeted step-up fix of D5 B.
- Issues found during execution, which become ledger issues.
