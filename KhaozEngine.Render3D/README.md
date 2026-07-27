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
- `Scene3D.RenderOrigin` / `RenderOriginActive` / `IRenderOriginAware` - camera-relative rendering, so a world
  100 km from the origin renders as cleanly as one at it. `Scene3D` subtracts a quantized render origin (default
  `WorldFrame.Nearest(camera.Eye).Anchor`, a 128 m grid point, so the subtraction is exact) from every world
  position on its way to the GPU, which removes the large operands before the matrix concatenation ever meets
  them. **Adoption: none** - every entry point still takes ABSOLUTE world coordinates, `WorldToScreen` and
  `ScreenToRay` still speak absolute, and every CPU-side spatial computation (frustum culling, the terrain cull
  fast path, shadow-cascade fitting, caster classification, the transparency sorts) still runs in absolute space,
  byte-identical to before. The origin is LATCHED at `Begin()`. `RenderOrigin = Vector3.Zero` opts out exactly.
  The three engine cameras implement `IRenderOriginAware`. A consumer camera that does not falls the WHOLE
  pipeline back to the absolute path (never half an origin) and `RenderOriginActive` reports `false`.
  `Transform3D.ToMatrix(Vector3 renderOrigin)` builds a reduced matrix for a consumer that wants one, which
  `Scene3D` itself never requires. Terrain chunk vertices, terrain texturing at range and depth precision are
  explicitly NOT fixed by it. See docs/USING-KHAOZENGINE.md.
- `GltfLoader` / `GltfMesh` / `MeshPrimitives` / `MeshBuilder` - runtime glTF load (SharpGLTF) + procedural meshes.
- `Scene3D` + `Render3DSurface(AppWindow)` - multi-instance mesh draw (`LoadMesh`/`LoadTexture`/`Begin`/`Draw`
  with per-instance tint + `Material`, plus a per-instance dissolve overload `Draw(handle, transform, tint,
  material, dissolve, edgeWidth, edgeColor)` - the rigid mirror of the skinned `DrawSkinned` dissolve, folded
  into the shared model pipeline so it stays one instanced draw per mesh), per-mesh albedo textures, lighting, camera-facing billboards, an
  immediate-mode debug-draw overlay (line/ray/box/grid/axes/circle) plus depth-tested debug wire volumes
  (sphere/dome/cylinder/circle), composited into the window. `UnloadTexture`/`UnloadMesh`/`UnloadSkinnedMesh`/
  `UnloadSplatMaterial` drain the device (`IGpuDevice.WaitForIdle`) before disposing GPU resources, since a
  queued upload or draw may still reference them.
- `Render3DPreview(AppWindow, width, height)` - live render-to-texture: render a model into a sampleable
  `Render2D.Texture2D` on the same device and draw it inside a 2D `SpriteBatch`/Gui panel (unit inspectors, shop
  previews, item icons). Load meshes + frame the camera once via `.Scene`, then call `Capture(drawFrame)` each
  frame (target reused, no per-frame allocation). Transparent background by default
  (`PixelPostProcessSettings.TransparentBackground`) so it composites cleanly. `Resize` drains the device
  before disposing the old target/framebuffer, since the previous frame's queued render may still reference
  them.
- `PixelPostProcessSettings` / `Palette` / `Palettes` - palette quantization, Bayer dither, depth/normal
  edge outline, cel bands, all independently toggleable (the smooth look is the default).
