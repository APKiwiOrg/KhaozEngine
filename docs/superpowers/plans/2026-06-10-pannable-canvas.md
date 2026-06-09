# PannableCanvas Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable `PannableCanvas` control to `KhaozEngine.UI`: a viewport that owns a camera and lets a consumer pan over content larger than the viewport, generalizing the inline camera/pan code in Nullwake's `SkillTreeScreen`.

**Architecture:** A single `sealed class PannableCanvas` in `KhaozEngine.UI`. Its testable core (pan, clamp, world/screen transforms, tap, focus) depends only on `InputManager` and works in virtual coordinates, so it is headless-testable. `Draw()` is the only method touching a `GraphicsDevice`/`SpriteBatch`; it takes the virtual→physical `renderScale` + `scaleMatrix` as parameters (no `VirtualResolution` dependency, since `VirtualResolution` is not headless-constructable). The camera transform is additive and zoom-shaped around a private `_zoom = 1f` seam.

**Tech Stack:** net10.0, C# (ImplicitUsings disabled — all usings explicit), MonoGame.Framework.DesktopGL 3.8, xUnit headless tests.

Spec: `docs/superpowers/specs/2026-06-10-pannable-canvas-design.md`. Reference (read, do not modify): `~/Nullwake/Nullwake/Nullwake.Core/Screens/SkillTreeScreen.cs`.

---

## File structure

- Create: `KhaozEngine.UI/PannableCanvas.cs` — the control. One responsibility: a generic pannable viewport (camera offset + transforms + clip).
- Create: `KhaozEngine.Tests/PannableCanvasTests.cs` — headless tests mirroring `KhaozEngine.Tests/InputManagerTests.cs` style.
- Modify: `Directory.Build.props` — `<Version>` 2.3.0 → 2.4.0.
- Modify: `CHANGELOG.md` — newest-first entry.
- Modify: `docs/CONSUMERS.md` — engine-version line 2.3.0 → 2.4.0.
- Produce: `local-feed/*.2.4.0.nupkg` via `dotnet pack`.

No existing files are otherwise touched. Additive and opt-in.

---

## Task 1: Class skeleton — config, constructor, transforms

**Files:**
- Create: `KhaozEngine.UI/PannableCanvas.cs`
- Test: `KhaozEngine.Tests/PannableCanvasTests.cs`

- [ ] **Step 1: Write the failing test**

Create `KhaozEngine.Tests/PannableCanvasTests.cs`. The `Mouse`/`NoTouch` helpers mirror `InputManagerTests.cs` so input frames can be driven headlessly.

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Input;
using KhaozEngine.UI;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;

namespace KhaozEngine.Tests;

public class PannableCanvasTests
{
    private static readonly IReadOnlyList<GamePadState> NoPads =
        new[] { new GamePadState(), new GamePadState(), new GamePadState(), new GamePadState() };

