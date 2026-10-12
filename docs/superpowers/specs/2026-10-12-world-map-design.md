# World map: design

- **Date:** 2026-10-12
- **Status:** design approved in conversation, written spec awaiting owner review
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
converts from its world format into map space and back. The TileWorld adapter converts through `TileWorldSpace`, which
already negates z once so that a top-down view has north up and east right (TILE-WORLD-DESIGN 7.4). This is the one
place handedness is handled. Every other package works in map space only.

`MapRect` is an axis-aligned rectangle in map space. A chunk is a square of `ChunkMetres` aligned to the map-space
origin and addressed by integer `(cx, cy)`.

## Source contract

```csharp
public interface IWorldMapSource
{
    MapRect Bounds { get; }
    long Revision { get; }
    event Action<MapRect>? Changed;

    WorldMapSurfaceGrid SampleSurface(MapRect area);
    void CollectFootprints(MapRect area, List<WorldMapFootprint> into);
    void CollectMarkers(MapRect area, List<WorldMapMarker> into);

    Vector2 ToMap(Vector3 worldPosition);
    float ToMapHeading(float worldYaw);
}
```

- **`SampleSurface`** returns the source's own surface grid over an area: origin, cell size, width and height, and per
  cell a ground colour, a surface height and a water surface height (NaN where dry). Samples sit at cell centres. The
  source picks its native spacing (one metre for TileWorld, whatever the terrain resolves to for MapDoc). The
  rasterizer resamples it, so the source never knows about pixels.
- **`CollectFootprints`** yields placed objects that touch the area: id, archetype key, an oriented shape (centre, half
  extents and rotation, or a polygon), top height, level, tags, `IsRoof` and `Blocking`.
- **`CollectMarkers`** yields named markers that touch the area: name, map position, level and tags.
- **`Revision` and `Changed`** carry dynamism. A source whose data changes raises `Changed` with the touched area on the
  thread that owns the data and increments `Revision`. A static source never raises it.
- **Thread safety:** the rasterizer reads a source from a worker thread. A source must allow concurrent reads. It may
  change only on its owner thread, and results computed against an older revision are discarded by the cache.
- **Levels:** this round maps the surface. The TileWorld adapter yields plane 0 terrain, plane 0 objects and roofs on
  any plane. A later level parameter selects caves or upper floors without changing the rest of the contract.
- **`ToMap` and `ToMapHeading`** let a game place live markers (the player arrow) from world poses.

### TileWorld adapter

`TileWorldMapSource` wraps a `TileWorldDocument` and its `TileWorldCatalogs`.

- One surface cell per tile. Colour is the underlay material colour, with the overlay material colour over it where an
  overlay is set. Height is the mean of the four corner heights. A tile whose underlay material has
  `GroundMaterialKind.Water` reports the water surface of the body that covers it.
- Footprints come from `TileObject` plus its archetype: `TileFootprint` gives the rotated size, `IsRoof` and
  `CollisionKind` give the roof and blocking flags, and archetype tags pass through unchanged.
- Markers come from `TileMarker` with their tags.
- Bounds are the union of loaded region extents.
- The document is static in a game client today, so the adapter raises no `Changed`. An editing host can raise it from
  its edit events later. The contract does not change for that.

## Style

`WorldMapStyle` is an immutable description owned by the game, with neutral engine defaults.

- **Raster:** `PixelsPerMetre` (default 4) and `ChunkMetres` (default 64), so a default chunk is 256 by 256 pixels.
- **Relief:** light from the north-west at 45 degrees altitude, a strength that keeps ground colours readable, and a
  steepness threshold above which slopes shade markedly darker so cliffs read as edges.
- **Water:** a shallow colour, a deep colour, the depth at which water is fully deep, and a shoreline colour and width.
- **Symbol rules:** an ordered list of rules from a footprint match (archetype key, all-of tags, roof flag, blocking
  flag) to a symbol. Symbols are `Hidden`, `Obstacle`, `Wall`, `Building`, `Tree` and `Bridge`, each with fill, outline
  and optional shadow. The first matching rule wins. With no match, a blocking footprint draws as `Obstacle` and a
  non-blocking one is `Hidden`, so a new blocking prop is visible before any style edit.