- Anti-aliasing: `PixelPostProcessSettings.Quality.AntiAliasing` (a `RenderQuality` container) is the AA dropdown
  most games ship - `AntiAliasing.Off` / `.Fxaa` / `.Msaa(2|4|8)` / `.Ssaa(factor)`. Validate a menu choice against
  the device with `AntiAliasing.ResolveFor(caps)` (clamps MSAA to `GpuCapabilities.MaxMsaaSampleCount` or falls back
  to FXAA, never throws). Default `Off`, so the low-level `RenderScale` / `Supersample` fields still govern.
  SSAA supersamples the whole image (geometry AND shaded interiors, the only one that kills high-frequency terrain
  shimmer) and now downsamples correctly at ANY factor via a mip-filtered blit. FXAA is a cheap one-pass edge
  smoother. MSAA multisamples geometry edges only. `RenderQuality` is the extension point for further quality knobs.
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
  repaints a character's legs). Radius follows the caster footprint. Strength fades with height above ground
  (`ShadowSettings.BlobFadeHeight`) so a jumping caster's blob shrinks + lightens.
  - `ShadowMode.ShadowMap`: a depth-only pass renders the instanced casters (models cast; terrain receives only) into
  a CASCADED ortho light-space depth atlas - `ShadowCascadeCount` cascades (default 3) side by side in one R32F
  texture, each fitted to the bounding sphere of its own slice of the camera's ACTUAL view frustum (frustum-slice CSM,
  not a fixed-radius circle around a focus point, so shadow sharpness no longer depends on where the camera looks and
  there is no gaze-ground-ray jump on a pan), texel-snapped per cascade to kill shimmer. The slices run from the tight
  near cascade (`ShadowNearDistance`) out to `ShadowMaxDistance`. The shared lighting block picks the tightest cascade
  per fragment and PCF-samples it (3x3 + slope-scaled bias) to shadow the KEY light's diffuse+spec only for BOTH
  models and terrain, cross-fading toward the next cascade inside a `ShadowCascadeBlend` UV border band so a cascade
  hand-off is invisible instead of a visible square seam, and fading the outermost cascade's term to lit at its border
  so the coverage limit is invisible (no hard box, no sliding coverage). Caster visibility for the shadow pass unions
  ALL cascade frustums (a caster camera-culled from the main pass but still inside any cascade still casts its
  shadow). Every drawn mesh casts automatically (no per-frame opt-in). A degenerate camera makes
  `ComputeShadowCascades()` return `0` and disables shadows for that frame rather than throwing. Knobs on
  `ShadowSettings`: `ShadowCascadeCount` (default `3`, `1..4` via `ResolvedCascadeCount`), `ShadowNearDistance`
  (default `16`, the near cascade's view-depth reach - smaller packs texels onto the near action) and
  `ShadowMaxDistance` (default `130`, the far reach, `ResolvedMaxDistance` clamps it `>= ShadowNearDistance`).
  `ShadowCascadeBlend` (default `0.15`, the cross-cascade blend band width as a UV fraction, `0` restores the hard
  cut). `ShadowMapResolution` (default `2048`, the PER-CASCADE resolution, so the atlas is `ShadowCascadeCount *
  ShadowMapResolution^2 * 4` bytes). `ShadowMapResolution` and `ShadowCascadeCount` are **construction-time** knobs sized
  as the scene builds its atlas (its handle is bound into every material set), so pass them through the `ShadowSettings`
  construction seam - `new Render3DSurface(window, shadows)` / `Render3DPreview` / `Render3DSnapshot.Capture(..., shadows)`
  / `GameApp3D` `base(options, shadows)` - and a write to either on a live scene throws instead of silently no-opping.
  `ShadowStrength`, and
  the acne biases `ShadowNormalOffset` (default `2.5` texels, the extent-aware normal-offset bias, scaled PER CASCADE
  so far cascades do not acne and near ones do not detach) plus the tiny residual depth biases
  `ShadowConstantBias`/`ShadowSlopeBias` (defaults `0.0004`/`0.0015`). On a device without depth-sample support
  (`GpuCapabilities.SupportsShadowMaps` false) it degrades to `Blob`. The atlas persists across frames, so the pass
  **dirty-skips**: it re-renders only when a shadow-relevant input changed (ANY cascade's fitted matrix, the rigid
  caster set/transforms, the resolution, or any animated skinned caster present) and otherwise reuses the prior atlas,
  so a mostly-static scene stops repainting it every frame. A skipped pass adds zero shadow draw calls to
  `LastFrameStats`. Read `Scene3D.ShadowPassSkippedLastFrame` for a diagnostics signal.
  - Validate a menu choice with `Shadows.ResolveFor(caps)` and read `.Effective`/`.Degraded`/`.Reason` (same
  `ResolveFor`-clamps-a-request pattern as AA, never throws). With `Off` the blob queue is ignored and the shadow tail
  sits at strength 0 (never tapped), so existing scenes are byte-stable.
- Frustum culling: `Scene3D.FrustumCulling` (on by default) skips any queued mesh instance whose world-space
  bounding sphere is entirely outside the camera frustum, so off-screen terrain chunks and props cost nothing to
  draw. Pixel-neutral by construction (only provably-offscreen geometry is dropped), so existing renders stay
  byte-stable. Set it `false` to force everything drawn. The shadow depth pass is never camera-culled (an off-screen
  caster still writes the shadow map, so its shadow lands on-screen). Read the win from `Scene3D.DrawnInstances` /
  `Scene3D.CulledInstances`. Mesh-local bounds (`MeshBounds`, computed once at `LoadMesh`) feed the pure plane math
  `FrustumPlanes.Extract(camera.ViewProjection)` + `IntersectsAabb`/`IntersectsSphere` (headless, allocation-free).
  Skinned draws (`DrawSkinned`) get the same treatment BEFORE the CPU skin pass, so an off-screen character skips
  the per-vertex skin + upload entirely, not just its draw call: a camera-culled draw is skinned only if it is
  ALSO inside the active shadow map's own ortho volume (so an off-camera caster still throws an on-screen shadow -
  the shadow pass is never camera-culled, matching the rigid contract). Rest-pose bounds are inflated by a safety
  factor (`Scene3D.SkinnedCullSafetyFactor`) to cover a pose swinging a limb outside the rest silhouette. Read the
  win from `Scene3D.DrawnSkinnedInstances` / `Scene3D.CulledSkinnedInstances`.