    private static RawInputState Mouse(int x, int y, bool down, int scroll = 0) =>
        new(new Point(x, y), down, false, false, scroll,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static RawInputState NoTouch() =>
        new(Point.Zero, false, false, false, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static PannableCanvas MakeCanvas(InputManager im, Rectangle? content = null) =>
        new(im)
        {
            Viewport = new Rectangle(0, 0, 200, 200),
            ContentBounds = content ?? new Rectangle(-1000, -1000, 2000, 2000),
        };

    [Fact]
    public void ScreenWorldRoundTrips()
    {
        var im = new InputManager();
        var canvas = MakeCanvas(im);
        canvas.CenterOn(new Vector2(37, -19));   // give the camera a non-zero offset

        foreach (var w in new[] { new Vector2(0, 0), new Vector2(50, 80), new Vector2(-123, 456) })
            Assert.Equal(w, canvas.ScreenToWorld(canvas.WorldToScreen(w)));
    }
}
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: FAIL — compile error, `PannableCanvas` does not exist.

- [ ] **Step 3: Write minimal implementation**

Create `KhaozEngine.UI/PannableCanvas.cs`:

```csharp
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.UI;

/// <summary>
/// A generic pannable viewport: owns a camera offset and lets a caller pan over world-space
/// content larger than a caller-supplied viewport. Drag and wheel pan, clamps to caller-supplied
/// content bounds plus padding, scissor-clips rendering, and exposes world/screen transforms plus
/// a click-through-safe tap helper. No game-specific concepts. Zoom is not implemented (a private
/// <c>_zoom = 1f</c> seam is kept so it can be added later).
///
/// Per frame: set <see cref="Viewport"/> and <see cref="ContentBounds"/>, call <see cref="Update"/>
/// to pan/clamp, then <see cref="Draw"/> with a world-space draw callback. Query
/// <see cref="TryGetTap"/> for the world point(s) tapped this frame.
/// </summary>
public sealed class PannableCanvas
{
    private readonly InputManager _input;
    private Vector2 _cameraOffset;
    private float _zoom = 1f;                  // seam for future zoom; fixed at 1 for now
    private RasterizerState? _scissorRasterizer;

    /// <summary>Creates a pannable canvas bound to an input source.</summary>
    public PannableCanvas(InputManager input) => _input = input;

    /// <summary>The viewport rectangle in virtual screen coordinates. Set each frame.</summary>
    public Rectangle Viewport { get; set; }

    /// <summary>The raw content extent in world coordinates, used (inflated by <see cref="Padding"/>) for clamping. Set each frame.</summary>
    public Rectangle ContentBounds { get; set; }

    /// <summary>Extra slack in world units added on all sides of <see cref="ContentBounds"/> before clamping.</summary>
    public int Padding { get; set; }

    /// <summary>World units panned per unit of wheel-scroll delta (vertical).</summary>
    public float ScrollPanSpeed { get; set; } = 0.5f;

    /// <summary>When true, <see cref="Update"/> reserves the viewport via <c>InputManager.BlockInputRegion</c> so lower screens ignore drags/scrolls that start inside it.</summary>
    public bool BlockInput { get; set; } = true;

    /// <summary>The current camera offset (pan state). Read-only; change it via panning or the focus helpers.</summary>
    public Vector2 CameraOffset => _cameraOffset;

    private Vector2 ViewportCenter =>
        new(Viewport.X + Viewport.Width / 2f, Viewport.Y + Viewport.Height / 2f);

    /// <summary>Maps a world point to virtual screen coordinates.</summary>
    public Vector2 WorldToScreen(Vector2 world)
    {
        Vector2 c = ViewportCenter;
        return new Vector2(c.X + world.X * _zoom + _cameraOffset.X,
                           c.Y + world.Y * _zoom + _cameraOffset.Y);
    }

    /// <summary>Maps a virtual screen point back to world coordinates (exact inverse of <see cref="WorldToScreen"/>).</summary>
    public Vector2 ScreenToWorld(Vector2 screen)
    {
        Vector2 c = ViewportCenter;
        return new Vector2((screen.X - c.X - _cameraOffset.X) / _zoom,
                           (screen.Y - c.Y - _cameraOffset.Y) / _zoom);
    }

    /// <summary>Centers the camera so <paramref name="world"/> sits at the viewport center, then clamps.</summary>
    public void CenterOn(Vector2 world)
    {
        _cameraOffset = new Vector2(-world.X * _zoom, -world.Y * _zoom);
        Clamp();
    }

    private void Clamp()
    {
        // Placeholder until Task 2; no-op keeps CenterOn usable for the round-trip test.
    }
}
```

- [ ] **Step 4: Run the test to verify it passes**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: PASS (1 test).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.UI/PannableCanvas.cs KhaozEngine.Tests/PannableCanvasTests.cs
git commit -m "Add PannableCanvas skeleton: config, transforms, round-trip test"
```

---

## Task 2: Update — drag pan, clamp, block region

**Files:**
- Modify: `KhaozEngine.UI/PannableCanvas.cs`
- Test: `KhaozEngine.Tests/PannableCanvasTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `PannableCanvasTests.cs`:

```csharp
[Fact]
public void DragInsideViewportAccumulatesOffset()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);   // large content -> clamp does not interfere

    // Hover frame first so the press frame has zero pointer delta.
    im.Update(Mouse(50, 50, false), true);  canvas.Update();
    im.Update(Mouse(50, 50, true), true);   canvas.Update();   // press, delta 0
    im.Update(Mouse(70, 50, true), true);   canvas.Update();   // drag +20 x
    im.Update(Mouse(90, 50, true), true);   canvas.Update();   // drag +20 x

    Assert.Equal(new Vector2(40, 0), canvas.CameraOffset);
}

[Fact]
public void DragBeganOutsideViewportDoesNotPan()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);

    im.Update(Mouse(300, 300, false), true); canvas.Update();
    im.Update(Mouse(300, 300, true), true);  canvas.Update();   // press OUTSIDE viewport
    im.Update(Mouse(120, 140, true), true);  canvas.Update();   // move inside while down

    Assert.Equal(Vector2.Zero, canvas.CameraOffset);
}

[Fact]
public void ClampKeepsCameraWithinBounds()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im, new Rectangle(0, 0, 400, 400));   // content 400, viewport 200

    im.Update(Mouse(100, 100, false), true); canvas.Update();
    im.Update(Mouse(100, 100, true), true);  canvas.Update();
    im.Update(Mouse(5000, 100, true), true); canvas.Update();     // huge drag right

    // maxOffX = -Left - halfW = 0 - 100 = -100
    Assert.Equal(-100f, canvas.CameraOffset.X);
}

[Fact]
public void ClampCentersAxisWhenContentSmallerThanViewport()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im, new Rectangle(0, 0, 100, 100));   // content smaller than viewport

    im.Update(Mouse(100, 100, false), true);   canvas.Update();
    im.Update(Mouse(100, 100, true), true);    canvas.Update();
    im.Update(Mouse(5000, 5000, true), true);  canvas.Update();   // drag far in both axes

    // centered: -(X + W/2) = -(0 + 50) = -50 on each axis
    Assert.Equal(new Vector2(-50, -50), canvas.CameraOffset);
}

[Fact]
public void UpdateBlocksViewportRegionWhenBlockInputTrue()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);

    im.Update(NoTouch(), true);
    canvas.Update();
    Assert.True(im.IsInputBlocked(new Vector2(100, 100)));
    Assert.False(im.IsInputBlocked(new Vector2(500, 500)));

    canvas.BlockInput = false;
    im.Update(NoTouch(), true);   // clears the previous frame's blocked region
    canvas.Update();
    Assert.False(im.IsInputBlocked(new Vector2(100, 100)));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: FAIL — `Update` not defined / clamp is a no-op so the new assertions fail.

- [ ] **Step 3: Write the implementation**

In `PannableCanvas.cs`, add `using` already present (`MathHelper` is in `Microsoft.Xna.Framework`). Add the `Update` method and `PaddedBounds`, and replace the placeholder `Clamp` body:

```csharp
/// <summary>Reserves the viewport (if <see cref="BlockInput"/>), pans on drag and wheel, then clamps. Call once per frame before <see cref="Draw"/>.</summary>
public void Update()
{
    if (BlockInput) _input.BlockInputRegion(Viewport);

    _cameraOffset += _input.GetDragDelta(Viewport);

    int scroll = _input.GetScrollIn(Viewport);
    if (scroll != 0) _cameraOffset.Y += scroll * ScrollPanSpeed;

    Clamp();
}

private Rectangle PaddedBounds => new(
    ContentBounds.X - Padding, ContentBounds.Y - Padding,
    ContentBounds.Width + Padding * 2, ContentBounds.Height + Padding * 2);
```

Replace the placeholder `Clamp` with:

```csharp
private void Clamp()
{
    Rectangle b = PaddedBounds;
    float halfW = Viewport.Width / 2f;
    float halfH = Viewport.Height / 2f;

    float minOffX = -b.Right + halfW;
    float maxOffX = -b.Left - halfW;
    if (minOffX > maxOffX) _cameraOffset.X = -(b.X + b.Width / 2f);   // content narrower than view -> center
    else _cameraOffset.X = MathHelper.Clamp(_cameraOffset.X, minOffX, maxOffX);

    float minOffY = -b.Bottom + halfH;
    float maxOffY = -b.Top - halfH;
    if (minOffY > maxOffY) _cameraOffset.Y = -(b.Y + b.Height / 2f);
    else _cameraOffset.Y = MathHelper.Clamp(_cameraOffset.Y, minOffY, maxOffY);
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: PASS (6 tests). The `ScreenWorldRoundTrips` test still passes (clamp now real, but its content is large so the offset is unchanged).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.UI/PannableCanvas.cs KhaozEngine.Tests/PannableCanvasTests.cs
git commit -m "PannableCanvas: drag pan, clamp to padded bounds, block region"
```

---

## Task 3: Wheel scroll pans vertically

**Files:**
- Modify: `KhaozEngine.Tests/PannableCanvasTests.cs` (Update already handles scroll from Task 2)

- [ ] **Step 1: Write the failing test**

Append to `PannableCanvasTests.cs`:

```csharp
[Fact]
public void WheelScrollPansVerticallyByScrollPanSpeed()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);   // large content, pointer inside viewport

    im.Update(Mouse(50, 50, false, scroll: 0), true);    canvas.Update();   // baseline wheel value
    im.Update(Mouse(50, 50, false, scroll: 120), true);  canvas.Update();   // delta 120

    Assert.Equal(60f, canvas.CameraOffset.Y);   // 120 * 0.5
    Assert.Equal(0f, canvas.CameraOffset.X);
}

[Fact]
public void WheelScrollIgnoredWhenPointerOutsideViewport()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);

    im.Update(Mouse(500, 500, false, scroll: 0), true);   canvas.Update();
    im.Update(Mouse(500, 500, false, scroll: 120), true); canvas.Update();   // pointer outside

    Assert.Equal(0f, canvas.CameraOffset.Y);
}
```

- [ ] **Step 2: Run the tests to verify they pass immediately**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: PASS (8 tests). Scroll handling already shipped in Task 2's `Update`; these tests lock the behavior. (`GetScrollIn` returns 0 when the pointer is outside the viewport, so the second test passes.)

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/PannableCanvasTests.cs
git commit -m "PannableCanvas: lock wheel-scroll vertical pan behavior with tests"
```

