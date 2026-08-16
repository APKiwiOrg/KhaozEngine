# KhaozEngine.TileWorld

The OSRS-style tile world: a discrete grid of 64x64 tile regions with stacked planes, a global tile-corner
height lattice, ground and overlay materials, tile-anchored objects and named markers, saved as a hash-checked
directory. GPU-free and render-free, so client, server, editor and authoring tool read one document through one
path. A sibling of `KhaozEngine.MapDoc`, not an extension of it. Design rationale:
`docs/design/TILE-WORLD-DESIGN-2026-08-15.md` in the engine repo.

## Coordinates and rotation

**World tile coordinates everywhere.** `TileCoord(X, Z, Plane)` is a world address, x east and z north, and so
are `TileObject.X/Z` and `TileMarker.X/Z`. The owning region is always `RegionCoord.Of(X, Z)`, which floors, so
a negative coordinate lands in a negative region with a local coordinate in 0..63. **Rotation is quarter turns
clockwise from above**, 0 west, 1 north, 2 east, 3 south, on objects, overlay shapes and prefab stamps.

**World z is MINUS tile z, and `TileWorldSpace` is the only place that knows it.** The engine renders in a
right-handed space with y up, where a camera facing +z has +x on its left, so mapping the document's north
(+tile z) onto +world z renders the world mirrored against a compass and makes a north-up minimap contradict
what the player sees. Negating z instead puts north on -z, which is also a right-handed camera's default
forward, and keeps (east, north, up) = (+x, -z, +y) a right-handed triple, so one top-down view has north up
AND east right. `TileWorldSpace.WorldX/WorldZ/TileX/TileZ/ToWorld` are the whole seam: every conversion between
the two spaces goes through them, including `HeightAt`, the raycast, the ground mesher's vertices and region
transform, and the prop anchors. Two consequences worth stating outright: a region-local ground mesh runs from
0 to MINUS 64 tiles on z, and an object's yaw is NEGATIVE per quarter turn, which is what makes a rotation turn
clockwise viewed from above with north up.

`TileRect` is a rect of world tiles with EXCLUSIVE far edges (`X1`, `Z1`, plus `FromCorners`, `Expand`,
`Intersect`, `Union`, `Intersects`, `Contains`), and `TileDirection` with `TileDirections.All/Delta/IsDiagonal`
gives the eight step directions in the fixed W, E, S, N, SW, SE, NW, NE order the pathfinder needs.

## The document

`TileWorldDocument` holds the header (`Id`, `DisplayName`, `TileSize` metres per tile, `PlaneCount`,
`PlaneHeight`, `CatalogPaths`, `NextObjectId`) plus a sparse map of `TileRegion`s. It is mutable, has no undo
of its own and raises no events: every mutation marks its region `Dirty`, and the caller tracks the rect.

- Regions: `GetRegion`, `GetOrCreateRegion`, `RegionAt`, `RequireRegion`, `RemoveRegion`, `RegionsTouching`.
  `GetOrCreateRegion` and `RequireRegion` are the two that THROW. `RequireRegion` throws for a region that does
  not exist at all, and both throw for one the manifest knows about but that is not in memory (creating it
  blind would let the next save overwrite authored terrain), so load that one through `TileWorldSource` first.
- Tiles: `Get`/`Set` pairs for `Underlay`, `Overlay`, `OverlayShape`, `OverlayRotation` and `Settings`, where
  `TileSettings` is `Blocked`, `Indoors`, `Bridge`, `NoDraw` and `TileOverlayShape` is `Full`, `DiagonalHalf`,
  `CornerQuarter`, `CornerThreeQuarter`. Reads outside a loaded region answer the default, writes require it.
- Heights: `CornerHeightCm`/`CornerHeight` read the ONE GLOBAL LATTICE (the owning region, else edge-extended
  from the region west, south or south-west), `SetCornerHeightCm`/`TrySetCornerHeightCm` write it,
  `FillDerivedHeights` materialises a higher plane from plane 0 plus the plane lift, and `HeightAt` is the
  bilinear height in metres at a world position.
- Objects and markers: `AddObject`, `FindObject`, `MoveObject`, `RemoveObject`, `ObjectsIn`, `AllObjects`,
  `SetMarker`, `FindMarker`, `RemoveMarker`, `AllMarkers`, `AllocateObjectId`, `RebuildObjectIndex`.

`TileRegion` is 64x64 (`Size`, `TileCount`) with one `TilePlaneData` per plane, its own object and marker lists
and a `Dirty` flag. `TilePlaneData` carries the six dense layers (`Heights` in centimetre shorts, `Underlay`,
`Overlay`, `OverlayShape`, `OverlayRotation`, `Settings`), each null until first written. `TileRegion.Trim()`
nulls all-default layers and is the entry point, because it hands each plane its own index: above plane 0 a null
height layer means "derive from plane 0", so `TilePlaneData.Trim(planeIndex)` keeps an authored all-zero one.

