# KhaozEngine.Render3D

Stylized 3D rendering through `KhaozEngine.Gpu`, with `System.Numerics` math.

The package carries no ECS edge. Entity rendering (`Transform3D`, `MeshInstance`, `Scene3DBinder.Submit`) lives
in the `KhaozEngine.Render3D.Ecs` arm under the same namespace, so a render-only consumer does not pull `Ecs`,
`Simulation` and `Serialization`. The `Game3D` umbrella carries both.

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
- `FollowCameraController` gestures (since 20.17.0, off by default). Set `OrbitGesture` and `LookGesture` to a
  `PointerGesture` each and `OrbitButton` is ignored. The camera orbits only while a gesture drags, by one delta a
  frame, and a press begun while the game's `UiBlocked` is set never taps, and above a zero threshold never
  orbits. `TurnBodyActive` is true while `LookGesture` drags, for `MoveCommand.FaceCamera`, and
  `WantsPointerCapture` while either drags, for `SetPointerCaptured`. `OrbitTap` and `LookTap` report taps after
  `Update` or `UpdateInput`, and a tap counts only if the camera did not turn during that press. `UpdateInput` is
  `Update` without the target and boom clocks, for a game that advances those itself later in the frame, and it
  still needs the real frame time for a tap tolerance. Gesture replay and continuing motion use
  `MouseDeltaPoints`, so sensitivity stays in window points before and during capture. Tap origins keep
  framebuffer coordinates. Gate `UiBlocked` on `GuiSurface.HoverCaptured`. Scroll zoom, damping and boom recovery
  are unchanged, and with neither gesture set the controller behaves exactly as before. See
  docs/USING-KHAOZENGINE.md.
- `FollowCamera3DPresets.CreateMouseLook()` returns a fresh mutable camera with pitch stops at -80 degrees and
  1.36 radians (about 78 degrees), distance stops at 1.5 and 22 metres, and initial pitch/distance of 0.75 radians
  and 12 metres. It orbits a pivot 1.5 metres above `Target`, with zero eye-only `HeightOffset`, 0.3 metre ground
  clearance and boom recovery rate 4 per second. Bind the target, ground sampler and `Occlusion` or `BoomProbe`
  to the game. For a capsule-centre target, subtract its half-height from `PivotHeight` to keep the same aim above
  the feet. Gesture policy, target damping and frame-clock binding remain caller-owned. `new FollowCamera3D()`
  retains its legacy defaults.
- `FollowCamera3D.Warp(target)` / `SnapToTarget()` (since 10.65.0) - hard-cut the third-person follow camera onto a
  point with no ease (the 3D counterpart of `Render2D.CameraFollow.Warp`), for a teleport/respawn so the smoothed
  camera does not "fly" across the jump.
- `FollowCamera3D.Eye` is computed once per frame, not once per read (since 17.37.0). With the `Occlusion`
  spring-arm on, every read used to issue its own broadphase sweep, and `Forward`/`View`/`ViewProjection`/
  `WorldToScreen`/`ScreenToRay` all funnel through it. The cache is keyed on the inputs the last computation read,
  so writing any knob mid-frame recomputes, and it is bounded by `IIsoCamera3D.BeginFrame()`, a no-op default
  interface member `Scene3D.Begin` calls on the active camera so a world that moves between frames (a wall slides
  in, terrain deforms) is never answered with last frame's eye. Drive the camera without a `Scene3D` and you call
  `BeginFrame()` (or `FollowCamera3D.InvalidateEye()`) once a frame yourself. `OcclusionSweepCount` /
  `EyeComputeCount` are cumulative-since-construction counters for watching the load.
- `FollowCamera3D.FrameClock` (since 20.14.0, off by default). A per-frame id (`() => Clock.FrameCount` over a
  `GameApp`) that stamps each computed eye. A read reuses the eye only under the same id and inputs, and
  `BeginFrame` keeps an eye computed earlier in the same frame, so a game that reads the eye in update after moving
  the target, and again through the render, pays one computation and one `BoomProbe` call a frame instead of two.
  A camera read in update BEFORE it moves pays two computations a frame with a clock, where it pays one without, so
  leave the clock null for that shape. A clock that advances once per frame, before the frame's first camera read,
  never returns an eye from an earlier frame, so a still camera still recomputes once a frame and sees the world
  move behind the probe. A clock ticked between update and render instead lets the next update read reuse the
  previous render's eye, and one that stops advancing can hand the next latch an eye from before the stall. Null
  keeps the previous behaviour. See docs/USING-KHAOZENGINE.md.
- `FollowCamera3D.PivotHeight` / `Pivot` / `PitchLimit` (since 20.13.0). The camera orbits and looks at `Pivot`,
  which is `EffectiveTarget` raised by `PivotHeight` (default 0). `HeightOffset` still raises only the eye. A
  negative `MinPitch` puts the eye below the pivot looking up, and `Pitch` clamps to `[-PitchLimit, PitchLimit]`
  (85 degrees) after `[MinPitch, MaxPitch]`, so the view never degenerates against world up.
- `ICameraBoomProbe` / `FollowCamera3D.BoomProbe` (since 20.13.0). Any probe can stop the boom. The camera asks
  `Reach(origin, direction, length, radius)` once per computed eye, from the pivot toward the geometric eye, and
  places the eye at that reach less `OcclusionSkin`, floored at `MinOcclusionDistance`. The `Occlusion` physics
  sweep runs through the same path, the shorter reach wins, and `GroundHeight` clearance still runs last.
  `BoomProbeCount` counts probe calls cumulatively. `KhaozEngine.TileWorld.Render3D` ships `TileWorldCameraProbe`.
- `FollowCamera3D.BoomRecoveryRate` / `AdvanceBoom(dt)` (since 20.13.0). A pull-in stays instant and the boom
  eases back out as `AdvanceBoom` multiplies the held shortfall by `exp(-BoomRecoveryRate * dt)`.
  `FollowCameraController.Update` calls it. `Warp` and `SnapToTarget` clear the shortfall. The `Distance` setter
  shifts it by the change in distance, so a zoom in during recovery never moves the eye outward, a zoom out
  continues the ease, and a zoom in the open is instant. The default rate of 0 follows the probe both ways at once.
  Reading `Eye` with both `Occlusion` and `BoomProbe` detached clears the shortfall, so reattaching a clear seam
  keeps the full boom length.
  See docs/USING-KHAOZENGINE.md.
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
  `Transform3D.ToMatrix(Vector3 renderOrigin)` (in `KhaozEngine.Render3D.Ecs`) builds a reduced matrix for a
  consumer that wants one, which `Scene3D` itself never requires. Terrain chunk vertices, terrain texturing at range and depth precision are
  explicitly NOT fixed by it. See docs/USING-KHAOZENGINE.md.
- `Scene3D.CameraCut()` / `Scene3D.DebugView` / `Scene3D.LastTemporalDiagnostics` / `Post.Temporal` - the temporal
  rendering foundations. Each render draws from one latched view and the previous frame's view is kept, rebased
  across the one-cell steps the automatic render origin takes. Nothing changes until something asks for temporal
  rendering: `AntiAliasing.Temporal` (the anti-aliasing bullet below), or `SceneDebugView.MotionVectors` as a
  development aid. The `History`, `Disocclusion` and `Reactive` views show the resolve's decisions and take effect
  only under `AntiAliasing.Temporal`. Call `CameraCut()` on a teleport, a loading screen or a cutscene cut.
  A camera that moves further than `Post.Temporal.CutDistanceMetres` (16 m) or turns more than
  `Post.Temporal.CutAngleDegrees` (60 degrees) in one frame is a cut automatically, and so is a render origin jump the
  previous view cannot be rebased across. See docs/USING-KHAOZENGINE.md.
