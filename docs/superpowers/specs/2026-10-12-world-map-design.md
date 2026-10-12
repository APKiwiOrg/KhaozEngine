# World map: design

- **Date:** 2026-10-12
- **Status:** design approved, engine round awaiting final review
- **First consumer:** Grimhollow, whose game-side spec is `docs/superpowers/specs/2026-10-12-world-map-design.md` on its
  `feature/world-map` branch
- **Related:** Grimhollow #456 (much larger worlds, caves, deep oceans), Grimhollow #441 (Hollowmere moves to MapDoc)

## Goal

A game can show a stylized world map derived from its authored world. The map is generated from the world data at
runtime, so editing terrain, water, props or buildings changes the map with no hand-made map art and no bake step.
The engine owns turning a world into map imagery and a reusable pan and zoom map widget. Each game owns its key
binding, window, styling table, icons and text.

## Owner rulings this design follows

- **WM1 Look:** stylized, in the manner of the OSRS world map. Ground material colours with relief shading, depth-tinted
  water, building roof footprints, tree canopies and service icons. Not a GPU capture of the 3D scene.
- **WM2 Surfaces:** a large world map window first. The design keeps a later corner minimap a second view over the same
  chunks and live markers.
- **WM3 Layers:** terrain, water, buildings and trees, the local player marker, service icons derived from tags, and
  authored place-name labels. No other players, creatures or fog of war.
- **WM4 Delivery:** engine first. The packages ride the staged engine 20.33.0, and Grimhollow adopts 20.33.0 before its
  game-side work.

## Non-goals

- No GPU, scene or mesh rendering. The map is drawn on the CPU from world data.
- No MapDoc adapter in this round. MapDoc R5 is about to change buildings and interiors, so the adapter is built when
  Grimhollow moves to MapDoc and is filed as its own engine issue.
- No minimap view, no levels below or above the surface, no fog of war, no click to travel. The contracts leave room for
  each without building them.
- No umbrella membership. The three packages are opt-in references.

## Packages and dependency edges

| Package | Depends on | Holds |
| --- | --- | --- |
| `KhaozEngine.WorldMap` | `Primitives` | The source contract, style, rasterizer, chunk cache and feature anchors. GPU-free. |
| `KhaozEngine.TileWorld.WorldMap` | `WorldMap`, `TileWorld` | `TileWorldMapSource`, the TileWorld adapter. |
| `KhaozEngine.WorldMap.Gui` | `WorldMap`, `Gui` | `WorldMapView`, the pan and zoom widget with overlays. |

No third-party dependency is added. The split follows the existing `MapDoc` and `MapDoc.Physics` and the `TileWorld`
and `TileWorld.Render3D` pairs: a GPU-free core that headless tests and tools can use, adapters per world format, and a
render-side consumer. A later `KhaozEngine.MapDoc.WorldMap` adapter slots in beside the TileWorld one. Each package gets
its root `README.md` catalog row, a package README and an entry in `KhaozEngine.slnx`. The architecture tests must
keep passing with all three outside every umbrella.

## Map space

All map data is in map space: x east and y north, in metres, with north up on screen and east to the right. A source
converts from its world format into map space. The TileWorld adapter converts through `TileWorldSpace`, which
already negates z once so that a top-down view has north up and east right (TILE-WORLD-DESIGN 7.4). This is the one
place handedness is handled. Every other package works in map space only.

`MapRect` is an axis-aligned, half-open rectangle with a south-west origin. South and west edges are included,
north and east edges are excluded, and zero-area rectangles intersect nothing. A chunk is a square of `ChunkMetres`
aligned to the map-space origin and addressed by integer `(cx, cy)`.

## Source contract

