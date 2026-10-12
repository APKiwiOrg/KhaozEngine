# KhaozEngine.TileWorld.WorldMap

The GPU-free `IWorldMapSource` adapter for a `TileWorldDocument` and its `TileWorldCatalogs`.
It references only `KhaozEngine.WorldMap` and `KhaozEngine.TileWorld`. It is opt-in and outside
every umbrella, so consumers add the package explicitly.

```csharp
var source = new TileWorldMapSource(document, catalogs, surfacePlane: 0);
var image = WorldMapRasterizer.Rasterize(source, new WorldMapStyle(), new WorldMapChunkCoord(0, 0));
```

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
`WorldMapChunkCache` defaults to retaining 256 built chunks, a 64 MiB RGBA image payload bound for
256 by 256 default chunks once pending work drains. It evicts the oldest requested eligible chunk,
protecting pending and building chunks. Protected rebuilds can temporarily exceed the cap, and
feature metadata and build allocations are additional memory.

## Footprints and markers

`CollectFootprints` queries object anchors in the area's tile rectangle expanded by the largest
archetype `Max(SizeX, SizeZ)`, computed once when the source is created. This includes large roofs
anchored in neighbouring chunks. It keeps objects on `surfacePlane` and roofs on any plane, then
filters by intersection between the rotated footprint's map rectangle and the requested area.
Unknown archetypes are skipped.

The centre comes from the `TileFootprint.Of` rectangle. Half extents are the unrotated
`(SizeX, SizeZ) * TileSize / 2`, and rotation is the object's quarter turns times PI / 2.
Tags combine archetype tags followed by object tags, removing ordinal duplicates while preserving
order. Collision maps `None` to `None`, `Solid` to `Solid`, and `Wall`, `WallCorner` and `Diagonal`
to `Wall`.

`CollectMarkers` reads regions touching the area and keeps markers on `surfacePlane` whose tile-centre
positions lie within the half-open map rectangle. Marker names and tags are preserved. Both collection
methods append to the caller's list.
