# KhaozEngine.WorldMap.Gui

The opt-in world map widget over `WorldMapChunkCache` and `PannableCanvas`.
References only `KhaozEngine.WorldMap` and `KhaozEngine.Gui` and stays outside every umbrella.

`WorldMapSymbol.SizeMetres` changes raster geometry only. Icon and label anchors, fixed screen sizes
and hover bounds continue to come from the feature rules and overlay layout.

## Use in an existing UI

```csharp
using var cache = new WorldMapChunkCache(source, new WorldMapStyle());
using var worker = new WorldMapChunkWorker(cache);
using var view = new WorldMapView(cache, surface);

// Each frame, after updating the pointer in the same coordinates as the UI batch.
view.Viewport = new Rect(100, 50, 400, 300);
view.Update(pointer, wheelDelta);

// Inside the game's already-open UI SpriteBatch.
view.Draw(batch);
```

Create, update, draw and dispose the view on the surface's frame thread. The view owns its chunk
textures and one white pixel used for placeholders. The caller owns the cache, worker and surface.
One worker can build for a cache shared by several views. Each view owns its own uploads and budget.
Dispose the views before their surface, and stop the worker before disposing the cache or source.

## Offscreen capture

Pass the callback's `Render2DContext` to draw the same widget through `Render2DSnapshot`:

```csharp
byte[] rgba = Render2DSnapshot.Capture(width, height, Color.Black, context =>
{
    using var view = new WorldMapView(cache, context);
    view.Viewport = new Rect(0, 0, width, height);
    view.Update(pointer, 0f);
    context.Batch.Begin();
    view.Draw(context.Batch);
    context.Batch.End();
});
```

Use a pointer in the capture's pixel coordinates. Chunk requests and upload budgets work as in the
surface view. Prepare the shared cache with a worker or `BuildNext` before the final update when the
capture needs completed imagery.

The context creates and owns every uploaded chunk texture and the white placeholder pixel. Replacement,
eviction and view disposal release the view's chunk references without disposing those textures. The
capture retains them until recorded commands finish submitting, then frees them through its normal
teardown. `MaxTextures` limits view retention, while the capture retains all textures it created until
teardown. Create and use the context view inside its capture callback. Icons, markers and label fonts
remain borrowed in both modes.

## Drawing

`Draw` uses the caller's open batch in `Viewport` coordinates. It does not open or close a batch or
change its scissor. Chunk destination rectangles are clipped to the viewport on the CPU, and their
source UV endpoints shrink by the same fractions. Quads also clip to the authored world bounds, including
letterboxed small worlds. The caller can keep drawing other UI in that batch.

## Navigation

Map x runs east and map y runs north, in metres. Canvas coordinates are `(mapX, -mapY)`, so north is
up and east is right. `MapToScreen` and `ScreenToMap` include the viewport offset.

- The first update with a usable viewport fits and centres the world. Calling `CenterOn` or `ZoomBy`
  before that update preserves explicit positioning. Before automatic fitting, zoom starts at the
  cache style's raster resolution, clamped to the maximum.
