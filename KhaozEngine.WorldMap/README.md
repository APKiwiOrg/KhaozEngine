# KhaozEngine.WorldMap

GPU-free contracts, rasterization and feature anchors for stylized world maps from any authored world. The package references only
`KhaozEngine.Primitives`. It is opt-in and outside every umbrella, so consumers add it explicitly.

## Map space

All map coordinates are metres, with x east and y north. North is up and east is right. A source owns
the conversion from its world's axes and handedness. Headings are radians clockwise from map north.
Footprint rotation instead turns the rectangle's local +x axis clockwise, with half extents measured
along its local axes before rotation.

`MapRect` has a south-west origin and half-open bounds. South and west edges belong to the rectangle,
north and east edges do not. Non-positive extents are empty and intersect nothing. `Expand` moves
each edge by a distance in metres, and `Union` encloses both rectangles' edges.

`WorldMapChunking` addresses square chunks aligned to the map origin. `ChunkAt` floors both axes and
corrects against canonical edges computed as `(float)(index * (double)chunkMetres)`,
including negative positions. `ChunkRect` returns the south-west origin and square size.
`ChunksTouching` enumerates intersecting chunks west to east within each row, from south to north,
and excludes chunks that only touch an area's north or east boundary. Chunk sizes must be finite
and positive.

`WorldMapChunkCoord(X, Y, Level = 0)` addresses a level-of-detail pyramid. `LevelMetres(chunkMetres,
level)` is `chunkMetres * 2^level`, and levels must be non-negative. `ChunkAt` and `ChunksTouching`
accept an optional level, and `ChunkRect` honours the coordinate's level using the same canonical
edges. `MaxLevel(bounds, chunkMetres)` returns the smallest level spanning the bounds' larger side,
or zero for empty bounds. Each level retains the same pixel dimensions while covering twice as
many metres per axis as the preceding level.

## Source contract

Implement `IWorldMapSource` to adapt an authored world:

- `Bounds` describes the surface in map metres. `SurfaceCellSize` exposes the native sampling spacing.
- `SampleSurface(area, maxCellSize)` returns a `WorldMapSurfaceGrid` covering at least the area and aligned to the
  source lattice. `Origin` is the south-west corner of cell (0, 0), and samples sit at cell centres.
  `Index(column, row)` addresses `row * Width + column`, with row zero to the south.
  `CellSize` is the coarsest multiple of `SurfaceCellSize` no larger than
  `max(maxCellSize, SurfaceCellSize)`. A hint below native spacing keeps native spacing.
- Grid arrays start void and dry. `Ground` uses `Color.Transparent` for void,
  `SurfaceHeight` uses NaN for void, and `WaterSurface` uses NaN where dry. Heights are metres.
  Fill the three arrays at matching indices. Cell sizes must be finite and positive, dimensions
  must be non-negative, and `Index` refuses coordinates outside the grid.
- `CollectFootprints(in WorldMapFootprintQuery query, List<WorldMapFootprint> into)` appends surface footprints.
  `WorldMapFootprintQuery(Area, MinExtentMetres = 0f, AnyTags = null)` requests every footprint intersecting
  `Area` whose `2 * max(HalfExtents)` meets `MinExtentMetres` and, when `AnyTags` is not null, carries
  at least one of those tags (ordinal). Empty `AnyTags` matches none. Sources may return extra records
  failing these filters, so callers re-check. Rasterization requests `0.5f / levelPpm` metres with no tag
  filter and still skips sub-pixel footprints itself. Feature collection uses zero so anchors never
  depend on size. Footprints carry a stable id, archetype key, centre, half extents, rotation,
  tags, roof flag and `Collision` (`None`, `Solid` or `Wall`). `Blocking` derives from collision.
- `CollectMarkers(area, into)` appends surface markers whose positions are inside the half-open area.
  Markers carry an authored name, position and tags. Games resolve label names to localized text.
- `ToMap(worldPosition)` converts world positions. `ToMapHeading(worldForward)` converts a world
  forward vector to a map heading without exposing a world yaw convention. A zero vector returns zero.
- Increment `Revision` on changes and raise `Changed` with the touched map area on the data's owner
  thread. Allow concurrent worker reads. Consumers discard results sampled against an older revision.

The source filters to the mapped surface before returning footprints or markers. The contract
carries neither levels nor top heights. Footprints remain oriented bounding rectangles, with optional
symbol geometry changing only their raster silhouette and details.

