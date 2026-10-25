# KhaozEngine.MapDoc.Physics

Physics for a native [KhaozEngine.MapDoc](../KhaozEngine.MapDoc) world. It reads the collision data a verified
asset closure carries as [KhaozEngine.Physics](../KhaozEngine.Physics) shapes and builds one immutable headless world
from a document: placement geometry, interaction envelopes, terrain physics chunks with face provenance, feature
query diagnostics and a build identity. It installs that world into any `IPhysicsWorld`, answers pick, reach and
physical relation queries against it, and names the residency owners and navigation tiles an edit invalidates.

It references exactly `KhaozEngine.MapDoc`, `KhaozEngine.Physics`, [KhaozEngine.Movement](../KhaozEngine.Movement)
and [KhaozEngine.Locomotion](../KhaozEngine.Locomotion), for the contact shell. It carries no renderer, no physics
backend and no TileWorld, so the caller picks the `IPhysicsWorld` (add
[KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the shipped one).

The package is opt-in and in no umbrella. Add it explicitly.

## Ownership boundary

This package owns geometry, interaction envelopes, physical relations, residency, registration and navigation tile
identity. It adds no step limit, ledge rule or support model. Ground support, seating, steps and navigation column
sampling belong to the #438 contact controller, which will stand on the statics this package installs. A native
(resolver 2) world installs its terrain as statics. A resolver 1 world keeps its analytic ground behind
`MapBuiltWorld.LegacySupportHeight` and installs no terrain statics.

| Area | Produced here |
| --- | --- |
| Asset shapes | `MapAssetShapes` |
| Placement geometry | `MapPlacementShapes`, `MapPlacementGeometry`, `MapShapeBounds` |
| Interaction envelopes | `MapInteractionEnvelope`, `MapInteractionPolicy` |
| Terrain physics | `MapTerrainPhysics`, `MapTerrainChunkPolicy`, `MapTerrainChunk`, `MapTerrainChunkSet` |
| World build | `MapWorldBuilder`, `MapWorldBuildOptions`, `MapBuiltWorld`, `MapStaticDescriptor`, `MapStaticKind`, `MapLegacySculptTile` |
| Diagnostics | `MapStaticDiagnostic`, `MapFeatureQuerySupport` |
| Queries | `MapWorldQueries`, `MapShapeQueries`, `MapPickRay`, `MapPickHit`, `MapInteractionBand` |
| Stances | `MapStanceCandidates`, `MapStanceOptions`, `MapStanceValidator` |
| Registration | `MapPhysicsRegistration`, `MapStaticOwner` |
| Physical relations | `MapPhysicalRelations`, `MapPhysicalResult` |
| Residency | `MapWorldGrids`, `MapServerCellCoord`, `MapResidencyOwnership`, `MapResidencyEntry`, `MapAffectedSet` |
| Navigation tiles | `MapNavTiling`, `MapNavTileOptions`, `MapNavTileCoord`, `MapNavTile`, `MapNavSeam`, `MapNavLink` |

## Asset shapes

```csharp
MapAssetClosure closure = MapAssetClosure.Load(roots, source);
MapAssetShapes shapes = MapAssetShapes.Read(closure, "doorway");
if (shapes.IsSolid)
    Install(shapes.Collider!, shapes.SourceUnitsToMetres);
```

`Collider` and `Selection` resources carry `PropCollisionFormat` version 1 bytes. `MapAssetShapes.Read` reads each
declared resource and keeps the shapes in asset source units. `SourceUnitsToMetres` is the scale the caller applies.
`ColliderSha256` and `SelectionSha256` are the verified resource digests. `SupportResources` carries detached
MapDoc payload-1 descriptors. Their lattice frames already declare metres, so asset source units are not applied
to support again. MapDoc installs and remaps those surfaces before resolving missing-Y placements. Physics then
compiles that captured effective view into statics with canonical face owners. A support resource never installs a
duplicate collider floor.

An asset is solid exactly when it declares a collider. A selection volume alone makes it examinable without
blocking.

`Read` throws `MapDocumentException` when:

- a collision payload does not read as exactly one well-formed shape, or has bytes after it,
- a compound holds a triangle mesh, which the physics backend cannot install,
- a hull or mesh has no points,
- a support resource has no validated MapDoc support payload.

`PropCollisionFormat.Read` refuses a non-finite or non-positive box half extent or cylinder size, a non-finite hull
point, mesh vertex or compound child pose, a mesh index count that is not a multiple of 3 or an index outside the
vertices, a compound with no children, compound nesting deeper than 16 levels, and a compound child orientation that
is not a unit quaternion. `Read` reports each as a payload that cannot be read.

## Placement geometry and interaction envelopes

```csharp
IReadOnlyList<MapPlacementGeometry> placements = MapPlacementShapes.Resolve(resolvedDocument);
MapBox3? placementBounds = MapPlacementShapes.Bounds(resolvedPlacement, MapAssetShapes.Read(closure, assetId));
MapBox3 bounds = MapShapeBounds.Of(shape, pose);
```

`MapPlacementShapes.Resolve` gives every placement of a resolved document its geometry, in ordinal placement order.
The collider is scaled once by the asset's source units times the placement scale and placed at `WorldPose`, the
placement position with its yaw. `ColliderBounds` conservatively covers every permitted whole-metre registration
origin, including frame-local float rounding. It is null for an asset with no collider. Use `MapShapeBounds.Of` at
`WorldPose` for tight physical dimensions at that pose. `Digest`
covers the placement and asset identities, both resource digests, the float bits of position, yaw, combined scale and
raise, and `MapInteractionPolicy.Hash`. A combined scale that is not finite and positive, an asset with neither a selection
volume nor a collider and an envelope that cannot be swept refuse with `MapDocumentException`.

`MapPlacementShapes.Bounds` reports the tight union of the collider at the resolved pose and its interaction
envelope. Registration-origin allowances stay in `ColliderBounds` for world residency and navigation, rather than
changing this reported interaction extent. It returns null instead of refusing when the asset declares
neither a collider nor a selection volume, so an editor can size an edit of such a placement. World builds keep
refusing it.
The additive `Bounds(placement, shapes, out spatialBounds)` overload also returns the conservative collider and
envelope union for edit invalidation, from the same geometry resolution. Use that spatial extent for Physics,
Nav and Residency consequences.

`MapShapeBounds.Of` delegates to `Physics.PropQueryGeometry.Bounds` and computes tight world bounds in scalar double under the physics seam's conventions: boxes,
spheres and capsules are centred on their pose, a cylinder stands on its base and spans `Length` along its local Y,
hull and mesh points are relative to their pose, and compound children compose their local pose.

`MapInteractionEnvelope` is the volume a placement is picked and reached through, under policy
`kemap/interaction-envelope/1` (`MapInteractionPolicy`). Its source is the selection volume when the asset has one,
otherwise the collider of a solid asset. A source shorter than `MinimumVerticalReachHeightMetres` (1 m) is swept
straight up in world space until it is that tall, and a doorway's opening survives because an unraised envelope is
the scaled source unchanged. Envelopes shape interaction only. They are never installed and never block.

## Terrain physics

```csharp
using MapTerrainChunkSet chunks = MapTerrainPhysics.Compile(view, new MapTerrainChunkPolicy(MaxTrianglesPerChunk: 1024));
```

`Compile` groups R2's compiled support floors, ceilings and wall strip sides of a complete view into one-sided
triangle meshes of at most `MaxTrianglesPerChunk` triangles (64 to 1,024), each within 64 m of its whole-metre
`Anchor` on every axis. Faces are R2's, never retriangulated. Each `MapTerrainChunk` has a stable `ChunkId`, its
`TriangleOwners` and `TriangleRoles`, and a `Digest`. `MapTerrainChunkSet` carries the chunks in ordinal id order,
the view's read witness and the count of legacy fallback cells, which compile to no faces. An incomplete view, a slot
cell over the cap ("physics chunk capacity") and an unresolved wall strip chain refuse with `MapDocumentException`.

## World build

```csharp
var options = new MapWorldBuildOptions(resolveOptions, ConsumerPolicyIdentity: "my-game/rules/1",
    new MapTerrainChunkPolicy(), LegacySupportHeight: field.SampleHeight);
MapBuiltWorld world = MapWorldBuilder.Build(document, closure, options);
```

`MapWorldBuilder.Build` resolves a complete document, resolves placement geometry, compiles resolver 2 terrain and
lists the installable statics: placement colliders, then terrain chunks, each in ordinal owner order, as
`MapStaticDescriptor` values with a `MapStaticKind`, a shape in metres, a pose, world bounds, a digest and, for a
terrain chunk, its triangle owners. `BuildHash` (domain `kemap/built-world/1`) covers the authored hash, builder
version, consumer policy, interaction policy, chunk policy, legacy terrain identity and every placement and chunk
digest. A resolver 1 world also carries its `LegacySculptTiles` (`MapLegacySculptTile`), sculpt cell size, terrain
block digest and `LegacyTerrainIdentity`, so a consumer covers only the sculpt tiles a region touches.

Prefab and unpacked complete builds validate every effective room coverage finding even without water.
Diagnostic expansion remains available for inspecting ambiguous membership. Independent coincident support floors
and collider floors duplicated by support surfaces are refused.

`Build` refuses a partial window, options that do not match the resolver identity, a missing legacy support height
for resolver 1, every resolution, placement and terrain refusal, a static beyond `MapWorldBuilder.MaxCoordinateMetres`
(1,000,000 m) on an axis and two statics with one owner id.
The released world-limit refusal precedes effective placement-coordinate representability checks.

### Scoped simulation ownership

`MapScopedWorldBuilder.Build` prepares one admitted `MapScopedScene` under its captured byte allowance. Patch,
strip, shape decode and per-instance workspace releases after its consumer. Independent terrain meshes, placement
shapes, water and descriptor containers keep one retained receipt on the returned `MapScopedWorld`.
Dispose a directly built scoped world after its last consumer. `MapSimulationCoverage.Acquire` owns its worlds
and neutral coverage until `MapSimulationLease.Dispose`, and releases unpublished worlds if a later domain refuses.
Navigation partitions, seams and links derived from a scoped world share that world's lifetime. Their workspace
releases on return and their retained descriptor inventory joins the world receipt. Navigation rejects disposed
worlds. `MapTerrainChunkSet` and `MapAssetShapes` also release their own metered preparation credit on disposal
when used as standalone products.

### Feature query diagnostics

`MapBuiltWorld.Diagnostics` lists, in static order, every limit of the backend's capsule feature query
(`IPhysicsCapsuleFeatures`) a static meets, as a `MapStaticDiagnostic(OwnerId, Support)`. A limit is reported, never
refused, because the static still collides and blocks. A static that meets none is `Supported` and absent from the
list.

| `MapFeatureQuerySupport` | Meaning |
| --- | --- |
| `LeafCapacity` | A compound flattens to more than 64 leaves |
| `CurvedLeaf` | A sphere, capsule or cylinder leaf, which the capsule feature query does not capture |
| `MeshTriangleCapacity` | A triangle mesh over 65,536 triangles |
| `LocalExtent` | A leaf whose installed geometry reaches more than 64 m from its own origin on an axis |
| `HullCapacity` | A convex hull leaf with more than 130 points |

`HullCapacity` is conservative. The backend captures at most 256 faces and 1,524 face entries, and a hull with V
vertices has at most 2V - 4 faces and 6V - 12 face entries, so a hull of 130 points or fewer always fits. Any hull
over 130 points is flagged, even one the query would still capture.

Every diagnostic predicts `IPhysicsCapsuleFeatures` capture and nothing else. The #438 contact controller's
foot-support primitive certifies explicit movement through `IPhysicsSupportFootprint`, which has different
limits. It captures sphere, capsule and cylinder leaves, so `CurvedLeaf` does not apply to it. It reads mesh
triangles in the complete admitted foot column, so `MeshTriangleCapacity` does not apply either, and it caps
one query at 256 relevant raw elements. It shares the 64 leaf limit of `LeafCapacity` and the hull face limits
behind `HullCapacity`. Local vertex admission for feature and footprint queries is 2048 m, with precision
enforced per query by unchanged publication ceilings, exact-arithmetic capacity and the 256 m incidence bound.
Curved leaf sizes retain their separate 64 m limit, and full capsule-feature mesh capture retains its
explicit 64 m subset. Complete footprints apply the 2048 m vertex domain to admitted mesh triangles.
`LocalExtent` still describes the builder's separate 64 m chunk diagnostic. It does not predict this admission
domain, and a mesh it flags may still pass a footprint query. One unsupported
static in the column refuses the whole footprint query. Native adoption remains separately gated.

`MapBackendPoseAdapter` is the explicit frame-local binary32 approximation of a retained `MapEffectiveTransform`.
`ErrorMetres` bounds origin, scale, quaternion and local-coordinate rounding. Semantic support stays exact.
Collider registration bounds compose every nested child in binary32 with `Pose.Compose`, the same operation used
by the installer. They retain the rounded leaf poses before applying the full registration-origin allowance, and
union the resulting extents with mathematical bounds. Intermediate offsets may exceed the final leaf extent.
The final rotation and quaternion product use a-priori `gamma_n` bounds on absolute input terms, per component.
Translation and extent addition have separate rounding bounds. Cancellation cannot shrink the allowance, and the
enclosure covers scalar and SIMD evaluation orders. Positive term arithmetic and final extrema round outward.
`MapPrefabSupportBounds.Bounds` measures transformed physical support without narrowing occupied-space partitions.
Each terrain chunk retains outward-rounded `CanonicalBounds` alongside its submitted mesh. Its `Bounds` unions
both, so exact support crossing a tile seam survives inward float vertex rounding. World bounds include these
effective support extents as well as render, LOD, asset light, emitted light and water extents.
Doorway compounds retain their jambs and lintel under one transform. Headless clearance proves a physical opening
only. Native walking routes remain with G1b and #438 phase 5.

## Roof and annotation intent

`MapBuiltWorld.Lights`, `Markers`, `RoofLinks` and `RoofGroups` are immutable captured annotation collections.
Links carry transformed world-metre heights. `AnnotationDigest` identifies the explicit roof inventory, links,
groups and effective versioned light and marker values. It also enters `BuildHash` when annotations are present.
Worlds without annotations keep the released hash path.

Evaluate `MapRoofPolicy` over `world.Document.EffectiveResources` and `world.Surfaces` to obtain independent colour,
shadow, camera-boom and sky-picking flags. Every mode leaves full static descriptors, registration handles and
interaction geometry unchanged. These are R8 presentation inputs, rather than a collision hide predicate.

`MapResidencyOwnership.BuildAnnotations(world, grids)` supplies separate `light:{id}` and `marker:{id}` entries.
Light extents use the effective position and scaled metre radius with outward rounding. Markers occupy their exact
world point. Both drive residency invalidation and neither registers a physics shape. Light extents join the world
bounds. Markers do not, so a marker-only edit never changes the world extent, a static or a navigation capture identity.
`Build` remains the static-only ownership list and `BuildWater` remains the compiled-water list.

Format-9 builds consume `kemap/effective-resources/1` with query policy 2 from the captured MapDoc snapshot.
Rebuild identity-keyed world and navigation captures when adopting 20.40.0, including prefab-free native maps.
AuthoredOpen doorway clearance is a geometry proof. Controller walking and layered routes still require G1b
and #438, and intent does not supply R8 pixels or live door state.

## Independent synthetic village heads

`MapPrefabVillageAcceptanceTests.IndependentHeads_ResolveIdenticalEffectiveVillage` builds two heads
from separately loaded format-7 documents and independently verified copies of the same invented
resource bytes. It compares build hashes, static owner/digest sequences, compiled water, annotation
identity, room domains and parent membership, water samples and roof intent at a fixed probe list.
Missing prefab bytes refuse before a built world can be installed. The village also exercises real
backend doorway clearance and the one-world-metre minimum reach envelope for a small scaled item.

The four definitions and five instances share support, partial ground paint, two storeys, local
water, markers, lights and a cross-building roof group. A roomless canopy keeps its unlinked roof
visible in Auto. Concealment leaves statics intact. The acceptance covers headless geometry and
ownership. The [MapDoc handoff table](../KhaozEngine.MapDoc/README.md#public-synthetic-village-acceptance)
names the remaining controller, route, rendering, streaming, marker and importer proofs.

`MapMarkerVillageAcceptanceTests` builds client and server heads of the invented marker village from separately
loaded monolithic and tiled documents with the same tile size and independently verified resource bytes. Both heads
and both forms agree on complete marker value rows, height provenance, point membership, half-open rectangle
queries, annotation digests and build hashes. Support heights equal the named owner's float answer, exact points
keep negative X, and yaw compares modulo a turn. The server's whole load lists every root-resident marker, and a
tiled manifest names all six from root metadata without reading a tile body.

A marker-only move or removal through `MapMarkerEdits` reports Residency alone. Static descriptors, world bounds and
every navigation tile capture identity stay equal, while the annotation digest and build hash move because markers
are authored data. The moved marker leaves its old storage tile and enters the new one exactly once, and
`Affected(before, after, grids, effects)` names its owner and both tiles. A removed marker's owner leaves the
annotation list and its old membership stays in the affected set. This proves marker residency only. It does not
solve whole-cell residency (#341).

Markers never join the world bounds that set navigation tile heights. The village's `actor` stands above every
static, and removing it leaves the world extent and every capture identity unchanged. `MapMarkerBoundsTests` sets a
marker above and one below every static and proves the same, with the marker's residency entry still installed.

## Native foliage physical boundary

`MapFoliageWorldTests` builds independent heads and both monolithic/tiled forms on invented stacked-support and
released prefab-cave fixtures. `IndependentHeadsAndStorageForms_ProduceIdenticalOwnedFoliage` compares owned CPU
samples, while `DensitySeedRuleAndMask_NeverChangeStaticsNavOrWorldBounds` proves cosmetic edits leave statics,
placement envelopes, navigation captures and world bounds equal. `ColliderBuilder_ReferencesNoFoliageType` checks the
builder type boundary. `MapFoliageHandoffBoundaryTests.HandoffCosmeticExtents_NeverChangeNavOrStatics` extends that proof
to model extents and motion padding. This package consumes no foliage render batch and installs no foliage collider.

| Consumer | Boundary and remaining proof |
| --- | --- |
| R7 | Complete CPU ownership and independent-head acceptance, with the [MapDoc evidence table](../KhaozEngine.MapDoc/README.md#public-foliage-acceptance-and-deferred-ownership) |
| R8a and R8b | R8a supplies windowed context, one render owner across memberships, upload and viewport drawing. R8b retains the interior-cut adapter [#1474](https://github.com/APKiwiOrg/KhaozEngine/issues/1474) |
| R9 and R10 | Interactive painting/input and complete wire parity, including native placement ID rename |
| R11/G2 and R8/G3 | Actual importer/source acceptance and appearance acceptance, respectively |

The [MapDoc foliage contract](../KhaozEngine.MapDoc/README.md#native-foliage-consumer-contract) owns full-request support,
canonical identity, ordinal fallback, typed refusal and atomic observer completion. Sparse storage does not prove
partial-residency generation by itself. `MapWindowFoliageContext` now supplies the separate scoped closure
and witness. `MapSimulationResidency` uses independent required-domain leases for statics and navigation.
Scene-oracle submission checks and native capture/backend evidence do not replace those simulation
leases. The [render verification contract](../KhaozEngine.MapDoc.Render3D/README.md#native-scene-verification)
separates source roster, frame and pixel proofs from physical admission.

## Pick, reach and stances

```csharp
var queries = new MapWorldQueries(world);
MapPickHit? hit = queries.Pick(new MapPickRay(eye, forward, MaxDistance: 6f));
bool inReach = queries.Within(body, "door-1", range: 1.5f, tolerance: 0.001f);
float gap = queries.PhysicalDistance(body, "door-1");
IReadOnlyList<Vector3> stances = MapStanceCandidates.Find(world, "door-1", actorFeet, tuning,
    new MapStanceOptions(Spacing: 0.5f, Range: 1.5f), controller.TrySeat);
```

`MapWorldQueries` runs in scalar double with no physics backend, so a client and a server built from one document
agree. `Pick`, `Distance` and `Within` query interaction envelopes only, clipped to an optional absolute
`MapInteractionBand`. `PhysicalDistance` queries colliders only. Equal pick distances resolve to the ordinally
smallest placement id. `MapShapeQueries` holds the same point, capsule and ray tests for one shape at a pose.
Its ray route delegates to `Physics.ShapeRayQueries`, while movement distances retain their released contract.
`MapInteractionEnvelope` delegates its upward sweep to `Physics.PropQueryGeometry.RaiseEnvelope` after admission.
The MapDoc wrappers preserve their exception categories and the 1 m interaction minimum.

Heads agree except for one residual dependency on the platform's double sine and cosine. A placement's world pose
takes the sine and cosine of half its yaw in double and rounds each to float once, so no float trigonometry enters
bounds, residency, navigation tile contents or pick. A last-bit difference in the double result shows only when it
rounds across a float boundary. A box turned about world Y alone is measured through `ReachGeometry`, which also
takes its yaw's sine and cosine in double, so a difference there shows only exactly at a range boundary. Heads that
must agree pass a `tolerance` to `Within`.

`MapStanceCandidates.Find` walks each envelope member's XZ outline, outset by the capsule radius, at `Spacing` and
proposes walk-up positions: at the envelope's base for a native world, at the legacy support height for resolver 1.
The `MapStanceValidator` the game binds to its controller's placement proof seats or refuses each proposal, and
owns every step, ledge, slope and support rule. Seats outside reach or the playable bounds are dropped.

## Registration

```csharp
using MapPhysicsRegistration registration = MapPhysicsRegistration.Register(world, physics);
if (rayHit.Body is { } body && registration.TryOwner(body, out MapStaticOwner? owner)) Report(owner.OwnerId);
registration.TryFaceOwner(feature.Target, incident.FaceId, out MapFaceKey canonicalFace);
```

`Register` installs every static of a built world into a caller-owned `IPhysicsWorld`, in static order, at
`Position - physics.Origin`. The origin must be whole metres within `MaxCoordinateMetres`, or it refuses with
"whole-metre origin". `TryOwner` maps a handle back to its placement id or terrain chunk id. `TryFaceOwner` maps a
feature query's incident face id on a terrain chunk to its canonical `MapFaceKey`. A feature id is not a triangle
index, since edges and vertices number after faces. `CreateLegacyMoveContext` gives a resolver 1 world a ground move
context over its analytic support height and playable bounds. A native world refuses it, since it moves on the
contact controller. The parameterless overload supplies no medium. Opt into resolver-1 global water with
`CreateLegacyMoveContext(legacyGlobalWater: true)`: feet strictly below `MapBuiltWorld.LegacyWaterLevel` are wet.

Any failure during `Register` removes what it added, in reverse order. `Dispose` removes every static in reverse
order, never disposes the physics world, and leaves the statics a failed removal kept in `Handles` so a later call
retries them. A removal that throws `ObjectDisposedException` counts as done, since a disposed physics world's statics
are gone with it, so disposing the physics world first is safe. Never register or dispose inside a held query read
lease: the backend refuses mutation then.

## Residency ownership

```csharp
var grids = new MapWorldGrids(StorageTileSize: 64f, NavTileStorageTiles: 2, ServerCellStorageTiles: 4, Vector2.Zero);
IReadOnlyList<MapResidencyEntry> entries = MapResidencyOwnership.Build(world, grids);
IReadOnlyList<string> owners = MapResidencyOwnership.InWindow(entries, window);
MapAffectedSet affected = MapResidencyOwnership.Affected(world, grids, effects);
```

`MapWorldGrids` aligns three grids. The storage tile is the document's own tile. A navigation tile and a server
cell are whole blocks of storage tiles counted from an origin on a storage tile seam, and a server cell matches
Sharding's `CellGrid` for the same size and origin. Grids that do not align refuse with "grid alignment".

Each static belongs to every storage tile its world bounds reach, minimum inclusive and maximum exclusive. An axis
with no extent belongs to the tile holding its minimum. A placement is keyed by its origin's tile, as
`MapSpatialIndex` stores it, and a terrain chunk by the tile holding its minimum corner. `Affected` takes the world
built after the edit and names the owners and tiles the edit invalidates from its old and new bounds and the chunks of
every listed patch, including the chunks of the wall strips recorded in it.

`Affected(before, after, grids, effects)` also retains removed owner IDs and their old memberships.
The matching `AffectedTiles(before, after, grids, options, effects)` retains old capture tiles, including tiles
that leave the post-edit partition. Preparation must widen effects with the old and new extents of leaves reseated
by a support edit. The 150 m leaf counterexample at a 64 m seam proves why patch-only bounds miss neighbor tiles.

`MapNativeInvalidation.Unbounded` is a full reset: discard all prior derived state for this world and rebuild from
the post-edit world. `Affected` then lists every static as an owner and every storage tile the post-edit world's
bounds reach. Pre-edit extents outside the post-edit world cannot be listed, which is why a consumer must reset rather
than patch.

`MapResidencyOwnership.MaxStorageTiles` (2^20) caps every storage tile list: one static's membership, and each extent
`Affected` lists. A static or extent that reaches more refuses with `MapDocumentException` naming the count before
any tile is listed.

`MapTileResidency` remains the streaming loader keyed by storage tile. The GPU-free `MapSimulationResidency` consumes this ownership independently of visual windows.

## Navigation tiles

```csharp
var options = new MapNavTileOptions(SeamMarginMetres: 2f, ProfileIdentity: "profile-a", ControllerIdentity: "legacy-stepper");
IReadOnlyList<MapNavTile> tiles = MapNavTiling.Partition(world, grids, options);
IReadOnlyList<MapNavSeam> seams = MapNavTiling.Seams(tiles, world, options);
IReadOnlyList<MapNavLink> links = MapNavTiling.Links(world, grids);
IReadOnlyList<MapNavTileCoord> rebake = MapNavTiling.AffectedTiles(editedWorld, grids, options, effects);
```

`Partition` lists every navigation tile the world's bounds reach, by the residency membership rule. A tile's
`CaptureBounds` is the tile grown by the seam margin on X and Z, so neighbouring captures overlap at every seam. Its
`GeometryDigest` covers the digest of every static whose residency bounds meet the capture bounds, in ordinal owner
order, and for a resolver 1 world the terrain block and every sculpt tile whose footprint meets them. Its
`CaptureIdentity` adds the profile, the controller, the seam margin and the tile, so a tile keeps its identity until
something it can see changes.

`Seams` digests each pair of neighbouring tiles over both geometry digests and their shared edge. `Links` lists one link
for each R2 cave portal or vertical link whose aperture reaches two navigation tiles, with a digest over the record id,
the semantic digests of the record and its aperture records, and both tiles. `AffectedTiles` widens the navigation tiles
`MapResidencyOwnership.Affected` names by the seam margin, so a consumer rebakes only those tiles. A resolver 1 terrain
edit also widens by the footprint of every sculpt tile position its bounds meet, whether the edit added or removed that
tile. The result stays inside the edited world's partition. An edit flagged `MapNativeInvalidation.Unbounded`, a full
reset, lists every tile of that partition without listing storage tiles first, under the same `MaxPartitionTiles`
refusal as `Partition`, and still refuses an edit box beyond 1,000,000 m. A tile that leaves the partition, because the
edit shrank the world, is never listed, so a consumer that keeps state per tile diffs the old and new partitions to
find removed tiles.

`Partition` refuses a world whose bounds reach more than `MapNavTiling.MaxPartitionTiles` (2^20) navigation tiles
before it places any static, and the refusal names the count. `MapWorldGrids.Validate`, which every grid consumer runs
first, refuses a grid origin so far from the world that a navigation tile or server cell index of the world's bounds,
widened by one block on each side, leaves the int range. The refusal names the grid alignment and the tile range. A
seam margin or edit box reaching past the int range beyond the world refuses naming the tile range too.

Tile and capture bounds are residency extents, not probe windows. A resolver 1 world's bounds exclude its analytic
terrain height, so a capture picks its own vertical window.

Navigation tiles are columns in X and Z. Vertical layers on native worlds wait for #438 phase 5.

## Water

Bounded water is built-world data and never a static, so it registers nothing with physics and never blocks a ray or a
capsule. `MapWorldBuilder` compiles a native document's water section over its whole-document view and refuses, with
"water: " and the detail, any body that does not compile complete. `MapBuiltWorld.Water` carries the compiled bodies
and `WaterMedia` the section's media, so the media travel with the built world. `WaterDigest` and every body digest
enter the build hash only when the section is present.
The build captures the water set and its digests, checks render material closure and containment, and refuses
incomplete geometry or a counted compile limit, including `MapWaterCompileLimits.MaxBoundCells`. Later edits to the
caller's set cannot change the built world.

`MapResidencyOwnership.BuildWater` gives each body a `water:{bodyId}` residency owner over the storage tiles its
region triangles reach. A placement and a body may share an id, but the builder refuses any static owner matching
that body's water owner. A complete body with regions but no bounds is refused too. A navigation capture carries a
`MediumDigest` only when a water triangle meets it with positive XZ area. Exact clipping excludes bounding-box
overreach and edge-only contact. The digest enters its capture identity, while its geometry digest is unchanged.
An edit flagged `MapNativeInvalidation.Medium` names bodies in `WaterBodyIds`, and `Affected` maps those ids to water
owners while `AffectedTiles` invalidates their capture tiles. Body ids alone do not invalidate water residency.
Each body digest covers only what that body read, so an edit elsewhere leaves its tiles alone.
R4 supplies data and queries only. Native movement uses the G1b adapter after #438 phase 4. R8a supplies current-look native rendering. Optical
algorithms remain with R8b. Format 6 moves native root digests and resolver-2 authored identities once even without
water, so navigation captures and caches keyed on them rebuild once.

## Physical relations

```csharp
var relations = new MapPhysicalRelations(registration);
using IPhysicsQueryLease lease = ((IPhysicsQueryLeaseSource)physics).AcquireQueryReadLease();
MapPhysicalResult sight = relations.LineOfSight(lease, eye, target);
MapPhysicalResult walk = relations.Clearance(lease, feetPath, tuning);
```

Both relations run under a held read lease over the registration's own physics world and call `AssertCurrent` before
any query. A disposed registration refuses with `ObjectDisposedException`. Points convert from their frame to world
double, less the lease's origin. Queries see statics only.

Line of sight casts one ray. A hit nearer than the segment length less 0.001 m is `Blocked` and names its owner, a
placement id or a terrain chunk id. A hit within 0.001 m of the end is `Unknown`, and a miss is `Clear`. Clearance
places the locomotion `ContactShell` at the first feet point and sweeps it along each segment of a path of 1 to 64
points. The shell is tested in place at every path point, the final one included. Penetration deeper than 0.001 m
there is `Blocked` at that point with no owner. Contact no deeper than that pushes the segment's start 0.001 m clear of
the contact along the minimum translation vector, and a zero-length segment keeps the previous push-out. Each moving
segment is swept from its pushed-out start, so its sweep ends at its end point plus that push-out, and a block
distance is measured from there. A sweep hit before the segment's length is `Blocked`. A path may slide along or leave
a wall it touches but not enter it, and a verdict never hides a point that sits deeper than 0.001 m. Between points, a
sweep shifted by its push-out, at most 0.002 m, can pass a convex corner where an added point would be tested deeper
than 0.001 m, so splitting a path can change the verdict only within that band. A point outside the document's
playable bounds is `Unknown` for either relation. Every result carries the world's `BuildHash` and its
terrain witness digest.

Terrain chunks are one-sided meshes, so a line of sight ray meeting a face from behind passes through it. A clearance
sweep sees no back-face culling, the same filter the #438 contact controller's shell sweeps use, so a face met from
behind blocks clearance as it blocks movement. Only the controller's support probes set `QueryFilter.CullBackFaces`.
Interaction envelopes are never installed and never block. A portal is an opening with no faces, so an authored open
portal needs no extra state.

This departs from R2's planned D5 signature in four ways:

- Invalid input throws rather than returning an `Invalid` status.
- A stale lease throws through `AssertCurrent` rather than returning `Stale`.
- There is no `Facts` input. Geometric relation facts stay with `MapSpaceRelations`, and these results add only
  physical certainty.
- A single-point clearance path is allowed. D5 required 2 to 64 points, and the 64-point cap stays.

## Editor tooling

`NativeMapAssetLoader.Load(asset, closure)` in [KhaozEngine.Terrain.Render3D](../KhaozEngine.Terrain.Render3D)
loads a native asset's verified mesh with its source units applied once and no height normalization, so the mesh
agrees with the collider this package reads. The [ke-mapedit](../KhaozEngine.MapEdit.Tool) `NativeCollisionService`
measures a placement's mesh top against its collider and resizes box colliders through content-addressed resources.

Editor placement edits report what they invalidate. `KhaozEngine.MapEditor` takes no reference to this package.
It defines `INativePlacementBounds`, bound next to the asset closure, and ke-mapedit's `NativePlacementBoundsProvider`
implements it with `MapPlacementShapes.Bounds` over the session resolve options. A placement add, remove, move,
rotate, scale or asset change then reports `Physics`, `Nav` and `Residency` with the changed placements' bounds
before as `OldBounds` and after as `NewBounds`. A label change reports `Placements` only, and so does an edit whose
changed placements have neither a collider nor a selection volume on either side. With no provider or no closure
bound, a geometry change reports `Unbounded` with the three flags and no bounds. The provider never refuses an edit:
a placement whose support does not resolve or whose asset shapes are refused marks its side unknown, and the edit
reports `Unbounded` the same way. Collider height edits size their placements through the same provider.
The provider separately carries conservative installed geometry as `NativePlacementExtent.SpatialBounds`.
`OldSpatialBounds` and `NewSpatialBounds` transport it in the effects while preserving the tight report.
Residency affected tiles and navigation capture invalidation consume those spatial extents. Effects without
separate spatial values fall back to their reported bounds.

## Deferred work

| Item | Owner | Status here |
| --- | --- | --- |
| Native F3 G1b adapter with `MovementQueryLease` and the complete capsule resolver | R3 amendment | After #438 phase 4 releases those types |
| Vertical-layer navigation capture on native worlds | #438 phase 5 | `MapNavTiling` provides tile, seam and link identity only |
| Incremental rebake orchestration, planning budgets, on-demand loading | #1301 | After #438 phase 5 |
| Multi-cell ghost handoff proof | R8 and G3 | `MapWorldGrids` and `MapResidencyOwnership` provide grid mapping and ownership |
| Live portal and door state | [#1363](https://github.com/APKiwiOrg/KhaozEngine/issues/1363) | Portals remain authored open |
| Clearance body model confirmation | Owner, #1344 | Clearance uses the #438 shell through `ContactShell` |
| Support certification of curved statics, creases and one-sided back faces | #438 phase 2b | Landed. Staged complete-footprint discovery uses `IPhysicsSupportFootprint` for explicit movement, and diagnostics here still predict only `IPhysicsCapsuleFeatures`. Native adoption remains separately gated |
| Editor placement edits reporting old and new placement bounds with Physics, Nav and Residency | R3 | Done, see Editor tooling |
| GUI native editing: `MapEditorScene`, hosted by Showcase, binds no closure or placement bounds provider yet | R3 follow-up, [#1349](https://github.com/APKiwiOrg/KhaozEngine/issues/1349) | Wiring it needs the provider reachable from that host, in this package or a shared adapter, since Showcase does not reference ke-mapedit |
| Import-time acceptance of changed targets, distances, occlusion and stances | R11 and G2 | Named differentials at import |
| Terrain edits that reseat support-bound placements without widening their tiles | #1348 | `Affected` widens terrain edits by patch extents only |