- `GltfLoader` / `GltfMesh` / `MeshPrimitives` / `MeshBuilder` - runtime glTF load (SharpGLTF) + procedural meshes.
- `Scene3D` + `Render3DSurface(AppWindow)` - multi-instance mesh draw (`LoadMesh`/`LoadTexture`/`Begin`/`Draw`
  with per-instance tint + `Material`, plus a per-instance dissolve overload `Draw(handle, transform, tint,
  material, dissolve, edgeWidth, edgeColor)` - the rigid mirror of the skinned `DrawSkinned` dissolve, folded
  into the shared model pipeline so it stays one instanced draw per mesh), per-mesh albedo textures, lighting, camera-facing billboards, an
  immediate-mode debug-draw overlay (line/ray/box/grid/axes/circle) plus depth-tested debug wire volumes
  (sphere/dome/cylinder/circle), composited into the window. **No unload call drains the device any more**
  (since 17.38.0, [#383](https://github.com/APKiwiOrg/KhaozEngine/issues/383)). `UnloadMesh`, `UnloadSkinnedMesh`,
  `UnloadTexture`, `UnloadSplatMaterial` and `UnloadTileGroundMaterial` all RETIRE their GPU resources through the
  seam's `GpuRetireQueue`, which destroys them a few frames later, once a fence (or the frame count on a backend
  with none) says the GPU is done. `UnloadProp` forwards to `UnloadMesh`, so it behaves the same. That is what
  took the per-chunk stall off the terrain streaming path and, for the skinned one, the per-despawn stall off an
  MMO client. The rule the old
  per-call `IGpuDevice.WaitForIdle` existed for is unchanged: a queued upload or draw may still reference the
  resource, so nothing is destroyed in the frame it was retired in. The unload bodies live in `Scene3D.Unload.cs`,
  and `Scene3D.RetiredResourceCount` is the observable: a healthy streaming world shows a small number that returns
  to 0 shortly after a burst. The same scene queue owns geometrically replaced buffers and resource sets from the
  model, shadow, decal, particle, distortion, water and overlay renderers. Those renderers keep no private retired
  lists, so a grow survives until the same fence or frame boundary and teardown drains the queue once before GPU
  resources are destroyed. Cascade and point-shadow replacement, MRT attachments and their pipelines, post
  bindings and temporal display targets use that queue too. This includes cached depth bindings for decals,
  particles, distortion and water. `BeginFrame(frame.Submission)` shares the host
  completion fence in its prepare phase, avoiding the standalone `Begin()` queue's extra submission.
- `Scene3D.LoadSurfaceMaps` uploads each present glTF material map transactionally. A later upload failure
  retires the maps already created. `UnloadSurfaceMaps(in maps)` releases each distinct valid map handle
  through the same deferred queue. Unload the meshes that reference a map bundle first. All-absent bundles
  neither upload textures nor release scene defaults.
- `RigidInstanceDraw` / `SkinnedInstanceDraw` / `MotionKey` - draw descriptors and motion keys. A descriptor carries
  every knob of one draw (mesh, absolute transform, tint, material, dissolve and edge, shadow casting, the
  shadow-only flag, inverted shadow dither and complement phase) plus a `Motion` key, for rigid and skinned draws.
  `Scene3D.Draw(in RigidInstanceDraw)` and `Scene3D.DrawSkinned(in SkinnedInstanceDraw, boneMatrices)` queue one.
  Every older `Draw`, `DrawShadowOnly` and `DrawSkinned` overload builds a descriptor with no key and forwards, so
  existing callers queue exactly what they did, and a new draw knob becomes a descriptor property rather than another
  overload. The constructor sets a white tint, `Material.None` and casting shadows (the plain rigid overload's), and
  shadow-only without casting is refused as before. `MotionKey` is a stable 64-bit identity for a draw that moves:
  `MotionKey.From(id)` wraps an id the game owns and `MotionKey.Combine(key, part)` derives one key per part of a
  multi-part body. While temporal rendering is active (see the `Scene3D.CameraCut()` bullet above) the scene
  remembers each keyed draw's previous world transform and, for a skinned draw, its previous bone palette. A draw
  with no key is static and gets camera-only motion, which is right for terrain, tile ground and placed props. Two
  draws of the same kind with one key in a frame collide, the last wins, and `LastTemporalDiagnostics.KeyCollisions`
  counts it. A rigid and a skinned draw never collide, and a shadow-only draw is never recorded. `CharacterAvatar`,
  `SkinnedLimb`, `Scene3DBinder.Submit` and `Scene3DTileWorldScene` key or forward keys themselves. See
  docs/USING-KHAOZENGINE.md.
- `Scene3D.GetOrLoadMesh` / `GetOrLoadSkinnedMesh` / `GetOrLoadTexture` - keyed, idempotent loads for a scene that
  outlives what it draws ([#250](https://github.com/APKiwiOrg/KhaozEngine/issues/250)). Each takes a key plus a
  loader and runs the loader only the FIRST time that key is seen, returning the cached handle after that, so a
  renderer that rebuilds its asset set on every run restart stops re-uploading it. The plain `Load*` calls are
  unchanged and still upload every time. Ownership does not change: a cached handle is owned by the scene exactly
  as the underlying `Load*` left it, `Dispose` frees it with everything else, and the key tables die with the
  scene. Eviction is explicit and by key: `UnloadSharedMesh` / `UnloadSharedSkinnedMesh` / `UnloadSharedTexture`
  unload through the matching `Unload*` and forget the key (`false` when nothing was cached under it), so the next
  `GetOrLoad*` uploads again. There is no refcount and nothing evicts on its own. Calling the plain `Unload*` on a
  cached handle directly leaves the key pointing at a freed handle, so pair the two.
  `Scene3D.SharedAssetCount` is the diagnostic.
- `Scene3D.PrepareFrame(width, height)` and `Scene3D.PrepareFrame()` - the frame's pre-recording phase. Call it
  once per frame, after the frame's `Draw*` calls and BEFORE opening the command list the scene is recorded into.
  Subsystems whose per-frame GPU work cannot go in the frame's list (the FFT ocean's priming dispatch, which needs
  a submit + device wait of its own) run there instead: opening a second command list while the frame's is
  recording resets the device's immediate context on Direct3D11 and corrupts the frame (issue #423). The
  viewport-aware `PrepareFrame(width, height)` also fixes the frame's render targets and prepares bound optical
  water. The no-argument form is the legacy form and throws when bound water is queued. `GameApp3D` prepares with
  its frame size. `Render3DPreview.Capture` and the `Render3DSnapshot` captures call `Begin` themselves and
  prepare with their own target size, so bound water needs no preparation in their draw callback, and a callback
  that also calls `PrepareFrame` with that size is a latched no-op. Since the record entry point is internal,
  those hosts and `Render3DSurface.Render` are the only ways a scene is rendered. A custom host calls
  `PrepareFrame(width, height)` after queueing and before recording. Recording consumes the prepared frame, so a
  scene records once per `Begin` and a second viewport means a second `Begin`. Only a frame that queues bound
  water freezes its inputs at preparation. A frame with no bound water keeps the legacy late binding on either
  form, so its targets may still resize and its clipmap program may still be created while recording. The
  no-argument form costs nothing on a frame that queues no water. The viewport form always sizes the targets and
  opens `device.water.prepare`. `Render3DSurface.Render` records inside the frame's open list and calls the
  no-argument form, so bound water must already be prepared at the frame's size. On a WINDOWED host,
  `Render3DSurface.Render`'s own
  call is a safety net rather than the effective one - the frame's list is already open by then - so queue and
  prepare the scene in `AppWindow.Run`'s `onPrepare` callback instead (`GameApp3D` does, in `OnPrepareWorld`, see
  issue #429). A host that does neither no longer gets a silently corrupt frame on one backend: every scene path
  that opens a list of its own goes through the seam's open-recording register, so the nesting is a
  `GpuNestedRecordingException` naming both sides on every backend (issue #424). That refusal is the whole of the
  residual since 18.0.0, and the pre-record phase it points at was KEPT rather than retired with the Veldrid leg
  it was built for ([#690](https://github.com/APKiwiOrg/KhaozEngine/issues/690)). Existing producers still
  use their own lists, which cannot be opened inside the frame's recording. Dependent buffer dispatches
  themselves can now share one list without a drain. The calls that refuse mid-frame
  are a mipped `LoadTexture`, `LoadSplatMaterial`, `LoadTileGroundMaterial`, `DebugReadShadowMap`,
  `DebugReadSplatAlbedoMip`,
  `Render3DPreview.Capture`, `Render3DPreview.ReadbackRgba`, and `Scene3D.Begin` on a device with GPU completion
  fences (its retire barrier submits). Every one of them frees whatever it had already built before it was
  refused, so catching the exception and moving the call costs nothing. **`Scene3D.Begin`'s refusal is
  data-dependent and the others are not**: the retire barrier only runs when something was retired since the
  previous `Begin`, so a mis-phased host boots clean and throws on the first mesh unload, which can be minutes
  into a session rather than on frame one.
- `TextureMipPolicy` - an optional trailing argument on both `LoadTexture` overloads choosing how much of the mip
  chain to build. `Full` (the default, and what `default(TextureMipPolicy)` is, so every existing call is
  unchanged), `None` for level 0 only, and `AtlasGrid(columns, rows, minCellTexels = 4)` to stop at the coarsest
  level where a grid cell still holds `minCellTexels` texels on its shorter side, which keeps a bilinear tap from
  reaching into the neighbouring cell. `LevelsFor(width, height)` is the pure arithmetic behind it.
- Splat terrain materials: `Scene3D.LoadSplatMaterial` uploads exactly five tileable albedo and normal layers
  with full mip chains and returns a `Scene3D.SplatMaterialHandle`. Bind it with `LoadMesh(mesh, material)`
  to blend the layers by the mesh's per-vertex splat weights. The scene owns the shared material until
  `UnloadSplatMaterial` or scene disposal. Load outside frame recording because mip generation uses its own
  command list. [KhaozEngine.Terrain.Render3D](../KhaozEngine.Terrain.Render3D/README.md) provides the layered
  material recipes and terrain mesh builder.
- Tile ground (`Scene3D.LoadTileGroundMaterial` / `TileGroundMaterialHandle` / `LoadMesh(mesh, material)` /
  `UnloadTileGroundMaterial`, `TileGroundLayerImage`, `TileGroundMaterialConfig`, since 17.38.0) - the N-layer
  sibling of the splat terrain pipeline, for a ground surface whose material palette is a CONTENT catalog rather
  than five fixed layers. `KhaozEngine.TileWorld.Render3D` is the shipped consumer, and any mesher that follows the
  vertex contract below can drive it. The splat pipeline could not be widened for it: its layer count is a
  `const 5`, four weights ride in `ModelVertex.Color` and the fifth is the remainder.
  - **The set.** `LoadTileGroundMaterial(width, height, layers, baseSpecStrength = 0.15f, sampler = null)` uploads
    1 to `TileGroundMaterialConfig.MaxMaterials` (64) tileable albedo layers, all the same `width` x `height`
    RGBA8, into ONE texture array with a full mip chain, plus a params UBO carrying each layer's `Tint` and
    `TilesPerMetre`. A layer's INDEX in the list is the SLOT a mesh vertex names, so the caller owns the
    material-to-slot mapping. A non-positive size throws naming `width` or `height`, a layer count outside 1..64
    throws naming the count, and a layer whose bytes are not `width * height * 4` throws naming that layer's index
    and both sizes. Albedo only by design: there is no normal array,
    and the specular exponent is a constant 28 (what the splat pass reaches at roughness 0.5) with
    `baseSpecStrength` as the whole set's strength, because an albedo layer carries no roughness channel. A set of
    exactly ONE layer is a one-layer array, not a padded two-layer one: between 17.38.0 and 17.39.0 it was
    duplicated into a second slice, because the GPU seam could not express a one-layer array and the fragment
    declares `texture2DArray` (#666, fixed by `GpuTextureDescription.IsArray`).
  - **The vertex contract, for this pipeline only.** `ModelVertex` is unchanged, so the upload path is the ordinary
    one, but the fields are repurposed: `Color` is the vertex's four weights, `Uv.x`, `Uv.y`, `Tangent.x` and
    `Tangent.y` are four material SLOTS as floats holding integers (constant across a triangle, so interpolation
    cannot smear them), `Tangent.z` is a per-vertex brightness multiplier and `Tangent.w` is 0 for the ordinary
    four-layer path. A positive value names an optional overlay slot plus one, whose weight is one minus the
    sum of `Color`. The fragment reads a slot as `int(x + 0.5)` clamped to 0..63, takes up to five `textureGrad`
    top-down taps at `worldXZ * TilesPerMetre[slot]` (plus side taps on steep ground, see Slope below),
    renormalises ordinary weights by their own sum (or preserves the
    opted overlay remainder) and
    multiplies by the slot tint and the jitter. **The jitter is a MULTIPLIER, not an offset, so `Tangent.z` of 0
    renders that vertex BLACK**: write 1 for none. A mesh built for the model pipeline, where `Tangent` is a
    tangent frame, is not a tile-ground mesh.
  - **Slope (since 20.36.0, #1289).** The fragment reads the slope off the interpolated vertex normal, so it needs
    no vertex data of its own and is continuous wherever the normal is. Two things follow from it, both owned by
    `TileGroundSteepShading`. TRIPLANAR: past `TriplanarStartDegrees` (45) every material's tap adds two side
    projections, one along world X (u = world Z) and one along world Z (u = world X), each with v running down world
    Y so a texture keeps its up. They fade in to their full share at `TriplanarFullDegrees` (60), weighted by the
    normal's axis components raised to `TriplanarSharpness` (4), and a projection whose weight falls to 0.001 or
    less is dropped and the rest renormalised. Below the start the projection is the one top-down tap it always
    was and costs nothing extra. That holds for every material. STEEP COMPANION, for layers that carry one only:
    `TileGroundLayerImage.Steep` (`TileGroundSteepLayer`: `CompanionSlot`, `FromDegrees`, `ToDegrees`) names
    another layer of the set. Wherever a tap of that layer is taken, in any corner slot or the overlay slot, it
    becomes `mix(layer, companion, s)` with `s` a smoothstep over the band on the normal's Y component. The
    companion's own companion is never followed. `TileGroundMaterialConfig.BuildParams` refuses a companion that
    names its own layer, names a slot outside the set, carries a band that does not rise strictly inside
    (0, 90) degrees, or carries one whose two ends pack to the same cosine. `Scene3D.LoadTileGroundMaterial` packs
    the params before it creates any GPU resource, so a refused set leaks nothing. Every derivative stays hoisted into uniform control flow and every side tap is a
    `textureGrad`.
  - **Constraints worth knowing.** Entries past the layer count are zeroed, while the shader clamps every mesh slot
    to the last real texture layer before reading either resource. A hand-built mesh naming an invalid slot
    therefore shows the set's last material instead of sampling a nonexistent array slice. The pipeline binds TWO
    sets, the same split the splat pipeline has: set 0 is the shared frame block both stages read, and set 1 is the
    material's own `TileGroundParams` buffer (`vec4 TintTiling[64]`, a `Misc` vector carrying base specular
    strength and layer count, a `Triplanar` vector and `vec4 SteepBand[64]`, 2080 bytes written once at load),
    then the albedo array, its sampler, and the shadow map and its sampler LAST, which is the binding-order
    convention the terrain pass established. Until 18.1.0 the frame block and the params rode in ONE buffer per
    material, re-uploaded whole every frame, which is what #727 unfolded. Tile ground CASTS shadows, like a model
    mesh and unlike the splat terrain.
  - **Lifetime.** The material is owned by the scene, shared by every mesh drawn with it, and unloading a mesh does
    not free it. `LoadTileGroundMaterial` builds its mip chain on a command list of its own, so it refuses
    mid-frame with `GpuNestedRecordingException` (load once per view construction or catalog change).
    `UnloadTileGroundMaterial` retires the whole bundle (array, UBO, resource set, any owned sampler) as ONE
    resource through the scene's `GpuRetireQueue`, so it never drains the device.
- `Render3DPreview(AppWindow, width, height)` - live render-to-texture: render a model into a sampleable
  `Render2D.Texture2D` on the same device and draw it inside a 2D `SpriteBatch`/Gui panel (unit inspectors, shop
  previews, item icons). Load meshes + frame the camera once via `.Scene`, then call `Capture(drawFrame)` each
  frame (target reused, no per-frame allocation). Transparent background by default
  (`PixelPostProcessSettings.TransparentBackground`) so it composites cleanly. `Resize` drains the device
  before disposing the old target/framebuffer, since the previous frame's queued render may still reference
  them.
- `Render3DSnapshot.Capture` - headless one-shot RGBA8 capture. Existing callers still receive the byte array.
  `CaptureWithBackend` runs the same render path once and returns `Render3DCapture`, whose `Rgba`, `Width`,
  `Height` and `Backend` identify the pixels and the exact device that produced them. A golden caller passes
  `capture.Backend` to `GoldenImage.Check` instead of resolving or guessing a backend separately.
  `CaptureSequence(width, height, setup, drawFrame, frames, onFrame, warmupFrames, shadows)` streams frames
  from one device, scene and render target. `setup` runs once. `drawFrame(scene, frame)` runs after `Begin`
  for every zero-based frame, including warm-up. `frames` includes the skipped `warmupFrames` prefix, so
  `onFrame(frame, capture)` first receives index `warmupFrames`. Each `Render3DCapture` owns independent
  top-to-bottom RGBA8 bytes and carries the dimensions and actual backend. Readback fences the GPU and
  invokes the callback synchronously. The engine retains no frame collection. TAA history survives between
  frames, and a callback exception stops capture and disposes its resources. Use stable motion keys on moving
  draws for TAA, as in a windowed scene.
- `PixelPostProcessSettings` / `Palette` / `Palettes` - palette quantization, Bayer dither, depth/normal
  edge outline, cel bands, all independently toggleable (the smooth look is the default).
- Anti-aliasing: `PixelPostProcessSettings.Quality.AntiAliasing` (a `RenderQuality` container) is the AA dropdown
  most games ship - `AntiAliasing.Off` / `.Fxaa` / `.Msaa(2|4|8)` / `.Ssaa(factor)` / `.Temporal`. Validate a menu
  choice against the device with `AntiAliasing.ResolveFor(caps)` (selects the largest member of
  `GpuCapabilities.SupportedMsaaSampleCounts` no greater than the request, or falls back to FXAA). Default `Off`,
  so the low-level `RenderScale` / `Supersample` fields still govern.
  SSAA supersamples the whole image (geometry AND shaded interiors, the only one that kills high-frequency terrain
  shimmer) and now downsamples correctly at ANY factor via a mip-filtered blit. FXAA is a cheap one-pass edge
  smoother. MSAA multisamples geometry edges only. `.Msaa(2, postFxaa: true)` keeps the FXAA post filter
  after the multisample resolve without increasing internal resolution. `UsesFxaa` reports both forms,
  and the post-filter choice survives device sample-count clamping. The one-argument MSAA factory keeps
  its existing unfiltered behavior. `RenderQuality` is the extension point for further quality knobs.
  `RenderScale.FixedInternal` (the default) keeps a single bilinear tap on its final downscale for byte-stable
  goldens, which under-samples on a window smaller than the fixed internal target. Opt into the same mip-filtered
  blit machinery there too with `PixelPostProcessSettings.MipFilterFixedInternalDownscale` (default `false`), or
  switch to `RenderScale.MatchViewport` outright. FXAA reads only the current base mip of intermediate post
  targets. The final downscale generates and samples their mip chain after FXAA has finished.
  `.Temporal` renders at a `Post.Temporal.Upscale` preset from `Native` through `UltraPerformance` at or below
  the window, jitters each frame and resolves a stable full-resolution image from history, with a light contrast
  adaptive sharpen (`Post.Temporal.Sharpness`) and a material mip bias (`Post.Temporal.MipBiasOffset`). It is
  single-sample and never combines with MSAA. Moving draws
  pass a `MotionKey` through `RigidInstanceDraw` or `SkinnedInstanceDraw`, camera jumps call `Scene3D.CameraCut()`,
  and `Scene3D.DebugView`, `Scene3D.RequestTemporalCounts()` and `LastTemporalDiagnostics` show what the resolve did.
  Lit rigid/skinned, foliage and ground paths export final key-shadow visibility to an R8 attachment. Transparent
  writers preserve it and cutout/dissolve discards expose the receiver below. Depth-qualified current/history
  means use relative tolerance 0.02 and finite reference/sample depths. A stored R8 difference strictly above 0.1
  releases the thin-feature lock and hold without changing accumulated weight or display share. This applies to
  either sign at every preset, independently of the existing colour-based shading release. The threshold is on
  stored values. Under round-to-nearest UNORM conversion, an exact input 0.1 stores as 26/255. A driver that
  truncates stores 25/255 instead, so the exact boundary case differs because release compares stored values.
  No public tuning API is added.
  Certified fully lit current and read-index frames use a separate compiled program without the visibility check.
  R8 storage and copies remain. Shadowed and uncertified frames keep the ordinary check.
  The signal and two R8 histories cost three bytes per internal texel, plus a 16-byte previous-jitter buffer.
  Histories follow colour indices, resets and target generations, copy once per resolving frame, and retire with
  TAA off or scene disposal. Repeated renders retain read history and jitter. Standalone resolves without lit
  input bind a fully lit fallback. Motion plus visibility needs five colour attachments. Below five, the engine
  logs the capability once and keeps bilinear filtering without visibility release on its supported layout.
  See docs/USING-KHAOZENGINE.md for the presets, the edge outline, cost, memory and known limits.
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
  - `ShadowSettings.ForDetail(ShadowMapDetail)` creates the full directional shadow-map profiles below. Every profile
    selects `ShadowMode.ShadowMap`.

    | Detail | Per-cascade resolution |
    | --- | ---: |
    | `ShadowMapDetail.Low` | 1024 |
    | `ShadowMapDetail.Default` | 2048 |
    | `ShadowMapDetail.High` | 3072 |

    ```csharp
    ShadowSettings shadows = ShadowSettings.ForDetail(ShadowMapDetail.Default);
    var game = new MyGame(options, shadows);
    ```

  - `ShadowMode.ShadowMap`: a depth-only pass renders the instanced casters (models cast, and splat terrain receives
  only unless `Scene3D.TerrainCastsShadows` is set) into
  a CASCADED ortho light-space depth atlas - `ShadowCascadeCount` cascades (default 3) side by side in one R32F
  texture, each fitted to the bounding sphere of its own slice of the camera's ACTUAL view frustum (frustum-slice CSM,
  not a fixed-radius circle around a focus point, so shadow sharpness no longer depends on where the camera looks and
  there is no gaze-ground-ray jump on a pan), texel-snapped per cascade to kill shimmer. The slices run from the tight
  near cascade (`ShadowNearDistance`) out to `ShadowMaxDistance`. The shared lighting block picks the tightest cascade
  per fragment and samples it with bilinear 3x3 comparison PCF plus slope-scaled bias to shadow the KEY light's diffuse+spec only for BOTH
  models and terrain, cross-fading toward the next cascade inside a `ShadowCascadeBlend` UV border band so a cascade
  hand-off is invisible instead of a visible square seam, and fading the outermost cascade's term to lit at its border
  so the coverage limit is invisible (no hard box, no sliding coverage). Caster visibility for the shadow pass unions
  ALL cascade frustums (a caster camera-culled from the main pass but still inside any cascade still casts its
  shadow). Every drawn mesh casts automatically, unless the draw opts out: `Draw(handle, transform, tint, material,
  castsShadows: false)` (and the dissolve overload's `castsShadows` argument) keeps that instance out of the depth
  pass while it still draws and still RECEIVES shadows - the per-instance policy a consumer sets on dense decorative
  geometry whose hundreds of small cast shadows cost more than they read. A caster carrying a rigid dissolve is drawn
  through a dissolve-aware depth pipeline instead, so its SHADOW erodes with the same world-space noise mask that
  erodes the mesh: a prop fading out at its draw radius no longer casts a fully solid shadow under an almost-invisible
  caster, and across an HLOD crossfade the props' shadow thins out as the merged mesh's thins in rather than both
  casting at full strength. A MASK caster (a mesh loaded with an alpha cutoff and an albedo, such as a leaf card)
  is drawn through an alpha-cutout depth pipeline, so its shadow is its silhouette rather than the solid quad. That
  pipeline culls nothing, so a single-sided card casts whichever face it turns to the sun. Only those spans sample
  the albedo: an opaque caster keeps the depth-only pipeline with no texture sample and no discard. Skinned draws follow the same policy: `DrawSkinned(..., material, castsShadows: false)` (and the
  dissolve overload's `castsShadows` argument) keeps a character out of the depth pass, and a dissolving skinned
  draw sheds its shadow with its body on both skinning paths. Both descriptors default `ShadowOnly` and
  `InvertShadowDissolve` to `false` and `DissolveComplement` to `0f`. `ShadowOnly` suppresses colour and motion
  while retaining key and point shadows. A skinned shadow-only draw without casting is refused before palette
  or queue mutation. `DissolveComplement = 1f` keeps the opposite noise samples in colour and ordinary shadow
  dissolve, including no colour coverage at threshold zero and complementary coverage at threshold one.
  HLOD handoffs use `DissolveComplement` with the individuals' threshold for both colour and shadow. They do not
  use the independent shadow-only inversion. `InvertShadowDissolve` selects the inverted shadow dither, leaving colour
  coverage unchanged. These policies work with CPU and GPU skinning. `DrawShadowOnly(handle, transform)` is the opposite
  of the opt-out: the instance records depth for the key light and never draws in the COLOUR pass, so a view can
  hide geometry from the eye and keep the shadow the world still throws (a tile world's hidden roof), counted by
  `ShadowOnlyInstances` and by neither `DrawnInstances` nor `CulledInstances`. The per-draw ones are inert at
  their defaults (no dissolve, casting on, not shadow-only), a MASK mesh is the only one that takes the cutout
  pipeline, and a frame with none of them is byte-identical to before. `Scene3D.TerrainCastsShadows` (default
  `false`) is the scene-wide policy: leave it
  off and splat terrain stays receive-only as it always was, set it and terrain chunks join the caster list through
  the same per-cascade cull, which is what a SCULPTED world with real hills wants (a 49 m mountain otherwise throws
  no shadow while the trees standing on it do). A degenerate camera makes
  `ComputeShadowCascades()` return `0` and disables shadows for that frame rather than throwing. Knobs on
  `ShadowSettings`: `ShadowCascadeCount` (default `3`, `1..4` via `ResolvedCascadeCount`), `ShadowNearDistance`
  (default `16`, the near cascade's view-depth reach - smaller packs texels onto the near action) and
  `ShadowMaxDistance` (default `130`, the far reach, `ResolvedMaxDistance` clamps it `>= ShadowNearDistance`).
  `ShadowCascadeBlend` (default `0.15`, the cross-cascade blend band width as a UV fraction, `0` restores the hard
  cut). `ShadowMapResolution` (default `2048`, the PER-CASCADE resolution, so the atlas is `ShadowCascadeCount *
  ShadowMapResolution^2 * 4` bytes). Seed `ShadowMapResolution` and `ShadowCascadeCount` through the `ShadowSettings`
  construction seam: `new Render3DSurface(window, shadows)` / `Render3DPreview` /
  `Render3DSnapshot.Capture(..., shadows)` / `GameApp3D` `base(options, shadows)`. To change an existing scene, call
  `Scene3D.RequestShadowMapDetail(ShadowMapDetail)` for the standard Low, Default, and High profiles, or
  `Scene3D.RequestShadowMapLayout(resolution, cascadeCount)` for a custom layout. Requests validate immediately and
  the latest request wins when several arrive before the next `Scene3D.Begin()`. That frame boundary applies the
  request before any command list begins. An unchanged layout allocates nothing. A changed layout builds the
  replacement atlas and every receiver binding as one transaction, then swaps them together without a device drain. If the
  device rejects the replacement, the scene logs one error for that request and keeps the old drawable layout.
  Requests change only the atlas layout. The current shadow mode and the other live shadow settings stay unchanged.
  Direct writes to either committed property still throw. The explicit request methods are the only live resize path.
  Requests follow the rest of `Scene3D` and originate on the scene thread. No cross-thread mutation lock is added.
  A request after disposal throws `ObjectDisposedException`. Disposal clears an unapplied request and frees only
  the current resource graph and queued retirements. A successful swap retires the old atlas and receiver sets
  until the GPU completes the frames that may still read them.
  `ShadowStrength`, and
  the acne biases `ShadowNormalOffset` (default `2.5` texels, the extent-aware normal-offset bias, scaled PER CASCADE
  so far cascades do not acne and near ones do not detach) plus the tiny residual depth biases
  `ShadowConstantBias`/`ShadowSlopeBias` (defaults `0.0004`/`0.0015`). On a device without depth-sample support
  (`GpuCapabilities.SupportsShadowMaps` false) it degrades to `Blob`. The atlas persists across frames, so the pass
  **dirty-skips**: it re-renders only when a shadow-relevant input changed (ANY cascade's fitted matrix, the rigid
  caster set/transforms, the resolution, any animated skinned caster present, or the skinned casters the last
  rendered pass drew having all gone) and otherwise reuses the prior atlas,
  so a mostly-static scene stops repainting it every frame. A skipped pass adds zero shadow draw calls to
  `LastFrameStats`. Read `Scene3D.ShadowPassSkippedLastFrame` for a simple diagnostics signal, or
  `Scene3D.LastShadowPassDiagnostics` (a `ShadowPassDiagnostics`) for the whole last-frame decision: `Rendered` /
  `Skipped` / `HadPrevious`, one bit per dirty reason (`AnySkinnedCaster`, `SkinnedCastersCleared`,
  `ResolutionChanged`, `LightMatrixChanged`,
  `CasterDataChanged`), `SkinnedCasterCount` / `CascadeCount`, and what the pass actually recorded, namely
  `RigidSpanCount(cascade)` / `TotalRigidSpanCount` plus the raw `RigidDrawCalls` / `SkinnedDrawCalls` /
  `TotalDrawCalls`. Same last-frame shape as `PassTimingsMs`, always on and allocation-free, so a game can sample it
  every frame into a telemetry line. The counts are what THIS frame recorded, so a skipped frame reports zero of them
  (the `ShadowCascadeSpanCount` / `ShadowCascadeCasterCount` properties keep reporting the last rendered pass instead).
  - **Underwater key light**. Every lit receiver inside an opted-in water slab queries the shared cluster grid
  once, then selects the lowest surface above it, keeping submission order on ties. Rectangle minima and
  `FloorY` are inclusive, rectangle maxima and the surface are exclusive. The key-shadow lookup moves to the
  refracted surface entry with the receiver's normal offset and slope bias and selects cascades there.
  With refractive index `1.333`, depth `h`, and refracted travel direction `t`, the in-water path is
  `L = h / -t.y`. Only key diffuse and specular receive `exp(-AbsorptionPerMetre * L)`. Ambient, indirect,
  fill, point lights, emissive and fog keep their values. Attenuation also applies with shadows disabled, while
  exported key visibility remains exactly one for non-positive shadow strength. Empty water counts skip
  buffer reads, and depth zero meets the dry result. Submerged-caster marching and penumbra widening remain
  pending, so a submerged rock or a bed above a cave does not yet occlude this entry lookup.
  - The fit also quantizes the LIGHT's rotation, not just its focus translation: `ShadowLightHoldTexels` (default
  `1`) and `ShadowLightHoldCasterHeight` (default `12`) hold the direction the cascades were last fitted from until
  the sun has turned far enough to drag a worst-case caster's shadow by that many shadow texels, so a stationary
  scene under a day/night cycle stops re-recording the whole atlas every frame. The threshold is elevation-corrected
  and recomputed per frame (`budget * (2r/res) * sin^2(e) / h` on the tightest active cascade), because a low sun
  throws shadows far: it is about 43x tighter at a 5 degree dusk than a 35 degree afternoon, and stands down near
  the horizon. That standdown elevation falls out of the same formula rather than being a constant, so it scales as
  `1/sqrt(r)` with cascade 0's camera-derived radius: about 2.6 degrees on a wide outdoor framing where `r` is
  around 60 m. Only the shadow FIT is held, so `Post.LightDirection` still drives diffuse, specular, sky and
  water live, and a caster that moves under a held sun still re-records via `CasterDataChanged`, so no shadow can
  ghost. `ShadowLightHoldTexels = 0` disables it and restores the pre-17.36.1 fit byte for byte.
  - Validate a menu choice with `Shadows.ResolveFor(caps)` and read `.Effective`/`.Degraded`/`.Reason` (same
  `ResolveFor`-clamps-a-request pattern as AA, never throws). With `Off` the blob queue is ignored and the shadow tail
  sits at strength 0 (never tapped), so existing scenes are byte-stable.
  - POINT lights get their own omnidirectional maps out of a second pass, so a lantern stops at a wall instead of
  pooling through it. `Scene3D.AddLight(pos, color, radius, intensity, LightShadow)` carries the request:
  `LightShadow.None` (the default, and what the four-argument overload forwards, so a scene queuing none of the others
  is byte-identical), `LightShadow.Static(key)` for a CACHED map re-rendered only when the light moves, its radius
  changes or the rigid casters inside its radius change, and `LightShadow.Dynamic` for one rebuilt every frame. The
  atlas is one R32Float texture of six face columns and enough rows for the static set plus its dynamic reserve,
  storing LINEAR distance over radius,
  so the receiver picks a cube face analytically and needs no matrices. Nothing is allocated until a frame has carried
  a request, and the allocation and every rebind happen at the FRAME BOUNDARY, so the first frame to ask renders
  unshadowed and the frame after it carries the map. Knobs on `ShadowSettings.PointShadows` (a `PointShadowSettings`):
  `Enabled`, `FaceResolution` (default `256`, `64..1024` via `ResolvedFaceResolution`), `MaxShadowedLights` (default
  `8`, `1..256` via `ResolvedMaxLights`, the configured capacity floor and dynamic-effect budget),
  `MaxStaticRebuildsPerFrame` (default `2`), `MaxDynamicLightsPerFrame` (default `4`, nearest-eye-first), and the
  radius-normalized `Bias`/`SlopeBias` (defaults `0.01`/`0.02`, clamped
  `0..MaxBias` via `ResolvedBias`/`ResolvedSlopeBias`).
  Every submitted point light reaches a growable shared structured buffer. CPU-built conservative 3D frustum
  clusters select per-fragment indices in the common lighting shader. Perspective depth is logarithmic and
  orthographic depth is linear. An overflowing cluster evaluates the full list, so capacity pressure does not
  remove light. The shader rejects lights outside their radius before shading, and the host no longer picks
  a camera-nearest subset. Keyed static shadows
  reserve rows independently of camera distance. Atlas resolution falls before static capacity when a larger
  set needs space, and an unsupported layout remains visible as an allocation refusal. Dynamic effects cannot
  evict static rows merely because the camera moves closer to them.
  A light placed INSIDE closed geometry, which is every lamp model, is fully shadowed by its own fixture unless
  `LightShadow.NearRadius` clears it: set it past the fixture's farthest part from the flame and short of the nearest
  surface that must still block, through `LightShadow.Static(key, nearRadius)`, `DynamicWithNearRadius(nearRadius)` or
  `WithNearRadius(metres)`, and `0` (the default) is the pass exactly as it was.
  A sphere cannot clear a WALL-MOUNTED fixture, whose parts reach further from the flame than the wall stands, so
  `LightShadow.Static(key, exclusionMin, exclusionMax)` or `WithExclusionBox(min, max)` leaves a world-space box out
  of that light's map instead: pass the fixture's own world bounds and the wall a centimetre outside still casts.
  The edge is SOFT by default (`PointShadowSettings.Filter`, a `PointShadowFilter`): a contact-hardening filter
  whose penumbra is `LightSizeMetres` (default `0.15`) times the receiver to blocker gap over the blocker
  distance, capped at `MaxPenumbraTexels` (default `6`), sampled by direction so it crosses cube faces without a
  seam. `PointShadowFilter.Hard` is the original four-tap edge, unchanged.
  A `Dynamic` light has no identity across frames and its row lives for one frame, so a dynamic light whose map was
  not redrawn on a given frame renders UNSHADOWED for that frame rather than sampling an older one. Stay at or
  under the budget, raise it, or prefer `LightShadow.Static(key)` for anything that does not move.
  `PointShadowSettings.AtlasBytes` estimates the configured base-atlas floor (27 MiB at the defaults).
  `ShadowSettings.ForDetail` seeds the same three profiles (`Low` off, `High` 384 by 12 at about 91 MiB).
  `Scene3D.RequestShadowMapDetail` carries a profile on a
  live scene and `Scene3D.RequestPointShadowSettings` requests a custom one at the next frame boundary.
  `Scene3D.DrawSkinned` and its dissolved variants cast by default through CPU or GPU skinning. Static lights keep
  rigid casters (`DrawShadowOnly` included) in cached base rows and render each current skinned pose in a compact
  transient atlas. Dynamic lights clear and redraw rigid and skinned casters together in their selected base row.
  `castsShadows: false` excludes a skinned draw from point work. The transient atlas grows to exact demand at a
  frame boundary, keeps high-water capacity within a compatible base layout, and never exceeds its base row count.
  Receiver hard and soft taps compare the nearer of the base and transient depths. An unassigned transient row
  takes the original base-only sampling path.
  `Scene3D.ResolvedPointShadows` reports live `BaseAtlasBytes`, `TransientAtlasRows`, `TransientAtlasBytes` and
  `TotalAtlasBytes` beside the live layout and any degradation. A refused transient texture or framebuffer keeps
  the previous compatible atlas or the white default. A failed receiver-set rebind keeps the complete old pair.
  On a base shrink, transient allocation may fail while the new rigid base still lands with the white transient
  default. Read `Scene3D.PointShadowedLights`, or `PointShadowedLights`/`PointStaticRebuilds`/
  `PointDynamicRenders`/`PointTransientDemand`/`PointTransientRowsRendered`/
  `PointDynamicSkinnedDrawCalls`/`PointStaticTransientSkinnedDrawCalls`/`PointFaceDrawCalls`/`PointSlotsInUse` on
  `ShadowPassDiagnostics`. `PointStaticRebuilds` counts rigid base rebuilds only.
  Point-light MASK alpha testing remains [#1098](https://github.com/APKiwiOrg/KhaozEngine/issues/1098).
  Skinned colour and outline cutouts are available, while key-light skinned cutout remains
  [#1097](https://github.com/APKiwiOrg/KhaozEngine/issues/1097). No draw overload or point-shadow quality
  field was added.
- Frustum culling: `Scene3D.FrustumCulling` (on by default) skips any queued mesh instance whose world-space
  bounding sphere is entirely outside the camera frustum, so off-screen terrain chunks and props cost nothing to
  draw. Pixel-neutral by construction (only provably-offscreen geometry is dropped), so existing renders stay
  byte-stable. Explicit `CastsShadows = false` instances are culled before packing and GPU upload,
  compacting visible instances without changing mesh submission order. Counters include these early rejects.
  Set culling `false` to force everything drawn. The shadow depth pass is never camera-culled (an off-screen
  caster still writes the shadow map, so its shadow lands on-screen). Read the win from `Scene3D.DrawnInstances` /
  `Scene3D.CulledInstances`. Mesh-local bounds (`MeshBounds`, computed once at `LoadMesh`) feed the pure plane math
  `FrustumPlanes.Extract(camera.ViewProjection)` + `IntersectsAabb`/`IntersectsSphere` (headless, allocation-free).
  Skinned draws (`DrawSkinned`) get the same treatment BEFORE they are recorded, so an off-screen character skips
  its skin or palette upload entirely, not just its draw call: a camera-culled draw is kept only if it casts and is
  ALSO inside the active shadow map's own ortho volume (so an off-camera caster still throws an on-screen shadow -
  the shadow pass is never camera-culled, matching the rigid contract). Rest-pose bounds are inflated by a safety
  factor (`Scene3D.SkinnedCullSafetyFactor`) to cover a pose swinging a limb outside the rest silhouette. Read the
  win from `Scene3D.DrawnSkinnedInstances` / `Scene3D.CulledSkinnedInstances`.
- GPU skinning (`Scene3D.UseGpuSkinning`, default ON): the vertex shader blends the bone palette instead of
  the CPU (`SkinningMath.SkinVertex`), so the rest-pose vertex buffer uploads once at load and only the per-draw
  palette + matrices upload each frame - the win at MMO crowd scale. Pixel-parity with the CPU path, same culling +
  shadow pass. Set it `false` for CPU skinning, which stays supported and is flippable per frame. Set 0 binding 0 is
  the shared frame block both stages read, binding 3 the per-draw `{ Model; P }`
  the vertex reads at its dynamic offset, material maps at set 1, and the per-CASTER bone palette at set 2. It was
  one combined buffer with the frame block copied into every draw's slot until issue #604 unfolded it, and it kept
  the palette per draw until issue #407 gave it a buffer of its own that the shadow pass binds too, so a caster's
  bones upload ONCE a frame instead of once per pass per cascade. All three packed buffers upload whole and once,
  avoiding D3D11's partial-uniform staging path. It shipped off while a windowed swapchain corruption on the Veldrid
  Metal backend was open, and that backend was deleted in 18.0.0. The Showcase 3D room's F key flips the path live
  for a windowed A/B. See `docs/USING-KHAOZENGINE.md`.
- Optional pass tracing: `Scene3D.PassObserver` accepts `IRenderPassObserver` from
  `KhaozEngine.Render3D.Diagnostics`. `RenderPassKey(Id, Occurrence)` identifies each logical interval.
  Begin/End callbacks enclose the existing commands, `SkipPass` records inactive or cached work, and
  `FusedMotion` tags motion already written by the geometry MRT draw without a second GPU interval.
  CPU phase callbacks cover grouping, visibility and upload packing, including work before the older timing
  buckets. Null is the production default and adds no allocation, marker, submission, barrier or target bind.
  The observer must preserve rendering state and must not submit or wait. CPU callbacks emit no GPU work.
  The first callback exception invalidates profiling and is exposed by `Scene3D.PassObserverFailure`.
  Recording continues and a successful frame then throws with that failure as its inner exception. An original
  rendering failure propagates unchanged. Attach observers before recording and keep them stable for the frame.
  Attach before `Begin` for scene-boundary work. The failure latch resets there and preserves preparation
  failures through rendering. CPU-only device leaves own scene begin, water geometry setup and bathymetry
  updates without GPU markers. The complete geometry, shadow, water, effect, temporal, post and display
  registry and occurrence rules are in `docs/USING-KHAOZENGINE.md`.
- Per-pass timing: `Scene3D.EnableTiming` (default `false`, no cost when off - a single `bool` check, no
  `Stopwatch` call, no allocation) brackets each render pass with a CPU `Stopwatch` and exposes the result as
  `Scene3D.PassTimingsMs` (a `Scene3DPassTimingsMs`: `ShadowDepthMs`/`ModelMs`/`TransparentsMs`/`PostMs`). This is
  CPU time spent RECORDING each pass's commands. The optional `KhaozEngine.Gpu` timestamp-pool seam
  separately measures GPU execution on supported devices, with completed-frame tails and retained completion
  fences. `Scene3D.EnableTiming` still reports CPU time. See `docs/USING-KHAOZENGINE.md` for the separate
  nonblocking GPU measurement contract. `Scene3D` has no
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
  the one knob for what fills a pixel no scene geometry drew. It is a derived view over `Starfield`,
  `Sky.Enabled` and `Atmosphere.Enabled`. Either sky flag resolves to `Sky`, ahead of `Starfield` and `Solid`,
  with the physical atmosphere taking precedence over the legacy sky. Selecting `Solid` or `Starfield` disables
  atmosphere. Selecting `Sky` enables the legacy sky without opting into atmosphere.
  Since 11.9.0 the starfield is a real background pass, `StarfieldRenderer`
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
- Legacy palette sky: `PixelPostProcessSettings.Sky` (a `SkySettings`, **default off**) draws an opt-in procedural
  sky behind all geometry, a vertical `HorizonColor`->`ZenithColor` gradient plus an optional sun disc and halo (`SunColor`,
  `SunRadius`, `HaloStrength`, `HaloFalloff`). Rendered as a far-plane background pass into the lit colour + read-only
  scene depth, so it fills only where no mesh drew, never touches the MRT normal/depth the outline pass reads, and
  costs nothing when off (`Sky.Enabled == false` and atmosphere off, existing scenes byte-stable).
  The sun direction **defaults to the key light** (`Post.LightDirection`) so sky and lighting agree
  (sun opposite the shadows). Override with
  `Sky.SunDirectionOverride`. `Sky.Horizon` (a `SkyHorizon`, default `Screen`) chooses where the horizon is: `World`
  anchors the gradient to the world horizon and paints `Sky.GroundColor` below it (blend depth
  `Sky.HorizonSoftness`), so a finite world runs on to the horizon and the sun disc sets through that line. It needs
  a perspective camera and `SunAnchor.World`, and the water's reflected sky follows it. `Sky.ExtraDiscs` (a list of
  `SkyDisc`, up to `SkySettings.MaxDiscs` = 8 with the primary) adds further bodies, each with its own direction,
  colour and shape, drawn in order and reflected by the water. `Sky.Anchor` (a `SunAnchor`, default `SunAnchor.World`) chooses how the disc is placed:
  `World` anchors it to the world-space sun direction via a true point-at-infinity projection through the camera (the
  disc stays fixed over the world direction the sun lies in as the camera orbits, and is hidden when the sun is behind
  the camera - correct for the perspective `FollowCamera3D`/`FlyCamera3D`. It degenerates under the orthographic
  `IsoCamera3D`, where a directional sun has no finite screen position, so pick `StylizedBackdrop` there).
  `StylizedBackdrop` keeps the legacy
  camera-relative placement (view-space right/up read as screen NDC, visible above the view horizon), which works
  under both cameras and is the pick for the iso look. The pure math is `SkyMath` (gradient + sun falloff +
  `ProjectSunToNdc`, which dispatches on the anchor to `ProjectSunWorldToNdc` / `ProjectSunStylizedToNdc`).
- `SunCycle` - a pure day/night mapping from a caller-supplied normalized time of day to the sky and lighting
  above. The palette and disc-slot descriptions here apply with atmosphere off.
  `SunCycle.Evaluate(timeOfDay, SunCycleSettings)` walks a latitude/declination/heading sun arc and
  returns a `SunCycleState` blended between `SunCycleSettings.DayPalette`/`DuskPalette`/`NightPalette`
  (three `SunCyclePalette` anchors) keyed on sun elevation, not time, so the same settings work at any
  latitude or day length. `SunCycle.Apply(state, scene.Post)` writes it onto `Post.LightDirection`/
  `LightColor`/`AmbientColor`/`FillLightColor` and `Post.Sky.HorizonColor`/`ZenithColor`/`SunColor`/
  `SunEnabled`/`GroundColor`, plus `Post.Sky.SunDirectionOverride`. `SunCycleSettings.DiscSetElevationDegrees`
  (default 0) keeps the disc alive that far below the horizon so it can set through a `SkyHorizon.World` sky.
  The night key follows `SunCycleSettings.NightKey`
  (`NightKeyMode`, default `AntiSolarMoon`): `AntiSolarMoon` is the historical virtual moon opposite the sun,
  dipping to zero across the crossing so the direction flip is invisible; `None` is a keyless night (black key,
  the sun's true direction held); `Moon` is a real decoupled moon body (own `MoonHourOffset`/`MoonDeclinationDegrees`/
  `MoonKeyColor`/`MoonDiscColor`/`MoonHorizonKeyDipDegrees`) that owns the key + single disc slot while it is up,
  with an independent disc color so a decorative moon can cast a black key. The moon keeps its own visibility and
  horizon dip, then takes the minimum of that dip and the solar-horizon handover envelope. Source changes therefore
  pass through black for unequal days and offsets without squaring coincident equatorial fades. A zero solar dip
  keeps the 0.001-degree numerical floor, making the transition effectively instantaneous outside the exact black
  crossing. `SunCycleState` exposes `MoonElevationDegrees`/`MoonDirection`/
  `ActiveSource` (`KeyLightSource`)/`DiscDirectionOverride`. The night ambient floor stays above black so scenes
  remain playable. The engine owns no clock: feed it your own game time (an MMO replicates it from the server)
  each frame.

  ```csharp
  var cycle = new SunCycleSettings();
  scene.Post.Sky.Enabled = true;
  // each frame, timeOfDay in [0,1) comes from YOUR game clock:
  SunCycle.Apply(SunCycle.Evaluate(timeOfDay, cycle), scene.Post);
  ```
- Directional indirect diffuse (21.13.0): the public field `PixelPostProcessSettings.IndirectDiffuse` defaults to
  `IndirectDiffuseMode.Uniform`, preserving flat `AmbientColor`, directional fill and existing pixels. Set it to
  `Directional` to evaluate the public `AmbientIrradiance` field at each surface normal in the six lit model and
  terrain fragments. The field defaults to `IrradianceSH.Zero` (`default`). `IrradianceSH` holds nine pre-convolved
  RGB coefficients in a Y-up real L2 basis. `Evaluate(Vector3 unitNormal)` returns irradiance in ambient units,
  clamped to zero per channel, without another `1/pi`. `Uniform(Vector3 irradiance)` builds an isotropic value,
  and `SkyAndGround(Vector3 skyIrradiance, Vector3 groundIrradiance)` takes upward and downward hemisphere
  irradiance. Its span constructor requires nine coefficients, `Coefficient(index)` reads indices 0 to 8,
  `Scale(Vector3 rgb)` scales channels and `+` adds coefficients. Without the atmosphere, `SunCycle.Apply`
  clears the field to `Zero` with its palette ambient and fill writes. Set custom `AmbientIrradiance` after
  `SunCycle.Apply`. An unset or `Zero` value falls back to `Uniform` of `AmbientColor` RGB. Fill remains independent,
  so zero `FillLightColor` RGB if your ground hemisphere already represents it. Water, particles, decals,
  billboards, outlines and silhouettes are unchanged.
- Physical atmosphere: `PixelPostProcessSettings.Atmosphere` (`AtmosphereSettings`, **opt-in, default off**)
  replaces the palette sky with Hillaire 2020 scattering, derives key and indirect lighting on the CPU,
  reflects the same sky in water and applies analytic height fog to terrain, models and effects.
  `AtmosphereSettings.Earth()` returns the default preset, also disabled. Use the camera-aware returning overload:

  ```csharp
  var cycle = new SunCycleSettings { NightKey = NightKeyMode.Moon };
  scene.Post.Atmosphere.Enabled = true;
  SunCycleState state = SunCycle.Apply(
      SunCycle.Evaluate(timeOfDay, cycle), scene.Post, scene.ActiveCamera.Eye);
  scene.Post.Water.HorizonColor = state.HorizonColor;
  ```

  `SunCycle.Apply(in SunCycleState, PixelPostProcessSettings, Vector3 cameraWorldPosition) -> SunCycleState`
  uses absolute camera Y for lighting and returns the physical horizon colour. The void overload remains and
  evaluates at world height zero. Both require an evaluated state in physical mode, throwing
  `InvalidOperationException` for a manually constructed one. Palette colours, `MoonKeyColor` and `MoonDiscColor`
  are ignored. The primary disc remains the true sun and the moon uses an extra disc. `Moon` keeps its independent
  track, `AntiSolarMoon` supplies an opposite-sun source, and `None` disables moon illumination.

  Earth defaults are planet radius 6360 km, atmosphere height 100 km, Rayleigh scattering
  `(0.005802, 0.013558, 0.0331)` per km with an 8 km scale height, Mie scattering/extinction
  `0.003996`/`0.004440` per km with a 1.2 km scale height and anisotropy 0.8, ozone absorption
  `(0.000650, 0.001881, 0.000085)` per km in a tent centred at 25 km with width 30 km, and ground albedo 0.3.
  Calibrated `SunIlluminance = 2.7` keeps noon blue, `MoonIlluminanceFraction = 0.04` keeps the moon visible,
  `NightAmbientFloor = 0.05` keeps receivers readable, `NightSkyFloor = 0.09` keeps moonless sky above black,
  and `NightTint = (0.45, 0.65, 1)` keeps night dark blue at the existing fixed exposure. Eye adaptation is
  [#1365](https://github.com/APKiwiOrg/KhaozEngine/issues/1365).

  Day and dusk tints default to white. The blended tint applies once to sky, fog and lighting. The floors always
  carry night hue, including at twilight, with per-channel `max(S * pi * L * Tint, NightSkyFloor * NightTint)`
  for sky and `max(S * E * AmbientScale * Tint, NightAmbientFloor * NightTint)` for ambient. `L` and `E` include
  sun and moon, with `E` the upward sky irradiance. Ambient and fill scales default to 1 and affect lighting only.
  In `Uniform`, ambient and directional fill retain their existing behavior. In `Directional`, `SunCycle.Apply`
  and scene recording write `AmbientIrradiance` and zero `FillLightColor` RGB because ground bounce is already
  in the SH. `AmbientColor` and the returned `SunCycleState` retain their original values. The sky follows both
  source azimuths with `AmbientScale`, the ground uses `FillScale` and both receive the frame tint.
  `Evaluate(up)` equals `AtmosphereLighting.Ambient`, including its floor, and `Evaluate(down)` equals
  `max(Fill, NightAmbientFloor * NightTint)` at every elevation within floating-point precision. Each pole's
  floor shortfall is added as its own hemisphere without raising the other pole. Switching back to `Uniform`
  restores fill and clears the SH. Directional indirect light does not yet include interior occlusion.
  `WorldUnitsPerKilometre = 1000` maps absolute Y = 0 to the planet surface. Lighting interpolates physical
  altitude across eight cached quadratic slices. `AerialDistanceScale = 20` compresses fog distance and endpoint
  heights together, without changing sky or lighting altitude. Zero disables fog. `AerialOnsetDistance = 0`
  keeps the linear distance, and a positive onset `s` uses `d * d / (d + s)` so near geometry stays clear while
  distant fog loses at most `s`. `HazeDensityPerKm = 0` defaults to no extra mist, with
  `HazeScaleHeightKm = 0.25`. Downward fog rescales the horizon sky sample to the view's true scattering angle,
  so a low sun's aureole stays on horizontal rays. Physical sky follows world view rays regardless of
  `SkyHorizon`. Static half-float LUTs are transmittance 256 by 64 and multi-scattering 32 by 32. Sky-view is
  192 by 108, rebuilt each enabled frame with separate horizon endpoints for sky and ground.

  `AtmosphereModel` is GPU-free. `CreateFrame` consumes a `SunCycle.Evaluate` state and returns `AtmosphereFrame`,
  with body directions, moon fraction, tint and key visibility. `EvaluateLighting(heightKm, frame)` returns
  `AtmosphereLighting` with `Key`, `Ambient`, `Fill`, `Horizon` and untinted source transmittances.
  `EvaluateIrradiance(float cameraHeightKm, in AtmosphereFrame frame)` returns directional `IrradianceSH`.
  Its separate table builds lazily in about 250 ms in the measured Earth fixture and rebuilds on physical
  settings changes. Warm frame evaluation allocates nothing. `SkyValue`
  supplies controlled sky values, while `SkyRadiance` exposes unit-source radiance. The model snapshots settings,
  so call `UpdateSettings` after edits. `AtmosphereFog` is the CPU height-fog oracle, exposing transmission and
  composition with alpha-aware and additive handling. CPU fog samples sky-view texels directly at the same UV
  as GPU fog, with bilinear filtering and periodic azimuth. Sky texel membership prevents a rounded tangent
  from entering the ground branch. Fog samples only the sky hemisphere. Its final sky texel
  eases into the geometric-horizon colour with zero slope, without changing the sky or reflection LUT
  mapping. Below the horizon the phase ratio eases in from one with zero slope as well. Additive effects
  attenuate without adding fog glow.

  Phase 1 fog can be too bright in deep valleys under a low sun. The aerial perspective volume remains
  [#1364](https://github.com/APKiwiOrg/KhaozEngine/issues/1364). Diffuse sky light reflected off the transport's
  ground boundary and its feedback into multi-scattering are omitted. Scene receivers get sky ambient and
  directional fill in `Uniform`, or directional sky irradiance and a ground hemisphere in `Directional`.
  Clouds, cloud shadows and stars under atmosphere remain phases 2 to 4 (#1367, #1368, #1369).
  The one-shot completed-frame timing was inconclusive, with an on-minus-off delta of -3.752200 ms.
  It does not establish the 0.3 ms target. See the [consumer guide](../docs/USING-KHAOZENGINE.md) for controls
  and [phase 1 evidence](../docs/design/PHYSICAL-ATMOSPHERE-PHASE-1-EVIDENCE.md) for calibration, timing and
  pending hosted validation.
- `EnvironmentPresets` + `EnvironmentPresetKind` (17.6.0) - ready-made legacy sky and lighting bundles for a
  look dropdown. `EnvironmentPresets.Apply(kind, scene.Post)` writes one coherent group of fields for
  `Day`/`Sunset`/`Night`/`Starfield`: the sky palette (`Sky.HorizonColor`/`ZenithColor`/`SunColor`/`SunEnabled`),
  the background mode (`Sky.Enabled` + `Starfield` + `BackgroundColor`), and the five lighting fields
  (`LightDirection`, `LightColor`, `AmbientColor`, `FillLightDirection`, `FillLightColor`). `Starfield` pulls the
  sky palette DOWN to its near-black background on purpose, because water reflects `Sky.HorizonColor`/`ZenithColor`
  whether or not the sky pass is on (see Water below), so leaving the palette at its bright defaults paints a
  jagged bright horizon into water against a black background. Distinct from `SunCycle`: these are fixed snapshots
  for a menu, `SunCycle` is a continuous arc off a caller-owned clock, and the two are not meant to be mixed on the
  same scene. `EnvironmentPresets.SunLightDirection(azimuthDegrees, elevationDegrees)` is the sun-angle helper a
  slider pair drives: it returns the key light's normalized TRAVEL direction (so an elevation above the horizon
  gives a downward-pointing vector) in the same Y-up, north = `-Z`, east = `+X` convention
  `SunCycle.SolarDirection` uses, and matches `SkySettings.ResolveSunDirection`'s sign convention exactly, so the
  sun disc, the key light and the water glint all move together off one pair of angles. `Apply` throws
  `ArgumentNullException` on a null `post` and `ArgumentOutOfRangeException` on an undefined kind.
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
  legible against the filmic roll-off while the hottest cores still read as hot. At `1`, a luminance-mapped vector
  past the display ceiling is uniformly scaled down until its largest channel is 1. This preserves exposed linear
  RGB ratios while allowing some luminance reduction near the gamut boundary. At `0` the tonemap short-circuits to the exact per-channel expression, byte-identical to the pre-chroma
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
    rather than uploaded per component. The swell's NORMAL is evaluated per pixel at the fragment's still-water
    position rather than interpolated from the grid, so a coarse grid (a clipmap's outer rings, the far cells of a
    large camera-focused plane) cannot shade it as flat triangle facets (#381). The whitecap fold is evaluated the
    same way, so those grids no longer draw whitecaps as triangles (#1100). The grid is a fixed 97x97 vertex budget
    concentrated toward the camera by `GridFocusBias`
    (1 = uniform), since a consumer plane can be 1200 units across.
  - **Analytic sky reflection** (`SkyReflectionStrength`/`SkyReflectionSunStrength`): the fresnel term blends
    toward the sky evaluated along the reflected view ray. With atmosphere off, `Internal.SkyMath.ShadeDirection`
    uses `PixelPostProcessSettings.Sky`'s gradient and sun even when the legacy sky pass is disabled. With atmosphere
    enabled, water samples the same physical sky-view LUT as the background, mirrored by `AtmosphereModel` on the
    CPU. Feed the returning camera-aware `SunCycle.Apply` horizon to `Post.Water.HorizonColor`.
    `SkyReflectionStrength = 0` restores that flat horizon colour.
  - **GGX sun glint** (`GlintStrength`/`GlintRoughness`/`GlintDistantRoughness`): a peak-normalized GGX lobe held
    at or above a roughness floor that widens toward `GlintDistantRoughness` wherever the surface is under-sampled,
    by camera distance over `DetailFadeDistance` OR by the pixel's world footprint against the ripple wavelength,
    whichever is worse. The floor sits under the variance transfer below rather than adding to it, so the ripple
    detail both respond to is counted once (#308). `GlintRoughness = 0` selects the legacy Blinn-Phong lobe on
    `GlintExponent`.
  - **Depth grading** (`AbsorptionPerMetre` over `DeepColor`/`ShallowColor`): per-channel Beer-Lambert
    transmittance, so the ramp bends through green-teal rather than running straight between two colours. All-zero
    coefficients restore the two-stop smoothstep over `ShallowDepth`. The same resolved coefficients drive underwater
    sunlight, where all-zero absorption means no attenuation.
  - **Foam** (`FoamColor`/`FoamStrength`/`FoamCrestCoverage`/`FoamShoreWidth`/`FoamPatternScale`): procedural, no
    texture assets. Whitecaps from the determinant of the swell's horizontal Jacobian (steepness-normalized, so
    coverage means the same at any steepness) and a shoreline band from the plane's depth source, both broken up by
    a scrolling pattern thresholded into graphic lobes. The fold eases toward 73% of itself as the pixel footprint
    on the still-water plane grows, which keeps distant whitecap coverage where the old per-vertex fold left it
    rather than whitening the horizon to the near field's density.

  Underwater sunlight is separately opt-in per water body through `UnderwaterSunlight` (scene default `false`) and
  `UnderwaterPenumbraPerMetre` (scene default `0.03` metres of extra penumbra radius per metre of in-water path).
  The value must be finite and non-negative. It is available on any plane, procedural or FFT, bound or unbound.
  A frame with no opted-in water renders exactly as before. The water refractive index is the fixed constant `1.333`,
  not a setting. How submerged receivers are lit is described under key shadows.

  On top of that the ripple normal field is a generated SLOPE SPECTRUM (14.26.0): `RippleComponents` cosines with
  golden-angle headings (no two parallel, no dominant ribbon direction) laddering by `RippleLacunarity` over about
  five octaves, amplitudes renormalized to a fixed slope variance so `NormalStrength` keeps its meaning, sampled at
  a domain-warped position (`WaveWarpStrength`). Each component is band-limited out of the normal once it falls
  below `FootprintSamples` pixel footprints, and the removed slope variance is transferred into the GGX lobe
  (`VarianceToRoughness`, Toksvig-style, widening `GlintRoughness` up to or past the glint's floor) so distant water
  becomes a rougher surface rather than stripes or glass, and
  the swell's shading contrast fades on the same measure. `DetailFadeDistance`/`DistantDetailScale` remain as an
  artistic extra layered on top. Plus the
  depth-driven shore fade (`ShoreFadeDistance`) and `Opacity`.
  **No view-ray refraction, no screen-space reflections, no caustics, no submerged view** (all out of scope) - a stylized
  LDR surface. Drawn after the sky + ground decals, before the MRT resolve, one draw per plane, with depth test ON
  (`Less`, so geometry above the surface occludes it) but depth WRITE OFF (so the resolved normal/linear-depth the
  outline pass reads is untouched - matching the ground-decal/beam/textured-billboard depth-interleave convention).
  Time is driven by the same `Scene3D.EffectTimeSeconds` clock the beam pulse/scroll uses (freeze it for a
  deterministic frame). On an UNBOUND plane the depth grading, the shore foam and the shore fade reconstruct the
  first opaque surface under each water pixel from the resolved scene depth via the same `gl_FragCoord` +
  raw-inverse-view-projection convention the ground-decal pass uses. That surface can be a creature or a prop as
  well as the bed. A plane bound to an optical field reads its own bed depth instead, described in the next item.
  Either measurement is taken under the DISPLACED surface, so a passing crest carries the waterline and the foam
  line up the beach for free. The pure math is `WaterMath` (the ripple
  normal, domain warp, distance detail fade, grid layout and focus warp, absorption, reflection blend, GGX and
  legacy glint, roughness widening, foam), `RippleSpectrum` (the ripple spectrum, the footprint band-limit and the
  variance transfer) and `GerstnerWaves` (the swell, whose offset the vertex stage mirrors and whose normal and
  fold the fragment stage mirrors), all internal, headless-tested and mirroring the GLSL `WaterVert`/`WaterFrag`
  exactly.
- Water volume (`WaterPlane.FloorY`, default `null` = unbounded below): set the optional trailing constructor
  argument to the lowest world Y of the body's volume under the plane rectangle. Receivers below that floor are
  outside the body. This is a conservative slab rather than exact local containment, so a dry space inside the slab
  under a higher part of the bed can still count as submerged. Only its key path changes, and the bed above it already
  shadows that path, so it stays dark. `DrawWater` refuses a non-finite floor or one above `SurfaceY`. Render-origin
  rebasing carries the floor with the plane, and stacked bodies resolve to the nearest surface above a receiver.
- Per-body optical beds (`WaterPlane.Optics`, a `WaterOpticsBinding`, default unbound, or
  `WaterPlane.WithOptics`). A bound plane drives depth grading, ordinary shore foam and `ShoreFadeDistance` from
  its own signed bed depth, `max(0, restDepth + displacedY - referenceY)`, rather than from the first opaque
  surface. Scene depth still depth-tests the plane, so geometry above the surface occludes it and a submerged
  object is never read as the bed. FFT crest foam, shoaling and surf keep their existing inputs, and
  `Post.Water.Bathymetry` is not reinterpreted. Unbound planes keep the legacy scene-depth source, with no optical
  preparation or upload. Two immutable resource kinds share one generation-checked `WaterOpticsHandle`:
  - **Bilinear direct field**: `WaterOpticsField(width, height, restDepths)`, a row-major lattice of finite
    signed depths with 2 through `MaxDimension` (1024) samples per axis, loaded with
    `Scene3D.LoadWaterOpticsField`. The shader reads it through four bounded R32Float texel fetches, so it does
    not depend on float linear filtering, and the final row and column carry weight one at the outer edge. The
    lattice spans the whole binding. Interpolation softens a vertical step across one lattice cell, so a
    producer with steep drops must sample densely enough that one cell of softening is acceptable.
  - **Triangle field**: `WaterOpticsTriangleField(size, binColumns, binRows, triangles, certifiedRectangles)` over
    `WaterOpticsTriangle` vertices in local metres, ordered X, signed rest depth, Z, loaded with
    `Scene3D.LoadWaterOpticsTriangles`. Pass the actual rendered triangles, for example `GltfMesh.Indices32` over
    its `Vertices` positions, without clipping or welding them. Each world vertex `(x, y, z)` becomes `(x -
    origin.X, reference - y, z - origin.Z)`, where `origin` is the binding origin and `reference` its Y, so the
    middle component is the signed rest depth. The constructor proves that the projected triangles cover every
    `WaterOpticsRectangle` certificate with no hole and no disagreeing overlap, then packs one R32Float atlas. Bin
    axes range from 1 through 1024, atlas texture axes from 2 through `MaxDimension` (1024), and a bin holds at most
    `MaxTrianglesPerBin` (32) candidates. The shader selects the containing triangle with exact integer predicates
    and interpolates its three depths. Refusals throw `WaterOpticsSurfaceException`, an `ArgumentException` whose
    `Reason` is `IncompleteCoverage`, `AmbiguousSurface`, `CapacityExceeded` or `UnrepresentableSurface`. The
    appended `UncertifiedDisplacement` reason is reported by the padding preflight below, not by the constructor.
    Map the typed reason rather than the message. `TrySampleDepth` returns false outside every certificate and
    `SampleDepth` throws there. No sample value means missing data. Upload also refuses nonzero subnormal atlas
    scalars and non-normal inverse extents as `UnrepresentableSurface` before allocating a texture.
    `ReconstructionErrorBoundMetres` is the field's upward-rounded reconstruction bound, which a producer counts
    with its own source and render-origin conversion error inside its single budget. It also includes the measured
    depth discontinuity of coincident shared edges and T junctions. Positive-area disagreement still refuses as
    `AmbiguousSurface`.
  - **Binding and coverage**: `WaterOpticsBinding(handle, origin, size)` uses the plane's absolute frame. Origin XZ
    is the field minimum. Origin Y is the reference the rest depths are measured from, the still-water surface or
    any datum the producer subtracts exactly. TileWorld binds datum 0 and stores `-y`. A triangle binding requires
    the field's exact `Size`. `DrawWater` refuses a bound plane whose rectangle, widened by the effective procedural
    swell displacement, leaves the field or, for triangles, does not fit one certificate. Triangle coverage must extend
    beyond each certificate by a rasterization margin. An edge fragment that rounds a few binary32 steps outside an
    exact-fit certificate finds no triangle and is discarded by the invariant guard. TileWorld fields meet this because
    their triangles extend at least one tile beyond every certificate. The enforced margin is tracked in
    https://github.com/APKiwiOrg/KhaozEngine/issues/1581.
    `Scene3D.GetWaterOpticsPadding` reports that displacement for producer preflight and throws
    `NotSupportedException` for FFT water, because FFT has no certified displacement envelope.
    `Scene3D.TryGetWaterOpticsPadding(look, out padding, out reason)` is the non-throwing form. It returns false
    with zero padding and `UncertifiedDisplacement` for FFT water and for a swell whose inputs or reach are not
    finite, where `GetWaterOpticsPadding` throws `ArgumentException`, and `reason` is meaningful only when it
    returns false. Bound FFT water throws `NotSupportedException` at `DrawWater`. A stale, foreign or released
    handle throws. No bound fragment falls back to scene depth, zero or deep water. With an active render origin the
    scene subtracts the frame origin from the binding origin as it does from the plane centre, so texture mapping
    never carries large absolute world coordinates and rebasing changes the mapping, not the samples.
  - **Preparation**: queue every plane, then call `Scene3D.PrepareFrame(width, height)` with the viewport size
    before opening the frame's command list. `GameApp3D` forwards its frame size automatically. The no-argument
    `PrepareFrame()` remains the legacy form and throws when bound water is queued. A changed viewport, target or
    water input after preparation throws at recording rather than drawing stale resources. Only a frame with bound
    water freezes those inputs. Preparation revalidates coverage from the absolute queue, the rectangle
    `DrawWater` admitted, so an explicit off-grid `RenderOrigin` cannot refuse an admitted plane.
  - **Lifetime**: reuse the handle across frames. `UnloadWaterOpticsField` invalidates either kind at once and
    defers texture destruction through the scene's retirement queue. On a disposed scene, unloading or binding a
    handle throws `ObjectDisposedException`. `WaterOpticsStats` counts live fields,
    padded live and retired texture bytes, uploads, and live and retired retained CPU payload bytes until actual
    retirement. Device recreation means a new scene: handles from another or an older scene are invalid, so the
    producer explicitly loads its retained CPU fields into the new scene.
  See [consumer use](../docs/USING-KHAOZENGINE.md) for a worked binding and
  [TileWorld](../KhaozEngine.TileWorld.Render3D/README.md) for the triangle producer.
- Underwater volume uploads (`scene.WaterVolumeClusters`). Queued planes whose resolved
  `WaterSettings.UnderwaterSunlight` is true contribute render-relative records, including their optional
  `WaterPlane.FloorY`, resolved absorption and penumbra. The scene default is false and `WaterLook` can override
  it per plane. Diagnostics expose `RecordCount`, `DroppedRecords`, `ReferenceCount` and `OverflowedClusters`.
  Water-only frames prepare the same camera grid as point lights. A failed light assignment keeps that grid
  valid for water, with full-list fallback headers for the lights. Empty and opted-out frames set the count
  in `ClusterCamera.w` to zero and skip water uploads, so retained buffer contents cannot be read next frame.
  Every lit receiver reads that scene-owned buffer in shared key shading. Set 0 appends binding 15 for rigid,
  foliage and CPU-skinned receivers, 7 for GPU-skinned, and 6 for splat and tile ground. Lit fragments require
  three storage buffers, checked at renderer creation against `GpuCapabilities.MaxFragmentStorageBuffers`.
- Per-plane water look (`WaterPlane.Look`, a `WaterLook`, since 17.7.0, **default `null` = the scene's look, byte-
  identical**): a trailing optional constructor parameter on `WaterPlane`, so every call site written before this
  existed still compiles and still packs from the caller's own `WaterSettings` object unchanged. Every field on
  `WaterLook` mirrors a `WaterSettings` field and is itself nullable - `null` inherits the scene, a value overrides
  it for that plane alone, leaving every other queued plane on the scene's look, which is what lets a calm inland
  lake and a rough FFT sea share a frame. The overridable fields are `WaveSource`, the swell group, the ripple/
  detail group, body colour including `AbsorptionPerMetre`, `UnderwaterSunlight` and
  `UnderwaterPenumbraPerMetre`, foam, the four `Glint*` scalars, `ShoreFadeDistance` and the two depth-response
  strengths `ShoalingStrength`/`SurfStrength`. Scene-wide and NOT on a look at all: `SeaState` (one FFT bake - two sea states a frame does not
  make two oceans, it makes the one producer rebake on every call for both and corrupts the persistent foam
  accumulator), `Bathymetry` (one depth texture), the grid group (`GridMode` and the `Clipmap*` knobs, which pick
  the pass's pipeline and vertex layout before the draw loop starts), reflection weights (read the one sky),
  the `Surf*` shape knobs, and the sample-count knobs. `WaveSource = WaterWaveSource.Procedural` on a look is the
  inland-body case: the plane leaves the shared ocean, so it loses the sea's swell, whitecaps and (shoaling/surf
  ride the same gate) its breaking surf. The shared ocean itself now runs on DEMAND - it bakes when any queued
  plane's effective wave source is `FftOcean`, rather than off the scene's own default, so a `Procedural` scene
  with one ocean plane still gets a real ocean. Costs zero new UBO bytes (payload 1088, slot 1280), zero new GPU
  per-plane GPU resources and zero new pipelines: each plane already owned its slot in the water pass's uniform
  buffer, so an override is a different set of numbers written into a slot that was being written anyway. Every
  procedural plane whose effective swell does not displace (an amplitude or a wavelength that is zero or negative)
  draws one quad of a shared, grow-only buffer of 48 bytes per plane over one six-index buffer, in either grid
  mode, and the frame uploads every such quad in one write before the water pass opens. Per-body sea states,
  bathymetry and grid modes stay deferred to [#275](https://github.com/APKiwiOrg/KhaozEngine/issues/275).
  Rationale, including why a per-plane sea state is refused rather than deferred:
  `docs/design/WATER-PER-PLANE-LOOK-DESIGN-2026-07-27.md`.
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
- Water surface grid (`WaterSettings.GridMode`, a `WaterGridMode`): `CameraFocused` (the default) is the fixed
  97x97 budget warped toward the camera by `GridFocusBias`. Each displaced plane gets its own slice of one shared,
  grow-only vertex buffer, and every slice uploads before the water pass opens. `Clipmap` is a WORLD-LOCKED alternative -
  concentric square rings, each at twice the previous ring's cell size, every vertex snapped to its own ring's
  lattice in world space, so the mesh does not slide through the wave field as the camera moves. That slide is a
  measured artifact rather than a theoretical one: on the FFT ocean a 0.10 m camera step at frozen wave time
  changed the rendered height field by about 85 per cent of the sea's own per-frame motion, and the clipmap cuts
  it 3.3x at a walk and 15.6x at a sprint. Ring boundaries are closed by stitch vertices (the fine side's
  off-lattice boundary vertices evaluate the surface at both coarse neighbours and average onto the coarse edge
  segment), never by skirts - the pass is alpha-blended - and never by collapsed quads. Sized by
  `ClipmapCellSize`, `ClipmapRingCells` and `ClipmapLevels` (0 = derive the ring count from the plane), and it
  band-limits each ring to its own Nyquist against the mipped cascade maps (`ClipmapBandLimitSamples`).
  `GridFocusBias` is inert under it. At the defaults it draws FEWER triangles than the grid it replaces and only
  rebuilds its buffers when a ring snaps (per plane: each plane owns a slice of the buffers, so one plane's rebuild
  never invalidates another's). A plane whose effective look is procedural with a `SwellAmplitude` or
  `SwellWavelength` of zero or less has no vertex displacement, so it skips the lattice, as it skips the
  camera-focused grid, and draws one six-index quad through the regular water pipeline. FFT planes and procedural
  planes with any swell keep their grid. Before any geometry is built, a procedural plane whose rectangle, grown by
  how far its swell can move the surface, lies wholly outside the view frustum is skipped. FFT planes are never
  culled. The clipmap's snap lattice is decided in ABSOLUTE world space and only then reduced by the
  camera-relative `RenderOrigin`, so an origin rebase moves no ring. Since 17.3.0 `ClipmapGeomorphBand` (0.5, `0`
  restores the 16.12.0 grid exactly) fades each ring's outer band toward the next ring out's evaluation - sampled
  displacement and band-limit spacing both - instead of swapping level at the boundary; it subsumes the stitch
  (which is its `Morph = 1` case) and its weights are static per grid build, so a frame where no ring snapped
  still does no work. Measured on the #296 acceptance metric it takes the ring-boundary residual from 0.00086 m
  RMS to 0.00047 m at both a 0.10 m and a 0.50 m camera step. The layout math is `Internal.WaterClipmap` (pure,
  headless-tested); rationale: `docs/design/WATER-CLIPMAP-DESIGN-2026-07-27.md` and
  `docs/design/WATER-SHORE-DESIGN-2026-07-27.md`.
- Bathymetry, shoaling and breaking surf (since 17.3.0, `WaterSettings.Bathymetry`, **null by default = the
  16.12.0 surface, byte for byte**; needs `WaterWaveSource.FftOcean`): a `WaterBathymetry` is water depth in
  metres over a world-space XZ rectangle, supplied as a plain `float[]` the consumer writes (`FillFromGround` fills
  it from any ground-height function) and re-uploaded only when the field is replaced or its `Revision` moves.
  Assigning a different field is a plain assignment and always re-uploads, whatever its `Revision` reads: before
  17.37.0 a same-resolution replacement was silently ignored and the old depths stayed bound (#645). With one
  bound, each cascade
  is scaled by `tanh(k d)` against its own energy-weighted mean wave number, so the long swell calms in metres of
  depth while the chop rides in untouched (`ShoalingStrength`, `ShoalingDepthScale`); and where the depth falls
  below `Hs / SurfBreakerIndex` the surface foams, gated on the incoming wave's CREST PHASE so the white surges up
  the beach with each wave rather than glowing in place (`SurfStrength`, `SurfBandWidth`, `SurfCrestBias`,
  `SurfTrailWidth`, `SurfAmplitudeCollapse`). `SurfStrength` controls that breaking band independently from
  `FoamStrength`, which controls whitecaps and the ordinary shoreline band, so overdriven whitecaps cannot flatten
  surf into a white slab. The surge direction is the DEPTH GRADIENT, not the wind heading, so
  anything shallow - a rock, a bar - breaks around itself with no authoring. Outside the rectangle the surface
  reads as deep open water, so a coastal strip can be baked at a useful resolution instead of an ocean at a
  useless one. Pure math is `Internal.WaterShoaling` (headless-tested, mirrors the GLSL). Rationale:
  `docs/design/WATER-SHORE-DESIGN-2026-07-27.md`. `WaterSeaState.DepthMetres` is a separate bake-time reference
  depth for the whole FFT spectrum. Choose it from the broad water body, while bathymetry describes local shelves
  and shore. Different values are often intentional.
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
- `OceanPresets` + `OceanPresetKind` (17.6.0) - the `EnvironmentPresets` idea applied to the sea.
  `OceanPresets.Apply(kind, post.Water)` writes one coherent group of `Calm`/`Moderate`/`Rough` values: the
  Gerstner swell (`SwellAmplitude`, `SwellWavelength`, `SwellSteepness`, `SwellSpeed`), `NormalStrength`, the
  whitecap foam (`FoamStrength`, `FoamCrestCoverage`) and the sun glint (`GlintStrength`, `GlintRoughness`).
  `OceanPresets.ApplyToLook(kind, look)` applies that whole bundle to a `WaterLook`, including glint. It keeps
  `WaveSource` unchanged, so the caller explicitly chooses whether a lake leaves the shared FFT ocean.
  It deliberately leaves `GridMode`, the clipmap fields, `Bathymetry` and the surf fields ALONE: those describe
  the water body's geometry and shoreline rather than its weather, so a preset pick never undoes a consumer's
  clipmap or bathymetry wiring. `Moderate` is closest to `WaterSettings`' own defaults. Throws
  `ArgumentNullException` on a null `water` and `ArgumentOutOfRangeException` on an undefined kind.
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
  `TextureHandle`, its `Columns` x `Rows` grid, an optional motion-vector sheet + `MotionStrength`, `Loop`, and the
  `FlipU`/`FlipV` UV mirrors) and a continuous `FlipbookFrame` (integer part = cell, fractional part = blend to the
  next, motion-vector warped when a motion sheet is bound, else a plain cross-fade). A flipbook cell samples with
  its origin at the BOTTOM-LEFT (the same convention as `BillboardGeometry.Triangles`, whose `(u0,v0)` is the
  bottom-left corner), while the 2D `SpriteBatch` path samples the same file top-left, so a sheet packed by a
  top-left tool (PIL, most packers) renders with every cell VERTICALLY INVERTED until you set `FlipV: true`. The
  flips apply within a cell only, so the cell the frame index selects never moves. `Columns`/`Rows` cap at 127,
  since the grid, the quantized motion strength, and the two flip bits share one 24-bit packed float (8192px at a
  64px cell is already past most GPUs' max texture size). Flipbooks are additive over the procedural shapes,
  selected per-sprite, and a sprite that leaves `Flipbook` default renders byte-identically to the procedural path
  (a 1x1 dummy atlas + neutral motion sheet keep procedural runs in the same one pipeline). Atlas and motion taps
  go through an explicit sampling LOD capped at the coarsest mip level where a cell still holds 4 texels on its
  shorter side, so a distant sprite stops averaging its cell together with the ones around it. The cap is derived
  in the shader from the packed grid and `textureSize`, so a consumer adopts it by version bump.
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
- Glowing beams: `Scene3D.DrawBeam(a, b, width, color, style)` queues a camera-facing additive strip between
  two world points, with a bright core and soft halo. `BeamStyle.Default with { ... }` adjusts core/glow colours,
  end taper, pulse and scrolling, driven by `Scene3D.EffectTimeSeconds`. Width is the full world-space width.
  Beams are depth-tested without writing depth, so nearer geometry occludes them. Submit each frame after
  `Scene3D.Begin`, which clears the queue. Coincident endpoints or non-positive width draw nothing.
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
- `Scene3D.InteriorCut` accepts an immutable `InteriorHeightField`, uploaded once at the next render.
  Null binds the zero default and disables the field. `RigidInstanceDraw.CutInsideInteriors` defaults false.
  Only opted-in static rigid draws whose cull bounds can reach an interior take the cut path. Colour, shadows
  and outlines share a cached zero-margin query, invalidated by field, world transform or render origin changes.
  Stable submission slots reuse the flag without transform-cache lookups. Reaching instances are packed
  contiguously within each mesh to preserve instancing. Silhouettes cache a separate answer with their
  world-metre hull width added to the bounds and included in the key. Missed models keep the
  original rendering and depth-only caster route with no cut texture read.
  Reaching draws discard only inside the field, in colour and temporal motion together and in
  both cascaded and point-light shadows. A field version change invalidates the shadow caches. Ordinary
  unflagged draws, roofs, skinned meshes, animated foliage, grass and ground cover keep their existing path.
  `InstanceData` stays 128 bytes, with `SpecParams.xyz` unchanged and `w` the 0/1 cut flag.
  The shared frame UBO is 1536 bytes, with interior vec4 tails at 1344 and 1360 and the appended 160-byte
  indirect tail at 1376. Model set 0 binding 14 follows atmosphere bindings 11 to 13 and reuses sampler 6,
  with six dynamic uniform descriptors unchanged.
  Cascade slots remain 256 bytes with tails at 96/112, point slots at 144/160. Rigid outline and silhouette
  payloads are 192 bytes in 256-byte slots, tails at 160/176, flags in `Params.w` and `Params.y` respectively.
  `InteriorCutBinding` shares the texture/sampler set per renderer and rebuilds it only on texture identity changes.
- `Scene3D.DrawMeshSilhouette(MeshHandle mesh, Matrix4x4 world, Color color, float widthMetres)` - the
  per-entity highlight rim (the RuneLite look): re-draws the mesh as an inverted hull, vertices pushed along
  angle-weighted geometric normals shared by exact-coincident positions. Hard shading, UV seams, and material
  parts therefore stay closed while the ordinary model keeps its authored normals and flat lighting. Each rigid
  mesh owns a compact 12-byte-per-vertex outline-normal stream. `LoadProp` and `LoadPropMeshes` compute that stream
  across the complete part list before uploading its material parts. Front faces are culled, the flat
  alpha-blending color is depth tested without writing, and nearer scene geometry still occludes the rim. Draw
  the model as usual and queue its silhouette the same frame. The normal push assumes a UNIFORM world scale
  (bake a nonuniform scale into the mesh). The
  whole-scene edge post keeps the Outline name on `PixelPostProcessSettings`.
- `Scene3D.BeginMeshOutline(Color color, float widthPixels)` plus
  `DrawMeshOutline(MeshOutlineGroup group, MeshHandle mesh, Matrix4x4 world)` queue one screen-space target
  border across any number of independently transformed parts. The group is valid for its scene and current
  frame only. Its full alpha-cutout-aware projected union rejects interior strokes, while opaque scene depth
  suppresses occluder cuts and border leaks onto nearer geometry. Width is 0.5 to 8 physical output pixels and
  stays fixed across model size, camera distance, MSAA and render scale. The one-part `DrawMeshOutline(mesh,
  world, color, widthPixels)` overload creates its own group. Mask targets allocate lazily. The older
  `DrawMeshSilhouette` metre-width hull remains available for world-space expansion effects.
  `DrawMeshOutlineDissolved(group, mesh, world, dissolve, dissolveComplement)` applies the same world-anchored
  coverage split as rigid LOD handoffs. Its full union still rejects strokes around individual dissolve holes.
  The overload with trailing `bool cutInsideInteriors` applies the field to both rigid coverage masks, so the
  border follows the cut boundary in either occlusion mode. `DrawMeshSilhouette` has the same trailing flag
  and tests the extruded hull position. Existing overloads forward false, and skinned outlines remain uncut.
- Posed skinned target outlines use the grouped `DrawSkinnedOutline(MeshOutlineGroup, SkinnedMeshHandle,
  ReadOnlySpan<Matrix4x4>, Matrix4x4)` and `DrawSkinnedOutlineDissolved` methods. The two convenience overloads
  start a group for one part, with the five-argument form using scene-depth occlusion and the six-argument form
  accepting `MeshOutlineOcclusion`. A group may mix rigid and skinned parts and produces one outer projected
  silhouette. `Scene3D` copies the composed pose during each submission, and the group is valid only for the
  scene and frame that created it. An outline can be submitted without an ordinary `DrawSkinned`, and an ordinary
  draw with another pose does not change the outline pose. `UseGpuSkinning` selects the outline deformation path
  when the frame renders. The GPU and CPU paths consume the same copied pose. The full mask applies geometry and
  alpha cutout while ignoring partial dissolve. The visible mask follows the ordinary or complement dissolve
  keep rule. `SurfaceMaps.AlphaCutoff` reaches plain and dissolved ordinary skinned colour draws and both target
  outline masks. `Scene3DTileWorldScene` forwards the grouped methods, while the `ITileWorldScene` defaults keep
  older implementations compiling and make dissolved submission fall back to plain submission. Cascaded
  key-light skinned alpha cutout remains follow-up [#1097](https://github.com/APKiwiOrg/KhaozEngine/issues/1097).
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
- glTF index validation: every rigid, material-part, skinned and animation load rejects a triangle index outside
  its primitive's position count. The error names the asset path, bad index and vertex count. Manifest and LOD
  loads through `PropLoader` add the prop id, and direct `Normalize` calls apply the same check to caller-supplied
  `GltfMesh` values before transforming them. The raw `GltfMesh` constructor remains permissive for live
  containment paths that must degrade safely when handed malformed data.
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
- Material parts from bytes: `GltfLoader.LoadPartsWithMaterials(ReadOnlyMemory<byte> glb, string identity)`
  imports the same welded geometry, baked node transforms and decoded maps as the path overload, in
  the same first-use material order. A byte slice reads only that slice. The source is read without
  mutation, and `identity` names malformed indices in diagnostics. Attachment consumers apply
  `AttachmentGlb.ValidateRigidEmbedded` before import through `KhaozEngine.Attachments`.
  `KhaozEngine.Attachments.Render3D` supplies the skeleton binding and ships in Game3D.
- Byte import exception contract: since 21.0.0 `LoadPartsWithMaterials(glb, identity)`,
  `LoadSkinnedPartsWithMaterials(glb, identity)` and `LoadSkinned(glb, identity)` report a malformed glTF as
  `InvalidDataException`. Its
  message names `identity` and the importer's diagnostic is the inner exception, so callers never name a
  SharpGLTF type. Missing triangles, a missing skin, the bone cap and bad indices still throw
  `InvalidOperationException`.
- Skinned material parts: `GltfLoader.LoadSkinnedPartsWithMaterials(path)` and its
  `(ReadOnlyMemory<byte> glb, string identity)` overload return one `GltfSkinnedMeshPart(Mesh, Maps)` per
  source material in first-use order, including an untextured group for primitives without a material.
  Parts share one `Skeleton`, inverse binds and rest poses in skin joint order. Vertices stay in skin
  model coordinates, with no mesh-node transform bake, and retain aligned joints, normalized weights,
  tangents and material-factor times vertex colour. Maps contain decoded RGBA8 albedo, normal and
  metallic-roughness images plus each material's alpha cutoff, exactly as the rigid importer reads them.
  Byte imports need no temporary file and leave the source unchanged. Attachment consumers call
  `AttachmentGlb.ValidateSkinnedEmbedded` before import. `GltfLoader.LoadSkinned(glb, identity)` also
  imports a flattened mesh from bytes. The existing `LoadSkinned(path)` and
  `LoadSkinnedWithMaterial(path)` keep their flattened geometry and first-textured-material behaviour.
- Mixed attachment sets: the [attachment renderer arm](../KhaozEngine.Attachments.Render3D/README.md)
  binds garments with `loaded.Bind(map, hostMesh)` against actual host inverse binds. It queues one
  `DrawSkinned` per material group using borrowed joint model palettes. CPU and GPU skinning preserve
  tint, material maps, dissolve and complement, casting, shadow-only and inverted shadow dissolve,
  motion history and both outline presentations. Use one scene-created outline group for the body
  and its attachments. Consume mixed descriptors before their bound view's next preparation or
  submission. Steady-state attachment submission allocates no managed memory.
- Named nodes and sockets: `GltfLoader.LoadNamedNodes(path) -> IReadOnlyList<GltfNamedNode>` returns every logical
  node with a non-empty name, empties included, in logical-node order. Each `GltfNamedNode(Name, WorldTransform)`
  carries the node's world matrix exactly as `Load` bakes a mesh node's vertices, so an empty named `socket_nose`
  in a hull glb gives the attachment transform in the hull mesh's space. Unnamed nodes are skipped but still
  place their named descendants, duplicate names are all returned, and the asset needs no geometry.
- Per-vertex colour: `GltfLoader` reads the glTF `COLOR_0` attribute (rigid and skinned paths alike, normalized
  ubyte/ushort or float, vec3 or vec4) and MULTIPLIES it into the vertex's material base colour, which is what
  the spec says it is. So a kit piece painted from a palette in Blender keeps its authored colours with no
  texture and no material per shade, and a white `COLOR_0` changes nothing. A per-vertex colour also enters the
  rigid weld key, so two coplanar faces in different shades stay two vertices at their shared edge instead of the
  first shade winning. Flat material colours also enter the rigid weld key, using exact float identity so
  distinct material factors stay split when flattened for HLOD. Equal flat colours still weld, and per-vertex
  paint retains its existing quantization. A colour seam splits the
  smooth-normal and tangent accumulation too, so a mesh with no source `NORMAL` gains a shading crease where two
  shades meet, exactly as a UV seam has always done.
- Alpha cutout (foliage / leaf cards): `GltfLoader` reads each material's glTF `alphaMode` into
  `GltfMaterialMaps.AlphaCutoff` - `0` for OPAQUE (the default when absent, no clip), else the material's
  `alphaCutoff` (default 0.5 per spec) for MASK. glTF BLEND is out of scope for the mesh pass and treated as
  MASK. The value flows through `Scene3D.SurfaceMaps.AlphaCutoff` and the loaded mesh's material state, and the
  model fragment discards any texel whose sampled baseColor alpha is below it, so an alpha-cutout leaf-card
  texture renders as its silhouette instead of a solid (and, for the Quaternius kits, black-fringed) quad. An
  OPAQUE mesh (cutoff 0) is byte-identical to the pre-cutout render. The key light's cascaded shadow pass applies
  the same test to a MASK caster that has an albedo, so the leaf card casts its silhouette. For skinned meshes,
  `SurfaceMaps.AlphaCutoff` reaches plain and dissolved ordinary colour draws and both target outline masks, so
  their albedo cutout coverage stays in parity. Point-light shadow maps do not alpha-test yet and record the full
  quad. Baked kits pair this with a bake-time RGB dilation (alpha bleed)
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
  `LoadFlattenedAlbedo(glb, identity)` parses a binary glTF already in memory the same way, naming `identity` in
  errors. It has no directory to resolve a relative external URI against, so a caller that must read nothing beyond
  its bytes, like `NativeMapAssetLoader`, refuses external URIs first.
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
    clamped 0..1 at draw; `NameplateRenderer.ShouldCull` shares `WorldLabel`'s cull. Warmed styles with up to four
    bars draw without heap allocation. Larger plates build an uncached template.
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
  - Component nameplate templates (20.35.0), the modular form of the plate above. A `NameplateTemplate` is an
    ordered list of components, each bound to a `NameplateSlot` (`Name`, `Level`, `Health`, or any string key):
    `NameplateTextComponent` (font scale, optional eight-way outline, optional shadow, its own `MaxWidth`
    ellipsis cap), `NameplateBadgeComponent` (padded text on a rounded base fill, washed by the content's
    optional tint) and `NameplateBarComponent` (inner track width and height, zero width meaning the template's
    content width, border outside the track). Each component after the first carries a `NameplateAttach` rule
    (`Self` point, `Target` id, `TargetPoint`, `Offset`) that pins it to an earlier component or, with a null
    target, to the root. The constructor validates unique ids and backward-only targets. The template also
    carries the panel look, content width bounds and the same `EdgeBehavior`, `EdgeMargin` and `EdgeHysteresis`
    as `NameplateStyle`. Per-entity values live in a reusable `NameplateContent` (`SetText`, `SetBar`, `Clear`)
    whose `Opacity` multiplies every colour of the plate, so a fade needs no new template. A component whose slot
    is empty or holds the wrong kind does not draw. `NameplateTemplateLayout.Measure` is the GPU-free panel
    size, `NameplatePlacement.Place` has a template overload, and `NameplateRenderer.Draw` takes a template and
    content in a framebuffer-int and an `IDesignViewport` overload, both with a caller-held
    `NameplatePlacementState`. Warm template draws allocate nothing. The legacy `Nameplate` and `NameplateStyle`
    API is now a built-in template with unchanged draw calls. A legacy panel capped narrower than the "..."
    glyphs grows to fit them. The legacy cache keys on geometry, so a colour fade keeps its template, but a style
    whose geometry changes every frame rebuilds its template each frame, so animate that through a template
    instead. The point rules come from
    `AttachPoint` and `Attach` in Primitives, the outline from `TextOutline` in Render2D. Template files, an
    editor and a mod surface are tracked in https://github.com/APKiwiOrg/KhaozEngine/issues/1378.
  - Opt-in nameplate stacking (20.41.0). Keep one `NameplateStacker` for the lifetime of the plates. Each
    frame calls `Begin()`, `Add(long id, Vector4 rect, float rank, bool pinned)` for each natural panel,
    `Resolve(float dt)` once, then `RectOf(long id)` and `ReadOnlySpan<long> DrawOrder { get; }` to draw.
    `NameplateStacker(float topMargin = 4f)` accepts a finite non-negative margin, otherwise throwing
    `ArgumentOutOfRangeException`. Match it to `NameplateTemplate.EdgeMargin`. The public constants are
    `Gap = 2f`, `RankBand = 0.5f` and `TimeConstant = 0.12f`. Rects `(x, y, width, height)`, gap and margin
    use framebuffer pixels or, for a design pass, design units consistently. Rank is finite distance,
    larger meaning farther away. Retained ranks swap only beyond the 0.5 band. Equal initial ranks place
    by ascending id and draw by reverse id. Pins stay exact and are obstacles first. Other panels move
    upward only, preserving X, width and height even when wider than the viewport. Overlapping pins stay
    overlapped. A panel stops at the top margin, or its natural Y if already above it, so top saturation
    can leave overlaps. Solved panels otherwise clear each other by 2 units when room exists.
    Non-finite coordinates or rank, non-positive dimensions and overflowing edges are ignored by `Add`
    and never become obstacles. The last valid submission for an id wins, including rank and pin flag.
    `Begin` invalidates public results but retains private history until `Resolve` prunes missing ids.
    `RectOf` throws `KeyNotFoundException` before resolution or for an absent id. `DrawOrder` is borrowed
    until the next `Begin`, farthest first in sticky rank order with pins last and nearer pins on top.
    New plates start at the solved spot. Retained upward offsets ease exponentially with time constant
    0.12 seconds, independently of frame rate. Non-positive or non-finite dt does not advance retained
    offsets, while new plates still start solved and pins reset immediately. Easing follows solving,
    so a rank reversal or new collision can temporarily overlap. Collision-safe animation is not promised.
    Warm submissions, resolution, span traversal, rect lookup and drawing allocate nothing.
  - Caller-resolved template drawing and shared anchor projection (20.41.0) expose these overloads:

    ```csharp
    // NameplateStacker
    public NameplateStacker(float topMargin = 4f);
    public const float Gap = 2f, RankBand = 0.5f, TimeConstant = 0.12f;
    public void Begin();
    public void Add(long id, Vector4 rect, float rank, bool pinned);
    public void Resolve(float dt);
    public Vector4 RectOf(long id);
    public ReadOnlySpan<long> DrawOrder { get; }

    // NameplateRenderer
    public static bool DrawAtRect(SpriteBatch batch, SpriteFont font, Texture2D white, Vector4 panelRect,
        NameplateTemplate template, NameplateContent content, int viewportWidth, int viewportHeight);
    public static bool DrawAtRect(SpriteBatch batch, SpriteFont font, Texture2D white, Vector4 panelRect,
        NameplateTemplate template, NameplateContent content, IDesignViewport designViewport);
    // NameplateAnchorProjection, now public
    public static bool Project(IIsoCamera3D camera, Vector3 worldPos, Vector3 offset,
        int viewportWidth, int viewportHeight, out Vector2 pixel);
    public static bool Project(IIsoCamera3D camera, Vector3 worldPos, Vector3 offset,
        IDesignViewport designViewport, out Vector2 pixel);
    ```

    `Project` uses body-column X and head-point Y with the renderer's head-only fallback. `DrawAtRect`
    returns false for missing resources, non-positive viewport dimensions, invalid panel geometry or
    empty content. Geometry must be finite with positive size and finite edges. It does not project,
    distance-cull, clamp, deflect, resize components or call `NameplatePlacement.Place`. Keep the measured
    dimensions and matching template/content until drawing. The design overload takes a rect already in
    design space and uses the batch pass's existing transform without another screen-to-design conversion.
    Existing `Draw` calls remain unchanged. See the
    [canonical stacking example](../docs/USING-KHAOZENGINE.md#opt-in-nameplate-stacking-20410) for the complete
    project, cull, measure, place, stack and draw sequence with retained per-id content.

- Animation (`Animation/`, pure + GPU-free, driven off a `Skeleton` + glTF `AnimationClip`s):
  - `BoneSocket.Compose(local, jointModel, model)` attaches a rigid piece with the row-vector order
    `local * jointModel * model`. `ComposeRigid` uses the same order after removing scale and shear from the joint
    basis while keeping its translation. Use the rigid form for props that must follow a posed joint without
    inheriting non-rigid animation data. It keeps a reflected basis reflected. A non-finite, collapsed, or
    linearly dependent basis throws `ArgumentException` because no rigid orientation can be recovered.
  - `GltfLoader.LoadSkinned` retains each skeleton node's glTF name in `Skeleton.NodeNames` with an empty string for
    an unnamed node. `Skeleton.IndexOf` and `TryIndexOf` resolve those names, while `BoneIndexOfNode` maps a skeleton
    node back to its skin bone or returns `-1` for a non-joint ancestor. The skeleton includes every `skin.joints`
    node and the ancestors needed to compose it. A child outside `skin.joints` does not reach `Skeleton`, so an
    authored socket that needs name lookup remains a zero-weight skin joint.
  - `AnimationSampler` / `AnimationPlayer` - one-shot pose sampling and a stateful single-clip player with a
    crossfade (`Play(clip, crossfade)` -> `Update(dt)` -> `GetBonePalette(buffer)`). `Play` loops the clip; `PlayOnce`
    plays it ONCE and CLAMPS the playhead at the clip duration, holding the final frame (a death / knockdown pose that
    must settle and stay) - switching back via `Play` restores looping. `AnimationSampler.SampleInto`
    is the allocation-free sample into a reused per-node pose buffer; `AnimationPlayer.GetLocalPoses(buffer)` writes
    the composited LOCAL poses (the crossfade result before hierarchy composition) so a `LayeredAnimator` can take
    the locomotion crossfade as its base layer.
  - `AnimationSampler.SampleBlendInto(skel, samples, into, scratch)` samples a weighted set of `ClipSample(Clip, Time,
    Weight)` values, with `Time` in clip seconds, into one per-node local pose. It is a running normalised lerp
    through `JointPose.Lerp`, so the weights need not sum to one, and one positive-weight sample is bit-identical to
    `SampleInto`. Zero-weight samples are skipped, and when every weight is zero `into` keeps what it held. `into`
    and `scratch` are distinct buffers of `Skeleton.NodeCount` entries. A negative or nonfinite weight, or a missing
    clip on a positive weight, throws before anything is written. The call allocates nothing.
  - `Animation.Inspection.PoseProbe` samples a clip without a GPU and exposes each skeleton node's model-space
    matrix or position by node index or retained name. `SampleClip` takes a closed normalised phase, with phase `1`
    preserving the authored end key. `SampleClipAtSeconds` clamps to the authored time range. `SetLocals` inspects
    a caller-supplied local pose. Composition covers every node in the supplied `Skeleton`, including non-palette
    nodes in a code-built skeleton. A glTF-loaded child socket survives only when it is a zero-weight skin joint,
    which places it in the bone palette. The loader otherwise retains only skin joints and their ancestors.
  - `Animation.Inspection.FootPlant.Measure` samples one or more named feet over looping phases and returns the
    lowest sole height, its first phase, and the largest horizontal slide within a circular stance run. Supply the
    clip's forward `+Z` stride in metres so an in-place backward-moving foot is inspected in travel space. All-stance
    clips anchor their single run at phase `0`.
  - `Animation.Inspection.SegmentClearance.Min` samples a node-mounted segment against named-node `Capsule` axes
    over the closed phase range. Its signed result is positive for separation, zero for contact, and negative for
    penetration. Segment endpoints, capsule radii, and results are model-space metres. Both measurements require
    finite geometry and at least two samples, and run without a mesh, graphics device, or test framework.
  - `Animation.Inspection.ClipHygiene.Check` checks allowed nodes, translation policy, scale channels, raw
    quaternion units, key-time order, key density, loop continuity, and unresolved targets. Findings have stable
    rule identifiers, invariant details, and deterministic track, rule, key, and loop ordering.
  - `Animation.Inspection.ClipReport.Write` emits a canonical LF-terminated text snapshot in caller clip and phase
    order. Each phase contains every node's sampled local rotation in skeleton order, followed by requested
    model-space positions. Phase `1` preserves the authored end key. Names are escaped and floats use invariant
    fixed-six precision, making repeated UTF-8 encodings byte-identical.
  - `Animation.Inspection.PoseBlend.BlendInto` blends one caller-owned local pose buffer toward another. Its finite
    global weight is multiplied by an optional `BoneMask` per node and clamped to `[0, 1]`. Zero effective weight
    leaves the destination exactly unchanged, unit effective weight copies the source exactly, and intermediate
    weights lerp translation and scale while rotation follows the normalized shortest spherical arc. Warmed calls
    allocate no managed memory and need no mesh, graphics device, or test framework.
  - `Animation.Inspection.PoseBlend.AddInto` layers an additive pose onto a caller-owned local pose buffer. Each
    node adds its sample's offset from its reference in the joint's local frame. Rotation composes
    `destination * slerp(identity, inverse(reference) * sample, w)` along the shortest arc, and translation and
    scale add `(sample - reference) * w`. The finite weight, optional `BoneMask`, and `[0, 1]` clamp follow
    `BlendInto`. A destination equal to the reference at unit weight reproduces the sample. `LayeredAnimator`'s
    additive layers run the same code. Warmed calls allocate no managed memory.
  - `SkeletonContract` / `ContractJoint` state a skinned body's named joints, each with its parent and whether it
    deforms the skin, the root first and each parent before its children. Construction refuses an empty table, an
    unnamed or repeated joint, a second root and a parent declared after its child, naming the joint.
  - `ContractJointMap(skeleton, contract, stance, stanceClipName)` checks a loaded skeleton against a contract. It
    refuses more nodes than `SkinningMath.MaxBonesPerDraw`, two nodes of one name, and a contract joint that is
    missing, misparented or, other than the root, outside the skin. Unnamed nodes are left alone. The base is an
    optional one-key stance clip or the bind rest. A stance must carry `stanceClipName` (`stance` by default), key
    each track once, animate contract joints only and carry no scale track. `BaseLocal`, `BaseWorld`,
    `ParentBaseInverse` and `BodyAlignment` expose the base frames, and the frame accessors refuse a node outside
    the contract. `BodyAlignment` pairs with `BoneSocket.ComposeRigid` to seat a piece authored in the body's axes.
    Its homogeneous component stays exactly one when general inversion rounds it for oblique base axes.
    `SkinAtBase` deforms a skin to the base on the CPU and allocates, so call it at load.
  - `ClipRefusals` words a skin loader's clip refusals one way, naming the clip, the rule and the joint. `Only`
    throws on a second clip of one name. `NoLength`, `Unkeyed`, `Uncovered` and `Breach` return null for a clean
    clip and a message otherwise, so a loader chains them with `??` and throws the first. They cover a clip of no
    length, a required rotation or translation left unkeyed, a stance channel left unkeyed on the nodes a filter
    takes, and the first `ClipHygiene` finding. A joint name the skeleton lacks throws.
  - `SkinnedGrounding.MinimumY(vertices, inverseBind, palette, model, scratch)` returns the lowest world y of the
    DEFORMED skin, so a crouched or striding body can be set down on its ground. The caller lends one `Vector4`
    scratch entry per bone and the call allocates nothing. An unweighted vertex draws through the model alone, as
    the shader does. An empty skin returns positive infinity.
  - `MomentSchedule` / `MomentScheduleOptions` / `MomentFamily` / `Moment` / `MomentSide` schedule a standing
    body's idle moments as a pure function of its id and the clock. The clock is cut into slots that each hold
    one moment or none, with quiet ends, an empty share, a cap on empty runs and a salt. A family is drawn by
    weight, and a mirrored family plays left or right with even odds. `At(seed, seconds)` allocates nothing, and
    `At(seed, seconds, stoppedAt)` skips a moment that began before the body last stopped. `Moment.None` is the
    default value.
  - `LayeredAnimator` / `AnimationLayer` / `BoneMask` / `LayerMode` - N animation layers composited into one final
    skeleton pose: a base locomotion layer below, masked `Override` / `Additive` action layers above (attack while
    running). Each `AnimationLayer` is a clip + its own looping playhead + a blend weight + an optional `BoneMask` +
    a `LayerMode`. `BoneMask` is per-node weights 0..1 (`BoneMask.Full`/`.Empty`, `BoneMask.Subtree(skel, root, w)`
    for "this bone and all descendants at weight w" - the upper-body-action shape). The root may be a node index or
    a retained glTF name. `BoneMask.ForJoints(skel, jointNames, w)` weighs a named joint group without its
    descendants and refuses a name the skeleton lacks. Override lerps toward the layer
    pose by `weight x mask`. Additive applies the clip's delta from its first frame (the reference), scaled by
    `weight x mask`: the rotation delta is both EXTRACTED and APPLIED in the joint's LOCAL frame
    (`delta = inverse(reference) * sample`, applied as `base * delta`), so a base equal to the reference reproduces
    the clip's authored pose exactly. Translation and scale add componentwise (scale as an offset, so unit scale is
    a no-op). `SetBaseLocals` sets an external base (the locomotion
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

Renderer deps (the GPU backends, the `Silk.NET.Shaderc` and `Silk.NET.SPIRV.Cross` shader toolchain,
SharpGLTF) are confined to this package via
`KhaozEngine.Gpu`. See
`docs/USING-KHAOZENGINE.md`.

## Persistent grass and foliage

Create a `FoliageBatch` once from authored `FoliageInstance` mesh handles, transforms and stable thinning
ranks in `[0, 1)`. `Scene3D.DrawFoliage` submits it each frame with a focus and `FoliageRenderSettings`.
Transforms upload on first visibility and remain resident. CPU work selects spatial patch prefixes.
The vertex shader performs exact fading, wind and nearby actor bending with roots anchored at mesh minimum Y.

Wind strength is a fraction of blade height. Direction, speed and spatial frequency are configurable per
submission. `WindFadeBladePixels` stops wind on blades shorter than that many internal render target
pixels on screen and restores it fully at twice the value. It keys on projected size from the final camera
and render size, not on focus distance. A perspective camera calms distant blades first. The default
orthographic `IsoCamera3D` gives every blade the same pixel size, view height over render height, so equal
blades calm together at one zoom threshold. The height is world height over the pixel size at the root and
ignores view pitch, so the true on-screen height is about 11% lower at the default iso elevation. Zero, the
default, keeps wind at every size. Advance `Scene3D.EffectTimeSeconds` from the render clock. Supply up to
four `FoliageInteractor` positions, radii, strengths and agitation values for cosmetic bending and shiver.
Vertical separation prevents a different floor from pushing the foliage. Position must be finite, radius
finite and non-negative, and strength and agitation finite in `[0, 1]`. Strength defaults to 1 and
agitation to 0. All submission inputs are copied by value. Foliage is lit and supports material alpha
masks. It adds no collision or world state.

`FoliageInstance(Mesh, Transform, ThinningRank, Motion = default, CastsShadows = false)` stores motion
and casting per instance. Both `default(FoliageMotionProfile)` and `FoliageMotionProfile.Blade` resolve
to the legacy coefficients, including when a profile is omitted:

| Coefficient | Meaning | Blade default | Inclusive range |
|---|---|---|---|
| `BendScale` | Interactor lean multiplier | 1 | 0 to 4 |
| `MaxLean` | Combined wind and lean cap as a fraction of instance height | 0.65 | 0 to 1 |
| `WindScale` | Wind sway multiplier | 1 | 0 to 4 |
| `Shiver` | Leaf shiver amplitude as a fraction of instance height | 0 | 0 to 0.25 |

`new FoliageMotionProfile(BendScale, MaxLean, WindScale, Shiver)` rejects non-finite or out-of-range
coefficients with `ArgumentOutOfRangeException` naming the field. Motion differences do not split
patches or add draws. Casting differences do. Each retained GPU instance is 96 bytes. Each foliage
uniform payload is 288 bytes in a 512-byte aligned slot, including current and previous agitation.
Colour, temporal motion vectors and cascade depth share one displacement function. Temporal resets
use the current state as the previous state.

Create one `FoliageAgitation` tracker per moving actor and retain it between frames:

```csharp
// Once per tracked actor, not inside the frame loop.
var agitation = new FoliageAgitation();
// Each frame, reuse the interactor array and advance the scene clock.
scene.EffectTimeSeconds += dt;
interactors[0] = new FoliageInteractor(playerPosition, 0.8f, 0.8f,
    Agitation: agitation.Update(playerPosition, dt));
```

The helper is allocation-free. Its configurable `referenceSpeed`, `settleTime` and
`teleportResetDistance` default to 2 m/s, 0.6 s and 5 m. Each must be finite and strictly positive.
Horizontal speed sets agitation, clamped to `[0, 1]`, with linear settling to rest once movement stops.
A full 3D jump beyond the reset distance resets to zero. The first sample seeds at rest. A zero frame
time rebases without changing agitation, and `Reset()` clears the value and sampled position.
`Update(position, deltaTime)` rejects non-finite positions and non-finite or negative frame times.
Shiver uses the strongest single interactor's `falloff * Strength * Agitation`, never their sum.
Its fixed angular frequency is 14 rad/s (about 2.2 Hz). It is added after the lean cap, varies across
vertices and uses the squared height weight so roots remain anchored. Zero agitation gives no shiver.

Casting is opt-in through `FoliageInstance.CastsShadows`. Cascade shadows follow animated displacement,
including the camera's wind-fade pixel scale and material alpha cutout. Casting patches are gathered
before camera culling, so off-camera foliage can shadow visible ground. An animated caster redraws the
cascade atlas every frame. Removing the last one causes one clearing render before static reuse.
Point-light shadows draw casting animated foliage at its authored rest pose through the rigid caster
path, preserving static point-shadow caching. A rest-pose proxy stream is created lazily only for
casting batches when point shadows need it, with 128 GPU bytes per casting instance. It uploads at
first use and after a render-origin change, and is retired when its batch is released. Non-casting
foliage pays no point-proxy cost.

Dispose the batch when placements change or unload. The scene owns GPU buffers and retires them safely.
Mesh handles remain caller-owned and must belong to that scene. `LastFoliageStats` reports workload and
upload counts. `CandidateInstances` includes instances the shader rejects, rather than exact visible blades.
