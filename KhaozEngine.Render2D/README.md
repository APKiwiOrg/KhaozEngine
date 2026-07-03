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
- `SpriteFont` - runtime TrueType text (stb_truetype glyph atlas), `DrawString` / `Measure`.
- `Render2DSurface(AppWindow)` - draw into a `KhaozEngine.Windowing` window; texture/font/`ImageRgba` loaders;
  `CaptureToTexture` / `CaptureToRgba` offscreen capture; `Render2DSnapshot` captures headless.

The GPU backend stays behind `KhaozEngine.Gpu`; this package has no direct graphics-backend reference (deps:
`KhaozEngine.Gpu` + `KhaozEngine.Windowing` + StbTrueTypeSharp/StbImageSharp). Windowing/input come from
`KhaozEngine.Windowing` (`AppWindow`, Silk.NET windowing, GLFW natives bundled per-RID - no SDL2/brew). Part of
the MonoGame-free engine.
