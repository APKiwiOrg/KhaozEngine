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
- **`IPhysicsWorldQueryView` / `CreateQueryViewExcludingStatics`** - a non-owning, read-only view over the
  same simulation. `SourceWorld` is the exact logical `IPhysicsWorld` that received the factory call. The
  default factory throws `NotSupportedException`, including for an empty selection, and the Bepu backend
  supports it. A view snapshots source-local `StaticHandle` values, applies exclusions before nearest or
  deepest selection, keeps the existing mobility gates independent, forwards dynamic observations, and does
  not filter simulation contacts. `CanRebase` is false while `Origin` follows the source live. Mutations and
  nested factories throw. `Dispose` is idempotent and disposes only the view. After disposal, operations and
  `Origin` throw `ObjectDisposedException`, while `SourceWorld` and `CanRebase` remain inspectable. The source
  must outlive the view.
- **`IPhysicsQueryLeaseSource` / `IPhysicsQueryLease`** - optional stable read intervals over a live
  owner. `AcquireQueryReadLease()` captures the exact owner, origin and process-local mutation generation.
  Queries remain available on the acquiring thread. Nested leases and same-thread mutations are refused,
  while other threads serialize behind the owner gate. Call `AssertCurrent()` before publishing pure
  results, then dispose on that same thread before applying physics mutations. Disposal is idempotent on
  the acquiring thread. A restricted view leases its complete owner and cannot be disposed during the
  interval. A lease does not certify water/terrain residency, portable world identity or scope completeness.
  Those facts belong to the environment adapter paired with this physical read interval. A backend that
  lacks the optional interface remains valid for legacy callers and cannot promise an explicit lease.
- **`IPhysicsCapsuleFeatures`** - optional finite-feature certification for one static. Under a read lease
  from the same receiver, `QueryCapsuleFeature(lease, target, capsule, pose, maximumSeparationMetres, faces)`
  names the closest feature of `target` to a capsule at `pose` within the capsule radius plus the
  separation band. A `Complete` `CapsuleFeatureResult` carries the feature `Kind` (`FaceInterior`,
  `OpenBoundary`, `ConvexCrease`, `ConcaveCrease` or `Vertex`), witness points, separation bounds, error
  bounds and every incident face written into `faces` together, each with a geometric normal. Every other
  status is a refusal with no witness and leaves `faces` untouched. `NoFeature` means nothing lies within
  the band, `Unavailable` that the target is not a live static in this view, `Unsupported` that the shape or
  pose is outside the backend's certified domain, `Ambiguous` and `Unresolved` that the backend could not
  decide the closest feature exactly, and `CapacityExceeded` that `faces` is too short (`RequiredCapacity`
  names the need when known). A result is bound to its receiver and its original lease. Call
  `AssertFeatureCurrent(result, lease)` before publishing from it, and never consume it after the lease is
  disposed. A later lease of the same generation cannot revive it. Structural fields alone do not permit
  movement. Support eligibility is the caller's decision.
- **Static handle provenance for query views** - the factory rejects invalid, missing and stale handles at
  creation. Handles are numeric identities local to their source world, so equal values from different worlds
  are not interchangeable. Use handles returned by that source, and do not reuse removed exclusions to infer
  a later Bepu static: a recycled backend id belongs to the new live seam handle. A logical decorator that
  offers the factory must wrap the delegated view and expose the decorator itself as `SourceWorld`.
