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
  Penetration initializes its batcher only after the first static passes mobility and view exclusions.
  If none is eligible, it returns false with a positive-zero MTV without renting, clearing or flushing
  native batcher storage. Broad-phase traversal and managed overlap scratch still run. Eligible pairs
  retain their submission order, and every initialized batcher reaches one `Flush` in `finally`.
  Whole-navigation CPU impact requires separate measurement.
  For a positive finite `SweepCapsule` distance, the full motion is carried in Bepu's velocity and
  `maximumT` stays at 1. Bepu 2.4 applies that bound twice in mesh and compound child searches, so a
  smaller bound would truncate the requested motion. Positive hits retain it while the handler tracks
  the nearest hit separately. Only a hit at zero lowers it to zero, since no nearer hit is possible.
  The returned hit time is scaled back to metres.
- **Complete capsule contacts** - the owner and restricted views implement `IPhysicsCapsuleContacts`.
  A bounded empty selection returns a complete empty result with zero error, without capsule/hull
  face selection. Excessive coordinate spacing still refuses. Every candidate query retains the
  existing radius-scaled error formula and 0.001 m limit.
  The query selects convex leaves with margin-expanded bounds, bypasses final contact-count reduction, applies mesh seam
  smoothing, then sorts geometric constraints with local provenance. Distinct normals survive even when
  a deeper bed contact exists. Mobility and view exclusions apply before gathering. Nonzero layer masks
  throw `NotSupportedException` because this backend does not assign per-body layers.
  Work limits are 4096 broad-phase candidates, 4096 selected leaves, 4096 children per compound, 16384 contacts and 1024 triangles per
  mesh smoothing region. Exhaustion, an unsupported mesh scale, a smoothed normal whose depth cannot be
  certified, or a numerical budget above 0.001 m returns incomplete without writing the destination.
  A too-small destination also returns incomplete and reports the required capacity. These limits and
  the pinned backend version belong in any bake policy identity that consumes this capability.
- **Static query views** - `BepuPhysicsWorld` supports
  `IPhysicsWorld.CreateQueryViewExcludingStatics(ReadOnlySpan<StaticHandle>)`. It validates every live seam
  handle at creation, copies the selection, and resolves the stable seam handle back to the current static
  before applying the exclusion. Removing an excluded static and allowing Bepu to recycle its internal id
  therefore cannot hide the replacement. An empty selection still returns a restricted non-owning view with
  unfiltered query results. The view preserves the source world's mobility gates and dynamic observations,
  while simulation contacts remain unchanged.
- **Query read leases** - the owner and its restricted views implement `IPhysicsQueryLeaseSource` and
  `IPhysicsConcurrentReadSource`. A lease is a thread-affine reader interval, and several threads may hold
  leases and query one world at once. Every outer read interval borrows private query scratch, including its
  own Bepu allocation pool, so readers never share backend buffers. Mutations, rebases and operational view
  disposal are exclusive writers that run in arrival order once every admitted reader interval has ended,
  and new outer readers wait behind a queued writer. Nested reads on one thread reuse its interval and never
  wait. Mutations from inside a read fail before changing the world, and a nested lease cannot shorten the
  first one's lifetime. The read paths and the storage each one uses are recorded in the
  [concurrent read audit](../docs/verification/2026-10-14-bepu-concurrent-read-audit.md). The captured
  generation is local to this process and advances conservatively before operations that may change the world,
  including writes that later fail. Rejected leased mutations never advance it. This is a synchronous
  lifetime contract, not immutable geometry or permission to call a lease from another thread. Release it
  before any physics write.
- **Capsule-feature certification** - the owner and its restricted views implement
  `IPhysicsCapsuleFeatures`. The query authenticates the lease as this owner's before entering the gate,
  reads the live static, and certifies boxes, convex hulls, compounds of those, and one-sided triangle meshes
  with bounded exact arithmetic. Sphere, capsule and cylinder statics, mesh children of a compound, tilted
  capsules, a radius outside 0.01 to 2, a length over 8 and a band over 0.01 m return `Unsupported`. A
  static excluded by a view, removed, or filtered out by a dynamics-only `QueryFilter` returns
  `Unavailable`. A probe that meets only the back face of a mesh returns `NoFeature`. A result from a view
  is valid only through that view and its original lease. The owner and its views also implement
  `IPhysicsSupportNeighborhood` for boxes, convex hulls, meshes, spheres, capsules, cylinders and compounds of
  those, under the same lease and receiver rules.
