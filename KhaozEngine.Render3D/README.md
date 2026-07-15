# KhaozEngine.Render3D

Stylized 3D on a custom MonoGame-free foundation (the `KhaozEngine.Gpu` seam, `System.Numerics`).

- `IsoCamera3D` - orthographic isometric camera (configurable angle/zoom/target; `ScreenToRay`/`ScreenToGround`
  picking; `Frame` fit-to-bounds).
- `FlyCamera3D` / `FlyCameraController` - free-fly editor camera: `FlyCamera3D` implements `IIsoCamera3D` (a
  world `Position` plus `Yaw`/`Pitch`, no orbit target, pitch clamped short of vertical) so it drops into
  `Scene3D.CameraOverride` exactly like `FollowCamera3D`, and carries the same `ScreenToRay`/`ScreenToGround`/
  `WorldToScreen` picking methods. `FlyCameraController.Update(InputState, dt)` drives it off the raw snapshot:
  hold `LookButton` (default right mouse) to mouselook (drag right looks right, drag up looks up,
  `InvertX`/`InvertY` flip either axis), WASD to fly along the view direction (true flight, not ground-locked),
  E/Q to rise/sink on world +Y, hold `Key.LeftShift` to sprint (`SprintMultiplier`), and the wheel to scale
  `MoveSpeed` (clamped `MinMoveSpeed`..`MaxMoveSpeed`). No smoothing (dt-scaled direct integration), no input
  statics touched (the snapshot is handed in), allocation-free per frame.
- `FollowCamera3D.Warp(target)` / `SnapToTarget()` (since 10.65.0) - hard-cut the third-person follow camera onto a
  point with no ease (the 3D counterpart of `Render2D.CameraFollow.Warp`), for a teleport/respawn so the smoothed
  camera does not "fly" across the jump.
- Teleport transitions (`ITransition` + `HardBlink` / `CameraDissolve` / `CharDissolve`, since 10.65.0) - a phased
  cover -> swap -> optional streaming hold -> reveal state machine (pure timing) that masks a teleport swap +
  destination pop-in. A teleport is a hard cut, so `HardBlink` defaults to an instant, reveal-only cover (opaque on the
  cut frame) and `CharDissolve` materializes the avatar IN at the destination (no unachievable origin dissolve-out).
  Screen-space effects (`IScreenTransition`) render over the final image via `Scene3D.ScreenTransition` (the scene
  freezes the PREVIOUS frame for the crossfade, so it shows the origin view, not the post-cut one); the world-space
  `CharDissolve` rides the `Scene3D.DrawSkinned(..., dissolve, edgeWidth, edgeColor)` overload through a dedicated
  pipeline variant. `Scene3D.ClearScreenTransition()` / `Transition.Reset()` cancel a transition on teardown. Remote
  teleports cut for observers automatically (see `KhaozEngine.NetWorld`). Byte-identical when none is active. See
  docs/USING-KHAOZENGINE.md.
- `GltfLoader` / `GltfMesh` / `MeshPrimitives` / `MeshBuilder` - runtime glTF load (SharpGLTF) + procedural meshes.
- `Scene3D` + `Render3DSurface(AppWindow)` - multi-instance mesh draw (`LoadMesh`/`LoadTexture`/`Begin`/`Draw`
  with per-instance tint + `Material`), per-mesh albedo textures, lighting, camera-facing billboards, an
  immediate-mode debug-draw overlay (line/ray/box/grid/axes/circle) plus depth-tested debug wire volumes
  (sphere/dome/cylinder/circle), composited into the window.
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
  `RenderScale.FixedInternal` (the default) keeps a single bilinear tap on its final downscale for byte-stable
  goldens, which under-samples on a window smaller than the fixed internal target. Opt into the same mip-filtered
  blit machinery there too with `PixelPostProcessSettings.MipFilterFixedInternalDownscale` (default `false`), or
  switch to `RenderScale.MatchViewport` outright.
