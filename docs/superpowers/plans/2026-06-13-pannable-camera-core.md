# PannableCanvas / Camera2D Shared Core Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `PannableCanvas` (UI) and `CameraController` (Graphics) both drive a `Camera2D` and share one implementation of pan-by-delta, zoom-about-focus, pinch, clamp, and tap - and give PannableCanvas real pinch-to-zoom.

**Architecture:** Promote `PanByScreenDelta` and `ZoomAboutScreenPoint` onto `Camera2D`; generalize `Camera2D.GetViewMatrix` to honor viewport X/Y; add two shared gesture helpers (`PinchGestureTracker`, `CameraGestures.TryGetTap`) in Graphics. Refactor `CameraController` to consume them (byte-identical). Rewrite `PannableCanvas` to delegate its transform/clamp/tap math to a backing `Camera2D` and add pinch-zoom, while keeping its scissor `Draw`, `BlockInput`, `Padding`, `ScrollPanSpeed`, and wheel-as-vertical-pan semantics. Release as 3.8.0.

**Tech Stack:** net10.0, MonoGame.Framework.DesktopGL 3.8, xUnit. Headless tests build `RawInputState` frame-by-frame.

**Working dir:** worktree `feat/pannable-camera-core` at `/Users/antonio/KhaozEngine-pannable-core`. All commands run from there.

---

## Task 1: Camera2D honors viewport X/Y in GetViewMatrix

