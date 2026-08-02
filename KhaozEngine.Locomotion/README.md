# KhaozEngine.Locomotion

Render-free character locomotion core. `CharacterMovement.Step` is the single movement function shared by the
local controller, the authoritative server sim, and client-side prediction - so local and networked movement
run identical code.

## API

Two overloads share one horizontal core (camera-relative WASD axis, normalised diagonals, walk/run speed,
optional direction-aware slope gate via a ground-normal delegate):

- **`Step(Vector3, in MoveCommand, float, groundHeight, in MoveTuning, groundNormal?, medium?) -> Vector3`**
  Horizontal-only step. Y is clamped to `groundHeight(x, z) + halfHeight` every tick. No air, no vertical
  physics. Use for top-down or no-jump scenarios.

- **`Step(in MoveState, in MoveCommand, float, groundHeight, in MoveTuning, groundNormal?, IPhysicsWorld?, clampXz?, medium?) -> MoveState`**
  Vertical-physics step. Gravity, jump (coyote-time + jump-buffer), air control, land/clamp, and 3D collision
  resolution against an `IPhysicsWorld` (8.0.0+). The collision path uses a **substepped swept
  collide-and-slide** over `IPhysicsWorld.SweepCapsule` (8.4.0): a fast move can no longer tunnel through a
  thin one-sided wall, and the capsule never gets trapped inside a closed mesh. Walkable contacts (slope at or
  below the gate) are followed; steep contacts block and redirect tangentially. A **step-up probe** wires
  `MoveTuning.StepHeight`: stair treads and curbs below `StepHeight` are mounted without a jump. Step-up climbing
  rises at a **bounded vertical rate** (`MoveTuning.MaxStepClimbSpeed`, default 3.5 m/s), so a stair run ascends at
  a steady walking pace instead of snapping up a whole riser per tick. The probe climbs **perpendicular to the riser
  edge** (straight up the stairs, not along the raw angled move), so an off-axis approach ascends square to the treads
  and merely shaves the lateral against a shaft wall like a normal flat-ground slide instead of leaking sideways and
  wedging; and the step-up's forward advance is **capped to the walk step** (no fore-aft lurch) and then validated
  against the underfoot support fan (10.66.0): a stair run's capped pose stays supported on the treads it spans, so
  the cap holds and the climb stays smooth, while a deep single riser's capped pose has no genuine floor below the
  feet, so the mount instead commits the step-up probe's already-proven landed seat, letting depenetration lift the
  capsule up onto the step rather than push it back off. The fan counts only a support that sits genuinely below the
  feet (a hit closer than the skin depth is the feet embedded in a solid riser box, not resting on it, and is
  rejected), so a solid building or doorway riser reads unsupported and mounts too, not just a one-sided tread gap
  (10.67.0). At a staircase **base**, where the terrain sits within a step of the tread, a footprint that straddles a
  tread shallower than the capsule diameter can make the downward support sweep miss the tread entirely (it grazes the
  vertical riser front); when that happens the same radius-less ray fan finds the tread the sweep cannot and seats the
  support on it, so the bottom step mounts cleanly at any approach speed and arrival phase instead of collapsing a
  whole riser back to the ground below and bobbing there. Monotone either way, so it starts cleanly from the flat floor at any
  angle/speed instead of vibrating on the first riser - a stair climbs like a ramp. A per-riser backward shove during
  a paced climb is also caught by a monotone-forward hold (grounded climb ticks only, a jump takeoff is never held).
  A low curb whose rise fits one tick's budget
  still mounts in one tick, and `MaxStepClimbSpeed <= 0` restores the instant snap. Depenetration
  via `ComputePenetration` is retained as a residual settle pass. Pass `world: null` for terrain-only
  (byte-identical to pre-8.4.0).

