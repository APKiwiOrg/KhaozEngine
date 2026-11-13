# KhaozEngine.TileWorld.Physics

Turns an authored [KhaozEngine.TileWorld](../KhaozEngine.TileWorld) world into static colliders for any
[KhaozEngine.Physics](../KhaozEngine.Physics) world, plus a floor sampler and a water medium sampler for
[KhaozEngine.Locomotion](../KhaozEngine.Locomotion).

It references exactly those three packages. It carries no renderer and no physics backend, so the caller picks the
`IPhysicsWorld` (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the shipped one).

The package is opt-in and in no umbrella. A tile world needs no physics to walk, render or serve.
Add it explicitly when a game needs physics under its tile world, together with its chosen backend.

The [water prism census](../test-shared/WaterPrismCensus/README.md) supplies fixed Contact workloads,
opt-in exact-work counters and an indicative timing command for nonuniform beds.

## Use

```csharp
TileWorldColliders colliders = TileWorldColliders.Build(document, catalogs);   // options default when omitted
using var fullWorld = new BepuPhysicsWorld();
using TileColliderRegistration registration = colliders.AddTo(fullWorld);
using IPhysicsWorldQueryView movementView = registration.CreateMovementQueryView();

body = CharacterMovement.Step(body, command, dt, colliders.Ground.HeightDelegate, MoveTuning.Default,
                              colliders.Ground.NormalDelegate, movementView,
                              medium: colliders.Medium.MediumDelegate);
GroundMoveContext context = new(
    colliders.Ground.HeightDelegate,
    colliders.Ground.NormalDelegate,
    physics: fullWorld,
    clampXz: null,
    medium: colliders.Medium.MediumDelegate,
    movementQueries: movementView);
```

`TileWorldColliders.Build(document, catalogs, options)` describes the loaded regions as data and registers
nothing. `AddTo(fullWorld)` adds every collider as a static and returns a `TileColliderRegistration`, whose
`Remove()` or `Dispose()` takes them out again. The complete world retains its ground meshes for simulation,
general queries and navigation capture. `CreateMovementQueryView()` is lazy and creates a non-owning view that
excludes only this registration's analytic ground, so walls, blocked tiles, objects, walk surfaces and later
non-ground additions remain visible to movement. Use that view for `CharacterMovement.Step` and as
`movementQueries` in the explicit six-argument `GroundMoveContext`, with `physics: fullWorld` for capture.
`Ground` and `Medium` are the samplers, and their `HeightDelegate`, `NormalDelegate` and `MediumDelegate` are
the ground-height, ground-normal and medium arguments of the movement APIs, each created once.

`GroundHandles` is the cached read-only subset of installed ground in canonical registration order. Rejected
ground has no handle or exclusion. The subset retains its metadata
after `Remove`, just like `Handles`, but removed handles cannot be used to create a new view. If a backend does
not support the optional query factory, `AddTo` still registers successfully and the view request throws
`NotSupportedException`. For multiple registrations on one source, combine their live `GroundHandles` and call
the generic factory once with that union. When replacing a registration, dispose its view first, remove the old
registration, add the new registration and create a new view. Dispose movement contexts and query views before
the registration, and the caller-owned source world last. Query selection never removes installed ground from
the complete capture world.

After an edit, remove the old registration, build again and add the new colliders. The ground sampler reads the
document live, but its bounds were captured at build, and the medium sampler snapshots the water. Rebuild after edits.

For the ordinary contact controller, use `Build(document, catalogs, worldId)` and
`registration.CreateMovementRegistration(worldId)`. The same identity names the captured water field and
installed support owners. Pass `MovementGroundHeight`, `MovementGroundNormal` and `Medium` to
`EffectiveMovementBinding.Ordinary`, together with that registration and the playable boundary.
The binding creates its movement view with ground handles excluded. Ground meshes remain installed in the
complete physics world. Use `CreateMovementRegistrationBuilder(worldId, combinedExtent)` to retain tile owners
and exclusions while adding other installed statics. Build that combined registration under a fresh source lease.