```csharp
public interface IWorldMapSource
{
    MapRect Bounds { get; }
    float SurfaceCellSize { get; }
    long Revision { get; }
    event Action<MapRect>? Changed;

    WorldMapSurfaceGrid SampleSurface(MapRect area);
    void CollectFootprints(MapRect area, List<WorldMapFootprint> into);
    void CollectMarkers(MapRect area, List<WorldMapMarker> into);

    Vector2 ToMap(Vector3 worldPosition);
    float ToMapHeading(Vector3 worldForward);
}
```

- **`SampleSurface`** returns the source's own surface grid over an area: origin, cell size, width and height, and per
  cell a ground colour (`Color.Transparent` for void), a surface height (NaN for void) and a water surface height
  (NaN where dry). The grid covers at least the requested area and is aligned to the source lattice, with its origin
  at the south-west corner of cell (0, 0). Rows run from south to north. Samples sit at cell centres. The
  source picks its native spacing (`TileSize` for TileWorld, whatever the terrain resolves to for MapDoc). The
  rasterizer resamples it, so the source never knows about pixels.
- **`SurfaceCellSize`** exposes the native source lattice spacing in metres.
- **`CollectFootprints`** appends every surface object's oriented rectangle whose shape intersects the area: id,
  archetype key, centre, local half extents, rotation, tags, `IsRoof` and `Collision` (`None`, `Solid` or `Wall`).
  `Blocking` derives from collision being other than `None`. Rotation is radians clockwise from local +x on the
  north-up map, and half extents are measured before rotation. Shapes are oriented rectangles only. The MapDoc
  adapter will derive those rectangles from resolved placement shapes. Top height and level are omitted because the
  source already filters to the mapped surface.
- **`CollectMarkers`** appends surface markers whose positions the half-open area contains: name, map position and tags.
- **`Revision` and `Changed`** carry dynamism. A source whose data changes raises `Changed` with the touched area on the
  thread that owns the data and increments `Revision`. A static source never raises it.
- **Thread safety:** the rasterizer reads a source from a worker thread. A source must allow concurrent reads and
  change only on its owner thread. Images carry the revision captured before sampling. `Changed` invalidates cache
  generations, so an invalidation during a build leaves its result stale until a replacement is built and uploaded.
- **Levels:** this round maps one surface. The TileWorld adapter's `surfacePlane` defaults to 0 and selects terrain,
  objects and markers. Roofs on any plane are included. General cave and upper-floor view selection is deferred.
- **`ToMap` and `ToMapHeading`** let a game place live markers (the player arrow) from world poses. The heading takes
  a world forward vector, returns radians clockwise from map north and returns zero for a zero vector. No world yaw
  convention leaks into the contract.

### TileWorld adapter

`TileWorldMapSource` wraps a `TileWorldDocument` and its `TileWorldCatalogs`.

- One surface cell per tile at the document's `TileSize`. A missing region or unresolved underlay is void. A resolved
  non-zero overlay replaces the underlay colour for the whole tile, ignoring overlay shapes. Height is the mean of the
  four corner heights on `surfacePlane`. `TileWaterBodies.IsWater` selects water tiles, which report the covering
  body's `SurfaceY` in metres. Dry cells have NaN water height, and void cells have NaN surface and water heights.
- Footprints come from `TileObject` plus its archetype. `TileFootprint.Of` supplies the centre, half extents use the
  unrotated archetype size, and object quarter turns supply rotation. `IsRoof` and `CollisionKind` give the roof and
  collision roles. Archetype tags and object tags combine with ordinal duplicates removed in their original order.
- Markers come from `TileMarker` with their tags.
- Bounds are the union of loaded region extents.
- Keep the document and catalogs unchanged for the adapter's lifetime. `Revision` stays zero and `Changed` is never
  raised. Create a new source and map pipeline after edits or changes to loaded regions.

## Style

`WorldMapStyle` is an immutable description owned by the game, with neutral engine defaults.

- **Raster:** `PixelsPerMetre` (default 4) and `ChunkMetres` (default 64), so a default chunk is 256 by 256 pixels.
- **Relief:** light from the north-west at 45 degrees altitude, a strength that keeps ground colours readable, and a
  steepness threshold above which slopes shade markedly darker so cliffs read as edges.