- **Complete support footprints** - owners and restricted views implement `IPhysicsSupportFootprint`.
  Both proposal sweeps run after authenticating the lease. Admission covers the outward expanded disc
  column and retains full polygons for exact disc maxima and released whole-polygon joins. Sphere,
  capsule and cylinder parts publish attained axis/footprint witnesses with independent upward bounds.
  Closed-form and certified tilted-region enclosures cover the admitted curved poses. Ring-only members
  supply cap/guard context. The cap counts 256 relevant raw pairs, at most 512 witness slots, and refuses
  atomically at 257 without touching either output span. Unsupported mesh scales, uncertified shapes,
  predicates or work domains refuse. Radius is 0.01 to 2 m and proposal probes are upright with length
  at most 8 m. Selected exclusions and original receiver/lease currentness apply to all work.
  Immutable installed local geometry is keyed by owner, installation identity and static-geometry version.
  Static add/remove/replacement and changed rebase invalidate it. Simulation steps and dynamic mutations
  do not. Results still authenticate the global lease generation. No selected-support result is cached.
  See [complete footprint use](../docs/USING-KHAOZENGINE.md#complete-support-footprints).
  Local vertex components are admitted through 2048 m. This is a range domain, not a precision guarantee.
  Each query still enforces its position, separation and normal publication ceilings, exact-arithmetic
  capacity and the 256 m incidence bound. A failed precision certificate refuses rather than widening a budget.
  `IPhysicsStaticGeometrySource.StaticGeometryGeneration` changes on actual static mutations. Removing an
  unknown handle is a no-op and leaves ordinary registrations current. Steps and pure rebases preserve them.
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
  Cost is O(statics + bodies) pose writes plus refits on the calling thread. Ordinary tests enforce one refit
  call per body and static, zero managed allocation and the safe static API. The separate
  [hosted rebase diagnostic](../docs/RENDERER-PERFORMANCE.md#hosted-physics-rebase-diagnostic) fails above a
  median 3x step ratio at the resident streaming scale, without gating merges on wall-clock noise.

## Exact completed-footprint reuse

Bepu owners and selected views implement the optional `IPhysicsSupportFootprintReuseSource`.
A session authenticates its exact receiver, owner, selected view/exclusions, installed/static/lease
geometry generations and all origin bits. Immutable profile and original raw-source token identities
select one domain. Reset discards every entry, and source-group disposal releases all owned storage.
The session is thread-affine, non-owning and never retains or opens a second explicit read lease.

Requests use the full bitwise disc/band/slope/contact-band, both proposal dimensions/poses/distances,
filter and consumed join mode. Complete-empty and full ordered member/witness/proposal/join payloads
can hit. Every hit authenticates the current basis and publishes new receiver/lease metadata before
copying only occupied output ranges. An old result never becomes current. Refusals, exceptions,
unsupported views and changed bases keep the original validation and evaluator path.

Each source session has at most 512 keys and 2 MiB of retained storage, with no eviction. Accounting
charges the entire fixed entry-array capacity by `Unsafe.SizeOf<Entry>()`, plus exact member-array
capacity by `Unsafe.SizeOf<SupportFootprintMember>()` and join-array capacity by `sizeof(ulong)`.
Entry size includes key, witness count, both proposal verdicts and buffer references. This deterministic
capacity charge is separate from measured runtime allocation. Saturation declines new publication.
Reference evaluation and non-default curved-work policies bypass lookup and publication. Hits do
not replay Volumes, Proposals or curved-work events. See the [consumer contract](../docs/USING-KHAOZENGINE.md#exact-completed-footprint-reuse).

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

## Certified sweep development status

The owner and restricted views also implement `IPhysicsCapsuleGeometryQueries`. Its
`CapsuleQueryDimensions` value inputs share the shape-input sweep and contact cores with exact represented
dimensions, identical restrictions and validation. Changing an endpoint-rounding enclosure does not
allocate a persistent shape.

The owner and restricted query views implement `IPhysicsCapsuleSweep` for identity boxes and
admitted rotated boxes, convex hulls, spheres, capsules and cylinders at their current installed
static or dynamic pose, plus finite static triangle meshes with strictly positive finite installed
scale. The identity-box kernel is unchanged. Rotated boxes use eight installed corners, six finite faces, twelve edges, capsule
axis endpoint/interior regimes and closed solid occupancy. They are never replaced by an AABB or
independently inflated planes. [The general sweep contract](../docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md)
defines this family and the remaining adoption gates.

Queries require an upright identity-oriented capsule, positive finite radius, nonnegative finite
cylindrical length, finite centre/displacement, `Layers == 0` and a supported mobility selection.
Both culling flag values are retained. General queries require radius and half-cylinder length at
most 1000 m, with each original centre and displacement component within [-1000000, 1000000] m.
Mutation-maintained owner and view selection counts refuse an out-of-domain query before traversal
when it selects any non-identity-box installation. Identity-only selections retain their original
domain and aperture, including when general statics are excluded or mobility is unselected.
General installed local coordinates and translations stay
within 2048 m, with the existing squared quaternion norm band `1 +/- 2^-20` and positive determinant.
A rounded quaternion is never normalized. The forward finite geometry and the pinned transpose-local
operation graph must both prove the same outcome, including exactly rigid maps. Rounded root-offset
subtraction and child-offset addition can disagree even with identity rotation. Stored Bepu
bounds retain the forward enclosure without any operational pad. A separate general-only
query traversal expands its aperture from the actual query operands and mutation-maintained
selected root, child, geometry and operator bounds. It covers the complete operational graph,
including cancellation and inverse-transpose skin departure. Only selected non-identity leaves
count against that pass's own 4096-leaf preflight and callback ceilings. Identity leaves retain
the original unpadded aperture, checked preflight, callback ceiling and kernels. The extra pass
runs only when the selection contains a general installation. General candidate rejection and
mesh inner traversal use the same actual-query proof over retained raw/recentered preimages.
Unproved evidence refuses. No query-time geometry scan, replacement spatial index or warm
allocation is introduced, and the simulation's collision tree receives no operational inflation.
Hit brackets include their complete disagreement
within 0.001 m. Unknown coefficient correspondence, ambiguous tangency and ill-conditioned transforms
refuse. Hulls use the installed face loops and point identities, including SIMD padding, after strict
exact topology validation. Planar convex faces, opposite edge pairing, Euler closure and connected
cyclic vertex links must all be proved. The exact one-hull centroid-compensating Compound wrapper
installed by `ShapeFactory` retains its child typed index and offset. Finite polygon faces, edges,
vertices and closed interior share the box convex kernel. A tetrahedron has four faces, four vertices,
six edges and four surface triangles. Its empty AABB corners remain clear. Topology caps are 256 faces,
508 vertices and 1524 face-index entries. A cap is not a promise that every query fits the work budget.
Mesh triangles reuse finite face, edge and vertex distance without solid occupancy. Raw triangle
identities are retained across seams. Installed scaled geometry and Bepu's separately recentered
sweep continuation must agree within the same complete bracket. The source-derived
[side/filter matrix](../docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md#installed-mesh-implementation-and-pinned-side-policy)
retains initial closed front contact before directional culling, raw back-face sweep hits without
culling, and culling of perpendicular faces. Unproved initial side agreement, degeneracy, scale,
continuation, tree structure or work refuses atomically. Inner-tree bounds are proved at mutation,
with closed checked traversal at query time. Sparse large meshes use pruning rather than a total
triangle cap. Sphere and capsule leaves use closed point/segment distance with the exact sum of
represented radii. Cylinders use continuous side, cap-disc and rim-circle predicates, with whole-cell
support bounds for separation. Numerical closest points only propose witnesses or directions.
An unproved isolated rim event refuses atomically. Curved radii and half-lengths stay within 2048 m
and use the same forward/operational correspondence, error and work ceilings. The public cylinder's exact
single-child base-offset wrapper is retained. General compounds admit the installer's actual flat
box, hull, sphere, capsule and cylinder children, including nested seam flattening and hull centering.
Every child completes before publication, including after Hit(0), within 64 children per root and
the shared 4096-descendant account. Children are not pruned independently. Mesh children remain an
installation refusal, and installed nested compounds and BigCompound remain unsupported.
Non-rigid composition must still complete the existing leaf proof. An enclosed tangent face without
a robust witness can reach depth 48 and refuse atomically despite a forward-oracle Hit.

Mutation completion encloses actual installed corners, hull vertices, curved radial sets or mesh
descendants and the rounded point graph, repairs inward bounds through Bepu's owning tree update/refit
path, and validates ancestors before publishing evidence.
Each capture authenticates its registration id and installation generation against the live owner
binding. Recycled handles and identical replacement geometry cannot authenticate a previous capture.
Selected unproved bound families refuse even outside the raw aperture. View/mobility exclusions retain
their source meaning and cannot hide invalid stored evidence. Failed or missed physical mutations stay
refused after unrelated successful mutations. Leases remain owner-scoped. Each restricted view applies
its own exclusions through the same owner interval.

Policy version 1 retains 8192 nodes, 4096 leaves and 128 pending branches per tree, 4096 aggregate
examined callbacks including exclusions, and 4096 unproved registrations. General queries admit at
most 4096 primitive descendants, 4096 feature regimes per primitive and 256 fraction cells at depth
at most 48 per primitive. Exact predicates use checked retained 4096-bit integers and a bounded
2048-number workspace. Every exhausted or unproved step returns the exact default Unresolved result.
Closed quadratic minima include isolated double roots and endpoint tangency. Clear encodes the full
original vector. Hit bounds never grant placement permission. Warm shape/value queries, exact boundary
fallback and refusal allocate no managed bytes and retain scratch capacity. Each reader retains up to
four general-sweep workspaces for nested loans. First use provisions each depth once, warmed nesting
allocates zero bytes, and a fifth simultaneous loan returns default Unresolved without allocation.
Acquired explicit cost scenes and captured player-scale hull stairs are covered by the
[general sweep acceptance record](../docs/design/CERTIFIED-GENERAL-SWEEPS-2026-10-18.md#task-6-captured-content-evidence-and-gb-r13).
Lane C resolves #1494: exact quarter-turn tread-edge footprints now complete under represented
support and the original split/compound stair rows advance and reach the unchanged Euclidean
1 mm arrival ball at both physics origins. Historical GB-R13 refusals are retained in that record.
Native G1b, SG-12/#1334 timing and
aggregate actor cost, navigation, release and consumer adoption remain separate gates.

## Support source and curved work contract

Support admits the represented binary32 Bepu matrix on each actual installed leaf, composed with
Compound.GetWorldPose before capture. Planar vertices, normals and mesh preimages derive from
that point-valued affine map. Circular primitives use its exactly normalized represented Y column
with outward arithmetic bounds, preserving radius and half length. Legacy feature/sweep factories
retain their dual contract. Scalar/SIMD correspondence, finite domains and determinant guards remain.

A complete Find or direct footprint shares one sticky 1,048,576-event account, derived from
256 raw-member slots times 4,096 source nodes. New, Sign, recursive Split/Compile, root cells,
Refine iterations and admission cells charge before execution, including cache-hit entries.
Reset, Restore, later parts, proposals and the below-band pass never refund work. Diagnostics
expose category counts, cell high water and first failure WorkLimit, RootStorage, TerminalRoots
or AdmissionStorage. Every failed request refuses atomically with untouched spans and no prefix.

The footprint-only whole-capsule crown certifies the attained global maximum by source identity.
Cylinder admission first offers a clamped nearest top-line XZ point or nearest cap point, with
only a decline from interval pruning and exact source proof for acceptance. A miss retains full
traversal. Neither certificate bypasses axis/lowest roles, full admission, proposal/roof guards,
other members or raw identity. The [lane C record](../docs/verification/2026-10-19-contact-followups-c.md)
resolves #1494 and #1487 while retaining historical results and every safety/arrival rule.

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
