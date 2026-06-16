# Changelog

All notable changes to KhaozEngine. The 4.x MonoGame-based packages share one version
(`Directory.Build.props` `<Version>`); the 5.x experimental custom-stack (MonoGame-free) packages share a
second version (`Directory.Build.props` `<KhaozEngine5xVersion>`). See the post-MonoGame plan in
`docs/ROADMAP.md`.

## 5.27.0-experimental (custom 5.x line)

P0 hardening, stage 3 — graphics-backend seam, **phase 3c of 4**. **Render3D is now fully off Veldrid**, and
`Frame.Commands` is the engine command list — so both renderer packages run entirely on `KhaozEngine.Gpu`.
Behaviour unchanged (both goldens pixel-identical, 3D scene visually confirmed).

### KhaozEngine.Render3D (migrated; Veldrid dropped)

- **No longer references Veldrid.** `Scene3D`, `ModelRenderer` (instanced model pass + MRT), `PixelPostProcess`,
  `RenderResources`, `LineRenderer`, `BillboardRenderer`, `Render3DSurface`, `Render3DSnapshot`, and the standalone
  `Render3DHost` are all rewritten against the `KhaozEngine.Gpu` interface; the `Veldrid`/`Veldrid.SPIRV`/
  `Veldrid.StartupUtilities` (and the now-unneeded Newtonsoft) package references are removed. The full pipeline
  state (MRT, instancing, depth/raster/blend, the post-process chain, the debug-line + billboard overlays) is
  preserved. `Render3DHost` now delegates its window/loop to `AppWindow`.

### KhaozEngine.Windowing / KhaozEngine.Gpu

- `Frame.Commands` is now an `IGpuCommandList` (the transitional `Frame.GpuCommands` + the `GpuCommandLists.Wrap`
  bridge are removed). `AppWindow` no longer exposes the Veldrid `GraphicsDevice`/`Swapchain` — its public GPU
  surface is `GpuDevice`/`Backend`/`Capabilities`, and it drives the loop through the engine command list. The
  SDL2 window + input pump remain on `Veldrid.Sdl2` (the windowing/input platform layer — abstracting SDL2 is a
  separate future item). `KhaozEngine.Gpu`'s public device factories no longer expose Veldrid's
  `GraphicsDeviceOptions` (kept internal), so creating a device touches no Veldrid type.

## 5.26.0-experimental (custom 5.x line)

P0 hardening, stage 3 — graphics-backend seam, **phase 3b of 4**. The full engine-owned GPU abstraction lands
and **Render2D is migrated onto it (Veldrid dropped from Render2D entirely)**. Behaviour unchanged (both
goldens pixel-identical).

### KhaozEngine.Gpu

- **Full GPU interface + Veldrid implementation**: `IGpuDevice`/`IGpuResourceFactory`/`IGpuCommandList` + the
  resource handles (`IGpuBuffer`/`Texture`/`Sampler`/`Framebuffer`/`Pipeline`/`ResourceLayout`/`ResourceSet`/
  `ShaderSet`), engine-owned descriptions + 16 `Gpu*` enums, all mapped 1:1 to Veldrid inside `Internal/`. Veldrid
  is now hidden behind this interface (a future Silk.NET backend becomes a new `IGpuDevice` impl). Covers the
  full surface both renderers use, so phase 3c migrates Render3D against the same interface. A gated `[GpuFact]`
  smoke test exercises buffer+texture+pipeline+draw+readback on the device. Plus `GpuCommandLists.Wrap(...)` — a
  transitional bridge presenting a window's frame command list as an `IGpuCommandList` until phase 3c retypes
  `Frame.Commands`.

### KhaozEngine.Render2D (migrated; one fewer dependency)

- **No longer references Veldrid.** `SpriteBatch`/`Render2DCore`/`Render2DSurface`/`Render2DSnapshot`/`Texture2D`/
  `SpriteFont` are rewritten against the `KhaozEngine.Gpu` interface; the `Veldrid`/`Veldrid.SPIRV` package
  references are removed. `AppWindow` now also exposes `GpuDevice` + `Frame.GpuCommands` for 2D consumers
  (Render3D still uses the Veldrid path until 3c). Submission order, scissor, and the persistent vertex buffer
  are preserved; the 2D golden passes pixel-identical.

## 5.25.0-experimental (custom 5.x line)

P0 hardening, stage 3 of 3 — the graphics-backend seam, **phase 3a of 4** (foundation). See
`docs/ENGINE-AUDIT-5x-2026-06-16.md` + `docs/superpowers/specs/2026-06-16-gpu-backend-seam-design.md`. Behaviour
on Metal is unchanged (both goldens pass pixel-identical).

### KhaozEngine.Gpu (NEW package)

- **Backend-seam foundation**: `GpuBackendKind`, `GpuCapabilities` (clip-Y / depth-range, read from the device),
  `GpuBackendSelector.Select()` (probe `RuntimeInformation` → Metal on macOS / Direct3D11 on Windows / Vulkan on
  Linux, with a `KE_GRAPHICS_BACKEND` env override; the core logic is a pure `Select(env, os)` overload that is
  headless-tested), and `GpuDeviceContext` factories (`CreateWindow`/`CreateHeadless`) that own device creation
  behind the selector. This is the first of four phases: it centralizes the previously hard-coded
  `GraphicsBackend.Metal` (removed from `AppWindow`, `Render3DHost`, and both snapshot helpers), and plumbs the
  device capabilities — without yet wrapping the GPU resource types (phases 3b/3c rewrite the renderers against
  engine-owned GPU interfaces so Veldrid stops appearing on any public API; 3d migrates consumers).

### Windowing / Render2D / Render3D

- Device creation now routes through `KhaozEngine.Gpu`; `AppWindow` exposes `Backend`/`Capabilities`. The
  clip-Y/depth derivation from `GpuCapabilities` is marked for phase 3c (behaviour identical on Metal for now).

## 5.24.0-experimental (custom 5.x line)

P0 hardening, stage 2 of 3 (see `docs/ENGINE-AUDIT-5x-2026-06-16.md`): submission performance. Internal
rewrite; `Scene3D.Draw`/`SpriteBatch.Draw` public APIs unchanged. Guarded by the stage-1 golden-snapshot net
(both 3D + 2D goldens pass pixel-equivalent).

### KhaozEngine.Render3D (perf)

- **GPU instancing.** The model pass no longer uploads a UBO + issues a draw per instance. Per-frame uniforms
  (view-projection, lights, camera) live in a 176-byte UBO uploaded once per frame; per-instance data (model
  matrix, tint, emissive, specular) moves to an instanced vertex stream uploaded once per frame; each UNIQUE
  mesh draws once with `instanceCount`. A 200-object board goes from ~200 UBO uploads + 200 draws to 1 UBO
  upload + ~(unique-mesh) instanced draws. (The previous per-instance ceiling was ~150-300 objects.)

### KhaozEngine.Render2D (perf)

- **Persistent SpriteBatch vertex buffer.** `SpriteBatch.Flush` no longer creates+disposes a GPU buffer and
  allocates a managed array per texture-run every frame; it uploads sub-ranges into one persistent growable
  buffer (uploaded directly from the run's backing storage, no `ToArray`). Removes the worst per-frame
  allocation/driver-churn hot spot in 2D.

## 5.23.0-experimental (custom 5.x line)

P0 hardening, stage 1 of 3 (see `docs/ENGINE-AUDIT-5x-2026-06-16.md`): correctness net + low-risk fixes. No
public API change.

### KhaozEngine.Render3D / Render2D / Gui (fixes + perf)

- **Fixed** a `ResourceLayout` GPU-resource leak in `ModelRenderer` (now stored + disposed like the other
  renderers).
- **Perf**: hoisted the invariant `SetPipeline`/`SetGraphicsResourceSet` binds out of the per-instance model
  loop (one bind per pass, not per instance); cached the post-process palette scratch array (was a 260-float
  allocation every frame); `ScreenStack.Update` reuses a scratch list instead of allocating a `Screen[]` every
  frame. (The per-instance UBO upload — the real 3D scaling ceiling — is stage 2.)
- **Mesh winding made consistent**: a new winding-vs-normal test net (applied to every `MeshPrimitives` shape)
  found that Cylinder/Cone/Pyramid-base/Sphere wound their triangles opposite their own outward normals (two
  conflicting conventions). Flipped those generators so winding is uniformly CCW-outward. Render-neutral today
  (`FaceCullMode.None`; positions + normals unchanged) but unblocks enabling back-face culling later.

### KhaozEngine.Tests

- **Golden-snapshot GPU regression net**: gated `[GpuFact]` tests render fixed 3D + 2D scenes through the
  offscreen snapshot path and compare a downsampled colour grid to committed references with tolerance —
  catching shader/blend/UBO/winding/orientation regressions that headless tests and `FaceCullMode.None`
  cannot see. Skipped by default (and on GPU-less CI); run with `KE_GPU_TESTS=1`, re-bake with
  `KE_UPDATE_GOLDENS=1`.

## 5.22.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive; one internal format change)

- **More mesh primitives**: `MeshPrimitives.Torus`, `Capsule`, `RoundedBox`, `Plane` (subdividable flat grid)
  join Box/Tile/Cylinder/Cone/Pyramid/Wedge/Sphere — smooth normals on curved surfaces, degenerate-arg
  clamping, CCW-outward winding.
- **UV texture coordinates**: `ModelVertex` gains a `Vector2 Uv` (vertex now 48 bytes) and every primitive
  generates sensible UVs (per-face for flats, cylindrical for cylinder/cone, lat/long for sphere, etc.);
  `MeshBuilder` carries UVs through, `GltfLoader` reads `TEXCOORD_0`. The model shader passes the UV through
  (it is not yet sampled — textured-mesh *rendering* is a later step; this makes the geometry data ready so
  primitives don't need re-touching then). Existing meshes are unaffected (the 3-arg `ModelVertex` ctor
  defaults UV to zero). Render verified unchanged after the vertex-format change.
- **`MeshOps`**: `WithSmoothNormals(mesh, epsilon)` welds vertices by position and averages normals (smooth a
  faceted mesh); `RecomputeFlatNormals(mesh)` for per-triangle face normals. Both return copies.

## 5.21.0-experimental (custom 5.x line)

### KhaozEngine.Particles (NEW package)

- **Particle simulation** (pure, MonoGame/Veldrid-free — System.Numerics + BCL only): `ParticleSystem`
  (capacity-bounded pool, swap-remove compaction, contiguous `Active` span), `EmitterConfig` (lifetime/speed
  ranges, cone `Direction`+`SpreadDegrees`, gravity, drag, start/end size + colour, `Spark`/`Puff` presets),
  `Particle`, and a `RateAccumulator` for continuous emission. Fully **deterministic** — an internal xorshift32
  RNG seeded per system, no `System.Random`/`DateTime`/wall-clock — so two systems with the same seed + calls
  produce identical particles (headless-testable). Render-agnostic: a game splats `system.Active` to any
  renderer.

### KhaozEngine.Render3D (additive)

- **Camera-facing billboards** for displaying particles (and any sprite-in-3D): `Scene3D.DrawBillboard(worldPos,
  size, color, BillboardBlend.Alpha|Additive)` draws a soft round disc (smoothstep falloff in the shader, no
  texture) facing the camera, composited over the post image like the debug lines. Alpha for smoke/puffs,
  additive for glowing sparks/flashes (pairs with the 5.19 emissive look). The camera basis is computed once
  per frame. Render3D deliberately does NOT depend on KhaozEngine.Particles — the game loops `Active` and calls
  `DrawBillboard` per particle. Snapshot-verified (additive spark burst + alpha puff over a lit scene). From
  the Hardpoint testbed.

## 5.20.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **Debug line/wireframe overlay**: immediate-mode `Scene3D.DebugLine/DebugRay/DebugBox/DebugGrid/DebugAxes/
  DebugCircle` draw coloured lines on top of the post-processed image with the camera's view-projection (depth
  disabled, alpha-blended overlay). For dev viz and in-game cues — tower range rings (`DebugCircle` on the
  ground), flow-field arrows, board grids, bounds, RGB axis gizmos. Segments accumulate per frame and clear in
  `Begin()` (same lifecycle as instances). The geometry builders live in a pure, headless-tested
  `DebugShapes` (Box/Grid/Circle/Axes). Backed by an internal `LineRenderer` (LineList pipeline). Snapshot-
  verified (grid + box wireframe + ground ring + axes + line over a lit scene). From the Hardpoint testbed.

## 5.19.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **Lighting + materials**: the model pass gains a second **fill light** (`PixelPostProcessSettings.
  FillLightDirection`/`FillLightColor`, a dim cool default that softens shadowed sides) on top of the existing
  key light, **Blinn-Phong specular** highlights, and **emissive** self-illumination — driven by a new
  per-instance `Material` (`Emissive`, `Specular` strength, `Shininess`) with `Material.None` (matte,
  the prior look), `Material.Glowing(color)`, and `Material.Shiny(strength, shininess)`. (The glow factory is
  `Glowing` rather than `Emissive` because that name is taken by the property.) `Scene3D.Draw(mesh, world,
  tint, material)` and a `MeshInstance.Material` field (additive, default matte) carry it; the
  `Scene3DBinder` scene overload applies it. The shader now also receives the camera eye for the specular
  view vector. Default look (matte materials, dim fill) is unchanged. Snapshot-verified (matte vs shiny vs
  emissive spheres + fill on form). From the Hardpoint testbed.

## 5.18.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **Procedural mesh primitives.** `MeshPrimitives` gains `Cylinder`, `Cone`, `Pyramid`, `Wedge`, and `Sphere`
  alongside the existing `Box`/`Tile`, all returning a `GltfMesh` with white vertex colour and CCW outward
  winding. `Cylinder(radius, height, segments, capped)` and `Cone(...)` seat their base at y=0 along +Y with
  smooth radial side normals and flat center-fan caps (`capped:false` drops the caps). `Pyramid(baseSize,
  height)` and `Wedge(size, height)` are flat-shaded solids (square-based pyramid; right-triangular prism ramp
  rising -Z->+Z). `Sphere(radius, rings, segments)` is a UV sphere centered at the origin with smooth radial
  normals. Degenerate args clamp (`segments>=3`, `rings>=2`).
- **`MeshBuilder`** composes transformed, optionally re-coloured `GltfMesh` parts into a single mesh, so a game
  can build a multi-part, multi-colour silhouette in code and draw it as one tinted instance. `Add(part,
  transform)` keeps the part's colours; `Add(part, transform, color)` bakes a colour onto the appended verts.
  Positions transform by `Vector3.Transform`; normals by the inverse-transpose of the linear 3x3 (correct under
  non-uniform scale, falling back to the raw linear part if non-invertible) then re-normalized; indices offset
  by the running vertex count. `Build()` throws if the total exceeds the `ushort` vertex ceiling (65535).
  Fluent. `VertexCount`/`IndexCount` expose the running totals.

## 5.17.0-experimental (custom 5.x line)

### KhaozEngine.Gui (additive)

- **Immediate-mode UI surface**: `GuiSurface` lets a game running a single `window.Run(frame => ...)` loop
  author a HUD-over-3D and full-screen menus with one call site per widget instead of hand-rolling
  `SpriteBatch` fills + per-widget `Pointer.BlockRegion` bookkeeping. `Begin(batch, pointer)` (the `batch` may
  be `null` for headless tests) then `Panel`/`Swatch`/`Label` (positioned or box-aligned via `GuiAlign`) and
  `Button(...) -> bool` (hover/press/disabled/selected visuals, fires on the press-origin `IsTapIn` invariant).
  `PointerCaptured` reports whether the pointer's press-origin landed on any widget this frame, centralizing the
  click-through gate that keeps a tap on a button from leaking to the world. `GuiStyle` carries the default
  palette (matching the retained `Button`) and is overridable per call or on the surface. Draws through the
  existing `GuiDraw` primitives and reuses the caller's begun batch so it composes with the design viewport for
  free. Headless-tested (interaction + capture, no GPU); demoed in `GuiSample`'s Immediate screen. From the
  Hardpoint testbed (the flagged immediate-mode-Gui engine-first candidate).

## 5.16.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **ECS->Scene3D binding**: render-component types `Transform3D` (position/scale/rotation; zero scale/rotation
  treated as identity) and `MeshInstance` (`MeshHandle` + `Vector4` tint; zero tint = white), plus
  `Scene3DBinder.Submit(world, scene)` which draws every entity carrying both. Replaces the per-game
  "query entities -> compute matrix -> Draw" loop with one call. The pure core
  `Submit(world, Action<MeshHandle,Matrix4x4,Vector4>)` is headless-tested with a real `World` + a recording
  delegate. Render3D now references the MonoGame-free `KhaozEngine.Ecs`. From the Hardpoint testbed.

## 4.11.0 (MonoGame 4.x line)

- **`KhaozEngine.Content.ColorHex`**: `FromHex(string) -> Vector4` (RGBA 0..1; accepts `#RRGGBB` / `RRGGBB` /
  `#RRGGBBAA`, leading `#` optional, missing alpha = opaque) and `ToHex(Vector4) -> #RRGGBBAA`. A
  MonoGame-free, Veldrid-free home for parsing config colour strings, usable by both the pure domain and the
  render stack (it lives in Content because games already reference it for config and it has no GPU deps).
  Headless-tested. (Centralizes a hex-colour helper games were hand-rolling; from the Hardpoint testbed.)
  Shared 4.x version bumped 4.10.0 -> 4.11.0.

