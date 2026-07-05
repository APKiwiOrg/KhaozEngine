# KhaozEngine.Render2D

2D rendering on the custom MonoGame-free foundation (engine-owned `KhaozEngine.Gpu` abstraction, `System.Numerics`).

- `SpriteBatch` - batched textured quads, alpha blend + tint, submission-ordered runs. `Begin` overloads for
  screen / `Camera2D` (world) / `IDesignViewport` (design) space, each with an optional `SamplerMode`
  (`Linear` / `Point`) and an optional `Matrix4x4` model transform (tilt/scale a composed group as one).
  `SetScissor`/`ClearScissor` DPI-aware clipping.
- `Camera2D` - position/zoom/rotation 2D camera (headless, unit-tested) + the camera-feel layer (follow,
  look-ahead, eased blends, room cameras, parallax).
- `Texture2D` - GPU texture; PNG load via StbImageSharp.
- `ImageRgba` - CPU-side RGBA8 image (no GPU): `Load`/`Decode`, `AlphaAt`/`IsOpaqueAt` for opaque-pixel masks.
- `SpriteFont` - runtime TrueType text (stb_truetype glyph atlas), `DrawString` / `Measure`. `DrawString` has a
  `float scale` overload (uniform scale about the top-left); `TextLayout.AlignedX`/`DrawAligned`/`DrawWrapped`
  take an optional `scale` so aligned/wrapped text stays correct when drawn scaled (`scale = 1` is unchanged).
- `PrimitiveRenderer` - filled/outlined 2D primitives through a `SpriteBatch` (owns a 1x1 white pixel):
  rects, lines, circles/rings, filled circles, vertical gradients, progress bars, filled sectors/arc-bands, and
  partial-ring strokes `DrawArc` (a general arc outline) / `DrawRadialProgress` (a 0..1 countdown/cooldown ring).
- `Render2DSurface(AppWindow)` - draw into a `KhaozEngine.Windowing` window; texture/font/`ImageRgba` loaders;
  `CaptureToTexture` / `CaptureToRgba` offscreen capture; `Render2DSnapshot` captures headless.

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

Draws a vertical gradient across a `Rect` by rendering `bands` horizontal strips with linearly interpolated
color from `top` to `bottom`. Useful for atmosphere gradients, panel scrims, and background layers.

```csharp
void DrawVerticalGradient(SpriteBatch batch, Rect r, Color top, Color bottom, int bands = 12)
```

```csharp
renderer.DrawVerticalGradient(spriteBatch, new Rect(0, 0, viewWidth, viewHeight), fogTop, fogBottom, 12);
```