- **`StepTowards(in MoveState, Vector2 worldDir, bool run, float, groundHeight, in MoveTuning, groundNormal?, IPhysicsWorld?, clampXz?, medium?) -> MoveState`** (10.64.0)
  The world-space kinematic step for **server-authoritative, non-player agents (enemy NPCs)**. It drives the SAME
  collision resolution the player gets - swept collide-and-slide + `StepHeight` step-up against the `IPhysicsWorld`,
  the analytic terrain support floor, the `groundNormal` slope gate, and the `clampXz` bounds - but from a
  **world-space steering direction** instead of a camera yaw. `worldDir` is an XZ direction whose length scales speed
  in `[0,1]` (unit = full speed, shorter = a slower saunter, longer clamped to full; near-zero = idle). Per-agent
  capsule radius / half-height / walk-run speed come from `MoveTuning`, so different creatures get different sizes and
  speeds with no extra plumbing. **No jump bit** (NPCs do not jump in v1) and **no client prediction** (AI is
  server-only). Both the camera-relative player `Step` and this world-space `StepTowards` resolve their input to one
  shape (a unit direction + a speed fraction) and share a single collision core, so player and AI can never drift
  apart - the player path stays byte-for-byte identical to before. Since 17.26.0 it also turns
  `MoveState.FacingYaw` toward the steering direction, so an NPC's heading is authoritative with no camera and no
  extra plumbing (the AI path never has a `FaceCamera` target: there is no camera on it).

- **`CameraRelativeDir(in MoveCommand) -> Vector2`** (14.9.0)
  The **commanded** camera-relative travel direction as a unit XZ vector (`Vector2.Zero` when idle, inside the
  1e-6 length-squared dead-zone), the exact direction the authoritative/prediction `Step` resolves the command to
  before it moves. For a consumer driving **explicit model facing** (facing the model toward where it is COMMANDED
  to travel, distinct from the direction the measured render position drifts): a commanded-facing yaw is just
  `MathF.Atan2(dir.X, dir.Y)` (world radians about +Y, 0 = +Z), gated on the vector being non-zero. Shares the ONE
  camera basis the step uses (`forward = (-sinYaw, -cosYaw)`, `right = (cosYaw, -sinYaw)`), so the public facing and
  the resolved movement can never drift apart.

- **`WrapYaw(float) -> float`** and **`FacingYawOf(Vector2 dirXz) -> float`** (17.26.0)
  The two public conversions for `MoveState.FacingYaw`. `WrapYaw` reduces any angle to the canonical
  `[-pi, pi)` (low end inclusive), returning an already-canonical angle **bit-identically**, which is what makes
  the "the heading converges to `CameraYaw` exactly" contract true rather than approximate. `FacingYawOf` is the
  heading of a world-space XZ direction (`X` = world +X, `Y` = world +Z, matching `CameraRelativeDir`'s output),
  and it is the exact inverse of the camera basis the step resolves a command in. A non-finite angle and a zero
  vector both yield 0, which is a legal heading (facing -Z) rather than a sentinel.

### Slope gate (direction-aware since 17.26.0)

Supply a `groundNormal` delegate and the step refuses to CLIMB ground steeper than `MoveTuning.MaxSlopeRadians`.
Until 17.26.0 it refused ANY move whose destination normal was too steep, whichever direction the tick was
travelling, so on the analytic-terrain path a cliff edge blocked walking OFF it exactly as it blocked walking INTO
it. The rule is now:

    blocked iff steep(destination normal) && rise > max(1 mm, travel * tan(MaxSlopeRadians))

`rise` is the destination ground height minus the **lower of the character's FEET (the capsule centre minus
`CapsuleHalfHeight`) and the ground height under the current column**, and `travel` is the tick's intended
horizontal distance, so the ascent allowance is a **gradient, not a height**:
it asks whether this tick climbs faster than the steepest walkable ramp would over the same ground, which makes
the answer independent of speed and of tick rate. A fixed allowance could not, and the version that tried let a
slowed character, a short steering vector or a high tick rate walk up an arbitrarily steep face a fraction of a
centimetre per tick. The 1 mm term is only a noise floor for a near-level traverse across a steep face. Neither
term is a new knob: `tan(MaxSlopeRadians)` is the existing gate read as a gradient.

A descent or a level traverse now falls through, the support floor finds no walkable ground, the character goes
airborne, and gravity does the rest. Flying into a face whose ground stands above the feet is still refused, so an
XZ can never be committed under terrain.

