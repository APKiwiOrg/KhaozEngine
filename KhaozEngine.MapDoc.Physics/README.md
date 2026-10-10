# KhaozEngine.MapDoc.Physics

Physics for a native [KhaozEngine.MapDoc](../KhaozEngine.MapDoc) world. It reads the collision data a verified
asset closure carries as [KhaozEngine.Physics](../KhaozEngine.Physics) shapes.

It references exactly `KhaozEngine.MapDoc`, `KhaozEngine.Physics`, [KhaozEngine.Movement](../KhaozEngine.Movement)
and [KhaozEngine.Locomotion](../KhaozEngine.Locomotion), for the contact shell. It carries no renderer, no physics
backend and no TileWorld, so the caller picks the `IPhysicsWorld` (add
[KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the shipped one).

The package is opt-in and in no umbrella. Add it explicitly.

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

- a collision payload does not read as exactly one well-formed shape,
- a compound holds a triangle mesh, which the physics backend cannot install,
- the asset declares a support resource, because placement-local support surfaces are not supported yet.

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
`MapResidencyOwnership.Affected` names by the seam margin, so a consumer rebakes only those tiles.

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
