# KhaozEngine.TileWorld.WorldMap

The GPU-free `IWorldMapSource` adapter for a `TileWorldDocument` and its `TileWorldCatalogs`.
It references only `KhaozEngine.WorldMap` and `KhaozEngine.TileWorld`. It is opt-in and outside
every umbrella, so consumers add the package explicitly.

```csharp
var source = new TileWorldMapSource(document, catalogs, surfacePlane: 0);
var image = WorldMapRasterizer.Rasterize(source, new WorldMapStyle(), new WorldMapChunkCoord(0, 0));
```

An optional map-only palette overrides selected material colours:

```csharp
var palette = new Dictionary<ushort, Color> { [1] = Color.FromHex("#b8b8b8") };
var source = new TileWorldMapSource(document, catalogs, surfacePlane: 0, groundPalette: palette);
```

The source copies the dictionary at construction. Later caller changes cannot affect worker reads.
Missing palette entries fall back to catalog colours. Null preserves existing sampling. The palette
does not change the source catalogs, heights, water roles, footprints or markers.
The palette overload takes an explicit `surfacePlane`. The original three-parameter constructor remains available.

## Static source and coordinates

Keep the document and catalogs unchanged for the source's lifetime. Concurrent worker reads are
supported. `Revision` is always zero and `Changed` is never raised. Create a new source after edits
or region loading changes.

Map x is east and map y is north, both in metres. Tile `(x, z)` becomes map `(x, z) * TileSize`.
`Bounds` is the union of loaded 64 by 64 tile region rectangles, including negative regions.
An empty document has empty bounds. `SurfaceCellSize` is the document's `TileSize`.
`ToMap` converts through `TileWorldSpace.TileX` and `TileZ` before scaling into map metres.
`ToMapHeading` returns `Atan2(worldForward.X, -worldForward.Z)`, clockwise from north.
A zero forward vector returns zero. World minus Z is north and world plus X is east.

## Surface sampling

`SampleSurface(area, maxCellSize)` uses stride `n = max(1, floor(maxCellSize / TileSize))`.
Its cell size is the coarsest native multiple no larger than `max(maxCellSize, TileSize)`.
Cells cover `n` by `n` tiles, aligned to multiples of `n` tiles, including negative coordinates.
Each cell point-samples the tile at `blockStart + n / 2` using integer division for both axes.
Colour, mean corner height and water surface all come from that tile. A stride of one preserves
native sampling byte for byte.

Sampling rounds the requested map rectangle outward to this stride lattice. Its grid origin is
the south-west corner of cell `(0, 0)`, and row zero is the south row. Raster images reverse this row
order to put north at the top.

- A tile outside loaded regions or without a resolved underlay is void. Its ground colour is
  `Color.Transparent`, and its surface and water heights are NaN.
- Ground colour starts with the underlay material's parsed hex colour. A non-zero, resolved overlay
  replaces it for the whole tile. Overlay shapes are ignored. An unresolved overlay keeps the underlay.
  Palette lookup uses that selected material id after resolving the overlay. An unknown underlay stays
  void even if the palette contains its id.
- Surface height is the mean of the four corner heights on `surfacePlane`, in metres.
- Water tiles are identified by `TileWaterBodies.IsWater`. Their water height is the covering body's
  `TileWaterBody.SurfaceY`, already in world metres. Dry tiles have NaN water height. Water-coloured
  overlays do not create a body. Bodies keep the TileWorld region-clipped surface rule.

Parsed colours are cached per material id. Water bodies are collected lazily per region and expanded
into a cached tile-to-surface lookup in a `ConcurrentDictionary`.

The core pyramid uses `WorldMapChunkCoord(X, Y, Level = 0)` and chunk lengths
`ChunkMetres * 2^Level`, with fixed image dimensions. Raster resolution is
`EffectivePixelsPerMetre / 2^Level`, and the rasterizer requests `maxCellSize: 2f / levelPpm`.
The adapter's stride therefore grows as coarse chunks cover more of the world.
`WorldMapChunkCache` defaults to retaining 256 built chunks, an RGBA image payload bound of about 65 MiB for
258 by 258 default images including their gutters after every `BuildNext` returns. It evicts chunks without
pending or active work first, then pending or building images, in oldest-request order within each group.
Pending and building entries keep their flags and generation so their builds still publish.
`TryGet` returns false for an evicted image until another build publishes. View uploads remain
independent of retained cache images. Feature metadata and build allocations are additional memory.
Trimming also runs after failed builds and before no-work returns. Cache generations are
monotonically increasing `long` values shared across entries. Requests for entries removed by eviction receive
fresh generations so surviving view uploads can be replaced.

## Footprints and markers

`CollectFootprints(in WorldMapFootprintQuery query, List<WorldMapFootprint> into)` queries object anchors in `query.Area`'s tile rectangle
expanded by the largest archetype `Max(SizeX, SizeZ)`, computed once when the source is created. This includes large roofs
anchored in neighbouring chunks. It keeps objects on `surfacePlane` and roofs on any plane, then
rejects archetypes with `max(SizeX, SizeZ) * TileSize < query.MinExtentMetres`. When `query.AnyTags`
is not null, it also rejects objects without an ordinal match in either the archetype's tags or the
object's own tags. An empty tag filter rejects every object. Both filters run before constructing any
footprint or combined tags. No per-object data is precomputed for these filters. Remaining objects
are filtered by intersection between the rotated footprint's map rectangle and the requested area.
Unknown archetypes are skipped.
Rasterization supplies `0.5f / levelPpm` metres with no tags and retains its own sub-pixel skip.
Symbols can set `WorldMapSymbol.SizeMetres` for a drawn crown larger than the trunk footprint.
The rasterizer widens its query area by the largest visible drawn half diagonal, so crowns from
neighbouring chunks are included. The adapter still reports the authored footprint and filters coarse
requests by the archetype size before materialization. Drawn size does not change feature anchors.
Feature collection supplies zero and the cached union of footprint icon rule tags to preserve every eligible
anchor. Empty rule requirements disable the tag filter, and no footprint rules skip this collection.
Every intersecting footprint satisfying both query filters is returned. The source contract permits
extra records, so consumers still re-check exact rules and raster size budgets.

The centre comes from the `TileFootprint.Of` rectangle. Half extents are the unrotated
`(SizeX, SizeZ) * TileSize / 2`, and rotation is the object's quarter turns times PI / 2.
Tags combine archetype tags followed by object tags, removing ordinal duplicates while preserving
order. Collision maps `None` to `None`, `Solid` to `Solid`, and `Wall`, `WallCorner` and `Diagonal`
to `Wall`.

`CollectMarkers` reads regions touching the area and keeps markers on `surfacePlane` whose tile-centre
positions lie within the half-open map rectangle. Marker names and tags are preserved. Both collection
methods append to the caller's list.