---

## Task 4: TryGetTap + PointerWorld

**Files:**
- Modify: `KhaozEngine.UI/PannableCanvas.cs`
- Test: `KhaozEngine.Tests/PannableCanvasTests.cs`

- [ ] **Step 1: Write the failing tests**

Append to `PannableCanvasTests.cs`:

```csharp
[Fact]
public void TryGetTapMapsPressAndReleaseToWorld()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);   // offset stays 0 (no drag/scroll)

    im.Update(Mouse(120, 140, false), true); canvas.Update();
    im.Update(Mouse(120, 140, true), true);  canvas.Update();
    im.Update(Mouse(120, 140, false), true); canvas.Update();   // release -> tap

    Assert.True(canvas.TryGetTap(out var press, out var release));
    Assert.Equal(new Vector2(20, 40), press);     // screen 120,140 minus viewport center 100,100
    Assert.Equal(new Vector2(20, 40), release);
}

[Fact]
public void TryGetTapFalseWhenPressBeganOutsideViewport()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);

    im.Update(Mouse(300, 300, false), true); canvas.Update();
    im.Update(Mouse(300, 300, true), true);  canvas.Update();   // press outside
    im.Update(Mouse(120, 140, false), true); canvas.Update();   // release inside

    Assert.False(canvas.TryGetTap(out _, out _));
}

[Fact]
public void TryGetTapFalseWhenNotReleasedThisFrame()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);

    im.Update(Mouse(120, 140, false), true); canvas.Update();
    im.Update(Mouse(120, 140, true), true);  canvas.Update();   // still pressed

    Assert.False(canvas.TryGetTap(out _, out _));
}

[Fact]
public void PointerWorldMapsCurrentPointer()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);

    im.Update(Mouse(130, 160, false), true); canvas.Update();

    Assert.Equal(new Vector2(30, 60), canvas.PointerWorld);   // 130,160 minus center 100,100
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: FAIL — `TryGetTap` / `PointerWorld` not defined.

- [ ] **Step 3: Write the implementation**

In `PannableCanvas.cs`, add:

```csharp
/// <summary>The current pointer position in world coordinates (for hover highlighting).</summary>
public Vector2 PointerWorld => ScreenToWorld(_input.PointerPosition);

