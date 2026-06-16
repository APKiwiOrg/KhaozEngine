# KhaozEngine.Render2D (experimental)

2D rendering on the custom MonoGame-free foundation (engine-owned `KhaozEngine.Gpu` abstraction, `System.Numerics`).

- `SpriteBatch` — batched textured quads, alpha blend + tint, per-texture batching.
- `Camera2D` — position/zoom/rotation 2D camera (headless, unit-tested).
- `Texture2D` — GPU texture; PNG load via StbImageSharp.
- `SpriteFont` — runtime TrueType text (stb_truetype glyph atlas), `DrawString` / `Measure`.
- `Render2DSurface(AppWindow)` — draw into a `KhaozEngine.Windowing` window; `Render2DSnapshot` captures headless.

The GPU backend stays behind `KhaozEngine.Gpu`; this package has no direct graphics-backend reference (deps:
`KhaozEngine.Gpu` + `KhaozEngine.Windowing` + StbTrueTypeSharp/StbImageSharp). Windowing/input come from
`KhaozEngine.Windowing` (`AppWindow`). Part of the post-MonoGame 5.x line;
see `docs/ROADMAP.md` ("The post-MonoGame pivot"). Metal-only for now; needs SDL2 (`brew install sdl2`).
