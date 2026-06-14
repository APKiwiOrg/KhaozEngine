# PannableCanvas - design

A reusable `KhaozEngine.UI` control: a viewport that owns a camera and lets a consumer
pan over content larger than the viewport. Generalizes the camera/pan code Nullwake's
`SkillTreeScreen` hand-rolls inline, so a node-graph / map screen needs no per-game
reinvention.

Reference (do not modify): `Nullwake.Core/Screens/SkillTreeScreen.cs` already does all of
this inline - a `Vector2 _cameraOffset`, drag pan via `_input.GetDragDelta`, wheel pan via
`_input.GetScrollIn`, `ClampCamera()` against auto-computed canvas bounds, scissor-clipped
drawing, and `ScreenToWorld`/`WorldToScreen` transforms.

## Goals

- One control that owns the camera offset, pans on drag and wheel within a caller-given
  viewport, clamps to caller-supplied content bounds plus padding, scissor-clips rendering,
  exposes world/screen transforms and a click-through-safe tap helper, and can center/focus
  the camera on a point or rect.
- Zero game-specific concepts. No levels, nodes, skills, towers. A generic pannable viewport.
- Consumers: Hardpoint (level-select map) first, Nullwake (`SkillTreeScreen` retrofit) later.
- Additive and opt-in. No changes to existing widgets or consumers.
- Zoom is out of scope; a single fixed scale, with the seam kept so zoom can be added later.

## Non-goals

- Zoom / pinch-zoom. A private `_zoom = 1f` seam is present but not exposed.
- Drawing any content itself (nodes, edges, decoration). The caller owns all drawing.
- Owning the viewport/content extents. The caller computes and sets them each frame.

## Surface

```csharp
namespace KhaozEngine.UI;

public sealed class PannableCanvas
{
    public PannableCanvas(InputManager input);

    // Per-frame config (caller sets before Update/Draw)
    public Rectangle Viewport { get; set; }        // virtual screen coords
    public Rectangle ContentBounds { get; set; }    // world coords, raw content extent
    public int Padding { get; set; } = 0;           // inflates content for clamp
    public float ScrollPanSpeed { get; set; } = 0.5f;
    public bool BlockInput { get; set; } = true;
    public Vector2 CameraOffset { get; }            // read-only pan state

    public void Update();                           // block region, pan on drag+wheel, clamp

    public Vector2 WorldToScreen(Vector2 world);    // -> virtual screen coords
    public Vector2 ScreenToWorld(Vector2 screen);   // virtual screen coords -> world
    public Vector2 PointerWorld { get; }            // ScreenToWorld(input.PointerPosition)

    public bool TryGetTap(out Vector2 pressWorld, out Vector2 releaseWorld);

    public void CenterOn(Vector2 world);            // put point at viewport center, then clamp
    public void Focus(Rectangle worldRect);         // CenterOn(rect center)
    public void CenterContent();                    // CenterOn(ContentBounds center)

    public void Draw(SpriteBatch sb, GraphicsDevice gd,
                     float renderScale, Matrix scaleMatrix, Action drawWorld);
}
```

### Dependency rationale

`VirtualResolution` is **not** headless-constructable - its constructor requires a
`GraphicsDeviceManager`. It is only needed for the scissor scale and the scale matrix, both
of which only matter inside `Draw()`. Everything else (pan, clamp, transforms, tap, focus)
works in virtual coordinates and needs only `InputManager`, which already reports
`PointerPosition` / `PressOrigin` in virtual coords.

So the testable core depends on `InputManager` alone, and `Draw()` takes the two raw values
(`renderScale`, `scaleMatrix`) instead of a `VirtualResolution`. Callers pass `vr.Scale` and
`vr.ScaleMatrix`. The control has zero dependency on `VirtualResolution`. `PrimitiveRenderer`
is not a dependency either - the caller owns all drawing.

## Behavior

### Camera math (zoom-shaped, `_zoom = 1f`)

```
center = (Viewport.X + Viewport.Width / 2f, Viewport.Y + Viewport.Height / 2f)
WorldToScreen(w) = center + w * _zoom + CameraOffset
ScreenToWorld(s) = (s - center - CameraOffset) / _zoom
```

`ScreenToWorld` is the exact inverse of `WorldToScreen`, so they round-trip. At `_zoom = 1`
this matches `SkillTreeScreen`'s transforms exactly. `WorldToScreen` and `ScreenToWorld` are
computed with plain arithmetic (no matrix), so they are headless and never touch a
`GraphicsDevice`.

### Update()

1. If `BlockInput`, call `_input.BlockInputRegion(Viewport)` so lower screens don't react to
   drags/scrolls that start inside the viewport.
2. `CameraOffset += _input.GetDragDelta(Viewport)` (zero unless a drag began inside Viewport).
3. `CameraOffset.Y += _input.GetScrollIn(Viewport) * ScrollPanSpeed` (wheel pans vertically;
   zero on mobile or when the pointer is outside Viewport).
4. **Clamp unconditionally** at the end of every `Update()`.

Clamping every frame (not only on input, as the reference does) means a shrinking
`ContentBounds` or a changed `Viewport` re-settles the camera. This is the one intentional
behavior change from the inline reference, and it is safe because clamp is idempotent.

### Clamp

Against `ContentBounds` inflated by `Padding` on all four sides (`PaddedBounds`). Lifted from
`SkillTreeScreen.ClampCamera`:

