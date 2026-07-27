# KhaozEngine.Physics.Bepu

BepuPhysics v2 backend for the [KhaozEngine.Physics](../KhaozEngine.Physics) seam.
**`BepuPhysicsWorld : IPhysicsWorld`** over BepuPhysics 2.4.0 (pure managed, Apache-2.0, no native
libraries) with a single-threaded, deterministic `Simulation` (null dispatcher, fixed solve description,
4 substeps, uniform gravity applied in the pose integrator). Opt-in and in NO umbrella: consumers depend on
the dependency-free seam and add this backend explicitly, the same pattern as `Netcode.LiteNetLib` or
`WorldStore.Sqlite`. The only assembly in the engine that references BepuPhysics.

The default constructor uses Earth gravity `(0, -9.81, 0)`; `new BepuPhysicsWorld(Vector3.Zero)` gives the
static-only, non-falling behaviour. The solver `substepCount` (default 4) and `velocityIterationCount`
(default 8) are optional constructor parameters. Both are part of the determinism fingerprint for any world
containing dynamic bodies: a consumer with a determinism tripwire fingerprinted on the pre-dynamics backend
(one substep) can pin the old integrator with `new BepuPhysicsWorld(gravity, substepCount: 1)`. Changing
either value shifts the bit-exact result legitimately.

## What it does

- **`AddStatic`/`RemoveStatic`** - converts every seam shape (sphere/capsule/box/cylinder/convex
  hull/triangle mesh/compound) to Bepu shapes. Removal frees the shape from the shape pool too, so a
  streaming load/unload cycle does not grow it unbounded.
- **`AddDynamic`/`RemoveDynamic`** - adds a dynamic rigid body: a convex primitive, hull, or compound of
  convex leaves (NOT a triangle mesh: no closed volume, so no inertia). Inertia is derived from the shape
  and the `DynamicBodyDescription.Mass`; base-aligned cylinder/hull shapes use `BuildDynamicCompound` so
  they stay base-aligned exactly like statics. Mass &lt;= 0 makes a kinematic (infinite-mass) body. Removal
  frees the shape from the pool too (`RecursivelyRemoveAndDispose`), like statics.
- **`GetDynamicPose`/`GetDynamicVelocity`/`SetDynamicVelocity`/`IsAwake`** - pose/velocity query and set
  (setting velocity wakes the body); `IsAwake` reflects Bepu's natural island sleeping (a settled body
  sleeps and reports false until disturbed).
- **Restitution** - Bepu 2.4 has no restitution coefficient (its contact recovery velocity only recovers
  penetration, which yields a constant-height limit cycle, not a decaying bounce). So `BepuPhysicsWorld.Step`
  applies restitution as an explicit, deterministic, APPROXIMATE game-feel bounce (not a true coefficient of
  restitution): a restitutive dynamic body whose approach speed a contact arrested this step is returned
  `restitution x` that speed in the opposite direction, so the bounce decays geometrically with restitution.
  It is a bounded post-solve reflection (it can over-restitute by up to the contact recovery velocity, and the
  apex is not analytically pinned - a hard impact spreads its arrest over 2-3 substeps). Non-restitutive bodies
  are untouched.
- **`AddConstraint`/`RemoveConstraint`** - joint constraints mapped to the verified BepuPhysics 2.4 constraint
  types (see the joints section below). `RemoveConstraint` is a safe no-op on a double-remove or a handle whose
  body was already removed. Removing a constrained body tears down its constraints first (Bepu corrupts if a body
  is removed with a live constraint), so a body's joints never dangle.
- **`SetConstraintTarget`** - retarget a powered joint's motor/servo. Re-describes ONLY the drive's Bepu
  constraint via `Solver.ApplyDescription` (a stack-built description, verified allocation-free: 1000 calls = 0
  bytes), leaving the joint's other constraints untouched, so a game can drive a servo goal every frame without GC
  pressure. Throws on a stale handle or a motorless joint.
- **`Step(dt)`**, **`Raycast`**, **`SweepCapsule`**, **`ComputePenetration`** - the last is one
  CollisionBatcher manifold query over every shape type, deepest contact wins.
- **`Origin`/`CanRebase`/`Rebase(newOrigin)`** (floating origin) - `CanRebase` is true here. A rebase is a bulk of
  direct pose writes plus broadphase refits, NOT a remove-and-re-add: `BodyReference.Pose` and
  `StaticReference.Pose` are ref-returning in Bepu 2.4 and `UpdateBounds` refits the broadphase for the new pose
  without waking anything. It enumerates `Bodies.Sets` (every allocated set, so SLEEPING bodies are covered
  alongside awake ones, as are the shapeless kinematic anchor bodies a world-space constraint end creates) and
  `Statics.IndexToHandle`. Sleep state, contacts, velocities and constraints all survive: a sleeping crate resting
  on a translated static stays asleep and moves 0.000000 m over the following steps, and a settled contact stack
  keeps its contacts. Constraints need nothing special because `ConstraintFactory` converts world poses into
  body-local offsets at build time, so a uniform translate of both ends preserves every joint exactly.
  `Statics.ApplyDescription` is deliberately NOT used: its own doc says it forces every sleeping body whose bounds
  overlap the old or new collidable active, which would wake the world's whole sleeping population on every shift.
  Cost is O(statics + bodies) pose writes plus refits on the calling thread, budgeted at less than one physics step
  on the same world.

