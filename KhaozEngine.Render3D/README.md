# KhaozEngine.Render3D

Stylized 3D on a custom MonoGame-free foundation (the `KhaozEngine.Gpu` seam, `System.Numerics`).

- `IsoCamera3D` — orthographic isometric camera (configurable angle/zoom/target; `ScreenToRay`/`ScreenToGround`
  picking; `Frame` fit-to-bounds).
- `GltfLoader` / `GltfMesh` / `MeshPrimitives` / `MeshBuilder` — runtime glTF load (SharpGLTF) + procedural meshes.
- `Scene3D` + `Render3DSurface(AppWindow)` — multi-instance mesh draw (`LoadMesh`/`LoadTexture`/`Begin`/`Draw`
  with per-instance tint + `Material`), per-mesh albedo textures, lighting, camera-facing billboards, an
  immediate-mode debug-draw overlay (line/ray/box/grid/axes/circle), composited into the window.
- `PixelPostProcessSettings` / `Palette` / `Palettes` — palette quantization, Bayer dither, depth/normal
  edge outline, cel bands, all independently toggleable (the smooth look is the default).

Renderer deps (Veldrid/Veldrid.SPIRV/SharpGLTF) are confined to this package via `KhaozEngine.Gpu`. See
`docs/USING-KHAOZENGINE.md` and `docs/superpowers/specs/2026-06-15-render3d-custom-engine-design.md`.