## File form

One on-disk form, a directory: `world.json` plus `regions/r_<rx>_<rz>.json`, dense layers as base64
little-endian arrays. `TileWorldFile.Save(doc, directory, force = false)` writes dirty regions (all of them
under `force`), deletes the files of regions the document no longer has, then the manifest LAST, each file
through a tmp plus rename, and clears `Dirty` only once the manifest naming those exact bytes has landed.
`Load(directory, options)` reads the manifest and every region, each hash-checked, and `RegionFileName`,
`RegionPath`, `ManifestPath`, `Exists`, `CurrentFormatVersion`, `SchemaUri` and
`TileWorldLoadOptions.RegisterMigration` complete the surface. Region file names are invariant-culture and
strictly canonical, so `r_+1_2.json` is not region (1, 2) and the stale sweep will not delete a file the
manifest never named. A torn write is DETECTED, not rolled back: bytes are replaced in place, so an interrupted
save leaves what it already wrote, and the next load refuses the world naming the first disagreeing file.

`TileWorldSource` is the streaming entry point over the same document: `Open(directory)` reads the manifest
only, `EnsureLoaded(coord)`/`EnsureLoaded(rect)` materialise regions on demand hash-checked, `IsKnown`,
`IsLoaded`, `KnownRegions` and `Document` expose the state, and `Unload(coord)` drops a CLEAN region while
refreshing its known-hash table from that region's current bytes, so a region that was edited and saved is not
later mistaken for a torn write.

`TileWorldHash` is world identity: `OfRegionBytes` over a region file's exact bytes, `OfRegion` over the
canonical write of a live region, `OfWorld` composing loaded regions with the stored hashes of unloaded ones,
and `OfManifestRegions` doing the same from a manifest's rows, rejecting a null hash or a duplicate region. It
folds in `TileSize`, `PlaneCount` and `PlaneHeight`, excludes the id, display name, catalog paths and object-id
allocator so renaming a world never desyncs a server from its clients, and formats every number invariant.
`OfRegion` and `OfWorld` TRIM the regions they hash, so re-take any layer array you were holding.

## Catalogs and validation

Catalogs are game content, referenced by id and never stored in the world. `TileWorldCatalogs.Load(paths)` reads
catalog JSON files (schema-checked, JSONC tolerated), `LoadJson` parses one in memory, `Merge` combines already
loaded ones, and `Greybox()` is the engine's six-material, twelve-archetype test catalog covering every
collision kind with square and non-square footprints. `Material(id)` and `Archetype(id)` are the lookups, and
`Archetype` is null-tolerant, so a null archetype id in content is a validator finding rather than a throw.
Malformed or duplicate content throws a `TileWorldException` naming the source file, and
`TileWorldSchema.GetCatalogJson()` returns the embedded catalog schema. `GroundMaterial` is
`{ Id, Name, Color, Texture, Kind }` (`Ground` or `Water`, id 0 reserved for void) and `TileObjectArchetype` is
`{ Id, Name, MeshRef, SizeX, SizeZ, CollisionKind, IsRoof, Interactive, YawOffsetDegrees, Tags }`, with
`TileCollisionKind` one of `None`, `Solid`, `Wall`, `WallCorner`, `Diagonal`. `TileFootprint.Rotated` and
`TileFootprint.Of` give the rotated footprint size and the world rect an instance covers.

`TileWorldValidator.Validate(doc, catalogs)` returns `TileWorldIssue(Code, Message, Region, Tile)` records and
never throws on bad content, while `ValidateOrThrow` throws once quoting the first five. The codes are stable
and callers may branch on them: `header.planeCount`, `header.tileSize`, `header.planeHeight`,
`region.planeCount`, `material.missing`, `overlay.shape`, `archetype.missing`, `object.plane`,
`object.footprint`, `object.duplicateId`, `object.region`, `marker.plane`, `marker.duplicateName`,
`marker.region`.

## Collision, derived and never authored

`TileCollisionFlags` is the per-tile bit set: `Blocked`, the four wall edges, the four corner bits, plus
`ProjectileBlocked` and `Decoration` reserved for later. A wall is ONE EDGE SHARED BY TWO TILES, so the baker
sets the edge bit on both tiles and a movement check never has to look at objects.

