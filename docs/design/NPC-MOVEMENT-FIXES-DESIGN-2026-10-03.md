# NPC movement fixes: swimming routes, route pace and low lips

Status: design and [implementation plan](../superpowers/plans/2026-10-03-npc-movement-fixes.md) written on
`feature/grimhollow-npc-movement`. Awaiting orchestrator rulings on the open questions at the end. No implementation,
release or tag is claimed.

Consumer: Grimhollow P5 creature host on engine 20.18.0, branch `feature/p5-lane-n`.
Engine issues: [#1256](https://github.com/APKiwiOrg/KhaozEngine/issues/1256) (swimming body cannot be steered),
[#1257](https://github.com/APKiwiOrg/KhaozEngine/issues/1257) (route pace loses a quarter tick per cell) and
[#1253](https://github.com/APKiwiOrg/KhaozEngine/issues/1253) (bake refuses a 2.5 cm lip, `confidence/lead`).

Base: engine main `fefb78876`. Newest tag `v20.19.0`. Main stages `20.20.0` untagged, carrying the
[nav profile bake](NAV-PROFILE-BAKE-DESIGN-2026-10-03.md) and the route-free `DirectMoveToRange`. This round rides that
staged version. Line citations are at main unless marked v20.18.0.

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
  swimmer does not collide with props. Entering water ends an active commitment as `Aborted` with `EnteredWater`
  (`CharacterMovement.cs` swim branch), so a swimming body never carries an active commitment.
- Default fractions: `SwimEnterDepthFraction` 0.65, `SwimExitDepthFraction` 0.55,
  `SwimSurfaceSubmersionFraction` 0.6 (`MoveTuning.cs:31-34`). A body starts swimming at chest depth and floats with
  its feet 0.6 body heights below the surface.

### Route pace (#1257)

A creature on a baked route at 2 m/s and 30 Hz covers 1.875 m/s, measured by Grimhollow at 93.9 percent of walk
speed. `MoveToRange.Tick` caps the command at the active waypoint (`MoveToRange.cs:66-67`, `BoundedDirection` at
`RangeApproachCore.cs:34-40`) and discards the leftover travel. With the 1e-5 m accept radius every 0.25 m cell centre
is landed on exactly, so one tick in four ends short: 0.0667, 0.0667, 0.0667, then 0.05 m. The follower advances
only by proximity (`PathFollower.Region.cs:129-146`).

### Low lip (#1253)

Grimhollow's creature bake refuses the edge from the bank onto the Hollowmere bridge deck at both ends for a 0.3 m
radius, 0.75 m half height capsule. The bank is at 0 m and the deck at 0.025 m. Not reproduced in isolation. The lead
is `GroundTraversalProbe`'s absolute 1 mm arrival tolerance (`GroundTraversalProbe.cs:10`, `:39`, `:54`, `:62-66`)
against the collide and slide `SkinWidth` of 0.01 m (`CharacterMovement.Collision.cs:22`). At cell centre
x = 71.875 the capsule's bottom cap passes about 2.3 mm above the deck corner, which is inside the skin.

## Goals

1. An aquatic profile routes a swimming body through water that the movement medium reports, independent of the bed
   shape, with nodes and edges proven at the height the body actually floats.
2. `MoveToRange` steers a swimming body on such a profile, opt-in per driver, with the same reach, stop ring, guard
   and status rules as a grounded body.
3. Airborne bodies and committed bodies stay `Suspended` exactly as today, including D0.2's precedence over
   `InRange`.
4. `MoveToRange` holds full pace along collinear runs of a route, opt-in. A mandatory turn still stops at the corner.
5. A minimal engine fixture settles #1253. If it reproduces, the bake accepts a lip the live core can mount.
6. Every new behaviour is opt-in. Today's defaults, ground profiles and existing bakes behave as they do now.

## Non-goals

- Diving, underwater routes, swim at any height other than the resting float line.
- Swim steering in `DirectMoveToRange`. See D6.
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

- Changes the unreleased KENB v1 payload and identity. It must land before `20.20.0` is tagged.
- About 400 production lines across capture, profile build, a swim probe, format and drivers.
- Capture samples the medium once per column when a medium is present.

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
adds reads the body's real position. The format window closes at the `20.20.0` tag, so this is the cheap moment to
take it. B scores 8 rather than 10 on correctness because the core still does not collide a swimmer at runtime, and a
penetration query reports only its deepest contact.

Rejected alternative: A, for the arrival livelock, the invisible surface obstacles and the unchecked bed
connectivity. It remains a possible fallback only if the orchestrator rules that the format must not change before
the tag (open question Q1).

### D2. Where the swim opt-in lives

| Option | Opt-in clarity | Identity safety | API size | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Profile flag in the bake plus a driver option | 9 | 10 | 7 | 26 |
| B. Driver option only, profile always carries float nodes | 5 | 6 | 9 | 20 |
| C. Profile flag only, drivers steer swimmers on any aquatic profile | 7 | 10 | 9 | 26 |

Select A. Float nodes change the graph, so the bake identity must say whether a profile has them, which rules out B.
C ties on score but makes a driver behave differently because of how a file was baked, while the round 2 contract
keeps steering policy at the driver. A keeps both explicit, and a driver that asks to steer swimmers on a profile
without float nodes is refused at construction.

### D3. Capture-time water sampling

| Option | Defaults unchanged | Misconfiguration caught | Surface | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Capture samples the medium whenever the context carries one | 8 | 9 | 10 | 27 |
| B. A new `PhysicsNavBakeOptions` flag | 10 | 6 | 6 | 22 |
| C. A separate game water delegate | 9 | 5 | 5 | 19 |

Select A. Ground profiles never read the water data, so their graphs, guards and routes are unchanged. The only
observable change for an existing caller is one medium call per column and a few bytes of bake payload. B adds an
identity field to a released options record, and a forgotten flag silently yields an aquatic profile without water.
C duplicates the medium, which is already the single both-heads source of water. `Create` and `BuildProfile` refuse
an aquatic profile from a capture without a medium. Open question Q2 asks whether A's default is acceptable.

### D4. Carrying travel past a waypoint (#1257)

| Option | Turn safety | Pace | Surface | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Steer at the end of a collinear run, follower consumes passed collinear waypoints | 10 | 9 | 8 | 27 |
| B. Split the leftover travel across the next segment in the driver | 6 | 10 | 7 | 23 |
| C. Widen the accept radius to one tick of travel | 3 | 8 | 10 | 21 |

Select A. On a collinear run, aiming at the run end and aiming at the next waypoint are the same direction, so the
body travels the full bound and passes intermediate cell centres. The follower must then consume a waypoint the body
passed rather than landed on. Restricting both rules to collinear waypoints keeps D1.1's guarantee: a mandatory turn
is never consumed early. B bends a single tick's command around a corner, which the round 2 design forbids. C is the
broad tolerance D1.1 removed.

### D5. Where a #1253 fix may land

| Option | Blast radius | Fidelity to runtime | Total |
| --- | ---: | ---: | ---: |
| A. Movement's `GroundTraversalProbe` arrival judgment, only after a fixture proves the live core mounts the lip | 9 | 9 | 18 |
| B. Locomotion's step-up or skin rules | 3 | 8 | 11 |

Select A, gated. If the fixture shows the live core itself cannot mount the lip, the bake is telling the truth and the
fault is in Locomotion. That change touches every game's movement and is out of this round. The implementer stops and
reports for a ruling (open question Q4).

### D6. Swim steering in `DirectMoveToRange`

Excluded. The direct driver has no graph guard, and a swimmer has no collision, so a direct swim approach would pass
through any prop at the waterline. Its documented refusal of steps that start swimming stays. Open question Q3 asks
whether the orchestrator wants it anyway with that limitation.

## 1. Package and dependency edges

No project reference changes. Movement keeps exactly Locomotion, Navigation and Physics. The swim clearance check
uses `IPhysicsWorld.ComputePenetration`, already in Physics. Navigation gains a route geometry query and a follower
option with no new reference. No wire, `NetWorld` or persisted payload change outside the unreleased KENB file.

## 2. Surface water layer (#1256)

### Capture

When `context.Medium` is not null, `PhysicsNavBake.Capture` samples it once per in-bounds column after the physics
probe. The sample feet height `y0` is the lowest captured surface height, or `ProbeHeight - ProbeRange` when the
column has no surface. A sample with `InWater` true and a finite `WaterSurfaceY` above `y0` records one water entry:
the cell, the surface Y and the area bits the classifier returns for `(x, WaterSurfaceY, z)`. The classifier receives
the water surface point for a water entry. With a null medium no entry is recorded and capture is unchanged.

Limits: one water surface per column, found from the lowest surface. A pool on a deck above dry ground is not seen.
The medium must be the same provider the runtime context uses, and the game's source digests must cover it, which
the nav bake design already requires.

### Aquatic columns

A profile baked with `swims` true reads a derived column view instead of the captured columns. For each column with
a water entry at height `W`, with body height `H = 2 x CapsuleHalfHeight`:

1. Let `s` be the highest captured surface below `W`, if any.
2. The column is swim-deep when there is no such `s`, or `(W - s.Height) / H >= SwimEnterDepthFraction`, and the
   float height `f = W - SwimSurfaceSubmersionFraction x H` lies above `s.Height`.
3. A swim-deep column replaces every captured surface below `W` with one float surface at `f`. Its areas are the
   water entry's areas. Its headroom is `s.Headroom - (f - s.Height)` when `s` exists, otherwise positive infinity.
   Surfaces at or above `W` are kept unchanged.
4. Every other column is unchanged. A column between the exit and enter fractions keeps its bed, where a wading body
   stands and a swimming body floats within `0.05 x H` of it at default tuning.

All arithmetic is IEEE single precision in this order, so a load derives the same bits as a fresh build. The
footprint, the candidate columns and `Resolve` all read the derived view, so the existing guards apply unchanged to
float surfaces. A ground profile reads the captured columns exactly as today.

Shore continuity follows from the tuning. At the enter threshold the bed sits `0.65 x H` below the surface and the
float line `0.6 x H`, so on a gently sloping bed the layered bake joins float cells to wading cells within the
step height. Cell sampling on a steep bed can widen that gap. A bank more than
one step above the float line is a drop. A body cannot step off it into the water, which matches the runtime rule
that an airborne step is refused.

### Proofs

A swim-deep candidate is held by a swim hold. Every edge or Stair link with a float endpoint is proven by a new
medium probe. Edges between two non-float nodes keep the existing dry proof, so an aquatic profile's dry graph equals
the ground profile's dry graph over the same cells.

The medium probe runs through the bake's live context, with its medium, not `DryContext`. It uses the profile tuning
with unit walk and run pace and no air momentum, as the dry probe does.

- A float start is `Swimming = true`, `Grounded = false`, at `f + CapsuleHalfHeight`. Any other start is grounded and
  not swimming, as today.
- The first slice holds with zero input and must stay in the start's mode and near the start.
- Each later slice requests `BoundedDirection(target - feet, bound)`, where `bound` is the swim-aware travel bound of
  section 3 for this slice with `run` false. A slice whose bound is zero fails the proof.
- A slice is admitted when the result is finite, the footprint accepts its feet, and it is either grounded and not
  swimming or swimming. A swimming slice must also pass the clearance check.
- A grounded slice arrives when its feet are within `ArrivalTolerance` of the target in all three axes, as today. A
  swimming slice arrives when its feet are within `ArrivalTolerance` horizontally and within
  `max(StepHeight, ArrivalTolerance)` vertically, the same band `Resolve` accepts at runtime. Buoyancy, not geometry,
  sets a swimmer's height.

Clearance check. The core does not sweep a swimmer, so each swimming slice asks
`(MovementQueries ?? Physics).ComputePenetration` for the profile capsule at its pose. It passes when there is no
overlap, or when the minimum translation points up within the walkable slope, `mtv.Y >= |mtv| x cos(MaxSlopeRadians)`,
and `|mtv| <= StepHeight`. That admits a float capsule grazing the bed near the shore and refuses a deck above, a
post beside and a steep bank wall. A context without physics passes. The query reports the deepest contact only, so a
shallow side contact under a deeper bed contact can pass a slice. Deep water has no bed contact, so the limit only
applies within about one step of the bed. It is recorded as a limit, and Q5 asks whether to replace it with a sweep.

### Bake format, still version 1

KENB v1 is unreleased, so the layout changes in place and `FormatVersion` stays 1. These changes must land before
`20.20.0` is tagged.

- Identity: each profile entry gains one `uint8` swim flag, 0 or 1, after `Excluded` and before the tuning. Any other
  value is non-canonical and `Corrupt`. The golden identity fingerprint is re-recorded.
- Payload capture section: after the surfaces, a `uint8` medium flag, 1 when the capture context carried a medium and
  0 otherwise, then a water entry count `W` as `int32`, then `W` entries in ascending cell order, each the cell index
  as `int32`, the surface Y as float bits and the areas as `uint32`. A flag other than 0 or 1 is `Corrupt`, and so is
  a nonzero `W` with flag 0. The flag lets `Create` and `BuildProfile` refuse an aquatic profile from a capture that had
  no medium, before and after a round trip.
- Reader invariants: `0 <= W <= C`, strictly ascending cells below `C`, each cell centre inside the bounds, finite
  surface Y above the column's lowest surface, or above `ProbeHeight - ProbeRange` for an empty column.
- Payload bound: the capture term gains `5 + 12 x C`.
- An aquatic profile's layers, exits and links are stored exactly as a ground profile's. Load rebuilds its derived
  column view from the captured columns, the water entries and the stored tuning before it builds the footprint.
- `ProfilesChanged` names `Swims` as the first differing field when only the flag differs.
- Ground profiles of one set still share one column instance. Each aquatic profile owns its derived view.
- `MoveTuning` stays covered as today. `SwimEnterDepthFraction`, `SwimSurfaceSubmersionFraction` and `SwimSpeed` are
  already among the 27 retained fields.

### Runtime profile

`GroundNavigation.Swims` reports the flag. An aquatic profile's `ValidateTuning` also requires the runtime
`SwimEnterDepthFraction`, `SwimExitDepthFraction` and `SwimSurfaceSubmersionFraction` to equal the baked values, since
they place float nodes. `Resolve`, `AllowsSegment`, `Planner` and `NavSpace.LayerAt` need no change, because a
floating body's real feet sit on a float node.

## 3. Swim steering in `MoveToRange`

`MoveToRangeOptions.SteerWhileSwimming`, default false. With it false, every behaviour is today's. With it true:

1. Validation and reach evaluation run first, as today.
2. A body is `Suspended` when its commitment is active, or when it is neither grounded nor swimming. An airborne,
   non-swimming body and a committed body therefore return `Suspended` before `InRange`, as D0.2 rules. A swimming
   body proceeds.
3. Step admission accepts a prediction that is grounded and not swimming, or swimming, and finite, and whose segment
   the guard allows from the current feet. A prediction that is airborne and not swimming is refused.
4. The travel bound becomes swim-aware. When `CharacterMovement.ResolveSwimming(body.Swimming, medium, feetY, tuning)`
   says this tick swims, the bound is `SwimSpeed x max(0, medium.WadeSpeedScale) x SpeedScale x dt`, with the medium
   sampled at the body's feet through the context. Otherwise it is today's walk or run bound. A non-finite bound
   throws as today.
5. Feet, region membership, `InRange`, the near-field approach and the stop ring use the body's real position, as
   for a grounded body.

The constructor that takes a `GroundNavigation` throws `ArgumentException` when `SteerWhileSwimming` is true and the
profile's `Swims` is false. The planner constructor trusts the caller's space and guard. `NpcGroundMovement` and
`PlayerPathMovement` are unchanged. A swimming hold steps the core with zero input, which settles buoyancy.

## 4. Route pace (#1257)

### Collinear pass-through

`NavPath.IsCollinearPassThrough(int index)` is true when the waypoint has a predecessor and a successor, the
waypoint and its successor are `Walk`, all three share one layer, and in double precision the incoming and outgoing
XZ directions `a` and `b` are nonzero with `dot(a, b) > 0` and `|cross(a, b)| <= 1e-4 x |a| x |b|`. Cell routes turn by
45 or 90 degrees, so the tolerance never admits a grid corner.

### Follower consumption

`PathFollowConfig.ConsumePassedWaypoints`, default false. When true, the follower's waypoint advance also consumes
the active waypoint `w` with successor `n` when `IsCollinearPassThrough` holds, the agent's layer equals `w`'s layer
when the follower has a space, and the feet are past `w` on the outgoing line: in double precision
`dot(feet - w, n - w) >= 0` and the lateral distance from the line through `w` and `n` is at most
`AcceptRadius + 4 x u`, where `u` is the float spacing at the largest absolute coordinate among `w`, `n` and the
feet. The allowance exists because at Hollowmere's 128 to 192 m coordinates one float step is about 1.5e-5 m, above
the 1e-5 m accept radius. The final waypoint of a route has no successor, so region arrival keeps requiring membership.
Proximity consumption is unchanged.

### Driver carry

`MoveToRangeOptions.CarryAlongRoute`, default false. When true, `MoveToRange` builds its strict follower config with
`ConsumePassedWaypoints` true. When false it copies the caller's value. On a route tick with
`CarryAlongRoute` true, when the remaining distance to the active waypoint is below the travel bound and the active
waypoint is a collinear pass-through, the driver aims at the run end instead: the first later waypoint that is not a
collinear pass-through. The command is `BoundedDirection(runEnd - position, bound)`. If the prediction fails step
admission, the driver falls back to today's command capped at the active waypoint and admits that as today. A corner
is never a pass-through, so the body still stops on it. The near-field approach and the stop ring are unchanged.

## 5. Low lip (#1253)

A fixture reproduces the reported geometry with local coordinates. A Bepu ground plane, a deck box with its west edge
at x = 0 and its top at 0.025 m, capture bounds from x = -1 to 1 at 0.25 m cells, so cell centres sit at -0.125 and
0.125 beside the edge, and the issue's capsule: radius 0.3 m, half height 0.75 m, step 0.4 m. At x = -0.125 the
capsule's bottom cap is 2.3 mm above the deck corner, as at x = 71.875 in Hollowmere.

The fixture asserts:

1. The bake accepts both directed edges between x = -0.375 and -0.125, and between -0.125 and 0.125.
2. A route from x = -0.875 to 0.875 is `Complete`.
3. A live body driven by `NpcGroundMovement` at walk pace from x = -0.875 crosses onto the deck grounded, with feet
   within 1 mm of 0.025 m.

Outcomes:

- Assertion 3 passes and 1 or 2 fails: the probe is wrong. The fix changes only `GroundTraversalProbe`'s arrival
  judgment. The rule is written into this section before GREEN and must keep `ThinWallBetweenPassableEndpointsCannotBeCrossed`
  and every existing probe and profile test green.
- Assertion 3 fails: the core cannot mount the lip. Stop and report for a ruling. Out of this round.
- All pass: repeat on a TileWorld bridge fixture with a 2.5 cm drawn deck. If that also passes, stop and report that the
  lead does not reproduce, so the orchestrator can relabel the issue.

## 6. Public API

All additive. Types new in the unreleased `20.20.0` gain members in place.

```csharp
namespace KhaozEngine.Navigation;

public sealed class PathFollowConfig
{
    public bool ConsumePassedWaypoints { get; init; }          // default false
}

public sealed class NavPath
{
    public bool IsCollinearPassThrough(int index);              // throws ArgumentOutOfRangeException off the list
}

namespace KhaozEngine.Movement;

public sealed record MoveToRangeOptions
{
    public static MoveToRangeOptions Default { get; }
    public bool SteerWhileSwimming { get; init; }
    public bool CarryAlongRoute { get; init; }
}

public sealed partial class MoveToRange
{
    public MoveToRange(GroundNavigation navigation, PathFollowConfig? follow, MoveToRangeOptions options);
    public MoveToRange(IRegionPathPlanner planner, NavSpace space,
        Func<Vector3, Vector3, bool> allowsSegment, PathFollowConfig? follow, MoveToRangeOptions options);
}

public sealed partial class PhysicsNavBake
{
    public GroundNavigation BuildProfile(in MoveTuning tuning, NavAreaFilter areas, bool swims);
}

public sealed class GroundNavigation
{
    public bool Swims { get; }
}

public sealed record NavBakeProfile(string Name, MoveTuning Tuning, NavAreaFilter Areas)
{
    public bool Swims { get; init; }                            // default false
}
```

The existing `MoveToRange` constructors and `BuildProfile(tuning, areas)` delegate with default options and
`swims` false. `BuildProfile` with `swims` true throws `ArgumentException` when the capture context had no medium.
`GroundNavigationBake.Create` passes each profile's `Swims`.

## 7. Test strategy

| Proof | Scenarios | Home |
| --- | --- | --- |
| Pass-through geometry | Straight, diagonal, 45 and 90 degree corners, reversal, layer change, Hop successor, ends of the list | Game.Tests Navigation |
| Follower consumption | Opt-in default off, passed collinear consumed, corner never consumed early, lateral miss kept, layer mismatch kept, large coordinate allowance, final region waypoint still needs membership | Game.Tests Navigation |
| Driver carry | 2 m/s held on a straight cell route, stop at a corner, fallback when the carried step is refused, default unchanged | Movement.Tests |
| Low lip | Section 5 fixture, then the gated fix | Movement.Tests, TileWorld.Physics.Tests if needed |
| Water capture | Entries only with a medium, lowest surface sampling, empty column, deck over water, classifier point | Movement.Tests |
| Aquatic profile | Deep pool with a steep or missing bed routes, low deck at the surface refused, shore edges proven both ways, ground profile of the same capture unchanged, tuning mismatch refused | Movement.Tests |
| Format | Aquatic round trip equivalence, swim flag identity and corruption, water section invariants and truncation, golden fingerprint re-recorded | Movement.Tests |
| Swim steering | Airborne and committed bodies suspended with the option, swimming body suspended without it, swim bound at swim speed, airborne exit refused, real duck crosses deep water into range | Movement.Tests |

No test needs Grimhollow assets. Focused runs per task and one full Release verification at the finish. No local
repetition or stress runs.

## 8. Version and release

Additive public API rides the staged `20.20.0` entry. No new version and no tag. The KENB layout change makes the
`20.20.0` tag wait for this round, or this round's format part must be dropped (Q1). Grimhollow adopts a released pin.

## Open questions for the orchestrator

1. Q1. Hold the `20.20.0` tag until this round's KENB change lands. If the tag cannot wait, D1 falls back to
   option A for this release.
2. Q2. Accept D3's default that capture samples the medium whenever the context carries one, or require an explicit
   opt-in.
3. Q3. Keep `DirectMoveToRange` out of swim steering (D6), or add it with the documented no-collision limit.
4. Q4. If the #1253 fixture shows the live core cannot mount a 2.5 cm lip, authorize a Locomotion step-up change in
   this round or file it separately.
5. Q5. Accept the clearance rule's deepest-contact limit in section 2, or require a sweep per swim slice at higher
   bake cost.

## Out of scope

- Grimhollow files, engine tags and consumer pin moves.
- Swimmer collision in the core, diving and multi-level water.
- Swim steering in `DirectMoveToRange` unless Q3 rules otherwise.
- Locomotion changes for #1253 unless Q4 rules otherwise.
- Issues found during execution, which become ledger issues.
