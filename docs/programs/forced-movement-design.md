# Forced movement design

The approved direction for the unbuilt milestones of the [forced movement program](forced-movement.md).
It does not describe shipped behaviour. Decisions it relies on carry their tracker ids.

## What exists to build on

- `MovementCommitment` (KhaozEngine.Locomotion) is a server-authored move held in `MoveState`. It rides
  `MovementState` to the owner's reconcile basis and to observers, and `ClientPrediction` replays it with
  input ignored, so a client does not fight it. It is ballistic only. The direction is fixed at the start,
  a vertical launch is required, and it ends on landing. Phases are Preparing, Airborne, Recovering,
  Completed and Aborted. End reasons include Blocked, TimedOut, EnteredWater and Superseded.
- `ShardedWorldServer` and `WorldServer` start and abort commitments through queued admin commands and
  raise `MovementCommitmentEnded`. The movement anomaly check exempts commitments. Nothing in production
  calls them yet.
- `CharacterMovement.StepCore` runs `PrepareCommitmentTick` and `FinishCommitmentTick` on the Legacy
  controller path. `ExplicitCharacterMovement` shares the same prepare step.
- The planners (`IRegionPathPlanner`, `GridPathPlanner`, `GroundNavigation.Planner`) answer synchronously
  with a `NavPath` of waypoints. A path has no length field, and budget exhaustion returns `Partial`.
  `PathFollower` exposes the active path and waypoint index but no remaining distance. `MoveToRange`
  returns Suspended while a commitment is active.

## M1 Pathed charge

### Locomotion: ground travel

- `MovementCommitment` gains a kind (`Ballistic`, `Ground`) and `DistanceRemaining` for ground travel, a
  `Travelling` phase, and end reasons `Arrived` and `TargetLost`. Ballistic behaviour is unchanged.
- After Preparing, `Travelling` sets the horizontal velocity to `Direction * HorizontalSpeed`, scaled down on
  the last tick so the step lands exactly on the remaining distance. Normal gravity and ground following
  apply (slopes, steps, collide and slide). Input and the speed scale are ignored (FM-D7) and the body faces
  along `Direction`.
- Achieved horizontal progress is subtracted from `DistanceRemaining`. Reaching zero ends with `Arrived`,
  through Recovering when a recovery time is set.
- Progress under a fraction of the intended step for several consecutive ticks ends with `Blocked`, so a
  single graze off a corner does not cancel the charge. Entering water and the timeout behave as today.
- The server may re-aim `Direction` and refresh `DistanceRemaining` each tick while the commitment is
  active. Prediction replays whatever the latest basis carries (FM-D5).

### NetWorld: begin, steer and end

- `MovementCommitmentRequest.ForGroundTravel(direction, speed, distance, preparationSeconds,
  timeoutSeconds, recoverySeconds)` starts a ground commitment through the existing begin call.
- A steering call takes the commitment sequence, a direction and the remaining distance. An end call takes
  the sequence and a reason (`Arrived`, `TargetLost`). Both queue like the existing admin commands, so a
  game calling them from its before-tick hook affects the same tick's movement step. A stale sequence is
  ignored.
- NetWorld takes no Navigation dependency (FM-D6).
- Wire generation 15. The new commitment fields append to the end of the `MovementState` payload, so the
  persisted layout change is a plain append with a `BuiltinBlobLayout` row and a rewriter case per the
  existing ladder. Mixed generations are refused at connect as today.

### Movement: pursuit planning and steering

- A new pursuit type plans with an `IRegionPathPlanner` toward the target's reach region, built the way
  `MoveToRange` builds its goal region from a `ReachTarget` and a range.
- Admission: the target is within the straight-line trigger range (FM-D2), the plan is `Complete`, every
  waypoint is a walk waypoint (no hop or drop), and the summed waypoint length is within the path-length
  cap (FM-D3). It returns a status the game maps to its refusal (`Ready`, `OutOfRange`, `NoPath`,
  `TooLong`) and the plan's length.
- Per tick: re-plan when the target has moved past a threshold or the body has left the route corridor.
  Return the direction toward the next waypoint, the remaining path length, and a status of `Travelling`,
  `Arrived` (within the stop distance), `TargetLost`, `NoPath` or `TooLong` (FM-D4).
- The same type serves a game that drives an NPC's `MoveState` commitment directly.
- `PathFollower` and `MoveToRange` are not edited (FM-D6). The pursuit builds on whatever the path follower
  work leaves on main (FM-D8).

### Game wiring the docs must show

On ability use, check admission and refuse with the returned status, else begin the ground commitment
with the plan's length. Each tick before movement, steer the pursuit and pass its direction and remaining
length to the steering call, or end with `Arrived` or `TargetLost`. Apply the strike on
`MovementCommitmentEnded` with `Arrived`. Refuse the charge while rooted (FM-D7). The server needs a
navigation profile sized for the charging actor's capsule.

### Tests

- Locomotion: ground travel follows a slope and a step, stops exactly on its distance with no overshoot,
  survives a single corner graze, ends Blocked against a wall over consecutive ticks, re-aims mid travel,
  and leaves ballistic commitments byte-identical.
- NetWorld: the wire round trip at generation 15, the persisted payload migration from 14, steering and
  ending by sequence, and a loopback test where a predicting client follows a steered charge around a
  corner with no correction above a small bound and no overshoot at arrival.
- Movement: admission refuses out of range, no path, a partial path, a hop or drop route and a route over
  the cap (the cliff case), and per-tick steering re-plans for a moving target and reports each end status.

## M2 Release and consumer note

Release through the normal ritual with the owner's approval of the exact tag, then post a short note on
the consumer issue from the tracker's links.

## Later consumers

Knockback is ground travel with a fixed direction and distance and no steering (FM-D9). A leap is the
existing ballistic commitment.