- Shadows: `PixelPostProcessSettings.Quality.Shadows` (a `ShadowSettings`) picks the shadow tier via `Shadows.Mode`
  - `ShadowMode.Off` (default, byte-stable), `ShadowMode.Blob` (soft dark ground blob under each caster), or
  `ShadowMode.ShadowMap` (key-light directional PCF shadow map). For the blob tier the scene submits one request per
  caster per frame with
  `Scene3D.AddShadowBlob(new ShadowBlob(position, groundY, radius, strength, heightAboveGround))` (cleared each
  `Begin`, like the ground-decal queue); the blob is drawn as a dark `Circle` `GroundDecal` through the existing
  depth-reconstructed ground-decal projection, BEFORE the skinned character pass so a caster's own body occludes its
  own blob (ground-receiver-only: terrain and rigid props receive the blob, characters do not - the Y-band never
  repaints a character's legs). Radius follows the caster footprint; strength fades with height above ground
  (`ShadowSettings.BlobFadeHeight`) so a jumping caster's blob shrinks + lightens.
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
  Skinned draws (`DrawSkinned`) get the same treatment BEFORE the CPU skin pass, so an off-screen character skips
  the per-vertex skin + upload entirely, not just its draw call: a camera-culled draw is skinned only if it is
  ALSO inside the active shadow map's own ortho volume (so an off-camera caster still throws an on-screen shadow -
  the shadow pass is never camera-culled, matching the rigid contract). Rest-pose bounds are inflated by a safety
  factor (`Scene3D.SkinnedCullSafetyFactor`) to cover a pose swinging a limb outside the rest silhouette. Read the
  win from `Scene3D.DrawnSkinnedInstances` / `Scene3D.CulledSkinnedInstances`.
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
- Per-frame draw counters: `Scene3D.LastFrameStats` (a `Primitives.RenderFrameStats`) - always-on draw-call /
  instance / estimated-triangle / buffer-upload-byte totals over the geometry passes (rigid instanced, terrain
  splat, CPU-skinned, shadow-caster depth), plus one draw-call increment per effect/overlay submission. Reset +
  finalized inside the render pass like `DrawnInstances`/`PassTimingsMs`, so read it after the scene rendered.
  Fullscreen post blits are not itemized (their encode time is the Pass-timings `PostMs`). Aggregate it with a
  2D batch's `FrameStats` via `+` for a whole-frame total.
- Sky: `PixelPostProcessSettings.Sky` (a `SkySettings`, **default off**) draws an opt-in procedural sky behind all
  geometry - a vertical `HorizonColor`->`ZenithColor` gradient plus an optional sun disc + halo (`SunColor`,
  `SunRadius`, `HaloStrength`, `HaloFalloff`). Rendered as a far-plane background pass into the lit colour + read-only
  scene depth, so it fills only where no mesh drew, never touches the MRT normal/depth the outline pass reads, and
  costs nothing when off (`Sky.Enabled == false`, existing scenes byte-stable). Screen-space, so it reads under both
  the orthographic `IsoCamera3D` and the perspective `FollowCamera3D`. The sun direction **defaults to the key light**
  (`Post.LightDirection`) so the sky and lighting agree (sun opposite the shadows); override with
  `Sky.SunDirectionOverride`. The pure math is `SkyMath` (gradient + sun falloff + `ProjectSunToNdc`).
