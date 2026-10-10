# KhaozEngine.MapDoc.Physics

Physics for a native [KhaozEngine.MapDoc](../KhaozEngine.MapDoc) world. It reads the collision data a verified
asset closure carries as [KhaozEngine.Physics](../KhaozEngine.Physics) shapes.

It references exactly `KhaozEngine.MapDoc`, `KhaozEngine.Physics` and
[KhaozEngine.Movement](../KhaozEngine.Movement). It carries no renderer, no physics backend and no TileWorld, so the
caller picks the `IPhysicsWorld` (add [KhaozEngine.Physics.Bepu](../KhaozEngine.Physics.Bepu) explicitly for the
shipped one).

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
with no extent belongs to the tile holding its minimum. `Affected` names the owners and tiles an edit invalidates
from its old and new bounds and the chunks of every listed patch, including the chunks of the wall strips recorded in
it.

`MapTileResidency` remains the streaming loader keyed by storage tile. This index is the ownership R8 consumes.
