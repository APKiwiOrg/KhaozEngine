# KhaozEngine.Render2D

2D rendering on the custom MonoGame-free foundation (engine-owned `KhaozEngine.Gpu` abstraction, `System.Numerics`).

- `SpriteBatch` - batched textured quads, alpha blend + tint, submission-ordered runs (consecutive same
  -texture draws coalesce into one draw call, and interleaved textures split into separate runs so painter's order
  across textures is preserved). `Begin` overloads for screen / `Camera2D` (world) / `IDesignViewport` (design)
  space, each with an optional `SamplerMode` (`Linear` / `Point`) and an optional `Matrix4x4` model transform
  (tilt/scale a composed group as one). `SetScissor`/`ClearScissor` DPI-aware clipping, which NESTS: an inner
  clip is intersected with whatever is already active and its `ClearScissor` restores that outer region rather
  than the whole framebuffer (`ScissorDepth` reports how many are active). `GroupByTexture` (opt-in,
  off by default, reset by every `Begin`) groups queued quads by texture at flush regardless of submission
  order, trading strict painter's order for fewer draw calls when interleaved same-texture draws would
  otherwise split into separate runs - order stays intact WITHIN a texture group, so only enable it for a pass
  whose correctness does not depend on cross-texture draw order. Quad corners are emitted in the batch's
  authoring space and transformed to clip space by the vertex shader (the `Begin`'s clip-corrected
  view-projection rides in a per-`Begin` uniform buffer), so there is no per-corner CPU `Vector4.Transform` -
  the transform is a single GPU multiply per vertex instead of four CPU transforms per quad. `FrameStats`
  exposes always-on per-frame draw counters (quads, draw calls, flushes, texture switches, vertex-upload bytes
  as a `Primitives.RenderFrameStats`), reset each `NewFrame` and read after the frame's draws. The batch caches one
  resource set per `(texture, sampler)` and evicts the ones unused for 600 frames, so a game that streams sprites
  does not accumulate one set per texture ever drawn. **That sweep never stalls the frame thread** (since 17.37.0):
  evicted sets, and the buffers a grow replaces, go to a `Gpu.GpuRetireQueue` and are destroyed a few frame
  boundaries later instead of behind the `WaitForIdle` the eviction path used to take every time anything aged out
  ([#84](https://github.com/APKiwiOrg/KhaozEngine/issues/84)). Eligibility is unchanged, only the disposal moved.
- `Camera2D` - position/zoom/rotation 2D camera (headless, unit-tested) + the camera-feel layer (follow,
  look-ahead, eased blends, room cameras, parallax). `ScreenToWorld` cannot return NaN: a `Zoom` of exactly 0
  collapses the view matrix, which then has no inverse, so the conversion falls back to `Position` (the world
  point the whole viewport collapsed onto). `TryScreenToWorld(screen, w, h, out world)` is the same conversion
  with a bool for a caller that wants to detect that case
  ([#88](https://github.com/APKiwiOrg/KhaozEngine/issues/88)). A negative zoom is a mirror rather than a
  degeneracy and still converts exactly.
- `Texture2D` - GPU texture. PNG load via StbImageSharp. Dispose drains the device (`WaitForIdle`) before
  freeing the handle when the texture carries a device reference (every engine loader: `LoadTexture`,
  `RenderToTexture`, `SpriteFont`'s atlas), since a queued upload may still reference it. A texture obtained
  via the public `Wrap(...)` factory has no device to hand it, so it disposes immediately.
- `ImageRgba` - CPU-side RGBA8 image (no GPU): `Load`/`Decode`, `AlphaAt`/`IsOpaqueAt` for opaque-pixel masks.
- `SpriteFont` - runtime TrueType text (stb_truetype glyph atlas), `DrawString` / `Measure`. `Measure` has a
  `ReadOnlySpan<char>` overload alongside the `string` one (declared on `ITextMeasurer` as a default interface
  method, so any existing measurer implementation keeps compiling unchanged) - `SpriteFont`'s override measures
  the span directly with no intermediate string allocation, for a caller (e.g. word-wrap) measuring a candidate
  that may be thrown away. `DrawString` has a `float scale` overload (uniform scale about the top-left).
  `TextLayout.AlignedX`/`DrawAligned`/`DrawWrapped` take an optional `scale` so aligned/wrapped text stays
  correct when drawn scaled (`scale = 1` is unchanged). `TextLayout.Wrap(font, text, maxWidth, hardBreak)`
  word-wraps on spaces and is memoized (a bounded LRU cache PER MEASURER, keyed on text + maxWidth + hardBreak +
  mode), so a caller re-wrapping the same unchanged text every frame (a static label, an idle tooltip)
  hits the cache instead of re-running the wrap algorithm, and the returned list is always a fresh copy, so mutating
  it can never corrupt the cache. The cache hangs off the measurer weakly (a `ConditionalWeakTable`), so a font
  nobody references any more is collected together with its entries instead of being pinned, glyph table and all,
  in a process-static dictionary until 256 later wraps age it out
  ([#767](https://github.com/APKiwiOrg/KhaozEngine/issues/767)). Concurrent callers are safe, including off the render thread: the wrap runs
  outside the cache lock, and two callers that miss on the same key both compute it, then the second to finish
  adopts the first's entry rather than inserting a duplicate that would orphan a node in the LRU list
  ([#87](https://github.com/APKiwiOrg/KhaozEngine/issues/87)). The opt-in `hardBreak` (default off) additionally slices a single token longer
  than `maxWidth` at character boundaries so every returned line fits. The opt-in `preserveSpaceRuns` (14.9.0,
  default off, default path bit-identical) keeps interior space runs verbatim instead of collapsing each to one
  space: a run stays ONE break opportunity but is re-emitted intact when no break is taken there (a break taken at
  the run still consumes it), for wrapping user-authored content (chat) without silently rewriting it. The memo key
  includes the mode, and `DrawWrapped` / `MeasureWrappedHeight` forward it. A `\n` in the text is an explicit line
  break in either mode (`\r\n` counts as one break, a lone `\r` is not a break), so N breaks give N+1 lines before
  the width has any say: consecutive breaks keep their empty lines, a text ending on a break keeps its empty last
  line, and the whitespace touching a break is consumed with it
  ([#82](https://github.com/APKiwiOrg/KhaozEngine/issues/82)). Default baked coverage is
  U+0020..U+017F (printable ASCII + Latin-1 Supplement + Latin Extended-A), so accented Western/Central European
  text renders out of the box. Anything outside the coverage (or missing from the face) measures AND draws as
  the visible `SpriteFont.FallbackChar` glyph (`?`) instead of silently dropping. Control characters stay
  zero-width.
- `PrimitiveRenderer` - filled/outlined 2D primitives through a `SpriteBatch` (owns a 1x1 white pixel):
  rects, lines, circles/rings, filled circles, vertical gradients, progress bars, filled sectors/arc-bands, and
  partial-ring strokes `DrawArc` (a general arc outline) / `DrawRadialProgress` (a 0..1 countdown/cooldown ring).
- `Render2DSurface(AppWindow)` - draw into a `KhaozEngine.Windowing` window; texture/font/`ImageRgba` loaders;
  `CaptureToTexture` / `CaptureToRgba` offscreen capture, and `Render2DSnapshot` captures headless. Both captures
  open, submit and drain a command list of their own, so they are NOT mid-frame calls: taken while the frame's
  list is recording they throw `GpuNestedRecordingException` naming the fix rather than corrupting the frame
  (the seam's one-open-recording-per-device rule, see `KhaozEngine.Gpu`). Capture from the frame's pre-record
  phase or outside the loop. `Render2DSnapshot.Capture`'s callback creates and forgets: every texture and font
  it makes through its `Render2DContext` is owned by the capture, which frees them once the submit has drained.
  The callback still must not dispose them itself, because the recorded command list names them until that
  submit.

The GPU backend stays behind `KhaozEngine.Gpu`; this package has no direct graphics-backend reference (deps:
`KhaozEngine.Gpu` + `KhaozEngine.Windowing` + StbTrueTypeSharp/StbImageSharp). Windowing/input come from
`KhaozEngine.Windowing` (`AppWindow`, Silk.NET windowing, GLFW natives bundled per-RID - no SDL2/brew). Part of
the MonoGame-free engine.

## `TextHelper` - pixel-snapped UI text

**Never call `SpriteBatch.DrawString` directly for UI text.** Use `TextHelper`: it floors every draw position
to integer pixels, so bitmap-font glyphs land on texel boundaries and stay crisp instead of blurring at
sub-pixel offsets. Static class, no instance. Colors are `KhaozEngine.Primitives.Color`, and the `alpha`
overloads modulate the color's alpha by an extra factor (fades).

| Method | Use for |
|--------|---------|
| `Draw(sb, font, text, x, y, color)` | Top-left at (x, y). `x`/`y` may be float, and are floored. |
| `Draw(sb, font, text, x, y, color, alpha)` | Same, alpha-modulated. |
| `DrawCentered(sb, font, text, centerX, y, color)` | Horizontally centered on `centerX`. |
| `DrawCentered(sb, font, text, centerX, y, color, alpha)` | Centered, alpha-modulated. |
| `DrawRight(sb, font, text, rightX, y, color)` | Right edge lands on `rightX`. |
| `DrawRight(sb, font, text, rightX, y, color, alpha)` | Right-aligned, alpha-modulated. |
| `DrawCenteredInRect(sb, font, text, rect, color)` | Centered horizontally AND vertically in a `Rect`. |
| `DrawCenteredInRect(sb, font, text, rect, color, alpha)` | Same, alpha-modulated. |
| `DrawWrappedCentered(sb, font, text, centerX, y, maxWidth, color, alpha)` | Word-wraps to `maxWidth`, each line centered on `centerX`, returns total height drawn. |

Pure positioning helpers (`CenteredX`, `RightX`, `CenteredInRect`, `MeasureWrappedHeight`) over
`ITextMeasurer` are exposed for headless layout math. Complements `TextLayout` (align/wrap within a width
region). This is the point-anchored API.

```csharp
TextHelper.Draw(spriteBatch, uiFont, "UPGRADES", x + 5, y + 8, Color.White);
TextHelper.DrawCentered(spriteBatch, uiFont, "HC: 1,234", vr.Width / 2, topBarY, Color.White);
TextHelper.DrawCenteredInRect(spriteBatch, uiFont, "Continue", buttonRect, Color.White, alpha);
```

## `PrimitiveRenderer.DrawVerticalGradient`

Draws a vertical gradient across a `Rect` as a single GPU-interpolated quad (`top` on the upper edge, `bottom`
on the lower edge, lerped per-pixel by the rasterizer). Useful for atmosphere gradients, panel scrims, and
background layers.

```csharp
void DrawVerticalGradient(SpriteBatch batch, Rect r, Color top, Color bottom, int bands = 12)
```

```csharp
renderer.DrawVerticalGradient(spriteBatch, new Rect(0, 0, viewWidth, viewHeight), fogTop, fogBottom, 12);
```

`bands` is unused (kept only for source/binary compatibility with existing call sites): a single GPU
-interpolated quad has no band count of its own and is smoother than any earlier CPU-banded approximation.

## `PrimitiveRenderer.DrawFilledCircle` row cap

`DrawFilledCircle` draws stacked horizontal rects, one row per pixel of diameter up to
`PrimitiveRenderer.MaxFilledCircleRows` (128) bands. Past that a very large radius steps by more than 1 pixel
per row (see `FilledCircleRowStep`) so it still draws a bounded number of (proportionally taller) bands instead
of one draw call per pixel row. A no-op change for any UI-scale circle (radius <= 63 stays exactly one row per
pixel, unchanged).

## `DpiFont` - crisp point-space text on HiDPI (since 10.12.0)

`DpiFont` is a logical-size font that stays crisp on HiDPI. It bakes its glyph atlas at the live DPI scale and
re-bakes only when that scale changes (stable per display, so not per window resize). Author at a logical
`pixelHeight` (points). Each frame call `font.For(dpiScale)` (pass `frame.DpiScale`) and draw the returned
`SpriteFont` 1:1 in a point-space pass (a `UiViewport` `Begin`). `DpiFont` is `IDisposable` (it owns the
baked `SpriteFont`s).

Each factory takes an optional `cacheSlots` (default 1). Pass `cacheSlots` &gt; 1 when the SAME face is drawn at
several different effective scales in one pass and each must be texel-exact - e.g. a boot screen title and a
smaller step label from one font: call `For(titleScale * dpiScale)` and `For(labelScale * dpiScale)` and both
atlases stay baked (LRU eviction past the slot count), instead of thrashing a single slot (baking twice every
frame). The default of 1 keeps the single-display-scale behaviour (one atlas, re-baked only on a DPI change).
`LiveCount` reports how many scales are baked right now (`BakeCount` the total (re)bakes over the font's life).

Create one via `Render2DSurface`:

| Factory | Loads from |
|---------|-----------|
| `Render2DSurface.LoadDpiFont(path, cacheSlots)` | TrueType file path |
| `Render2DSurface.LoadDpiFont(byte[], cacheSlots)` | in-memory font bytes |
| `Render2DSurface.LoadDpiFont(FontManager, key, cacheSlots)` | a font already registered in a `FontManager` |
| `Render2DSurface.LoadDefaultDpiFont(pixelHeight, cacheSlots)` | the built-in default font at `pixelHeight` |
| `Render2DContext.LoadDpiFont(byte[], pixelHeight, cacheSlots)` | the offscreen snapshot path |
| `Render2DContext.LoadDefaultDpiFont(pixelHeight, cacheSlots)` | the built-in default font, offscreen snapshot path |

```csharp
var uiFont = surface.LoadDpiFont("fonts/Inter.ttf");  // logical points
// per frame, inside a UiViewport Begin:
var font = uiFont.For(frame.DpiScale);
TextHelper.Draw(spriteBatch, font, "UPGRADES", x, y, Color.White);
```

## `SpriteFont` fractional bake density (since 10.12.0)

`SpriteFont` now bakes at a fractional bake `density`: the atlas is rasterized at `pixelHeight * density`,
`RenderScale` is set to `1/density`, and all layout metrics are still reported at the logical `pixelHeight`.
The integer `oversample` form delegates to this and is byte-identical at density 1. This is what lets a
`DpiFont` bake at an arbitrary DPI scale (e.g. 1.5) rather than a whole-integer oversample.

## `SpriteBatch` device-pixel snapping (since 10.12.0)

For DPI-aware UI, `SpriteBatch` exposes device-pixel snapping:

| Member | Meaning |
|--------|---------|
| `DeviceScale` (`Vector2`) | device pixels per authoring unit |
| `DeviceOffset` (`Vector2`) | device-pixel origin offset |
| `SnapRect(Rect)` | snaps a rect to whole device pixels |
| `SnapLength(float length, float minDevicePixels = 0)` | snaps a length to whole device pixels |

These are non-zero / active ONLY inside a point-space `UiViewport` `Begin`. A fractional design viewport,
world/camera space, screen space, or a transformed pass leaves `DeviceScale` at `Vector2.Zero`, so snapping is
a no-op there. Inside a point-space pass `SpriteBatch` also snaps each text block's origin (its ascent baseline)
to device pixels - once per `DrawString`, not per glyph - so text drawn with a `DpiFont` is crisp AND every glyph
of a word stays on one baseline (snapping each glyph independently used to wave the baseline at fractional scales).

## `SpriteBatch.DrawQuad`

`DrawQuad(tex, topLeft, topRight, bottomRight, bottomLeft, srcUV, color)` draws an arbitrary convex quad from
four corner points in the batch's authoring space, riding the same two-triangle path (and batching / z-order)
as the rotated `Draw` overload. The source UV corners `(u0,v0)`, `(u1,v0)`, `(u1,v1)`, `(u0,v1)` map to the
four corners in order. Corners need not form a rectangle, and two coincident corners are allowed (the quad
collapses to a triangle), which is how the Gui radial cooldown fan is built.

## 2D particles + ambient fields (`Vfx`)

`Particle2DSystem` is a fixed-size, zero-allocation, deterministic (seeded `XorRng`) screen-space particle pool.
`Update`/`Draw`/`ActiveCount` cost is O(live particles), not O(`Capacity`): a sparse-set live-slot index means a
large pool that only ever holds a handful of simultaneously-live particles is not scanned in full every frame.
Per particle: velocity, acceleration (gravity), drag, sway, rotation + angular velocity, size/colour lerp over
life, a per-particle `BlendMode`, and an optional trapezoid alpha envelope (see below). Two lifecycles:

- **Burst pool** - `Emit(in Particle2DEmitterConfig, origin, count)` (+ tint overload) spawns emit-and-die
  particles (a ring buffer; a full pool overwrites the oldest). `Update(dt)`, `Draw(batch, texture)` (per-
  particle blend) or `Draw(batch, texture, BlendMode)` (forced), `Clear()`, `ActiveParticles()` snapshots.
- **Ambient field** - `EmitField(in cfg, Rect region, count)` (+ `tint` / `exitMargin` overload) fills a bounds
  region with particles that RESPAWN at a fresh random in-region position when they die or leave the region
  (past `exitMargin` pixels), so a persistent field (dust, embers, snow) holds a stable population with no
  emission pop. The initial fill randomizes each particle's life so the field starts mid-envelope.
  `SetFieldTint(fieldId, tint)` recolours a live field instantly (e.g. following a depth/biome palette);
  `FieldCount` reports registered fields. Size `Capacity` to the field's `count` so it owns its pool.

`Particle2DEmitterConfig` (immutable `record struct`; derive with `with`) adds `FadeInDuration` /
`FadeOutDuration` (the fade-in / hold / fade-out alpha envelope, both default 0 = no envelope) and `SizeJitter`
(per-particle +/- size variation, default 0). All three default to today's behaviour, so existing bursts are
unchanged. `VfxRenderer` (glow / ring / beam / white-pixel textures) is the convenience entry point.

`EnergyBeam` / `BeamParams` draw the additive A-to-B beam. `BeamParams.JitterShape` picks what the sideways
displacement looks like: `BeamJitter.Wave` (the default) is the coherent sine wobble, a wavy straight line, while
`BeamJitter.Jagged` displaces every segment boundary by its own signed noise under a mid-span envelope and tilts
each quad to run between its two displaced boundaries, giving a chain-lightning / tesla bolt. In jagged mode
`JitterAmount` is the peak mid-span displacement in pixels (both endpoints stay pinned on the axis) and
`JitterSpeed` becomes the re-roll rate in whole new bolts per second, with 0 holding one still bolt.
`JitterSeed` picks which bolt, so concurrent arcs need different seeds. The bolt is a pure function of seed and
time, so the beam stays stateless and every client draws the same one. `BeamParams.ElectricArc` is the tuned
preset. Wave is byte-identical to the pre-jagged behaviour.
