# World map contracts

Status: implemented GPU-free map generation, source adapters and GUI view.

The engine derives map imagery from authored data rather than capturing the 3D scene. Games own the
binding, window, styling, icons, labels and live marker policy. The
[core README](../../KhaozEngine.WorldMap/README.md),
[TileWorld source README](../../KhaozEngine.TileWorld.WorldMap/README.md) and
[view README](../../KhaozEngine.WorldMap.Gui/README.md) own API usage and limits.
The [atlas design](WORLD-MAP-ATLAS-DESIGN-2026-10-20.md) owns optional ink styling.

## Owner contracts

| Id | Current contract and reason |
| --- | --- |
| WM1 | Stylized data-derived ground, relief, depth-tinted water and footprint symbols. No GPU capture or backend-specific renderer is needed to generate a map. |
| WM2 | A map is a view over a shared chunk cache. Another view can share it and live marker inputs without creating another imagery pipeline. |
| WM3 | Imagery, tag-derived feature anchors, labels and caller-supplied live markers are separate layers. The engine supplies no player population, creature or fog-of-war policy. |
| WM4 | `WorldMap`, `TileWorld.WorldMap` and `WorldMap.Gui` are explicit opt-in packages outside umbrellas. Core depends only on Primitives, adapters own format conversion and GUI owns textures/input. Consumer adoption is independent. |

## Coordinate and source ownership

Map X is east and map Y is north, in metres. North is up and east is right on screen. The adapter
handles world handedness once. `MapRect` is half-open at north/east and includes south/west.
Negative chunk coordinates floor rather than truncate. Source grids run south to north with samples
at cell centres, while raster row zero is the north edge. Confusing those two row conventions mirrors
the imagery.

`SampleSurface` returns a lattice-aligned grid covering the request. Its spacing is the coarsest
native-spacing multiple no larger than `max(maxCellSize, SurfaceCellSize)`. Void has transparent
ground and NaN heights. Dry cells have NaN water height. Rasterization interpolates valid neighbours
without manufacturing land where the nearest source cell is void.

Footprint collection must include every intersecting oriented rectangle meeting the extent/tag query.
Sources may over-return because consumers recheck. Query tags use any-of matching, including an empty
set matching none. Style rules still use all-of matching. This distinction permits cheap source
filtering without changing style semantics. A footprint's extents precede rotation. Feature ownership
uses its centre, or a marker's point, so a half-open chunk border has one anchor owner per level.

A source supports concurrent worker reads and changes only on its owner thread, incrementing Revision
and raising Changed for the touched area. Images capture revision before sampling. The TileWorld
adapter is immutable for its lifetime, keeps revision zero and raises no changes. Replace its pipeline
after editing the document, catalogs or loaded-region set.

## Raster and cache invariants

Defaults are 4 pixels per metre and 64 m chunks, giving 256-square images. Relief lights from the
north-west at 45 degrees. The style is immutable. First matching symbol rule wins, with a blocking
unmatched footprint shown as Obstacle and a nonblocking one Hidden. Shadows precede symbols, then
Bridge, Obstacle, Wall, Building and Tree order, with ascending footprint ID within each kind.
Four-by-four subsampling keeps fill and outline coverage disjoint. A sub-half-pixel footprint also
loses its shadow. Colour math uses stored channel values without sRGB decoding.

Level L spans `ChunkMetres * 2^L` metres with the same pixel dimensions. Raster scale falls by `2^L`.
This pyramid is required for whole-world fit to remain bounded as the world grows (world-map R11).
Surface and footprint sampling margins share `WorldMapRasterReach`, including shoreline, shadow,
wall and pixel reach. Invalidation expands by those margins separately at each cached level.

Every requested or invalidated entry receives a fresh cache-wide monotonically increasing generation.
A build publishes the generation it started with. An invalidation during that build keeps its result
stale. Evicting and requesting the same coordinate cannot revive an old upload. A failed generation
is quarantined while the worker continues, and invalidation permits retry. Last successful imagery
may remain available during replacement.

The default retained-image cap is 256, requiring at least one. Request sequence determines age.
Eviction prefers entries without pending/active work, then pending/active entries, oldest first in
each group. Evicting an image with work pending does not cancel that work. Default 256-square RGBA
images have a one-pixel cache gutter, making each stored image 258-square. At 256 retained images
that is about 65 MiB of RGBA payload, excluding features and temporary storage.
Uploaded view textures have a separate lifetime and cap.

## View ownership

`WorldMapView` requests visible chunks nearest its centre first. One worker rasterizes. The frame
thread uploads at most four chunks per update by default and retains an old texture until its
replacement arrives. `Draw` uses the caller's open UI batch and clips geometry/UVs without changing
the caller's scissor or beginning another batch. Pointer and viewport share coordinates.

Pan and cursor-anchored zoom clamp to the world. The default maximum is 8 screen pixels per metre,
independent of raster resolution. Labels precede icons, then live markers, at fixed screen size.
Decluttering uses screen-space priority and merge rules. Missing text, font or sprites hides the
corresponding overlay. Marker placement and heading conversion do not infer game world-yaw policy.

Create, update, draw and dispose views on the surface frame thread. Views own their uploaded chunk
textures and borrow overlay sprites/fonts. Dispose views, worker, then cache before releasing source
data or the surface. Same-machine deterministic bytes and cross-platform two-byte-per-channel golden
tolerance describe imagery correctness, not a runtime timing guarantee.
