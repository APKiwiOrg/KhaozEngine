# KhaozEngine.Locomotion

Render-free character locomotion core. `CharacterMovement.Step` is the single movement function shared by the
local controller, the authoritative server sim, and client-side prediction - so local and networked movement
run identical code.

## API

Two overloads share one horizontal core (camera-relative WASD axis, normalised diagonals, walk/run speed,
optional slope gate via a ground-normal delegate):

- **`Step(Vector3, in MoveCommand, float, groundHeight, in MoveTuning, groundNormal?) -> Vector3`**
  Horizontal-only step. Y is clamped to `groundHeight(x, z) + halfHeight` every tick. No air, no vertical
  physics. Use for top-down or no-jump scenarios.

- **`Step(in MoveState, in MoveCommand, float, groundHeight, in MoveTuning, groundNormal?, IPhysicsWorld?, clampXz?) -> MoveState`**
  Vertical-physics step. Gravity, jump (coyote-time + jump-buffer), air control, land/clamp, and 3D collision
  resolution against an `IPhysicsWorld` (8.0.0+). The collision path uses a **substepped swept
  collide-and-slide** over `IPhysicsWorld.SweepCapsule` (8.4.0): a fast move can no longer tunnel through a
  thin one-sided wall, and the capsule never gets trapped inside a closed mesh. Walkable contacts (slope at or
  below the gate) are followed; steep contacts block and redirect tangentially. A **step-up probe** wires
  `MoveTuning.StepHeight`: stair treads and curbs below `StepHeight` are mounted without a jump. Depenetration
  via `ComputePenetration` is retained as a residual settle pass. Pass `world: null` for terrain-only
  (byte-identical to pre-8.4.0).

## Types

- **`MoveCommand`** - movement intent: camera-relative XZ axis, run flag, camera yaw, jump bit.
- **`MoveState`** - carried kinematic state: position, `VerticalVelocity`, `Grounded`, coyote/buffer timers.
- **`MoveTuning`** - all speed and feel constants:
  `WalkSpeed` / `RunSpeed` / `CapsuleHalfHeight` / `CapsuleRadius` (default 0.4) / `StepHeight` (default 0.4) /
  `Gravity` / `JumpSpeed` / `MaxFallSpeed` / `CoyoteTime` / `JumpBuffer` / `AirControl` / `GroundedEpsilon`.

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

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
