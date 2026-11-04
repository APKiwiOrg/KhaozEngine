# KhaozEngine.TileWorld

The OSRS-style tile world: a discrete grid of 64x64 tile regions with stacked planes, a global tile-corner
height lattice, ground and overlay materials, tile-anchored objects and named markers, saved as a hash-checked
directory. GPU-free and render-free, so client, server, editor and authoring tool read one document through one
path. In the `Foundation` umbrella, also carried by `Game2D`, `Game3D` and `Server`.
A sibling of `KhaozEngine.MapDoc`, not an extension of it. See the
[tile-world design](../docs/design/TILE-WORLD-DESIGN-2026-08-15.md).

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
Tile addresses use signed 32-bit coordinates. Rect operations that read or produce an exclusive far edge reject
geometry whose edge falls outside that same domain, so a rect cannot include `int.MaxValue` on either axis.

## The document

`TileWorldDocument` holds the header (`Id`, `DisplayName`, `TileSize` metres per tile, `PlaneCount`,
`PlaneHeight`, `CatalogPaths`, `NextObjectId`) plus a sparse map of `TileRegion`s. It is mutable, has no undo
of its own and raises no events: every mutation marks its region `Dirty`, and the caller tracks the rect.

- Regions: `GetRegion`, `GetOrCreateRegion`, `RegionAt`, `RequireRegion`, `RemoveRegion`, `DeleteRegion`,
  `RegionsTouching`.
  `RequireRegion` refuses a region that does not exist with `TileWorldRefusal.RegionMissing`. Both it and
  `GetOrCreateRegion` refuse one the manifest knows about but that is not in memory with `RegionNotLoaded`
  (creating it blind would let the next save overwrite authored terrain), so load that one through
  `TileWorldSource` first. Both carry `TileWorldException.Region`, and `RequireRegion` also carries the
  requested planar `TileX` and `TileZ`.
- Tiles: `Get`/`Set` pairs for `Underlay`, `Overlay`, `OverlayShape`, `OverlayRotation` and `Settings`, where
  `TileSettings` is `Blocked`, `Indoors`, `Bridge`, `NoDraw`, `FeatherOverlay` and `TileOverlayShape` is `Full`, `DiagonalHalf`,
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
save leaves what it already wrote, and the next load refuses the world with `RegionHashMismatch`.
`TileWorldException.SourceName` identifies the first disagreeing file, `Region` identifies its coordinate,
and `ExpectedHash` and `ActualHash` carry the manifest and file-byte hashes.

