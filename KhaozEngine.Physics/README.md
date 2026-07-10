# KhaozEngine.Physics

Dependency-free 3D physics seam. `IPhysicsWorld` is the render-free, headless contract the character
controller and the netcode resolve against: authoritative on the server, re-run identically in client
prediction. Backends implement it (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu)
explicitly, it is in no umbrella). Depends only on `System.Numerics`.

## Types

- **`IPhysicsWorld`** - static bodies (`AddStatic`/`RemoveStatic`), dynamic bodies
  (`AddDynamic`/`RemoveDynamic`/`GetDynamicPose`/`GetDynamicVelocity`/`SetDynamicVelocity`/`IsAwake`), joint
  constraints (`AddConstraint`/`RemoveConstraint`), `Step(dt)`, and the queries: `Raycast` (nearest hit),
  `SweepCapsule` (nearest time of impact, what the swept collide-and-slide in `Locomotion` uses),
  `ComputePenetration` (minimum translation to separate an overlapping capsule). Dynamic-body stepping is
  deterministic under a fixed dt.
- **`ConstraintDescription`** - a discriminated joint description for `AddConstraint`: a `ConstraintKind`
  (`BallSocket`, `Hinge`, `Slider`, `Distance`, `Weld`) plus body-local anchors/axes and the fields that kind
  uses. Prefer the factories `BallSocketJoint`/`HingeJoint`/`SliderJoint`/`DistanceJoint`/`WeldJoint`, then
  `WithAngularLimit(min, max)` for a hinge stop and `WithSpring(stiffnessHz, dampingRatio)` for a custom spring
  (defaults `DefaultStiffnessHz` = 30 Hz, `DefaultDampingRatio` = 1.0 critically damped). Each end is a
  **`ConstraintAttachment`**: `OnBody(handle)` for a dynamic body, or `AtWorld(pose)`/`AtWorld(position)` for a
  fixed world-space anchor. At least one end must be a dynamic body. Removing either connected body cleans up the
  constraint automatically. **`ConstraintHandle`** is the opaque handle.
- **Motors and servos** (powered joints) - layer a drive onto the description with `WithHingeMotor(velocity)` /
  `WithHingeServo(angle)` / `WithSliderMotor(velocity)` / `WithSliderServo(offset)` / `WithWinch(length)`. A MOTOR
  chases a target velocity (rad/s or m/s), a SERVO chases and holds a target position/angle/length. Each takes
  optional `maxForce`/`maxTorque` and (servos) `maxSpeed` caps; `0` = the backend defaults
  (`DefaultMotorMaxForce` = 2000, `DefaultServoMaxSpeed` = 2). Update the live target every frame, allocation-free,
  with **`SetConstraintTarget(handle, target)`** (throws on a stale handle or a joint with no motor). A servo
  target outside the joint's limits is clamped; a motor drives into a limit and the limit clamps it. Boundary this
  batch: a servo-driven platform moves but a rider does NOT inherit its velocity (no character-carrying yet).
- **Shapes** - `SphereShape`, `CapsuleShape` (upright, local Y), `BoxShape` (half-extents),
  `CylinderShape`, `ConvexHullShape` (solid props), `TriangleMeshShape` (non-convex buildings/interiors,
  static only), and `CompoundShape` (`CompoundChild[]`, disjoint children each at a local `Pose`). A dynamic
  body takes any of these except a triangle mesh. Base-aligned cylinder/hull shapes rest base-on-ground.
- **`DynamicBodyDescription`** - the mass/inertia + initial-motion knobs for `AddDynamic`:
  `WithMass(mass)`, plus optional `LinearVelocity`/`AngularVelocity`/`SleepThreshold`. Mass &lt;= 0 = an
  infinite-mass (kinematic) body: unmoved by gravity/impacts, moved only by its velocity.
- **`Pose`** - world position + orientation record struct. `Pose.At(position)` for identity orientation,
  `Pose.Identity` for the origin (register a body whose geometry already carries its world position, e.g. a
  terrain chunk collision mesh, at this pose).