## 5.15.0-experimental (custom 5.x line)

### KhaozEngine.Render3D (additive)

- **`IsoCamera3D.Frame(center, size, margin = 1.1f)`**: aim the camera at a bounds center and size `OrthoSize`
  so an axis-aligned bounds fits the viewport (projects the 8 corners into view space, fits both axes against
  the current `AspectRatio`/`Zoom`). Replaces the per-game "OrthoSize = max(w,h)*spacing*k" guesswork with a
  correct fit. Pure math, headless-tested (tight fit at margin 1, slack with margin > 1, wide-aspect). From
  the Hardpoint testbed (board framing).

## 5.14.0-experimental (custom 5.x line)

Engine maturity from the Hardpoint 3D testbed: per-instance tint + code-built mesh primitives, so one mesh
draws in many colors and games stop hand-rolling primitive geometry.

### KhaozEngine.Render3D (additive)

- **Per-instance tint**: `Scene3D.Draw(MeshHandle, Matrix4x4 world, Vector4 tint)` (the existing
  `Draw(mesh, world)` defaults to white = no tint). The tint multiplies the lit colour in the model shader
  (a `vec4 Tint` added to the model UBO; `SceneInstances.Instance` carries it). Lets a single white mesh be
  drawn in many colours instead of one mesh per colour.
- **`MeshPrimitives`**: `Box(size)` and `Tile(size, thickness)` build `GltfMesh` cubes/flat-tiles in code
  (24 verts / 36 indices, per-face normals, white vertex colour for tinting), no asset files. Headless-tested
  (vertex/index counts, corner positions, tile base at y=0). Verified visually (one box drawn in three tints).

## 5.13.1-experimental (custom 5.x line)

### KhaozEngine.Render3D (fix)

- **3D scenes rendered vertically upside-down.** `ModelRenderer` multiplied the camera view-projection by a
  clip-Y flip (`M22 = -1`), which inverted the image (world-up landed at the bottom) AND disagreed with
  `IsoCamera3D.ScreenToGround` picking, which uses the *unflipped* matrix. The flip was invisible on symmetric
  content (a sphere, a starfield, a symmetric instance grid) and only showed up on the first asymmetric scene
  (an iso game board). Removed the flip: the view-projection is uploaded as-is, so the render is right-side up
  and consistent with picking. Verified with an asymmetric two-sphere snapshot (world-up now maps to
  screen-up). No API change.

## 4.10.0 (MonoGame 4.x line)

- **`KhaozEngine.Ecs` is now MonoGame-free**: dropped the unused `MonoGame.Framework.DesktopGL` package
  reference (the ECS source uses no Xna types; its only dependency, `KhaozEngine.Serialization`, is pure BCL).
  This lets the custom MonoGame-free 5.x stack reuse the same ECS. No API or behaviour change; existing 4.x
  consumers are unaffected (they carry their own MonoGame reference). Shared 4.x version bumped 4.9.0 -> 4.10.0.

## 5.13.0-experimental (custom 5.x line)

Render3D grows from a single-model demo into a scene a game can use (Phase A of the Hardpoint 3D vertical
slice): many instances per frame, screen->ground picking, and composition into an `AppWindow` alongside a
Render2D HUD.

### KhaozEngine.Render3D

- **Multi-instance `Scene3D`** (breaking vs the demo API): `LoadMesh(GltfMesh) -> MeshHandle` (load several
  meshes), then per frame `Begin()` + `Draw(MeshHandle, Matrix4x4 world)` to queue instances. The old
  single-model `LoadModel`/`Spin` is removed; instances are drawn through the iso camera + `PixelPostProcess`
  in one pass. `SceneInstances` (the instance queue) is headless-tested.
- **`IsoCamera3D` picking**: `ScreenToGround(screenPixel, viewportW, viewportH, groundY = 0)` and
  `ScreenToRay(...)` (returns the new `Ray` struct) unproject a screen pixel into the world. Pure /
  headless-tested (round-trips a known ground point; screen-centre maps to the camera target).
- **`Render3DSurface`**: binds a `Scene3D` to a `KhaozEngine.Windowing.AppWindow` and renders into the
  window's per-frame command list, so a 3D scene composes into the same window as a `Render2D` HUD (3D fills
  the frame, the HUD draws on top). Mirrors `Render2DSurface`; adds a Render3D->Windowing reference.
- `Scene3D.RenderInternal` now records into a caller-supplied `CommandList` + target `Framebuffer` (the
  caller owns Begin/End/Submit); `Render3DHost` and `Render3DSnapshot` drive that path. `ModelRenderer.Draw`
  split into `BeginModelPass` (clear once) + `DrawInstance` (per instance). `Render3DSnapshot` gains a
  multi-instance `Capture(width, height, setup, drawFrame, frames)` overload (verified a 3x3 instance grid).
- `Render3DSample` now submits a grid of instances instead of spinning one model.

## 5.12.0-experimental (custom 5.x line)

Native packaging (milestone 3), part 1: bundle openal-soft so audio no longer depends on the deprecated
macOS system OpenAL.

### KhaozEngine.Audio

