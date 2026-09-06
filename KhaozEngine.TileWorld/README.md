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

- Regions: `GetRegion`, `GetOrCreateRegion`, `RegionAt`, `RequireRegion`, `RemoveRegion`, `DeleteRegion`,
  `RegionsTouching`.
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
- Cosmetic foliage: immutable `TileFoliageLayer` values through `FoliageLayers`, `GetFoliageLayer`,
  `SetFoliageLayer` and `RemoveFoliageLayer`. A layer carries a world-metre density raster, weighted archetypes,
  deterministic placement settings and material, indoor, solid, door and edge rules. Density is row major.
  X advances within a row and row index advances along positive world Z from `OriginZ`. `CopyDensity` returns a
  detached copy and `WithDensity` builds a replacement layer.
- `Source` is the `TileWorldSource` this document was opened through, or null for one built in memory. It is the
  document's only view of the regions it does not hold, and `SetMarker` uses it: a marker name is unique across
  the WHOLE world, so a name an unloaded region already carries is refused rather than authored into a second
  region that collides once both are loaded. A document with no source checks the loaded regions alone.

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

Foliage layers are an optional manifest block. Their density bytes use base64 and retain the row convention
above. An empty layer list is omitted, so old world JSON and old world hashes stay unchanged. Non-empty foliage
is part of `TileWorldHash.OfWorld`, including every placement and exclusion setting. `TileWorldSource.Open`
loads the global layers without materialising a region, and a partial save carries them forward.

`TileWorldSource` is the streaming entry point over the same document: `Open(directory)` reads the manifest
only, `EnsureLoaded(coord)`/`EnsureLoaded(rect)` materialise regions on demand hash-checked, `IsKnown`,
`IsLoaded`, `KnownRegions` and `Document` expose the state, and `Unload(coord)` drops a CLEAN region while
refreshing its known-hash table from that region's current bytes, so a region that was edited and saved is not
later mistaken for a torn write, and refreshing its marker rows the same way and for the same reason.
`Document.RemoveRegion(coord)` takes that same safe unload path on a source-backed document. Use
`Document.DeleteRegion(coord)` for permanent deletion: it accepts dirty or unloaded regions and clears the
source's known hash, unloaded hash and marker rows so the next save removes the file and manifest entry.
`FindMarker(name)` and `Markers` answer off the manifest's marker index with no
region read at all, which is how a client finds the spawn before it streams anything. The index is DERIVED from
the regions, like the collision map: `Save` rebuilds it for every region it holds and carries the rest forward
from the previous manifest, so a partial save keeps the markers of regions it never materialised. It rides
outside `OfWorld`, which reads region hashes and the header only, so no digest moved when it landed. A world
saved by an older engine carries no index and answers null.

`TileWorldHash` is world identity: `OfRegionBytes` over a region file's exact bytes, `OfRegion` over the
canonical write of a live region, `OfWorld` composing loaded regions with the stored hashes of unloaded ones,
and `OfManifestRegions` doing the same from a manifest's rows, rejecting a null hash or a duplicate region. It
folds in `TileSize`, `PlaneCount` and `PlaneHeight`, excludes the id, display name, catalog paths and object-id
allocator so renaming a world never desyncs a server from its clients, and formats every number invariant.
`OfRegion` and `OfWorld` TRIM the regions they hash, so re-take any layer array you were holding.

`OfCatalogs` is the second half of that identity: every material and every archetype, field by field, composed
canonically so the digest is independent of file formatting and merge order. `OfWorldAndCatalogs` composes the
two, and it is the one a netcode connect gate should compare. `OfWorld` alone cannot see an archetype gaining a
`CollisionKind`, so two heads over the same world directory with independently updated catalogs pass the gate and
then disagree on the baked collision map, which reads as a per-step correction on every wall instead of a refusal
at the door. `OfWorld` is unchanged, so moving a gate to the composed digest is a change on BOTH heads at once.

## Catalogs and validation

Catalogs are game content, referenced by id and never stored in the world. `TileWorldCatalogs.Load(paths)` reads
catalog JSON files (schema-checked, JSONC tolerated), `LoadJson` parses one in memory, `Merge` combines already
loaded ones, and `Greybox()` is the engine's six-material, twelve-archetype test catalog covering every
collision kind with square and non-square footprints. `Material(id)` and `Archetype(id)` are the lookups, and
`Archetype` is null-tolerant, so a null archetype id in content is a validator finding rather than a throw.
Malformed or duplicate content throws a `TileWorldException` naming the source file, and
`TileWorldSchema.GetCatalogJson()` returns the embedded catalog schema. `GroundMaterial` is
`{ Id, Name, Color, Texture, Kind, TilesPerMetre }` (`Ground` or `Water`, id 0 reserved for void) and
`TileObjectArchetype` is
`{ Id, Name, MeshRef, SizeX, SizeZ, CollisionKind, IsRoof, Interactive, YawOffsetDegrees, Tags }`, with
`TileCollisionKind` one of `None`, `Solid`, `Wall`, `WallCorner`, `Diagonal`. `TileFootprint.Rotated` and
`TileFootprint.Of` give the rotated footprint size and the world rect an instance covers.