## Style

`WorldMapStyle` is a sealed record with init-only settings. The neutral defaults are:

- Four pixels per metre and 64 metres per chunk. `ChunkPixels` rounds their product, giving 256 by
  256 pixels by default. Void colour is `#d9cba6`.
- `WorldMapRelief` lights from azimuth 315 degrees at altitude 45 degrees, with strength 0.5,
  steep-slope threshold 1 and steep darkening multiplier 0.75.
- `Contours` defaults to null. Set `new WorldMapContours(5f, Color.Black, WidthPixels: 0.75f)`
  for five-metre elevation lines on dry ground after relief and before footprint symbols.
  Intervals use the source's actual metre heights, including negative elevations. Width is measured
  in image pixels at every pyramid level. Colour alpha controls compositing over the shaded ground.
- `WorldMapWater` uses shallow `#6f9cc0`, deep `#2a5a9a` and shoreline `#1f3f66`. Water is fully deep
  at four metres, and the shoreline is one pixel wide.
- `SymbolRules` default to an empty array. Each rule matches an optional archetype key, all-of tags,
  roof flag and collision role. The first matching rule wins. An unmatched blocking footprint uses
  `FallbackBlocking`, an `Obstacle` with fill `#6f6a60` and outline `#3d3a34`. An unmatched
  non-blocking footprint uses `WorldMapSymbol.Hidden`, with transparent fill and outline.
- Symbols draw in declaration order after `Hidden`: `Bridge`, `Obstacle`, `Wall`, `Building`, `Tree`.
  Building shadows use black at alpha 0.35 and offset (0.6, -0.6) metres, east and south.
  Walls are 0.4 metres wide, and outlines are one raster pixel wide.
- `WorldMapSymbol(Kind, Fill, Outline)` has an optional `Vector2? SizeMetres` init property. Set the
  full local width and depth in map metres, for example `SizeMetres = new Vector2(3.5f)` for a tree crown.
  The symbol stays centred on its footprint and
  follows its rotation. Building shadows use the same size. Sized walls use both explicit dimensions
  instead of `WallWidthMetres`. Null preserves the authored footprint dimensions and existing raster bytes.
  Coarse admission still uses the authored footprint, not this drawn size. This keeps source filtering
  before materialization bounded without requiring every small prop to be collected to resolve style rules.
  Icons, labels and hover targets remain unchanged.
- `WorldMapSymbol.Geometry` optionally replaces the default rectangle or ellipse with a
  `WorldMapSymbolGeometry(outline, strokes: null)`. The constructor snapshots both inputs and exposes
  read-only `Outline` and `Strokes` collections. Use finite normalized coordinates in `[-1, 1]` on each
  local axis. The outline needs at least three vertices enclosing a nonzero area, may be concave and
  accepts either winding. Its last vertex connects to the first. Do not repeat the closing vertex or
  supply crossing, touching or overlapping edges. Invalid geometry throws `ArgumentException` at construction.
  Each `WorldMapSymbolStroke(Start, End)` is an independent open detail segment. Geometry follows the
  existing dimensions, `SizeMetres`, rotation and wall sizing. `Fill` paints the polygon. `Outline`
  paints its inset boundary and centred round-capped details using `OutlinePixels` in raster pixels,
  including non-square symbols and coarse levels. Details blend over polygon fill. Overlapping ink
  shares coverage once. Details can
  lie outside a concave fill and are clipped to the normalized footprint box. Building shadows use
  only the filled silhouette. Null preserves existing raster bytes, anchors and hover targets.
- `IconRules` default to an empty array. Rules select footprint or marker tags, an icon key, a priority
  and a merge radius, defaulting to 24 screen pixels. `Labels` defaults to null. When enabled,
  `WorldMapLabelRules` selects a label tag and tagged tiers with rank, zoom range and text scale.

## Surface rasterization