- GPU skinning (opt-in, `Scene3D.UseGpuSkinning`, default OFF): the vertex shader blends the bone palette instead of
  the CPU (`SkinningMath.SkinVertex`), so the rest-pose vertex buffer uploads once at load and only the per-draw
  palette + matrices upload each frame - the win at MMO crowd scale. Pixel-parity with the CPU path, same culling +
  shadow pass. Built on the fold-matrix binding (one combined per-draw UBO read by both stages, material maps at set
  1) that sidesteps the Metal one-uniform-buffer-per-pipeline limit (see `docs/DEPENDENCY-SEAMS.md`). Ships OFF
  pending a windowed A/B against CPU skinning (the Showcase 3D room's F key + HUD). See `docs/USING-KHAOZENGINE.md`.
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
- Background: `PixelPostProcessSettings.Background` (a `BackgroundMode`: `Solid`, `Starfield` (default), `Sky`) is
  the one knob for what fills a pixel no scene geometry drew. It is a DERIVED view over the existing
  `Starfield`/`Sky.Enabled` booleans, not new state: the getter encodes the long-standing `Sky` over `Starfield`
  over `Solid` precedence in one place, the setter clears the modes it did not select. Nothing is `[Obsolete]` and
  the booleans keep working unchanged. Since 11.9.0 the starfield is a real background pass, `StarfieldRenderer`
  (an internal `SkyRenderer` sibling): a fullscreen triangle at the far plane with a read-only `Equal` depth test
  paints ONLY where the stored depth still equals the cleared far plane, i.e. background where no geometry drew,
  writing `alpha = 1` before the ground decals. Procedural stars therefore flow through the whole post chain like
  any other scene content: they quantize/dither with the retro passes, bloom, warp under screen-space distortion,
  and tonemap in HDR, instead of being pasted on after the chain finished. `TransparentBackground` interaction: a
  non-`Solid` background always paints alpha 1, so a transparent composite needs
  `Background = BackgroundMode.Solid` set explicitly. `Render3DPreview` already forces `Post.Starfield = false`,
  which resolves to `Solid` through the derived getter.
- Ground decal void fallback: `GroundDecal.VoidFallback` (default `false`) projects a flagged decal
  onto its own horizontal plane wherever its usual depth-reconstructed paint surface is missing,
  instead of truncating at the geometry's edge (a range ring overhanging a floating island's edge
  reads as a complete ring instead of vanishing at the cliff). The decal still CONFORMS to any
  surface that is its ground (inside `YTolerance`/`MaxStep` and near-horizontal). The plane is a
  fallback only for background and for a surface outside that band, and there it paints only where
  a depth comparison says it is genuinely visible, so it paints across a cliff it overhangs but is
  occluded (not x-rayed) by a wall standing on its own ground. `GroundDecal.VoidDim` (default `0`)
  scales alpha on plane-projected pixels only, so they can read as projected rather than as
  standing on ground (0 = no dim, 1 = fully transparent). Default `false` keeps the legacy
  depth-only behavior byte-for-byte, with zero extra draws and no new pipeline bound. See
  `docs/USING-KHAOZENGINE.md`.
- Dynamic-geometry reject (since 14.0.1, automatic): the main ground-decal pass conforms to the
  static world but skips skinned characters, so a decal with a tall Y-band no longer paints onto a
  character standing in it. The model pass tags skinned meshes (CPU- and GPU-skinning) in the normal
  target's alpha and the decal pass discards those pixels. Rigid geometry still receives decals and a
  scene with no skinned geometry is byte-for-byte identical. The early blob-shadow pass is unaffected.
