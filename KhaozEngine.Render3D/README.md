# KhaozEngine.Render3D (experimental)

Retro/pixel 3D on a custom MonoGame-free foundation (Veldrid + SPIR-V, `System.Numerics`).

- `IsoCamera3D` — orthographic isometric camera (configurable angle/zoom/target).
- `GltfLoader` / `GltfMesh` — runtime glTF load via SharpGLTF.
- `Scene3D` + `Render3DHost` — lit/cel model draw into a low-res target, pixel post chain, point-upscale.
- `PixelPostProcessSettings` / `Palette` / `Palettes` — palette quantization, Bayer dither, depth/normal
  edge outline, all independently toggleable.

This is the seed of the post-MonoGame KhaozEngine (5.x line). Deps (Veldrid/Veldrid.SPIRV/SharpGLTF) are
confined to this package. See `docs/USING-KHAOZENGINE.md` and
`docs/superpowers/specs/2026-06-15-render3d-custom-engine-design.md`.
