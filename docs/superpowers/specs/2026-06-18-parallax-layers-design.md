# Camera feel layer: parallax background layers (5.x Render2D)

**Date:** 2026-06-18
**Package:** `KhaozEngine.Render2D` (5.x line — the engine)
**Status:** design approved, ready for implementation plan

## Goal

Final slice of the camera feel layer: **parallax background layers**. Background layers scroll at fractional
rates relative to the camera to convey depth, off the same `Camera2D`. The engine provides the scroll math;
the game owns the layer textures and the draw loop.

## Approach

Two small, pure units in `KhaozEngine.Render2D` (translation-only parallax; zoom/rotation are shared with the
main camera). Rejected: a richer `ParallaxBackground` manager that orders N layers — the draw is inherently
game-specific (the game has the textures), so a manager would mostly sort a list; YAGNI.

## Components

### `ParallaxLayer`

```csharp
namespace KhaozEngine.Render2D;

/// A background layer's parallax rate. Factor is per-axis relative to the camera: 0 = static (a fixed
/// backdrop / skybox), 1 = locked to the world (moves with the foreground), 0.5 = half speed (appears
/// farther away). The game derives a layer camera from ViewPosition and draws its sprites with it.
public readonly struct ParallaxLayer
{
    public readonly Vector2 Factor;

    public ParallaxLayer(Vector2 factor);
    public ParallaxLayer(float factor);   // uniform on both axes

    /// World position a layer's camera should sit at for the given main-camera position: cameraPosition * Factor.
    public Vector2 ViewPosition(Vector2 cameraPosition);
}
```

### `Parallax`

```csharp
namespace KhaozEngine.Render2D;

public static class Parallax
{
    /// Non-negative remainder (value mod size, in [0, size)) for seamlessly tiling a repeating background:
    /// the game draws copies starting at -Wrap(layerViewX, tileWidth) across the viewport. Returns 0 when
    /// size <= 0.
    public static float Wrap(float value, float size);
}
```

`Wrap` is a positive modulo: `value - size * floor(value / size)`, guarded so `size <= 0` returns 0 (no
divide-by-zero, no NaN).

## Usage (documented)

```csharp
var layer = new ParallaxLayer(new Vector2(0.5f, 1f));   // half-speed horizontally, locked vertically
var layerCam = new Camera2D { Zoom = camera.Zoom, Rotation = camera.Rotation,
                              Position = layer.ViewPosition(camera.Position) };
spriteBatch.Begin(layerCam);                            // draw this layer's sprites
// for an infinitely repeating strip of width W:
float start = -Parallax.Wrap(layerCam.Position.X, W);   // draw copies at start, start+W, start+2W, ...
```

## Testing (headless, pure)

New file `KhaozEngine.Tests/Render2DParallaxTests.cs`.

`ParallaxLayer`:
- `ViewPosition` with `Factor (0.5, 1)` at camera `(200, 50)` → `(100, 50)`.
- `Factor 0` → `(0, 0)` (static backdrop, independent of camera).
- `Factor 1` → equals the camera position (locked to the world).
- The uniform `float` ctor sets both axes to the same value.

`Parallax.Wrap`:
- `Wrap(250, 100) = 50`; `Wrap(100, 100) = 0`; `Wrap(0, 100) = 0`.
- `Wrap(-30, 100) = 70` (positive remainder, not -30).
- `Wrap(5, 0) = 0` and `Wrap(5, -2) = 0` (non-positive size guard, no NaN).

## Shipping (engine release ritual)

Additive → minor. Next 5.x version is **5.57.0**.

1. Bump `<KhaozEngine5xVersion>` to `5.57.0`.
2. Newest-first `CHANGELOG.md` entry (same commit).
3. Update the three guard-checked declarations (CONSUMERS, ROADMAP, README package refs) to 5.57.0.
4. In `docs/ROADMAP.md` camera section, move the parallax item from "Still open" to "Shipped". With parallax
   shipped, the camera feel-layer backlog is complete.
5. `dotnet pack -c Release -o ./local-feed` (cumulative).
6. Commit, `git tag v5.57.0`. (Push at branch-finish.)

No consumer adopts immediately; the planned side-scroller is the first user.

## Files

- `KhaozEngine.Render2D/ParallaxLayer.cs` (new)
- `KhaozEngine.Render2D/Parallax.cs` (new)
- `KhaozEngine.Tests/Render2DParallaxTests.cs` (new)
- `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` (release)