`TileCollisionBaker.Bake(doc, catalogs)` builds the whole map. `Rebake(map, doc, catalogs, dirtyRect, plane)`
re-derives one rect, expanded by a tile for mirrored edges, ensuring and full-ground-baking any document region
the cleared rect touches that has no storage yet, dropping the storage of any it touches that the DOCUMENT no
longer has (so a deleted region reads blocked again rather than turning into walkable void), and gathering
objects out to a margin taken from the catalogs' largest footprint. THE CALLER MUST PASS A RECT COVERING THE
FULL FOOTPRINT of anything it removed,
measured with `TileFootprint.Of` BEFORE the removal. `EdgeFlag` and `WallFacing` are the mapping helpers.

`TileCollisionMap` is the storage, keyed by region and plane, never persisted. Reads outside storage answer
`Blocked`, so an unloaded region is a wall rather than a void, and an `Or` outside storage is a NO-OP.
`EnsureRegion` is the only allocator, so a footprint or a mirrored edge spilling into a region the document does
not have is dropped and that region stays blocked. `Get`, `Or`, `Clear`, `HasRegion`, `RemoveRegion`, `Regions`.

`TileCollision.IsBlocked` and `CanStep(map, x, z, plane, dir, agentSize = 1)` are the one movement primitive the
pathfinder and a tick mover share. A cardinal step needs no wall on the leaving edge, an unblocked target and no
wall on the entering edge. A diagonal additionally needs both corner bits clear and all four cardinal sub-steps
legal, the no-corner-cutting rule, and an NxN agent must pass on every footprint tile. `CanStep` deliberately
allows EGRESS from a `Blocked` tile, so an agent standing where the ground was blocked under it can walk out.

## Pathing, raycast, prefabs

`TilePathfinder.FindPath(map, plane, start, goal, agentSize = 1, maxRadius = 64)` is a deterministic BFS over
the collision map through `CanStep` in the fixed direction order, bounded to a square window around the start,
so both heads replay identical paths. `maxRadius` must be 1..`MaxSearchRadius` (4096), the window's two scratch
arrays being `(2r + 1)^2` entries each. `TilePath` carries `Tiles` (the steps AFTER the start), `Reached` and
`End`. An unreachable goal yields the walk to the nearest reachable tile, nearest by SQUARED EUCLIDEAN distance
to the goal, then by BFS distance, then by scan order, and a start on a `Blocked` tile behaves like any other.
**Branch on `Reached`, never on `Tiles.Count`**: a partial walk and a reached one both carry steps.

`TileRaycast.Pick(doc, plane, origin, direction, maxDistance = 2000f)` marches the lattice in XZ and returns the
first `TileHit(X, Z, Plane, Point, Distance)`, or null. The direction need not be normalised, a plane outside
the document throws as soon as the walk touches a tile, and world units are tiles times
`TileWorldDocument.TileSize` on x and z and metres on y. `TileTriangulation.SplitSwNe` is the ONE
diagonal-split rule, shared with the ground mesher so a click lands on the triangle that is drawn: a
`DiagonalHalf` overlay forces the split, otherwise the diagonal whose corners differ least in height wins. The
raycast winds its triangles downward, harmless for its two-sided test, and a culled mesher winds the other way.

`TilePrefab` is a rect of tiles lifted out of a world (every layer, corner heights relative to the rect's SW
corner, objects and markers in prefab-relative coordinates) that can be stamped elsewhere with a rotation.
`TilePrefabFile.Save`/`Load` are its JSON form (base64 layers, indented for git, atomic replace).

- `Extract(doc, catalogs, rect, planeFrom, planeCount, includeObjects, includeMarkers, name)` lifts it,
  stamping each object's UNROTATED `SizeX`/`SizeZ` so a rotation later needs no catalog.
- `Rotate(prefab, rotation)` turns a copy, bumping overlay rotations with the tiles and re-basing every plane's
  heights by the shift that puts the rotated SW corner on plane 0 at height 0, so the inter-plane offsets
  survive, then re-trimming, so a rotated prefab is shaped exactly like a fresh `Extract` of the same content.
- `Place(doc, prefab, x, z, plane, rotation)` validates the prefab's shape (sizes, layer lengths, and every
  object's and marker's plane), rotates, then requires every region of the TILE RECT BEFORE the first write, so
  a bad stamp cannot tear half way through. The far-edge CORNER writes at `x + w` and `z + h` are the one
  exception: at the edge of the authored world their region may not exist, and those writes are SKIPPED rather
  than refusing the stamp, because a corner out there is edge-extended from the tile rect and is not readable
  as its own value anyway. The prefab's SW corner is the height datum, so it lands on the existing ground at
  (x, z) whatever the rotation. Objects get fresh ids, markers replace same-name markers, and the returned rect
  is the touched area for a collision rebake.

A stamp is ADDITIVE per layer: a null layer is skipped rather than zeroed, so pre-existing overlays or settings
under the stamp survive it. Clear the rect first if you want a replace.