- Molten cracks + edge erosion (since 13.4.0, both opt-in and byte-neutral when unset):
  `DecalFillPattern.MoltenCracks` is an animated Voronoi crack web in decal-local XZ - a near-white
  core at each cell border falling off through a `GroundDecal.AccentColor` glow into the dark
  `FillColor` field between cells. Field alpha rides `FillColor.A` (near-opaque scorch), crack
  alpha rides `AccentColor.A` independently, `FlashAdd` stays the global brightness lift.
  `PatternScale` = cells per world unit, `PatternSpeed` = slow per-cell breathing (drift + heat
  swell, not a scroll), `GroundDecal.PatternParam` = crack width in cell units (0 = 0.22 default).
  Deterministic per position, and `GroundDecalQuality.Reduced` swaps the exact two-pass Voronoi border
  distance for a cheaper single-pass approximation. `GroundDecal.EdgeErosion` (0..1, default 0)
  breaks the analytic silhouette into stable organic fingers for every shape and pattern (value
  noise thresholded against a margin rising toward the edge, biting inward up to ~35% of the
  shape's half-thickness), then `FeatherWidth` feathers the survivors. See
  `docs/USING-KHAOZENGINE.md`.
- Sky: `PixelPostProcessSettings.Sky` (a `SkySettings`, **default off**) draws an opt-in procedural sky behind all
  geometry - a vertical `HorizonColor`->`ZenithColor` gradient plus an optional sun disc + halo (`SunColor`,
  `SunRadius`, `HaloStrength`, `HaloFalloff`). Rendered as a far-plane background pass into the lit colour + read-only
  scene depth, so it fills only where no mesh drew, never touches the MRT normal/depth the outline pass reads, and
  costs nothing when off (`Sky.Enabled == false`, existing scenes byte-stable). The sun direction **defaults to the
  key light** (`Post.LightDirection`) so the sky and lighting agree (sun opposite the shadows). Override with
  `Sky.SunDirectionOverride`. `Sky.Anchor` (a `SunAnchor`, default `SunAnchor.World`) chooses how the disc is placed:
  `World` anchors it to the world-space sun direction via a true point-at-infinity projection through the camera (the
  disc stays fixed over the world direction the sun lies in as the camera orbits, and is hidden when the sun is behind
  the camera - correct for the perspective `FollowCamera3D`/`FlyCamera3D`. It degenerates under the orthographic
  `IsoCamera3D`, where a directional sun has no finite screen position, so pick `StylizedBackdrop` there).
  `StylizedBackdrop` keeps the legacy
  camera-relative placement (view-space right/up read as screen NDC, visible above the view horizon), which works
  under both cameras and is the pick for the iso look. The pure math is `SkyMath` (gradient + sun falloff +
  `ProjectSunToNdc`, which dispatches on the anchor to `ProjectSunWorldToNdc` / `ProjectSunStylizedToNdc`).
- `SunCycle` - a pure day/night mapping from a caller-supplied normalized time of day to the sky and lighting
  above. `SunCycle.Evaluate(timeOfDay, SunCycleSettings)` walks a latitude/declination/heading sun arc and
  returns a `SunCycleState` blended between `SunCycleSettings.DayPalette`/`DuskPalette`/`NightPalette`
  (three `SunCyclePalette` anchors) keyed on sun elevation, not time, so the same settings work at any
  latitude or day length. `SunCycle.Apply(state, scene.Post)` writes it onto `Post.LightDirection`/
  `LightColor`/`AmbientColor`/`FillLightColor` and `Post.Sky.HorizonColor`/`ZenithColor`/`SunColor`/
  `SunEnabled`, plus `Post.Sky.SunDirectionOverride`. The night key follows `SunCycleSettings.NightKey`
  (`NightKeyMode`, default `AntiSolarMoon`): `AntiSolarMoon` is the historical virtual moon opposite the sun,
  dipping to zero across the crossing so the direction flip is invisible; `None` is a keyless night (black key,
  the sun's true direction held); `Moon` is a real decoupled moon body (own `MoonHourOffset`/`MoonDeclinationDegrees`/
  `MoonKeyColor`/`MoonDiscColor`/`MoonHorizonKeyDipDegrees`) that owns the key + single disc slot while it is up,
  each body fading to black at its own crossing so a switch is always through black, with an independent disc color
  so a decorative moon can cast a black key. `SunCycleState` exposes `MoonElevationDegrees`/`MoonDirection`/
  `ActiveSource` (`KeyLightSource`)/`DiscDirectionOverride`. The night ambient floor stays above black so scenes
  remain playable. The engine owns no clock: feed it your own game time (an MMO replicates it from the server)
  each frame.

  ```csharp
  var cycle = new SunCycleSettings();
  scene.Post.Sky.Enabled = true;
  // each frame, timeOfDay in [0,1) comes from YOUR game clock:
  SunCycle.Apply(SunCycle.Evaluate(timeOfDay, cycle), scene.Post);
  ```
- Bloom: `PixelPostProcessSettings.Bloom` (a `BloomSettings`, **default off**) is an opt-in threshold +
  separable-blur bloom - beams, emissive materials, and bright billboards read as a glow instead of flat. A
  bright-pass thresholds the lit colour (soft smoothstep knee, `Threshold`/`Knee`) into a HALF-resolution target,
  blurs it separably (horizontal then vertical, `Radius` taps per side, gaussian weights via `BloomMath`), and adds
  it back onto the full-resolution image at `Intensity` strength. In HDR mode (the default) it runs FIRST, on the
  float16 scene BEFORE the tonemap (hot cores over 1.0 halo then desaturate through the filmic curve). In legacy
  mode it runs AFTER palette quantize + the edge outline (so the glow composites on top of, and is never itself
  posterized/outlined by, the stylized chain) and BEFORE FXAA. Costs nothing when off (`Bloom.Enabled == false`, no
  extra passes, no half-res targets allocated, existing scenes byte-stable), the half-res pair is lazily allocated
  the first frame it is enabled and freed the frame it is disabled, re-derived from the CURRENT internal target size
  on every resize (works under both `RenderScale.FixedInternal` and `.MatchViewport`). Respects
  `TransparentBackground` (the composite pass preserves the source alpha unchanged, so bloom never resurrects an
  alpha-0 background pixel into an opaque one). HDR-aware threshold: in HDR mode the target is float16
  (`R16G16B16A16Float`) and the bright-pass reads the PRE-tonemap scene, so `Threshold` sees luma over 1.0 (set it at
  or above 1.0 to bloom only genuinely over-range content). In legacy mode the target is `R8G8B8A8UNorm` with no
  over-1.0 headroom, so it thresholds the already-clamped-to-[0,1] colour (lower `Threshold` for a softer cutoff).
  Either way it is a convincing glow on beams/emissive materials/bright billboards. The pure math (`BloomMath`: the
  knee curve, gaussian weight generation, half-res sizing) is headless-tested and mirrors the GLSL bright-pass/blur
  shaders exactly.
- HDR + filmic tonemap: `PixelPostProcessSettings.Hdr` (an `HdrSettings`, **on by default**) renders the internal
  colour chain at float16 (`R16G16B16A16Float`) so shading carries values above 1.0, blooms the over-range
  highlights BEFORE tonemapping, then maps the float scene back to LDR with a filmic `TonemapOperator` (`AcesFilmic`
  default, `Reinhard`/`Clamp` alternates) modulated by `Exposure`. Hot cores desaturate toward white and roll off
  instead of hard-clipping at the UNorm boundary, so every glowing feature (emissive, beams, particles, sky, water
  glints, telegraphs) reads hotter at once. Authored via unclamped `Color` (a `new Color(4f, 2f, 1f)` emissive
  carries four units of energy), no new per-material field. The retro passes (palette quantize, pixelation) and FXAA
  run AFTER the tonemap, so a per-game retro look survives on top of HDR. Default behaviour changed: every consumer's
  render shifts on repin. `Hdr.Enabled = false` restores the exact legacy UNorm chain and pass order BYTE-IDENTICAL
  to the pre-HDR output (golden-proven via `scene3d_hdr_off`). Formats: only the colour targets flip, the
  encoded-normal/linear-depth MRTs, the swapchain, and everything post-blit stay LDR.
  `Hdr.ChromaPreservation` (`0..1`, default `0.75`) controls how much highlight colour survives the roll-off: `0`
  applies the operator per channel (the historical look, hot cores desaturate toward white), `1` maps luminance only
  and rescales RGB so hue is fully preserved (a coloured glow stays chromatic into its core), in between blends. The
  `0.75` default is the user-approved balance from the look-evidence ladder review: saturated preset colours stay
  legible against the filmic roll-off while the hottest cores still read as hot. Hue is preserved except where a
  saturated channel clips at the display ceiling before the rescale, where a partial desaturating shift remains even
  at `1`. At `0` the tonemap short-circuits to the exact per-channel expression, byte-identical to the pre-chroma
  output. The mapping is mirrored headlessly by `Internal.TonemapMath` (kept in sync with the GLSL `TonemapFrag`).
- Water: `Scene3D.DrawWater(in WaterPlane)` (a per-frame request: centre XZ + surface height + XZ half-extents) plus
  `PixelPostProcessSettings.Water` (a `WaterSettings`, **default no request queued = no pass, no cost**) draws an
  opt-in stylized ocean surface. `WaterSettings.WaveSource` picks where the DISPLACEMENT, NORMAL and WHITECAPS come
  from - `WaterWaveSource.Procedural` (the default, everything below) or `WaterWaveSource.FftOcean` - and the
  shading stack is identical either way. Five layers of the procedural path, each independently reachable at zero so
  the previous release's look stays one knob away:
  - **Gerstner swell** (`SwellAmplitude`/`SwellWavelength`/`SwellDirectionDegrees`/`SwellSpreadDegrees`/
    `SwellSteepness`/`SwellSpeed`/`SwellSeed`/`SwellComponents`): a stack of up to eight trochoidal components
    displacing the surface grid in the VERTEX stage, so crests pinch and the surface has a real silhouette. The
    whole stack is generated from those wind scalars, on the CPU (`Internal.GerstnerWaves`) and in the shader,
    rather than uploaded per component. The grid is a fixed 97x97 vertex budget concentrated toward the camera by
    `GridFocusBias` (1 = uniform), since a consumer plane can be 1200 units across.
  - **Analytic sky reflection** (`SkyReflectionStrength`/`SkyReflectionSunStrength`): the fresnel term blends
    toward the sky evaluated along the reflected view ray (`Internal.SkyMath.ShadeDirection`, the same gradient +
    sun the background sky pass paints, in per-direction form) using `PixelPostProcessSettings.Sky`'s palette
    whether or not the sky PASS is enabled. `SkyReflectionStrength = 0` restores the flat `HorizonColor`.
  - **GGX sun glint** (`GlintStrength`/`GlintRoughness`/`GlintDistantRoughness`): a peak-normalized GGX lobe whose
    roughness widens wherever the surface is under-sampled, by camera distance over `DetailFadeDistance` OR by the
    pixel's world footprint against the ripple wavelength, whichever is worse. `GlintRoughness = 0` selects the
    legacy Blinn-Phong lobe on `GlintExponent`.
  - **Depth grading** (`AbsorptionPerMetre` over `DeepColor`/`ShallowColor`): per-channel Beer-Lambert
    transmittance, so the ramp bends through green-teal rather than running straight between two colours. All-zero
    coefficients restore the two-stop smoothstep over `ShallowDepth`.
  - **Foam** (`FoamColor`/`FoamStrength`/`FoamCrestCoverage`/`FoamShoreWidth`/`FoamPatternScale`): procedural, no
    texture assets. Whitecaps from the determinant of the swell's horizontal Jacobian (steepness-normalized, so
    coverage means the same at any steepness) and a shoreline band from the reconstructed depth, both broken up by
    a scrolling pattern thresholded into graphic lobes.

  On top of that the ripple normal field is a generated SLOPE SPECTRUM (14.26.0): `RippleComponents` cosines with
  golden-angle headings (no two parallel, no dominant ribbon direction) laddering by `RippleLacunarity` over about
  five octaves, amplitudes renormalized to a fixed slope variance so `NormalStrength` keeps its meaning, sampled at
  a domain-warped position (`WaveWarpStrength`). Each component is band-limited out of the normal once it falls
  below `FootprintSamples` pixel footprints, and the removed slope variance is transferred into the GGX lobe
  (`VarianceToRoughness`, Toksvig-style) so distant water becomes a rougher surface rather than stripes or glass;
  the swell's shading contrast fades on the same measure. `DetailFadeDistance`/`DistantDetailScale` remain as an
  artistic extra layered on top. Plus the
  depth-sampled shore fade (`ShoreFadeDistance`) and `Opacity`.
  **No refraction, no screen-space reflections, no caustics, no submerged view** (all out of scope) - a stylized
  LDR surface. Drawn after the sky + ground decals, before the MRT resolve, one draw per plane, with depth test ON
  (`Less`, so geometry above the surface occludes it) but depth WRITE OFF (so the resolved normal/linear-depth the
  outline pass reads is untouched - matching the ground-decal/beam/textured-billboard depth-interleave convention).
  Time is driven by the same `Scene3D.EffectTimeSeconds` clock the beam pulse/scroll uses (freeze it for a
  deterministic frame). The depth grading, the shore foam and the shore fade all reconstruct the ground height
  under each water pixel from the resolved scene depth via the same `gl_FragCoord` + raw-inverse-view-projection
  convention the ground-decal pass uses; because that measurement is taken under the DISPLACED surface, a passing
  crest carries the waterline and the foam line up the beach for free. The pure math is `WaterMath` (the ripple
  normal, domain warp, distance detail fade, grid layout and focus warp, absorption, reflection blend, GGX and
  legacy glint, roughness widening, foam), `RippleSpectrum` (the ripple spectrum, the footprint band-limit and the
  variance transfer) and `GerstnerWaves` (the swell), all internal, headless-tested and mirroring the GLSL
  `WaterVert`/`WaterFrag` exactly.
