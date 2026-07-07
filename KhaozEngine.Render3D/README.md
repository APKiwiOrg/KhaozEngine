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
- Anti-aliasing: `PixelPostProcessSettings.Quality.AntiAliasing` (a `RenderQuality` container) is the AA dropdown
  most games ship - `AntiAliasing.Off` / `.Fxaa` / `.Msaa(2|4|8)` / `.Ssaa(factor)`. Validate a menu choice against
  the device with `AntiAliasing.ResolveFor(caps)` (clamps MSAA to `GpuCapabilities.MaxMsaaSampleCount` or falls back
  to FXAA, never throws). Default `Off`, so the low-level `RenderScale` / `Supersample` fields still govern.
  SSAA supersamples the whole image (geometry AND shaded interiors, the only one that kills high-frequency terrain
  shimmer) and now downsamples correctly at ANY factor via a mip-filtered blit; FXAA is a cheap one-pass edge
  smoother; MSAA multisamples geometry edges only. `RenderQuality` is the extension point for further quality knobs.
- Shadows: `PixelPostProcessSettings.Quality.Shadows` (a `ShadowSettings`) picks the shadow tier via `Shadows.Mode`
  - `ShadowMode.Off` (default, byte-stable), `ShadowMode.Blob` (soft dark ground blob under each caster), or
  `ShadowMode.ShadowMap` (key-light directional PCF shadow map). For the blob tier the scene submits one request per
  caster per frame with
  `Scene3D.AddShadowBlob(new ShadowBlob(position, groundY, radius, strength, heightAboveGround))` (cleared each
  `Begin`, like the ground-decal queue); the blob is drawn as a dark `Circle` `GroundDecal` through the existing
  depth-reconstructed ground-decal projection. Radius follows the caster footprint; strength fades with height above
  ground (`ShadowSettings.BlobFadeHeight`) so a jumping caster's blob shrinks + lightens.
  - `ShadowMode.ShadowMap`: a depth-only pass renders the instanced casters (models cast; terrain receives only) into
  an ortho light-space depth map fitted around the camera focus (texel-snapped to kill shimmer), which the shared
  lighting block PCF-samples (3x3 + slope-scaled bias) to shadow the KEY light's diffuse+spec only for BOTH models
  and terrain. Every drawn mesh casts automatically (no per-frame opt-in). Knobs on `ShadowSettings`:
  `ShadowMapResolution` (default `2048`, a construction-time knob), `ShadowFocusRadius`/`ShadowGroundHeight`,
  `ShadowStrength`, and the acne biases `ShadowConstantBias`/`ShadowSlopeBias`. On a device without depth-sample
  support (`GpuCapabilities.SupportsShadowMaps` false) it degrades to `Blob`.
  - Validate a menu choice with `Shadows.ResolveFor(caps)` and read `.Effective`/`.Degraded`/`.Reason` (same
  `ResolveFor`-clamps-a-request pattern as AA, never throws). With `Off` the blob queue is ignored and the shadow tail
  sits at strength 0 (never tapped), so existing scenes are byte-stable.
- Frustum culling: `Scene3D.FrustumCulling` (on by default) skips any queued mesh instance whose world-space
  bounding sphere is entirely outside the camera frustum, so off-screen terrain chunks and props cost nothing to
  draw. Pixel-neutral by construction (only provably-offscreen geometry is dropped), so existing renders stay
  byte-stable; set it `false` to force everything drawn. The shadow depth pass is never camera-culled (an off-screen
  caster still writes the shadow map, so its shadow lands on-screen). Read the win from `Scene3D.DrawnInstances` /
  `Scene3D.CulledInstances`. Mesh-local bounds (`MeshBounds`, computed once at `LoadMesh`) feed the pure plane math
  `FrustumPlanes.Extract(camera.ViewProjection)` + `IntersectsAabb`/`IntersectsSphere` (headless, allocation-free).
