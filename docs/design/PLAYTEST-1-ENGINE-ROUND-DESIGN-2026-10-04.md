# Playtest 1 engine round: straight routes, smooth facing and directional locomotion

Status: proposed, for the owner's review. No code, version or tag yet. The orchestrator ruled Q1 and Q2 below.

Consumer: Grimhollow `feature/continuous-movement` at `78294e64`, engine pin 20.20.0 moving to 20.21.0. Base: engine
main `ee934bba5`, tag `v20.21.0`, `<KhaozEngineVersion>` 20.21.0, nothing staged. Rulings PT.1 to PT.3 in the
playtest ledger put this round in the engine. Line citations are at `ee934bba5` unless marked Grimhollow.

## Problem and measured facts

### Staircase routes

- `GroundNavigation.Planner` returns "unsmoothed cell-center routes" (`GroundNavigation.cs:37`) from an 8-direction
  grid search (`GridPathPlanner.Region.cs:9-13`). Every bend is 45 or 90 degrees.
- `MoveToRange` lands exactly on every corner under the strict 1e-5 m accept radius (`MoveToRange.cs:144`,
  `MoveToRange.Carry.cs:11-13`). Only collinear waypoints are passed through (`NavPath.PassThrough.cs:7-9`).
- Grimhollow's probe: 94 percent of moving cow ticks face one of the 8 grid directions, and cow-05 alternates a 45
  degree leg and a straight leg every 0.15 to 0.3 s on any off-axis walk.

### Snapping local facing

`ClientPrediction.RenderedState` lerps planar position and height between ticks (`ClientPrediction.cs:138-147`).
`PlayerMoveState.WithRenderState` replaces only the position (`PlayerMoveState.cs:87-92`), so the rendered heading is
the last tick's `MoveState.FacingYaw`. A keyboard turn at 180 deg/s steps 6 degrees every 33 ms at 30 Hz.
`WorldClient` hands that value to `EntityRenderState.FacingYaw` for the local entity (`WorldClient.cs:544`).

### The corner stall lead (ruled out)

The probe's run-length log shows, at every 45 degree leg to straight handover, a one-tick run at about 45 degrees
printed as `0.00 m`. Two facts settle it:

1. The log computes a run's distance as `cp[t - 1] - cp[start]` (`CowYawProbe.cs:86`), which is zero for every
   one-tick run by construction. The printed `0.00 m` is not a measurement.
2. A run splits when the yaw moves by 0.01 degrees or more. The split tick is the partial landing tick on the
   diagonal corner. At 2 m/s and 30 Hz a full tick is 0.0667 m. A diagonal cell (0.3536 m) takes five full ticks
   plus a 0.020 m partial, and a straight cell (0.25 m) takes three plus a 0.05 m partial, so one stair period is
   ten ticks. The log shows exactly five, one and four. A stall would make it eleven. The partial tick's command is
   `waypoint - position` (`MoveToRange.cs:96-97`). On a diagonal at 128 to 192 m coordinates both components carry
   up to half a float step (about 7.6e-6 m) of rounding, which on a 0.014 m component turns the heading by up to
   about 0.06 degrees. On a straight leg the cross component is exactly zero, so the straight partial never splits.

Neither candidate fires: `AllowsStep` admits the step and the bounded corner step moves. The real cost at corners is
the partial tick itself (30 percent of a tick at a diagonal corner), which straightening removes with the corners.

### Skating diagonals

Grimhollow's `ContinuousLocomotion` (Grimhollow `ContinuousLocomotion.cs:190-198`) picks one of four clip families,
and a 45 degree tie goes forward (pinned by Grimhollow `ContinuousLocomotionTests.cs:77-92`). Each family keeps its own
distance phase on its own axis (`ContinuousLocomotion.cs:144-152`). A forward-left walk plays the forward walk while
the body moves half sideways, so the feet skate. The engine already composes poses (`LayeredAnimator`,
`AnimationSampler`, `JointPose.Lerp`) and picks speed states (`LocomotionStateMachine`, `LocomotionSpeedSync` in
`KhaozEngine.Game.Render3D`), but it has no directional blend and no distance-synchronised phase.

