# KhaozEngine.WorldMap.Gui

The opt-in world map widget over `WorldMapChunkCache` and `PannableCanvas`.
References only `KhaozEngine.WorldMap` and `KhaozEngine.Gui` and stays outside every umbrella.

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

`Draw` uses the caller's open batch in `Viewport` coordinates. It does not open or close a batch or
change its scissor. Chunk destination rectangles are clipped to the viewport on the CPU, and their
source UV endpoints shrink by the same fractions. The caller can keep drawing other UI in that batch.

## Navigation

Map x runs east and map y runs north, in metres. Canvas coordinates are `(mapX, -mapY)`, so north is
up and east is right. `MapToScreen` and `ScreenToMap` include the viewport offset.

- The first update with a usable viewport fits and centres the world. Calling `CenterOn` or `ZoomBy`
  before that update preserves explicit positioning. Before automatic fitting, zoom starts at the
  cache style's raster resolution, clamped to the maximum.
- Drag pans, and the wheel zooms toward the cursor. Navigation composes `PannableCanvas` and clamps
  the view to the source bounds. `Update` reserves the viewport through `Pointer.BlockRegion`.
- `CenterOn(mapPosition)` keeps zoom. `ZoomBy(factor)` multiplies zoom about the viewport centre and
  re-clamps. The factor must be finite and positive. `FitWorld()` restores whole-world fitting.
- `PixelsPerMetre` is the current screen zoom. Its minimum is
  `Min(Viewport.Width / Bounds.Width, Viewport.Height / Bounds.Height)`, capped at
  `MaxPixelsPerMetre`. The maximum defaults to 8 and must be finite and positive.
- An update with either viewport dimension below one pixel does nothing. Automatic fitting waits
  for a usable viewport. Changes to the viewport or source bounds are applied on the next update.

## Streaming and uploads

Each update requests chunks intersecting both the visible map area and source bounds, with distance
from the chunk centre to the view centre as the build priority. Completed visible chunks upload
nearest first on the frame thread. `UploadBudgetPerFrame` defaults to 4. Zero pauses uploads, and
negative budgets are refused. Rasterization stays with `WorldMapChunkCache` and its worker.

Each upload records the cache generation. An unchanged generation never uploads again. A stale chunk
keeps its old texture until a newly built generation uploads, including while the upload budget is
exhausted. Replacing a texture disposes the previous one. Uploaded textures remain available after
panning away and are all disposed with the view.

A chunk with no uploaded texture draws a filled rectangle in `PlaceholderColor`, which defaults to
`cache.Style.VoidColor`. Uploaded chunks draw with `Color.White` to preserve their authored colours.

The internal `WorldMapViewCore<TTexture>` separates navigation, requests, uploads and clipped draw
items from the GPU wrapper. Its texture factory and disposal callback allow headless lifecycle and
layout tests. Icons, labels, live markers and hover are the next layer of the world-map round.