- Bloom: `PixelPostProcessSettings.Bloom` (a `BloomSettings`, **default off**) is an opt-in LDR threshold +
  separable-blur bloom - beams, emissive materials, and bright billboards read as a glow instead of flat. A
  bright-pass thresholds the lit colour (soft smoothstep knee, `Threshold`/`Knee`) into a HALF-resolution target,
  blurs it separably (horizontal then vertical, `Radius` taps per side, gaussian weights via `BloomMath`), and adds
  it back onto the full-resolution image at `Intensity` strength. Runs AFTER palette quantize + the edge outline
  (so the glow composites on top of - and is never itself posterized/outlined by - the stylized chain) and BEFORE
  FXAA (so FXAA also smooths the bloom composite's edges). Costs nothing when off (`Bloom.Enabled == false`, no
  extra passes, no half-res targets allocated, existing scenes byte-stable); the half-res pair is lazily allocated
  the first frame it is enabled and freed the frame it is disabled, re-derived from the CURRENT internal target size
  on every resize (works under both `RenderScale.FixedInternal` and `.MatchViewport`). Respects
  `TransparentBackground` (the composite pass preserves the source alpha unchanged, so bloom never resurrects an
  alpha-0 background pixel into an opaque one). **LDR, not HDR**: the internal target is `R8G8B8A8UNorm` (no
  over-1.0 headroom), so the bright-pass thresholds the already-tonemapped-to-[0,1] colour rather than a linear HDR
  value - still a convincing glow on beams/emissive materials/bright billboards, but it will not bloom a surface
  that is merely well-lit white; lower `Threshold` for a softer cutoff. The pure math (`BloomMath`: the knee curve,
  gaussian weight generation, half-res sizing) is headless-tested and mirrors the GLSL bright-pass/blur shaders
  exactly.
- Water: `Scene3D.DrawWater(in WaterPlane)` (a per-frame request: centre XZ + surface height + XZ half-extents) plus
  `PixelPostProcessSettings.Water` (a `WaterSettings`, **default no request queued = no pass, no cost**) draws an
  opt-in animated water surface - a flat, alpha-blended, procedurally-perturbed plane with a fresnel-style blend
  between `DeepColor` and a sky-derived `HorizonColor`, a key-light specular sun glint (`GlintStrength`/
  `GlintExponent`), and depth-sampled shore fade (`ShoreFadeDistance`). **No reflections/probes** (out of scope) -
  an LDR stylized surface. Drawn after the sky + ground decals, before the MRT resolve, with depth test ON (`Less`,
  so geometry above the surface occludes it) but depth WRITE OFF (so the resolved normal/linear-depth the outline
  pass reads is untouched - matching the ground-decal/beam/textured-billboard depth-interleave convention). Time is
  driven by the same `Scene3D.EffectTimeSeconds` clock the beam pulse/scroll uses (freeze it for a deterministic
  frame). The shore fade reconstructs the ground height under each water pixel from the resolved scene depth via
  the same `gl_FragCoord` + raw-inverse-view-projection convention the ground-decal pass uses. The pure math is
  `WaterMath` (internal: scrolling-normal perturbation, Schlick fresnel, Blinn-Phong glint, shore-fade curve, grid
  tessellation), headless-tested and mirroring the GLSL `WaterVert`/`WaterFrag` exactly.