- Drag pans, and the wheel zooms toward the cursor. Navigation composes the legacy
  `PannableCanvas.Update(Pointer, wheelDelta)` path and clamps the view to the source bounds.
  `Update` reserves the viewport through `Pointer.BlockRegion`. This wrapper does not expose the
  new full-frame canvas gesture path. See the [consumer contract](../docs/USING-KHAOZENGINE.md#pan-and-zoom)
  for direct canvas use with native gestures and accepted taps.
- `CenterOn(mapPosition)` keeps zoom. `ZoomBy(factor)` multiplies zoom about the viewport centre and
  re-clamps. The factor must be finite and positive. `FitWorld()` restores whole-world fitting.
- `PixelsPerMetre` is the current screen zoom. Its minimum is
  `Min(Viewport.Width / Bounds.Width, Viewport.Height / Bounds.Height)`, capped at
  `MaxPixelsPerMetre`. The maximum defaults to 8 and must be finite and positive.
- An update with either viewport dimension below one pixel does nothing. Automatic fitting waits
  for a usable viewport. Changes to the viewport or source bounds are applied on the next update.

## Streaming and uploads

`CurrentLevel` reports the pyramid level chosen by the last update with a usable viewport. The view
chooses the largest level up to `WorldMapChunking.MaxLevel` whose
`EffectivePixelsPerMetre / 2^level` is at least the screen's `PixelsPerMetre`. At or above the native
raster scale it chooses level zero. Coarser chunks keep the same raster dimensions and cover more metres.

Each update requests only chunks of `CurrentLevel` intersecting both the visible map area and source
bounds, with distance from the chunk centre to the view centre as the build priority. Completed visible
chunks upload nearest first on the frame thread. Ready coarser ancestors can use the remaining budget
for fallback imagery, without additional requests. `UploadBudgetPerFrame` defaults to 4. Zero pauses
uploads, and negative budgets are refused. Rasterization stays with `WorldMapChunkCache` and its worker.

Each upload records the cache's `long` generation. Cache-wide monotonically increasing generations
ensure that re-requesting an evicted chunk replaces any surviving upload. An unchanged generation
never uploads again. A stale chunk keeps its old texture until a newly built generation uploads,
including while the upload budget is exhausted. Replacing a surface-owned texture disposes the previous
one. A snapshot view leaves disposal to its capture.

`MaxTextures` defaults to 192 and rejects values below one with `ArgumentOutOfRangeException`. After
building the draw list, `Update` evicts the least recently drawn texture that is absent from this frame's
draws until retention reaches the cap. Textures used for fallback count as drawn. If active draws alone
exceed the cap, they remain protected until a later update can evict them. Setting the cap does not dispose
textures until `Update`. Default retained chunk imagery is 48 MiB per view, for 192 textures of
256 by 256 RGBA pixels, excluding GPU overhead and any temporary active-draw excess. Every remaining
surface-owned texture is disposed with the view. Snapshot textures remain capture-owned through eviction
and view disposal. Eviction and disposal stay on the rendering thread.

A chunk with no uploaded texture samples the matching north-up UV sub-rectangle of its nearest uploaded
coarser ancestor. With no uploaded ancestor it draws a filled rectangle in `PlaceholderColor`, which
defaults to `cache.Style.VoidColor`. Uploaded chunks draw with `Color.White` to preserve their authored colours.

The internal `WorldMapViewCore<TTexture>` separates navigation, requests, uploads and clipped draw
items from the GPU wrapper. Its texture factory and disposal callback allow headless lifecycle and
layout tests. Overlay layout uses icon sizes and text measurements, with no textures or fonts in the
core's overlay pass.

The core retains overlay candidates, sorting scratch, accepted rectangles and placement views across
updates. Marker identities stay in ordinal draw order as markers are added and removed. With unchanged
visible features, allocation-free callbacks and the same hover target, the warmed overlay pass allocates
no working storage. Warmed level selection, visible traversal and fallback sampling also allocate no frame
storage when chunks and uploads are unchanged.

## Icons, labels, markers and hover

Set `IconSprites` to resolve each cached icon key to a `WorldMapSprite`. A null callback or result hides
that icon. `WorldMapSprite.Size` is in screen pixels and stays fixed as the map zooms. Its source UVs
are `(left, top, right, bottom)`, and its texture remains caller-owned. The view resolves sizes during
`Update` and resolves the accepted sprites again during `Draw`.

Set `LabelText` to resolve an authored marker name to localized text and set `LabelFont` to a borrowed
`SpriteFont`. A null text result or absent font hides the label. Labels centre on their map anchors,
with dimensions `LabelFont.Measure(text) * tier.TextScale`. A tier shows at its inclusive minimum zoom
and hides at its exclusive maximum. Text defaults to `#2b2117`, with a one-pixel shadow in `#f3ead2`.
Change these colours through `LabelColor` and `LabelShadowColor`.

Icons are selected by descending priority, ordinal key, then screen x and y. Same-key candidates
within their screen-pixel merge radius collapse, and overlapping icons lose to an already accepted
icon. Labels select descending tier rank, ordinal marker name, then screen x and y, and declutter
against other labels. Icon and label rectangles must fit fully inside the viewport. Touching edges
are allowed. Only visible built chunks of `CurrentLevel` supply anchors, even when imagery uses a coarser
fallback. Cached anchors can appear before their chunk's texture uploads.

`SetMarker(id, mapPosition, heading, sprite)` adds or updates a live marker. Call it before `Update`
to place its latest pose. Its artwork points north at zero heading, with heading in clockwise radians.
Markers stay fixed in screen size and skip only when their centre is outside the viewport. They draw
in ordinal ID order and do not participate in decluttering or hover. `RemoveMarker(id)` returns
whether the identity was present. The view never disposes marker or icon textures or the label font.

After the chunk list, `Draw` emits label shadows and text, then icons, then live markers in the same
caller-open batch. `Hovered` is refreshed by `Update`, returning the last-drawn accepted icon under
the pointer, otherwise the last label, otherwise null. Its key is the icon key or authored marker name,
and its map position is the original anchor. An unusable viewport clears overlays and hover. Since 21.3.0
an occluded pointer position (`Pointer.IsOccluded`) reports no hover while overlays still place.