- **Icon rules:** a match over footprints or markers (all-of tags) to an icon key, a priority and a merge radius in
  screen pixels.
- **Label rules:** a marker tag that makes a marker a label, plus tier tags. The label key is derived from the marker
  name, and the game resolves it to text.

## Rasterizer and chunk cache

`WorldMapRasterizer.Rasterize(source, style, chunk)` is pure and returns a `WorldMapChunk` with its coordinate, size,
RGBA bytes and the source revision it was drawn from. Per chunk:

1. Sample the surface over the chunk plus a one-cell margin, so blending and shading match across chunk borders.
2. Resample ground colour with bilinear blending between cell centres, so one-metre tiles do not read as a checkerboard
   at full zoom.
3. Apply hillshade from the height gradient, with extra darkening above the steepness threshold.
4. Tint water by depth between the shallow and deep colours, then draw the shoreline.
5. Draw footprints in fixed symbol order: bridges, obstacles, walls, buildings, trees. Buildings draw their rotated roof
   footprint with a one-pixel outline and a short shadow toward the south-east. Trees draw a canopy disc sized to the
   footprint. Shapes are clipped to the chunk and edge pixels use a fixed supersampling pattern, so rotated outlines are
   clean and the output stays deterministic.

The same source revision and style always produce identical bytes. Colour math follows the texture colour convention
`Render2D` uses for UI textures, confirmed in the implementation plan.

`WorldMapChunkCache` holds chunks by coordinate and revision. `Invalidate(MapRect)` marks every chunk the area
touches, widened by the sampling margin, as stale. Feature anchors (icons and labels) are collected per chunk with the
same keys and invalidation, so they scale with world size like the imagery.

## Map view

`WorldMapView` in `KhaozEngine.WorldMap.Gui` composes `PannableCanvas`.

- **Navigation:** drag pans, the wheel zooms around the cursor, and pan is clamped so the world cannot leave the
  viewport. Zoom runs from whole-world fit to twice the raster resolution, 8 screen pixels per metre by default.
  `CenterOn`, `Focus` and zoom-step calls back the game's buttons.
- **Streaming:** only chunks that intersect the view are requested, nearest to the view centre first. One background
  worker rasterizes, and finished chunks upload on the frame thread under a per-frame budget (default 4). A chunk not
  yet ready draws in a placeholder colour. A stale chunk keeps drawing its old texture until its replacement uploads.
  Textures are created through an internal factory over `Render2DSurface.CreateTexture`, which the tests replace with a
  GPU-free fake. The view disposes every texture it created.
- **Overlays:** drawn at fixed screen size in this order: labels, icons, then live markers. Icon sprites, label text and
  the label font come from the game through callbacks, so the engine holds no game strings or art. Decluttering is a
  per-frame greedy pass in screen space: icons with the same key inside the merge radius collapse into one, a
  higher-priority icon hides a lower one it overlaps, and higher-tier labels win collisions. Tiers also set the zoom
  range in which a label shows.
- **Live markers:** `SetMarker(id, mapPosition, heading, sprite)` and `RemoveMarker(id)`. A minimap is a second view
  over the same chunk cache and marker calls.
- **Hover:** `Hovered` reports the icon or label under the pointer so the game can show a localized tooltip.
- **Input:** `Update(Pointer, wheelDelta)` takes the pointer and wheel delta the game passes, like every engine widget.
  It blocks pointer input from reaching the world behind it. Nothing reads raw device input.

## Testing

All proofs are CPU tests run synchronously through the build slot. No GPU backend run is needed because no GPU code is
added.

- **Rasterizer, with a small synthetic source:** ground colour at cell centres, smooth blending between cells, water
  depth tint and shoreline, a building footprint rotated 90 degrees, tree discs, `Hidden` footprints absent, the
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

- **Filed with this round:** the MapDoc adapter, linked to Grimhollow #441.
- **Possible later, not filed:** a minimap view, levels and caves, and a `render_map` verb in `ke-tileedit` and
  `ke-mapedit` that writes the same imagery to a PNG.
