# KhaozEngine.WorldMap

GPU-free contracts for stylized world-map imagery from any authored world. The package references only
`KhaozEngine.Primitives`. It is opt-in and outside every umbrella, so consumers add it explicitly.

## Map space

All map coordinates are metres, with x east and y north. North is up and east is right. A source owns
the conversion from its world's axes and handedness. Headings are radians clockwise from map north.
Footprint rotation instead turns the rectangle's local +x axis clockwise, with half extents measured
along its local axes before rotation.

`MapRect` has a south-west origin and half-open bounds. South and west edges belong to the rectangle,
north and east edges do not. Non-positive extents are empty and intersect nothing. `Expand` moves
each edge by a distance in metres, and `Union` encloses both rectangles' edges.

`WorldMapChunking` addresses square chunks aligned to the map origin. `ChunkAt` floors both axes,
including negative positions. `ChunkRect` returns the south-west origin and square size.
`ChunksTouching` enumerates intersecting chunks west to east within each row, from south to north,
and excludes chunks that only touch an area's north or east boundary. Chunk sizes must be finite
and positive.

## Source contract

Implement `IWorldMapSource` to adapt an authored world:

- `Bounds` describes the surface in map metres. `SurfaceCellSize` exposes the native sampling spacing.
- `SampleSurface(area)` returns a `WorldMapSurfaceGrid` covering at least the area and aligned to the
  source lattice. `Origin` is the south-west corner of cell (0, 0), and samples sit at cell centres.
  `Index(column, row)` addresses `row * Width + column`, with row zero to the south.
- Grid arrays start void and dry. `Ground` uses `Color.Transparent` for void,
  `SurfaceHeight` uses NaN for void, and `WaterSurface` uses NaN where dry. Heights are metres.
  Fill the three arrays at matching indices. Cell sizes must be finite and positive, dimensions
  must be non-negative, and `Index` refuses coordinates outside the grid.
- `CollectFootprints(area, into)` appends every surface footprint whose oriented rectangle
  intersects the area. Footprints carry a stable id, archetype key, centre, half extents, rotation,
  tags, roof flag and `Collision` (`None`, `Solid` or `Wall`). `Blocking` derives from collision.
- `CollectMarkers(area, into)` appends surface markers whose positions are inside the half-open area.
  Markers carry an authored name, position and tags. Games resolve label names to localized text.
- `ToMap(worldPosition)` converts world positions. `ToMapHeading(worldForward)` converts a world
  forward vector to a map heading without exposing a world yaw convention. A zero vector returns zero.
- Increment `Revision` on changes and raise `Changed` with the touched map area on the data's owner
  thread. Allow concurrent worker reads. Consumers discard results sampled against an older revision.

The source filters to the mapped surface before returning footprints or markers. The contract
carries neither levels nor top heights, and this round supports oriented rectangles only.

## Style

`WorldMapStyle` is a sealed record with init-only settings. The neutral defaults are:

- Four pixels per metre and 64 metres per chunk. `ChunkPixels` rounds their product, giving 256 by
  256 pixels by default. Void colour is `#d9cba6`.
- `WorldMapRelief` lights from azimuth 315 degrees at altitude 45 degrees, with strength 0.5,
  steep-slope threshold 1 and steep darkening multiplier 0.75.
- `WorldMapWater` uses shallow `#6f9cc0`, deep `#2a5a9a` and shoreline `#1f3f66`. Water is fully deep
  at four metres, and the shoreline is one pixel wide.
- `SymbolRules` default to an empty array. Each rule matches an optional archetype key, all-of tags,
  roof flag and collision role. The first matching rule wins. An unmatched blocking footprint uses
  `FallbackBlocking`, an `Obstacle` with fill `#6f6a60` and outline `#3d3a34`. An unmatched
  non-blocking footprint uses `WorldMapSymbol.Hidden`, with transparent fill and outline.
- Symbols draw in declaration order after `Hidden`: `Bridge`, `Obstacle`, `Wall`, `Building`, `Tree`.
  Building shadows use black at alpha 0.35 and offset (0.6, -0.6) metres, east and south.
  Walls are 0.4 metres wide, and outlines are one raster pixel wide.
- `IconRules` default to an empty array. Rules select footprint or marker tags, an icon key, a priority
  and a merge radius, defaulting to 24 screen pixels. `Labels` defaults to null. When enabled,
  `WorldMapLabelRules` selects a label tag and tagged tiers with rank, zoom range and text scale.

The [world-map design](../docs/superpowers/specs/2026-10-12-world-map-design.md) describes the broader
engine round and the consumers of these contracts.
