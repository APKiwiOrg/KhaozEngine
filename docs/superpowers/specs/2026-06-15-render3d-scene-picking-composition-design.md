# Render3D: multi-instance scene + camera picking + AppWindow composition — design

Date: 2026-06-15
Status: draft for review
Ships as: `KhaozEngine.Render3D` `5.13.0-experimental` (shared 5.x line)

## Why

`KhaozEngine.Render3D` is still demo-shaped: `Scene3D` loads ONE model and `Spin`s it, and `Render3DHost`
owns its own SDL window. That proved the renderer + look (the POC go-gate), but a real game needs three
generic capabilities the module does not have. They are the prerequisite (Phase A) for the Hardpoint 3D
vertical slice (Phase B, a separate spec); all three are game-agnostic and belong in the engine.

This is additive to the 5.x line and verified the usual way: pure math headless-tested, the composed scene
checked by `Render3DSnapshot`→PNG (Metal-only locally, consistent with the rest of the 5.x work).

## Scope (Phase A)

Three units in `KhaozEngine.Render3D`:

### 1. Multi-instance `Scene3D`

Today `Scene3D.LoadModel(mesh)` holds a single mesh and `RenderInternal` draws it once with a spin matrix.
Replace that with a scene that holds several meshes and draws many instances per frame.

- `MeshHandle LoadMesh(GltfMesh mesh)` — upload a mesh to GPU buffers once, return a lightweight handle.
- Immediate-mode submission per frame (mirrors `SpriteBatch`):
  - `scene.Begin()` — clear the frame's instance list.
  - `scene.Draw(MeshHandle mesh, Matrix4x4 world)` — queue one instance (optionally a tint later; not in
    this slice).
  - the surface (unit 3) flushes all queued instances in one scene render (camera + `PixelPostProcess`).
- `Camera` (`IsoCamera3D`) and `Post` (`PixelPostProcessSettings`) stay on the scene as today.
- The single-model `LoadModel`/`Spin` demo API is removed; `Render3DHost` and `Render3DSample` migrate to
  the instance API (submit one or two instances).

Rationale: immediate-mode (re-submit each frame) matches a game loop where instance transforms change every
frame, and keeps the engine free of scene-graph retained state / node lifetime concerns.

### 2. `IsoCamera3D` picking

- `Vector3 ScreenToGround(Vector2 screenPixel, int viewportWidth, int viewportHeight, float groundY = 0f)` —
  unproject the screen point through the inverse `ViewProjection` to a world ray, intersect the horizontal
  plane `y = groundY`, return the world hit. For the orthographic iso camera the ray direction is constant
  (`Forward`); the function still derives it generally so it holds if a perspective camera is added later.
- `Ray ScreenToRay(Vector2 screenPixel, int viewportWidth, int viewportHeight)` — the underlying ray
  (origin + direction), exposed for non-ground picks. `Ray` is a small `readonly struct { Vector3 Origin,
  Direction }` in Render3D.
- Pure math, headless-tested: a known world point projected to screen (via `ViewProjection`) and back through
  `ScreenToGround` round-trips; the ground hit lies on `y = groundY`; picks track camera pan/zoom.

### 3. `Render3DSurface` — compose into an `AppWindow`

Today 3D can only render through `Render3DHost` (its own window). Add a surface that draws the scene into a
shared `AppWindow` frame so a `Render2D` HUD composes on top — the "3D world + 2D UI in one window" path.

- `new Render3DSurface(AppWindow window)` — builds the Veldrid scene resources against the window's
  `GraphicsDevice` / output.
- `surface.Scene` — the `Scene3D` (camera, post, mesh loading, instance submission).
- `surface.Render(Frame frame)` — render the queued instances (through `PixelPostProcess`) into the frame's
  framebuffer using the frame's command list. Opaque: the 3D pass owns the background for that frame.
- Composition contract, one window, one frame:
  1. `surface3d.Scene.Begin()` + submit instances; `surface3d.Render(frame)` (3D world, fills the frame).
  2. `surface2d.NewFrame(frame)` + `Batch.Begin(...)` … `End()` (HUD alpha-blends on top).
  `AppWindow` already clears the framebuffer at frame start; the 3D post-process blit writes opaque colour
  over it, then 2D draws on top.

This requires `Scene3D`/`ModelRenderer`/`PixelPostProcess` to accept an external `CommandList` + target
`Framebuffer` (today `Render3DHost` owns them). `Render3DHost` is refactored to drive the same path with its
own window, so the standalone sample keeps working.

## Architecture / data flow

```
AppWindow.Run(frame =>
    scene.Begin()
    for each visible thing: scene.Draw(meshHandle, worldMatrix)     // towers, enemies, tiles, projectiles
    surface3d.Render(frame)        // iso camera -> low-res RT -> PixelPostProcess -> blit into frame FB
    surface2d.NewFrame(frame); batch.Begin(viewport) ... batch.End()  // HUD over the 3D
)
picking:  scene.Camera.ScreenToGround(pointer.Position, frame.Width, frame.Height)  // tile under cursor
```

`PixelPostProcess` keeps its low-res RT + palette/dither/edge chain; only its final blit target changes from
"the host swapchain" to "the supplied framebuffer". The Metal gotchas already solved (clip-Y flip, MRT-clear
collapse, alpha-marker background) are unchanged — instances reuse the same model pipeline.

## Out of scope (Phase A)

Per-instance material/tint, lighting beyond the existing single directional sun, shadows, frustum culling,
animation, a retained scene graph, depth-sorted transparency, and anything Hardpoint-specific (board, tiles,
gameplay). Those are later (Phase B is the game; tint/animation are their own engine slices when needed).

## Testing

- `IsoCamera3DPickingTests` (headless): `ScreenToGround` round-trips a known point; hit lies on the ground
  plane; centre-screen maps to the camera target's ground point; picks shift correctly with `Target`/`Zoom`.
- `Scene3D` instance bookkeeping (headless where possible): `Begin` clears, `Draw` queues, the queued
  count/transforms are what render consumes (extract the instance list as a testable seam; GPU draw stays
  visual).
- Visual: `Render3DSnapshot`→PNG of a multi-instance scene (e.g. a 3×3 grid of the test model at distinct
  transforms) confirms instances render at the right places through the iso camera; a second snapshot with a
  2D overlay confirms composition order.

## Release

Additive minor on the shared 5.x line: bump `<KhaozEngine5xVersion>` 5.12.0 → `5.13.0-experimental`,
CHANGELOG + ROADMAP, pack the 5 packages, tag `v5.13.0-experimental`. Then Phase B (Hardpoint slice) pins
5.13.0 and consumes `Render3DSurface` + `ScreenToGround` + the instance API.