- Reference `Silk.NET.OpenAL.Soft.Native` (1.23.1) and create the API with `AL.GetApi(true)` /
  `ALContext.GetApi(true)` (the openal-soft library-name container). The native ships RID-specific
  (linux-arm/arm64/x64, osx-arm64/x64, win-arm64/x64/x86) and flows to the consuming app's `runtimes/<rid>/
  native/` via the runtime graph. Verified on osx-arm64: the process now loads the bundled
  `libopenal.dylib`, not `/System/Library/Frameworks/OpenAL.framework` (deprecated), with the audio device
  opening cleanly. Deviceless CI still falls back to `NullMusicBackend` as before.

### Known gap (SDL2)

- SDL2 is still sourced on macOS by a per-sample `CopySdl2` MSBuild target that copies a Homebrew-installed
  `libSDL2.dylib` (Veldrid.SDL2 4.9.0 bundles only osx-x64, no osx-arm64/linux), so a clean macOS checkout
  still needs `brew install sdl2`. A proper bundled SDL2 across RIDs is folded into the cross-platform-backends
  milestone (it shares the Windows/Linux native-coverage work and can't be run-verified on the Apple-Silicon
  dev box).

## 5.11.0-experimental (custom 5.x line)

Input breadth, part 2: gamepad + touch state on `InputState`. Additive and non-breaking. The state model is
headless-tested; a *live* gamepad smoke needs a physical controller (the SDL polling is best-effort and
compile-verified, defensive on every call) and touch is mobile (the type + any mapping stay testable).

### KhaozEngine.Windowing (additive)

- `GamepadState` + `GamepadButton`: immutable per-frame pad snapshot (button down/pressed/released sets, two
  analog sticks raw, two triggers), with `IsDown`/`WasPressed`/`WasReleased` and radial-deadzone stick
  helpers. `GamepadState.Disconnected` is the not-connected sentinel.
- `Deadzone.Radial(stick, deadzone)`: shared magnitude-based deadzone (rejects small diagonal drift as a
  whole, rescales the remainder so the edge maps to 0 and full tilt to 1).
- `TouchPoint` + `TouchPhase`: a touch point (stable id, position, phase). Empty on desktop; mobile fills it.
- `InputState` gains `Gamepads` / `Touches` (default empty) plus `Gamepad(index)` (returns
  `GamepadState.Disconnected` when absent) and `PrimaryGamepad`. The existing 10-arg constructor is unchanged
  (the new parameters are optional), so all current call sites keep compiling.
- `AppWindow` polls SDL2 game controllers each frame via a defensive `SdlGamepadPoller` (every SDL call
  guarded; degrades to no pads on any failure, never affecting the window loop). `WindowingSample` shows a pad
  readout and lets the left stick nudge the box / A reset it.

## 5.10.0-experimental (custom 5.x line)

Input breadth, part 1 (milestone 2 of engine maturity): pause/time-scale and a gesture seam. All additive in
`KhaozEngine.Windowing`, MonoGame-free, and headless-tested. Gamepad + touch state land next (part 2).

### KhaozEngine.Windowing (additive)

- `GameClock`: 5.x-native clock separating real delta from a scaled simulation delta, driven by a raw
  `float` dt (`AppWindow.Frame.Dt`). `TimeScale` (slow-mo / normal / fast-forward), `Pause`/`Resume`
  (orthogonal to scale), `RealDeltaSeconds`/`ScaledDeltaSeconds`, `ElapsedRealSeconds`/`ElapsedScaledSeconds`
  accumulators, and `Paused`/`Resumed` edge events. The custom-stack analogue of the 4.x
  `KhaozEngine.Time.GameClock` (which is MonoGame-coupled via `GameTime`).
- `GestureRecognizer`: single-pointer tap / long-press / drag from raw (isDown, position, dt) frames or a
  `Pointer`. Per-frame flags (`Tapped`, `LongPressed`, `DragStarted`/`DragEnded`) plus `IsDragging`,
  `DragDelta`/`DragTotal`/`DragStart`; tunable `MoveThreshold`/`TapMaxDuration`/`LongPressDuration`. Feed it
  the design-space `Pointer.Position` so gestures match scaled/letterboxed draws; use real (unscaled) dt.
- `PinchRecognizer`: two-point pinch -> relative `Scale`, per-frame `ScaleDelta`, midpoint `PanDelta` +
  `Center`. Headless-testable; live it needs two touch points (mobile).
- `WindowingSample` now demos drag/tap/long-press and a `GameClock` (Space pauses, 1/2/3 set speed) on a
  `DesignViewport`.

## 5.9.1-experimental (custom 5.x line)

### KhaozEngine.Render2D (fix)

- `SpriteBatch` scissor clipping now composes with a design viewport. Two bugs: the clip rect passed to
  `SetScissor` was treated as window points even under `Begin(IDesignViewport)` (so it ignored the design
  scale + letterbox offset), and the clip helpers re-`Begin()`ed in screen space to resume after the scissor
  (throwing away the design transform, so clipped content drew unscaled at raw design coordinates). Now
  `SetScissor`/`ClearScissor` **flush internally and preserve the active transform** (no surrounding `Begin`
  needed), and a clip rect is mapped through the active viewport. New pure overload
  `ComputeScissor(rect, IDesignViewport?, ...)` is headless-tested. Visible symptom: on the resized `GuiSample`
  Widgets screen the scrollable list drew unscaled and escaped its panel.

### KhaozEngine.Gui (fix)

- `ScrollablePanel.BeginClip`/`EndClip` are now one-liners over `SetScissor`/`ClearScissor` (they no longer
  `End()`+`Begin()` around the scissor, which was the source of the lost-transform bug). Clipped content under
  a `DesignViewport` now scales and clips correctly.

## 5.9.0-experimental (custom 5.x line)

Resolution independence + layout (milestone 1 of engine maturity). The window already resized the
framebuffer; this adds the missing design layer so content scales, centers, and letterboxes instead of
sitting at hard pixel coordinates. Additive across Windowing/Render2D/Gui.

### KhaozEngine.Windowing (additive)

- `DesignViewport` + `IDesignViewport`: a fixed design space (e.g. 960x540) mapped onto the current window
  with a `ScaleMode` (`Fit` = letterbox/pillarbox centered, `Fill` = cover/crop centered, `Stretch` =
  distort). Exposes `ScaleX/Y`, `OffsetX/Y`, `ContentBounds`, `DesignBounds`, `ScreenToDesign`/`DesignToScreen`,
  and `GetClipProjection(viewportW, viewportH)` for the batch. Pure math, headless-tested.
- `Pointer.Update(InputState, IDesignViewport)`: maps the cursor into design space so all bounds helpers
  hit-test in the same coordinates draws use (press-origin click-through invariant preserved). The existing
  `Update(InputState)` is unchanged (identity). In-window guard still uses the raw window position.

### KhaozEngine.Render2D (additive)

- `SpriteBatch.Begin(IDesignViewport)`: draw in design coordinates; scaling, centering, and letterbox happen
  for free. Mirrors `Begin(Camera2D)`. Existing `Begin()` / `Begin(Camera2D)` unchanged.

### KhaozEngine.Gui (additive)

- `Layout.Resolve(parent, Anchor, width, height, marginX, marginY)`: pure anchor-based rect placement
  (`TopLeft`..`BottomRight`, `Center`, `Stretch`) against the design viewport or a container, so widgets stop
  hard-coding absolute pixels. Headless-tested.
- `ScreenStack.Update(dt, InputState, IDesignViewport)`: routes the pointer through the design viewport.
- `Screen.BackgroundColor` + `Screen.DrawBackground(batch, white, viewport)`: opaque full-screen fill
  convention for non-modal screens (fixes screens showing the one below through their gaps).
- `GuiSample` now drives a `DesignViewport(960, 540, Fit)`: resize the window and the UI scales, centers, and
  letterboxes, with hit-testing aligned and opaque backgrounds on the full screens.

## 5.8.1-experimental (custom 5.x line)

### KhaozEngine.Render2D (fix)

- `SpriteBatch` now preserves **submission order across textures**. It previously grouped all quads globally
  per texture and flushed those groups in first-seen order, so a draw issued later could paint *under* or
  *over* the wrong layer whenever textures interleaved (text vs. solid-fill rectangles). Visible symptom: a
  menu's text bled through a modal panel drawn on top of it, and in-screen overlays (dropdown popup, tooltip)
  could land beneath later fills. Quads are now coalesced into submission-ordered *runs* — only consecutive
  same-texture draws merge — so painter's order is correct. Pure run-coalescing logic is headless-tested
  (`QuadRunBuilder`); no API change.

## 5.8.0-experimental (custom 5.x line)

The heavy `KhaozEngine.UI` widgets ported onto the custom stack: `Dropdown`, `TextInput`, `Tooltip`,
`PopupPanel`, `ScrollablePanel` in `KhaozEngine.Gui`, plus a scissor-clip capability in `KhaozEngine.Render2D`
and a headless `TextEntry` helper. Game-specific coupling from the 4.x versions (VirtualResolution,
LayoutConstants, nav/top-bar assumptions) was dropped — these are clean generic widgets.

### KhaozEngine.Render2D (additive)

- `SpriteBatch` gains **scissor clipping**: `SetScissor(Rect)` / `ClearScissor()` (call between an `End` and the
  next `Begin`) clip subsequent draws to a viewport-space rect. `ComputeScissor(...)` is a pure, unit-tested
  helper that scales viewport points to framebuffer pixels (DPI / Retina aware) and clamps to the framebuffer.
  The pipeline now enables the scissor test (default = full framebuffer, so unclipped draws are unaffected).

### KhaozEngine.Gui (additive)

- `TextEntry` — headless text-entry helper: maps a frame's `InputState` key presses (+ shift, US layout) to
  typed characters and applies them to a string (append printable, Backspace deletes), with max-length and a
  char filter. No SDL text-input plumbing, so it is fully unit-testable. (No IME/locale/dead-keys.)
- `TextInput` — single-line field: tap to focus / tap-out to blur; while focused, typed keys edit the text
  (via `TextEntry`); bordered field with placeholder + blinking caret. Ported from the 4.x `UI.TextInput`
  (which hooked SDL's TextInput event).
- `Dropdown` — selector with a trigger + an option list that opens below; tap to open/select, release-outside
  dismisses. Two-phase draw (`Draw` trigger inside any clip, `DrawOverlay` the open list last/unclipped).
- `Tooltip` — auto-sized floating bubble; `ComputeBounds(...)` is a pure layout function (sizes to content,
  sits above the anchor, flips below when it would cross the top margin, clamps into the viewport) testable
  with a fake `ITextMeasurer`. `Show`/`Hide`/`Draw` instance API.
- `PopupPanel` — modal dialog: scrim, centered auto-sized panel (clamped between a min height and a viewport
  fraction), title bar, label/value content rows (`PopupRow` Header/Stat/Spacer), and a footer dismiss button
  (+ optional primary action). `Update` blocks the pointer over the panel. (No internal scroll — that is
  `ScrollablePanel`.)
- `ScrollablePanel` — vertically-scrolling fixed-height list: wheel (while hovering) + drag scroll, clamped to
  range; the owner draws rows positioned via `ItemBounds` between `BeginClip`/`EndClip` (which set/clear the
  SpriteBatch scissor); `TappedItemIndex` hit-tests a row (gaps return -1). Ported from the 4.x
  `UI.ScrollablePanel` (clipping now via the engine scissor instead of MonoGame's).
- Headless tests cover all of the above logic (`TextEntryTests`, `TextInputTests`, `DropdownTests`,
  `TooltipTests`, `PopupPanelTests`, `ScrollablePanelTests`, plus `SpriteBatchScissorTests` for the DPI scissor
  math) — 40 new, 752 green. `GuiSample` gains a "Widgets" screen driving the dropdown, text field, scrollable
  list, hover tooltip, and a modal popup. NOTE: this stack is Metal-only and was built without a display, so the
  GPU scissor clip itself is not yet visually verified (the scroll logic + pixel math are).

## 5.7.0-experimental (custom 5.x line)

Core `KhaozEngine.UI` widgets ported onto the custom stack: `Label`, `Panel`, `Slider`, `Toggle` in
`KhaozEngine.Gui`, plus a device-free text-layout helper in `KhaozEngine.Render2D`.

### KhaozEngine.Render2D (additive)

- `ITextMeasurer` — a text-measurement seam (`LineHeight` + `Measure(string)`) implemented by `SpriteFont`.
  Lets the layout math be unit-tested headlessly with a fake measurer (no GPU device / real font).
- `TextLayout` — pure word-wrap + alignment helpers over `ITextMeasurer` (`AlignedX`, `Wrap`,
  `MeasureWrappedHeight`), plus pixel-snapped draw overloads taking a `SpriteBatch` + `SpriteFont`
  (`DrawAligned`, `DrawWrapped`) and a `TextAlign` enum. Ported from the 4.x MonoGame-bound `UI.TextHelper`.

### KhaozEngine.Gui (additive)

- `Label` — non-interactive text widget: aligned (left/center/right) and optionally word-wrapped within its
  bounds, vertical-centered for single lines. Pure presentation over the (tested) `TextLayout`.
- `Panel` — filled, optionally-bordered container/backdrop; `BlocksPointer` reserves its region on the
  `Pointer` (via `BlockRegion`) so a layer beneath can skip hit-testing under it (modal scrims/popups).
- `Slider` — horizontal slider over `Pointer`; the bounds are the track. A press that begins inside starts a
  drag and jumps the value to the pointer (clamped 0..1), tracking until release; a press that began elsewhere
  is ignored (press-origin invariant). `Update` returns whether the value changed.
- `Toggle` — two-state switch; a valid tap (press + release both inside, the click-through invariant) flips
  `IsOn` and fires `OnChanged`. Drawn as a track with a thumb that slides to the on/off side.
- Internal `GuiDraw` fill/border helpers (1x1-white-texture rects) shared by the widgets.
- Headless tests cover the layout math (`TextLayoutTests`), the slider drag/clamp/press-origin behaviour
  (`SliderTests`), the toggle flip + click-through (`ToggleTests`), and panel pointer-blocking (`PanelTests`).
  `GuiSample`'s settings screen now drives a `Panel`, `Label`s, a volume `Slider` (with live readout), and a
  fullscreen `Toggle`. The heavier widgets (ScrollablePanel/Dropdown/TextInput/PopupPanel) are a follow-up batch.

## 5.6.0-experimental (custom 5.x line)

New `KhaozEngine.Gui` package — the screen-stack + first widget on the custom stack.

### KhaozEngine.Gui (new)

- `ScreenStack` — owns a stack of `Screen`s and routes input top-to-bottom: the first visible,
  non-passthrough screen that reports consuming input blocks the screens below it; a modal
  (`PassUpdateThrough == false`) screen also stops them updating; `AlwaysReceivesInput` opts back in. Draws
  bottom-to-top and drives transitions. Exposes a shared `Pointer` + `InputState`. Ported faithfully from the
  MonoGame `ScreenManager` (uses `dt` instead of `GameTime`; the click-through layering model is intact).
- `Screen` — base UI surface: `Update(dt, receivesInput)` (return whether it consumed input) + `Draw(SpriteBatch)`,
  with `DrawOrder`/`PassUpdateThrough`/`AlwaysReceivesInput`/transitions/`ExitScreen`.
- `Button` — bounds-aware widget over `Pointer.IsTapIn` (press-origin click-through invariant), hover/press
  visuals. Built on `KhaozEngine.Windowing` + `KhaozEngine.Render2D`.
- Headless `ScreenStackTests` cover the routing core (consume-blocks-lower, modal-stops-lower,
  AlwaysReceivesInput, transition-on, animated exit). `GuiSample` shows a menu that pushes a modal settings
  screen. Pause/timescale, per-player scoping, touch gestures, and the wider widget set
  (Slider/Dropdown/ScrollablePanel/...) are follow-ups.

## 5.5.0-experimental (custom 5.x line)

Bounds-aware pointer input (the click-through core) in `KhaozEngine.Windowing`, and the renderer windowing
consolidates onto `AppWindow`.

### KhaozEngine.Windowing

- `Pointer` — a bounds-aware pointer over the mouse with the **press-origin click-through invariant**, ported
  from the MonoGame `InputManager` core. `Update(InputState)` per frame, then hit-test with `IsTapIn`,
  `IsPressingIn`, `IsHoveringIn`, `IsPointerIn`, `IsReleasedOutside`, `IsDraggingIn`/`GetDragDelta`,
  `IsTapFromTo`, plus region blocking (`BlockRegion`/`IsBlocked`) for overlay click-through. New `Rect`
  type for hit-testing. Headless `PointerTests` cover the invariant (press-outside-release-inside is not a
  tap). Touch/gamepad/pinch/menu-nav and virtual-resolution transforms are still follow-ups.

### KhaozEngine.Render2D (cleanup)

- Removed the standalone `Render2DHost` + its own `Key`/`FrameInfo` (superseded by `AppWindow` from
  Windowing). Draw into a window via `Render2DSurface(AppWindow)`; `Render2DSnapshot` (headless) is
  unchanged. `Render2DSample` now uses `AppWindow`. Render2D dropped its direct `Veldrid.StartupUtilities`
  reference (windowing comes from `KhaozEngine.Windowing`). The `WindowingSample` gained a clickable button
  demonstrating `IsTapIn` + region-blocking.

## 5.4.0-experimental (custom 5.x line)

New `KhaozEngine.Windowing` package — the shared windowing + input foundation — and Render2D integrates with it.

### KhaozEngine.Windowing (new)

- `AppWindow` — owns the SDL2/Metal window, Veldrid device + swapchain, and the frame loop. `Run(onFrame)`
  clears + presents around the callback; each `Frame` exposes `Dt`, an engine-native `InputState`, and the
  GPU command list to draw into. `Device`/`MainSwapchain` are the advanced GPU boundary (the only Veldrid in
  the API).
- `InputState` — per-frame keyboard + mouse snapshot: keys down/pressed/released, mouse position/delta,
  mouse buttons, scroll, window size; `IsDown`/`WasPressed` helpers over engine-native `Key`/`MouseButton`
  enums. No MonoGame. (Headless `InputStateTests`.) Gamepad/touch and the rich gesture/`InputManager` layer
  are follow-ups.
- **Render2D integration:** `Render2DSurface(AppWindow)` builds a `SpriteBatch` + texture/font loaders on the
  window's device, so a consumer draws a 2D scene into `AppWindow` frames (see `WindowingSample`). Render2D
  now references Windowing. `Render2DCore` gained an `ownsDevice` flag so a borrowed (window-owned) device
  isn't double-disposed.

Follow-up noted: Render2D still ships its own standalone `Key`/`FrameInfo`/`Render2DHost`; these will fold
into the Windowing path so there's one window/input layer (the `WindowingSample` aliases around the overlap).

## 5.3.1-experimental (custom 5.x line)

- **Fix (`KhaozEngine.Audio`):** `AudioSystem`'s default constructor no longer throws when no OpenAL
  implementation / audio device is available (headless CI, servers, machines without sound) - it falls back
  to a silent `NullMusicBackend` and logs a warning. A real device still gets the OpenAL backend. (This was
  red on the Linux CI runner, which has no OpenAL.)

## 5.3.0-experimental (custom 5.x line)

`KhaozEngine.Audio` **graduates from the 4.x MonoGame line to the 5.x custom stack** (the first existing
package to graduate; the 4.x MonoGame Audio is frozen at its last 4.x version, still pinnable by current
consumers). `Render3D` and `Render2D` roll to 5.3.0 with no functional change.

### KhaozEngine.Audio (now MonoGame-free)

- Backend swapped to a cross-platform **OpenAL streaming backend** (`OpenAlMusicBackend`, Silk.NET.OpenAL):
  decodes **WAV / OGG (NVorbis) / MP3 (NLayer)** and streams via queued buffers, pumped from
  `AudioSystem.Update()`. The MonoGame and macOS-AVAudioPlayer backends are removed; no `Microsoft.Xna`
  reference remains.
- **Breaking API (intended for the 5.x graduation):** `IMusicBackend.TryLoadTrack` drops its
  `ContentManager` parameter (`TryLoadTrack(contentDirectory, trackName)`) and gains `Update()`;
  `AudioSystem.LoadContent(ContentManager)` becomes `LoadContent(string contentDirectory)` (the folder
  holding the audio files; track names are file names without extension). The rest of `AudioSystem`
  (volume, enable, rotation, `PlayMode`, `TrackChanged`) is unchanged.
- `AudioSystem` logic stays covered by the headless `AudioSystemTests` (fake backend); real OpenAL
  streaming is eyeball/spike-verified (can't run on the CI audio-less runner). Needs an OpenAL impl at
  runtime (macOS ships one; bundle openal-soft for production). Music-only; SFX is a future layer.

## 5.2.0-experimental (custom 5.x line)

New `KhaozEngine.Render2D` package, and the 5.x line becomes a **shared** version (was per-package for the
first two Render3D releases).

### KhaozEngine.Render2D (new)

- New package: 2D rendering on the custom MonoGame-free foundation (Veldrid + SPIR-V, `System.Numerics`).
  `SpriteBatch` (batched textured quads, alpha blend + tint, per-texture batching; quads transformed to clip
  space on the CPU so there is no per-batch uniform), `Camera2D` (position/zoom/rotation, headless +
  unit-tested), `Texture2D` (PNG load via `StbImageSharp`), `SpriteFont` (runtime TrueType text - glyph atlas
  via `stb_truetype` - with `DrawString`/`Measure`). `Render2DHost` owns the SDL2/Metal window + frame loop +
  input; `Render2DSnapshot` captures headless. Veldrid stays internal; deps
  (Veldrid/Veldrid.SPIRV/StbTrueTypeSharp/StbImageSharp) confined to the package. Proves 2D + text on the
  custom stack (de-risks the 2D game migration). Metal-only for now.

### 5.x line now shared

- The 5.x custom-stack packages now share `Directory.Build.props` `<KhaozEngine5xVersion>` (both
  `Render3D` and `Render2D` reference it). They release together under one `vX.Y.Z-experimental` tag, ending
  the per-package tag collisions. `Render3D` rolls `5.1.0 -> 5.2.0-experimental` with no functional change.

## KhaozEngine.Render3D 5.1.0-experimental

Polish pass on the experimental renderer (additive; default look unchanged).

- **Procedural starfield** behind the model (`PixelPostProcessSettings.Starfield`, default on), composited in
  the final pass. Background is flagged by the color target's alpha (model writes alpha 1, the clear sets 0)
  and preserved through the palette/edge passes - keeps the blit to a safe binding count (a depth texture in
  the blit tripped a Veldrid/Metal multi-resource binding bug).
- **Second test model**: a lumpy low-poly `asteroid.glb` (noise-perturbed icosphere) alongside the planet,
  proving the loader handles arbitrary glTF geometry. The sample switches models (Space), zooms (W/S), and
  toggles the starfield (A).
- **`Newtonsoft.Json` pinned to 13.0.3** in the package to override the vulnerable transitive 9.0.1 from
  `Veldrid.SPIRV` (clears NU1903).
- **Sample runs without env vars**: `Render3DSample` auto-copies the system SDL2 (Homebrew) into its output as
  `libsdl2.dylib`, so `dotnet run --project Render3DSample` works without `DYLD_FALLBACK_LIBRARY_PATH`.

## KhaozEngine.Render3D 5.0.0-experimental (new, independent 5.x line)

First package of the post-MonoGame custom engine (see `docs/ROADMAP.md`). EXPERIMENTAL. Versions
independently of the shared 4.x line via its own csproj `<Version>`; ships nothing that changes existing
packages. Proven on Apple Silicon (Metal) at net10.0.

### KhaozEngine.Render3D (new)

- New package: real-time stylized 3D on a **custom MonoGame-free renderer** - `Veldrid` (GPU) +
  `Veldrid.SPIRV` (GLSL -> SPIR-V -> MSL/HLSL/GLSL at load, compiled natively, no Wine) + `SharpGLTF`
  (runtime glTF load). Math is `System.Numerics`. All three deps are confined to this package; no
  `Microsoft.Xna.Framework` reference.
- `IsoCamera3D` / `IIsoCamera3D`: orthographic isometric camera (no perspective). Configurable
  `Azimuth` (default 45 deg), `Elevation` (default `atan(0.5)` ~= 26.57 deg, the 2:1 iso look), `Target`,
  `OrthoSize`, `Zoom`, near/far. Exposes `View` / `Projection` / `ViewProjection` (`Matrix4x4`). Headless,
  unit-tested.
- `GltfLoader` / `GltfMesh`: load a `.glb`/`.gltf` at runtime (SharpGLTF) into a welded-normal mesh.
- `Scene3D`: a camera + one model + a `PixelPostProcessSettings`. Renders the lit model into an internal
  render target, runs the post chain, presents. `Render3DHost` owns the SDL2/Metal window + frame loop +
  engine-native input (`Key`/`FrameInfo`); Veldrid stays fully internal. `Render3DSnapshot` captures a
  scene offscreen to a CPU RGBA buffer (headless, for tooling/tests).
- Directional "sun" lighting with smooth diffuse or **cel** shading. `PixelPostProcess` chain, every stage
  independently toggleable: palette quantization (swappable `Palette`/`Palettes`), 4x4 Bayer dither,
  depth/normal-edge silhouette outline, point-or-linear upscale, configurable internal resolution and
  background. Default settings target a smooth, stylized space look; flipping the toggles gives the
  chunky retro/pixel look.
- Known limitations (POC): Metal backend only (clip-Y flip + MRT-clear handling are Metal-specific, gated
  for a future per-backend pass); `Render3DHost` needs SDL2 on the loader path (`brew install sdl2`);
  `Veldrid.SPIRV` pulls a transitive `Newtonsoft.Json` flagged `NU1903` (build-time). GPU rendering is
  eyeball-verified (the sample / `Render3DSnapshot`); only the camera math has CI unit tests.

## Tools

Repo utilities under `tools/`. Not packages: never versioned, packed, or tagged.

### PixelLabSheetAssembler

- New offline tool (`tools/PixelLabSheetAssembler`, `IsPackable=false`). Assembles a PixelLab
  character export (zip or dir) plus an animation name into one `Direction8` grid sheet PNG for
  `PixelLabSpriteLoader.FromGridSheet`: 8 rows in `Direction8` order, N frame columns, uniform cell
  size, feet-on-baseline anchoring (opaque-bbox bottom), and hold-previous (or hold-next for a
  leading gap) missing-frame tolerance with warnings. Prints the `frameCount` and suggested `fps`.
  Uses SixLabors.ImageSharp 2.1.13 (Apache-2.0); no MonoGame/GraphicsDevice. See its README.

## KhaozEngine 4.9.0

Additive. New zero-dependency package; no source change for existing consumers. The `4.8.0` move put
the channel-split contract in `KhaozEngine.Netcode`, but that package depends on
`MonoGame.Framework.DesktopGL` (its `UnitAxisQuantizer`/`IPredictedState` use `Vector2`/`MathHelper`),
so a MonoGame-free, web-server-shared DTO project still could not implement the contract without
dragging MonoGame + native SDL in. This release extracts the contract into a package with no
dependencies at all.

### KhaozEngine.Netcode.Abstractions (new)

- New package, **zero NuGet dependencies** (BCL only: no MonoGame, no LiteNetLib, no UDP transport).
  `IChannelSplittable<TSelf>` and the `NetChannelReliability` enum now physically live here. A batch
  DTO in a MonoGame-free, transport-agnostic project (e.g. a contracts assembly referenced by an
  ASP.NET leaderboard server) references **only** this package to implement the contract.
- **Namespace stays `KhaozEngine.Netcode`** (assembly name `KhaozEngine.Netcode.Abstractions` differs
  deliberately), so no consumer needs a `using` change.

### KhaozEngine.Netcode (changed, non-breaking)

- Takes a package dependency on `KhaozEngine.Netcode.Abstractions` and adds assembly-level
  `[TypeForwardedTo(typeof(IChannelSplittable<>))]` + `[TypeForwardedTo(typeof(NetChannelReliability))]`.
  Type-forwards **work here**: the full type name is unchanged and only the assembly moved (unlike the
  4.8.0 namespace move, which forwards could not bridge). Anyone referencing `KhaozEngine.Netcode`
  keeps compiling and binding both types with no change.
- `KhaozEngine.Netcode.LiteNetLib`'s `ChannelSplitter` references the contract transitively; its
  `Send<T>`/`ToDeliveryMethod` still use `LiteNetLib.DeliveryMethod` and stay put.

Guards: a new test project `KhaozEngine.Netcode.Abstractions.DecouplingTests` references **only**
`KhaozEngine.Netcode.Abstractions` and implements the contract on a dummy struct (compiling proves the
contract needs no MonoGame and no transport; a reflection test asserts the declaring assembly
references neither `MonoGame.Framework` nor `LiteNetLib`). The existing
`KhaozEngine.Netcode.DecouplingTests` stays green and now also asserts the types resolve through the
type-forwards to the Abstractions assembly. No shipped consumer references these types yet (SpaceGame
is the intended first adopter via `EntityUpdateBatchDto`).

## KhaozEngine 4.8.0

Breaking change shipped as a minor bump: the `5.x` line is reserved for the experimental branch, so
this breaking namespace move ships as `4.8.0` rather than `5.0.0`. Pin deliberately if you implement
the moved contract.

- **`IChannelSplittable<TSelf>` and the `NetChannelReliability` enum moved from
  `KhaozEngine.Netcode.LiteNetLib` to `KhaozEngine.Netcode`** (namespace
  `KhaozEngine.Netcode.LiteNetLib` -> `KhaozEngine.Netcode`). Both are pure: the interface is just
  the `Has*/Extract*` members, the enum is two values, and neither names a LiteNetLib type. Moving
  them lets a batch DTO that lives in a transport-agnostic project (e.g. one shared with a web
  server) implement the split contract without pulling a UDP transport into that project.
- **`ChannelSplitter` stays in `KhaozEngine.Netcode.LiteNetLib`** (its `Send<T>` orchestration and
  `ToDeliveryMethod` genuinely use `LiteNetLib.DeliveryMethod`). `KhaozEngine.Netcode.LiteNetLib`
  now has a package dependency on `KhaozEngine.Netcode` for the moved types.
- **`KhaozEngine.Netcode` still has no LiteNetLib dependency** (only MonoGame). A dedicated test
  project (`KhaozEngine.Netcode.DecouplingTests`) references only the core package and implements
  `IChannelSplittable<T>` on a dummy struct; it compiling is the standing guard that the contract
  stays transport-free.

No type-forwards: `[TypeForwardedTo]` redirects the *assembly* for an unchanged full type name, so
it cannot bridge a *namespace* change. No shipped consumer references these types yet (all consumers
on 4.0.0; netcode unadopted), so nothing breaks in practice. Migration for any code that used them
is a one-line `using` swap:

```csharp
// before
using KhaozEngine.Netcode.LiteNetLib;   // IChannelSplittable<T>, NetChannelReliability, ChannelSplitter
// after
using KhaozEngine.Netcode;              // IChannelSplittable<T>, NetChannelReliability
using KhaozEngine.Netcode.LiteNetLib;   // ChannelSplitter (keep only if you call Send/ToDeliveryMethod)
```

## KhaozEngine 4.7.0

Additive. Two new packages extracting SpaceGame's reusable netcode. No change to existing packages.

### KhaozEngine.Netcode (new)

- New package: game-agnostic, transport-free netcode primitives (refs MonoGame for `Vector2`/`MathHelper`).
- `UnitAxisQuantizer`: 8-bit quantization of a unit-range `[-1,1]` axis to a signed byte and back
  (`Quantize` clamps then rounds `*127` away-from-zero; `Dequantize` is `v/127f`). The game keeps its
  own command record + packed field layout. Determinism: this rounding is sim-hash-relevant for any game
  that dequantizes commands before its host-authoritative deterministic sim, so the scheme is fixed.
- `ClientPrediction<TState,TCommand>`: client-side prediction + authoritative reconciliation. Seq-keyed
  pending-command buffer with oldest-drop bound, ack-prune, rebase to an authoritative basis + replay of
  unacknowledged commands, and decaying render-offset error smoothing with hard-snap and dead-zone. Game
  supplies `IPredictedState<TSelf>` (Position + WithPosition) and `ITickSimulator<TState,TCommand>`
  (one deterministic step); tunables via `PredictionSettings` (`PredictionSettings.Default` = 60 Hz,
  256-command buffer, 100u snap, rate 8, 1.5u dead-zone). Returns `ReconciliationResult`. State type is
  `struct`-constrained.
- `RemoteCommandQueue<TCommand>`: host-side per-slot, seq-ordered command queue. Dedups duplicate
  `(slot,seq)` and negative seqs, returns a caller-supplied neutral command for an empty slot, tracks
  the last-acknowledged seq per slot. Determinism-neutral (orders/dedups only).

### KhaozEngine.Netcode.LiteNetLib (new)

- New package: LiteNetLib channel-split kernel (refs `LiteNetLib 2.1.2`).
- `IChannelSplittable<TSelf>` + `ChannelSplitter.Send`: split a batch into its unreliable
  (position/transient, latest-wins) and reliable (spawns/destroys/events) parts and send each non-empty
  part on its own channel (Sequenced vs ReliableOrdered) so reliable events never head-of-line-block
  position updates. `NetChannelReliability` + `ChannelSplitter.ToDeliveryMethod` expose the mapping. The
  game keeps its own batch DTO and field layout.

## KhaozEngine 4.6.0

Additive. New package `KhaozEngine.Updates`. No change to existing packages.

### KhaozEngine.Updates (new)

- New package centralizing a game-agnostic **delta auto-update pipeline** (promoted from SpaceGame so
  Hardpoint/Nullwake can reuse it). Determinism-neutral (never touches sim/RNG). Pure .NET
  (+ `KhaozEngine.Diagnostics`), no MonoGame dependency.
- `UpdateManifest` - SHA256 file manifest (`path`/`sha256`/`size`, ordinal-sorted, stable camelCase
  JSON wire format). `GenerateFromDirectory(dir, version, platform)` builds one from an install dir
  (also usable by an offline publish-side manifest generator); `ComputeDiff(local, remote)` returns
  `FilesToDownload` + `FilesToDelete` + `TotalDownloadBytes`.
- `IUpdateSource` - host-agnostic transport. `HttpUpdateSource` is the default (HTTP against a
  configurable `ServerBaseUrl` + `LatestVersionPath` template; files resolved as siblings of the
  manifest - SpaceGame's Azure Blob layout, but a game points it elsewhere via config or implements
  the interface for any backend). `LatestVersionInfo` carries version/build/manifest-url/required.
- `UpdateService` - the check -> download -> apply state machine (`UpdateState`), with resumable
  staging (already-staged files with a matching SHA256 are skipped; corrupt downloads retry up to
  `MaxDownloadRetries`), boot hygiene (stale-staging cleanup, interrupted-apply detection), and
  offline-safe checks (failures fall back to `Idle`). Shim launch and process exit are injectable via
  `UpdateServiceOptions`, so the whole lifecycle is headless-testable. `Platform`/`InstallDir` default
  to the current OS runtime id / `AppContext.BaseDirectory`.
- `UpdateApplier` + `IUpdaterEnvironment` - the cross-platform **staged-apply core** for an external
  updater shim: wait for the game to exit, back up each install file before overwriting, copy with
  retries for locked files, roll every overwrite back on any failure (install never left half-new),
  abort before touching the install if a staged source is missing, delete removed files, install the
  new manifest, clear the macOS quarantine attribute, relaunch. All side effects go through
  `IUpdaterEnvironment` (`SystemUpdaterEnvironment` is the real impl); a game's shim is just
  `UpdateApplier.Run(args, new SystemUpdaterEnvironment(log))`.
- `ApplyUpdateConfig` is the `apply-update.json` handoff contract; it (de)serializes through a
  source-generated `UpdatesJsonContext`, so the shim needs no reflection and stays trim/AOT safe.
- 46 headless tests (manifest diffing, resume skip/retry, download verification, apply / rollback /
  abort).

## KhaozEngine 4.5.0

Additive. Two new packages of game-agnostic 2D primitives, ported verbatim from SpaceGame.

### KhaozEngine.Collision (new package)

- New package: deterministic 2D collision + broadphase primitives. Refs `MonoGame.Framework.DesktopGL`
  for `Vector2`. Float math and iteration order are bit-identical to the SpaceGame originals
  (`CircleCollision`, `EnemySpatialIndex`) so it can be adopted in a lockstep sim without moving the hash.
- `CircleCollision` (static): `Intersects(Vector2, float, Vector2, float)` and `Intersects(ICircleCollider,
  ICircleCollider)` broad overlap (`DistanceSquared <= combined^2`, touching counts), plus three
  `DoCollidersCollide` overloads (collider/collider, bare-circle/collider, collider/bare-circle) that apply
  per-pixel precise refinement when a side implements `IPreciseCircleCollisionTarget`.
- `ICircleCollider` (`Position`, `Radius`) and `IPreciseCircleCollisionTarget` (`IntersectsCircle`).
- `SpatialHashGrid`: uniform spatial hash for broadphase. Generic rebuild via `BeginRebuild(capacity)` +
  `Add(index, position, radius)` per item (replaces the snapshot-coupled `Rebuild`), then
  `QueryCandidates(center, radius)` / `GetQueryIndex(i)` / `SortQueryIndicesAscending(count)`. Cell coord =
  `(int)MathF.Floor(world / cellSize)`, queries walk Y-outer/X-inner, cell chains are LIFO (head insertion).
  Renamed off "Enemy"; stores caller-supplied indices into whatever collection the caller owns.

### KhaozEngine.Pooling (new package)

- New package: `ObjectPool<T>` where `T : class, IPoolable`, a fixed-capacity free-list pool genericized
  from SpaceGame's `XpFlyerPool` (XpFlyer specialization + `Update`/`Draw` dropped). Zero dependencies.
- O(1) `Rent()` (null when exhausted) / `Return(item)` (resets, ignores foreign items), `Clear()`,
  `ActiveCount`/`FreeCount`, and `GetActive(slot)` over a swap-removal-compacted active set. `IPoolable`
  exposes `PoolIndex` (pool-owned) + `Reset()`.

## KhaozEngine 4.4.0

Additive. New package `KhaozEngine.Platform` for native platform interop. No change to existing packages.

### KhaozEngine.Platform (new)

- New package: game-agnostic native platform interop, pure BCL P/Invoke, no MonoGame dependency.
- `Clipboard`: cross-platform system-clipboard facade. `TryGetClipboardText()` / `TrySetClipboardText(string)`
  dispatch SDL2 first, then a macOS `NSPasteboard` fallback, then an optional Android/iOS bridge.
  `TrySetClipboardImagePng(byte[])` covers macOS + mobile; `TrySetClipboardImageRgba32(w, h, rgba)` writes a
  bottom-up `CF_DIB` on Windows. Every call is best-effort and never throws (a missing/failing backend
  yields `""` / `false`).
- `Clipboard.MobileBridgeTypeName`: fully-qualified type name of the consumer's mobile clipboard bridge,
  resolved by reflection across loaded assemblies (static `TryGetClipboardText(out string)` /
  `TrySetClipboardText(string)` / `TrySetClipboardImagePng(byte[])`). Defaults to `null` (mobile fallback
  skipped); reassigning clears the resolution cache. This replaces the hard-coded bridge type name in the
  promoted-from source, so consumers register their own bridge.
- Ported verbatim from SpaceGame's `ClipboardInterop` (the SDL2 / Windows GDI / macOS Objective-C / mobile
  marshaling is unchanged); the dispatch/fallback ordering and the `CF_DIB` packing are extracted into pure
  helpers and covered by headless tests. The native bridges themselves can't run headless.

## KhaozEngine 4.3.1

Bugfix. No API change.

### KhaozEngine.Audio

- `MacOsMusicBackend.TryLoadTrack` now locates the built track file by probing the formats the
  content pipeline actually emits (`.ogg`, `.mp3`, `.m4a`, `.wav`, `.aiff`, `.caf`), preferring
  `.ogg`. It previously looked only for a raw `.mp3` on disk, but the DesktopGL pipeline transcodes
  music to `.ogg` (the `.xnb` is just a header that references it), so every track failed to load and
  no music played. AVAudioPlayer decodes the built `.ogg` directly.
- The native AVAudioPlayer bridge is now created lazily on first playback instead of in the
  constructor, so track loading is headless-testable on non-macOS CI.

## KhaozEngine 4.3.0

Additive. Completes the isometric toolkit's picking + extensibility seams from 4.2.0. No behaviour
change for existing 4.2.0 calls.

### KhaozEngine.Graphics

- `IsometricProjection.ScreenToWorld(screen, z)`: inverts the projection on the horizontal plane at
  height `z` (not just the ground). `ScreenToWorld(screen, 0)` equals `ScreenToGround`. This is the
  building block for picking over varying terrain - a consumer that owns the heightmap tests candidate
  heights front-to-back; the toolkit supplies the per-plane inverse.
- `IIsometricProjection` interface, implemented by `IsometricProjection`. Consumers can depend on the
  seam and substitute a fake/stub projection in headless tests (mirrors `Input.IDesignViewport`).
- `IsoDepth.DepthKey` gains an optional `zWeight` (default 1): scales how strongly height pushes a
  drawable toward the front, so a tall stack can be made to sort in front of a taller-but-nearer
  neighbour, or `zWeight: 0` drops height from ordering. Existing 4-argument calls are unchanged.

## KhaozEngine 4.2.0

Additive. A render-only isometric toolkit in `KhaozEngine.Graphics`, plus an opt-in footprint
anchor on the directional sprite draw path. No gameplay/grid/pathfinding concepts: consumers keep
their own world model and project at draw time. Orthographic consumers are unaffected (the only
signature change is a trailing optional parameter).

### KhaozEngine.Graphics

- `IsometricProjection`: configurable 2:1-style tile footprint (default 64x32) and `heightScale`
  (defaults to tile height). `WorldToScreen(wx, wy, z = 0)` maps world to screen
  (`sx = (wx - wy) * TileWidth/2`, `sy = (wx + wy) * TileHeight/2 - z * HeightScale`);
  `ScreenToGround(screen)` inverts on the ground plane (`z = 0`), returning a continuous world
  point for picking. `z` is a real input now (v1 callers pass 0) - the seam for terrain height.
- `IsoDepth.DepthKey(wx, wy, z = 0, layer = 0)` returns a comparable `IsoDepthKey` for Y-sorting a
  draw list: primary order `wx + wy + z`, integer `layer` as tiebreak. The consumer sorts its own list.
- `PrimitiveRenderer.DrawIsoDiamond` (filled 2:1 tile), `DrawIsoBlock` (top + two shaded side faces
  for a given height), `DrawIsoEllipse` (filled 2:1, for shadows) and `DrawIsoEllipseOutline`
  (stroked 2:1, for range rings). Match the existing pixel-quad rendering style.
- `ColorHelper.Scale(color, factor)`: per-channel RGB multiply (alpha kept), clamped - used for the
  default block face shading.

### KhaozEngine.Sprites

- `SpriteAnchor` enum and a new optional `anchor` parameter on `DirectionalAnimatedSprite.Draw`
  (default `Center`, unchanged). `FootprintBottomCenter` anchors the draw position at the frame's
  bottom-centre so a tall iso sprite stands on its (z-lifted) tile instead of being centred on it.
  An explicit `origin` still overrides the anchor. Facing/`Direction8` logic is unchanged.

## KhaozEngine 4.1.0

Additive. Logging normalization: packages that log now lean on the logger's category (already
rendered by `LogFormatter` as `[Category]`) instead of hand-rolled message prefixes, and fall back
to the ambient `Log` facade when no `ILogger` is injected. Two more packages gain logging where it
earns its keep. No public type removed; on-disk formats unchanged.

### KhaozEngine.Audio

- Log messages drop the redundant `Audio:` prefix across `AudioSystem` and the three backends. The
  category already identifies the source (`AudioSystem`, `MonoGameMusicBackend`, `MacOsMusicBackend`,
  `MacOsMusicPlayer`), so the prefix was doubling up. No behavior change beyond log text.

### KhaozEngine.Persistence

- `SaveEncoder`, `PersistenceQueue`, and `SettingsManager<T>` drop inline `[ClassName]` message
  prefixes and now resolve a logger via `?? Log.For<T>()` (the generic `SettingsManager<T>` uses the
  fixed category `SettingsManager` to avoid a `` `1 `` suffix). They log under their own category
  whether or not a logger is injected.