- **`PhysicsGroundProbe`** - the OPT-IN unified-terrain adapter: wraps an `IPhysicsWorld` and exposes
  `HeightDelegate`/`NormalDelegate` (a downward raycast) to hand `CharacterMovement.Step` in place of the
  analytic `TerrainCollision` ground delegates, once the terrain surface is registered as physics geometry.
  So terrain, props, and buildings all resolve through one world. The probe is STATICS-ONLY by default
  (`GroundMobility`), so a dynamic body under the character (a crate) is not read as ground; set
  `GroundMobility = QueryMobility.All` to stand on dynamic bodies. Additive: a game that has not adopted keeps
  passing the analytic delegates and this never runs.
- **`PhysicsMaterial`** - friction + restitution, `PhysicsMaterial.Default` is full friction, no bounce.
  A dynamic body's `Restitution` (0..1) drives an approximate, deterministic game-feel bounce that decays
  geometrically with restitution (NOT a true coefficient of restitution: a bounded post-solve reflection, exact
  apex not analytically pinned).
- **`QueryFilter`** - which bodies a raycast/sweep may hit: a `QueryMobility` (statics / dynamics / both) plus a
  layer mask. Default (`QueryFilter.All`) matches every body; `QueryFilter.StaticsOnly` /
  `QueryFilter.DynamicsOnly` restrict by mobility (the Bepu backend honours the mobility gate, so a statics-only
  ground probe ignores dynamic bodies).
- **`StaticHandle`**, **`DynamicBodyHandle`**, **`RayHit`**, **`SweepHit`** - opaque body handles and the query result structs.
- **`PhysicsShapeScale.Uniform(shape, scale)`** - a new shape with all geometry scaled uniformly
  (compound child poses included). For per-placement scatter scale before `AddStatic`.
- **`PropCollisionFormat`** - the KECL `.coll` binary format: `Write`/`Read` a single `PhysicsShape`,
  plus the headless manifest-free loaders `LoadDirectory(dir)` (every `<id>.coll` keyed by file name)
  and `Load(entries)`. A GPU-less server loads the same baked shapes a client predicts against,
  byte-identical, so queries match and prediction reconciles. The glTF bake that PRODUCES `.coll`
  files stays in `KhaozEngine.Render3D` / `ke-propbake`.

## Usage

```csharp
using KhaozEngine.Physics;

IPhysicsWorld world = new BepuPhysicsWorld();   // from KhaozEngine.Physics.Bepu

// Headless server: load baked shapes and build the same world the client has.
var shapes = PropCollisionFormat.LoadDirectory("content/collision");
StaticHandle rock = world.AddStatic(
    PhysicsShapeScale.Uniform(shapes["rock_big"], 1.3f),
    Pose.At(new Vector3(10f, groundY, -4f)));

// Dynamic bodies fall under gravity and are stepped by Step(dt):
DynamicBodyHandle crate = world.AddDynamic(
    new BoxShape(new Vector3(0.5f, 0.5f, 0.5f)),
    Pose.At(new Vector3(10f, 8f, -4f)),
    DynamicBodyDescription.WithMass(10f));
world.Step(1f / 60f);
Pose cratePose = world.GetDynamicPose(crate);

// Joints: hang a door on a hinge anchored to the world, swinging about Y, limited to a quarter turn.
DynamicBodyHandle door = world.AddDynamic(
    new BoxShape(new Vector3(0.5f, 1f, 0.05f)),
    Pose.At(new Vector3(0.5f, 2f, 0f)),
    DynamicBodyDescription.WithMass(20f));
world.AddConstraint(ConstraintDescription.HingeJoint(
    ConstraintAttachment.OnBody(door),
    ConstraintAttachment.AtWorld(new Vector3(0f, 2f, 0f)),
    anchorA: new Vector3(-0.5f, 0f, 0f), anchorB: Vector3.Zero,
    axisA: Vector3.UnitY, axisB: Vector3.UnitY)
    .WithAngularLimit(0f, MathF.PI / 2f));

if (world.Raycast(origin, direction, 50f, out RayHit hit))
    Console.WriteLine($"hit {hit.Body} at {hit.Point}");

world.RemoveDynamic(crate);
world.RemoveStatic(rock);
```

No render, window, or GPU dependency. In the `Foundation` umbrella metapackage.

Part of [KhaozEngine](https://github.com/APKiwiOrg/KhaozEngine).