- Motion trails: `Scene3D.DrawTrail(ReadOnlySpan<TrailSample>, TrailStyle)` (since 10.41.0) queues an immediate-mode
  tapered ribbon traced through an ordered list of recent world-space samples (oldest-first) - weapon swings,
  thruster streaks, projectile tracers. Each `TrailSample` carries a world position, per-sample ribbon half-width,
  and alpha (a fading tail is decreasing alpha toward the oldest sample), plus an optional `Facing`: zero =
  camera-facing (`cross(viewDir, tangent)`, like a beam), non-zero = twist-following (`cross(Facing, tangent)`, so
  the ribbon holds a fixed plane, e.g. a blade's sweep). `TrailStyle` (`TrailStyle.Default with { ... }`) carries the
  tint (its alpha multiplies each sample's alpha), `Blend` (`TrailBlend.Additive` default, or `Alpha`), and
  `SoftEdge` (across-width feather). Each sample's across-direction is the bisector (miter) of its two adjacent
  segment tangents, computed once, so the two segments meeting at a sample share the same corner pair and joints
  stay continuous (no gap/overlap). Drawn INTO the model pass with depth test ON (no write) right after the beams,
  so a nearer mesh occludes it. The strip math is the pure, headless-tested `TrailGeometry` (taper/fade/miter);
  timed-sample bookkeeping is the pure `TrailSampler`/`TrailPoint` in `KhaozEngine.Primitives`. Fewer than 2 samples
  is a no-op, samples are copied at call time, and no `DrawTrail` call = no pass (existing scenes stay byte-stable).
- Transparency ordering (since 10.18.2): alpha-blended draws (colour and textured billboards, translucent
  overlay meshes) sort back-to-front by view-space depth within each batch, so overlapping alpha composites
  correctly no matter the submission order. Additive paths (beams, additive billboards) are order-independent
  and skip the sort. Cross-pass order between renderers is fixed. No public API involved.
- `Scene3D.DrawOverlayMesh(MeshHandle mesh, Matrix4x4 world)` - queues a translucent, unlit,
  depth-tested-but-not-depth-writing, alpha-blended draw of an already-loaded mesh, colored by the mesh's
  per-vertex color. A general overlay primitive, not collision-specific: drawn after the meshes/beams and
  before the pixel post.
- Debug wire volumes: `Scene3D.DebugWireSphere` / `DebugWireDome` (hemisphere, flat side down) /
  `DebugWireCylinder` (vertical, `radius` + `halfHeight`) / `DebugWireCircle`, each `(..., Color color, float
  opacity = 1, DebugDepthMode depth = DepthTested, int segments = DebugWireSegments)`. Immediate-mode (cleared each
  `Begin`), for visualising gameplay volumes in-world (an NPC's aggro sphere / attack dome or cylinder). Default
  `DebugDepthMode.DepthTested` draws into the lit colour + read-only scene depth before the post chain, so terrain
  and props occlude the buried parts. `DebugDepthMode.AlwaysOnTop` routes to the crisp post-pass line overlay
  instead. Geometry lives in `DebugShapes.Sphere/Dome/Cylinder/Circle` (pure, headless-testable endpoint builders).
  The depth-tested draw is `Rendering.DepthLineRenderer` (line-list, depth-test-less-equal-no-write, alpha blend
  into `ColorDepthFB`).
- `KhaozEngine.Render3D.Debug` - the collision-shape debug overlay, the first consumer of `DrawOverlayMesh`:
  `CollisionShapeOverlay` (build once from an `IReadOnlyList<CollisionStatic>`, `Enabled`-gated `Draw`,
  `Palette`, `PresentKinds`, `IDisposable`), `CollisionShapeMesh.Build(PhysicsShape, CollisionOverlayPalette)
  -> GltfMesh` (headless shape-to-mesh conversion, recurses into `CompoundShape`), `ConvexHull3D.Triangulate`
  (dependency-free 3D convex-hull triangulation for `ConvexHullShape` proxies), `CollisionOverlayPalette` /
  `CollisionShapeKind` (per-kind color + name lookup) / `CollisionStatic` (the `PhysicsShape`+`Pose` input
  record). See `docs/USING-KHAOZENGINE.md`, "Collision-shape debug overlay".
- Asset manifest categories: `AssetEntry.Category` (an optional `"category"` manifest field, e.g. `"trees"`,
  `"rocks"`, `"buildings"`) tags a prop-kit entry for palette or browser grouping. Null when the manifest
  declares none, in which case a consumer such as `KhaozEngine.MapEditor.ViewportWorld.KindCategories` falls
  back to the declaring manifest's own file-name stem (`props.manifest.json` maps to `props`),
  first-manifest-wins on a duplicate id across manifests.
- Textured props: `PropLoader.LoadPropWithMaterial(AssetEntry, PropValidation?) -> (GltfMesh Mesh, GltfMaterialMaps
  Maps)` loads + normalizes a prop like `LoadProp`, AND auto-reads its glTF's first textured material's
  baseColor/normal/metallicRoughness textures (via `GltfLoader.LoadWithMaterial`). A prop whose glTF has no
  textures yields an all-absent `GltfMaterialMaps` (`GltfMaterialMaps.IsEmpty`), never a throw, so it renders
  exactly as `LoadProp`. Upload the result with `Scene3D.LoadMesh(GltfMesh, GltfMaterialMaps)`. Opt in per-asset
  via the manifest `"textured": true` flag (`AssetEntry.Textured`, default false: renders with the flat
  per-material base colour as before).
- Multi-texture-per-primitive props: a prop whose parts are separate materials (a tree with a bark material +
  a leaf material, a signpost with a wood post + a painted sign) renders each part with its own texture instead
  of one flattened mesh. `GltfLoader.LoadPartsWithMaterials(path) -> IReadOnlyList<GltfMeshPart>` splits the glTF
  into one welded `GltfMeshPart` (`{ GltfMesh Mesh, GltfMaterialMaps Maps }`) per source material, in stable
  first-use order; a single-material asset yields exactly one part byte-identical to `Load` /
  `LoadWithMaterial`, and primitives with no material form their own untextured part.
  `PropLoader.LoadPropParts(AssetEntry, PropValidation?) -> IReadOnlyList<GltfMeshPart>` normalizes all parts by
  ONE shared transform over the whole prop's combined bounds (scaled to `HeightMeters`, base dropped to y=0),
  so the parts stay aligned exactly as authored (never per-part). Upload with `Scene3D.LoadProp(parts) ->
  Scene3D.PropHandle`, draw as a unit with `Scene3D.Draw(PropHandle, world[, tint])` (each part is a normal
  instanced mesh sharing the transform, so drawing a prop at several transforms batches as instances), and free
  with `Scene3D.UnloadProp`.
- Alpha cutout (foliage / leaf cards): `GltfLoader` reads each material's glTF `alphaMode` into
  `GltfMaterialMaps.AlphaCutoff` - `0` for OPAQUE (the default when absent, no clip), else the material's
  `alphaCutoff` (default 0.5 per spec) for MASK. glTF BLEND is out of scope for the mesh pass and treated as
  MASK. The value flows through `Scene3D.SurfaceMaps.AlphaCutoff` and the loaded mesh's material state, and the
  model fragment discards any texel whose sampled baseColor alpha is below it, so an alpha-cutout leaf-card
  texture renders as its silhouette instead of a solid (and, for the Quaternius kits, black-fringed) quad. An
  OPAQUE mesh (cutoff 0) is byte-identical to the pre-cutout render. Shadow casters do not alpha-test (a
  cutout prop casts its full-quad silhouette). Baked kits pair this with a bake-time RGB dilation (alpha bleed)
  in `tools/kit-bake` so mip/bilinear averaging pulls leaf colour, not the black stored under the leaves.
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
- Load-time flatten: `GltfLoader.LoadFlattenedAlbedo(path) -> GltfMesh` is what `PropLoader.LoadProp` calls.
  When a source material carries a `baseColorTexture`, it decodes the texture and folds
  `GltfLoader.AverageAlbedo`'s alpha-weighted average colour (texels with alpha >= 0.5, falling back to a
  plain average when fully transparent) into that material's flattened `baseColorFactor`, so a textures-ON
  kit still renders a sensible flat colour through the flat single-mesh path. **The average is computed on
  the decoded gamma-space (sRGB-encoded) texel bytes directly - do NOT linearize first**, the result feeds
  the same gamma-space `baseColorFactor` slot a hand-authored flat material uses. A material with no texture
  is untouched, so an existing untextured prop is byte-identical to before.
- Manifest-driven textured opt-in: `PropLoader.LoadPropAuto(AssetEntry, PropValidation?) ->
  IReadOnlyList<GltfMeshPart>` reads the entry's `AssetEntry.Textured` flag so a call site never has to
  branch itself. `Textured == true` returns `LoadPropParts`' multi-material part list (one textured
  sub-mesh per source material). `Textured == false` returns the flat `LoadProp` mesh wrapped as a single
  part with all-absent `GltfMaterialMaps`, rendering untextured exactly as `LoadProp` would. Either way the
  result is a uniform `IReadOnlyList<GltfMeshPart>` a caller uploads the same way regardless of mode.
  `Scene3D.LoadPropMeshes(IReadOnlyList<GltfMeshPart>) -> IReadOnlyList<MeshHandle>` uploads that list and
  returns the raw per-part handles (rather than bundling them into one `Scene3D.PropHandle`) - this is the
  shape the multi-part **scatter** path wants (`KhaozEngine.Terrain.PropLayer` / `Scene3DChunkSink` /
  `PropRenderer.DrawProps`, in `KhaozEngine.Terrain.Render3D` - see that package's README). A single-part
  list uploads to one handle, identical to a plain `Scene3D.LoadMesh(GltfMesh, GltfMaterialMaps)`.
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

- Animation (`Animation/`, pure + GPU-free, driven off a `Skeleton` + glTF `AnimationClip`s):
  - `AnimationSampler` / `AnimationPlayer` - one-shot pose sampling and a stateful single-clip player with a
    crossfade (`Play(clip, crossfade)` -> `Update(dt)` -> `GetBonePalette(buffer)`). `Play` loops the clip; `PlayOnce`
    plays it ONCE and CLAMPS the playhead at the clip duration, holding the final frame (a death / knockdown pose that
    must settle and stay) - switching back via `Play` restores looping. `AnimationSampler.SampleInto`
    is the allocation-free sample into a reused per-node pose buffer; `AnimationPlayer.GetLocalPoses(buffer)` writes
    the composited LOCAL poses (the crossfade result before hierarchy composition) so a `LayeredAnimator` can take
    the locomotion crossfade as its base layer.
  - `LayeredAnimator` / `AnimationLayer` / `BoneMask` / `LayerMode` - N animation layers composited into one final
    skeleton pose: a base locomotion layer below, masked `Override` / `Additive` action layers above (attack while
    running). Each `AnimationLayer` is a clip + its own looping playhead + a blend weight + an optional `BoneMask` +
    a `LayerMode`. `BoneMask` is per-node weights 0..1 (`BoneMask.Full`/`.Empty`, `BoneMask.Subtree(skel, root, w)`
    for "this bone and all descendants at weight w" - the upper-body-action shape). Override lerps toward the layer
    pose by `weight x mask`; Additive applies the clip's delta from its first frame (the reference), rotations
    composed multiplicatively, scaled by `weight x mask`. `SetBaseLocals` sets an external base (the locomotion
    crossfade) the stack composites over instead of the rest pose. Zero layers is the rest pose and a single
    full-weight unmasked Override layer is byte-identical to the single-clip path, so existing skinned rendering is
    unchanged until a game adds a layer. Rotation blending matches the crossfade (shortest-arc `Quaternion.Slerp` +
    re-normalize). Steady-state `Update`/`GetBonePalette` allocate nothing.
  - One-shot and held actions - `LayeredAnimator.PlayAction(clip, mask, fadeIn, fadeOut, speed, mode, hold)` ->
    `ActionHandle` plays a clip as a masked action over the base: fade in, then (default `hold: false`) play through
    and fade out overlapping the clip tail and auto-retire, or (`hold: true`) stay at full weight looping the clip as a
    persistent masked pose (a drawn-weapon arm idle) until `Cancel`. `Cancel(handle)` fades an action out early from its
    current weight (no pose pop) and retires it. Slots pooled + reused, so repeated actions allocate nothing. A held
    action played first sits below later one-shot actions, which composite over it and fall back to it as they retire.
    `AnimatedCharacter.PlayAction` / `CancelAction` wrap this over the locomotion base (the byte-stable single-player
    path when no action is live). Callable on a remote character's brain too (no ownership state) -
    `ReplicatedCharacterAnimators.BrainFor(id)` reaches it.

Renderer deps (Veldrid/Veldrid.SPIRV/SharpGLTF) are confined to this package via `KhaozEngine.Gpu`. See
`docs/USING-KHAOZENGINE.md`.
