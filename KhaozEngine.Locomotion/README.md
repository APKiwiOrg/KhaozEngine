# KhaozEngine.Locomotion

Render-free character locomotion core. `CharacterMovement.Step` is the single movement function shared by the
local controller, the authoritative server sim, and client-side prediction - so local and networked movement
run identical code.

## API

Two overloads share one horizontal core (camera-relative WASD axis, normalised diagonals, walk/run speed,
optional slope gate via a ground-normal delegate):

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
  apart - the player path stays byte-for-byte identical to before.

- **`CameraRelativeDir(in MoveCommand) -> Vector2`** (14.9.0)
  The **commanded** camera-relative travel direction as a unit XZ vector (`Vector2.Zero` when idle, inside the
  1e-6 length-squared dead-zone), the exact direction the authoritative/prediction `Step` resolves the command to
  before it moves. For a consumer driving **explicit model facing** (facing the model toward where it is COMMANDED
  to travel, distinct from the direction the measured render position drifts): a commanded-facing yaw is just
  `MathF.Atan2(dir.X, dir.Y)` (world radians about +Y, 0 = +Z), gated on the vector being non-zero. Shares the ONE
  camera basis the step uses (`forward = (-sinYaw, -cosYaw)`, `right = (cosYaw, -sinYaw)`), so the public facing and
  the resolved movement can never drift apart.

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

- **`MoveCommand`** - movement intent: camera-relative XZ axis, run flag, camera yaw, jump bit.
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
  scales a jump's horizontal reach (jump HEIGHT is untouched). Assignment clamps to `>= 0`: a negative multiplier
  would reverse travel against the command, which is never what a modifier means.
  It is a **property, not a field like the rest of this struct**, deliberately: a struct field cannot have a
  non-zero default, so a raw field would make `default(MoveState)` (and every pre-existing initializer) a character
  frozen at 0 m/s. The backing store is the offset from 1, which makes the zero default mean "unmodified", exactly.
  On a networked player the server is the sole author (`WorldServer.SetSpeedScale` / `ShardedWorldServer.SetSpeedScale`,
  replicated as `MovementState.SpeedScaleQ`). A server-only NPC or a single-player controller just sets it.
- **`MovementMedium`** - the fluid medium at one world sample the medium provider returns: `WaterSurfaceY`,
  `InWater`, `WadeSpeedScale` (a per-sample zone multiplier, default 1). `default` / `MovementMedium.Dry` is dry land.
- **`MoveTuning`** - all speed and feel constants:
  `WalkSpeed` / `RunSpeed` / `CapsuleHalfHeight` / `CapsuleRadius` (default 0.4) / `StepHeight` (default 0.4) /
  `Gravity` / `JumpSpeed` / `MaxFallSpeed` / `CoyoteTime` / `JumpBuffer` / `AirControl` / `GroundedEpsilon` /
  `WadeStartDepthFraction` (default 0.15) / `WadeEndDepthFraction` (default 0.65) / `WadeMinSpeedScale` (default 0.45) /
  `SwimEnterDepthFraction` (default 0.65) / `SwimExitDepthFraction` (default 0.55) / `SwimSpeed` (default 2.5) /
  `SwimSurfaceSubmersionFraction` (default 0.6) / `SwimBuoyancyStiffness` (default 8).

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