Call `WorldMapRasterizer.Rasterize(source, style, chunk)` to produce a `WorldMapChunkImage`.
Rasterization and cache construction reject invalid styles with `ArgumentException` naming the
offending property. Resolution and chunk size must be finite and positive, with at least one rounded
pixel. Relief altitude is finite in `(0, 90]`, strength and steep darkening are finite in `[0, 1]`,
and the steep slope threshold is finite and non-negative. Deep water depth is finite and positive.
Shoreline pixels, wall width and outline width are non-negative, with finite floating-point widths.
Both shadow offset components must be finite.
Contour interval and pixel width must be finite and strictly positive.
Symbol size components must be finite and positive, with a finite half-diagonal reach.
`Coord` identifies the chunk, `Size` is its interior pixel edge length covering the chunk rectangle, and
`Border` is the gutter width on each side, defaulting to zero. `TextureSize` is `Size + 2 * Border`, and
`Rgba` holds `TextureSize * TextureSize * 4` opaque RGBA8 bytes. Raster row zero is north, and column zero
is west. `ByteOffset(column, row)` addresses interior-relative pixels, so the gutter runs from `-Border`
to `Size + Border - 1` on each axis. `Revision` is captured before sampling, so a consumer can discard an
image when the source changes during the read.

`Rasterize(source, style, chunk, border)` paints the gutter from the adjacent map area, using the same
surface, water, contour and footprint rules as the interior. Gutter pixels therefore match the edge
pixels of the neighbouring chunks, including void beyond the world bounds, and the interior bytes equal
the borderless raster. The three-argument overload and the positional constructor keep a zero border.
`WorldMapChunkCache` builds every image with a one-pixel border, so bilinear display filtering at a chunk
edge reads real neighbouring imagery rather than wrapping to the chunk's opposite edge. Code that reads
cache image pixels indexes through `ByteOffset` and uploads `TextureSize`, never `Size`, as the texture
extent.

At level `L`, the rasterizer uses `levelPpm = EffectivePixelsPerMetre / 2^L` and requests
`SampleSurface(area, maxCellSize: 2f / levelPpm)` once for each chunk that owns output pixels. Each query
covers that owner's full chunk rectangle plus a margin of
`2 * returnedCellSize + Water.ShorelinePixels / levelPpm` metres, exactly as its borderless raster would.
Preserving this query frame also preserves the returned grid origin and fractional source-cell ownership.
A borderless image makes one surface query. A cache image's one-pixel gutter makes nine. Wider gutters
query every owner they reach once. The image retains one revision stamp captured before the first query.
These reads can materialize full owner grids, but pixel painting, contours and shoreline masks cover only each
owner's clipped output region and its required halo. Neighbour images are never rasterized in full.
The resolution contract determines the returned cell size before sampling, so the margin covers coarse
relief samples too.
When contours are enabled, replace the shoreline pixel radius in that margin with
`Max(Water.ShorelinePixels, Contours.WidthPixels / 2 + 1)`. This bounded neighbourhood covers
the contour's height differences and stroke fringe, and participates in cache invalidation.
It blends ground colours and
heights between source cell centres, dropping void contributions and renormalizing the remaining
weights. Pixels whose nearest cell is void retain `VoidColor`. Dry ground receives normalized
hillshade and the configured steep-slope multiplier. Water blends the depth palette without relief,
then receives shoreline colour when dry, non-void pixels lie within the configured Chebyshev radius,
including across chunk edges.

Optional contours estimate distance to a height-interval multiple from the local height gradient,
then antialias coverage in raster pixels. They require a height crossing in their bounded
neighbourhood. Water, void and flat plateaus at an interval are excluded. Null contours retain
the existing sampling margin and raster bytes.

Footprints are collected over the painted area expanded by `BuildingShadowOffsetMetres.Length()` plus
half of `WallWidthMetres`, one raster pixel and the largest visible symbol's drawn half diagonal.
This includes the blocking fallback and excludes hidden symbols. The half diagonal covers any rotation,
including rectangles whose corners extend beyond their largest local half extent. The same margin widens
cache invalidation at every level. Surface sampling and footprint collection use the
shared internal `WorldMapRasterReach` margins. `WorldMapSymbolResolver.Matches` tests every non-null
rule field with ordinal archetype and all-of tag matching. `SymbolFor` selects the first matching rule,
then the blocking fallback or hidden symbol. All building shadows draw first. Symbols then draw by kind in declaration
order and ascending footprint `Id` within each kind.
Footprints whose larger extent times `levelPpm` is below half a pixel are skipped, including their
building shadows. Footprint reach converts its extra pixel at `levelPpm`.