- FFT ocean (`WaterSettings.WaveSource = WaterWaveSource.FftOcean`, `WaterSettings.SeaState`, a `WaterSeaState`):
  a Tessendorf inverse-FFT surface computed on the GPU, replacing the Gerstner swell and the ripple spectrum as the
  displacement/normal/foam source while reusing the whole shading stack above. A TMA (JONSWAP + Kitaigorodskii)
  directional spectrum is baked once per sea-state change on the CPU from wind speed, fetch, depth, spreading and
  swell; every frame it is evolved by the finite-depth dispersion relation and inverse-transformed into
  displacement, slope and Jacobian-foam maps over two or three octave-separated cascades whose wave-number bands
  PARTITION (never overlap), so the cascades sum additively with no double counting. Foam accumulates and
  dissipates over time rather than being a per-fragment fold test. Wave height follows from the sea state and is
  deliberately not a knob, so `Procedural` remains the right answer for a small body of water. Requires
  `GpuCapabilities.SupportsCompute` and degrades to `Procedural` silently without it. Costs exactly one GPU stall
  per frame regardless of cascade count and resolution (about 0.3 ms measured on Metal at the defaults). The pure math
  is `Internal.OceanSpectrum` (headless-tested); the kernels are `Internal.OceanComputeShaders` and the per-frame
  producer is `Rendering.OceanFftProducer`. Rationale: `docs/design/FFT-OCEAN-DESIGN-2026-07-26.md`; attribution:
  `NOTICE.md`.
