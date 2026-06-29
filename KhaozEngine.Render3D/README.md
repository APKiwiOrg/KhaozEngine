# KhaozEngine.Render3D

Stylized 3D on a custom MonoGame-free foundation (the `KhaozEngine.Gpu` seam, `System.Numerics`).

- `IsoCamera3D` - orthographic isometric camera (configurable angle/zoom/target; `ScreenToRay`/`ScreenToGround`
  picking; `Frame` fit-to-bounds).
- `GltfLoader` / `GltfMesh` / `MeshPrimitives` / `MeshBuilder` - runtime glTF load (SharpGLTF) + procedural meshes.
- `Scene3D` + `Render3DSurface(AppWindow)` - multi-instance mesh draw (`LoadMesh`/`LoadTexture`/`Begin`/`Draw`
  with per-instance tint + `Material`), per-mesh albedo textures, lighting, camera-facing billboards, an
  immediate-mode debug-draw overlay (line/ray/box/grid/axes/circle), composited into the window.
- `Render3DPreview(AppWindow, width, height)` - live render-to-texture: render a model into a sampleable
  `Render2D.Texture2D` on the same device and draw it inside a 2D `SpriteBatch`/Gui panel (unit inspectors, shop
  previews, item icons). Load meshes + frame the camera once via `.Scene`, then call `Capture(drawFrame)` each
  frame (target reused, no per-frame allocation). Transparent background by default
  (`PixelPostProcessSettings.TransparentBackground`) so it composites cleanly.
- `PixelPostProcessSettings` / `Palette` / `Palettes` - palette quantization, Bayer dither, depth/normal
  edge outline, cel bands, all independently toggleable (the smooth look is the default).
- `PropCollisionBake` - offline bakes a `PhysicsShape` from a normalized prop mesh for the `.coll` format.
  Classification: trees -> `BakeTrunkHull` (convex hull of the lower trunk following the leaning centreline;
  `BakeTrunkCylinder` is the degenerate fallback); buildings -> `TriangleMeshShape`; rocks/short solids ->
  `BakeConvexHull`. `PropBakePlan.For` single-sources the per-prop bake decision. `HullFromPoints` is the
  shared hull builder used by both trunk and solid paths.

Renderer deps (Veldrid/Veldrid.SPIRV/SharpGLTF) are confined to this package via `KhaozEngine.Gpu`. See
`docs/USING-KHAOZENGINE.md` and `docs/superpowers/specs/2026-06-15-render3d-custom-engine-design.md`.