- **Floating origin: `Origin` / `CanRebase` / `Rebase(newOrigin)`** - default interface members, so an existing
  backend or test double keeps compiling and correctly reports that it cannot rebase. `Origin` is the world-space
  point this world's coordinates are expressed against: every pose passed in, every query coordinate, and every
  pose read back is relative to it. `Vector3.Zero` (the default) means the world speaks absolute world coordinates,
  which is what a backend does until something rebases it. `Rebase(newOrigin)` re-expresses the whole world against
  a new origin, translating every static, every body (awake AND sleeping) and every world-space constraint anchor,
  then adopting the origin. Velocities, sleep state, contacts and constraints all survive: it is a change of
  coordinate space, not a physical event, and nothing inside the world can observe it. It takes the TARGET origin
  rather than a delta so the contents and `Origin` move as one atomic operation, and it must be called BETWEEN
  steps. Check `CanRebase` first (the default `Rebase` throws).
  **A caller that speaks absolute converts at the call site**: `world.AddStatic(shape, new Pose(absolute - world.Origin, rot))`,
  `world.Raycast(from - world.Origin, ...)` (a direction and a hit distance are frame-invariant, so only the
  position converts). A site that forgets is a site that never read `Origin`, which is greppable. The engine's own
  streaming sinks and the follow camera's occlusion sweep already do this. **`PhysicsGroundProbe`/`PhysicsColumnProbe`
  are the same contract, not an exception**: their `Height`/`Normal`/`Sample` take `(x, z)` straight into `Raycast`
  with no conversion of their own, so on a rebased world the caller must already have reduced by `Origin` before
  calling in (on a framed `WorldServer` that means `SamplerSpace.Frame`, so the stepper hands them frame-local
  coordinates rather than wrapping them back out to absolute).
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
- **`PhysicsColumnProbe`** / **`ColumnSurface`** - the multi-surface widening of `PhysicsGroundProbe`:
  `Sample(x, z, Span<ColumnSurface>)` sweeps a vertical column with repeated downward raycasts (cast from
  `ProbeHeight`, re-cast from just below each hit, until `ProbeRange` is spent) and writes every STANDABLE
  surface bottom-up (ascending `ColumnSurface.Height`), returning how many were written. A hit is standable
  when its normal passes the walkable-slope gate (`MaxSlopeRadians`, default 50 degrees). A non-standable
  hit (a wall, a bridge underside, a too-steep face) still counts as the ceiling of whatever lies beneath
  it, which is how each `ColumnSurface.Headroom` is measured (`float.PositiveInfinity` for the topmost
  surface). A SOLID convex static (box, hull, or compound) yields exactly one standable surface per exposed
  top face, never a stack: the sweep recognises the inside-solid self-hits BepuPhysics reports as the ray
  descends through the body's interior and skips them (each such body's underside bounds the headroom of the
  first real surface beneath it). On overflow the lowest surfaces are kept and the highest dropped,
  deterministically, matching the ground-least-affordable-to-lose convention. When the usual 0.01-unit
  descent rounds away, the sweep uses the next representable lower Y. It returns the surfaces collected
  so far if a finite lower cast origin or reduced remaining range cannot be represented. STATICS-ONLY by default
  (`GroundMobility`), the same stance as `PhysicsGroundProbe`. This is the physics half of KhaozEngine.Navigation's layered overworld
  bake: a game glues `Sample` to `INavColumnProvider` with a one-line delegate, since Physics and
  Navigation deliberately never reference each other (see `docs/DEPENDENCY-SEAMS.md`'s surface-source
  seam). Deterministic for a fixed physics world.
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
  files stays in `KhaozEngine.Render3D` / `ke-propbake`. `Read` throws `InvalidOperationException` on
  a bad magic, unsupported version, unknown shape kind, or an array count that is negative or could
  not possibly fit in what remains of the stream (a truncated or corrupted file), rather than risking
  an `OverflowException`/`OutOfMemoryException` from an unchecked allocation. It refuses malformed shape
  data the same way: a non-finite or non-positive box half extent or cylinder size, a non-finite hull
  point, mesh vertex or compound child pose, a compound child orientation that is not a unit quaternion
  (squared length off 1 by more than 1e-3), a mesh index count that is not a multiple of 3 or an index
  outside the vertices, a compound with no children, and compound nesting deeper than 16 levels. `Write`
  refuses the same non-unit compound child orientation with `ArgumentException` before writing anything, so it
  never produces a payload `Read` would refuse for it.

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