- FFT ocean sampling frame (since 16.5.0, all opt-in, all defaulting to the exact identity): `OnshoreFocusPoint` /
  `OnshoreFocusStrength` / `OnshoreFocusSectors` aim the local wave heading at a world point, so an island gets
  surf running at it from every azimuth instead of a sea running past it; `CascadeRotationDegrees` turns each
  cascade's lattice so their repeats stop stacking along the same world axes; `DomainWarpMetres` /
  `DomainWarpWavelengthMetres` bend the sampling domain at a wavelength several times the largest tile, which is
  the only lever that reaches the largest cascade's OWN repeat period. None of it touches the spectrum, the
  kernels or the maps - it is all in how they are sampled - so it costs no extra dispatch and no memory. The focus
  is a two-tap blend over a ring of fixed lattice rotations rather than a per-position rotation of the sampling
  coordinate, because the latter is degenerate (it maps the whole plane onto one ray of the map and renders a
  bullseye); above strength 0 the surface takes two cascade samples per stage instead of one, and the sector count
  is free. Pure math is `Internal.OceanFocus` (headless-tested, mirrors the GLSL). Rationale:
  `docs/design/WATER-SAMPLING-FRAME-DESIGN-2026-07-26.md`.
- Modern particle pass: `Scene3D.DrawParticle(in ParticleSprite)` / `DrawParticles(ReadOnlySpan<ParticleSprite>)`
  queue procedural particle sprites that render as ONE premultiplied-alpha instanced draw after the water pass and
  BEFORE the post chain, so additive sprites feed bloom and every sprite flows through the pixel post like meshes.
  A `ParticleSprite` carries position, velocity, size, rotation, colour, a `ParticleShape`, its shape param, life
  norm, seed, velocity `Stretch`, `BillboardBlend`, a `ParticleOrientation`, and a per-sprite `SoftFadeScale`. The
  six procedural shapes are SDF/noise in the fragment shader (no atlas): `SoftGlow` (soft gaussian disc, a premium
  take on the legacy blob), `Ember` (hot core + warm halo with a subtle flicker), `Spark` (streak along local X,
  pairs with velocity stretch), `Wisp` (noise-eroded smoke that dissolves at its edges with life instead of fading
  uniformly), `Ring` (soft annulus for shockwaves and impact rings), `Star` (four-point glint for sparkles). A sprite can
  instead play an authored flipbook: set `ParticleSprite.Flipbook` (a `ParticleFlipbook` naming an atlas
  `TextureHandle`, its `Columns` x `Rows` grid, an optional motion-vector sheet + `MotionStrength`, and `Loop`)
  and a continuous `FlipbookFrame` (integer part = cell, fractional part = blend to the next, motion-vector warped
  when a motion sheet is bound, else a plain cross-fade). Flipbooks are additive over the procedural shapes,
  selected per-sprite, and a sprite that leaves `Flipbook` default renders byte-identically to the procedural path
  (a 1x1 dummy atlas + neutral motion sheet keep procedural runs in the same one pipeline).
  `ParticleOrientation` is `CameraFacing` (default) or `FlatGround` (the quad lies in the XZ plane, for shockwave
  rings and ground glows). The whole queue sorts back-to-front and BOTH blend modes interleave in the one stream,
  because the fragment premultiplies colour and zeroes the alpha lane for additive sprites. Depth state is test
  LessEqual / no write against the resolved scene depth, and a soft depth fade (`Scene3D.ParticleSoftFade`, world
  units, default 0.35, 0 disables the fade and its texture work) dims a sprite as it approaches geometry, scaled
  per sprite by `SoftFadeScale`. The fade is skipped for flat-ground sprites (they lie coplanar with the floor the
  fade measures against, so it would erase a ground ring's near/far arcs at a grazing angle; `SoftFadeScale` is
  ignored there). `Scene3D.ParticleQuality` (`Full`/`Reduced`, host-set, not cleared by `Begin`) drops the second
  noise octave and the ember flicker on weak GPUs. The pass obeys the engine one-UBO rule: a single set-0 frame
  uniform, every per-sprite value on an instanced vertex-attribute stream, and the textures at set 0 bindings 1..5
  in the order they are sampled (scene depth + its sampler at 1 and 2, then the motion, atlas, and atlas sampler at
  3, 4, 5 for flipbook playback), the Metal-safe pattern the ground-decal pass proves. The untextured
  `DrawBillboard(Vector3, float, Color, BillboardBlend)` overlay remains the LEGACY particle path (post-post,
  unoccluded, always crisp, still fully supported for on-top markers), and the textured `DrawBillboard(TextureHandle, ...)`
  overloads remain the artist-texture path. The turn-key `ParticleSystem`/`ParticleEffectPlayer` mapping lives in
  `KhaozEngine.Particles.Render3D`. See `docs/USING-KHAOZENGINE.md`.
