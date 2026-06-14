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

`Zoom` must be `> 0`. Framing helpers: `CenterOn(world)` puts a world point at the viewport center;
`Focus(rect, viewport, paddingFraction, minZoom, maxZoom)` is a fit-to-rect contain zoom (sets zoom so
the rect fills the view, then centers on it) — handy for "frame the whole level" or "zoom to selection".

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

## CameraFollow

Drives a `Camera2D` to follow a moving target. The game decides *what* to follow; `CameraFollow` owns
the smoothing, an optional deadzone, and the bounds clamp. Use it instead of `CameraController` on a
screen with a follow-cam (the two are mutually exclusive per screen).

Smoothing is frame-rate-independent (`1 - exp(-Stiffness * dt)`); `Stiffness <= 0` snaps. The deadzone
is a screen-space rectangle the target can move within before the camera chases; `Rectangle.Empty`
centers on the target.

```csharp
var camera = new Camera2D { Viewport = GraphicsDevice.Viewport };
var follow = new CameraFollow(camera) { Stiffness = 8f, Deadzone = new Rectangle(360, 240, 200, 120) };

// per frame:
follow.Update(playerWorldPos, dt, GraphicsDevice.Viewport, levelBounds);
spriteBatch.Begin(transformMatrix: camera.GetViewMatrix());
```

## Isometric toolkit (render-only)

Project a 2:1 diamond grid at draw time. No grid/tiles/pathfinding: keep your own world model.

`IsometricProjection` (configurable footprint, default 64x32, configurable `heightScale`; implements
`IIsometricProjection` so you can fake it in headless tests):

```csharp
var iso = new IsometricProjection();
Vector2 screen = origin + iso.WorldToScreen(tileX, tileY, z);  // z lifts up the screen
Vector2 ground = iso.ScreenToGround(mouseScreen - origin);     // pick on flat ground (z = 0)
Vector2 onTile = iso.ScreenToWorld(mouseScreen - origin, z);   // pick on a raised plane (terrain)
```

`IsoDepth.DepthKey(wx, wy, z, layer, zWeight)` returns a comparable `IsoDepthKey` for Y-sorting your
own draw list: primary order `wx + wy + z * zWeight` (`zWeight` defaults to 1; raise it so a tall stack
sorts in front of a nearer neighbour, or `0` to ignore height), integer `layer` as tiebreak.

`PrimitiveRenderer` iso shapes: `DrawIsoDiamond` (filled tile), `DrawIsoBlock` (top + two shaded side
faces for a given height), `DrawIsoEllipse` (filled 2:1, blob shadows) and `DrawIsoEllipseOutline`
(stroked 2:1, range rings). `ColorHelper.Scale(color, factor)` is the per-channel shade helper.