```
halfW = Viewport.Width / 2f;  halfH = Viewport.Height / 2f
minOffX = -PaddedBounds.Right + halfW;  maxOffX = -PaddedBounds.Left - halfW
minOffY = -PaddedBounds.Bottom + halfH; maxOffY = -PaddedBounds.Top - halfH

// X axis (Y identical with the Y values):
if (minOffX > maxOffX)               // padded content narrower than the viewport
    CameraOffset.X = -(PaddedBounds.X + PaddedBounds.Width / 2f);   // center it
else
    CameraOffset.X = Clamp(CameraOffset.X, minOffX, maxOffX);
```

### TryGetTap

```
if (_input.IsTapIn(Viewport))                 // press-origin AND release both inside Viewport
{
    pressWorld   = ScreenToWorld(_input.PressOrigin);
    releaseWorld = ScreenToWorld(_input.PointerPosition);
    return true;
}
pressWorld = releaseWorld = Vector2.Zero;
return false;
```

`IsTapIn` is the click-through-safe gate (it already enforces the press-origin invariant).
Returning both world points lets the caller hit-test each and require the same node - the
precision check `SkillTreeScreen` does today - without the control knowing what a node is.

### Focus

- `CenterOn(world)`: `CameraOffset = -world * _zoom`, then `Clamp()`. Lands `world` at the
  viewport center (subject to clamping).
- `Focus(rect)`: `CenterOn(rect center)`.
- `CenterContent()`: `CenterOn(ContentBounds center)` - the on-open default ("focus the
  frontier" is the caller passing its own rect to `Focus`).

All three use the current `Viewport`/`ContentBounds`, so the caller sets those before calling
(e.g. in `LoadContent` before `CenterContent()`).

### Draw

```
_scissorRasterizer ??= new RasterizerState { ScissorTestEnable = true };
gd.ScissorRectangle = new Rectangle(
    (int)(Viewport.X * renderScale), (int)(Viewport.Y * renderScale),
    Math.Max(0, (int)(Viewport.Width  * renderScale)),
    Math.Max(0, (int)(Viewport.Height * renderScale)));

Matrix world =
    Matrix.CreateScale(_zoom, _zoom, 1f) *
    Matrix.CreateTranslation(center.X + CameraOffset.X, center.Y + CameraOffset.Y, 0f);

sb.Begin(samplerState: SamplerState.PointClamp,
         rasterizerState: _scissorRasterizer,
         transformMatrix: world * scaleMatrix);
drawWorld();
sb.End();
```

The caller draws nodes/edges in **world coordinates** inside `drawWorld()` (e.g.
`renderer.DrawFilledCircle(sb, new Vector2(node.X, node.Y), r, c)`); the control's transform
maps world → virtual screen → physical pixels. Screen-space extras (a detail popup, a pinned
HUD) draw in the caller's own batch after `Draw()` returns, exactly as `SkillTreeScreen` draws
its popup outside the scissor batch today. The `Math.Max(0, …)` guard mirrors the reference's
protection against negative scissor dimensions during transition frames. `Draw()` is the only
method that touches a `GraphicsDevice`/`SpriteBatch`.

### Zoom seam

`_zoom` is a private field fixed at `1f`. It appears in the transforms and the draw matrix
(`CreateScale(_zoom)`), so adding zoom later is a localized change: expose a setter, adjust
`Clamp`/`CenterOn` for the zoom-about-viewport-center pivot, and the caller's world-space draw
code is unchanged. Not solved now, only seamed.

## Testing

Headless xUnit in `KhaozEngine.Tests`, mirroring `InputManagerTests` (drive `RawInputState`
frame by frame; `GameTime` is `new GameTime(TimeSpan.Zero, TimeSpan.FromSeconds(dt))`).
Construct `new PannableCanvas(new InputManager())`; set `Viewport`/`ContentBounds`; feed
input frames; assert on `CameraOffset` and the transforms. No `GraphicsDevice`.

- Pan accumulates offset: a drag inside the viewport moves `CameraOffset` by the drag delta.
- Drag that began outside the viewport does not pan.
- Wheel scroll pans `CameraOffset.Y` by `delta * ScrollPanSpeed`.
- Clamp keeps the camera within padded bounds (offset never exceeds the computed min/max).
- Clamp centers the axis when padded content is smaller than the viewport.
- `WorldToScreen`/`ScreenToWorld` round-trip for arbitrary points and offsets.
- `TryGetTap` maps press + release to the correct world points on a tap inside the viewport;
  returns false when the press began outside the viewport, or when the pointer was not released
  this frame. (An in-viewport pan-release also returns true - `IsTapIn` does not measure drag
  distance - but its press and release world points differ because the camera moved between them,
  so the caller's same-target check rejects it.)
- `CenterOn`/`Focus`/`CenterContent` place the target at the viewport center (when bounds are
  large enough that clamping does not move it).
- `BlockInput` toggles whether `Update()` reserves the viewport region.

## Release

Same commit: new `PannableCanvas.cs` + new test file, `CHANGELOG.md` entry (newest-first,
additive minor bump), `Directory.Build.props` `<Version>` bump (2.3.0 → 2.4.0),
`docs/CONSUMERS.md` engine-version line, then `dotnet pack -c Release -o ./local-feed`. Ping
the user so they can bump Hardpoint and build the level-select map on it.