- `SaveEncoder`'s `logger` constructor argument is now **optional** (`ILogger? logger = null`); a null
  logger no longer throws, it falls back to the ambient facade. Callers passing a logger are
  unaffected.

### KhaozEngine.Content

- `ConfigLoader.Load<T>` now emits a Debug line naming the resolved source (disk path vs embedded
  resource) under category `ConfigLoader` - the usual "which config actually loaded" question. Adds a
  `KhaozEngine.Diagnostics` dependency. `JsonSchemaValidator` keeps its `TextWriter` reporter (it is a
  CLI tool surface, not runtime diagnostics).

### KhaozEngine.Localization

- `LocalizationManager.SetCulture` and `GetSupportedCultures` emit Debug lines (culture set, count of
  discovered cultures) under category `LocalizationManager`. Adds a `KhaozEngine.Diagnostics`
  dependency (still pure BCL, Diagnostics has no MonoGame dep).

Pure-compute packages (Ecs, Time, Sprites, UI, Graphics, Input, Serialization, Effects, App, Screens)
intentionally stay logless: no IO and no swallowed exceptions, so logging would be noise.

## KhaozEngine 4.0.0

Breaking. Inter-package tidy-up: a rendering primitive moves to the rendering package, and JSON
defaults are centralized in a new package. No runtime behavior change, but two namespaces moved and
`KhaozEngine.Effects` swaps a dependency, so consumers need `using` and possibly `<PackageReference>`
updates.