- Screen-space distortion: `Scene3D.DrawDistortion(in DistortionSprite)` / `DrawDistortions(ReadOnlySpan<DistortionSprite>)`
  queue heat-haze / refractive-shockwave / splash-lens sprites that WARP the pixels behind them instead of drawing
  over them. The queue accumulates a signed screen-space offset field (a lazily allocated half/quarter-res
  `R16G16Float` target) as ONE instanced draw with the modern particle pass's quad-expansion + depth-occlusion
  recipe, and the post chain's FIRST pass re-samples the resolved scene colour through that field, so the warp
  precedes every camera-response pass (bloom halos follow the warped sources, the tonemap and retro palette see the
  warped image). Three `DistortionShape`s (`Ripple` shockwave rings, `Heat` upward-scrolling wobble, `Lens` radial
  bulge, sign chooses magnify/pinch). `DistortionSprite.Strength` is the magnitude dial, converted to a UV
  excursion and clamped to a small maximum so stacked sprites cannot smear the whole screen. The apply pass
  preserves each pixel's own alpha, so the transparency background marker never warps. The starfield stopped being
  that marker: since 11.9.0 stars are ordinary scene content drawn before the post chain (see Background above),
  so a ripple over the void now warps the stars behind it too. `Scene3D.DistortionQuality`
  (`Full`/`Reduced`, host-set, not cleared by `Begin`) drops the second heat noise octave and renders the offset
  field at quarter res instead of half. Zero cost when unused: a frame that queues no distortion sprite allocates
  nothing, runs no extra pass, and is byte-identical to before distortion existed. The turn-key `ParticleLook.Distortion`
  mapping lives in `KhaozEngine.Particles.Render3D`. See `docs/USING-KHAOZENGINE.md`.
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
- Manifest relative paths resolve to the native separator: manifests author forward-slash paths
  (`"sub/rock_a.glb"`), and `AssetManifest`'s internal `ResolveFile` normalizes that segment to
  `Path.DirectorySeparatorChar` before combining with the base directory, so a resolved path is never a
  mixed `\`/`/` form on Windows.
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
- Far LOD variants (author-supplied): a manifest entry may declare an optional `"lodFile"`
  (`AssetEntry.LodFile`, resolved against the manifest dir like `file`, null when absent), a hand-authored
  low-poly glTF for the prop. `PropLoader.LoadPropLodAuto(AssetEntry, PropValidation?) ->
  IReadOnlyList<GltfMeshPart>?` loads it exactly as `LoadPropAuto` loads the full mesh (the `Textured` flag
  chooses textured parts vs a flat part), normalized to the SAME `HeightMeters` so the runtime swap is
  size-stable, and returns **null** when no `lodFile` is declared (the kit then has no variant and keeps its
  full mesh at every distance). Upload the returned parts like the full mesh into a parallel LOD set that
  `KhaozEngine.Terrain.PropLayer` carries (`LodMeshes`/`LodPartMeshes` at `LodDistance`), where
  `PropRenderer` swaps to them past the distance. **The engine ships no mesh decimator, so `ke-propbake` does
  NOT generate this** - the far mesh is authored by hand and placed beside the full one.
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
  `IIsoCamera3D.WorldToScreen`, distance-cull, not depth-tested). `WorldToScreen` and every `Draw`/`Resolve`
  below have two overloads: framebuffer ints for a framebuffer-space pass, and an `IDesignViewport` overload
  for a design-space HUD pass (a `SpriteBatch.Begin(IDesignViewport)`). The `IDesignViewport` overload remaps
  NDC onto `WindowBounds` (the whole window, letterbox bars included), exact on any window aspect. Calling
  the int overload with design dims instead is only correct when the window happens to match the design
  aspect, which is the defect the `IDesignViewport` overloads exist to close:
  - `WorldLabel.Draw(...)` - a single centred name floating above a world point (text only). `WorldLabel.ShouldCull`
    exposes the distance predicate render-free.
  - `NameplateRenderer.Draw(...)` - the MMO-style plate that supersedes `WorldLabel`: a rounded panel (`DrawRounded`
    on a white texture) holding a centred title and stacked `NameplateBar`s (health/resource meters). Data-driven
    via the `Nameplate` model (title + `Bars` list, add more bars without a rewrite) and styled by `NameplateStyle`
    (`.Default` = the opaque unified plate, `.TextOnly` = the panel-less name-only preset for the `Text` tier).
    `NameplateLayout.Measure` is the pure, GPU-free panel-size math (headless-testable); `NameplateBar.Fraction` is
    clamped 0..1 at draw; `NameplateRenderer.ShouldCull` shares `WorldLabel`'s cull. No per-frame heap allocation.
    Opt-in edge-aware placement via `NameplateStyle.EdgeBehavior` (a `NameplateEdgeBehavior`: `None` is the
    default, byte-identical to before):
    `Clamp` insets the plate into the viewport by `EdgeMargin` on both axes, `Deflect` also moves it beside the
    anchor on top overflow instead of clamping down over the anchor's face, with an `EdgeHysteresis` band so a
    plate near the threshold never flips between above and beside frame to frame. `NameplatePlacement.Place` is the
    pure, GPU-free placement math behind both modes (headless-testable, like `NameplateLayout.Measure`), and the
    stateful `Draw` overload threads a caller-held `NameplatePlacementState` (one per plate) so `Deflect`'s
    hysteresis survives across frames (both the framebuffer-int and `IDesignViewport` `Draw` overloads carry a
    stateless and a stateful form). Opt-in presentation tiers via `NameplateTiers.Resolve(focusPixel, onScreen,
    distance, viewportWidth, viewportHeight, in config, pinned, ref state)` (or the `IDesignViewport` overload, its
    focus ellipse normalized over `WindowBounds`), a pure per-entity resolver picking
    `NameplateTier.Hidden` / `.Text` / `.Full` from a `NameplateTierConfig` distance ladder (`FullDistance`,
    `TextDistance`, both with a derived hysteresis band) and a normalized centre-ellipse look-at gate
    (`FocusRadius`, also derived), with a caller `pinned` override forcing `Full`. Same enter-at-edge, exit-past-band
    stability contract as the placement hysteresis above. `NameplateTierState` is the caller-held per-entity state
    (one per plate, exposing `Tier`). `.TextOnly` pairs with `NameplateTier.Text`: zero panel alpha, zero border,
    zero `MinBarWidth`, a black `TitleShadow`, drawn against a `Nameplate` carrying no `Bars`.

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
