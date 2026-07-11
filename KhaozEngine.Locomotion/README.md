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
  wedging; and the step-up's forward advance is **capped to the walk step** (no fore-aft lurch) but floored at a small
  clearance so the mount always carries the footprint onto the tread past the depenetration pushback (so it starts
  cleanly from the flat floor at any angle/speed rather than vibrating on the first riser) - a stair climbs like a ramp.
  A low curb whose rise fits one tick's budget
  still mounts in one tick, and `MaxStepClimbSpeed <= 0` restores the instant snap. Depenetration
  via `ComputePenetration` is retained as a residual settle pass. Pass `world: null` for terrain-only
  (byte-identical to pre-8.4.0).

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
  `Swimming` (surface-swim flag, carried tick-to-tick for the enter/exit hysteresis).
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
```

Depends on `KhaozEngine.Primitives` and `KhaozEngine.Physics` (the `IPhysicsWorld` seam). No input, render,
or netcode dependency. Part of the `Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