### KhaozEngine.Graphics

- `PrimitiveRenderer` and `ColorHelper` moved here from `KhaozEngine.UI` (namespace
  `KhaozEngine.UI` -> `KhaozEngine.Graphics`). They are low-level rendering helpers (1x1 pixel
  shapes, hex color parsing) with no UI concepts, so they belong in the rendering package that
  already sits below UI. **Migration:** add `using KhaozEngine.Graphics;` where you used
  `PrimitiveRenderer`/`ColorHelper`. `KhaozEngine.UI` consumers need no new package reference (UI
  already depends on Graphics); the types are just in a different namespace now.

### KhaozEngine.Effects

- Now depends on `KhaozEngine.Graphics` instead of `KhaozEngine.UI`. Its only use of UI was
  `PrimitiveRenderer`, which now lives in Graphics, so the package no longer drags in the whole UI
  widget set. **Migration:** if you reference `KhaozEngine.Effects` directly, no change; the
  transitive dependency just shifts from UI to Graphics.

### KhaozEngine.Serialization (new package)

- New leaf package holding `JsonDefaults`: shared `System.Text.Json` option baselines so config,
  persistence, and ECS serialize the same way. `TolerantRead` (case-insensitive, `//` comments,
  trailing commas), `IndentedWrite` (`WriteIndented`), and `IncludeFields` (round-trips public
  fields). Each is a single shared, effectively read-only instance. Pure BCL, no MonoGame.