`TilesPerMetre` is the optional `tilesPerMetre` catalog field, the texture repeats per metre the textured ground
path gives that material, null to take the renderer default of 0.5. `MaterialSource(id)` returns the catalog
FILE a material was loaded from, or null when the catalog came from `LoadJson`, `Merge` or `Greybox` rather than
`Load(paths)`, which is what lets a caller resolve a relative `Texture` against the file that declared it.

`TileWorldValidator.Validate(doc, catalogs)` returns `TileWorldIssue(Code, Message, Region, Tile)` records and
never throws on bad content, while `ValidateOrThrow` throws once quoting the first five. The codes are stable
and callers may branch on them: `header.planeCount`, `header.tileSize`, `header.planeHeight`,
`region.planeCount`, `material.missing`, `overlay.shape`, `archetype.missing`, `object.plane`,
`object.footprint`, `object.duplicateId`, `object.region`, `marker.plane`, `marker.duplicateName`,
`marker.region`, `foliage.archetype`, `foliage.underlay`.

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

`FindPath` takes an optional `TilePathfinderScratch` as its last argument. Without one it allocates both window
arrays per call, about 83 KB at radius 64. A scratch owns those arrays and the BFS queue across calls, so a
caller that paths on a tick allocates only the result: `new TilePathfinderScratch(64)` pre-sizes it, a bigger
radius grows it once, `Capacity` is the window it holds, and it is NOT thread safe, one per worker. The window
is reset to exactly what fresh arrays hold before every search, so a scratch-fed path is byte identical.

`TileRaycast.Pick(doc, plane, origin, direction, maxDistance = 2000f)` marches the lattice in XZ and returns the
first `TileHit(X, Z, Plane, Point, Distance)`, or null. The direction need not be normalised, a plane outside
the document throws as soon as the walk touches a tile, and world units are tiles times `TileWorldDocument.TileSize`
with z running the OTHER WAY from tile z, so the conversion goes through `TileWorldSpace` rather than a bare
divide. `HeightAt` reads its world position the same way.

`TileTriangulation` is the ONE tile triangulation, shared with the ground mesher in
`KhaozEngine.TileWorld.Render3D` so a click lands on the triangle that is drawn, and the raycast hits a SHAPED
tile at the surface that was actually drawn rather than at the plain pair. `SplitSwNe(h00, h10, h01, h11, shape,
rotation)` is the shared split choice: a `DiagonalHalf` overlay forces the diagonal, otherwise the one whose
corners differ least in height wins. `Triangulate(shape, rotation, splitSwNe, into)` is the shared shape
triangulation, writing up to `MaxTriangles` (4) `TileLatticeTriangle(A, B, C, Overlay)` records over the eight
`TileLatticePoint` lattice points (four corners plus four mid-edge points), two for a plain tile or a diagonal
half and four for a corner cut. `Local(point)` places a lattice point in tile-local 0..1 and `Ends(point, out
first, out second)` names the two corners a mid-edge point averages, a corner being its own pair so a caller maps
every point the same way. Every triangle comes back wound the SAME way (counter-clockwise on x and z), so a
pass that culls a face direction keeps or drops all of them together rather than half of them. Pass the shape
the tile actually draws with, so a shape whose overlay material is missing is passed as `Full`.

`TilePrefab` is a rect of tiles lifted out of a world (every layer, corner heights relative to the rect's SW
corner, objects and markers in prefab-relative coordinates) that can be stamped elsewhere with a rotation.
`TilePrefabFile.Save`/`Load` are its JSON form (base64 layers, indented for git, atomic replace).

- `Extract(doc, catalogs, rect, planeFrom, planeCount, includeObjects, includeMarkers, name,
  includeDerivedHeights)` lifts it, stamping each object's UNROTATED `SizeX`/`SizeZ` so a rotation later needs no
  catalog. `includeDerivedHeights: false` omits unauthored higher-plane height lattices, so those planes derive
  from the destination ground after placement. Explicitly authored height layers remain in the prefab even when
  their values match the source derivation. The default is true for compatibility.
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