- **Water:** a shallow colour, a deep colour, the depth at which water is fully deep, and a shoreline colour and width.
- **Symbol rules:** an ordered list of rules from a footprint match (archetype key, all-of tags, roof flag, collision
  role) to a symbol. Symbols are `Hidden`, then `Bridge`, `Obstacle`, `Wall`, `Building` and `Tree` in draw order,
  each with fill and outline. Building shadows are style settings. The first matching rule wins. With no match, a
  blocking footprint draws as `Obstacle` and a non-blocking one is `Hidden`, so a new blocking prop is visible before
  any style edit.
- **Icon rules:** a match over footprints or markers (all-of tags) to an icon key, a priority and a merge radius in
  screen pixels.
- **Label rules:** a marker tag that makes a marker a label, plus tier tags. The label key is derived from the marker
  name, and the game resolves it to text.

## Rasterizer and chunk cache

`WorldMapRasterizer.Rasterize(source, style, chunk)` returns a `WorldMapChunkImage` with `Coord`, `Size`, `Rgba` and
the `Revision` captured before sampling. Per chunk:

1. Sample the chunk with the surface reach, two source cells plus the shoreline width converted to metres. Collect
   footprints with the footprint reach, the building shadow offset length, half the wall width and one raster pixel
   converted to metres. Both reaches are defined once in `WorldMapRasterReach` and preserve chunk-border continuity.
2. Resample ground colours and heights with bilinear blending between cell centres, dropping void contributions and
   renormalizing the remaining weights. Pixels whose nearest cell is void keep `VoidColor`.
3. Apply hillshade to dry ground from the height gradient, with extra darkening above the steepness threshold.
4. Tint water by depth between the shallow and deep colours, then draw the shoreline.
5. Draw all building shadows first, then footprints in fixed symbol order, bridges, obstacles, walls, buildings and
   trees, ordered by ascending footprint `Id` within each kind. Buildings and bridges draw oriented rectangles with
   the configured outline, one pixel by default. Trees and obstacles draw inscribed ellipses. Walls follow the longer
   local axis with the configured width and no outline. Shapes are clipped to the chunk and use fixed four by four
   subsampling with disjoint fill and outline coverage.

The same source data, revision and style produce identical bytes on one machine. Colour math uses the stored channel
values directly, matching `Render2D` UI texture uploads without sRGB decoding. Cross-platform goldens allow two byte
values per channel for floating-point differences.

`WorldMapChunkCache` holds generation-tagged `WorldMapChunk` results containing `Image`, `Features` and `Generation`.
`Invalidate(area)` widens the touched area by the sum of the surface and footprint reaches from `WorldMapRasterReach`,
marks previously requested chunks stale and clears failed generations for retry. `TryGet(chunk, out built)` retains
the last successful result during a rebuild. `BuildNext()` attempts the lowest-priority pending chunk outside the
bookkeeping lock. A source failure quarantines that generation without stopping `WorldMapChunkWorker`.

`WorldMapFeatureCollector.Collect(source, style, chunk)` collects icon and label anchors owned by the chunk containing
the footprint centre or marker position. Half-open ownership prevents duplicates at chunk borders. Anchors share the
imagery's cache generation and invalidation, so they scale with world size like the imagery.

## Map view

`WorldMapView` in `KhaozEngine.WorldMap.Gui` composes `PannableCanvas`.

- **Navigation:** drag pans, the wheel zooms around the cursor, and pan is clamped so the world cannot leave the
  viewport. Zoom runs from whole-world fit to `MaxPixelsPerMetre`, 8 screen pixels per metre by default, independently
  of raster resolution. `CenterOn(mapPosition)`, `FitWorld()` and `ZoomBy(factor)` back the game's buttons. The first
  usable viewport fits the world unless explicit positioning came first.