- `KhaozEngine.Content` (`ConfigLoader`), `KhaozEngine.Persistence` (`AtomicJsonWriter`,
  `PersistenceQueue`, `FileSettingsStorage`), and `KhaozEngine.Ecs` (`WorldSerializer`) now consume
  `JsonDefaults` instead of each declaring their own options. Public APIs and on-disk format are
  unchanged; these packages gain a `KhaozEngine.Serialization` dependency.

## KhaozEngine 3.12.0

Additive. New keyed registry for directional sprites in `KhaozEngine.Sprites`.

### KhaozEngine.Sprites

- New `SpriteRegistry` - a keyed store of `DirectionalAnimatedSprite` with one bulk
  `Update(float deltaSeconds)` that advances every registered sprite's animation clock once per
  frame. `Add(key, sprite)` (non-empty key, no duplicates, non-null sprite), `Get(key)` returning
  the sprite or null, `Contains(key)`, and `Count`. Takes already-built sprites - loading by
  embedded-resource manifest name stays game-side, since resource names are game-specific.
  Centralizes the `Dictionary<string, DirectionalAnimatedSprite>` + per-frame bulk-advance that
  Hardpoint hand-rolls in `SpriteLibrary`.

## KhaozEngine 3.11.0

Additive seam so consumers stop wrapping `VirtualResolution` just to make screens headless-testable.

### KhaozEngine.Input

- New `IDesignViewport` interface: `int Width`, `int Height`, `float Scale`, `Matrix ScaleMatrix`.
  `VirtualResolution` now implements it (its existing properties already satisfy the contract - no
  behavior change). Screens that need only design-space size/scale/matrix can take an `IDesignViewport`
  and tests can hand them a fixed-size fake instead of standing up a `VirtualResolution`. Hardpoint's
  game-side `IViewport` + `VirtualResolutionViewport` adapter exist purely for this; they can drop the
  adapter and reference the engine interface directly.

## KhaozEngine 3.10.0

Shared camera-gesture core: `PannableCanvas` and `CameraController` now drive a `Camera2D` and share
one implementation of pan / zoom / pinch / clamp / tap. Additive API plus one scoped behavior change.

### KhaozEngine.Graphics

- `Camera2D.GetViewMatrix` now honors the viewport's X/Y offset (centers `Position` on
  `(viewport.X + W/2, viewport.Y + H/2)`). **Behavior change**, but only for a viewport with a non-zero
  X/Y origin (an inset sub-rectangle) - the previously unsupported/incorrect case. Whole-screen
  viewports (X = Y = 0, every prior call site) are unchanged. Makes inset viewports map correctly.
- New `Camera2D.PanByScreenDelta(screenDelta)` - grab-and-drag pan (`Position -= screenDelta / Zoom`).
- New `Camera2D.ZoomAboutScreenPoint(target, focusScreen, viewport, min, max)` - clamped zoom that keeps
  the world point under the focus fixed.
- New `PinchGestureTracker` - the shared two-finger pinch state machine (midpoint pan + zoom-about-focus).
- New `CameraGestures.TryGetTap(input, camera, viewport, out press, out release)` - the shared
  press-origin tap-vs-pan helper.
- `CameraController` now drives `Camera2D` through these shared pieces. No public API or behavior change.

### KhaozEngine.UI

- `PannableCanvas` delegates its transform / clamp / pan / tap math to a backing `Camera2D` (shared with
  `CameraController`). `CameraOffset` is preserved as the legacy additive view (`-Position * Zoom`).
  Drag pan, wheel-as-vertical-pan, scissor `Draw`, `BlockInput`, `Padding`, `ScrollPanSpeed`, and the
  press-origin tap invariant are byte-identical.
- New: real two-finger **pinch zoom** (the old `_zoom = 1f` seam is now live). New `MinZoom` / `MaxZoom`
  (defaults 0.1 / 10), `EnablePan` / `EnableZoom` (default true), and a `Camera` accessor. Wheel stays a
  vertical pan. Mouse-only behavior is unchanged. Disable pinch with `EnableZoom = false`; `EnablePan =
  false` disables all panning (drag, two-finger, and wheel).
- `Focus(rect)` now **fits zoom to the rect** (delegates to `Camera2D.Focus`, clamped to `MinZoom`/
  `MaxZoom`), fulfilling its long-standing "becomes fit-to-rect once zoom exists" intent - it previously
  only centered. Optional `paddingFraction` parameter. Use `CenterOn`/`CenterContent` for a center-only move.
- `KhaozEngine.UI` now references `KhaozEngine.Graphics` (transitive package dependency added).

## KhaozEngine 3.9.0

Camera framing + follow, both in `KhaozEngine.Graphics`. Additive, no breaking changes.

### Camera2D framing helpers: CenterOn + Focus (fit-to-rect zoom)

`Camera2D` gains the framing math that consumers were hand-rolling (Hardpoint's `BoardFraming`,
SpaceForge's grid framing, `PannableCanvas`'s long-dormant `Focus(rect)` zoom seam):

- `CenterOn(Vector2 world)` - sets `Position` so the world point is at the viewport center (an explicit
  alias for API parity).
- `Focus(Rectangle worldRect, Viewport viewport, float paddingFraction = 0f, float minZoom, float maxZoom)`
  - fit-to-rect: sets `Zoom` so the rect (optionally inflated by `paddingFraction` on each side) is fully
  visible (contain fit, `min(viewport.Width / rectW, viewport.Height / rectH)`), clamped to
  `minZoom`/`maxZoom`, then centers `Position` on the rect. Pure and headless. Does not clamp to world
  bounds - call `ClampPosition` after if the rect is a sub-region. A no-arg-viewport overload uses the
  stored `Viewport` property.

Because these live on `Camera2D`, both `CameraController` and (once consolidated) `PannableCanvas`
inherit them.

### CameraFollow (target-follow with smoothing + deadzone)

New `CameraFollow` drives a `Camera2D` to follow a moving target. The game decides what to follow; this
owns only the smoothing/deadzone/clamp. Kept separate from the gesture `CameraController` - a screen
typically uses one or the other.

- `Update(Vector2 target, float dt, Viewport viewport, Rectangle worldBounds)` - eases toward the target,
  then clamps via `Camera2D.ClampPosition`. Headless (explicit `Viewport`).
- **Frame-rate-independent smoothing**: per-frame catch-up is `1 - exp(-Stiffness * dt)`, so the result
  is independent of step size / frame rate. `Stiffness <= 0` snaps instantly.
- **Optional deadzone**: a screen-space (virtual) `Rectangle` the target may move within before the camera
  chases; once the target crosses an edge the camera moves just enough to put it back on that edge.
  `Rectangle.Empty` (default) disables it (camera centers on the target).

Wiring:

    var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
    var follow = new CameraFollow(camera) { Stiffness = 8f, Deadzone = new Rectangle(360, 240, 200, 120) };
    // per frame:
    follow.Update(playerWorldPos, dt, GraphicsDevice.Viewport, levelBounds);
    // or frame a region instead of following:
    camera.Focus(levelBounds, GraphicsDevice.Viewport, paddingFraction: 0.05f, minZoom: 0.5f, maxZoom: 3f);

## KhaozEngine 3.8.0

New package `KhaozEngine.Sprites`: 2D sprite + directional-animation playback. Additive, no breaking
changes. Replaces flat-primitive entity rendering with directional, animated sprites for all games.

### KhaozEngine.Sprites (new)

- **`Direction8`** - the 8 facings `S, SE, E, NE, N, NW, W, SW`, ordered so the enum value is the
  direction's row index in a PixelLab grid sheet. `Direction8Extensions.FromVector(facing, fallback)`
  maps a movement/aim vector to the nearest of 8 in y-down screen space (+X east, +Y south); magnitude
  is irrelevant, a 22.5-degree seam rounds to the higher (clockwise) direction, and a zero vector
  returns `fallback`. `ToVector()` returns the unit facing.
- **`SpriteSheetLayout`** - pure grid math (no `Texture2D`, headless): `FromFrameSize` / `FromGrid`,
  then `GetFrame(row, column)` -> source `Rectangle`. **`SpriteSheet`** pairs it with a texture.
- **`SpriteFrame`** - a `(Texture2D, Rectangle)` drawable frame; frames carry their own texture so an
  animation can span one packed sheet or a set of loose per-frame textures.
- **`SpriteAnimation`** - ordered frames + per-frame duration + loop flag (`FromFps` or seconds ctor).
  **`SpriteAnimationPlayer`** advances it by a `float` seconds delta or a `GameTime`, yields the current
  frame, loops, flags `IsFinished` for one-shots, and `Play(anim, preservePhase)` swaps animations. A
  small relative tolerance on the frame boundary keeps exact-multiple deltas from dropping a frame to
  float noise.
- **`DirectionalAnimatedSprite`** - one animation per `Direction8`, plays the one matching the current
  facing, draws via `SpriteBatch` with a centered origin by default; switching facing preserves the
  animation phase so a walk cycle stays smooth. `Update(facing, gameTime)` does both in one call.
- **`PixelLabSpriteLoader`** - builds a `DirectionalAnimatedSprite` from a PixelLab export, either an
  assembled grid sheet (`FromGridSheet`: 8 direction rows x N frame columns) or loose per-direction
  frame textures (`FromFrames`). PixelLab's row order is isolated here (in `RowFor`) so the core types
  stay PixelLab-agnostic. Note: PixelLab exports loose per-frame PNGs, not a canonical sheet, so the
  grid layout matches an assembly step's output; verify row order against a real export on first use.

The animation clock decouples from `KhaozEngine.Time` deliberately (advances on a `float` delta), so
callers feed either `GameTime.ElapsedGameTime` or a scaled `GameClock.ScaledDeltaSeconds`.

## KhaozEngine 3.7.0

Two additive camera/viewport features. No breaking changes.

### KhaozEngine.Graphics: CameraController (pan/zoom/pinch gesture controller)

New `CameraController` drives an existing `Camera2D` from an `InputManager`, so gameplay can pan
and zoom an arbitrary world render without re-implementing the gesture math. It owns no matrix math
of its own: it reuses `Camera2D.ScreenToWorld` and `Camera2D.ClampPosition`.

- **Pan**: single-pointer drag and two-finger drag (by pinch midpoint travel). Grab-and-drag, so
  world content tracks the finger; the screen delta is divided by `Zoom` to a world delta.
- **Zoom**: scroll wheel (desktop) and pinch (mobile), clamped to `MinZoom`/`MaxZoom`. Zoom is about
  the cursor / pinch midpoint - the focal world point stays under the pointer. `WheelZoomStep` is the
  multiplicative factor per 120-unit notch (fractional/multi-notch deltas scale smoothly via a power).
- **Bounds clamp**: after pan/zoom, clamps via `Camera2D.ClampPosition(Position, worldBounds, viewport)`
  so the view stays inside a caller-supplied world rectangle (auto-centers when the world is smaller).
- **Tap vs pan**: `TryGetTap(out pressWorld, out releaseWorld)` mirrors `PannableCanvas.TryGetTap` and
  honors the press-origin invariant - gameplay places a tower on a tap but treats a drag as a pan
  (a pan returns true too, but its press/release world points differ, so a same-target check rejects it).