## Goals

1. Opt-in route straightening that drops cell centres only where the segment guard accepts the whole segment.
2. Opt-in inter-tick interpolation of the locally predicted heading, the short way round.
3. A clip-agnostic directional blend: body-frame velocity in, weighted clip samples with distance-synced phases out.
4. Defaults unchanged. Captures, routes and rendered output stay identical without the opt-ins.

## Non-goals

Any-angle search or a nav mesh (the grid search and bake are unchanged). Remote heading smoothing (D5), wire or
`MovementState` changes. Turn-in-place, jump, idle or action layers in the blend (section 3). Moving
`ContinuousLocomotion` or `WowMovementInput` into the engine (Grimhollow #415). Grimhollow files, pin moves and tags.

## Decisions

Scores are design judgments from 1 to 10, higher is better.

### D1. Where straightening lives

| Option | Guard fidelity | Driver interplay | Surface | Total |
| --- | ---: | ---: | ---: | ---: |
| A. A planner wrapper in Movement, built by `MoveToRange` under a `RouteApproachOptions` flag | 9 | 9 | 8 | 26 |
| B. A post-pass inside `GridPathPlanner` (Navigation) | 4 | 8 | 7 | 19 |
| C. Per-tick look-ahead in the driver | 9 | 4 | 6 | 19 |

Select A. Only Movement holds the full guard (`AllowsSegment` checks footprint areas and every directed crossed edge,
`GroundNavigation.cs:65-91`). Navigation knows neither. The wrapper runs once per plan, so the follower, carry and
replan rules see an ordinary `NavPath`. C would repeat the scan every tick and fight follower consumption.

### D2. How wide the straight segment is checked

| Option | Collision safety | Straightening near walls | Cost | Total |
| --- | ---: | ---: | ---: | ---: |
| A. Centre line only | 5 | 9 | 9 | 23 |
| B. Centre line plus two side lines offset half a cell perpendicular | 8 | 7 | 7 | 22 |

Select B, at the cost of fewer shortcuts beside walls and fences. The bake proves the capsule at cell centres and on
centre-to-centre edges. A body on a straight line sits up to half a cell off a proven centre, and only B also requires
the neighbouring cells on both sides to be proven, which keeps the line inside proven cell triangles. A slide into a
refused cell pair under A would hold the body at `Following` with zero input for good, because the corridor test never
fires on a body that has not moved. D3 is the backstop either way.

### D3. A refused step on a straightened segment

The driver falls back to the raw cell route for one plan: it resets the follower and the wrapper returns the
unstraightened route on the next plan, then straightens again on the plan after a successful arrival or a replan.
This mirrors carry's fallback (`MoveToRange.Carry.cs:30-31`). Cost: one extra plan when it fires.

### D4. Where heading interpolation lives

| Option | Reuse | Reconcile correctness | Surface | Total |
| --- | ---: | ---: | ---: | ---: |
| A. A yaw axis in `ClientPrediction` beside position and height, opted in by `PredictionSettings` | 9 | 9 | 7 | 25 |
| B. Consumer lerps `PredictedState` against the previous tick | 4 | 5 | 9 | 18 |

Select A. Only `ClientPrediction` knows the inter-tick fraction, hard snaps and teleport epochs. Cost: up to one tick
(33 ms) of heading latency, the same latency position already has.

### D5. Remote heading stays with the consumer

Remote `MovementState` is discrete-sampled as one component, and `EntityRenderState.FacingYaw` already says
presentation may ease it (`EntityRenderState.cs:125-140`). Interpolating one field needs a per-field interpolator in
Replication, a larger change than this round. Ruling PT.2's rate-limited client ease toward the snapshot, together
with server turn speeds, covers creatures and remote players today. A follow-up issue records the engine option.

### D6. Shape and home of the blend

| Option | Second game fit | Coupling | Size | Total |
| --- | ---: | ---: | ---: | ---: |
| A. A clip-id solver in `KhaozEngine.Game.Render3D` plus a weighted pose helper in `Render3D` | 9 | 9 | 8 | 26 |
| B. A full animator owning clips, turn and jump states | 6 | 4 | 4 | 14 |
| C. Leave it in Grimhollow | 2 | 8 | 9 | 19 |

Select A. The solver sits beside `LocomotionStateMachine` and `LocomotionSpeedSync`, GPU-free and keyed by consumer
clip ids, so Ruinborne can use it with any clip set. It is compatible with either #415 answer: an engine
`ContinuousLocomotion` later composes it with turn and jump states, and a Grimhollow one calls it directly.

### D7. Direction weights

Angular linear weights between the two adjacent cardinals: at angle `a` into a 90 degree sector the far direction
gets `a / 90`, so a 45 degree diagonal is exactly half and half with no tie rule. Cartesian weights bend the midrange.
Gradient-band blending serves arbitrary sample sets, which four cardinals do not need.

### D8. Phase synchronisation

One shared gait phase per blend, advanced by `speed x dt / sum(weight x stride)`, and each clip sampled at
`frac(phase + SyncPhase)`, where `SyncPhase` is the clip's authored right-foot contact time. Independent per-family
phases (today's Grimhollow rule) put the blended feet out of step on a diagonal. Cost: consumers must supply a
contact time per clip. Grimhollow's forward loops record right contact at 0.25 (Grimhollow
`art-assets/characters/README.md:969`). Its strafe and backpedal contacts are unrecorded and are measured with the
engine's `FootPlant` inspection.

### D9. Riders

- #1269 joins. It is verified, about 20 lines in `DirectMoveToRange.AllowsStep`, and repeats the drop round's fix:
  judge the predicted end state with `CharacterMovement.ResolveSwimming` at the landed feet before admitting it.
- #1270 stays out. It is a lead whose cause is untraced, and #1253 needed a fixture, eight rulings and a deferral
  before its cause was known. It needs its own investigation round, ideally before Grimhollow P6.
- [#1272](https://github.com/APKiwiOrg/KhaozEngine/issues/1272) joins (added after the owner's second playtest): an
  opt-in tap tolerance on `PointerGesture` that measures straight-line distance from the press point with a short time
  grace, instead of the summed path length, so a click with a small wobble stays a tap. The strict path rule stays the
  default. An option caps or drops the catch-up step when the grace decides the press. Grimhollow raises its own
  thresholds meanwhile (its ruling PT.10).
- [#1273](https://github.com/APKiwiOrg/KhaozEngine/issues/1273) stays out until a live probe shows a capture warp jump
  on macOS.

## 1. Route straightening

`RouteApproachOptions.StraightenRoutes`, default false. When true, `MoveToRange` wraps its planner in an internal
`RouteStraightener(IRegionPathPlanner inner, NavSpace space, Func<Vector3, Vector3, bool> allowsSegment)`, so both the
`GroundNavigation` constructor and the caller-supplied planner constructor get it. Player routed walk-up rides the
same driver through `PlayerPathMovement`.

Rule, applied to every `Complete` and `Partial` path. `Unreachable` passes through unchanged:

- Waypoints are only dropped, never created, so every kept waypoint is still a cell centre on its layer.
- The anchor starts at the query's start feet. From the anchor, scan forward over `Walk` waypoints on the anchor's
  layer and keep the farthest waypoint `j` for which the centre line and both side lines pass `allowsSegment`. Stop
  the scan at the first refusal. Emit `j` and make it the anchor.
- Mandatory waypoints end every scan and are always kept: the final waypoint (region membership), every `Hop`
  waypoint and the waypoint before it (the hop start), and both ends of a layer change.
- Feet heights come from `space.Layers[layer].SurfaceHeightAt` at the waypoint's cell.
- The returned path keeps the inner path's status.

Interplay. Carry stays valid: a straightened route rarely has collinear waypoints left, and where it does the
follower consumes them as today. Every kept bend is still landed on exactly, so the strict accept radius and D1.1
hold. The near-field approach, stop ring, replan drift and corridor rules are unchanged. Aquatic routes follow the
same rule because `AllowsSegment` already judges float nodes.

Cost: per plan, one pass with at most `n + k` segment checks for `n` waypoints and `k` kept waypoints, each check
walking the cells it crosses three times, so worst case `O(n^2)` cell steps on an open field. About 1,800 cell steps
for a 60-cell route, run only when a plan is made. One extra waypoint list per plan.

## 2. Local heading interpolation

- `IPredictedState<TSelf>` gains default members `bool HasYaw => false`, `float Yaw => 0f` and
  `TSelf WithRenderState(Vector2 position, float vertical, float yaw) => WithRenderState(position, vertical)`.
  Existing implementers compile and behave unchanged.
- `PlayerMoveState` implements them over `Move.FacingYaw`.
- `PredictionSettings.InterpolateYaw`, default false. When true and `HasYaw`, `ClientPrediction` keeps a previous yaw
  beside `previousPredictedPosition` at the same four sites (`ClientPrediction.cs:162`, `:226`, `:277`, `:377`), and
  `RenderedState` passes `previous + WrapPi(current - previous) x frac`, the short way round.
- `Reset`, `Reseed`, a hard snap and a teleport epoch advance collapse the previous yaw onto the current one. A
  non-snap reconcile keeps the inter-tick phase and rebases only the target, as position does. There is no decaying
  yaw offset, because the authoritative heading comes from the same commands the client predicted.
- `EntityRenderState.FacingYaw` for the local entity becomes the interpolated heading with no further change, since
  it reads `LocalRenderState`.

## 3. Directional locomotion blend

Home: `KhaozEngine.Game.Render3D`, namespace `KhaozEngine.Game`, in new types. Presentation only, allocation-free in
steady state.

```csharp
public readonly record struct GaitClip(int ClipId, float FullWeightSpeed, float StrideMetres, float SyncPhase);

public sealed class DirectionalGaitSet
{
    // Each family has at least one member, ordered by strictly increasing FullWeightSpeed.
    public DirectionalGaitSet(ReadOnlySpan<GaitClip> forward, ReadOnlySpan<GaitClip> backward,
        ReadOnlySpan<GaitClip> left, ReadOnlySpan<GaitClip> right);
    public int ClipCount { get; }
}

public readonly record struct GaitSample(int ClipId, float Phase, float Weight);

public sealed class DirectionalLocomotionBlend
{
    public DirectionalLocomotionBlend(DirectionalGaitSet gaits, float blendSeconds = 0.15f, float movingSpeed = 0.05f);
    public static Vector2 BodyFrame(Vector3 worldVelocity, float facingYaw); // X right, Y forward, engine yaw convention
    public float Phase { get; }
    public float TravelWeight { get; }
    public int Advance(Vector2 bodyVelocity, float dt, Span<GaitSample> samples);
    public void Reset();
}
```

- Target weights: D7 splits between the two adjacent directions. Within a direction, speed splits linearly between
  the two members whose `FullWeightSpeed` brackets it, clamped to the slowest and fastest. A steady target has at
  most four nonzero clips.
- Each clip weight eases toward its target at `dt / blendSeconds`. Output weights are normalised to sum to one.
  Below `movingSpeed` targets hold their last values and `TravelWeight` eases to zero, so a stop fades out of the
  last gait. A W to S reversal crossfades through both families for `blendSeconds`, with feet kept in step by D8.
- Phase follows D8 with the eased weights. Zero travel holds it.
- `BodyFrame` uses the `MoveCommand.CameraYaw` convention (0 faces -Z, positive swings toward -X), so consumers stop
  hand-rolling the sine and cosine.

Pose helper in `KhaozEngine.Render3D`: `readonly record struct ClipSample(AnimationClip Clip, float Time, float Weight)`
and `AnimationSampler.SampleBlendInto(Skeleton, ReadOnlySpan<ClipSample>, JointPose[] into, JointPose[] scratch)`, a
running normalised lerp through `JointPose.Lerp`. One full-weight sample is bit-identical to `SampleInto`.

What the consumer keeps: idle, turn-in-place, jump and action layers. It feeds `TravelWeight` as its locomotion
layer weight, plays turn-in-place when that weight is low and the body yaws, and keeps calling `Advance` while
airborne so the gait phase continues into the landing. Grimhollow's mapping: forward walk, run, run-fast, backpedal
with members at its 65 percent pace, strafe walk and run per side, each with its recorded stride.

## 4. Test strategy

| Proof | Scenarios | Home |
| --- | --- | --- |
| Straightening rule | Default off byte-identical, open field collapses to one segment, convex obstacle keeps its corner, area mask blocks a shortcut, side line refusal beside a wall, hop and stair ends and final waypoint kept, `Partial` status kept, 128 to 192 m coordinates | Movement.Tests |
| Straightening driver | Off-axis walk arrives with heading changes only at kept bends and full bound between them, refused straight step falls back for one plan and arrives, carry and straightening together | Movement.Tests |
| Corner partial tick | Staircase route at 150 m coordinates moves on every tick, pinning the ruled-out stall | Movement.Tests |
| #1269 | Sloped shore grounded step past the swim-enter line ends `Blocked`, not `Suspended` | Movement.Tests |
| Heading interpolation | Default off bit-identical, half tick is half the turn, the 3.0 to -3.0 seam turns 0.28 rad, reset, reseed, hard snap and epoch collapse, non-snap reconcile keeps the phase, local `EntityRenderState.FacingYaw` | the Netcode prediction tests and the NetWorld client tests |
| Blend weights | Each cardinal is one clip, both diagonal pairs split half and half, continuity across every cardinal and the 180 degree seam, speed brackets and clamps, sum one, at most four in steady state | Game.Tests |
| Blend phase | Distance over blended stride, pure strafe reproduces its own stride, sync phases keep contacts aligned on a diagonal, zero travel holds, `dt` split invariance, ease timings, `BodyFrame` convention, validation, allocation-free `Advance` (`AllocSensitive`) | Game.Tests |
| Pose helper | One sample bit-identical to `SampleInto`, two samples equal `JointPose.Lerp`, zero total weight leaves the base | Render.Tests |
| Tap tolerance (#1272) | Default off identical, a 20 point wobble ending 2 points from the press is a tap, a 6 point move released within 150 ms is a tap, a slow 15 point drag crosses, nothing reaches the consumer before the crossing, the catch-up cap | the gesture tests |

No test needs Grimhollow assets. Focused runs per task and one full Release run at the finish. No local repetition.

## 5. Version and release

Additive public API, so the next free minor: 20.22.0 (tags re-read at `ee934bba5`, newest `v20.21.0`, nothing
staged). One version bump with one `CHANGELOG.md` entry. Grimhollow adopts a released pin.

## Rulings on the open questions

- Q1. Heading interpolation defaults off this round for every consumer, and Grimhollow opts in. Reason: a default flip
  changes every consumer's rendered output in a minor. Cost: a later minor flips the default once a second consumer
  has run it.
- Q2. D2 option B stands: shortcuts must pass the centre line and both side lines. Reason: a slide into a refused cell
  pair under the centre line alone holds a body in `Following` for good. Cost: staircases remain beside walls and
  fences.

## Task order

1. Corner partial tick fact (Movement.Tests), recording the ruled-out lead.
2. `RouteStraightener` with the D1 rule and its rule tests.
3. `RouteApproachOptions.StraightenRoutes`, the D3 fallback in `MoveToRange` and the driver tests.
4. #1269 fix and fact.
5. `IPredictedState` yaw members, `PlayerMoveState`, `PredictionSettings.InterpolateYaw`, `ClientPrediction` and
   tests.
6. `ClipSample` and `AnimationSampler.SampleBlendInto` with tests.
7. `GaitClip`, `DirectionalGaitSet`, `DirectionalLocomotionBlend` with weight and phase tests.
8. #1272 tap tolerance on `PointerGesture` with its tests.
9. Living docs (Movement, Navigation and `Game.Render3D` READMEs, `USING-KHAOZENGINE.md`, the
   `GroundNavigation.Planner` summary, design index row) and a follow-up issue for D5.
10. Version 20.22.0 with its changelog entry, full Release run, guards, merge and pack through the orchestrator.