- **Streaming:** only chunks that intersect the view are requested, nearest to the view centre first. One background
  worker rasterizes, and finished chunks upload on the frame thread under a per-frame budget (default 4). A chunk not
  yet ready draws in a placeholder colour. A stale chunk keeps drawing its old texture until its replacement uploads.
  Textures are created through an internal factory over `Render2DSurface.CreateTexture`, which the tests replace with a
  GPU-free fake. The view disposes every texture it created.
- **Drawing:** `Draw(batch)` uses the caller's already-open UI batch, without beginning, ending or changing its scissor.
  `Viewport`, the pointer and the batch use the same coordinates. Chunk quads and UVs clip to the viewport.
- **Overlays:** drawn at fixed screen size in this order: labels, icons, then live markers. `IconSprites` resolves borrowed
  `WorldMapSprite` values, `LabelText` resolves localized text, and `LabelFont` borrows the game's `SpriteFont`.
  Null sprites or text hide that overlay, and no font hides labels. Decluttering is a per-frame greedy pass in screen
  space: icons with the same key inside the merge radius collapse into one, a
  higher-priority icon hides a lower one it overlaps, and higher-tier labels win collisions. Tiers also set the zoom
  range in which a label shows.
- **Live markers:** `SetMarker(id, mapPosition, heading, sprite)` and `RemoveMarker(id)`. A minimap is a second view
  over the same chunk cache and marker calls.
- **Hover:** `Hovered` reports the icon or label under the pointer so the game can show a localized tooltip.
- **Input:** `Update(pointer, wheelDelta)` takes the pointer and wheel delta the game passes, like every engine widget.
  It blocks pointer input from reaching the world behind it. Nothing reads raw device input.
- **Lifetime:** create, update, draw and dispose views on the surface's frame thread. Dispose views, then the worker,
  then the cache before releasing source data or the surface. Icon and marker textures and the label font remain
  caller-owned. A worker and cache can serve several views, each with its own uploads and budget.

## Testing

All proofs are CPU tests run synchronously through the build slot. Headless view tests replace texture creation and
disposal with fakes. The wrapper reuses the existing Render2D upload and draw APIs and adds no backend implementation.

- **Rasterizer, with a small synthetic source:** ground colour at cell centres, smooth blending between cells, water
  depth tint and shoreline, a building footprint rotated 90 degrees, tree ellipses, `Hidden` footprints absent, the
  blocking fallback, symbol draw order, chunk borders identical to one large raster pixel for pixel, a determinism
  digest, invalidation marking only touched chunks plus the margin, and one reference PNG of the fixture.
- **TileWorld adapter, with a fixture document:** an object at greater tile z draws higher on the map and one at greater
  tile x draws further right, rotated footprints, the roof and blocking flags, water bodies and depth, markers with
  tags, plane filtering, and `ToMap` agreeing with `TileWorldSpace`.
- **View, with the fake texture factory:** map, screen and world conversions, cursor-anchored zoom, zoom and pan clamps,
  the upload budget, placeholder then texture, stale-texture replacement, icon merge and priority, label tier
  collisions, hover hits and marker placement.

Release build, the full suite once at the round finish, format, guards and hosted CI gate the merge to engine main.

## Documentation and release

- Root `README.md` rows for the three packages, a README in each package, and a `docs/USING-KHAOZENGINE.md` section
  that replaces the `MinimapComponent` sample with the real world map flow.
- `CHANGELOG.md` bullets under the staged 20.33.0.
- The owner tags 20.33.0 from a KhaozEngine-rooted thread.

## Follow-ups

- **Filed with this round:** the MapDoc adapter, https://github.com/APKiwiOrg/KhaozEngine/issues/1370, linked to https://github.com/APKiwiOrg/Grimhollow/issues/441.
- **Possible later, not filed:** a minimap view, levels and caves, and a `render_map` verb in `ke-tileedit` and
  `ke-mapedit` that writes the same imagery to a PNG.
