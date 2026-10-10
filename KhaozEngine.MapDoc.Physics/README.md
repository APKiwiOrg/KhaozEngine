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
points. Penetration deeper than 0.001 m, or a sweep hit before a segment's end, is `Blocked`. A shell that starts in
contact no deeper than that sweeps its first segment from 0.001 m clear of the contact along the minimum translation
vector, so a path may leave a wall it starts touching but not enter it. A point outside the
document's playable bounds is `Unknown` for either relation. Every result carries the world's `BuildHash` and its
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
