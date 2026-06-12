# KhaozEngine.Graphics

Game-agnostic 2D rendering helpers for MonoGame games.

## Camera2D

A matrix camera: `Position` (world point at screen center), `Zoom`, `Rotation` (radians) ->
a view `Matrix`, plus `WorldToScreen` / `ScreenToWorld` and an optional `ClampPosition`
world-bounds helper.

The math is headless: the core methods take a `Viewport` argument, so no `GraphicsDevice` is
required to compute a matrix (handy for tests and tools). Convenience no-arg overloads use a
settable `Viewport` property.

```csharp
var camera = new Camera2D { Position = shipPosition, Zoom = 1.5f };

// Per-call viewport (split-screen, minimap, tests):
spriteBatch.Begin(transformMatrix: camera.GetViewMatrix(GraphicsDevice.Viewport));

// Or set the viewport once (refresh on resize) and use the no-arg overloads:
camera.Viewport = GraphicsDevice.Viewport;          // also on ClientSizeChanged
spriteBatch.Begin(transformMatrix: camera.GetViewMatrix());
Vector2 world = camera.ScreenToWorld(input.PointerPosition);
```

`Zoom` must be `> 0`. Follow-cam logic (smoothing, bounds tracking) lives game-side and
composes a `Camera2D`.

## CameraController

A reusable pan/zoom/pinch gesture controller that drives a `Camera2D` from an `InputManager`
(this package now references `KhaozEngine.Input`). Drag and two-finger drag pan; scroll wheel and
pinch zoom, clamped to `MinZoom`/`MaxZoom` and focused on the cursor/pinch midpoint; the view is
clamped into a caller-supplied world rectangle via `Camera2D.ClampPosition`. `TryGetTap` mirrors
`PannableCanvas` so a caller can tell a tap from a pan (place a tower on a tap, pan on a drag).

The step takes an explicit `Viewport`, so it is headless and unit-testable with no `GraphicsDevice`.

```csharp
var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
var controller = new CameraController(input, camera) { MinZoom = 0.5f, MaxZoom = 4f };

// per frame, after input.Update(...):
controller.Update(GraphicsDevice.Viewport, worldBounds);
if (controller.TryGetTap(out var pressWorld, out var releaseWorld)) { /* place on tap */ }
spriteBatch.Begin(transformMatrix: camera.GetViewMatrix());
```
