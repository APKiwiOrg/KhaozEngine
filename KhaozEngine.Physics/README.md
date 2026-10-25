# KhaozEngine.Physics

Dependency-free 3D physics seam. `IPhysicsWorld` is the render-free, headless contract the character
controller and the netcode resolve against: authoritative on the server, re-run identically in client
prediction. Backends implement it (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu)
explicitly, it is in no umbrella). Depends only on `System.Numerics`.

## Support-query measurement

`SupportQueryCapture`, `SupportQueryMeasurement`, `SupportQueryMetrics`, `SupportQueryCounter`,
`SupportQueryRouteMetrics` and `SupportProofReuseMetrics` are public in namespace/package
`KhaozEngine.Physics`. They wrap the existing recorder without consumer friend access or reflection.
`Begin(string label)` enables fresh counters on the calling managed thread and suspends prior recorder
callbacks. Closing restores its exact prior enablement, counters, routes, observations and callbacks, while
preserving physical live retention balance across the scope boundary. Other threads are not enabled by this scope.

These consumer-supplied functions run synchronously and close each complete source/reuse lifetime:

```csharp
using KhaozEngine.Physics;

var player = SupportQueryCapture.Begin("player");
try
{
    RunPlayerSourceLifetime();
}
finally
{
    player.Dispose();
}
SupportQueryMeasurement playerProfile = player.Snapshot();

var cow = SupportQueryCapture.Begin("cow");
using (cow)
{
    RunCowSourceLifetime();
}
SupportQueryMeasurement cowProfile = cow.Snapshot();
SupportQueryMeasurement combined = SupportQueryMeasurement.Combine([playerProfile, cowProfile]);
long finds = combined.Total[SupportQueryCounter.Find];
long playerFinds = combined.ByLabel["player"][SupportQueryCounter.Find];
bool complete = combined.Total.ProofReuse.HasCompleteLifetimes;
```

`using` and `finally` restore state when measured work throws. The closed capture retains a partial frozen
snapshot inspectable on its creating thread. Scopes are synchronous and thread-affine, close in reverse order
and must never cross `await`, `Task.Run` or a thread handoff. Wrong-thread use, out-of-order disposal and sampling
a parent with an active child throw `InvalidOperationException` before changing state. Successful disposal is
idempotent. Immutable results may be read on any thread.

Labels are caller descriptions, never inferred body IDs. Null labels throw `ArgumentNullException`, blank
labels throw `ArgumentException`. Other labels are preserved exactly, including whitespace, and compared and
enumerated ordinally with case sensitivity. Nested direct work belongs exclusively to the innermost label.
A closed child contributes once to its parent's snapshot. Repeated labels coalesce into one row, so summing
label counters equals `Total`. A nested engine Find is counted under the active label, not a second interval.

`Snapshot()` is detached and cumulative, never a reset. Before disposal only the active top scope can sample.
Earlier snapshots cannot change. An empty capture still has its zero-valued label. `Combine` adds disjoint
measurements without deduplication. Never combine parent and child snapshots, or multiple cumulative snapshots
covering the same work. Independent consumer threads start their own scopes, join, then combine their results.
There is no implicit propagation to arbitrary consumer tasks. An empty combination has no labels and zero
totals. Null sequences or elements throw `ArgumentNullException`.

The counter indexer returns `long`. The 42 `SupportQueryCounter` identities are explicit values 0 through 41,
from `Find` to `InitialHoldReuseHits`, with new counters appended and no Count sentinel. Undefined values throw
`ArgumentOutOfRangeException`. Enabled recording and aggregation use checked long arithmetic and throw
`OverflowException` on overflow. Ordinary counters and routes add. `Routes` exposes `WallRetries`,
`PushedPlacements`, `MediumResolves`, `MaximumMediumRetries`, `GroundSegments`, `SlideSegments`, `AirSegments`,
`WaterSegments`, `RoundedReads` and `MediumHandoffs`. Only `MaximumMediumRetries` takes the maximum.

