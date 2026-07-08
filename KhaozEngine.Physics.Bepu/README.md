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
- **`Step(dt)`**, **`Raycast`**, **`SweepCapsule`**, **`ComputePenetration`** - the last is one
  CollisionBatcher manifold query over every shape type, deepest contact wins.

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

**Static-anchor side.** Bepu 2.4 has no one-body position joints (only `OneBodyAngularMotor`/`OneBodyAngularServo`),
so a world-space anchor (`ConstraintAttachment.AtWorld`) is NOT a one-body constraint: it is realised as an
infinite-mass kinematic body (a tiny non-colliding sphere) pinned at the anchor pose, and every joint is a two-body
Bepu constraint whether the far end is dynamic or that kinematic anchor. This is the idiomatic BepuPhysics way to
tie a joint to the world and reuses the seam's existing kinematic-body path. The anchor body is owned by the
constraint and removed with it. At least one end must be a real dynamic body (both-anchor throws).

**Spring defaults.** A joint's spring is a `SpringSettings(frequency Hz, dampingRatio)`. When
`ConstraintDescription.Stiffness`/`DampingRatio` are left at 0 the backend applies **30 Hz, critically damped
(ratio 1.0)** - a firm joint that removes constraint error within a couple of steps at a 60 Hz step without the
ringing a much higher frequency invites (it matches the contact spring the dynamics backend already uses). Raise
the stiffness (via `WithSpring`) toward, but not far past, your step frequency for a tighter joint; drop the
damping ratio below 1 for a springy/bouncy joint. A frictionless hinge or slider conserves energy: it swings or
slides indefinitely rather than settling. Damped settling and powered targets are motors/servos (a follow-up),
not part of this joint set.

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

Part of [KhaozEngine](https://github.com/APKiwi/KhaozEngine).