Buildings and bridges use oriented rectangles. Trees and obstacles use inscribed ellipses. Walls
follow the longer local axis, choosing local x on a tie, with the configured wall width and no outline.
Other outlines stay inside the shape by subtracting its inset fill coverage. Each pixel uses a fixed
four by four subsample pattern. Fill and outline occupy disjoint samples. Their alpha-weighted colour
contributions are combined into one blend per pixel, so opaque symbols fully replace covered ground.

Colour calculations use the stored channel values directly. Quantization clamps each channel to
zero through one, rounds to the nearest byte, and sets alpha to 255. Matching source data, revision
and style produce identical bytes on one machine. `PixelsPerMetre` is the requested scale.
`EffectivePixelsPerMetre` is `ChunkPixels / ChunkMetres`, and all raster geometry uses that rounded
scale, divided by `2^Level`, so the image fills its chunk rectangle. Chunk images match the corresponding pixels of a
larger raster with the same effective pixels per metre and aligned pixel centres. Increasing chunk
size at the same requested scale can round to a different effective scale.

## Feature anchors

Call `WorldMapFeatureCollector.Collect(source, style, chunk)` to produce `WorldMapChunkFeatures`.
The collector queries footprints only when a footprint icon rule exists. Its zero-size query uses the
ordinal union of every footprint rule's `AllTags`, cached once per immutable style instance. Any
footprint rule with empty `AllTags` disables the query's tag filter. Exact rule matching still checks
every required tag. Marker collection runs only when marker icon rules or label rules exist.
Both queries cover the chunk rectangle. A footprint icon belongs to
the chunk containing its `Center`, and a marker's icon and label belong to the chunk containing its
`Position`. Ownership uses `ChunkAt(anchor, style.ChunkMetres, chunk.Level) == chunk`, so each
anchor belongs to one chunk per pyramid level, including when a footprint crosses a chunk border.

Each feature takes the first `IconRules` entry whose `Source` matches its kind and whose `AllTags`
are all present. Tag matching is ordinal and case-sensitive. An empty requirement matches any tags.
`WorldMapIconAnchor` preserves the rule's `IconKey`, `Priority` and `MergeRadiusPixels`.

A marker becomes a label only when it carries `Labels.LabelTag` and at least one tier's `Tag`.
The first matching tier wins. `WorldMapLabelAnchor` preserves `MarkerName`, `Position` and that tier.
Null label rules disable labels. A marker can produce both an icon and a label.

Both collections sort by descending `Position.Y`, ascending `Position.X`, then ordinal `IconKey`
or `MarkerName`. Collected lists are read-only. `WorldMapChunkFeatures.Empty` contains no anchors.
Screen-space merging, label placement and localized text resolution belong to the view and game.

## Chunk cache and retention

`WorldMapChunkCache(source, style, maxRetainedChunks: 256)` builds generation-tagged imagery and
feature anchors together. The cap must be at least one. `MaxRetainedChunks` exposes the cap, and
`RetainedCount` includes both current images and stale images retained during rebuilds.
`Request` refreshes a chunk's request sequence even when its image is already current.
After every build completion, including failure, and before a no-work `BuildNext` returns,
excess images are evicted until `RetainedCount <= MaxRetainedChunks`. Eviction visits chunks without
pending or active work first, then pending or building chunks, in oldest-request order within each
group. The 256 default chunks hold 258 by 258 RGBA pixels each, a 256-pixel interior plus the one-pixel
gutter, for a retained image payload bound of about 65 MiB, excluding feature metadata and temporary
build allocations.

Generations are `long` values allocated by one cache-wide monotonically increasing counter.
A first request and every touching invalidation assign a fresh generation. A build captures it
at start, and `IsCurrent` compares it with the entry's current generation.
Eviction drops the retained image. Pending and building chunks keep their flags and generation entry,
so their queued or active builds still publish. `TryGet` and `IsCurrent` return false until a build
publishes another image. Other evicted chunks lose their entry too. Requesting those chunks again
rebuilds them with a never-used generation, permitting views to replace any surviving upload.
Uploaded view textures are independent of retained cache images. `Invalidate(area)` expands the edit
separately for each cached level using that level's surface cell size, footprint reach and one gutter pixel
at the level's raster scale. Builds run outside
the bookkeeping lock. An invalidation during a build leaves its result stale, and a failed source
read quarantines only that generation until invalidation permits a retry.

The [world-map design](../docs/design/WORLD-MAP.md) describes the broader
engine round and the consumers of these contracts.
