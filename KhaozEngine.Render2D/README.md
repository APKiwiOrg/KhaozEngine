# KhaozEngine.Render2D (experimental)

2D rendering on the custom MonoGame-free foundation (Veldrid + SPIR-V, `System.Numerics`).

- `SpriteBatch` — batched textured quads, alpha blend + tint, per-texture batching.
- `Camera2D` — position/zoom/rotation 2D camera (headless, unit-tested).
- `Texture2D` — GPU texture; PNG load via StbImageSharp.
- `SpriteFont` — runtime TrueType text (stb_truetype glyph atlas), `DrawString` / `Measure`.
- `Render2DSurface(AppWindow)` — draw into a `KhaozEngine.Windowing` window; `Render2DSnapshot` captures headless.

Veldrid stays internal; deps (Veldrid/Veldrid.SPIRV/StbTrueTypeSharp/StbImageSharp) are confined to this
package. Windowing/input come from `KhaozEngine.Windowing` (`AppWindow`). Part of the post-MonoGame 5.x line;
see `docs/ROADMAP.md` ("The post-MonoGame pivot"). Metal-only for now; needs SDL2 (`brew install sdl2`).
