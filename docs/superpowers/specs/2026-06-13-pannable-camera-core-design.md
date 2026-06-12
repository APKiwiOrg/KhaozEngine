# PannableCanvas delegates pan/zoom/clamp/tap to a shared Camera2D core (3.8.0)

## Problem

`KhaozEngine.UI/PannableCanvas.cs` and `KhaozEngine.Graphics/CameraController.cs` (shipped 3.7.0)
both contain pan/zoom gesture logic on different coordinate conventions:

- **PannableCanvas**: additive `_cameraOffset` model, a `_zoom = 1f` seam fixed at 1 (zoom never
  implemented), an inset sub-rectangle `Viewport` (X/Y offset matters), scissor-clipped `Draw`,
  `Padding`, `ScrollPanSpeed`, `BlockInput`, `TryGetTap`. Wheel = vertical pan.
- **CameraController**: drives a `Camera2D` (Position = world point at viewport center, real zoom),
  wheel + pinch zoom about focus, `ClampPosition` bounds, headless `Update(Viewport, worldBounds)`,
  `TryGetTap`. Wheel = zoom.

The two will diverge as either evolves. This was explicitly deferred from 3.7.0 to avoid regressing
games already on PannableCanvas (Hardpoint's map runs on it).

## Goal

Centralize the pan / zoom / clamp / tap math so both classes drive a `Camera2D` and share **one**
implementation of pan-by-delta, zoom-about-focus, pinch, clamp, and tap. The only per-class code
left is the wheel policy (zoom vs vertical-pan) and the clamp source. PannableCanvas additionally
gains real pinch-to-zoom (previously the zoom seam was inert).

## Coordinate reconciliation (the core fact)

The two transform models are the same affine map up to a sign:

- PannableCanvas: `screen = viewportCenter + world·zoom + cameraOffset`
- Camera2D (rotation 0): `screen = (world − Position)·zoom + (W/2, H/2)`

Equate (with the viewport `(X,Y)` offset applied as the outer translation Camera2D omits today):

    cameraOffset = −Position·zoom

Every clamp branch maps exactly too: PannableCanvas's offset range `[−Right+halfW, −Left−halfW]`
is identically `Camera2D.ClampPosition`'s Position range `[Left+halfW, Right−halfW]`, and the
content-smaller-than-view centering branch matches on both the value and the boundary (`==`) case.

So `CameraOffset` becomes a derived `−Position·Zoom`, and the rest delegates to Camera2D.

### Why exactness holds

`PannableCanvasTests` assert with **exact** `Assert.Equal` at integer values and zoom 1. Under the
substitution every tested quantity stays bit-identical: pan is `Position −= dragDelta/zoom` (÷1 is
exact), wheel pan is `Position += (0, −scroll·speed/zoom)`, clamp returns the same integer, and
`−Position·1f` round-trips the offset exactly. `WorldToScreen` via the Camera2D matrix is a pure
translate+scale on integers (no inversion). `ScreenToWorld` inverts a translation+uniform-scale
matrix, exact for these integer cases.

**Fallback (TDD-gated):** if `Matrix.Invert` round-trip breaks any exact-equality test, PannableCanvas
keeps a direct-affine `ScreenToWorld` derived from `Position`/`Zoom`/`viewportCenter` instead of the
matrix. Same math, guaranteed bit-identical. The test suite decides which path ships.

## Design

### 1. Camera2D (Graphics) — generalize + two new camera ops

All additive except the X/Y term, which is a no-op when X = Y = 0 (every current call site).

- **`GetViewMatrix`**: final translation becomes `T(viewport.X + W/2, viewport.Y + H/2)` (was
  `T(W/2, H/2)`). Makes inset viewports correct for *any* consumer (a future inset CameraController
  too). `ClampPosition` is unchanged — it is world-space and X/Y-independent. New `CameraTests` case
  asserts a non-zero-offset viewport maps `Position` to the inset rect's center.
- **`void PanByScreenDelta(Vector2 d)`** → `if (d == Zero || Zoom <= 0) return; Position -= d / Zoom;`
  Lifted verbatim from `CameraController.PanByScreenDelta`.
- **`void ZoomAboutScreenPoint(float target, Vector2 focusScreen, Viewport vp, float min, float max)`**
  → clamp `target` to `[min,max]`; if unchanged return; else keep the world point under `focusScreen`
  fixed (`worldBefore − worldAfter` correction). Lifted verbatim from `CameraController.ApplyZoom`,
  with min/max passed in (Camera2D stays stateless about zoom limits).

### 2. Two shared gesture helpers (Graphics, public, additive)

- **`PinchGestureTracker`** — owns `_wasPinching` / `_prevMidpoint`. `Apply(Camera2D cam, Pinch pinch,
  Viewport vp, bool enablePan, bool enableZoom, float min, float max)`: on a continuing pinch, pan by
  `cam.PanByScreenDelta(pinch.Midpoint − _prevMidpoint)` when `enablePan`; zoom via
  `cam.ZoomAboutScreenPoint(cam.Zoom · pinch.Scale, pinch.Midpoint, vp, min, max)` when `enableZoom`
  and `pinch.Scale > 0`; store midpoint; set active. `Reset()` clears active (called on non-pinch
  frames). The single pinch pan+zoom state machine.
- **`CameraGestures.TryGetTap(InputManager input, Camera2D cam, Viewport vp, out Vector2 pressWorld,
  out Vector2 releaseWorld)`** — the press-origin tap→world body: `IsTapIn(vp.Bounds)` then map
  `PressOrigin` and `PointerPosition` through `cam.ScreenToWorld(p, vp)`. Used by both classes.

### 3. CameraController refactors to consume #1 and #2

No public API change. Behavior byte-identical, held green by `CameraControllerTests` (1e-2 tol) — the
arithmetic is moved, not changed. Its pinch branch becomes a `PinchGestureTracker.Apply` call, its
`PanByScreenDelta`/`ApplyZoom` become `Camera2D` calls, its `TryGetTap` becomes `CameraGestures.TryGetTap`.

### 4. PannableCanvas (UI) — delegates; gains UI → Graphics project reference

- Drops `_cameraOffset` and `_zoom` fields; holds a `Camera2D _camera`. `CameraOffset` is derived
  `−_camera.Position · _camera.Zoom`.
- `WorldToScreen` / `ScreenToWorld` delegate to `_camera.WorldToScreen/ScreenToWorld(world, Viewport)`
  (real viewport, X/Y now honored). `Draw`'s world matrix becomes
  `_camera.GetViewMatrix(Viewport) · scaleMatrix`. (Fallback path keeps direct-affine ScreenToWorld
  if exactness demands.)
- `CenterOn(world)` → `_camera.Position = world; Clamp();`. `Focus`/`CenterContent` unchanged callers.
- `Clamp()` → `_camera.Position = _camera.ClampPosition(_camera.Position, PaddedBounds, Viewport);`.
- `TryGetTap` → `CameraGestures.TryGetTap(_input, _camera, Viewport, ...)`.
- `Update` gesture map:
  - `if (BlockInput) _input.BlockInputRegion(Viewport);`
  - **pinch present** → `_pinch.Apply(_camera, pinch, Viewport, EnablePan, EnableZoom, MinZoom, MaxZoom)` (NEW)
  - **else** → `_pinch.Reset();` then drag-pan `_camera.PanByScreenDelta(_input.GetDragDelta(Viewport))`
    (when `EnablePan`) + wheel-vertical-pan `_camera.Position += (0, −scroll·ScrollPanSpeed/Zoom)`
  - `Clamp();`
  - Mouse never pinches, so the existing mouse-only tests stay byte-identical.
- New public API: `MinZoom` / `MaxZoom` (defaults mirror CameraController: 0.1 / 10), `EnableZoom`
  / `EnablePan` (default true), `Camera` (expose the `Camera2D`). `Padding` / `ScrollPanSpeed` /
  `BlockInput` / scissor `Draw` / `PointerWorld` preserved.
- New TDD tests for PannableCanvas pinch-zoom (two-finger, `InputManager(isMobile: true)`), mirroring
  CameraController's pinch tests, plus `EnableZoom = false` ⇒ pinch does not zoom.

### Packaging / cycle check

UI → {Input, Graphics}; Graphics → Input; Effects → UI. UI → Graphics is acyclic (Graphics never
references UI). The `KhaozEngine.UI` package gains a transitive `KhaozEngine.Graphics` dependency —
additive.

## Testing

- All existing `PannableCanvasTests`, `CameraTests`, `CameraControllerTests` stay green (the first two
  unchanged; controller refactor verified byte-identical).
- New `CameraTests`: inset (non-zero X/Y) viewport maps correctly.
- New `PannableCanvasTests`: pinch zooms about midpoint; two-finger pan; `EnableZoom = false` disables
  pinch zoom; transforms stay zoom-correct after a pinch.
- `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj` green.

## Release (3.8.0, minor)

Additive API + refactor. The one behavior change — Camera2D honoring viewport X/Y — affects only the
previously-unused non-zero-offset path; called out loudly in CHANGELOG. New UI → Graphics package dep
noted in CHANGELOG + CONSUMERS.

Ritual: bump `<Version>` to 3.8.0 in `Directory.Build.props` → CHANGELOG entry → CONSUMERS engine line
+ note the UI→Graphics dep → `dotnet pack -c Release -o ./local-feed` → commit → `git tag v3.8.0` →
push main + tag. Verify Hardpoint builds against the new package (its map is on PannableCanvas; pinch
zoom is now default-on — note it can opt out via `EnableZoom = false`).

## Non-goals

- Modifier+wheel zoom in PannableCanvas (wheel stays vertical-pan). Pinch is the zoom gesture.
- Rotation gestures.
- Migrating Hardpoint's map to CameraController (PannableCanvas stays its own type; only the core is shared).