A complete ground installation also attaches optional `OrdinaryMovementRegistration.AnalyticSupport`.
The captured source offers `IMovementFlatAnalyticSupportSource` for exact constant-height regions, including
flat cell seams. `IMovementSupportedAnalyticSupportSource` also captures bounded sets of drawn triangles for
supported slope arrivals. Its immutable region reuses `DrawnGroundTriangle.Select` and the ordinary barycentric
height expression and clamp, including cell and triangle boundary ties. It never substitutes a plane equation.
Missing triangles, steep regions or an unenclosed query keep live movement previews.
[Analytic support provenance](../KhaozEngine.Locomotion/README.md#analytic-support-provenance)
defines the certificate contract.
It recognizes the exact captured `MovementGroundHeight` and `MovementGroundNormal` delegates in world coordinates.
Wrapping those delegates requires a separately authenticated source, even when their sampled values agree.
The canonical key qualifies packed signed tile X/Z and the triangle ordinal with the world and immutable captured
lattice fingerprint. It uses the same drawn-triangle selector as the field. Shared-edge adjacency comes from the
original integer half-tile lattice, including partial edge joins at corner cuts. It does not join faces by height.
Keys survive a rebase and are independent of local handles. Editing the source document cannot mutate the captured
identity. Rebuilding edited geometry produces new provenance. No authored file format changes are needed.

Holes, clamped exterior samples, unknown keys and a partial installation that omits any ground collider supply no
analytic identity. Ordinary movement and its existing fallback sampling remain unchanged. Only an identity consumer
must refuse that unavailable evidence. This capability does not create terrain navigation links.

Retain the returned `OrdinaryMovementRegistration` for `ApplyRuntimeStatics` batches between movement reads.
Use `CreateMovementRegistrationBuilder(worldId, combinedExtent)` to reserve possible runtime height and width
before binding, even for a flat tile world. The default tile extent does not reserve future walls automatically.
Runtime Set poses are absolute world metres. Existing Contact and Legacy selected views include these real
statics without rebuilding the tile document, authored colliders or `TileWorldHash`. Runtime entries never join
`Handles`, `GroundHandles` or `ColliderIndices`. Sanctioned removal releases the runtime shape. Baked installation
removal still owns only baked handles, and the complete physics world owns remaining runtime entries.

The contact delegates capture terrain layers at build. Resident islands may share a declared bounding extent.
An analytic query inside a non-resident hole answers unresolved, while physics and water remain available.
Outside the loaded bounding rectangle, captured analytic terrain extends the nearest edge like the legacy sampler.
`MovementExtent` contains the loaded terrain range, every transformed collider and finite water columns,
including objects outside their anchor region.

`TileMediumSampler` implements `IMovementWaterField` with immutable body identities, finite beds and free surfaces.
Its optional `IMovementDryTracePointProof` accepts only a trace whose directed lateral capsule-path bounds are
disjoint from every captured column projection. Such points return Known dry before any rounded drawn-bed
lookup, even at large coordinates. An overlapping projection declines the proof, including dry regions below
beds or above water, because the separate bed lookup can refuse. This adds no tolerance and does not relax any
trace precision or capacity check. The bound uses the same immutable column index as point selection.
A point is in a column when the exact framed centre lies in X [minimum, maximum), Z (minimum, maximum] and its
feet in [bed, surface). Support and water share captured drawn triangles, triangle selection and anchored height
evaluation. The lower boundary follows the selected triangle at the queried axis, including sloped beds and a
level triangle beside a lower corner. A triangle at or above the free surface contributes no positive water volume.
The internal shape evaluator supports a query preparation with ordered, first-use projection, clip and
post-feasibility terms. Each stored term is the original interval expression, with its operand order unchanged.
Window clipping, minima, root clamping, seam certification and first-witness selection remain per evaluation.
An unavailable or changed floating point control token takes the direct evaluator. Sticky status is outside
the coverage contract. On a memo miss, coverage prepares each selected prism and face separately once per trace, and reuses
that preparation for span, point and dry evaluations. One per-thread temporary workspace supports up to 176
bases and ten planes. Full-window hits alone are retained, at their actual basis and plane sizes, in an
optional 4 MiB per-thread slab. Misses consume no retained storage. Exhaustion takes the direct evaluator,
without adding a refusal or consuming output capacity. Every offset expires at trace end. Repeated traces
allocate no preparation storage after warm-up. After a complete full-window evaluation, a basis rejected
by its clip chain is skipped for later valid subwindows of [0,1]. Other window orders and invalid or
out-of-range windows replay the original clip chain. Feasible distance misses still contribute their error
and nearest distance. Each plane expansion and each normal/start or normal/drive interval dot is filled
once per preparation on first use. Normal/q and normal/v dots retain their original per-basis expressions.
Geometry-only basis terms are filled on first use and cached on the shape under their floating point control
token. A different control domain computes them directly and never replaces the original entry. Unsupported
control domains use direct evaluation. Immutable entries are published atomically, so parallel readers cannot
see a partially initialized entry. Determinant failures retain their original basis ordinal and predicate.
Trace coverage uses water prisms bounded by those triangles and clipped at the body's free surface, including
exposed patches and dry volume. Internal triangle and same-body tile edges contribute no dry wall.
For the canonical coplanar triangle pair in one raw column, a grazing bed-diagonal root can partition an
already continuously intersected boundary. Its path-width error is redundant only after directed bounds prove
a represented, strictly wet bed point inside the actual capsule throughout that root's clamped uncertainty window.
The affine normal displacement retains dependence on the path fraction. Entry and exit windows are checked
independently, including overlaps and query endpoints. Each unproved window keeps its own charge, so a later
real bed departure retains its original roots and contacts without requiring bed contact for the whole query.
The proof pays column rounding in XYZ, proves its ground lookup still selects the same tile, and retains the
original ranges, cuts, witnesses, overlap tags and contact order. Exact plane, normal and owner equality are
required. Creases, other columns, free surfaces and
real side or corner roots keep their original error charges. An unproved interval still refuses at the same budget.
Point and trace queries receive the physics-local body plus its frame origin. Exact predicates select the
lateral column, then the bed uses the same world-axis mapping as ordinary support. An immutable column index
searches the actual rounded XZ bounds,
including large tile addresses whose rounded faces move across several nominal tiles. Candidate generation
does not assume a fixed neighbouring-tile radius and never turns a missing candidate into a false dry fact.
Equal-height bodies in different regions keep distinct identities. Internal same-body faces are suppressed.
Capacity refusals publish no prefix. `MediumAt` and `MediumDelegate` retain the legacy surface-only behavior,
including water below a finite certified bed. Legacy builders derive a stable content identity independent of
`DisplayName`. Explicit identities remain the adoption door for matching server and client worlds.

If the world refuses a static partway through `AddTo`, the statics already added are removed and the world's
exception is rethrown. If that rollback fails too, `AddTo` throws an `AggregateException` whose first inner
exception is the add failure. `Remove` tries every handle once even when one fails, then rethrows the one failure as it was
or several as an `AggregateException`. A second call retries only the handles that failed.

## Filtered installation

The overload `AddTo(IPhysicsWorld world, Func<TileCollider, bool> include)` selects whole colliders before
physical insertion. For example, this neutral policy omits object boxes:

```csharp
using KhaozEngine.TileWorld.Physics;
using KhaozEngine.Physics;

Func<TileCollider, bool> include = collider => collider.Kind != TileColliderKind.Object;
using TileColliderRegistration selected = colliders.AddTo(fullWorld, include);
for (int i = 0; i < selected.Handles.Count; i++)
{
    TileCollider original = colliders.Colliders[selected.ColliderIndices[i]];
    StaticHandle installed = selected.Handles[i];
}
```

The predicate runs synchronously once per collider in canonical order, with absolute document-space poses,
before reading `world.Origin` or inserting any static. Keep it pure: no mutation of the document, shapes or
world, reentrant installation or concurrently changing inputs. Decisions are snapshotted, and the registration
never calls the predicate again. Null arguments refuse. A predicate exception propagates unchanged with no
insertion. Selected adds share the default rollback transaction and failure ordering.

`Handles` is compact installed order. `ColliderIndices[i]` is its original `Colliders` index, strictly increasing.
Both lists are immutable metadata retained after removal. The default overload installs every collider, with
indices `0..Colliders.Count-1`. Default and all-true calls preserve add order, poses, owners, exclusions, extent,
semantic digest and same-build serialized bake bytes. Historical tests normalize only engine-version identity
and its framing lengths. Production engine-version load refusals still apply.

Selection copies the original canonical owners, aliases, feature ranges and absolute transformed bounds,
including partially selected placed owners. It retains the complete captured `MovementExtent`, even when
ground or everything is omitted. Analytic ground, water and resident coverage remain available. Only installed
ground enters `GroundHandles` and movement exclusions. `PhysicsNavBake.Capture` samples the installed physical
set, so omitted ground supplies no capture floor even though the analytic sampler remains available.

Keep the source alive through contexts, leases and capture, and unchanged within each read interval. The ordinary
registration remains bound to its exact source. Equal handle values in another world cannot authenticate it.
Raw static mutation invalidates authentication. Sanctioned runtime batches publish their own provenance and live
revision while retaining authored identity. Bepu steps and supported rebases preserve static provenance.
Keep navigation capture authored-only and fixed throughout its reads. Removing any selected baked handle prohibits new
movement registration from that installation. Dispose contexts and views before removing the registration,
then dispose the source last. Rollback can still advance source generation history.

### Persist the installation policy

`colliders.Hash` always identifies the complete built sequence, independent of selection. The ordinary live
semantic digest distinguishes selected provenance, but a persisted bake also needs an explicit policy source:

```csharp
using System.Text;
using KhaozEngine.Movement;

// Canonical UTF-8 with explicit LF, naming both policy version and inputs.
byte[] policyBytes = Encoding.UTF8.GetBytes("omit-kind/v1\nkind=Object\n");
var sources = new NavBakeSources().AddHashOf("world", canonicalWorldBytes);
sources.AddHashOf("policy/colliders", policyBytes);
GroundNavigationBake bake = GroundNavigationBake.Create(capture, sources, profiles);

// Runtime reconstructs the same canonical inputs, including the policy entry.
var runtimeSources = new NavBakeSources().AddHashOf("world", canonicalWorldBytes);
runtimeSources.AddHashOf("policy/colliders", policyBytes);
var expected = new NavBakeExpectation(options, runtimeSources, profiles);
NavBakeLoadResult loaded = GroundNavigationBake.Load(stream, expected);
```

Include every other proof input in both source builders too. Change the policy digest whenever its semantics
or inputs change. Contact registration, live environment identity, format 2 and runtime profile validation do
not insert this entry or replace it. The bounded integration proof loads each policy's bake with its matching
expectation, then changes only `policy/colliders` and receives `SourcesChanged` naming that source, with the
complete collider hash and tuning unchanged. Consumer material policies belong in the consumer.

## Types

- **`TileWorldColliders`**: `Build`, `Colliders` (every `TileCollider` in canonical order), `Hash`, `Ground`,
  `Medium` and `AddTo`.
- **`TileCollider(Kind, Shape, Pose)`**: one static. `TileColliderKind` is `Ground`, `Wall`, `Blocked`, `Object` or
  `WalkSurface`, and its declaration order is the canonical order.
- **`TileColliderOptions`**: `WallThickness` (0.1 m), `BlockedHeight` (null means the document's plane height) and
  `WalkSurfaceThickness` (0.1 m). `Build` refuses a value that is not a finite number above 0 with a
  `TileColliderOptionsRangeException` carrying `TileColliderOptionsRefusal.OptionNotFinitePositive`, `OptionName`,
  `ParamName = options` and the numeric `ActualValue`.
- **`TileColliderRegistration`**: compact `Handles`, original `ColliderIndices`, selected `GroundHandles`,
  `Remove` and `Dispose`.
- **`TileGroundSampler`**: `HeightAt(worldX, worldZ)` and `NormalAt(worldX, worldZ)`.
- **`TileMediumSampler`**: `MediumAt(worldX, worldZ, feetY)`.

## Refusals

Inspect the reason and identifiers rather than parsing the developer-facing message.
`TileColliderOptionsRangeException` derives from `ArgumentOutOfRangeException`, so existing range catches still
match. Missing collision heights keep the exact `TileWorldException` type.

| Exception and reason | What it refuses | Identifiers |
| --- | --- | --- |
| `TileColliderOptionsRangeException`, `TileColliderOptionsRefusal.OptionNotFinitePositive` | A thickness or set blocked height that is not finite and positive | `OptionName`, `ParamName = options`, `ActualValue` |
| `TileWorldException`, `TileWorldRefusal.ColliderCollisionHeightMissing` | A collidable placed archetype without a collision height, other than a `Solid` or `Diagonal` one that lists collision boxes | `ArchetypeId`, `CollisionKind`, `ObjectId`, `Tile` including its known plane |

## What becomes a collider

- **Ground:** one triangle mesh per region with any drawable tile, the triangles `TileGroundTriangles.Build` gives
  the renderer. Backend meshes are one-sided, so each triangle's second and third index are swapped to turn its
  front face up, the same swap the terrain's collision mesh makes.
- **Wall:** one box per edge a placed `Wall` or `WallCorner` blocks, as the collision baker reads its rotation,
  `WallThickness` thick and centred on the edge.
- **Blocked:** one box per tile with no underlay or marked `TileSettings.Blocked`, from the tile's lowest corner to
  its highest corner plus `BlockedHeight`. The tile collision map refuses the whole tile, so the box stands above
  every point of its ground, and a body cannot step onto a steep blocked tile from the high side.
- **Object:** a box over a `Solid` object's rotated footprint, or over a `Diagonal` object's anchor tile. When the
  archetype lists `collisionBoxes`, one box per authored box instead, in catalog order (see below).
- **WalkSurface:** a box `WalkSurfaceThickness` thick under each walk surface's top, over its rectangle as
  `TileWalkSurfaces` resolves it.

A wall or footprint object box's top is the height the model stands at, `TileObjectPlacement.AnchorPosition`, plus
the archetype's `CollisionHeight`. Its bottom is the lower of the lowest ground corner under the box and that
standing height, so it never inverts on a steep slope. Footprint, anchor-tile and wall boxes are axis-aligned over
the tiles the collision baker blocks, so they stand where the editor's and the server's tile collision says. On a
quarter turn a walk surface stays axis-aligned with its half extents swapped as the turn needs.

**Authored collision boxes.** An archetype's `collisionBoxes` (field rules in the
[`KhaozEngine.TileWorld` README](../KhaozEngine.TileWorld/README.md#catalogs-and-validation)) replace the footprint
or anchor-tile box of a `Solid` or `Diagonal` object, so a bench is its seat and its back. A body stands on the seat at
the seat's own top and walks into the footprint's empty strips, where one footprint box would have held it up at the
back's height over the whole 2x1. The placement is the one rule `ke-tileedit` draws and reports with:

- **Planar:** the box's local centre goes through the object's anchor and planar basis exactly as a walk surface rect
  does, and its axes turn by the whole turn, the instance rotation plus the yaw offset plus the box's own
  `yawDegrees`. A whole turn that is a whole number of quarter turns is an axis-aligned box with its half extents
  swapped on an odd one. Any other turn is a box yawed about +Y. The centre turns with the object alone, so it is
  exact when the object's turn is a whole number of quarter turns, and the axes are exact when the whole turn is.
- **Vertical:** the top is the standing height plus the box's `maxY`, a surface a capsule stands on. The bottom is the
  standing height plus `minY`, and a box resting on or sunk below the base (`minY` at or below 0) reaches further down
  to the lowest ground corner under its own plan bounds when that is lower, the rule the footprint box keeps, so on a
  slope it meets the ground on the downhill side instead of leaving a gap a foot catches under. A box raised off the
  base keeps its authored bottom, so the space under a table top or an awning stays open. The corner search is
  clamped to the loaded regions, beyond which the ground is only edge-extended.
- **Collision height:** not read. Every box carries its own top, so a boxed archetype needs no `collisionHeight`.
- A box edited in memory after load into one that cannot be a volume (null, non-finite, inverted, or too thin to
  survive being added to its standing height in a float) gets no collider.

The tile collision map is unchanged by boxes: the object still blocks its footprint or anchor tile there, the
reasons are in the [`KhaozEngine.TileWorld` README](../KhaozEngine.TileWorld/README.md#collision-derived-and-never-authored).
So a body can stand on a seat or in a footprint's empty strip that the map calls blocked, and `CanStep` lets it walk
out.

**The floor is the drawn ground only.** `Ground` answers from the ground triangles and never from a walk surface or
an object top. A deck, a crate or a bridge holds a body up only as a physics static, so a body can walk under a
high deck on the ground and stand on it from above.

**Every Wall and WallCorner archetype, and every Solid and Diagonal one without collision boxes, needs a
`collisionHeight`.** `Build` throws a `TileWorldException` with
`TileWorldRefusal.ColliderCollisionHeightMissing`, `ArchetypeId`, `CollisionKind`, `ObjectId` and `Tile` when a
placed one has none. `ke-tileedit` fills them from the kit:
`archetype_measure_heights` reads each model's top and `archetype_set_collision_heights` writes the heights into the
catalog files.

## Coordinates

Colliders and samplers speak document world coordinates, absolute metres from `TileWorldSpace`. `AddTo` subtracts
`world.Origin` from each pose once, at registration, and a later `Rebase` is the backend's job. The samplers stay
absolute, so a caller stepping a body in a rebased world adds the origin to the coordinates it hands them and takes
it off the heights they answer:

```csharp
Func<float, float, float> height = (x, z) =>
{
    Vector3 o = fullWorld.Origin;
    return colliders.Ground.HeightAt(x + o.X, z + o.Z) - o.Y;
};
Func<float, float, Vector3> normal = (x, z) =>
{
    Vector3 o = fullWorld.Origin;
    return colliders.Ground.NormalAt(x + o.X, z + o.Z);
};
Func<float, float, float, MovementMedium> medium = (x, z, feetY) =>
{
    Vector3 o = fullWorld.Origin;
    return colliders.Medium.MediumAt(x + o.X, z + o.Z, feetY + o.Y) is { InWater: true } m
        ? new MovementMedium(m.WaterSurfaceY - o.Y, inWater: true, m.WadeSpeedScale)
        : MovementMedium.Dry;
};

body = CharacterMovement.Step(body, command, dt, height, MoveTuning.Default, normal, movementView, medium: medium);
```

Each delegate reads `fullWorld.Origin` when it is called, so the wrapped delegates stay right across later rebases.
Movement keeps using the selected view while the complete source is rebased between steps.

## Samplers

`HeightAt` and `NormalAt` answer in three cases, with no search and no allocation per call. On a drawable plane-0
tile they take the height and up normal of the ground triangle under the point. A point outside the rectangle
bounding the loaded regions is first moved just inside it, so the floor carries on level past the world's edge. On a
tile with no ground (no underlay, `TileSettings.NoDraw`, or a missing region inside the rectangle) they answer the
document's lattice height, `TileWorldDocument.HeightAt`, with a straight up normal. They never answer NaN.

Triangle heights interpolate differences from one lattice vertex. A level triangle keeps that exact lattice
height, so certified support and the water field use the same bed boundary.
The evaluated height stays inside the selected triangle's exact vertex range, including rounded seams. This
prevents a rounding undershoot from creating support or water below a level that all drawn vertices bound.
Captured support and water borrow the same immutable triangle records. The water semantic identity includes
their complete captured geometry and loaded-region arrangement, so edits that change a sampled bed invalidate
the field identity even when the tile's minimum corner stays unchanged.

`MediumAt` is in water only when the point is over a water body and `feetY` is below that body's surface, so a body
standing on a deck above a river is dry. The surface is the body's rim less `TileWaterBodies.SurfaceDropMetres`. An
in-water answer is always built through the `MovementMedium` constructor and leaves the wade speed to the tuning.

The legacy samplers have no shared scratch. The ground sampler needs its document to remain unedited while read.
The water field and certified ground delegates retain immutable captured data. Water tracing uses bounded
per-thread scratch and precomputed exterior topology. A proved constant captured bed can use an exact rectangular
union specialization. Immutable bounds trees select flat columns and their boundary and exterior patches in original
source order. A whole capsule path with no potential column certifies a single dry interval immediately, without
geometric intersections. Nonuniform-bed selection uses its own captured column tree. Fixed candidate storage is
only an optimization: overflow retains the original full scan and all original refusal limits. Trace certificates include represented height-evaluation and geometric arithmetic error.
Point membership retains exact half-open comparisons, and an error beyond the existing coverage limit refuses
the whole trace. A finite over-budget certificate reports `MovementAvailability.PrecisionExceeded` without
publishing spans or contacts. Unresolved geometry and unavailable evidence keep their existing refusal kinds.
Independent readers do not share mutable scratch.

Prism traces also retain exact query results in the sampler instance, collected with its captured world.
Four thread tables are claimed without waiting. Each holds 128 entries and replaces the oldest insertion.
A fixed 256-bucket hash index uses the same FIFO slots and full-key equality after a hash match.
Each result retains at most 8 spans and 48 contacts. Caller space identifiers over 256 characters are not
retained. Hits allocate nothing and a warm table adds no allocation on a miss. The key preserves every query
float bit, including signed zero, the uncombined physics origin, both space identifiers, the received buffer
lengths and the calling thread's floating point controls. A hit reproduces every output bit, caller-owned
references, caller buffer contents, refusal kinds and capacities, and all validation outside the kernel.
Identity and input validation still run per call. Replayed contacts carry the current caller's space string
references, as recomputation would. A joined-triangle boundary id that recomputation allocates fresh is replayed
by string value, without a new allocation or a reference-identity guarantee for that string.
Refusals retain their reported capacities and leave caller buffers untouched. Known results copy only their
written prefixes from owned storage. Native control reads cover rounding and denormal input/output handling
without changing the environment. Sticky floating point status flags are outside this equivalence contract.
Direct kernel evaluation can leave different sticky status for the same inputs across compiled tiers, and
validation around the sampler also performs arithmetic. Executed-work counters deliberately measure actual
work, so a hit does not count skipped kernel work. Controls remain part of the key because rounding and denormal
handling affect numeric results. Windows x64 uses its control-only reader and can memoize. Platforms without a
supported control reader bypass the memo.
Entries survive ticks, but thread changes, eviction, large results and exhausted thread slots can prevent reuse.
A hit is an optimization, never a guarantee. The flat specialization and point queries do not use the memo.

An owned lateral column with no reachable captured bed answers unresolved. Represented loaded rectangles use
directed lower and upper bounds, retaining a coordinate even when the nominal region is narrower than one float step.

These providers were audited for a parallel contact navigation bake, `NavBakeExecution` above degree 1 in
[KhaozEngine.Movement](../KhaozEngine.Movement/README.md#parallel-contact-bake), and are endorsed by method:
`MovementGroundHeight` and `MovementGroundNormal`, `Ground.HeightAt`, `NormalAt`, `HeightDelegate` and
`NormalDelegate` while the document stays unedited, `Medium` as a water field (`SampleCentreWater`, `TraceWater`,
`SemanticIdentity`, `Generation`, `Extent`) and through `MediumAt` and `MediumDelegate`, and the registration that
`CreateMovementRegistration` or `CreateMovementRegistrationBuilder` builds.

## Hash

`Hash` is SHA-256 over every collider in order: its kind byte, its shape as `PropCollisionFormat.Write` writes it,
and its pose as seven little-endian floats (position x, y, z, then orientation x, y, z, w). Filtering installation
does not change this complete-set hash or create an installed-subset hash. The same world and
catalogs hash equal on every rebuild, whatever order the regions were loaded in. Ground meshes and every box on a
quarter turn use no trigonometry, so they hash exactly across architectures. A walk surface on an object turned off a
whole number of quarter turns, or a collision box when its object's turn or its own whole turn is off one, takes its
centre or orientation from `MathF` trigonometry, which can differ in the last bits between x64 and ARM64, and so can
the hash of a world that has one. Collision boxes are
colliders like any other, so an edit to one moves the hash, and a navigation bake that folds the hash into its
sources reports `SourcesChanged` until it is rebaked. A catalog without boxes builds exactly the colliders, and so the
hash, it built before the field existed.

## Limits

- Plane 0 only: its ground, its objects and their walk surfaces. Anything on another plane, every roof and an object
  whose archetype the catalogs do not define get no collider. A plane-0 `Solid` roof is still blocked by the tile
  collision map, so the two disagree there.
- A `TileSettings.NoDraw` tile has no ground triangle and no blocked box, so it is a hole in the physics floor.
- Nothing collides at the edge of the loaded world or across a missing region inside it. The floor sampler carries on
  level there, so a body keeps walking on an invisible floor. Author a blocked border around the playable area.
- A down ray exactly on an outer edge of the drawn ground, the loaded world's boundary or a drawn tile beside an
  undrawn one, may miss under the backend's half-open triangle edges. Interior lattice lines and region seams hit.
  The movement floor is the sampler, not a ray, so a walking body never depends on that line, and a void tile
  beside it carries a blocked box.
- No `.coll` prop shapes, hulls or round primitives. Every object collider is a box, either over the footprint or
  one of the archetype's authored collision boxes, so a round prop is approximated by boxes turned against each other.