- Per-pass timing: `Scene3D.EnableTiming` (default `false`, no cost when off - a single `bool` check, no
  `Stopwatch` call, no allocation) brackets each render pass with a CPU `Stopwatch` and exposes the result as
  `Scene3D.PassTimingsMs` (a `Scene3DPassTimingsMs`: `ShadowDepthMs`/`ModelMs`/`TransparentsMs`/`PostMs`). This is
  CPU time spent RECORDING each pass's commands, NOT true GPU execution time - Veldrid 4.9.0 (the pinned GPU
  abstraction) exposes no timestamp-query API, so true per-pass GPU timing is out of scope pending a Veldrid
  upgrade; see `docs/USING-KHAOZENGINE.md` for the full explanation and why a whole-frame GPU-time number was
  considered and rejected (it would need a per-frame `WaitForIdle`, which breaks frame pacing). `Scene3D` has no
  dependency on `KhaozEngine.Diagnostics`, so feed these numbers into a `KhaozEngine.Diagnostics.PassTimings`
  meter yourself (one `Sample(name, ms)` call per pass per frame) to get rolling avg/min/max and a
  `DiagnosticsOverlay.PassTimingsSection` row set, the same shape `FrameStats`/`PerformanceSection` give you.
  `ShadowDepthMs` stays 0 whenever the shadow tier is not `ShadowMode.ShadowMap` (that pass does not run).
- Sky: `PixelPostProcessSettings.Sky` (a `SkySettings`, **default off**) draws an opt-in procedural sky behind all
  geometry - a vertical `HorizonColor`->`ZenithColor` gradient plus an optional sun disc + halo (`SunColor`,
  `SunRadius`, `HaloStrength`, `HaloFalloff`). Rendered as a far-plane background pass into the lit colour + read-only
  scene depth, so it fills only where no mesh drew, never touches the MRT normal/depth the outline pass reads, and
  costs nothing when off (`Sky.Enabled == false`, existing scenes byte-stable). Screen-space, so it reads under both
  the orthographic `IsoCamera3D` and the perspective `FollowCamera3D`. The sun direction **defaults to the key light**
  (`Post.LightDirection`) so the sky and lighting agree (sun opposite the shadows); override with
  `Sky.SunDirectionOverride`. The pure math is `SkyMath` (gradient + sun falloff + `ProjectSunToNdc`).
- Transparency ordering (since 10.18.2): alpha-blended draws (colour and textured billboards, translucent
  overlay meshes) sort back-to-front by view-space depth within each batch, so overlapping alpha composites
  correctly no matter the submission order. Additive paths (beams, additive billboards) are order-independent
  and skip the sort. Cross-pass order between renderers is fixed. No public API involved.
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
  - `MeshOps.ScaleUv(GltfMesh, float scale) -> GltfMesh` multiplies every UV by `scale` so a material tiles
    `scale` times across the original 0..1 span (denser texels read crisp instead of one stretched, blurry
    copy). Apply before `WithTangents` when you want the tangent basis derived from the tiled UVs.
  - `PropMaterialPresets.Procedural(int size = 256, int seed = 1337) -> GltfMaterialMaps` generates a
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

- World-space overlays drawn screen-space in the 2D pass after the 3D scene (project a world point via
  `IIsoCamera3D.WorldToScreen`, distance-cull, not depth-tested):
  - `WorldLabel.Draw(...)` - a single centred name floating above a world point (text only). `WorldLabel.ShouldCull`
    exposes the distance predicate render-free.
  - `NameplateRenderer.Draw(...)` - the MMO-style plate that supersedes `WorldLabel`: a rounded panel (`DrawRounded`
    on a white texture) holding a centred title and stacked `NameplateBar`s (health/resource meters). Data-driven
    via the `Nameplate` model (title + `Bars` list, add more bars without a rewrite) and styled by `NameplateStyle`
    (`.Default` = the opaque unified plate; drop `PanelFill` alpha + set `TitleShadow` for the panel-less pill).
    `NameplateLayout.Measure` is the pure, GPU-free panel-size math (headless-testable); `NameplateBar.Fraction` is
    clamped 0..1 at draw; `NameplateRenderer.ShouldCull` shares `WorldLabel`'s cull. No per-frame heap allocation.

Renderer deps (Veldrid/Veldrid.SPIRV/SharpGLTF) are confined to this package via `KhaozEngine.Gpu`. See
`docs/USING-KHAOZENGINE.md`.