**Vertical motion buys no admission** (17.26.1, [#440](https://github.com/APKiwiOrg/KhaozEngine/issues/440)). The
rise reference is the LOWER of the feet and the ground under them because the feet alone were inflatable: a jump
raises them, so near the apex a steep face's local ground stood level with the feet, the rise read as ~0, the drift
onto the face was admitted, the ground clamp seated the character on it, and the next jump repeated - a jump height
of free climb per cycle up a sea cliff no walk could enter. Any airtime did it, so a character merely falling past
a face while steering into it was seated partway up. The floor of the two terms cannot be raised by leaving the
ground. Grounded motion is unchanged (the feet ARE the ground, so the minimum is a no-op), genuine descents stay
open at any airtime (a destination column below the current one is below both terms), and because the gate only
ever got more conservative the anti-tunnel property is untouched. One consequence off the exploit path: a character
elevated on a PROP measures its rise from the terrain under the prop, not from the prop top, so stepping off a prop
straight onto a steep face is now refused - the analytic gate reads `groundHeight`, and prop support is not visible
to it.

It applies identically to the grounded path, the airborne-momentum path and the horizontal-only overload, and it
needs no new wiring (it reads the `groundHeight` delegate every step already takes). **A game that used the gate
as a cliff guardrail now gets real falls** - that is the fix, and it is a behaviour change rather than an opt-in.

### Movement medium (wading)

Both overloads take an **optional medium provider** `medium: (x, z, feetY) -> MovementMedium`. It is a **pure,
deterministic read of the game's world** (a lake plane, a river, a swamp) that the game supplies **on BOTH heads**
(the authoritative server tick and the client's prediction replay), exactly like the `groundHeight` delegate: the
engine never computes water itself. When a sample reports `InWater`, horizontal speed is scaled by a **depth wade
ramp** - full speed at ankle depth (`MoveTuning.WadeStartDepthFraction`) down to a floor
(`MoveTuning.WadeMinSpeedScale`) at chest depth (`MoveTuning.WadeEndDepthFraction`), where depth is
`WaterSurfaceY - feetY` as a fraction of body height (`2 * CapsuleHalfHeight`). The medium's own `WadeSpeedScale`
composes as a further per-sample multiplier (a swamp zone dial). **A null provider (or a dry sample) is
bit-identical to the pre-medium behaviour.** `CharacterMovement.WadeSpeedScale(x, z, feetY, tuning, medium)`
exposes the same scale for callers that predict or echo the wade factor (floored at 0, **uncapped above** so a
zone scale > 1 lifts the result past 1).

### Surface swim v1

Past the wade band the same seam drives **surface swim** (vertical-physics `Step` only). Submersion reaching
`MoveTuning.SwimEnterDepthFraction` (default 0.65, chest, where the wade ramp bottoms out) flips the character into
swimming; it exits below the LOWER `MoveTuning.SwimExitDepthFraction` (default 0.55) or on leaving the water - the
enter/exit gap is a **hysteresis band** (no flicker at the boundary), carried on `MoveState.Swimming`. While
swimming, gravity and ground-snap are suspended, the capsule **settles to a buoyancy waterline**
(`MoveTuning.SwimSurfaceSubmersionFraction`, default 0.6) via an exact analytic **critically-damped** approach
(`MoveTuning.SwimBuoyancyStiffness`, default 8 - unconditionally stable, no oscillation), horizontal travel is
`MoveTuning.SwimSpeed` (default 2.5, the zone `WadeSpeedScale` still composing), and jump is a **hop-out in
near-shore shallows only** (fires the ordinary jump + drops swim when submersion is within the exit band; ignored
in deep water). `CharacterMovement.ResolveSwimming(wasSwimming, medium, feetY, tuning)` exposes the pure enter/exit
decision. **A null provider never engages swim.** The swim flag replicates via NetWorld's `MovementState.Swimming`
(a breaking wire change: `MoveProtocol.WireProtocolVersion` -> 3).

## Types

- **`MoveCommand`** - movement intent: camera-relative XZ axis, run flag, camera yaw, jump bit, and (17.26.0) the
  `FaceCamera` flag, which asks the character to face `CameraYaw` instead of its travel direction. `FaceCamera`
  changes `MoveState.FacingYaw` only and never the position, so a strafing character keeps its body pointed at the
  camera and - the case that is impossible without it - a character with NO movement input can turn on the spot.
  `false` (the default, and what every pre-facing construction site produces) is the pre-facing behaviour exactly.
- **`MoveState`** - carried kinematic state: position, `VerticalVelocity`, `Grounded`, coyote/buffer timers,
  `Swimming` (surface-swim flag, carried tick-to-tick for the enter/exit hysteresis), and `SpeedScale` (see below).
  Also the stair-glide
  step-climb signals: `ClimbRate` (signed step-climb rate in m/s, replicated quantized as
  `MovementState.ClimbRateQ`, positive on a paced stair-climb run and negative on a stepped-down descent, 0
  when not on a step climb) and `StepDeltaY` (sim-local, not replicated: the signed vertical delta a single
  discrete step commits, read once by the local owner at the client-prediction boundary). Both are what a
  presentation layer consumes to glide/ease the drawn feet on stairs, see the "Animated characters" section
  of `docs/USING-KHAOZENGINE.md` for the full `CharacterController3D.ClimbRate` / `.StepDeltaY` consumer
  contract. `ClimbRateEwma` is a sim-local (not replicated) smoothing average `ClimbRate` is stamped from on
  a run tick, with no consumer of its own.
- **`MoveState.SpeedScale`** (14.26.0) - per-entity HORIZONTAL speed multiplier: haste (`> 1`), slow (`< 1`),
  root (`0`), unmodified (`1`, the default). A movement INPUT the step reads and carries through unchanged, and nothing
  in the sim derives or decays it. It multiplies INTO the existing speed product rather than replacing any of it, so
  it composes with the grounded/`AirControl` term and the medium's `WadeSpeedScale`, applies while swimming, and
  scales a jump's horizontal reach (jump HEIGHT is untouched). Under `MoveTuning.AirMomentum` the airborne
  composition drops the `AirControl` term (air control becomes a steering authority there, not a speed scale) and
  the multiplier scales the commanded speed the arc steers toward. Assignment clamps to `>= 0`: a negative
  multiplier would reverse travel against the command, which is never what a modifier means.
  It is a **property, not a field like the rest of this struct**, deliberately: a struct field cannot have a
  non-zero default, so a raw field would make `default(MoveState)` (and every pre-existing initializer) a character
  frozen at 0 m/s. The backing store is the offset from 1, which makes the zero default mean "unmodified", exactly.
  On a networked player the server is the sole author (`WorldServer.SetSpeedScale` / `ShardedWorldServer.SetSpeedScale`,
  replicated as `MovementState.SpeedScaleQ`). A server-only NPC or a single-player controller just sets it.
- **`MoveState.CommandedVelocity`** (16.0.0, was the `float CommandedSpeed` field in 14.27.0) - sim-local step
  OUTPUT (not replicated): the unconstrained horizontal VELOCITY in m/s the step actually commanded this tick,
  before the slope gate, collision, or the play-area clamp denied any of it. Its magnitude is the whole speed
  product (walk/run or `SwimSpeed`, times air control, the wade ramp, the zone scale, and `SpeedScale`), so with
  nothing denying the move the step travels exactly `CommandedVelocity * dt`. `(0,0)` on an idle tick.
  `CommandedSpeed` survives as a computed readonly property (`CommandedVelocity.Length()`), so reads still compile
  and writes do not. It exists so the server-side movement-anomaly check can measure ONLY the denial: rebuilding
  that product downstream is what made a swimming or wading player read as a speed hacker. It is a vector and not
  a scalar because the check needs the direction of travel too, and under `MoveTuning.AirMomentum` that is the
  conserved `HorizontalVelocity` rather than the input. Same "the sim exports what it knows" pattern as
  `ClimbRate`.
- **`MoveState.HorizontalVelocity`** (16.0.0) - `Vector2` (XZ, m/s), the CARRIED airborne inertia and new
  replicated state (`MovementState.HorizontalVelocityXQ` / `HorizontalVelocityZQ`). Maintained on EVERY tick
  regardless of `MoveTuning.AirMomentum` and consumed only when it is on, so with momentum off the field is
  written and never read. What is stored is the step's intended velocity CLIPPED to what the collision resolve
  delivered, projected along its own direction and clamped into `[0, |intended|]`: free flight leaves it exactly
  untouched, a head-on wall clips it to ~0, a glancing wall sheds magnitude and keeps direction, and no
  depenetration nudge or play-area clamp can ever inject speed into it. `SwimStep` replaces it with the swim's own
  commanded velocity, so a flight into water drops its arc at the waterline. `default` (zero) is "carrying
  nothing".
- **`CharacterMovement.IntendedHorizontalTargetAtSpeed(position, cmd, dt, speed)`** (14.27.0) - the unconstrained
  target a command reaches in one step at an EXPLICIT speed. The existing `IntendedHorizontalTarget(..., tuning,
  speedScale)` now delegates to it, so the two share one camera basis. Correct for any caller whose travel
  direction is its input direction, which is every grounded step and every airborne one without momentum.
- **`CharacterMovement.IntendedHorizontalTargetAtVelocity(position, velocity, dt)`** (16.0.0) - the vector form,
  `position.XZ + velocity * dt`. No command and no camera basis, because the direction comes from the velocity.
  Pair it with `CommandedVelocity`. This is the form the movement-anomaly check uses.
- **`MovementMedium`** - the fluid medium at one world sample the medium provider returns: `WaterSurfaceY`,
  `InWater`, `WadeSpeedScale` (a per-sample zone multiplier, default 1). `default` / `MovementMedium.Dry` is dry land.
- **`MoveState.LandingImpactSpeed`** (17.26.0) - sim-local step OUTPUT (not replicated): the DOWNWARD speed in m/s,
  non-negative, that this tick's landing erased, captured before the ground contact zeroes `VerticalVelocity`. Set
  on exactly the tick a character transitions airborne to grounded and 0 on every other tick, so it is a per-tick
  EVENT rather than carried state. It is the authoritative fall-damage input, following the `StepDeltaY` precedent
  of exporting the fact the sim already computed: the speed is gone by the time anything downstream can read the
  state, so without it a game has to finite-difference a position across the very tick the ground clamp moved it.
  Inherently capped by `MaxFallSpeed`, which is terminal velocity and therefore physical. A landing that a
  BUFFERED JUMP re-launches on the same tick still reports its impact (so `Grounded` may be false while this is
  nonzero), because suppressing it would let a bunny-hop cancel fall damage. Swimming never fabricates one (a swim
  tick is never grounded). A spawn or teleport reports about `Gravity * dt` on its first tick, honestly, since
  `default(MoveState).Grounded` is false, so **consumers should threshold** rather than treat any nonzero value as
  a fall. The server-side read path is NetWorld's `WorldServer.OnAfterTick` / `ShardedWorldServer.OnAfterTick`.
- **`MoveState.FacingYaw`** (17.26.0) - the CARRIED heading in radians, in the same convention as
  `MoveCommand.CameraYaw`: 0 faces world -Z, a positive angle swings toward -X, canonical range `[-pi, pi)` with
  the low end inclusive. See "Authoritative facing" below. It affects NO position output, which is what makes
  every existing game bit-identical across the feature. Replicated as `MovementState.FacingYawQ`.
- **`MoveTuning`** - all speed and feel constants:
  `WalkSpeed` / `RunSpeed` / `CapsuleHalfHeight` / `MaxSlopeRadians` / `CapsuleRadius` (default 0.4) /
  `StepHeight` (default 0.4) /
  `Gravity` / `JumpSpeed` / `MaxFallSpeed` / `CoyoteTime` / `JumpBuffer` / `AirControl` / `GroundedEpsilon` /
  `WadeStartDepthFraction` (default 0.15) / `WadeEndDepthFraction` (default 0.65) / `WadeMinSpeedScale` (default 0.45) /
  `SwimEnterDepthFraction` (default 0.65) / `SwimExitDepthFraction` (default 0.55) / `SwimSpeed` (default 2.5) /
  `SwimSurfaceSubmersionFraction` (default 0.6) / `SwimBuoyancyStiffness` (default 8) /
  `MaxStepClimbSpeed` (default 3.5) / `AirMomentum` (default false) / `AirBrakeAccel` (default 0) /
  `FacingTurnSpeed` (default `float.PositiveInfinity`, which snaps).

## Airborne momentum (16.0.0, opt-in)

Off by default. With `MoveTuning.AirMomentum = false` a character in free flight has no inertia: the horizontal is
recomputed from the command every tick, so a mid-air `SpeedScale` change collapses a committed arc and releasing
input stops horizontal travel dead. That is the pre-16.0.0 model and it is what every game gets until it opts in.

With `AirMomentum = true` the AIRBORNE step flies the carried `MoveState.HorizontalVelocity` instead. A jump at
speed S travels its whole arc at S whatever the command does afterwards, releasing input holds both speed and
direction, and pressing into the arc can ACCELERATE it but never brake it. **Grounded motion is untouched either
way** - it stays instant-to-target with no acceleration and no friction. `AirControl` becomes the STEERING
authority over the direction of travel rather than a speed scale, which gives it a reading it never had: `1` is
still full control (an instant 180 mid-flight, still at the carried speed), and `0` is now a true ballistic arc
rather than "frozen horizontally in mid-air".

`MoveTuning.AirBrakeAccel` (m/s^2, default 0) bleeds a conserved speed down toward a STRICTLY SLOWER commanded
speed, stopping there and never going below it. `0` is pure conservation. It is there for a root or a snare
landing mid-flight, and it is the one knob that dials back toward the old feel without turning momentum off. It
never accelerates: braking is its job alone and steering is the blend's, and the two deliberately do not overlap.

Networked play needs nothing extra - the carried velocity replicates and survives a reconcile - but it is a wire
break, see `KhaozEngine.NetWorld/README.md`. Rationale and the full per-tick resolve are in
`docs/design/AIRBORNE-MOMENTUM-DESIGN-2026-07-26.md`.

## Authoritative facing (17.26.0)

Which way a character POINTS is now part of the movement model rather than something each presentation layer
re-derived from a position delta. That derivation cannot turn a stationary character at all, and it reads a fast
diagonal or a slope walk as a turn that never happened.

`MoveCommand.FaceCamera` selects the target: with it set the character turns toward `CameraYaw` whatever the move
axis is doing, and without it toward the yaw of the commanded world-space move direction while there is input, or
the current heading when there is none (an idle character holds its heading rather than snapping to a default).
The result is `MoveState.FacingYaw`, carried tick to tick, radians, **0 faces world -Z and a positive angle swings
toward -X** - the basis the step already resolves a camera-relative command in (forward is
`(-sin yaw, 0, -cos yaw)`), so a character walking straight forward under camera yaw `y` faces exactly `y`.
Canonical range `[-pi, pi)`, low end inclusive. Convert with `CharacterMovement.FacingYawOf` and
`CharacterMovement.WrapYaw`. A consumer whose gameplay basis is the opposite converts at its own boundary, once.

`MoveTuning.FacingTurnSpeed` (rad/s) rate-limits the turn, always along the SHORTEST ARC, and lands exactly on the
target on the tick the remaining gap fits inside one step's budget - so a rate changes how long a turn takes and
never where it ends. The default `float.PositiveInfinity` SNAPS, deliberately rather than some plausible finite
rate: before facing became authoritative state a consumer pointed its model straight at `CameraRelativeDir` with
no smoothing, so infinite is the feel every existing game already has. A finite value (2 to 10 rad/s is the usual
range) leans the body into its turns and does so identically on the server, in client prediction and on every
remote, because the turn is part of the authoritative step rather than a smoother each end runs its own version
of. A value of 0, which is what a bare `default(MoveTuning)` reads, FREEZES the heading rather than meaning "no
limit": treating 0 as unlimited would make the un-configured case the most aggressive setting there is.

Facing is an OUTPUT and nothing else. No position, velocity or grounded value is derived from it anywhere, so
every existing game is bit-identical on position across the feature. Networked play needs the heading on the wire
(it is carried state that the next tick turns FROM), which is a wire break: see `KhaozEngine.NetWorld/README.md`.
Rationale and the phase plan are in `docs/design/PHYSICS-LOCOMOTION-DESIGN-2026-08-02.md`.

## Usage

```csharp
using KhaozEngine.Locomotion;

// Horizontal-only (top-down / no air):
Vector3 next = CharacterMovement.Step(pos, new MoveCommand(move, run, cameraYaw), dt,
                                      terrain.GroundHeight, MoveTuning.Default);

// Vertical physics (gravity + jump + swept 3D collision):
MoveState s = CharacterMovement.Step(s, new MoveCommand(move, run, cameraYaw, jump), dt,
                                     terrain.GroundHeight, MoveTuning.Default,
                                     groundNormal: terrain.GroundNormal, world: physicsWorld);
// s.Position, s.VerticalVelocity, s.Grounded

// Server-authoritative enemy NPC: same collision as the player, steered by a world heading.
Vector2 toTarget = Vector2.Normalize(new Vector2(target.X - a.Position.X, target.Z - a.Position.Z));
a = CharacterMovement.StepTowards(a, toTarget, run: true, dt,
                                  terrain.GroundHeight, enemyTuning,
                                  groundNormal: terrain.GroundNormal, world: physicsWorld,
                                  clampXz: bounds.Clamp);
```

Depends on `KhaozEngine.Primitives` and `KhaozEngine.Physics` (the `IPhysicsWorld` seam). No input, render,
or netcode dependency. Part of the `Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