/// <summary>
/// True on the frame the viewport was tapped (press-origin and release both inside the viewport,
/// the click-through-safe invariant). Returns the press and release world points so the caller can
/// hit-test both and require the same target — the precision check for dense node graphs. A pan
/// that ends inside the viewport also returns true, but its press and release world points differ
/// (the camera moved between them), so the same-target check rejects it.
/// </summary>
public bool TryGetTap(out Vector2 pressWorld, out Vector2 releaseWorld)
{
    if (_input.IsTapIn(Viewport))
    {
        pressWorld = ScreenToWorld(_input.PressOrigin);
        releaseWorld = ScreenToWorld(_input.PointerPosition);
        return true;
    }
    pressWorld = releaseWorld = Vector2.Zero;
    return false;
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: PASS (12 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.UI/PannableCanvas.cs KhaozEngine.Tests/PannableCanvasTests.cs
git commit -m "PannableCanvas: TryGetTap (press+release world) and PointerWorld"
```

---

## Task 5: Focus — Focus(rect) + CenterContent

**Files:**
- Modify: `KhaozEngine.UI/PannableCanvas.cs`
- Test: `KhaozEngine.Tests/PannableCanvasTests.cs`

`CenterOn` already exists from Task 1. This task adds `Focus` and `CenterContent`.

- [ ] **Step 1: Write the failing tests**

Append to `PannableCanvasTests.cs`:

```csharp
[Fact]
public void CenterOnPlacesPointAtViewportCenter()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);   // large content -> no clamp

    canvas.CenterOn(new Vector2(50, 60));

    Assert.Equal(new Vector2(100, 100), canvas.WorldToScreen(new Vector2(50, 60)));
}

