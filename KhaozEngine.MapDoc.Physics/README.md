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
sampling belong to the #438 contact controller, which stands on the statics this package installs. A native
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
`ColliderSha256` and `SelectionSha256` are the verified resource digests.

An asset is solid exactly when it declares a collider. A selection volume alone makes it examinable without
blocking.

`Read` throws `MapDocumentException` when:

- a collision payload does not read as exactly one well-formed shape, or has bytes after it,
- a compound holds a triangle mesh, which the physics backend cannot install,
- a hull or mesh has no points,
- the asset declares a support resource. Placement-local support surfaces (`Surface` resources) arrive with R5, and
  until then the refusal names it.

`PropCollisionFormat.Read` refuses a non-finite or non-positive box half extent or cylinder size, a non-finite hull
point, mesh vertex or compound child pose, a mesh index count that is not a multiple of 3 or an index outside the
vertices, a compound with no children, compound nesting deeper than 16 levels, and a compound child orientation that
is not a unit quaternion. `Read` reports each as a payload that cannot be read.

## Placement geometry and interaction envelopes

```csharp
IReadOnlyList<MapPlacementGeometry> placements = MapPlacementShapes.Resolve(resolvedDocument);
MapBox3 bounds = MapShapeBounds.Of(shape, pose);
```

`MapPlacementShapes.Resolve` gives every placement of a resolved document its geometry, in ordinal placement order.
The collider is scaled once by the asset's source units times the placement scale and placed at `WorldPose`, the
placement position with its yaw. `ColliderBounds` is its world bounds, null for an asset with no collider. `Digest`
covers the placement and asset identities, both resource digests, the float bits of position, yaw, combined scale and
raise, and `MapInteractionPolicy.Hash`. A combined scale that is not finite and positive, an asset with neither a selection
volume nor a collider and an envelope that cannot be swept refuse with `MapDocumentException`.

`MapShapeBounds.Of` computes tight world bounds in scalar double under the physics seam's conventions: boxes,
spheres and capsules are centred on their pose, a cylinder stands on its base and spans `Length` along its local Y,
hull and mesh points are relative to their pose, and compound children compose their local pose.

`MapInteractionEnvelope` is the volume a placement is picked and reached through, under policy
`kemap/interaction-envelope/1` (`MapInteractionPolicy`). Its source is the selection volume when the asset has one,
otherwise the collider of a solid asset. A source shorter than `MinimumVerticalReachHeightMetres` (1 m) is swept
straight up in world space until it is that tall, and a doorway's opening survives because an unraised envelope is
the scaled source unchanged. Envelopes shape interaction only. They are never installed and never block.

## Terrain physics

```csharp
MapTerrainChunkSet chunks = MapTerrainPhysics.Compile(view, new MapTerrainChunkPolicy(MaxTrianglesPerChunk: 1024));
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

`Build` refuses a partial window, options that do not match the resolver identity, a missing legacy support height
for resolver 1, every resolution, placement and terrain refusal, a static beyond `MapWorldBuilder.MaxCoordinateMetres`
(1,000,000 m) on an axis and two statics with one owner id.

### Feature query diagnostics

`MapBuiltWorld.Diagnostics` lists, in static order, every limit of the backend's capsule feature query a static
meets, as a `MapStaticDiagnostic(OwnerId, Support)`. A limit is reported, never refused, because the static still
collides and blocks. A static that meets none is `Supported` and absent from the list.

| `MapFeatureQuerySupport` | Meaning |
| --- | --- |
| `LeafCapacity` | A compound flattens to more than 64 leaves |
| `CurvedUntilPhase2b` | A sphere, capsule or cylinder leaf, which the query captures from phase 2b |
| `MeshTriangleCapacity` | A triangle mesh over 65,536 triangles |
| `LocalExtent` | A leaf whose installed geometry reaches more than 64 m from its own origin on an axis |
| `HullCapacity` | A convex hull leaf with more than 130 points |

`HullCapacity` is conservative. The backend captures at most 256 faces and 1,524 face entries, and a hull with V
vertices has at most 2V - 4 faces and 6V - 12 face entries, so a hull of 130 points or fewer always fits. Any hull
over 130 points is flagged, even one the query would still capture.

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
smallest placement id. A box turned about world Y alone is measured through `ReachGeometry`, whose sine and cosine
come from the platform math library, so heads that must agree pass a `tolerance` to `Within`. `MapShapeQueries`
holds the same point, capsule and ray tests for one shape at a pose.

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
contact controller.

Any failure during `Register` removes what it added, in reverse order. `Dispose` removes every static in reverse
order, never disposes the physics world, and leaves the statics a failed removal kept in `Handles` so a later call
retries them. Never register or dispose inside a held query read lease: the backend refuses mutation then.

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

`MapTileResidency` remains the streaming loader keyed by storage tile. This index is the ownership R8 consumes.

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
tile. The result stays inside the edited world's partition.

`Partition` refuses a world whose bounds reach more than `MapNavTiling.MaxPartitionTiles` (2^20) navigation tiles, and
the refusal names the count.

Tile and capture bounds are residency extents, not probe windows. A resolver 1 world's bounds exclude its analytic
terrain height, so a capture picks its own vertical window.

Navigation tiles are columns in X and Z. Vertical layers on native worlds wait for #438 phase 5.

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

Terrain chunks are one-sided, so a ray or sweep meeting a face from behind passes through it. Interaction envelopes
are never installed and never block. A portal is an opening with no faces, so an authored open portal needs no
extra state.

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

## Deferred work

| Item | Owner | Status here |
| --- | --- | --- |
| Native F3 G1b adapter with `MovementQueryLease` and the complete capsule resolver | R3 amendment | After #438 phase 4 releases those types |
| Vertical-layer navigation capture on native worlds | #438 phase 5 | `MapNavTiling` provides tile, seam and link identity only |
| Incremental rebake orchestration, planning budgets, on-demand loading | #1301 | After #438 phase 5 |
| Multi-cell ghost handoff proof | R8 and G3 | `MapWorldGrids` and `MapResidencyOwnership` provide grid mapping and ownership |
| Placement-local support surfaces (`Surface` resources) | R5 | `MapAssetShapes.Read` refuses them |
| Live portal and door state | R5 | Portals are authored open |
| Water medium and walker profiles | R4 and #1299 | `CreateLegacyMoveContext` leaves the medium delegate null |
| Clearance body model confirmation | Owner, #1344 | Clearance uses the #438 shell through `ContactShell` |
| Editor placement edits reporting old and new placement bounds with Physics, Nav and Residency (P16) | R3 follow-up | Placement commands report `Placements` only for now |