Contact navigation joins every started worker and merges its local record exactly once in worker-index order,
under the label captured when that build started. Partial worker work merges before the selected build failure
propagates. Workers restore their own prior state. Degree one retains its serial path. Caller-side binding,
lease, pin and disposal work is counted on the caller. Extra worker setup and speculative work are real raw
counts. Equal graph bytes do not imply equal counters or peaks across worker counts, CPUs or schedules.
Capture changes neither scheduling nor default reuse. See the [Movement worker contract](../KhaozEngine.Movement/README.md#labelled-support-query-capture).

`ProofReuse.PeakKeys` counts occupied memo entries and `PeakBytes` charges owned table capacity and member/join
payload by element size, including spare fixed slots. Each is the maximum observed single-session value, not
a sum of workers or simultaneous process peak. The two maxima need not describe the same session. They exclude
CLR headers, allocator overhead, unrelated scratch, initial-Hold maps, GC allocation totals and process RSS.
Combining takes maxima and combines `HasCompleteLifetimes` with logical AND, whose empty identity is true.
Completeness requires zero live retained keys and bytes at entry and at the sampled boundary for every
contributor. Partial or pre-existing lifetimes can miss steady storage and make that flag false.

For bake peak evidence, begin before the first source store and sample only after every joined source group
has reset or disposed. Partial snapshots remain useful for counters, but cannot establish complete memory
evidence. Enabled scopes and snapshot objects allocate diagnostic overhead excluded from proof-reuse bytes.
Create scopes outside timed regions where possible and materialize results afterwards. Disabled paths retain
their existing gates and allocate no widened worker records. This API supplies counts, not timers or RSS.
Hosted timing, allocation and world-bake acceptance gates still require their separate approval and evidence.

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
  Queries remain available on the acquiring thread. Nested leases and same-thread mutations are refused.
  Other threads wait behind the owner gate unless the receiver also declares `IPhysicsConcurrentReadSource`,
  which admits simultaneous readers while writers stay exclusive. A parallel contact navigation bake
  (`NavBakeExecution` above degree 1 in `KhaozEngine.Movement`) requires it on both the physics source and the
  selected query view. Call `AssertCurrent()` before publishing pure
  results, then dispose on that same thread before applying physics mutations. Disposal is idempotent on
  the acquiring thread. A restricted view leases its complete owner and cannot be disposed during the
  interval. A lease does not certify water/terrain residency, portable world identity or scope completeness.
  Those facts belong to the environment adapter paired with this physical read interval. A backend that
  lacks the optional interface remains valid for legacy callers and cannot promise an explicit lease.
- **`IPhysicsStaticGeometrySource.StaticGeometryGeneration`** - optional installer provenance marker.
  Bepu and its restricted views expose the existing static-support version. Static installation and
  removal advance it before mutation. Steps, dynamics, constraints and pure rebases preserve it.
  This marker does not replace the read lease's current `GeometryGeneration`.
- **`IPhysicsCapsuleContacts` / `QueryCapsuleContacts`** - optional complete contact constraints in the
  current physics frame. Supply a finite nonnegative separation margin and caller-owned span. Normals
  point away from the contacted body, separation is positive outside and negative inside. Contacts retain
  source-local body, child and feature provenance. `Complete == false` leaves the entire span untouched,
  with `Written == 0`. `RequiredCapacity` reports the needed size when known, otherwise zero. Check the
  returned `CertifiedErrorMetres` against the caller's tolerance before using a complete result. Uncertain
  boundary contacts may appear within that error beyond the requested margin. Unsupported filters must
  be refused, never silently ignored. Use a query lease when composing several queries into one result.
- **`IPhysicsCapsuleSweep` / `SweepCapsuleCertified`** - optional closed-path sweep contract. Bepu has a
  [certified installed-family implementation](../KhaozEngine.Physics.Bepu/README.md), with bounded
  numerical/work domains and atomic refusal outside its proved cases. `CapsuleSweepResult` defaults to `Unresolved`
  with no usable prefix. `Clear` has no impact distance and must cover the full request. `Hit` has an
  ordered clear-through/impact interval, neither endpoint grants placement. Distances and declared error
  are finite nonnegative metres. `IsComplete` validates Clear/Hit structure only. Consumers still check
  requested extent, accepted error, scope and read currentness. Unsupported orientation, filters or
  numerical/work domains must refuse. Zero displacement requires a complete stationary certificate.
- **`CapsuleQueryDimensions` / `IPhysicsCapsuleGeometryQueries`** - optional value-input sweep and
  contact queries. Radius and cylindrical length retain their exact represented values. This avoids
  constructing a persistent capsule shape for an enclosure whose dimensions vary with endpoint rounding.
  Geometry, filters, completeness, validation, selected-view restrictions and lifetime rules match the
  shape-input capabilities. Bepu owners and restricted views implement both routes through the same core.
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
- **`IPhysicsSupportNeighborhood`** - optional support neighborhood capability, separate from
  `IPhysicsCapsuleFeatures` and bound to leases and receivers the same way.
  `QuerySupportNeighborhood(lease, probe, pose, bandMetres, elements, joins, filter)` publishes every front-facing element of every selected static whose separation from an upright probe may be
  at most `bandMetres`, ordered by static handle then element id. A `SupportElement` is a `Polygon` (a box or
  hull face, or one mesh triangle) or a `Tangent` (a point on a sphere, capsule or cylinder side, or a cylinder
  cap), with its normal, witness, error bounds and separation interval. `joins` receives a symmetric bit matrix:
  row `i` starts at word `i * JoinWordsPerRow` of the `SupportNeighborhoodResult`, and bit `j` is set when
  polygons `i` and `j` meet within the band and each lies on or below the other's plane. Size the spans with
  `SupportNeighborhoodResult.MaximumElements` (256) and `JoinWordsFor`. Mesh triangles certify one by one, so
  mesh topology never refuses. Refusals are `CapacityExceeded` with `RequiredElements`, `Unsupported` outside
  the certified probe, pose, band or shape domain, and `Ambiguous` for a hull not proven convex. A layer filter
  and a join span shorter than `elements.Length * JoinWordsFor(elements.Length)` throw `ArgumentException`. Check a complete result with `AssertNeighborhoodCurrent(result, lease)`.
- **`IPhysicsSupportFootprint`** - optional complete footprint capability used by the contact controller.
  `QuerySupportFootprint` certifies the expanded closed-disc column, both retained proposal contacts
  and the whole-polygon join matrix under one authenticated lease. Polygon maxima cover the full
  original disc. Ring-only members cap and feed guards without contributing support. Curved parts
  retain their raw IDs and publish up to two attained witness roles, axis then footprint. Certified
  upward bounds classify slope equality independently of the full normal enclosure.
  Capacity counts at most 256 relevant raw pairs, with atomic refusal at 257 and untouched output
  spans. A below-band member can be omitted only with proof that it cannot contribute, join a
  retained band-capable member or establish a required proposal guard. `AssertFootprintCurrent`
  checks the original receiver and interval, including same-generation reacquisition. Size member storage
  with `SupportFootprintResult.MaximumElements`, joins with `members.Length * JoinWordsFor(members.Length)`
  and index published rows with `result.JoinWordsPerRow`. A short join span throws `ArgumentException`.
  `SupportFootprintBounds.TryCreate` supplies the shared outward scope envelope. Radius must be at least
  0.01 m, `ContactBand` is exactly `0.0001f` and a zero cosine slope gate returns `Unsupported`.
  The released neighborhood capability remains independent. See
  [complete footprint use](../docs/USING-KHAOZENGINE.md#complete-support-footprints).
- **`IPhysicsSupportFootprintReuseSource` / `IPhysicsSupportFootprintReuseSession`** - discover this optional
  capability on the exact selected receiver. A thread-affine session borrows its opening current lease only
  for authentication and never owns or prolongs a lease. Reset it with immutable profile/raw-source identity
  tokens, query through the existing footprint API with each current lease, and dispose at source-group end.
  Reset always releases prior payloads, including with the same tokens. Nested or unsupported opens decline
  without replacing the ordinary path. Complete geometry can be copied across unchanged authenticated leases,
  with fresh receiver/lease metadata. Old results and arena evidence stay expired. The backend owns the exact
  key, payload bounds and refusal policy. See [proof reuse](../docs/USING-KHAOZENGINE.md#exact-completed-footprint-reuse).
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
  static only, every vertex finite, or the Bepu backend throws `ArgumentException` at `AddStatic`), and
  `CompoundShape` (`CompoundChild[]`, disjoint children each at a local `Pose`). A dynamic body takes any of these
  except a triangle mesh. Base-aligned cylinder/hull shapes rest base-on-ground.
- **`DynamicBodyDescription`** - the mass/inertia + initial-motion knobs for `AddDynamic`:
  `WithMass(mass)`, plus optional `LinearVelocity`/`AngularVelocity`/`SleepThreshold`. Mass &lt;= 0 = an
  infinite-mass (kinematic) body: unmoved by gravity/impacts, moved only by its velocity.
- **`Pose`** - world position + orientation record struct. `Pose.At(position)` for identity orientation,
  `parent.Compose(child)` for compound flattening with each binary32 result retained before the next level,
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
  ground probe ignores dynamic bodies). The init-only `CullBackFaces` property (set it with
  `with { CullBackFaces = true }`) makes `SweepCapsule` skip a static mesh triangle that
  does not face against the sweep (front normal `Cross(C - A, B - A)` dotted with the sweep direction at least
  zero, vertical triangles included), so a support probe passes one-sided back faces as Bepu's contacts do.
  Compound children are never culled and raycasts ignore the flag.
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
