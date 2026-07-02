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
- `Scene3D.DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world)` - queues a translucent, unlit,
  depth-tested-but-not-depth-writing, alpha-blended draw of an already-loaded mesh, colored by the mesh's
  per-vertex color. A general overlay primitive, not collision-specific: drawn after the meshes/beams and
  before the pixel post.
- `KhaozEngine.Render3D.Debug` - the collision-shape debug overlay, the first consumer of `DrawOverlayMesh`:
  `CollisionShapeOverlay` (build once from an `IReadOnlyList<CollisionStatic>`, `Enabled`-gated `Draw`,
  `Palette`, `PresentKinds`, `IDisposable`), `CollisionShapeMesh.Build(PhysicsShape, CollisionOverlayPalette)
  -> GltfMesh` (headless shape-to-mesh conversion, recurses into `CompoundShape`), `ConvexHull3D.Triangulate`
  (dependency-free 3D convex-hull triangulation for `ConvexHullShape` proxies), `CollisionOverlayPalette` /
  `CollisionShapeKind` (per-kind color + name lookup) / `CollisionStatic` (the `PhysicsShape`+`Pose` input
  record). See `docs/USING-KHAOZENGINE.md`, "Collision-shape debug overlay".
- Textured props: `PropLoader.LoadPropWithMaterial(AssetEntry, PropValidation?) -> (GltfMesh Mesh, GltfMaterialMaps
  Maps)` loads + normalizes a prop like `LoadProp`, AND auto-reads its glTF's first textured material's
  baseColor/normal/metallicRoughness textures (via `GltfLoader.LoadWithMaterial`). A prop whose glTF has no
  textures yields an all-absent `GltfMaterialMaps` (`GltfMaterialMaps.IsEmpty`), never a throw, so it renders
  exactly as `LoadProp`. Upload the result with `Scene3D.LoadMesh(GltfMesh, GltfMaterialMaps)`. Opt in per-asset
  via the manifest `"textured": true` flag (`AssetEntry.Textured`, default false: renders with the flat
  per-material base colour as before).
  - `MeshOps.WithTangents(GltfMesh) -> GltfMesh` computes a per-vertex tangent from UV + position (Lengyel
    accumulate, then Gram-Schmidt against the normal) so a UV-mapped primitive mesh (e.g. `MeshPrimitives.Box`)
    can be normal-mapped. A vertex whose faces have no UV gradient keeps a zero tangent, which the shader reads
    as "no TBN" (falls back to the geometric normal).
  - `PropMaterialPresets.Procedural(int size = 64, int seed = 1337) -> GltfMaterialMaps` generates a
    deterministic, asset-free mossy-stone albedo + derived tangent-space normal (raw RGBA, no PNG encoder, no
    asset file) for samples and tests, mirroring `TerrainMaterialPresets.Procedural`.
- `PropCollisionBake` - offline bakes a `PhysicsShape` from a normalized prop mesh for the `.coll` format.
  Classification: trees -> `BakeTrunkCylinder` (a thin trunk cylinder, `BakeTrunkHull` retained but no longer
  the default); buildings -> `TriangleMeshShape`; rocks/short solids -> `BakeConvexHull`. `PropBakePlan.For`
  single-sources the per-prop bake decision. `HullFromPoints` is the shared hull builder.
  - `BakeProxy(renderRaw, heightMeters, proxyGroups)` bakes a building's SEPARATE simplified collision proxy: an
    authored `<id>_collision.glb` of convex blocks (one per object) becomes a `CompoundShape` of convex hulls,
    normalized into the render mesh's frame. Convex pieces never wedge the capsule, unlike a building's full
    one-sided render mesh. `GltfLoader.LoadGroups(path)` loads the proxy one `GltfMesh` per logical node (object
    boundaries preserved); `PropBakePlan.ForProxy` keeps the surface rule. See `tools/proxy-authoring/`.

Renderer deps (Veldrid/Veldrid.SPIRV/SharpGLTF) are confined to this package via `KhaozEngine.Gpu`. See
`docs/USING-KHAOZENGINE.md` and `docs/superpowers/specs/2026-06-15-render3d-custom-engine-design.md`.