Manifest loading distinguishes read, JSON, format-version, migration and field admission failures through
`TileWorldRefusal`. Each carries the manifest path in `SourceName`. `ManifestJsonMalformed` retains the
original `JsonException` and its nullable JSON path in `Field`. Required or invalid manifest fields use
their property name in `Field`, with the refused `PlaneCount` or `TileSize` where applicable.
`ManifestFormatVersionUnsupported` carries the requested `FormatVersion` and `SupportedFormatVersion`.
`ManifestMigrationMissing` carries the original `FormatVersion` and the missing step's `MigrationFrom`
and `MigrationTo`. `ManifestMigrationReturnedNull` carries the step's `MigrationFrom` and `MigrationTo`.
`ManifestNotObject` distinguishes a non-object JSON root from `ManifestEmpty`, a null deserialization result.
`ManifestReadFailed`, `RegionReadFailed` and `WorldStaleRegionDeleteFailed` retain their original I/O cause
in `InnerException`. The region read and stale-delete failures also carry the file path and region coordinate.
The [refusal table](#refusals) lists each reason and identifier.

Foliage layers are an optional manifest block. Their density bytes use base64 and retain the row convention
above. An empty layer list is omitted, so old world JSON and old world hashes stay unchanged. Non-empty foliage
is part of `TileWorldHash.OfWorld`, including every placement and exclusion setting. `TileWorldSource.Open`
loads the global layers without materialising a region, and a partial save carries them forward.

Layer construction, DTO admission and document admission throw `TileFoliageArgumentException`, an
`ArgumentException` subtype with non-nullable `TileWorldRefusal Reason` and the foliage identifiers in the
[refusal table](#refusals). `ParamName` retains its original null value. File loading adds the manifest path
in `TileWorldException.SourceName`, preserves the same reason and every identifier, and retains the original
refusal in `InnerException`. Direct null `density` and `archetypes` constructor arguments stay plain
`ArgumentNullException`s with their original `ParamName`. A null manifest density instead uses
`FoliageDensityNull`, `LayerId` and `Field` = `density`, retaining the original null-parameter exception.
`ExpectedCount` is `long?` on the foliage subtype and retains the existing `int?` type on `TileWorldException`.
The density-length guard runs after the sample limit, so its expected count fits both types without loss.
A null layer entry carries `LayerIndex` only when the caller knows its position. No placeholder index is used.

The optional `clearance` block is the other manifest-level authoring block. It is outside every world hash and
is described under [Clearance](#clearance).

`TileWorldSource` is the streaming entry point over the same document: `Open(directory)` reads the manifest
only, `EnsureLoaded(coord)`/`EnsureLoaded(rect)` materialise regions on demand hash-checked, `IsKnown`,
`IsLoaded`, `KnownRegions` and `Document` expose the state, and `Unload(coord)` drops a CLEAN region while
refreshing its known-hash table from that region's current bytes, so a region that was edited and saved is not
later mistaken for a torn write, and refreshing its marker rows the same way and for the same reason.
Unsaved changes are refused with `RegionDirty` and the region coordinate in `TileWorldException.Region`.
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

## Refusals

`TileWorldException` retains its exact type and both message-only constructors. Use `Reason` and the
identifiers below to distinguish region residency, content, world-file, manifest-region, object, marker, dense-layer,
embedded-schema, validation, prefab, catalog, steep-companion, clearance and foliage refusals.
Messages remain developer prose. `Reason` is nullable only for a message-only instance built outside the
engine. Its reason-taking constructors also accept an original cause through `InnerException`.
PGM heightmap reads also use this type and enum. Their source, header and raster identifiers are listed in
the [editing refusal table](../KhaozEngine.TileWorld.Editing/README.md#refusals).
Byte-preserving catalog edits share this enum and use `ArchetypeIds` for the complete ordinal-sorted list
of missing archetypes, copied into an owned read-only snapshot. See the
[catalog edit refusal table](../KhaozEngine.TileEdit.Tool/README.md#catalog-edit-refusals) for their reasons
and the `ArgumentOutOfRangeException`-compatible `CatalogEditRangeException`.

Regions are planar. `Region` is a `RegionCoord?`, and `TileX` and `TileZ` are `int?` values set only when the
guard received tile coordinates. They never imply a plane. A region-only operation leaves both null.
For an authored object or marker outside its containing region, `Tile` carries its full address, including
the plane supplied by that content. Region content is checked on parse and before the first save write.
Region attachment and undo restoration also check plane counts. `SourceName` is set when parsing a named
region file and is null for these in-memory attachment, restoration and save checks.

| `TileWorldRefusal` reason | Refuses | Identifiers |
| --- | --- | --- |
| `RegionMissing` | An operation requiring a region that does not exist | `Region`, plus `TileX` and `TileZ` for a tile lookup |
| `RegionNotLoaded` | An operation requiring a known region that is not loaded | `Region`, plus `TileX` and `TileZ` for a tile lookup |
| `RegionDirty` | Unloading a region with unsaved changes | `Region` |
| `ManifestRegionDuplicate` | A repeated region coordinate in `Open` or `OfManifestRegions` | `Region`, plus `SourceName` for `Open` |
| `ManifestRegionHashMissing` | A null region hash passed to `OfManifestRegions` | `Region` |
| `RegionHashMismatch` | Region bytes that disagree with the manifest's hash | `Region`, `SourceName`, `ExpectedHash`, `ActualHash` |
| `WorldStaleRegionDeleteFailed` | A save whose stale region file cannot be deleted | `SourceName`, `Region`, original `InnerException` |
| `ManifestReadFailed` | A world manifest that cannot be read | `SourceName`, original `InnerException` |
| `ManifestNotObject` | A manifest whose JSON root is not an object | `SourceName` |
| `ManifestJsonMalformed` | Manifest JSON that cannot be parsed or deserialized | `SourceName`, nullable parser path in `Field`, original `JsonException` in `InnerException` |
| `ManifestFormatVersionMissing` | A manifest with no format version | `SourceName`, `Field` = `formatVersion` |
| `ManifestFormatVersionNotInteger` | A manifest whose format version is not an integer | `SourceName`, `Field` = `formatVersion` |
| `ManifestFormatVersionUnsupported` | A manifest newer than the supported format | `SourceName`, `FormatVersion`, `SupportedFormatVersion` |
| `ManifestMigrationMissing` | A manifest requiring an unregistered migration step | `SourceName`, original `FormatVersion`, `MigrationFrom`, `MigrationTo` |
| `ManifestMigrationReturnedNull` | A migration that returns no JSON object | `SourceName`, `MigrationFrom`, `MigrationTo` |
| `ManifestEmpty` | A manifest that deserializes to no value | `SourceName` |
| `ManifestPlaneCountInvalid` | A manifest whose plane count is below one | `SourceName`, `Field` = `planeCount`, `PlaneCount` |
| `ManifestTileSizeInvalid` | A manifest whose tile size is not positive | `SourceName`, `Field` = `tileSize`, `TileSize` |
| `ManifestIdMissing` | A manifest with a missing or blank world id | `SourceName`, `Field` = `id` |
| `RegionReadFailed` | A region file that cannot be read | `SourceName`, `Region`, original `InnerException` |
| `RegionPlaneCountMismatch` | A region whose plane count differs from the document | `Region`, `ActualPlaneCount`, `ExpectedPlaneCount`, plus `SourceName` on parse |
| `RegionObjectOutsideBounds` | An object anchored outside its containing region | `Region`, `ObjectId`, `ArchetypeId`, `Tile`, plus `SourceName` on parse |
| `RegionObjectPlaneOutsideRange` | An object on a plane the document does not have | `Region`, `ObjectId`, `ArchetypeId`, `Plane`, `PlaneCount`, plus `SourceName` on parse |
| `RegionMarkerOutsideBounds` | A marker outside its containing region | `Region`, `MarkerName`, `Tile`, plus `SourceName` on parse |
| `RegionMarkerPlaneOutsideRange` | A marker on a plane the document does not have | `Region`, `MarkerName`, `Plane`, `PlaneCount`, plus `SourceName` on parse |
| `RegionCoordinateMismatch` | A region file whose coordinate differs from its manifest row | Expected `Region`, `ActualRegion`, `SourceName` |
| `RegionFileEmpty` | A region file that deserializes to no region | `SourceName` |
| `RegionJsonMalformed` | Region JSON that cannot be parsed or deserialized | `SourceName`, `Field` when the parser supplies a JSON path, original `JsonException` in `InnerException` |
| `ObjectMissing` | Moving, editing or querying a required object id that is absent | `ObjectId` |
| `ObjectIdDuplicate` | Inserting an object id already in use | `ObjectId` |
| `ObjectIndexStale` | Moving an indexed object absent from its indexed region | `ObjectId`, indexed `Region` |
| `MarkerMissing` | Removing a required marker name that is absent | `MarkerName` |
| `MarkerNameInUnloadedRegion` | Authoring a marker name already held by an unloaded region | `MarkerName`, holding `Region`, existing marker's full `Tile` |
| `LayerBase64Malformed` | A dense layer with malformed base64 | `LayerName`, `SourceName` and `Plane` when loading a region or prefab, original `FormatException` in `InnerException` |
| `LayerByteCountMismatch` | A dense layer whose decoded byte count differs from its shape | `LayerName`, `ActualByteCount`, `ExpectedByteCount`, plus `SourceName` and `Plane` when loading a region or prefab |
| `SchemaResourceMissing` | An embedded catalog schema resource absent from the assembly | `ResourceName` |
| `CatalogJsonMalformed` | Catalog JSON that cannot be parsed or deserialized | `SourceName`, nullable parser path in `Field`, original `JsonException` in `InnerException` |
| `CatalogSchemaMismatch` | A catalog that fails its JSON schema | `SourceName`, owned read-only `SchemaFields` and `SchemaFindings` snapshots |
| `CatalogReadFailed` | A catalog file that cannot be read | `SourceName`, original `InnerException` |
| `CatalogMaterialIdReserved` | A material using id 0, reserved for void | `SourceName`, `MaterialId` |
| `CatalogMaterialDuplicate` | A material id already defined | `SourceName`, `FirstSourceName`, `MaterialId`, `MaterialName` |
| `CatalogArchetypeIdMissing` | An archetype with a missing or blank id | `SourceName`, `ArchetypeId` as supplied |
| `CatalogArchetypeDuplicate` | An archetype id already defined | `SourceName`, `FirstSourceName`, `ArchetypeId` |
| `CatalogWalkSurfaceNull` | A null entry in an archetype's walk surfaces | `SourceName`, `ArchetypeId`, `SurfaceIndex` |
| `CatalogCollisionHeightInvalid` | A collision height that is not finite and positive | `SourceName`, `ArchetypeId`, `Field`, `Value` |
| `CatalogWalkSurfaceNonFinite` | A non-finite walk-surface height or extent | `SourceName`, `ArchetypeId`, `SurfaceIndex`, `Field`, `Value` |
| `CatalogWalkSurfaceBoundsUnordered` | Walk-surface bounds whose minimum is not below the maximum | `SourceName`, `ArchetypeId`, `SurfaceIndex`, `MinField`, `MaxField`, `Minimum`, `Maximum` |
| `CatalogArchetypeMissing` | Placement or scatter requiring an archetype absent from the catalogs | `ArchetypeId` |
| `SteepSelfReference` | A material using itself as its steep companion | `SourceName`, `MaterialId`, `MaterialName`, `CompanionMaterialId` |
| `SteepCompanionMissing` | A steep companion absent from the same catalog | `SourceName`, `MaterialId`, `MaterialName`, `CompanionMaterialId` |
| `SteepCompanionWater` | A water material used as a steep companion | `SourceName`, `MaterialId`, `MaterialName`, `CompanionMaterialId`, `CompanionMaterialName` |
| `SteepFromOutsideRightAngle` | A resolved steep blend start outside (0, 90) | `SourceName`, `MaterialId`, `MaterialName`, `Field` = `fromDegrees`, `FromDegrees`, `ToDegrees` |
| `SteepToOutsideRightAngle` | A resolved steep blend end outside (0, 90) | `SourceName`, `MaterialId`, `MaterialName`, `Field` = `toDegrees`, `FromDegrees`, `ToDegrees` |
| `SteepBandUnordered` | A resolved steep blend start not below its end | `SourceName`, `MaterialId`, `MaterialName`, `FromDegrees`, `ToDegrees`, `FromIsDefault`, `ToIsDefault` |
| `SteepCosinesCollapsed` | A steep blend whose endpoint cosines cannot be ordered in single precision | `SourceName`, `MaterialId`, `MaterialName`, `FromDegrees`, `ToDegrees` |
| `WorldValidationFailed` | A document with semantic validation issues | `WorldId`, total `IssueCount`, complete owned read-only `Issues` snapshot |
| `PrefabSizeNonPositive` | A prefab whose width or height is not positive | `PrefabName`, `Width`, `Height`, plus `SourceName` on load |
| `PrefabPlaneCountNonPositive` | A prefab file whose plane count is not positive | `SourceName`, `PrefabName`, `PlaneCount` |
| `PrefabPlaneCountMismatch` | A prefab whose plane list differs from its declaration | `PrefabName`, `ActualPlaneCount`, `ExpectedPlaneCount`, plus `SourceName` on load |
| `PrefabLayerCountMismatch` | A hand-built prefab layer whose entry count differs from its size | `PrefabName`, `Plane`, `LayerName`, `ActualCount`, `ExpectedCount`, `Width`, `Height` |
| `PrefabObjectOutsideBounds` | A prefab file with an object anchored outside its footprint | `SourceName`, `PrefabName`, `ArchetypeId`, full relative `Tile`, `Width`, `Height` |
| `PrefabObjectPlaneOutsideRange` | A prefab object on a plane the prefab does not have | `PrefabName`, `ArchetypeId`, `Plane`, `PlaneCount`, full relative `Tile`, plus `SourceName` on load |
| `PrefabMarkerOutsideBounds` | A prefab file with a marker outside its footprint | `SourceName`, `PrefabName`, `MarkerName`, full relative `Tile`, `Width`, `Height` |
| `PrefabMarkerPlaneOutsideRange` | A prefab marker on a plane the prefab does not have | `PrefabName`, `MarkerName`, `Plane`, `PlaneCount`, full relative `Tile`, plus `SourceName` on load |
| `PrefabEmpty` | A prefab file that deserializes to no value | `SourceName` |
| `PrefabJsonMalformed` | Prefab JSON that cannot be parsed or deserialized | `SourceName`, nullable parser path in `Field`, original `JsonException` in `InnerException` |
| `PrefabReadFailed` | A prefab file that cannot be read | `SourceName`, original `InnerException` |
| `ClearanceMeshRootRooted` | A rooted clearance mesh root | `MeshRoot` |
| `ClearanceMeshRootBackslash` | A clearance mesh root containing a backslash | `MeshRoot` |
| `ClearanceCellSizeOutsideRange` | A non-finite or out-of-range sampling cell edge | `CellSize`, `Minimum`, `Maximum` |
| `ClearanceWaiverTagBlank` | A supplied blank waiver tag | `WaiverTag` |
| `ClearanceRuleDuplicate` | A repeated ordinal rule name | `RuleName` |
| `ClearanceMeshRootRequired` | Rules with a missing or blank mesh root | `MeshRoot`, `RuleCount` |
| `ClearanceRuleMissing` | Removing an unknown rule | `RuleName` |
| `ClearanceRuleNull` | A null rule entry | Zero-based `RuleIndex` |
| `ClearanceRuleNameBlank` | A null or blank rule name | Nullable `RuleName` |
| `ClearanceToleranceInvalid` | A negative or non-finite tolerance | `RuleName`, `Tolerance` |
| `ClearanceTagsEmpty` | A missing or empty tag list | `RuleName`, `Field`, `TagCount` (null for a null list) |
| `ClearanceTagBlank` | A null or blank tag | `RuleName`, `Field`, zero-based `TagIndex`, nullable `Tag` |
| `ClearanceRulesNull` | A file block with a null rule list | `Field` = `rules` |
| `ClearanceMeshRootNull` | A file block with a null mesh root | `SourceName`, `Field` = `meshRoot`, original `ArgumentNullException` with `ParamName` = `meshRoot` in `InnerException` |
| `ClearanceBlockMissing` | Editing rules before declaring a clearance block through TileEdit | `WorldId`, owned read-only `RequiredTools` snapshot containing `clearance_set` |
| `FoliageIdBlank` | A null or blank foliage layer id | Nullable `LayerId` |
| `FoliagePlaneNegative` | A negative foliage plane | `LayerId`, `Plane` |
| `FoliageOriginNonFinite` | A non-finite raster origin | `LayerId`, `OriginX`, `OriginZ` |
| `FoliageCellSizeInvalid` | A non-finite or non-positive cell edge | `LayerId`, `CellSize` |
| `FoliageDimensionsNonPositive` | A non-positive raster dimension | `LayerId`, `Width`, `Height` |
| `FoliageSampleLimitExceeded` | A raster exceeding the supported sample count | `LayerId`, `SampleCount` (`long`), `MaximumSampleCount` |
| `FoliageDensityLengthMismatch` | Density length differing from the raster dimensions | `LayerId`, `ActualCount`, `ExpectedCount`, `Width`, `Height` |
| `FoliageSpacingInvalid` | Non-finite or non-positive placement spacing | `LayerId`, `Spacing` |
| `FoliageScaleRangeInvalid` | A non-finite, non-positive or unordered scale range | `LayerId`, `ScaleMin`, `ScaleMax` |
| `FoliageRootOffsetNonFinite` | A non-finite root offset | `LayerId`, `RootOffset` |
| `FoliageDoorClearanceInvalid` | A negative or non-finite door clearance | `LayerId`, `DoorClearance` |
| `FoliageEdgeFadeInvalid` | A negative or non-finite edge fade | `LayerId`, `EdgeFade` |
| `FoliageArchetypesEmpty` | A layer without an archetype | `LayerId`, `ArchetypeCount` |
| `FoliageArchetypeIdBlank` | A null or blank archetype id | `LayerId`, nullable `ArchetypeId`, zero-based `ArchetypeIndex` |
| `FoliageArchetypeDuplicate` | A repeated ordinal archetype id | `LayerId`, `ArchetypeId` |
| `FoliageWeightInvalid` | A non-finite or non-positive archetype weight | `LayerId`, `ArchetypeId`, `Weight` |
| `FoliageTotalWeightNonFinite` | A non-finite accumulated archetype weight | `LayerId`, `TotalWeight` |
| `FoliageUnderlayVoid` | Void material zero as an allowed underlay | `LayerId`, `MaterialId` = `0` |
| `FoliageLayerNull` | A null layer entry in a manifest | Zero-based `LayerIndex` when the caller knows the index |
| `FoliageArchetypesNull` | A null archetype list in a manifest layer | `LayerId` |
| `FoliageArchetypeNull` | A null archetype entry in a manifest layer | `LayerId`, zero-based `ArchetypeIndex` |
| `FoliageLayerDuplicate` | A repeated ordinal layer id | `LayerId` |
| `FoliagePlaneOutsideWorld` | A layer on a plane the world does not have | `LayerId`, `Plane`, `PlaneCount` |
| `FoliageDensityNull` | A null density array in a manifest layer | `SourceName`, `LayerId`, `Field` = `density`, original `ArgumentNullException` with `ParamName` = `density` in `InnerException` |
| `FoliageLayerMissing` | A required foliage query, density edit, paint or removal for an absent layer | `LayerId` |
| `FoliageCatalogArchetypeMissing` | A TileEdit foliage configuration referencing an archetype absent from the catalogs | `LayerId`, `ArchetypeId` |
| `FoliageCatalogUnderlayMissing` | A TileEdit foliage configuration allowing a material absent from the catalogs | `LayerId`, `MaterialId` |

Required foliage adapters share `FoliageLayerMissing`. The document's optional `GetFoliageLayer` still returns
null for an absent id. TileEdit checks catalog references before replacing a layer. A refused lookup or
reference change leaves the document and undo history unchanged.

`MoveObject`, required object commands and `QueryService.ObjectGet` share `ObjectMissing`. A stale object
index can follow direct edits to a region's public object list. `MoveObject` checks the destination region
before reporting `ObjectIndexStale`, and `RebuildObjectIndex` repairs the index after bulk list edits.
`MarkerNameInUnloadedRegion` identifies the existing marker's tile, including its known plane, rather than
the requested destination. Load that region before moving the marker, or choose a different name.

Region and prefab loaders pass dense-layer context directly to the codec. `LayerName` is `heights`,
`underlay`, `overlay`, `overlayShape`, `overlayRotation` or `settings`, with `heights` also used for prefab
relative heights. `SourceName` is the file name or supplied content name, and `Plane` is the decoded plane's
index. Neither identifier is reconstructed from message text.

## Catalogs and validation

Catalogs are game content, referenced by id and never stored in the world. `TileWorldCatalogs.Load(paths)` reads
catalog JSON files (schema-checked, JSONC tolerated), `LoadJson` parses one in memory, `Merge` combines already
loaded ones, and `Greybox()` is the engine's six-material, twelve-archetype test catalog covering every
collision kind with square and non-square footprints. `Material(id)` and `Archetype(id)` are the lookups, and
`Archetype` is null-tolerant, so a null archetype id in content is a validator finding rather than a throw.
Required-archetype adapters in placement commands and scatter use `CatalogArchetypeMissing` and `ArchetypeId`.
The null-returning queries keep their existing behavior.
Malformed or duplicate content throws the exact `TileWorldException` type with a catalog reason from the
[refusal table](#refusals). `SourceName` identifies the content label or file path, and duplicates also carry
`FirstSourceName` plus the material or archetype id. `CatalogSchemaMismatch` carries `SchemaFields` as distinct
failing instance locations and immutable `KhaozEngine.Content.JsonSchemaFinding` values in `SchemaFindings`.
Locations are JSON Pointers, including unknown properties such as `/materials/0/steep/chain`. Both collections
are owned read-only snapshots. `TileWorldSchema.GetCatalogJson()` returns the embedded catalog schema. If the
resource is missing, it throws `SchemaResourceMissing` with `ResourceName`. `GroundMaterial` is
`{ Id, Name, Color, Texture, Kind, TilesPerMetre, Steep }` (`Ground` or `Water`, id 0 reserved for void) and
`TileObjectArchetype` is
`{ Id, Name, MeshRef, LodMeshRef, SizeX, SizeZ, CollisionKind, IsRoof, Interactive, YawOffsetDegrees, Tags, WalkSurfaces, CollisionHeight }`, with
`TileCollisionKind` one of `None`, `Solid`, `Wall`, `WallCorner`, `Diagonal`. `TileFootprint.Rotated` and
`TileFootprint.Of` give the rotated footprint size and the world rect an instance covers.

`LodMeshRef` is the optional authored render LOD for an archetype. A missing or blank value means the archetype
has no simplified tier. Both mesh references participate in the canonical catalog hash, while collision and
pathing continue to use the archetype footprint and `CollisionKind`.

```json
{
  "id": "oak",
  "name": "Oak",
  "meshRef": "models/oak.glb",
  "lodMeshRef": "models/lod/oak.glb"
}
```

This is one authored render tier only. It does not change the object's footprint, stable ID, collision, pathing,
world-region bytes or server representation. Catalog hash scheme 2 includes `lodMeshRef`, so client and server
catalog gates detect a different LOD reference even though gameplay authority still comes from the full object.

`WalkSurfaces` is the optional `walkSurfaces` catalog field: the flat tops a body can stand on, for a bridge deck,
a dock or a pier whose prop is anchored on ground well below its top. Each `TileWalkSurface` is
`{ Height, MinX, MaxX, MinZ, MaxZ }`, a horizontal rectangle in the MESH's local metres (the frame its vertices are
authored in, before the instance rotation and the yaw offset) at `Height` metres above the mesh base, which is the
anchor. A null extent is the footprint edge on that side (`SizeX * tileSize / 2` on x, `SizeZ * tileSize / 2` on z,
unrotated), and an extent may reach past the footprint, the way a bridge landing overhangs the bank.

```json
{
  "id": "bridge",
  "name": "Bridge",
  "meshRef": "models/bridge.glb",
  "sizeX": 3,
  "sizeZ": 3,
  "walkSurfaces": [ { "height": 0.825, "minX": -2.5, "maxX": 2.5 } ]
}
```

The schema requires `height` and refuses any other key. The loader uses `CatalogWalkSurfaceNonFinite` for a
non-finite height or extent and `CatalogWalkSurfaceBoundsUnordered` for a min that is not below its max when
both are given. Both carry `SourceName`, `ArchetypeId` and `SurfaceIndex`, plus the field and value or both
bound fields and values. `Merge` re-runs that check on every archetype it adds, so an in-memory edit cannot carry a NaN through it. A pair with one null side is not
judged at load, because its resolved edge depends on a tile size the catalog does not know, and a rectangle that
comes out inverted simply covers nothing. An empty list loads as null. Walk surfaces change no footprint, no
collision and no pathing. `OfCatalogs` digests them, writing nothing for an archetype without any, so a catalog
with no walk surfaces keeps the digest it had before the field existed.

`CollisionHeight` is the optional `collisionHeight` catalog field: how tall the object stands for collision, in
metres above the mesh's local base, so a collider's top is the height the model stands at plus this. Absent loads
as null, and a present value must be a finite number above 0. The schema refuses 0, a negative number and a JSON
`null`. `Merge` refuses any such value edited in after load, NaN and infinity included, with
`CatalogCollisionHeightInvalid`, `SourceName`, `ArchetypeId`, `Field` = `collisionHeight` and `Value`.
The tile collision map and pathing never read it. `KhaozEngine.TileWorld.Physics` requires it on every
`Solid`, `Diagonal`, `Wall` and `WallCorner` archetype, and `ke-tileedit`'s `archetype_measure_heights` and
`archetype_set_collision_heights` fill it from the kit. `OfCatalogs` digests it only when present, so a catalog
without heights keeps its digest.

`TilesPerMetre` is the optional `tilesPerMetre` catalog field, the texture repeats per metre the textured ground
path gives that material, null to take the renderer default of 0.5. `MaterialSource(id)` returns the catalog
FILE a material was loaded from, or null when the catalog came from `LoadJson`, `Merge` or `Greybox` rather than
`Load(paths)`, which is what lets a caller resolve a relative `Texture` against the file that declared it.
`ArchetypeSource(id)` is the same for an archetype under the same file-only rule, which is how `ke-tileedit`'s
`archetype_set_collision_heights` finds the one file to write an archetype's height into.

`Steep` is the optional `steep` catalog field, a `GroundMaterialSteep` naming the material this one blends toward
on steep ground: `{ "material": 14, "fromDegrees": 40, "toDegrees": 55 }`. `material` is required. The band ends
are optional and default to `GroundMaterialSteep.DefaultFromDegrees` (40) and `DefaultToDegrees` (55), which
`ResolvedFromDegrees` and `ResolvedToDegrees` apply. The rule is the material's own: the tile ground renderer
blends every tap of the material toward the companion over that band of the ground normal's slope, whichever corner
or overlay slot the material fills, so a cliff face renders the companion even where the corner vote picked the
material at its foot. A companion's own `steep` is never followed. The schema refuses a missing `material`, an
unknown key, and a band end outside (0, 90). `GroundMaterialSteepRules.ThrowIfInvalid` throws
`TileWorldException` with one of the seven `Steep` reasons in the refusal table. `SourceName`, `MaterialId`
and `MaterialName` identify the source and first invalid material in ascending id order.
`SteepSelfReference`, `SteepCompanionMissing` and `SteepCompanionWater` carry `CompanionMaterialId`, with
`CompanionMaterialName` also set for water. Companions must belong to the SAME catalog file.
`SteepFromOutsideRightAngle` and `SteepToOutsideRightAngle` name the invalid endpoint in `Field`.
All four endpoint and band reasons carry resolved `FromDegrees` and `ToDegrees`, and `SteepBandUnordered`
also carries `FromIsDefault` and `ToIsDefault`. `SteepCosinesCollapsed` refuses ends that cannot be ordered
in single precision cosine, which is what the renderer blends on. `Problem` reports the same validation
finding as developer prose without throwing. `OfCatalogs` digests the companion only when present, so a
catalog without companions keeps its digest.

`TileWorldValidator.Validate(doc, catalogs)` returns `TileWorldIssue(Code, Message, Region, Tile)` records and
never throws on bad content. `ValidateOrThrow` throws `WorldValidationFailed` with `WorldId`, total
`IssueCount` and the complete `Issues` list, including findings beyond the first five summarized in the
developer message. `Issues` is copied when set and exposed as a read-only snapshot, so changing the source
collection or casting the exposed list cannot mutate it. The codes are stable
and callers may branch on them: `header.planeCount`, `header.tileSize`, `header.planeHeight`,
`region.planeCount`, `material.missing`, `overlay.shape`, `archetype.missing`, `object.plane`,
`object.footprint`, `object.duplicateId`, `object.region`, `marker.plane`, `marker.duplicateName`,
`marker.region`, `foliage.archetype`, `foliage.underlay`, `material.steep`. The last reports a steep companion
edited unsound in memory after the loader accepted it. The three `clearance.*` codes below share
`TileWorldIssue` but never come from `Validate`.

## Clearance

An authoring-time check that keeps tagged object meshes out of each other, for a tree crown drawing through a
roof or a wall. A world declares it in an optional `clearance` manifest block beside `foliageLayers`:

```json
"clearance": {
  "meshRoot": "../../models",
  "cellSize": 0.1,
  "waiverTag": "clearance-waived",
  "rules": [
    { "name": "trees-clear-buildings", "subjectTags": ["tree"], "obstacleTags": ["structure"], "tolerance": 0.05 }
  ]
}
```

`TileWorldDocument.Clearance` holds it as an immutable `TileClearanceSettings`, null when the world declares
none, and `SetClearance(settings)` replaces it. The constructor validates every field and `WithRule`,
`WithoutRule`, `With` and `Rule(name)` build and read the next value.

| Field | Contract |
|---|---|
| `meshRoot` | Directory the archetypes' `meshRef` paths resolve under, relative to the directory holding `world.json`, with forward slashes only (`../models`). A rooted path is refused with `TileClearanceArgumentException` (`ClearanceMeshRootRooted`), because world files are committed and shared. The rule is the same on every OS: a leading `/` or `\`, a drive prefix such as `C:` and any backslash are refused. Required while rules exist. |
| `cellSize` | Sampling and rasterizing pitch in metres, 0.02 through 1. Default 0.1 (`TileClearanceSettings.DefaultCellSize`). |
| `waiverTag` | Object tag that exempts an object from every rule. Optional. Null waives nothing. |
| `rules` | `TileClearanceRule(Name, SubjectTags, ObstacleTags, Tolerance)` in authoring order. Names are unique by ordinal comparison, both tag lists are non-empty with no blank tag, and `tolerance` is metres of allowed depth, finite and at least 0, default 0. |

`TileClearanceRule` compares by value: the name and every tag ordinally, tag order included, and the tolerance.
A save writes every default out. An absent block stays absent, so an older world keeps its bytes. The block is
outside `TileWorldHash.OfWorld`, so adding or tuning a rule changes no world digest and forces no navigation
rebake. Direct validation throws `TileClearanceArgumentException`, an `ArgumentException` subtype with a
non-nullable `TileWorldRefusal Reason` and the identifiers in the [refusal table](#refusals). Its `ParamName`
remains null, as before. A null constructor `meshRoot` and a null `WithRule` argument remain plain
`ArgumentNullException`s with their original `ParamName`.

A malformed block, a rooted `meshRoot` included, fails the load as a `TileWorldException`. The file wrapper
adds the manifest path in `SourceName` and preserves the exact reason, all applicable clearance identifiers
and the original exception in `InnerException`. A null file mesh root translates the plain parameter check
to `ClearanceMeshRootNull`, with `Field` = `meshRoot` and the original `ArgumentNullException` retained.

**Rule matching.** An object is a subject of a rule when its archetype's catalog `tags` contain any of the rule's
`subjectTags`, and an obstacle when they contain any `obstacleTags`. Matching reads archetype tags only, and the
engine interprets no tag string. The waiver reads the OBJECT's own tags: a waived object pairs with nothing under
any rule. An object never pairs with itself, and pairs across planes are checked, because a roof stands on the
plane above the trees beside it.

**Running it.** `TileWorldClearance.Check(doc, catalogs, meshes)` returns `TileWorldIssue`s for the loaded
regions, empty when the world declares no rules. `meshes` is an `ITileMeshGeometry`, the seam that keeps mesh
loading out of this package: `TryLoad(meshRef, out mesh, out error)` returns a `ClearanceMesh` in the archetype's
local metres, centred on its footprint with its base at y 0, or false with a reason.
`KhaozEngine.TileWorld.Render3D`'s `GltfTileMeshGeometry` is the glb implementation. Each object is placed with
`TileObjectPlacement.LocalToWorld`, the transform the prop renderer draws with, and each mesh is loaded,
rasterized and sampled once per call. `TileWorldClearance.ResolveMeshRoot(worldDirectory, settings)` gives the
absolute mesh directory.

```csharp
TileWorldDocument doc = TileWorldFile.Load(worldDir);     // every region, the check reads loaded ones only
TileWorldCatalogs catalogs = TileWorldCatalogs.Load(catalogPaths);
var meshes = new GltfTileMeshGeometry(TileWorldClearance.ResolveMeshRoot(worldDir, doc.Clearance!));
foreach (TileWorldIssue issue in TileWorldClearance.Check(doc, catalogs, meshes))
    Console.WriteLine($"[{issue.Code}] {issue.Message}");
```

The codes are stable (`TileWorldClearance.OverlapCode`, `MeshCode`, `RuleCode`):

- `clearance.rule`: a rule's subject or obstacle tags match no archetype in the catalogs, so a typo cannot pass
  everything.
- `clearance.mesh`: a matched archetype has no `meshRef`, or its mesh cannot be read or built (including a solid
  above `ClearanceSolid.MaxCells` and a mesh source that reports success with no mesh). Reported once per archetype, and its objects are never silently passed.
- `clearance.overlap`: one per penetrating pair, at its deepest sample, naming the rule, both objects with their
  ids, archetypes, tiles and planes, and the depth in metres. `Region` and `Tile` are the subject's.

Issues come back rule issues first, then mesh issues by archetype id, then overlaps by rule order, subject id and
obstacle id. An object whose archetype the catalogs lack is skipped, because `Validate` reports it as
`archetype.missing`.

**The solid model and its limit.** An obstacle is solid from its mesh's lowest point up to its top surface in
every planar cell that surface covers, and a subject sample's depth is its nearest way out: up to the top, down
to the base or sideways to an empty cell. A thin piece therefore caps reported depth near half its thickness.
Planar depth is quantized to the cell size. A doorway, an arch, the space under eaves or a bridge deck over water
reads as filled, which leans toward reporting a contact a precise test would clear. A hollow piece (a bridge
deck, a balcony) must not carry an obstacle tag.

**The error band.** The solid is a raster, so a reading carries an error that scales with `cellSize` (figures
for the default 0.1 in brackets):

- A point outside the surface but inside a boundary cell of a curved or off-grid edge reads up to `cellSize / 2`
  inside (0.05).
- A cell whose only empty neighbour is diagonal reads about `(sqrt 2 - 1/2) x cellSize` inside (0.091).
- A cell top is the highest sample in the cell, so a point just above a sloped top reads up to about
  `cellSize x tan(slope)` inside, less its height above the surface. A point 1 cm above a 45 degree roof reads
  about 0.09, and about 0.16 at 60 degrees.
- The check can also under-report. A contact narrower than the subject's sample pitch (`cellSize`) can fall
  between samples and be missed, and an obstacle wholly inside a subject is never seen, because only the
  subject's surface is sampled.

Give each rule a `tolerance` of at least `cellSize / 2` to absorb the edge band, and use a smaller `cellSize` in
a world with steep roofs.

**Not part of `Validate`.** `TileWorldValidator.Validate` never runs the check, because it needs mesh files that
a head or a server opening a world never reads. Tools run it beside the validator, and `ke-tileedit` refuses a
save on any clearance issue. Design rationale: `docs/design/TILEWORLD-CLEARANCE-DESIGN-2026-10-16.md` in the
engine repo.

## Collision, derived and never authored

`TileCollisionFlags` is the per-tile bit set: `Blocked`, the four wall edges, the four corner bits, plus
`ProjectileBlocked` and `Decoration` reserved for later. A wall is ONE EDGE SHARED BY TWO TILES, so the baker
sets the edge bit on both tiles and a movement check never has to look at objects.

`TileCollisionBaker.Bake(doc, catalogs)` builds the whole map. Its overload taking
`Func<int, int, int, bool> groundBlocked` lets a game replace only the standable-ground rule. The arguments are
absolute world tile X and Z plus a zero-based plane, and the callback runs once for every tile and plane in each
loaded region. The baker applies the ordinary solid, diagonal, wall and wall-corner object passes afterward, so
an alternate ground topology does not erase placed-object or directional-wall collision. Missing regions remain
absent and read as blocked. Callback invocation order is not an API contract, so the rule must be deterministic
and independent of earlier calls.

`Rebake(map, doc, catalogs, dirtyRect, plane)` re-derives one rect through the ordinary authored-ground rule,
expanded by a tile for mirrored edges, ensuring and full-ground-baking any document region the cleared rect
touches that has no storage yet, dropping the storage of any it touches that the DOCUMENT no longer has (so a
deleted region reads blocked again rather than turning into walkable void), and gathering objects out to a margin
taken from the catalogs' largest footprint. THE CALLER MUST PASS A RECT COVERING THE FULL FOOTPRINT of anything it
removed, measured with `TileFootprint.Of` BEFORE the removal. `EdgeFlag` and `WallFacing` are the mapping helpers.

`TileCollisionMap` is the storage, keyed by region and plane, never persisted. Reads outside storage answer
`Blocked`, so an unloaded region is a wall rather than a void, and an `Or` outside storage is a NO-OP.
`EnsureRegion` is the only allocator, so a footprint or a mirrored edge spilling into a region the document does
not have is dropped and that region stays blocked. `Get`, `Or`, `Clear`, `HasRegion`, `RemoveRegion`, `Regions`.

`TileCollision.IsBlocked` and `CanStep(map, x, z, plane, dir, agentSize = 1)` are the one movement primitive the
pathfinder and a tick mover share. A cardinal step needs no wall on the leaving edge, an unblocked target and no
wall on the entering edge. A diagonal additionally needs both corner bits clear and all four cardinal sub-steps
legal, the no-corner-cutting rule, and an NxN agent must pass on every footprint tile. `CanStep` deliberately
allows EGRESS from a `Blocked` tile, so an agent standing where the ground was blocked under it can walk out.

`TileCollision.CanStand(map, x, z, plane, agentSize = 1)` is whether an NxN agent anchored on its south-west tile
at (x, z) may STAND there: no tile of the footprint is `Blocked` (an unloaded region reads blocked, so it refuses
too) and no wall lies on an edge between two tiles of the footprint. For one tile it is exactly `!IsBlocked`. For a
size above 1, `CanStep` also requires `CanStand` at the destination, because the per-tile steps cross every internal
edge along the axis of travel but not the perpendicular ones, so a large body could otherwise straddle a fence and
walk along it. `FindPath` walks through `CanStep`, so every step of a size-above-1 route lands on a standable
footprint. Size 1 is unchanged.

## Pathing, raycast, prefabs

`TilePathfinder.FindPath(map, plane, start, goal, agentSize = 1, maxRadius = 64)` is a deterministic BFS over
the collision map through `CanStep` in the fixed direction order, bounded to a square window around the start,
so both heads replay identical paths. `maxRadius` must be 1..`MaxSearchRadius` (4096), the window's two scratch
arrays being `(2r + 1)^2` entries each. `TilePath` carries `Tiles` (the steps AFTER the start), `Reached` and
`End`. An unreachable goal yields the walk to the nearest reachable tile, nearest by SQUARED EUCLIDEAN distance
to the goal, then by BFS distance, then by scan order, and a start on a `Blocked` tile behaves like any other.
The window's near and exclusive far edges must fit signed 32-bit tile coordinates. A start and radius that would
cross that boundary throw instead of wrapping the search to the opposite extreme.
**Branch on `Reached`, never on `Tiles.Count`**: a partial walk and a reached one both carry steps.

`TilePathfinder.FindPathToAny(map, plane, start, goals, agentSize, maxRadius, scratch, out int goalIndex)` walks
to the NEAREST of a goal list in ONE search and names the winner through `goalIndex`. It answers what a `FindPath`
per goal answers, for the cost of one: the shortest walk wins, goals that tie on length fall to the LOWEST index
in the list, so the caller's own order is the tie rule, and each walk is the one that goal's own search would have
built, because a cell's parent is written once at first discovery and the discovery order does not depend on which
goal ends the search. It finishes the BFS level the first goal is found on, which is what makes that index tie
total. Both entry points run ONE expansion, so the step rules, the window bound and the direction order cannot
drift between them. The one difference from `FindPath` is that there is NO nearest-reachable fallback, since a
goal set has no single tile to measure nearness to: an empty list, and a list the window cannot reach, both answer
a not-reached empty path with a `goalIndex` of -1. `TileReach.TryNearest` is the caller it exists for.

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
The picker uses `TileGroundTriangles` for its cut, split and lattice positions. Document picking includes
underlay-bearing `NoDraw` tiles for editor inspection, while drawable ground descriptions and visible-surface
picking continue to exclude them.

`TileObjectPlacement` is the ONE object transform, shared by the prop renderer, the model pick and the walk-surface
queries. `AnchorPosition(doc, archetype, o)` is the centre of the rotated footprint at the document's ground height
there, so a mesh is authored centred on its own footprint with its base at y 0. `YawRadians(archetype, rotation)`
is `-(rotation * DegreesPerRotation + YawOffsetDegrees)` in radians, NEGATIVE so `Matrix4x4.CreateRotationY` turns
clockwise seen from above with north up. `LocalToWorld(doc, archetype, o)` is the model matrix a prop draw builds,
`CreateRotationY(yaw) * CreateTranslation(anchor)` at scale 1. `TileObjectProps.AnchorPosition`, `YawRadians` and
`DegreesPerRotation` in `KhaozEngine.TileWorld.Render3D` forward here unchanged.

The anchor height is ONE bilinear sample at the footprint centre, on purpose. A footprint wider than a tile on a
slope therefore sinks at its uphill corners and stands clear at its downhill ones. The highest lattice corner under
the footprint would stop the sinking and was rejected for what it floats. A one-tile prop on a slope would hang
above the ground by up to half the tile's rise: reeds on a river's sloped rim by tens of centimetres, a tree on a
2 m step by a full metre. A deck whose footprint spans a carved channel from rim to rim would rise by the whole
carve, walk surface with it, because the deck is authored against the centre sample. A piece that has to meet
sloped ground carries its own skirt or foundation below y 0.

`TileWorldInteriors(detached, catalogs, meshes)` implements the Primitives `IInteriorSource` seam.
`Collect(window)` returns simple rectangle volumes, source-wide encoding and issue strings, with structured
issues also on `LastIssues`. The cell size divides `TileSize`, at most 25 cm (`CellSizeFor`). Indoor columns
keep only their lowest contiguous run of retained indoor planes, never bridging an outdoor gap or a storey
excluded by a wall check. Roofs apply only if that run reaches the column's highest indoor plane.
`TileInteriorRoofSamples` uses `ClearanceSamples.Build(mesh, cellSize / 2f)`, not the clearance distance field.
The core clips the retained source mesh triangles to each field cell and takes their exact minimum height.
`CeilingY` is the fallback where no roof triangle intersects, including unreadable roofs and courtyards.
On the highest plane the fallback ceiling is the corner height plus `PlaneHeight`. Inner-edge and diagonal
boundary walls leave affected tiles uncut and report `interior.wall-inner-edge` or `interior.diagonal-wall`.
Unreadable roofs report `interior.roof-mesh`, and coarser vertical encoding reports `interior.step` at
collection. No world validation integration is implied ([#1473](https://github.com/APKiwiOrg/KhaozEngine/issues/1473)).

The optional walk-surface `acceptPoint` predicate is `Func<TileObject, Vector3, bool>`. It runs for each
candidate intersection before that candidate competes for the nearest hit, so rejecting a cut surface still
allows a farther surface to win. `include` remains the separate object-level filter. `TryHeightAt` is unchanged.

`TileWalkSurfaces` answers the walk surfaces two ways. `TryHeightAt(doc, catalogs, worldX, worldZ, plane, out
height)` takes world metres, as `HeightAt` does, and reports the HIGHEST surface covering the point on that plane,
edges inclusive, as the anchor height plus `Height`. It allocates nothing and does not consult the terrain, so a
surface buried under the ground is still reported and the caller takes the higher of the two.
`Raycast(doc, catalogs, plane, origin, direction, maxDistance, include?, acceptPoint?)` returns the nearest `TileHit` on a surface
TOP. Only a descending ray can land on one, the direction need not be normalised, the distance is world metres,
the maximum is inclusive, and the tile is the floor of the hit point in tile coordinates. `include` is consulted per
candidate object, which is how a view limits the pick to what it draws. Both read the document and the catalogs
through on every call, so an edit shows up on the next frame, and both place each surface through
`TileObjectPlacement`, with quarter turns taken on exact axes so a point exactly on a deck edge stays on it under
every rotation. The search covers every anchor that could reach the point, a radius built from the farthest rect
corner over the archetypes that carry a surface plus the largest footprint side, so it holds under any yaw offset.
A catalog without a surfaced archetype answers before touching a region. Only loaded regions are searched, an
object whose archetype the catalogs do not define is skipped, and a surface covers every body on its plane under
it, so anything meant to pass beneath a deck belongs on another plane.

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
`Load` and hand-built `Place` share the size, plane-list and object/marker plane reasons in the
[refusal table](#refusals). `PrefabName` is the authored name, including an empty unnamed label.
`Load` also carries the file path in `SourceName`, while hand-built admission leaves it null. Empty,
malformed and unreadable files have no admitted prefab value, so `PrefabName` remains null.
File admission additionally checks positive plane counts and object/marker footprints. Serialized dense-layer
errors retain `LayerBase64Malformed` or `LayerByteCountMismatch`, while hand-built layer lengths use
`PrefabLayerCountMismatch`.

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

## Ground triangles and water bodies

The ground the renderer draws and the water it lays over that ground are decided here, GPU-free, so a server, a
physics bake or a test reads the same surface as `KhaozEngine.TileWorld.Render3D` without a renderer.

`TileGroundTriangles` is the full-detail ground triangle rule, and the ground mesher draws exactly its triangles.
None of its members takes catalogs.

- `IsDrawable(doc, worldX, worldZ, plane)` is true when the tile has an underlay and is not marked
  `TileSettings.NoDraw`. Every drawability check in the render package, the water rule and the visible-surface
  pick included, calls it.
- `TryDescribe(doc, worldX, worldZ, plane, out cell, triangles)` writes the tile's triangles in
  `TileTriangulation.Triangulate` order into a span of at least `TileTriangulation.MaxTriangles`, and returns false
  with nothing written for a tile that does not draw. The `TileGroundCell(Cut, Rotation, SplitSwNe, TriangleCount)`
  it hands back carries the cut shape, which is the authored overlay shape when the overlay id is not 0 and `Full`
  otherwise, and the split, which always comes from the authored shape. An overlay id the catalogs do not define
  still cuts the tile, because the renderer paints that cut with its missing-material slot.
- `LatticePosition(doc, worldX, worldZ, plane, point, originX, originZ)` places one `TileLatticePoint` relative to a
  tile corner. A corner is taken as it stands and a mid-edge point is the midpoint of its two corners, so every copy
  of a shared corner is bit-identical across tiles and regions.
- `Build(doc, region, plane)` returns a `TileGroundMesh` of one region-plane: `Region`, `Plane`, region-local
  `Positions` with three fresh points per triangle, and `Indices` running 0, 1, 2 and on. A region-plane with no
  drawable tile has both arrays empty. Tiles marked `FeatherOverlay` draw smaller triangles in the mesher, on this
  same surface.

`TileWaterBodies` is the water body rule. `IsWater(doc, catalogs, worldX, worldZ, plane)` is true for a drawable
tile whose UNDERLAY material has `GroundMaterialKind.Water`. An overlay in a water material is never water.
`Collect(doc, catalogs, region, plane)` returns the region-plane's `TileWaterBody(Rects, SurfaceY)` list: the water
tiles grouped into 4-connected bodies clipped to the region, each cut into disjoint world tile rects, with its still
surface `SurfaceDropMetres` (2 cm) under the highest corner the body touches. `Components(mask)` and
`Rectangles(mask)` are the body search and the greedy row-run rectangle cut on their own. The order of bodies and
rects is a pure function of the document. `TileWaterPlanes` in the render package turns these bodies into
`WaterPlane` requests.