## Joints

Each seam `ConstraintKind` maps to Bepu 2.4 constraint types (verified against the assembly, never from memory):

| Seam kind | Bepu constraint(s) |
|-----------|--------------------|
| `BallSocket` | `BallSocket` (point-to-point) |
| `Hinge` | `Hinge` (point pin + parallel hinge axes), plus a `TwistLimit` when angular limits are set |
| `Slider` | `PointOnLineServo` (keep on the axis line) + `AngularServo` (lock relative rotation to the add-time pose) + `LinearAxisLimit` (clamp travel) |
| `Distance` | `DistanceLimit` (min/max anchor separation; min == max is a rigid rod) |
| `Weld` | `Weld` (fixed relative offset + orientation captured at add time) |

A single seam slider expands to three Bepu constraints; the backend tracks them together and removes them as a
unit. The hinge angular limit uses a `TwistLimit` whose basis Z is aligned with the hinge axis (Bepu measures the
twist about the basis Z axis, confirmed empirically), and it runs a stiffer end-stop spring (>= 60 Hz) than the
joint pin so a fast swing does not overshoot the clamp much.

## Motors and servos

A powered drive (`ConstraintDescription.Motor` != `None`) adds ONE more Bepu constraint on top of the joint (so a
powered slider is four Bepu constraints), tracked and removed as part of the same seam constraint. Each seam drive
maps to a verified Bepu 2.4 motor/servo type:

| Seam drive | Bepu constraint | Target units |
|------------|-----------------|--------------|
| `HingeVelocity` (`WithHingeMotor`) | `AngularAxisMotor` (`LocalAxisA` = hinge axis) | rad/s |
| `HingeAngle` (`WithHingeServo`) | `TwistServo` (basis Z = hinge axis, same as `TwistLimit`) | rad |
| `SliderVelocity` (`WithSliderMotor`) | `LinearAxisMotor` | m/s |
| `SliderPosition` (`WithSliderServo`) | `LinearAxisServo` (`LocalPlaneNormal` IS the drive axis, despite the name) | m |
| `DistanceLength` (`WithWinch`) | `DistanceServo` | m |

Verified empirically against live sims (never from memory): `TwistServo` measures/drives the twist about the
basis's LOCAL Z axis, the SAME convention as `TwistLimit` (an early local-X draft only worked for a Y-axis hinge by
coincidence and spun a Z-axis hinge wildly). `LinearAxisServo.LocalPlaneNormal` is the drive axis (a target of +2
along +Y parks the body at +2). `AngularAxisMotor`/`TwistServo` drive the RELATIVE motion of the two bodies, so the
target's sign is right-handed about the axis with end A as the reference; when A is a world anchor and B the moving
body, a positive hinge-motor target spins B negatively. `Solver.ApplyDescription` retargets a live drive
allocation-free (verified: 1000 calls = 0 bytes), which is what `SetConstraintTarget` uses.

**Motor/servo caps.** A servo takes `ServoSettings(maxSpeed, baseSpeed=0, maxForce)`; a motor takes
`MotorSettings(maxForce, softness=0)`. The seam exposes `MotorMaxSpeed` (servos) and `MotorMaxForce` (both); a `0`
means the backend defaults `DefaultServoMaxSpeed` = 2 (rad/s or m/s) and `DefaultMotorMaxForce` = 2000 (N or N-m).
The defaults are deliberately CAPPED, not `ServoSettings.Default`'s unlimited: a servo eases toward its target at
up to 2 units/s instead of snapping (stable, game-readable), and a 2000 cap moves typical props and holds them
against gravity without the numeric explosiveness of an uncapped drive fighting a stiff constraint. Raise the caps
for heavy loads or snappier motion. `softness`/`baseSpeed` are 0 (a maximally stiff motor, a servo that eases to a
smooth stop). A servo target is clamped to the joint's travel/angle limits; a motor drives into a limit and the
limit clamps it (a capped motor eases into the end-stop, an uncapped one can overshoot the compliant limit spring
by ~0.3 rad, same compliance as the passive limit).

**Character-carrying boundary.** A servo-driven platform MOVES, but a character standing on it does NOT inherit its
velocity - character-carrying is not solved in this batch. The platform's motion is correct; a game that needs a
rider to move with it must add the platform's per-frame delta to the rider itself (while grounded on it), or wait
for the follow-up. Stated honestly so nobody assumes free platform-riding.