**Files:**
- Modify: `KhaozEngine.Graphics/Camera2D.cs:37-43` (GetViewMatrix final translation)
- Test: `KhaozEngine.Tests/CameraTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `KhaozEngine.Tests/CameraTests.cs` (inside the class):

```csharp
    [Fact]
    public void InsetViewport_HonorsOffset_MapsPositionToInsetCenter()
    {
        var camera = new Camera2D { Position = new Vector2(50f, 60f), Zoom = 1f };
        var inset = new Viewport(300, 200, 400, 300);   // center = (300+200, 200+150) = (500, 350)
        AssertClose(new Vector2(500f, 350f), camera.WorldToScreen(camera.Position, inset));
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~InsetViewport_HonorsOffset"`
Expected: FAIL - got `(200, 150)` (X/Y ignored), expected `(500, 350)`.

- [ ] **Step 3: Change GetViewMatrix to honor X/Y**

In `KhaozEngine.Graphics/Camera2D.cs`, change the final translation line of `GetViewMatrix`:

```csharp
    public Matrix GetViewMatrix(Viewport viewport)
    {
        return Matrix.CreateTranslation(-Position.X, -Position.Y, 0f)
            * Matrix.CreateRotationZ(Rotation)
            * Matrix.CreateScale(Zoom, Zoom, 1f)
            * Matrix.CreateTranslation(viewport.X + viewport.Width * 0.5f, viewport.Y + viewport.Height * 0.5f, 0f);
    }
```

Also update the XML doc summary on `GetViewMatrix` to read: "translate to the viewport center `(X + W/2, Y + H/2)`" (was "viewport center").

- [ ] **Step 4: Run the new test and the full CameraTests**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: PASS (all). The existing tests use `Viewport(0,0,W,H)` so X+W/2 = W/2 - unchanged.

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Graphics/Camera2D.cs KhaozEngine.Tests/CameraTests.cs
git commit -m "feat(Graphics): Camera2D.GetViewMatrix honors viewport X/Y offset (inset viewports)"
```

---

## Task 2: Promote PanByScreenDelta + ZoomAboutScreenPoint onto Camera2D

**Files:**
- Modify: `KhaozEngine.Graphics/Camera2D.cs` (add two public methods)
- Test: `KhaozEngine.Tests/CameraTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `KhaozEngine.Tests/CameraTests.cs`:

```csharp
    [Fact]
    public void PanByScreenDelta_MovesPositionOppositeDividedByZoom()
    {
        var cam = new Camera2D { Zoom = 2f, Position = Vector2.Zero };
        cam.PanByScreenDelta(new Vector2(40f, 0f));
        AssertClose(new Vector2(-20f, 0f), cam.Position);   // 40 / 2 = 20, opposite (grab-and-drag)
    }

    [Fact]
    public void PanByScreenDelta_IgnoresZeroAndDegenerateZoom()
    {
        var cam = new Camera2D { Zoom = 0f, Position = new Vector2(5f, 5f) };
        cam.PanByScreenDelta(new Vector2(10f, 10f));   // Zoom <= 0 guarded -> no-op
        AssertClose(new Vector2(5f, 5f), cam.Position);
    }

    [Fact]
    public void ZoomAboutScreenPoint_KeepsFocusWorldPointFixed()
    {
        var cam = new Camera2D { Zoom = 1f, Viewport = Vp };
        var focus = new Vector2(500f, 300f);   // Vp center (400,300) -> world (100,0)
        var worldBefore = cam.ScreenToWorld(focus, Vp);
        cam.ZoomAboutScreenPoint(2f, focus, Vp, 0.1f, 10f);
        Assert.Equal(2f, cam.Zoom, 3);
        AssertClose(worldBefore, cam.ScreenToWorld(focus, Vp));   // focal world point pinned under focus
    }

    [Fact]
    public void ZoomAboutScreenPoint_ClampsToMax()
    {
        var cam = new Camera2D { Zoom = 1f };
        cam.ZoomAboutScreenPoint(50f, new Vector2(400f, 300f), Vp, 0.1f, 10f);
        Assert.Equal(10f, cam.Zoom, 3);
    }
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PanByScreenDelta|FullyQualifiedName~ZoomAboutScreenPoint"`
Expected: FAIL - methods do not exist (compile error).

- [ ] **Step 3: Add the methods to Camera2D**

In `KhaozEngine.Graphics/Camera2D.cs`, add before the closing brace:

```csharp
    /// <summary>Moves the camera so world content tracks a screen drag of <paramref name="screenDelta"/>:
    /// the world moves by <c>screenDelta / Zoom</c>, applied opposite to the drag (grab-and-drag).
    /// No-op for a zero delta or a non-positive <see cref="Zoom"/>.</summary>
    public void PanByScreenDelta(Vector2 screenDelta)
    {
        if (screenDelta == Vector2.Zero || Zoom <= 0f) return;
        Position -= screenDelta / Zoom;
    }

    /// <summary>Sets <see cref="Zoom"/> to <paramref name="targetZoom"/> clamped to
    /// <c>[<paramref name="minZoom"/>, <paramref name="maxZoom"/>]</c> while keeping the world point
    /// currently under <paramref name="focusScreen"/> fixed on screen. No-op if the clamped zoom equals
    /// the current zoom.</summary>
    public void ZoomAboutScreenPoint(float targetZoom, Vector2 focusScreen, Viewport viewport, float minZoom, float maxZoom)
    {
        float clamped = MathHelper.Clamp(targetZoom, minZoom, maxZoom);
        if (clamped == Zoom) return;

        Vector2 worldBefore = ScreenToWorld(focusScreen, viewport);
        Zoom = clamped;
        Vector2 worldAfter = ScreenToWorld(focusScreen, viewport);
        Position += worldBefore - worldAfter;
    }
```

- [ ] **Step 4: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraTests"`
Expected: PASS (all).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Graphics/Camera2D.cs KhaozEngine.Tests/CameraTests.cs
git commit -m "feat(Graphics): Camera2D.PanByScreenDelta + ZoomAboutScreenPoint (shared camera ops)"
```

---

## Task 3: Shared gesture helpers - PinchGestureTracker + CameraGestures

**Files:**
- Create: `KhaozEngine.Graphics/PinchGestureTracker.cs`
- Create: `KhaozEngine.Graphics/CameraGestures.cs`
- Create: `KhaozEngine.Tests/CameraGesturesTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/CameraGesturesTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using KhaozEngine.Graphics;
using KhaozEngine.Input;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Microsoft.Xna.Framework.Input.Touch;
using Xunit;

namespace KhaozEngine.Tests;

public class CameraGesturesTests
{
    private const float Tol = 1e-2f;
    private static Viewport Vp => new Viewport(0, 0, 800, 600);   // center (400, 300)

    private static readonly IReadOnlyList<GamePadState> NoPads =
        new[] { new GamePadState(), new GamePadState(), new GamePadState(), new GamePadState() };

    private static RawInputState Mouse(int x, int y, bool down) =>
        new(new Point(x, y), down, false, false, 0,
            new KeyboardState(), NoPads, Array.Empty<TouchPoint>(), Rectangle.Empty);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    [Fact]
    public void PinchTracker_FirstFrame_StoresMidpoint_NoPanNoZoom()
    {
        var cam = new Camera2D { Viewport = Vp };
        var tracker = new PinchGestureTracker();
        tracker.Apply(cam, new Pinch(true, new Vector2(400, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 10f);
        AssertClose(Vector2.Zero, cam.Position);
        Assert.Equal(1f, cam.Zoom, 3);
    }

    [Fact]
    public void PinchTracker_SecondFrame_PansByMidpointTravel()
    {
        var cam = new Camera2D { Viewport = Vp };
        var tracker = new PinchGestureTracker();
        tracker.Apply(cam, new Pinch(true, new Vector2(400, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        tracker.Apply(cam, new Pinch(true, new Vector2(430, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        AssertClose(new Vector2(-30, 0), cam.Position);   // midpoint +30, zoom 1 -> pan -30
    }

    [Fact]
    public void PinchTracker_Reset_ClearsContinuity()
    {
        var cam = new Camera2D { Viewport = Vp };
        var tracker = new PinchGestureTracker();
        tracker.Apply(cam, new Pinch(true, new Vector2(400, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        tracker.Reset();
        tracker.Apply(cam, new Pinch(true, new Vector2(430, 300), 100f, 0f, 1f), Vp, true, true, 0.1f, 100f);
        AssertClose(Vector2.Zero, cam.Position);   // reset -> treated as first frame -> no pan
    }

    [Fact]
    public void CameraGestures_TryGetTap_MapsPressAndRelease()
    {
        var cam = new Camera2D();
        var im = new InputManager();
        im.Update(Mouse(450, 320, false), true);
        im.Update(Mouse(450, 320, true), true);
        im.Update(Mouse(450, 320, false), true);   // release -> tap
        Assert.True(CameraGestures.TryGetTap(im, cam, Vp, out var press, out var release));
        AssertClose(new Vector2(50, 20), press);    // 450,320 minus center 400,300
        AssertClose(new Vector2(50, 20), release);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraGesturesTests"`
Expected: FAIL - `PinchGestureTracker` / `CameraGestures` do not exist (compile error).

- [ ] **Step 3: Create PinchGestureTracker**

Create `KhaozEngine.Graphics/PinchGestureTracker.cs`:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.Graphics;

/// <summary>
/// The shared two-finger pinch state machine: on a continuing pinch it pans a <see cref="Camera2D"/>
/// by the midpoint travel and zooms about the pinch midpoint. Owns the across-frame state
/// (whether a pinch was already in progress, and the previous midpoint) so both
/// <see cref="CameraController"/> and <c>PannableCanvas</c> apply identical pinch behaviour.
/// </summary>
public sealed class PinchGestureTracker
{
    private bool _active;
    private Vector2 _prevMidpoint;

    /// <summary>
    /// Applies one pinch frame to <paramref name="camera"/>: when the pinch was already in progress
    /// and <paramref name="enablePan"/>, pans by <c>pinch.Midpoint - previousMidpoint</c>; when
    /// <paramref name="enableZoom"/> and <c>pinch.Scale &gt; 0</c>, zooms by <c>Zoom * pinch.Scale</c>
    /// about the midpoint, clamped to <c>[<paramref name="minZoom"/>, <paramref name="maxZoom"/>]</c>.
    /// The first frame only records the midpoint (no pan). Call <see cref="Reset"/> on a non-pinch frame.
    /// </summary>
    public void Apply(Camera2D camera, Pinch pinch, Viewport viewport,
                      bool enablePan, bool enableZoom, float minZoom, float maxZoom)
    {
        if (_active && enablePan)
            camera.PanByScreenDelta(pinch.Midpoint - _prevMidpoint);

        if (enableZoom && pinch.Scale > 0f)
            camera.ZoomAboutScreenPoint(camera.Zoom * pinch.Scale, pinch.Midpoint, viewport, minZoom, maxZoom);

        _prevMidpoint = pinch.Midpoint;
        _active = true;
    }

    /// <summary>Clears the in-progress flag so the next <see cref="Apply"/> is treated as a fresh
    /// pinch (no pan on its first frame). Call once per frame when no pinch is present.</summary>
    public void Reset() => _active = false;
}
```

- [ ] **Step 4: Create CameraGestures**

Create `KhaozEngine.Graphics/CameraGestures.cs`:

```csharp
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;

namespace KhaozEngine.Graphics;

/// <summary>Shared input-to-camera gesture helpers used by both <see cref="CameraController"/> and
/// <c>PannableCanvas</c>, so the press-origin tap math has a single implementation.</summary>
public static class CameraGestures
{
    /// <summary>
    /// True on the frame <paramref name="viewport"/> is tapped (press-origin and release both inside it,
    /// the click-through-safe invariant). Returns the press and release points in world coordinates via
    /// <paramref name="camera"/> so the caller can hit-test both and require the same target. A pan also
    /// satisfies the invariant, but the camera moved between press and release, so its press/release
    /// world points differ and the same-target check rejects it.
    /// </summary>
    public static bool TryGetTap(InputManager input, Camera2D camera, Viewport viewport,
                                 out Vector2 pressWorld, out Vector2 releaseWorld)
    {
        if (input.IsTapIn(viewport.Bounds))
        {
            pressWorld = camera.ScreenToWorld(input.PressOrigin, viewport);
            releaseWorld = camera.ScreenToWorld(input.PointerPosition, viewport);
            return true;
        }
        pressWorld = releaseWorld = Vector2.Zero;
        return false;
    }
}
```

- [ ] **Step 5: Run to verify pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraGesturesTests"`
Expected: PASS (all 4).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Graphics/PinchGestureTracker.cs KhaozEngine.Graphics/CameraGestures.cs KhaozEngine.Tests/CameraGesturesTests.cs
git commit -m "feat(Graphics): PinchGestureTracker + CameraGestures.TryGetTap (shared gesture core)"
```

---

## Task 4: Refactor CameraController to consume the shared core (byte-identical)

This is a refactor under a green suite: no behavior change, no new test. `CameraControllerTests` is the safety net.

**Files:**
- Modify: `KhaozEngine.Graphics/CameraController.cs`

- [ ] **Step 1: Confirm the suite is green before refactoring**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraControllerTests"`
Expected: PASS (all).

- [ ] **Step 2: Replace pinch state + private helpers with the shared core**

In `KhaozEngine.Graphics/CameraController.cs`:

Replace the private fields block:

```csharp
    private Viewport _lastViewport;
    private bool _wasPinching;
    private Vector2 _prevPinchMidpoint;
```

with:

```csharp
    private Viewport _lastViewport;
    private readonly PinchGestureTracker _pinch = new();
```

Replace the body of `Update` (the `TryGetPinch` if/else through the clamp line) with:

```csharp
    public void Update(Viewport viewport, Rectangle worldBounds)
    {
        _lastViewport = viewport;
        Rectangle bounds = viewport.Bounds;
        if (BlockInput) _input.BlockInputRegion(bounds);

        if (_input.TryGetPinch(out Pinch pinch))
        {
            _pinch.Apply(_camera, pinch, viewport, EnablePan, EnableZoom, MinZoom, MaxZoom);
        }
        else
        {
            _pinch.Reset();

            if (EnablePan)
                _camera.PanByScreenDelta(_input.GetDragDelta(bounds));

            if (EnableZoom)
            {
                int scroll = _input.GetScrollIn(bounds);
                if (scroll != 0)
                    _camera.ZoomAboutScreenPoint(_camera.Zoom * MathF.Pow(WheelZoomStep, scroll / 120f),
                                                 _input.PointerPosition, viewport, MinZoom, MaxZoom);
            }
        }

        _camera.Position = _camera.ClampPosition(_camera.Position, worldBounds, viewport);
    }
```

Replace the `TryGetTap` body:

```csharp
    public bool TryGetTap(out Vector2 pressWorld, out Vector2 releaseWorld) =>
        CameraGestures.TryGetTap(_input, _camera, _lastViewport, out pressWorld, out releaseWorld);
```

Delete the private `PanByScreenDelta` and `ApplyZoom` methods entirely (now on `Camera2D`). Keep the XML doc comment that preceded the old `PanByScreenDelta`/`ApplyZoom` region only if it documents `Update`; otherwise remove the now-orphaned comments. The `MathF` using (`using System;`) is still needed for `MathF.Pow`.

- [ ] **Step 3: Run the CameraController suite - must stay green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~CameraControllerTests"`
Expected: PASS (all). If any fail, the refactor changed behavior - diff against the original arithmetic in Tasks 2-3.

- [ ] **Step 4: Commit**

```bash
git add KhaozEngine.Graphics/CameraController.cs
git commit -m "refactor(Graphics): CameraController drives Camera2D via shared pan/zoom/pinch/tap core"
```

---

## Task 5: PannableCanvas delegates transform/clamp/tap to a backing Camera2D

Refactor under a green suite. PannableCanvas keeps drag + wheel-vertical-pan (no pinch yet); existing `PannableCanvasTests` must stay byte-identical.

**Files:**
- Modify: `KhaozEngine.UI/KhaozEngine.UI.csproj` (add Graphics project ref)
- Modify: `KhaozEngine.UI/PannableCanvas.cs` (full rewrite of internals)

- [ ] **Step 1: Confirm PannableCanvasTests green before refactoring**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PannableCanvasTests"`
Expected: PASS (all).

- [ ] **Step 2: Add the Graphics project reference to KhaozEngine.UI**

In `KhaozEngine.UI/KhaozEngine.UI.csproj`, add to the `<ItemGroup>` that holds the Input ref:

```xml
    <ProjectReference Include="../KhaozEngine.Graphics/KhaozEngine.Graphics.csproj" />
```

so it reads:

```xml
    <ProjectReference Include="../KhaozEngine.Input/KhaozEngine.Input.csproj" />
    <ProjectReference Include="../KhaozEngine.Graphics/KhaozEngine.Graphics.csproj" />
```

- [ ] **Step 3: Rewrite PannableCanvas internals to delegate**

Replace `KhaozEngine.UI/PannableCanvas.cs` with:

```csharp
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using KhaozEngine.Input;
using KhaozEngine.Graphics;
using XnaViewport = Microsoft.Xna.Framework.Graphics.Viewport;

namespace KhaozEngine.UI;

/// <summary>
/// A generic pannable viewport over world-space content larger than a caller-supplied viewport.
/// Drag and wheel pan (wheel = vertical pan), optional two-finger pinch zoom, clamps to caller-supplied
/// content bounds plus padding, scissor-clips rendering, and exposes world/screen transforms plus a
/// click-through-safe tap helper. No game-specific concepts.
///
/// <para>Delegates its transform / clamp / pan / zoom / tap math to a backing
/// <see cref="KhaozEngine.Graphics.Camera2D"/> (shared with
/// <see cref="KhaozEngine.Graphics.CameraController"/>), so the gesture math has a single
/// implementation. <see cref="CameraOffset"/> is the legacy additive-offset view of the camera
/// (<c>-Position * Zoom</c>).</para>
///
/// Per frame: set <see cref="Viewport"/> and <see cref="ContentBounds"/>, call Update to pan/zoom/clamp,
/// then Draw with a world-space draw callback. Query TryGetTap for the world point(s) tapped this frame.
/// </summary>
public sealed class PannableCanvas
{
    private readonly InputManager _input;
    private readonly Camera2D _camera = new();
    private readonly PinchGestureTracker _pinch = new();
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

    /// <summary>When true, Update reserves the viewport via <c>InputManager.BlockInputRegion</c> so lower screens ignore drags/scrolls that start inside it.</summary>
    public bool BlockInput { get; set; } = true;

    /// <summary>When false, drag and two-finger pan are ignored.</summary>
    public bool EnablePan { get; set; } = true;

    /// <summary>When false, pinch zoom is ignored (the canvas stays at its current zoom; wheel still pans).</summary>
    public bool EnableZoom { get; set; } = true;

    /// <summary>Smallest allowed camera zoom (pinch zoom-out clamps here).</summary>
    public float MinZoom { get; set; } = 0.1f;

    /// <summary>Largest allowed camera zoom (pinch zoom-in clamps here).</summary>
    public float MaxZoom { get; set; } = 10f;

    /// <summary>The backing camera. Exposed so callers can read/drive zoom/position directly.</summary>
    public Camera2D Camera => _camera;

    /// <summary>The current camera offset (legacy additive pan state): <c>-Position * Zoom</c>. Read-only; change it via panning or the focus helpers.</summary>
    public Vector2 CameraOffset => -_camera.Position * _camera.Zoom;

    private XnaViewport CameraViewport => new(Viewport.X, Viewport.Y, Viewport.Width, Viewport.Height);

    /// <summary>Maps a world point to virtual screen coordinates.</summary>
    public Vector2 WorldToScreen(Vector2 world) => _camera.WorldToScreen(world, CameraViewport);

    /// <summary>Maps a virtual screen point back to world coordinates (inverse of <see cref="WorldToScreen"/>).</summary>
    public Vector2 ScreenToWorld(Vector2 screen) => _camera.ScreenToWorld(screen, CameraViewport);

    /// <summary>Centers the camera so <paramref name="world"/> sits at the viewport center, then clamps.</summary>
    public void CenterOn(Vector2 world)
    {
        _camera.Position = world;
        Clamp();
    }

    /// <summary>Centers the camera on the middle of <paramref name="worldRect"/>, then clamps.</summary>
    public void Focus(Rectangle worldRect) =>
        CenterOn(new Vector2(worldRect.X + worldRect.Width / 2f, worldRect.Y + worldRect.Height / 2f));

    /// <summary>Centers the camera on the middle of <see cref="ContentBounds"/>, then clamps. The typical on-open default.</summary>
    public void CenterContent() =>
        CenterOn(new Vector2(ContentBounds.X + ContentBounds.Width / 2f, ContentBounds.Y + ContentBounds.Height / 2f));

    /// <summary>Reserves the viewport (if <see cref="BlockInput"/>), pans on drag and wheel, zooms on pinch, then clamps. Call once per frame before drawing.</summary>
    public void Update()
    {
        if (BlockInput) _input.BlockInputRegion(Viewport);

        if (_input.TryGetPinch(out Pinch pinch))
        {
            _pinch.Apply(_camera, pinch, CameraViewport, EnablePan, EnableZoom, MinZoom, MaxZoom);
        }
        else
        {
            _pinch.Reset();

            if (EnablePan)
            {
                _camera.PanByScreenDelta(_input.GetDragDelta(Viewport));

                int scroll = _input.GetScrollIn(Viewport);
                if (scroll != 0)
                    _camera.Position += new Vector2(0f, -scroll * ScrollPanSpeed / _camera.Zoom);
            }
        }

        Clamp();
    }

    /// <summary>The current pointer position in world coordinates (for hover highlighting).</summary>
    public Vector2 PointerWorld => ScreenToWorld(_input.PointerPosition);

    /// <summary>
    /// True on the frame the viewport was tapped (press-origin and release both inside it). Returns the
    /// press and release world points so the caller can hit-test both and require the same target; a pan
    /// that ends inside returns true too, but its press/release world points differ so the check rejects it.
    /// </summary>
    public bool TryGetTap(out Vector2 pressWorld, out Vector2 releaseWorld) =>
        CameraGestures.TryGetTap(_input, _camera, CameraViewport, out pressWorld, out releaseWorld);

    /// <summary>
    /// Scissor-clips to the viewport and invokes <paramref name="drawWorld"/> with a SpriteBatch whose
    /// transform maps world coordinates -> virtual screen -> physical pixels. Pass <c>vr.Scale</c> and
    /// <c>vr.ScaleMatrix</c> for <paramref name="renderScale"/> / <paramref name="scaleMatrix"/>.
    /// </summary>
    public void Draw(SpriteBatch sb, GraphicsDevice gd, float renderScale, Matrix scaleMatrix, Action drawWorld)
    {
        _scissorRasterizer ??= new RasterizerState { ScissorTestEnable = true };

        gd.ScissorRectangle = new Rectangle(
            (int)(Viewport.X * renderScale),
            (int)(Viewport.Y * renderScale),
            Math.Max(0, (int)(Viewport.Width * renderScale)),
            Math.Max(0, (int)(Viewport.Height * renderScale)));

        Matrix world = _camera.GetViewMatrix(CameraViewport);

        sb.Begin(samplerState: SamplerState.PointClamp,
                 rasterizerState: _scissorRasterizer,
                 transformMatrix: world * scaleMatrix);
        drawWorld();
        sb.End();
    }

    private Rectangle PaddedBounds => new(
        ContentBounds.X - Padding, ContentBounds.Y - Padding,
        ContentBounds.Width + Padding * 2, ContentBounds.Height + Padding * 2);

    private void Clamp() =>
        _camera.Position = _camera.ClampPosition(_camera.Position, PaddedBounds, CameraViewport);
}
```

- [ ] **Step 4: Run PannableCanvasTests - must stay byte-identical green**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PannableCanvasTests"`
Expected: PASS (all). The exact-equality tests (`ScreenWorldRoundTrips`, drag/clamp/wheel offsets, tap, pointer) must pass.

- [ ] **Step 5 (only if Step 4's `ScreenWorldRoundTrips` or tap/pointer exact-equality tests FAIL): apply the direct-affine ScreenToWorld fallback**

If `Matrix.Invert` introduces float error that breaks an exact `Assert.Equal`, replace `ScreenToWorld` and add the helper (keeps WorldToScreen on the matrix, derives the inverse directly from Position/Zoom - bit-identical to the original formula):

```csharp
    private Vector2 ViewportCenter =>
        new(Viewport.X + Viewport.Width / 2f, Viewport.Y + Viewport.Height / 2f);

    /// <summary>Maps a virtual screen point back to world coordinates (exact inverse of <see cref="WorldToScreen"/>).</summary>
    public Vector2 ScreenToWorld(Vector2 screen)
    {
        Vector2 c = ViewportCenter;
        float z = _camera.Zoom;
        return new Vector2((screen.X - c.X) / z + _camera.Position.X,
                           (screen.Y - c.Y) / z + _camera.Position.Y);
    }
```

Re-run Step 4; expected PASS. (If Step 4 already passed, skip this step - do not add the fallback.)

- [ ] **Step 6: Run the FULL suite (catch any cross-package fallout)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all).

- [ ] **Step 7: Commit**

```bash
git add KhaozEngine.UI/KhaozEngine.UI.csproj KhaozEngine.UI/PannableCanvas.cs
git commit -m "refactor(UI): PannableCanvas delegates pan/clamp/tap to a backing Camera2D"
```

---

## Task 6: PannableCanvas pinch-zoom (new behavior + tests)

The Update rewrite in Task 5 already wired the pinch branch. This task locks the new behavior with tests.

**Files:**
- Test: `KhaozEngine.Tests/PannableCanvasTests.cs`

- [ ] **Step 1: Write the failing tests**

Add to `KhaozEngine.Tests/PannableCanvasTests.cs`:

```csharp
    private const float Tol = 1e-2f;

    private static RawInputState Touches2(Vector2 a, Vector2 b) =>
        new(Point.Zero, false, false, false, 0, new KeyboardState(), NoPads,
            new[] { new TouchPoint(a, TouchLocationState.Moved, 1), new TouchPoint(b, TouchLocationState.Moved, 2) },
            Rectangle.Empty);

    [Fact]
    public void PinchZoomsAboutMidpoint()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);
        canvas.MaxZoom = 100f;

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();  // mid 100, dist 80
        im.Update(Touches2(new Vector2(20, 100), new Vector2(180, 100)), true); canvas.Update();  // mid 100, dist 160 -> 2x

        Assert.Equal(2f, canvas.Camera.Zoom, Tol);
    }

    [Fact]
    public void PinchTwoFingerDragPans()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);   // large content -> no clamp interference

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();  // mid 100, dist 80
        im.Update(Touches2(new Vector2(90, 100), new Vector2(170, 100)), true); canvas.Update();  // mid 130, dist 80

        Assert.Equal(1f, canvas.Camera.Zoom, Tol);              // distance unchanged -> no zoom
        Assert.True(Math.Abs(canvas.CameraOffset.X - 30f) < 0.01f, $"offset.X {canvas.CameraOffset.X}");  // mid +30, zoom 1
    }

    [Fact]
    public void PinchDoesNotZoomWhenZoomDisabled()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);
        canvas.EnableZoom = false;

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();
        im.Update(Touches2(new Vector2(20, 100), new Vector2(180, 100)), true); canvas.Update();  // spread

        Assert.Equal(1f, canvas.Camera.Zoom, Tol);
    }

    [Fact]
    public void TransformsStayZoomCorrectAfterPinch()
    {
        var im = new InputManager(isMobile: true);
        var canvas = MakeCanvas(im);
        canvas.MaxZoom = 100f;

        im.Update(Touches2(new Vector2(60, 100), new Vector2(140, 100)), true); canvas.Update();
        im.Update(Touches2(new Vector2(20, 100), new Vector2(180, 100)), true); canvas.Update();  // ~2x

        // Round-trip must still hold under non-unit zoom.
        foreach (var w in new[] { new Vector2(0, 0), new Vector2(33, -41) })
            Assert.True(Vector2.Distance(w, canvas.ScreenToWorld(canvas.WorldToScreen(w))) < 0.01f);
    }
```

- [ ] **Step 2: Run to verify they pass (the pinch wiring already exists from Task 5)**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~PannableCanvasTests"`
Expected: PASS (all, including the 4 new). If `PinchZoomsAboutMidpoint` fails on the zoom ratio, confirm `InputManager(isMobile: true)` produces a `Pinch` with `Scale` = newDist/oldDist (160/80 = 2). If the two existing mouse-only tests now fail, the pinch branch leaked into the mouse path - re-check `TryGetPinch` returns false for mouse input.

- [ ] **Step 3: Commit**

```bash
git add KhaozEngine.Tests/PannableCanvasTests.cs
git commit -m "test(UI): PannableCanvas pinch-zoom + two-finger pan + EnableZoom gate"
```

---

## Task 7: Release 3.8.0

**Files:**
- Modify: `Directory.Build.props` (Version 3.7.0 -> 3.8.0)
- Modify: `CHANGELOG.md` (newest-first entry)
- Modify: `docs/CONSUMERS.md` (engine-version line + UI->Graphics dep note)
- Modify: `KhaozEngine.UI/KhaozEngine.UI.csproj` (`<Description>` mentions delegation/pinch) - optional polish

- [ ] **Step 1: Full suite green before releasing**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all).

- [ ] **Step 2: Bump the version**

In `Directory.Build.props`, change `<Version>3.7.0</Version>` to `<Version>3.8.0</Version>`.

- [ ] **Step 3: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert directly under the `# Changelog` header block (newest-first, above `## KhaozEngine 3.7.0`):

```markdown
## KhaozEngine 3.8.0

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
  vertical pan. Mouse-only behavior is unchanged. Disable pinch with `EnableZoom = false`.
- `KhaozEngine.UI` now references `KhaozEngine.Graphics` (transitive package dependency added).
```

- [ ] **Step 4: Update CONSUMERS.md**

In `docs/CONSUMERS.md`, change the engine-version line to `3.8.0` and update its summary line. Find:

```
**Engine current version:** `3.7.0` (all packages share one version, set in `Directory.Build.props`).
```

Change `3.7.0` to `3.8.0`. Then add a one-line note after the existing 3.7.0 summary paragraph:

```
> 3.8.0 centralizes the pan/zoom/clamp/tap gesture math: `PannableCanvas` (UI) and `CameraController`
> (Graphics) both drive a `Camera2D` through shared `PinchGestureTracker` / `CameraGestures` helpers.
> `PannableCanvas` gains pinch zoom (`MinZoom`/`MaxZoom`/`EnableZoom`, `Camera` accessor). New
> `KhaozEngine.UI -> KhaozEngine.Graphics` package dependency. `Camera2D.GetViewMatrix` now honors
> viewport X/Y (only affects inset viewports). No consumer adopts yet.
```

- [ ] **Step 5: Pack to local-feed (cumulative - do NOT delete old versions)**

Run: `dotnet pack -c Release -o ./local-feed`
Expected: builds and writes `KhaozEngine.*.3.8.0.nupkg` into `local-feed/`. Confirm: `ls local-feed/ | grep 3.8.0` shows Input, Screens, UI, Ecs, Graphics, Audio, Content, Diagnostics, Effects, Localization, Persistence, Time, App at 3.8.0.

- [ ] **Step 6: Commit the release**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md KhaozEngine.UI/KhaozEngine.UI.csproj
git commit -m "release(3.8.0): shared Camera2D gesture core; PannableCanvas pinch zoom + delegation"
```

- [ ] **Step 7: Verify Hardpoint still builds against 3.8.0**

Hardpoint's map runs on PannableCanvas. Verify it compiles against the new package without committing a consumer bump here.

```bash
cd /Users/antonio/Hardpoint
grep -rn "KhaozEngine" --include=*.csproj .   # find the pinned KhaozEngine.* versions
```

Temporarily bump every `KhaozEngine.*` `<PackageReference>` `Version` in Hardpoint's csproj(s) to `3.8.0`, then:

```bash
dotnet restore --source /Users/antonio/KhaozEngine-pannable-core/local-feed --source https://api.nuget.org/v3/index.json
dotnet build -c Debug
```

Expected: build succeeds (PannableCanvas API is source-compatible; new members are additive). Note for the user: pinch zoom is now **default-on** for Hardpoint's map - it can opt out with `EnableZoom = false` on its `PannableCanvas`.

Then revert the Hardpoint csproj edits (consumer adoption is Hardpoint's own work, not this engine release):

```bash
git -C /Users/antonio/Hardpoint checkout -- .
```

- [ ] **Step 8: Tag (after user confirms finishing the branch)**

Tagging + pushing happens during branch finish (see handoff), not inline. The tag is `v3.8.0`.

---

## Self-Review notes (already applied)

- **Spec coverage:** Camera2D X/Y (Task 1), Camera2D ops (Task 2), shared helpers (Task 3), CameraController refactor (Task 4), PannableCanvas delegation (Task 5), pinch zoom + new props (Tasks 5-6), release + Hardpoint verify (Task 7). All spec sections mapped.
- **Type consistency:** `PinchGestureTracker.Apply(camera, pinch, viewport, enablePan, enableZoom, minZoom, maxZoom)` / `Reset()`; `CameraGestures.TryGetTap(input, camera, viewport, out pressWorld, out releaseWorld)`; `Camera2D.PanByScreenDelta(screenDelta)`; `Camera2D.ZoomAboutScreenPoint(targetZoom, focusScreen, viewport, minZoom, maxZoom)` - identical across Tasks 2-6.
- **Exactness:** primary path delegates ScreenToWorld to the Camera2D matrix; Task 5 Step 5 is the direct-affine fallback, applied only if an exact-equality test regresses.
```