[Fact]
public void FocusCentersOnRectCenter()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im);

    canvas.Focus(new Rectangle(20, 20, 40, 40));   // center (40,40)

    Assert.Equal(new Vector2(100, 100), canvas.WorldToScreen(new Vector2(40, 40)));
}

[Fact]
public void CenterContentCentersOnContentMiddle()
{
    var im = new InputManager();
    var canvas = MakeCanvas(im, new Rectangle(0, 0, 400, 400));

    canvas.CenterContent();

    Assert.Equal(new Vector2(-200, -200), canvas.CameraOffset);   // -(content center 200,200)
    Assert.Equal(new Vector2(100, 100), canvas.WorldToScreen(new Vector2(200, 200)));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: FAIL — `Focus` / `CenterContent` not defined. (`CenterOnPlacesPointAtViewportCenter` passes already.)

- [ ] **Step 3: Write the implementation**

In `PannableCanvas.cs`, add below `CenterOn`:

```csharp
/// <summary>Centers the camera on the middle of <paramref name="worldRect"/>, then clamps. (Becomes fit-to-rect if zoom is added.)</summary>
public void Focus(Rectangle worldRect) =>
    CenterOn(new Vector2(worldRect.X + worldRect.Width / 2f, worldRect.Y + worldRect.Height / 2f));

/// <summary>Centers the camera on the middle of <see cref="ContentBounds"/>, then clamps. The typical on-open default.</summary>
public void CenterContent() =>
    CenterOn(new Vector2(ContentBounds.X + ContentBounds.Width / 2f, ContentBounds.Y + ContentBounds.Height / 2f));
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter PannableCanvasTests`
Expected: PASS (15 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.UI/PannableCanvas.cs KhaozEngine.Tests/PannableCanvasTests.cs
git commit -m "PannableCanvas: Focus(rect) and CenterContent"
```

---

## Task 6: Draw — scissor clip + world-space transform

`Draw` touches `GraphicsDevice`/`SpriteBatch`, so it is verified by compilation + consumer integration, not a headless unit test (the existing widgets' draw paths are likewise untested headlessly).

**Files:**
- Modify: `KhaozEngine.UI/PannableCanvas.cs`

- [ ] **Step 1: Write the implementation**

In `PannableCanvas.cs`, add (`Math` needs `using System;`, already present):

```csharp
/// <summary>
/// Scissor-clips to the viewport and invokes <paramref name="drawWorld"/> with a SpriteBatch whose
/// transform maps world coordinates -> virtual screen -> physical pixels. The caller draws nodes/edges
/// in world coordinates inside the callback. Pass <c>vr.Scale</c> and <c>vr.ScaleMatrix</c> for
/// <paramref name="renderScale"/> / <paramref name="scaleMatrix"/>. Draw screen-space extras (popups,
/// pinned HUD) in your own batch after this returns.
/// </summary>
public void Draw(SpriteBatch sb, GraphicsDevice gd, float renderScale, Matrix scaleMatrix, Action drawWorld)
{
    _scissorRasterizer ??= new RasterizerState { ScissorTestEnable = true };

    gd.ScissorRectangle = new Rectangle(
        (int)(Viewport.X * renderScale),
        (int)(Viewport.Y * renderScale),
        Math.Max(0, (int)(Viewport.Width * renderScale)),
        Math.Max(0, (int)(Viewport.Height * renderScale)));

    Vector2 c = ViewportCenter;
    Matrix world =
        Matrix.CreateScale(_zoom, _zoom, 1f) *
        Matrix.CreateTranslation(c.X + _cameraOffset.X, c.Y + _cameraOffset.Y, 0f);

    sb.Begin(samplerState: SamplerState.PointClamp,
             rasterizerState: _scissorRasterizer,
             transformMatrix: world * scaleMatrix);
    drawWorld();
    sb.End();
}
```

- [ ] **Step 2: Build to verify it compiles**

Run: `dotnet build KhaozEngine.UI/KhaozEngine.UI.csproj -c Release`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full test suite (nothing regressed)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS — all existing tests plus the 15 PannableCanvas tests.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.UI/PannableCanvas.cs
git commit -m "PannableCanvas: Draw with scissor clip and world-space transform"
```

---

## Task 7: Release 2.4.0 to local-feed

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`
- Produce: `local-feed/*.2.4.0.nupkg`

- [ ] **Step 1: Bump the version**

In `Directory.Build.props`, change `<Version>2.3.0</Version>` to `<Version>2.4.0</Version>`.

- [ ] **Step 2: Add the CHANGELOG entry (newest-first)**

In `CHANGELOG.md`, insert directly under the `# Changelog` ... intro block and above `## KhaozEngine 2.3.0`:

```markdown
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
```

- [ ] **Step 3: Update the engine-version line in CONSUMERS**

In `docs/CONSUMERS.md`, change the line
`**Engine current version:** \`2.3.0\` ...` to `\`2.4.0\``.
Leave the per-consumer matrix rows at their current versions (they bump when each consumer adopts). Update the trailing `_Last verified:_` line's engine version to `2.4.0` if present.

- [ ] **Step 4: Ensure local-feed exists and pack**

Run:
```bash
mkdir -p local-feed
dotnet pack -c Release -o ./local-feed
```
Expected: `Successfully created package` lines for `KhaozEngine.Input/.Screens/.UI/.Ecs/.Time` at `2.4.0`. Do NOT delete older `.nupkg` files (consumers pin).

- [ ] **Step 5: Verify the UI package contains the new type**

Run: `ls local-feed/KhaozEngine.UI.2.4.0.nupkg`
Expected: the file exists.

- [ ] **Step 6: Commit (version bump + changelog + consumers together)**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md
git commit -m "Release KhaozEngine 2.4.0 (add PannableCanvas)"
```

- [ ] **Step 7: Stop and ping the user**

Do NOT `git tag` or push yet. Per the user's request, report that 2.4.0 is in `local-feed`, so they can bump Hardpoint's `KhaozEngine.UI` reference and build the level-select map on it. Tag/push (which triggers CI publish to GitHub Packages) is a separate step the user authorizes after Hardpoint validates.

---

## Self-review notes

- **Spec coverage:** camera offset + drag/wheel pan (Tasks 2–3), clamp to content+padding with center-when-smaller (Task 2), scissor clip + world transform Draw (Task 6), World/Screen transforms + round-trip (Task 1), `TryGetTap` press+release click-through-safe (Task 4), `CenterOn`/`Focus`/`CenterContent` (Tasks 1 & 5), zoom seam (`_zoom` present throughout), `InputManager`-only core + `Draw` takes scale/matrix (all tasks), tests mirroring `InputManagerTests` (every task), CHANGELOG + version bump + CONSUMERS + local-feed (Task 7). All covered.
- **Naming consistency:** `Viewport`, `ContentBounds`, `Padding`, `ScrollPanSpeed`, `BlockInput`, `CameraOffset`, `WorldToScreen`, `ScreenToWorld`, `PointerWorld`, `TryGetTap`, `CenterOn`, `Focus`, `CenterContent`, `Draw` are used identically in the spec, tests, and implementation.
- **TryGetTap nuance:** `IsTapIn` does not measure drag distance, so an in-viewport pan-release also returns true; the caller distinguishes via differing press/release world points (documented on the method and in the spec). `TryGetTapFalseWhenNotReleasedThisFrame` and the press-outside test pin the genuine false cases.
- **Press-frame delta:** every drag/tap test inserts a hover frame before pressing so `GetDragDelta` (which returns `PointerDelta`) is zero on the press frame.