- **Headless**: `Update(Viewport, Rectangle worldBounds)` takes an explicit `Viewport` like `Camera2D`,
  so the step is unit-testable with no `GraphicsDevice`. Toggles: `EnablePan`, `EnableZoom`, `BlockInput`.

`KhaozEngine.Graphics` now references `KhaozEngine.Input` (for `InputManager`). Wiring:

    var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
    var controller = new CameraController(input, camera) { MinZoom = 0.5f, MaxZoom = 4f };
    // per frame, after input.Update(...):
    controller.Update(GraphicsDevice.Viewport, worldBounds);
    if (controller.TryGetTap(out var pressWorld, out var releaseWorld)) { /* place on tap */ }
    spriteBatch.Begin(transformMatrix: camera.GetViewMatrix());

Relationship to `PannableCanvas` (KhaozEngine.UI): both now carry pan/zoom gesture logic, but on
different coordinate conventions (`PannableCanvas` uses an additive offset and an inset sub-rectangle
viewport with scissor clipping; `CameraController` uses `Camera2D`'s position/zoom matrix). This
release ships `CameraController` standalone and leaves `PannableCanvas` as-is to avoid regressing the
games already on it (Hardpoint's map). Consolidating `PannableCanvas` onto `CameraController` is a
tracked follow-up; the two are not meant to diverge long-term.

### KhaozEngine.Input: opt-in desktop design-scale for VirtualResolution

`VirtualResolution` now offers a design-scaled mode on desktop, mirroring mobile: a fixed
`BaseWidth` × `ReferenceHeight` design space scaled to fill the window, so desktop UI presents the
same fixed design space (and scales up on a large/Retina window) instead of sizing in raw
back-buffer pixels.

- **Opt-in, non-breaking**: the desktop default (`isMobile:false` → scale 1, identity matrix, virtual
  size = back-buffer) is unchanged. Opt in with the new `VirtualResolution.DesignScaled(gdm, baseWidth,
  referenceHeight)` factory (still pass `isMobile:false` to the `InputManager`; only the scaling differs).
- **Fill policy**: fill-the-width, adaptive-height (the same as mobile) - no letterbox bars and no
  offset, so `ScreenToVirtual` stays a plain divide-by-`Scale` and `InputManager` hit-testing lines up.
- The `GraphicsDeviceManager` ctor argument is now nullable, and a new `Configure(int screenWidth,
  int screenHeight)` computes the scaling from an explicit size (`Initialize` delegates to it). This
  makes the scaling headless-testable and lets a consumer drive it from a known/fixed size.

Wiring a desktop game into design-scale:

    var vr = VirtualResolution.DesignScaled(graphicsDeviceManager, baseWidth: 932, referenceHeight: 430);
    vr.Initialize();                                  // and again on Window.ClientSizeChanged
    var input = new InputManager(isMobile: false, transform: vr);

## KhaozEngine 3.6.0

### KhaozEngine.Ecs: CachedQuery (per-tick allocation-free query reuse)

New `CachedQuery` lets sim hot paths reuse a single `Query` instead of allocating a fresh one
every tick. `World.Query()` returns `new Query(this)` per call, so calling it inside a per-tick
loop violates the consumers' "no per-frame allocation in sim hot paths" rule.

- `CachedQuery(Func<World, Query> build)` captures the filter builder once.
- `Query For(World world)` returns the reused `Query`, rebuilding it only when the `World`
  instance changes (`ReferenceEquals` check) - for consumers that recreate the `World` on
  run-reset. The underlying `Query` still self-refreshes its matched-archetype list on
  `ArchetypeGen` changes, so newly spawned archetypes are picked up through the cache.

Additive, no breaking changes. Usage:

    private readonly CachedQuery _projectiles = new(w => w.Query().With<ProjectileTag>());
    // per tick:
    _projectiles.For(world).ForEach((Entity e, ref Position p) => ...);

## KhaozEngine 3.5.0

### KhaozEngine.Graphics: DisplayManager (display/window configuration)

New `DisplayManager` centralizes MonoGame `GraphicsDeviceManager` + `GameWindow` setup so games
stop configuring the device bespoke.

- `DisplaySettings` (immutable record): `Width`/`Height`, `Mode` (`WindowMode.Windowed` /
  `BorderlessFullscreen` / `ExclusiveFullscreen`), `AllowUserResizing`, `MinWidth`/`MinHeight`
  floor, `SupportedOrientations`, `Title`. Factories `DisplaySettings.Landscape(w, h)` and
  `Portrait(w, h)`. Pure and headless-testable; build variants with `with`.
- `DevicePresets` catalog of common iOS logical-point sizes (iPhone SE to 15 Pro Max, iPad to
  Pro 12.9") via `DevicePreset.Portrait()` / `.Landscape()`.
- `DisplayManager(graphics, window, settings)` applies settings to the live device and exposes
  runtime mutators `Apply`, `SetResolution`, `SetMode`, `ToggleFullscreen`, `SetResizable`, plus
  `Width`/`Height`/`Size`/`IsFullscreen`. Enforces the min-size floor by clamping on
  `ClientSizeChanged`. Composes with `VirtualResolution`, which still reads the device for scaling.

One-liner for an iPhone 15 Pro Max landscape window (932x430):

    display = new DisplayManager(graphicsDeviceManager, Window, DisplaySettings.Landscape(932, 430));

## KhaozEngine 3.4.1

Bug fix for the 3.4.0 now-playing feature. No API or behaviour change for callers whose tracks all load.

- **KhaozEngine.Audio** - `AudioSystem.LoadContent` now drops any track that fails to load from its
  internal name list, keeping it aligned with the backend's compact track list. Previously a partial
  load failure left the names and the backend's indices misaligned, so `CurrentTrack` / `TrackChanged`
  reported the wrong song and `PlayTrack(name)` could resolve to the wrong index. The load log still
  reports `loaded/requested` against the originally requested count.

## KhaozEngine 3.4.0

Additive feature pass unblocking SpaceGame/Nullwake adoption, plus review-nit fixes. No breaking changes.

- **KhaozEngine.Persistence** - `SettingsManager<T>` gains an optional `sanitizeOnLoad` constructor hook
  (`Func<T,T>`). It runs on every load, including the initial load inside the constructor (which the
  `SettingsLoaded` event can't reach), so callers can clamp fields / migrate a schema version on the
  first load. Null = passthrough; a throwing hook is swallowed/logged and the unsanitized value is used.
  The README documents the `[JsonExtensionData]` + version-field downgrade-safe migration pattern.
- **KhaozEngine.Audio** - `AudioSystem` now supports explicit and repeating playback alongside random
  rotation: `PlayTrack(int)` / `PlayTrack(string)` (an unknown name or out-of-range index is a logged
  no-op, not a throw), a settable `PlayMode { RandomRotation, RepeatOne }` (default `RandomRotation`),
  and now-playing state via `CurrentTrack` plus the `TrackChanged` event.
- **KhaozEngine.Audio** - a transient exception while reading `IMusicBackend.IsPlaying` in `Update()`
  now skips the frame (logged) and recovers, instead of permanently disabling audio. The availability
  latch is reserved for real play/load failures.
- **KhaozEngine.Ecs** - `DeterministicRng.Next(maxExclusive)` and `Next(min, max)` now throw
  `ArgumentOutOfRangeException` on non-positive / empty ranges (previously a DivideByZero or
  negative-modulo trap).
- Docs/tests: `docs/USING-KHAOZENGINE.md` gains a `KhaozEngine.Graphics` / `Camera2D` section; the
  Effects pool-recycle test now asserts the oldest particles are actually overwritten.

## KhaozEngine 3.3.0

Batch 2 of the "promote duplicated game code into KhaozEngine" effort: three new packages plus
additions to two existing ones. All additive; no consumer adopts these yet.

- **KhaozEngine.Audio** (new; MonoGame + Diagnostics): `AudioSystem` (track-registry music player,
  seed-via-ctor + additive idempotent `RegisterTrack`/`RegisterTracks` that work pre- and post-load)
  over a public `IMusicBackend`. Public `MonoGameMusicBackend` and `MacOsMusicBackend` (the macOS
  backend works around MonoGame's broken `Song` playback via an AVAudioPlayer P/Invoke shim). Logs
  through an injected `ILogger` (defaults to the engine `Log`).
- **KhaozEngine.Effects** (new; MonoGame + UI): pooled, data-driven particle system. A
  `ParticleEmitterConfig` record holds all tunables; `ParticlePresets.Spark`/`.Ember` reproduce the
  promoted Nullwake hit effects; `ParticleSystem.Emit(config, position, baseColor, count)` with a
  ring-buffer pool. First resident of a generic visual-effects package (room for screen shake, flashes, etc.).
- **KhaozEngine.Graphics** (new; MonoGame): `Camera2D` - a generic 2D matrix camera
  (position/zoom/rotation → view matrix), headless `WorldToScreen`/`ScreenToWorld` (explicit `Viewport`,
  no `GraphicsDevice`), turn-key no-arg overloads via a settable `Viewport`, and a pure
  `ClampPosition` world-bounds helper. The base for a future follow/deadzone/parallax camera layer.
- **KhaozEngine.Persistence** additions: `AtomicJsonWriter` (crash-safe temp-then-move writes),
  `PersistenceQueue` (`IPersistenceQueue`; per-path coalescing async writer, never throws into the
  game, retry + `WriteFailed` event, blocking `Flush()` + flush-on-dispose), and
  `SettingsManager<T>` / `ISettingsStorage` / `FileSettingsStorage` (typed settings persisted via the
  queue, default paths through `KhaozEngine.App.AppDataPaths`). Persistence now also references `KhaozEngine.App`.
- **KhaozEngine.Ecs** addition: `DeterministicRng.CreateDerived(string systemName)` - named, stable,
  reproducible substreams (mixes the parent seed with a fixed string hash; not `string.GetHashCode`).
  Note: derived streams do not byte-match `System.Random`, so any consumer migrating to it must re-baseline golden values.

## KhaozEngine 3.2.0

Batch 1 of the "promote duplicated game code into KhaozEngine" effort. Three new pure-.NET packages
(plus a small consolidation of the `AppDataPaths` that 3.1.0 had shipped). No consumer adopts these yet.

- **KhaozEngine.App** (new, pure .NET): app/runtime helpers.
  - `BuildMetadata.Read(string key, string fallback, params Assembly?[] assemblies)` - reads
    `AssemblyMetadataAttribute` values at runtime, probing the supplied assemblies in order (null
    entries skipped), so a game can surface its own version/build identity without re-deriving it.
  - `AppDataPaths` - instance resolver for the OS-correct per-app data directory (Windows `%APPDATA%`,
    macOS `~/Library/Application Support`, Linux `$XDG_DATA_HOME`/`~/.local/share`, with fallbacks).
    `BaseDirectory` is resolved + created once and cached (thread-safe via `Lazy<T>`); convenience
    `SaveFilePath`/`SettingsFilePath`/`LogFilePath`/`PreviousLogFilePath`/`GetFilePath`. OS resolution
    sits behind an internal seam for headless testing.
  - `ServiceLocator : IServiceProvider` - generic register/resolve-by-type service registry backed by a
    `ConcurrentDictionary` (`Register`/`Replace`/`Get`/`TryGet`/`Has`/`GetService`). Fits
    `ScreenManager.Services`.
- **KhaozEngine.Localization** (new, pure .NET): `LocalizationManager(ResourceManager)` discovers the
  cultures backed by satellite resources (`GetSupportedCultures`) and sets the current thread culture
  (`static SetCulture`, fail-fast on null/empty); `DefaultCultureCode = "en-US"`.
- **KhaozEngine.Persistence** (new; refs `KhaozEngine.Diagnostics`): `SaveEncoder(byte[] hmacKey,
  string magicPrefix, ILogger logger)` wraps save JSON in a Base64 + HMAC-SHA256 envelope
  (`{prefix}:{hmac}:{base64}`) as a casual tamper-deterrent. Decoding is lenient (recovers the JSON
  even on an HMAC mismatch) and reports each outcome (Info / Warn / Error) through the injected
  engine `ILogger`.
- **AppDataPaths consolidation:** `KhaozEngine.App.AppDataPaths` is the canonical resolver; the
  duplicate static `KhaozEngine.Diagnostics.AppDataPaths` that 3.1.0 shipped is **removed** (engine
  logging is path-agnostic - pass resolved paths into `FileSinkOptions`). Removing a 3.1.0 public type
  is breaking in principle, but numbered 3.2.0 (not 4.0.0): no released consumer referenced it (3.1.0
  is not yet adopted by any game), consistent with 3.1.0's owner-choice handling of the `FileLogger`
  removal.

## KhaozEngine 3.1.0

- **KhaozEngine.Diagnostics**: replaced the minimal `FileLogger` with a full logging service.
  `LogManager` (instance core, injectable) + a static `Log` facade own a runtime-settable
  `MinimumLevel`, an injectable `IClock`, and a list of `ILogSink`s. Category loggers via
  `Log.For<T>()` / `GetLogger(string)` stamp a component tag on each `LogEntry`
  (`Trace`/`Debug`/`Info`/`Warn`/`Error`/`Fatal`, each with an optional exception). Writes are
  non-blocking by default (a single background thread drains a bounded queue; overflow is counted in
  `DroppedCount`, reported on the next flush, and never blocks the caller) with a synchronous mode for
  deterministic tests; `Flush`/`Shutdown` drain the queue and flush sinks, and logging never throws,
  including after shutdown.
- Sinks: `FileSink` (rotate-on-launch + optional size-based rotation + retention via
  `FileSinkOptions.MaxBytes`/`MaxFiles`, `AutoFlush` for crash survivability), `ConsoleSink`
  (stderr for errors), `DebugSink` (`System.Diagnostics.Trace`), and `InMemorySink` (tests). Games
  add their own target by implementing `ILogSink`.
- `CrashHandler.Install` wires `AppDomain.UnhandledException` + `TaskScheduler.UnobservedTaskException`
  to log a `Fatal` `Crash` entry and flush, so games stop hand-rolling crash hooks.
- Promoted `AppDataPaths`: OS-correct per-app data directory resolver (Windows `%APPDATA%`, macOS
  `~/Library/Application Support`, Linux XDG), created on first access and cached per app name. Engine
  logging stays path-agnostic; games pass resolved paths into `FileSinkOptions`.
- **BREAKING (shipped as a minor):** `FileLogger` is removed; consumers move to `Log`/`LogManager`. The
  default log line format gains a `[Category]` field: `[ts] [LEVEL] [Category] message`. Numbered 3.1.0
  (not 4.0.0) by owner decision: every consumer is first-party and migrated in lockstep, so the 3.x
  line is kept. This deliberately deviates from the usual SemVer "breaking = major" rule. All packages
  to 3.1.0.

## KhaozEngine 3.0.0

- **KhaozEngine.UI**: new `PrimitiveRenderer.DrawRing` (static + instance overloads) draws a circle
  outline with sub-pixel **float** thickness by stitching rotated 1x1-pixel quads along the radius
  path, so fractional thicknesses render faithfully (unlike `DrawCircle`'s integer line width). No-op
  when radius or thickness is non-positive. `RingSegments(radius, segmentsOverride)` exposes the
  segment count: an explicit override (floored at 3) or a radius-adaptive count clamped to `[18, 64]`.
- New package **KhaozEngine.Diagnostics** with `FileLogger`: a thread-safe, timestamped file logger
  for diagnosing silent crashes and startup failures. `Initialize(logFilePath, previousLogFilePath?)`
  opens an `AutoFlush` `StreamWriter` and rotates an existing log aside (when a previous path is given)
  so the most recent run is always in the primary file; `Info`/`Warn`/`Error`/`Error(msg, ex)` write
  `[ts] [LEVEL] message` lines; `Shutdown` (also `Dispose`) flushes and closes. Every method swallows
  IO failures so logging can never crash the game. Pure `System.IO`, no MonoGame dependency. The log
  path is the caller's concern (each game resolves its own app-data path and passes it in). Extracted
  from SpaceGame's in-house `GameLogger` (Nullwake had a near-identical copy; Hardpoint had none);
  instance-based and headless-testable. Adopted by SpaceGame and Nullwake.
- **KhaozEngine.Content**: fix `JsonSchemaValidator` crash ("Overwriting registered schemas is not
  permitted") when multiple data files reference the same schema file (share a `$id`). The validator
  now passes an isolated `SchemaRegistry` via `BuildOptions` to each `JsonSchema.FromText()` call
  instead of using the global static registry, so repeated builds and multi-file directories with
  shared schemas no longer abort with exit code 134. No API surface change; all existing callers
  are unaffected.
- Major bump consolidates the Content validator fix, the new Diagnostics package, and the
  `DrawRing` primitive into one clean release after untangling concurrent development. All changes are
  additive; no behaviour change for existing consumers. All packages bump to 3.0.0.

## KhaozEngine 2.4.0

- **KhaozEngine.UI**: new `PannableCanvas`, a generic pannable viewport. Owns a camera offset;
  pans on drag (`InputManager.GetDragDelta`) and vertical wheel (`InputManager.GetScrollIn`) within
  a caller-set `Viewport`; clamps the camera to `ContentBounds` inflated by `Padding` (centering an
  axis when content is smaller than the viewport). Exposes `WorldToScreen`/`ScreenToWorld`,
  `PointerWorld`, and `TryGetTap(out pressWorld, out releaseWorld)` (gated on the press-origin tap
  invariant so it stays click-through-safe). `CenterOn`/`Focus`/`CenterContent` recenter the camera.
  `Draw(sb, gd, renderScale, scaleMatrix, drawWorld)` scissor-clips to the viewport and invokes a
  world-space draw callback (pass `vr.Scale`/`vr.ScaleMatrix`). Zoom is not implemented; a single
  fixed scale, with the transform seam kept for later.
- Generalizes the inline camera/pan code in Nullwake's `SkillTreeScreen` so a node-graph / map screen
  needs no per-game reinvention. Additive and opt-in; no behaviour change for existing consumers.
  All packages bump to 2.4.0.

## KhaozEngine 2.3.0

- **KhaozEngine.Time**: new `TimeSkip` (+ `TimeSkipResult`) for advancing a simulation by a span of
  sim-time in one analytical call. `Advance(simSeconds, step)` clamps to an optional `MaxSimSeconds`,
  scales by `Multiplier`, skips requests below `MinSimSeconds` (and any `<= 0`), invokes the consumer's
  analytical catch-up callback once, raises `Completed`, and returns a `TimeSkipResult`
  (requested/applied seconds, `WasCapped`, `Ran`). Static `TimeSkip.ElapsedSimSeconds(lastSave, now,
  timeScale)` computes offline wall time (clamped >= 0, optionally scaled by sim speed).
- For on-demand "fast-forward for credits" and offline catch-up. The engine simulates nothing itself
  (the game supplies the analytical step); there is no per-frame budget because analytical catch-up is
  instant. Additive and opt-in; no behaviour change for existing consumers. All packages bump to 2.3.0.

## KhaozEngine 2.2.0

- New package **KhaozEngine.Time** with `GameClock`: separates real delta time (UI, transitions,
  notifications) from a scaled simulation delta. `TimeScale` gives slow-mo (`<1`), normal (`1`), and
  fast-forward (`>1`); `Pause()`/`Resume()` freeze the sim orthogonally to `TimeScale` (resume keeps the
  intended speed); `Paused`/`Resumed` events fire on transitions; `IsPaused` is true when paused or
  `TimeScale == 0`.
- **KhaozEngine.Screens**: `ScreenManager` now owns a `GameClock` (new `ScreenManager(InputManager, GameClock)`
  overload to share one), exposes `Clock`/`IsPaused`/`TimeScale`/`RealDeltaSeconds`/`ScaledDeltaSeconds`,
  drives transitions on real dt (so they stay live while paused), dispatches new
  `GameScreen.OnPause()`/`OnResume()` virtuals to stacked screens on pause transitions, and is now
  `IDisposable` (unsubscribes from a shared clock).
- Additive and opt-in. Default `TimeScale == 1` makes scaled dt identical to today, so the existing
  consumers are unchanged. Gameplay reads `ScaledDeltaSeconds` (e.g. `world.Update(ScaledDeltaSeconds)`);
  UI/transitions/notifications keep using real time. SpaceGame's fixed-timestep lockstep never reads the
  scaled delta, so determinism is preserved. All packages bump to 2.2.0.

## KhaozEngine 2.1.0

- New package **KhaozEngine.Content** (pure .NET, depends on JsonSchema.Net): `ConfigLoader.Load<T>`
  (embedded/disk JSON) and `JsonSchemaValidator` (instance + directory validation), plus a bundled
  validator tool and a `buildTransitive` target that validates a consumer's `Data/` against its schemas
  when `KhaozContentDataDir` is set. Generalizes Nullwake's config pattern; opt-in. All packages bump to
  2.1.0 (unified versioning); no changes to the existing four.

## KhaozEngine 2.0.0 (unified versioning)

- All four packages (Input, Screens, UI, Ecs) now share one version line and the `v*` tag scheme; the
  separate `ecs-v*` line is retired and `Ecs` no longer overrides its version. **No functional change:**
  Input/Screens/UI `2.0.0` are identical to `0.2.1`, and Ecs `2.0.0` is identical to `1.6.0`. Future
  releases bump all four together. Games can adopt `2.0.0` whenever convenient; existing vendored
  `0.2.1`/`1.6.0` references keep working.

## KhaozEngine.Ecs 1.6.0

- Deterministic outcome model: `EntityCommandBuffer.Defer(Action<World>)` (ordered deferred actions);
  a pull-model typed event channel (`World.Emit<T>` / `Events<T>`, cleared by `AdvanceTick`); and
  `DeterministicRng` (xorshift128+, seedable, save/resume `State`). Drawing RNG inside deferred actions
  gives a reproducible draw sequence (record order = the deterministic iteration order from 1.5.0).
  Additive and opt-in. Completes the determinism work (Cycles A + B).

## KhaozEngine.Ecs 1.5.0

- Deterministic iteration order: queries, `ForEach`, and serialization now walk archetypes in a
  guaranteed creation order (an explicit ordered list) rather than relying on `Dictionary` enumeration.
  Iteration is reproducible for an identical operation sequence, run-to-run and across processes
  (foundation for lockstep determinism). Swap-remove within an archetype is unchanged. Additive.

## KhaozEngine.Ecs 1.4.0

- Add named system groups: `AddSystem(system, group)`, `SetGroupOrder(...)`, `UpdateGroup(name, dt)`,
  and `SystemGroups`. `Update(dt)` runs all groups in order; `UpdateGroup` runs one (e.g. a
  fixed-timestep simulation group). Systems without a group use `"default"`, so existing usage is
  unchanged. Additive.

## KhaozEngine.Ecs 1.3.0

- Add a parent-child hierarchy: built-in `Parent` component, `World.SetParent` / `Detach` /
  `GetParent` / `Children`, and `DespawnTree` (cascade) vs plain `Despawn` (detaches children to
  root). Cycle-guarded. Hierarchies serialize (the children index rebuilds on load; `Parent` is
  auto-included by `WorldSerializer`). Transform propagation stays game-side. Additive.

## KhaozEngine.Ecs 1.2.0

- Add per-tick change detection: `World.AdvanceTick()` (call once per frame), `Added<T>()` /
  `Removed<T>()` (automatic from structural changes), `Changed<T>()` with explicit `MarkChanged<T>(e)`
  (since `ref` writes are invisible to the ECS). `Removed<T>` may include despawned entities. The load
  path does not generate events. Additive; no breaking change.

## KhaozEngine.Ecs 1.1.0

- Add `WorldSerializer`: JSON save/load of a `World` (entities + components + id-allocator state).
  Entities restore at their exact id/version so `Entity`-typed fields survive; tags and free-slot
  versions are preserved. Construct with your component types or `FromAssemblyOf<T>()`. Resources and
  systems are not serialized. Additive; no breaking change.

## KhaozEngine.Ecs 1.0.0

- Rewrite as a struct-based archetype ECS: versioned `Entity`, archetype/column storage, `ref`
  `Get<T>`, `With`/`Without` queries, `ForEach` arities 1-8, `EntityCommandBuffer`, typed `Resources`.
- Breaking vs 0.1.x: components are now `struct : IComponent`; `Get<T>` returns `ref T`; the
  `List<Entity> Query<T>()` overloads are replaced by `ForEach`. Versioned independently of the
  other KhaozEngine packages (which stay on 0.2.x).

## 0.2.1

- Fix: `PrimitiveRenderer.DrawProgressBar` rendered short bars as a solid line in the border
  color. A bar only a few pixels tall (e.g. a zoomed-out HP bar at 2px) left zero inner height
  after subtracting a 1px border on each side, so the fill never drew and the border covered the
  whole bar. The border thickness is now capped to keep at least a 1px fill area, dropping to 0 on
  bars too small to fit one. Adds headless geometry regression tests.

## 0.2.0

- `InputManager`: middle/right mouse-button edges (`IsMiddle/RightDown/JustPressed/JustReleased`).
- `InputManager.Touches` - active touches in virtual coordinates with stable ids (`TouchPoint.Id`).
- `InputManager.TryGetPinch(out Pinch)` - virtual midpoint, distance, per-frame delta, scale ratio.
- Optional gamepad/keyboard controller cursor via `cursorSpeed` ctor arg + `Update(raw, isActive, dt)`.
- All additive; 0.1.x consumers are unaffected until they bump.

## 0.1.3

- Fix: desktop clicks were suppressed whenever the game window was not at the screen
  origin. `InputManager`'s in-window check compared window-relative mouse coords against
  `WindowBounds` carrying the window's screen offset, so `Contains` rejected every click.
  The check now ignores `WindowBounds.Location` (uses Width/Height only), and
  `MonoGameRawInput` reports the client area at the origin. Adds headless regression tests.

## 0.1.2

- Add per-package README files (shown on the NuGet package pages).
- Add this changelog.

## 0.1.1

- XML documentation comments across the public API of `KhaozEngine.Input`, `.Screens`, and `.Ecs`.
- Enable `GenerateDocumentationFile` so docs ship in the packages for IntelliSense.
- No functional change from 0.1.0.

## 0.1.0

Initial release. Four packages extracted from Hardpoint/Nullwake/SpaceGame:

- **KhaozEngine.Input** - unified pointer (mouse+touch), `IsTapIn` press-origin invariant
  (click-through fix), region blocking, drag/scroll/pinch, keyboard + gamepad + menu-navigation,
  coordinate-transform seam (`Identity` / `Matrix` / `VirtualResolution`), all behind the testable
  `IRawInput` seam.
- **KhaozEngine.Screens** - screen stack with top-to-bottom routing, `ConsumeWhenVisible` /
  `ConsumeWhenHandled` policies, and transitions.
- **KhaozEngine.UI** - widget library, `PrimitiveRenderer`, `TextInputHandler`.
- **KhaozEngine.Ecs** - minimal `World` / `Entity` / `ISystem`.

30 headless tests. Hardpoint migrated onto it.