**Static-anchor side.** Bepu 2.4 has no one-body position joints (only `OneBodyAngularMotor`/`OneBodyAngularServo`),
so a world-space anchor (`ConstraintAttachment.AtWorld`) is NOT a one-body constraint: it is realised as an
infinite-mass, SHAPELESS kinematic body pinned at the anchor pose, and every joint is a two-body Bepu constraint
whether the far end is dynamic or that kinematic anchor. The anchor carries no collidable (a default `TypedIndex`),
so it never enters the broadphase and is invisible to every raycast and sweep, at any `QueryMobility`. That matters:
a shape-bearing kinematic anchor would be hit by `QueryMobility.All`/`Dynamics` queries (kinematic counts as
non-static), which would snag a character's collide-and-slide sweep on the invisible pivot of a world-anchored
hinge or rope. Shapeless keeps it a pure solver mass point. The anchor body is owned by the constraint and removed
with it. At least one end must be a real dynamic body (both-anchor throws).

**Spring defaults.** A joint's spring is a `SpringSettings(frequency Hz, dampingRatio)`. When
`ConstraintDescription.Stiffness`/`DampingRatio` are left at 0 the backend applies **30 Hz, critically damped
(ratio 1.0)** - a firm joint that removes constraint error within a couple of steps at a 60 Hz step without the
ringing a much higher frequency invites (it matches the contact spring the dynamics backend already uses). Raise
the stiffness (via `WithSpring`) toward, but not far past, your step frequency for a tighter joint; drop the
damping ratio below 1 for a springy/bouncy joint. A frictionless hinge or slider with NO motor conserves energy: it
swings or slides indefinitely rather than settling. Damped settling to a target comes from a powered drive (see
Motors and servos above): a servo holds a position/angle/length, a motor drives a velocity.

**Recommended solver settings.** The 10.29.0 defaults (`substepCount` 4, `velocityIterationCount` 8) resolve every
joint here stably at a 60 Hz step and are NOT changed by this feature (they are part of the determinism
fingerprint). If you stack many stiff joints or push very high spring frequencies and see jitter, prefer raising
`substepCount` on the `BepuPhysicsWorld` constructor over changing the joint springs, and pin whatever you choose
for a stable fingerprint.

## Base alignment and compound gotchas

The shape factory papers over three Bepu behaviours so seam shapes act like their baked geometry:

- Bepu recenters a `ConvexHull` on its centre of mass and centres a `Cylinder` on the body pose. Both
  get wrapped (or, inside a compound, pose-offset) so they stay base-aligned: a prop static placed at
  its base no longer sinks half its height into the ground.
- A Bepu compound's children must be FLAT convex leaves. Children of a `CompoundShape` are added as
  direct leaves with the recentering folded into their local pose, and a nested `CompoundShape` is
  recursed into the same builder pose-composed. A compound-of-compounds would throw in the broadphase
  sweep. Note the struct `CompoundBuilder` is passed by `ref` (a by-value copy builds an empty compound).
- A `TriangleMeshShape` inside a `CompoundShape` is rejected (`NotSupportedException`): mesh children
  break the sweep bounds. Bake building proxies as compounds of convex hulls instead.
- Mesh statics are one-sided (front faces only), and NOT recentered (unlike a hull/cylinder), so a mesh's
  vertices are used at their world positions with an identity pose. Fine in practice: the swept
  collide-and-slide in `Locomotion` always approaches from a known-outside position. A terrain SURFACE mesh
  must have its winding flipped so its collidable face points UP (a falling body / downward ground probe hits
  the top); `TerrainChunkCollision` in `KhaozEngine.Terrain.Render3D` does this when it extracts the chunk
  surface.
- The Bepu `Mesh` takes ownership of a `BufferPool` triangle buffer. `RemoveStatic` disposes it
  (`RecursivelyRemoveAndDispose`), so streaming terrain/building meshes register on load and remove on
  unload with a flat pool across thousands of cycles (no leak).

## Usage

```csharp
using KhaozEngine.Physics;
using KhaozEngine.Physics.Bepu;

IPhysicsWorld physics = new BepuPhysicsWorld();
physics.AddStatic(new BoxShape(new Vector3(2f, 1f, 2f)), Pose.At(new Vector3(0f, 1f, 5f)));
physics.Step(1f / 60f);
```

Wire it in through the `IPhysicsWorld? physics` ctor param on `CharacterController3D`
(local play), `WorldServer` (authoritative sim), and `WorldClient` (client prediction). Server and
client each build their own `BepuPhysicsWorld` from the same baked shapes
(`PropCollisionFormat.LoadDirectory`), so prediction resolves the exact collision the server does and
solid props do not rubber-band.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
