# Camera Multi-Target Framing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add multi-target (co-op) camera framing to 5.x `KhaozEngine.Render2D`: a `GroupCamera` driver that eases a `Camera2D`'s position and zoom to keep N targets framed, on top of a pure `CameraFraming` helper.

**Architecture:** `CameraFraming` (pure static) computes the targets' padded bounding box and the position+zoom that frames it in the viewport. `GroupCamera` (stateful) asks `CameraFraming` for the desired position+zoom each frame and eases the camera toward it (frame-rate-independent), then clamps to world bounds. Base `Camera2D` is untouched.

**Tech Stack:** C# / net10.0, `System.Numerics` (`Vector2`), `KhaozEngine.Windowing.Rect`, xUnit. Headless, no `GraphicsDevice`.

**Spec:** `docs/superpowers/specs/2026-06-18-camera-multi-target-framing-design.md`

---

## File Structure

- `KhaozEngine.Render2D/CameraFraming.cs` (new) — pure static: `Bounds` + `Solve`.
- `KhaozEngine.Render2D/GroupCamera.cs` (new) — stateful driver.
- `KhaozEngine.Tests/Render2DGroupCameraTests.cs` (new) — headless coverage.
- Release files (Task 3): `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

Reference facts:
- `Camera2D` (`KhaozEngine.Render2D/Camera2D.cs`): public fields `Vector2 Position`, `float Zoom` (default 1); `Vector2 WorldToScreen(Vector2 world, int vw, int vh)`; `Vector2 ClampPosition(Vector2 desired, Rect worldBounds, int vw, int vh)`.
- `Rect` (`KhaozEngine.Windowing`): `readonly record struct Rect(float X, float Y, float Width, float Height)` with `Right`, `Bottom`, `Contains`.
- `KhaozEngine.Render2D` already references `KhaozEngine.Windowing` and has `InternalsVisibleTo("KhaozEngine.Tests")`.
- Run all `dotnet` commands from the worktree root.

---

## Task 1: `CameraFraming` pure helper

**Files:**
- Create: `KhaozEngine.Render2D/CameraFraming.cs`
- Test: `KhaozEngine.Tests/Render2DGroupCameraTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render2DGroupCameraTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for multi-target framing (CameraFraming + GroupCamera).</summary>
public class Render2DGroupCameraTests
{
    private const int Vw = 800, Vh = 600;
    private const float Tol = 1e-2f;
    private static readonly Rect Unbounded = new(-1_000_000f, -1_000_000f, 2_000_000f, 2_000_000f);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    private static void AssertInViewport(Camera2D cam, Vector2 world)
    {
        var s = cam.WorldToScreen(world, Vw, Vh);
        Assert.True(s.X is >= 0f and <= Vw && s.Y is >= 0f and <= Vh, $"world {world} -> screen {s} outside viewport");
    }

    // ---- CameraFraming ----

    [Fact]
    public void Bounds_IsTightAabbWithNoPadding()
    {
        var pts = new[] { new Vector2(0f, 0f), new Vector2(100f, 40f) };
        var b = CameraFraming.Bounds(pts, 0f, Vector2.Zero);
        Assert.Equal(0f, b.X, Tol);
        Assert.Equal(0f, b.Y, Tol);
        Assert.Equal(100f, b.Width, Tol);
        Assert.Equal(40f, b.Height, Tol);
    }

    [Fact]
    public void Bounds_PaddingExpandsSymmetrically()
    {
        var pts = new[] { new Vector2(0f, 0f), new Vector2(100f, 40f) };
        var b = CameraFraming.Bounds(pts, 0.1f, Vector2.Zero);   // w*1.2=120, h*1.2=48, center (50,20)
        Assert.Equal(-10f, b.X, Tol);
        Assert.Equal(-4f, b.Y, Tol);
        Assert.Equal(120f, b.Width, Tol);
        Assert.Equal(48f, b.Height, Tol);
    }

    [Fact]
    public void Bounds_MinViewSizeFloorsClusteredPoints()
    {
        var pts = new[] { new Vector2(5f, 5f), new Vector2(5f, 5f) };   // zero-extent cluster
        var b = CameraFraming.Bounds(pts, 0f, new Vector2(10f, 10f));   // floored to 10x10 centered on (5,5)
        Assert.Equal(0f, b.X, Tol);
        Assert.Equal(0f, b.Y, Tol);
        Assert.Equal(10f, b.Width, Tol);
        Assert.Equal(10f, b.Height, Tol);
    }

    [Fact]
    public void Solve_CentersPositionAndContainFits()
    {
        var (pos, zoom) = CameraFraming.Solve(new Rect(0f, 0f, 200f, 100f), Vw, Vh, 0.0001f, float.MaxValue);
        AssertClose(new Vector2(100f, 50f), pos);
        Assert.Equal(4f, zoom, Tol);   // min(800/200, 600/100) = min(4,6) = 4
    }

    [Fact]
    public void Solve_ClampsZoomToMax()
    {
        var (_, zoom) = CameraFraming.Solve(new Rect(0f, 0f, 200f, 100f), Vw, Vh, 0.0001f, 2f);
        Assert.Equal(2f, zoom, Tol);
    }

    [Fact]
    public void Solve_ZeroSizeBoundsDoesNotDivideByZero()
    {
        var (pos, zoom) = CameraFraming.Solve(new Rect(0f, 0f, 0f, 0f), Vw, Vh, 0.0001f, 100f);
        AssertClose(Vector2.Zero, pos);
        Assert.Equal(100f, zoom, Tol);   // huge fit, clamped to maxZoom; no NaN/Infinity
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DGroupCameraTests.Bounds_|FullyQualifiedName~Render2DGroupCameraTests.Solve_"`
Expected: FAIL — `CameraFraming` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/CameraFraming.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Pure framing math for multi-target cameras: the padded bounding box of a set of world points, and the
    /// camera position + zoom that frames a box in a viewport. No state, no easing - <see cref="GroupCamera"/>
    /// layers the smoothing on top. Headless, <see cref="System.Numerics"/> only.
    /// </summary>
    public static class CameraFraming
    {
        private const float Epsilon = 1e-4f;

        /// <summary>
        /// Axis-aligned bounding box of <paramref name="targets"/>, expanded on each side by
        /// <paramref name="paddingFraction"/> of the extent, then grown (about its center) to at least
        /// <paramref name="minViewSize"/> per axis. Throws if <paramref name="targets"/> is empty.
        /// </summary>
        public static Rect Bounds(IReadOnlyList<Vector2> targets, float paddingFraction, Vector2 minViewSize)
        {
            if (targets == null || targets.Count == 0)
                throw new ArgumentException("targets must be non-empty", nameof(targets));

            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            for (int i = 0; i < targets.Count; i++)
            {
                Vector2 p = targets[i];
                if (p.X < minX) minX = p.X;
                if (p.Y < minY) minY = p.Y;
                if (p.X > maxX) maxX = p.X;
                if (p.Y > maxY) maxY = p.Y;
            }

            float cx = (minX + maxX) * 0.5f;
            float cy = (minY + maxY) * 0.5f;
            float w = (maxX - minX) * (1f + 2f * paddingFraction);
            float h = (maxY - minY) * (1f + 2f * paddingFraction);

            w = MathF.Max(w, MathF.Max(minViewSize.X, Epsilon));
            h = MathF.Max(h, MathF.Max(minViewSize.Y, Epsilon));

            return new Rect(cx - w * 0.5f, cy - h * 0.5f, w, h);
        }

        /// <summary>
        /// Position (the box center) and zoom (contain-fit <c>min(vw / width, vh / height)</c>, clamped to
        /// <paramref name="minZoom"/>/<paramref name="maxZoom"/>) that frames <paramref name="bounds"/>.
        /// Box dimensions are floored to a tiny epsilon so a zero-size box never divides by zero.
        /// </summary>
        public static (Vector2 Position, float Zoom) Solve(Rect bounds, int vw, int vh, float minZoom, float maxZoom)
        {
            float w = MathF.Max(bounds.Width, Epsilon);
            float h = MathF.Max(bounds.Height, Epsilon);
            float fit = MathF.Min(vw / w, vh / h);
            float zoom = Math.Clamp(fit, minZoom, maxZoom);
            var pos = new Vector2(bounds.X + bounds.Width * 0.5f, bounds.Y + bounds.Height * 0.5f);
            return (pos, zoom);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DGroupCameraTests.Bounds_|FullyQualifiedName~Render2DGroupCameraTests.Solve_"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/CameraFraming.cs KhaozEngine.Tests/Render2DGroupCameraTests.cs
git commit -m "feat(render2d): CameraFraming — padded bounding box + contain-fit solve for N targets"
```

---

## Task 2: `GroupCamera` driver

**Files:**
- Create: `KhaozEngine.Render2D/GroupCamera.cs`
- Test: `KhaozEngine.Tests/Render2DGroupCameraTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append these methods inside the `Render2DGroupCameraTests` class (before the closing brace):

```csharp
    // ---- GroupCamera ----

    [Fact]
    public void Group_WarpFramesTwoTargetsInsideViewport()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam);
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 0f) };

        group.Warp(targets, Vw, Vh, Unbounded);

        AssertInViewport(cam, targets[0]);
        AssertInViewport(cam, targets[1]);
    }

    [Fact]
    public void Group_WarpCentersAndZoomsLikeSolve()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 100f) };

        group.Warp(targets, Vw, Vh, Unbounded);

        // bounds (0,0,200,100) -> center (100,50), zoom min(800/200,600/100)=4
        AssertClose(new Vector2(100f, 50f), cam.Position);
        Assert.Equal(4f, cam.Zoom, Tol);
    }

    [Fact]
    public void Group_UpdateEasesTowardFramingAndConverges()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 100f) };

        for (int i = 0; i < 200; i++)
            group.Update(targets, 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(100f, 50f), cam.Position);
        Assert.Equal(4f, cam.Zoom, Tol);
    }

    [Fact]
    public void Group_NonPositiveStiffnessSnapsToFraming()
    {
        var camSnap = new Camera2D();
        var snap = new GroupCamera(camSnap) { Stiffness = 0f, ZoomStiffness = 0f };
        var camWarp = new Camera2D();
        var warp = new GroupCamera(camWarp);
        var targets = new[] { new Vector2(10f, 20f), new Vector2(210f, 120f) };

        snap.Update(targets, 0.016f, Vw, Vh, Unbounded);
        warp.Warp(targets, Vw, Vh, Unbounded);

        AssertClose(camWarp.Position, camSnap.Position);
        Assert.Equal(camWarp.Zoom, camSnap.Zoom, Tol);
    }

    [Fact]
    public void Group_SeparatingTargetsZoomsOut()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };

        var close = new[] { new Vector2(0f, 0f), new Vector2(50f, 0f) };
        group.Warp(close, Vw, Vh, Unbounded);
        float zoomClose = cam.Zoom;

        var spread = new[] { new Vector2(0f, 0f), new Vector2(400f, 0f) };
        for (int i = 0; i < 200; i++)
            group.Update(spread, 0.1f, Vw, Vh, Unbounded);

        Assert.True(cam.Zoom < zoomClose, $"expected zoom-out: spread {cam.Zoom} < close {zoomClose}");
    }

    [Fact]
    public void Group_FrameRateIndependent()
    {
        var targets = new[] { new Vector2(0f, 0f), new Vector2(200f, 100f) };

        var camOne = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var groupOne = new GroupCamera(camOne) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        groupOne.Update(targets, 0.2f, Vw, Vh, Unbounded);

        var camTwo = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var groupTwo = new GroupCamera(camTwo) { PaddingFraction = 0f, MinViewSize = Vector2.Zero };
        groupTwo.Update(targets, 0.1f, Vw, Vh, Unbounded);
        groupTwo.Update(targets, 0.1f, Vw, Vh, Unbounded);

        AssertClose(camOne.Position, camTwo.Position, 0.1f);
        Assert.Equal(camOne.Zoom, camTwo.Zoom, 0.05f);
    }

    [Fact]
    public void Group_EmptyTargetsHoldsView()
    {
        var cam = new Camera2D { Position = new Vector2(5f, 5f), Zoom = 2f };
        var group = new GroupCamera(cam);

        group.Update(Array.Empty<Vector2>(), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(5f, 5f), cam.Position);
        Assert.Equal(2f, cam.Zoom, Tol);
    }

    [Fact]
    public void Group_WarpClampsPositionToWorldBounds()
    {
        var cam = new Camera2D();
        var group = new GroupCamera(cam) { PaddingFraction = 0f, MinViewSize = new Vector2(100f, 100f) };
        // single point at origin -> bounds (-50,-50,100,100) -> zoom min(800/100,600/100)=6, center (0,0).
        var targets = new[] { Vector2.Zero };
        var bounds = new Rect(0f, 0f, 1000f, 1000f);   // halfW 800/(2*6)=66.67, halfH 600/(2*6)=50

        group.Warp(targets, Vw, Vh, bounds);

        Assert.Equal(6f, cam.Zoom, Tol);
        Assert.Equal(66.6667f, cam.Position.X, 1e-1f);   // clamped to worldBounds.X + halfW
        Assert.Equal(50f, cam.Position.Y, 1e-1f);        // clamped to worldBounds.Y + halfH
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DGroupCameraTests.Group_"`
Expected: FAIL — `GroupCamera` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/GroupCamera.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Drives a <see cref="Camera2D"/> to frame multiple targets (co-op / shared screen): each frame it
    /// computes the targets' padded bounding box via <see cref="CameraFraming"/> and eases the camera's
    /// position and zoom toward the framing (frame-rate-independent), then clamps to world bounds. The game
    /// supplies the target positions; this owns the framing + smoothing. Headless, no GPU.
    /// </summary>
    public sealed class GroupCamera
    {
        private readonly Camera2D _camera;

        /// <summary>Creates a group camera driving the given camera.</summary>
        public GroupCamera(Camera2D camera) => _camera = camera;

        /// <summary>The camera this controller drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>Position smoothing rate (per second): <c>1 - exp(-Stiffness*dt)</c> per frame. <c>&lt;= 0</c>
        /// snaps position instantly.</summary>
        public float Stiffness { get; set; } = 8f;

        /// <summary>Zoom smoothing rate (per second), separate from <see cref="Stiffness"/> so zoom can lag or
        /// lead position. <c>&lt;= 0</c> snaps zoom instantly.</summary>
        public float ZoomStiffness { get; set; } = 8f;

        /// <summary>Margin around the targets, as a fraction of their extent added on each side.</summary>
        public float PaddingFraction { get; set; } = 0.15f;

        /// <summary>Floor on the framed box extent (world units, per axis): keeps zoom sane when targets cluster
        /// (a single target frames a box of this size rather than fitting a zero-area point).</summary>
        public Vector2 MinViewSize { get; set; } = new(1f, 1f);

        /// <summary>Lower zoom clamp.</summary>
        public float MinZoom { get; set; } = 0.0001f;

        /// <summary>Upper zoom clamp.</summary>
        public float MaxZoom { get; set; } = float.MaxValue;

        /// <summary>Eases the camera toward the framing of <paramref name="targets"/>, then clamps position to
        /// <paramref name="worldBounds"/>. Empty <paramref name="targets"/> holds the current view.</summary>
        public void Update(IReadOnlyList<Vector2> targets, float dt, int viewportWidth, int viewportHeight, Rect worldBounds)
        {
            if (targets == null || targets.Count == 0) return;

            var (desiredPos, desiredZoom) = SolveFor(targets, viewportWidth, viewportHeight);

            _camera.Zoom = Ease(_camera.Zoom, desiredZoom, ZoomStiffness, dt);
            _camera.Position = new Vector2(
                Ease(_camera.Position.X, desiredPos.X, Stiffness, dt),
                Ease(_camera.Position.Y, desiredPos.Y, Stiffness, dt));

            _camera.Position = _camera.ClampPosition(_camera.Position, worldBounds, viewportWidth, viewportHeight);
        }

        /// <summary>Snaps the camera directly to the framing of <paramref name="targets"/> (no easing), then
        /// clamps position to <paramref name="worldBounds"/>. Empty <paramref name="targets"/> is a no-op.</summary>
        public void Warp(IReadOnlyList<Vector2> targets, int viewportWidth, int viewportHeight, Rect worldBounds)
        {
            if (targets == null || targets.Count == 0) return;

            var (desiredPos, desiredZoom) = SolveFor(targets, viewportWidth, viewportHeight);
            _camera.Zoom = desiredZoom;
            _camera.Position = desiredPos;
            _camera.Position = _camera.ClampPosition(_camera.Position, worldBounds, viewportWidth, viewportHeight);
        }

        private (Vector2 Position, float Zoom) SolveFor(IReadOnlyList<Vector2> targets, int vw, int vh)
        {
            var bounds = CameraFraming.Bounds(targets, PaddingFraction, MinViewSize);
            return CameraFraming.Solve(bounds, vw, vh, MinZoom, MaxZoom);
        }

        private static float Ease(float current, float desired, float rate, float dt)
            => rate <= 0f || dt <= 0f ? desired : current + (desired - current) * (1f - MathF.Exp(-rate * dt));
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DGroupCameraTests.Group_"`
Expected: PASS (8 tests). Then the whole class:
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DGroupCameraTests"`
Expected: PASS (14 tests).

- [ ] **Step 5: Run the full suite for regressions**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (prior 1220 + 14 new = 1234, 6 GPU-skipped).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render2D/GroupCamera.cs KhaozEngine.Tests/Render2DGroupCameraTests.cs
git commit -m "feat(render2d): GroupCamera — eased multi-target framing (position + zoom) over Camera2D"
```

---

## Task 3: Release ritual (5.53.0)

Additive change → minor bump. Follows `KhaozEngine/CLAUDE.md` release order.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the 5.x version**

In `Directory.Build.props`, change `<KhaozEngineVersion>5.52.0</KhaozEngineVersion>` to `5.53.0`.

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert directly above the `## 5.52.0 (custom 5.x line)` heading:

```markdown
## 5.53.0 (custom 5.x line)

Multi-target (co-op / shared-screen) camera framing on the 5.x engine, the next slice of the camera feel
layer.

- **`GroupCamera`** (`KhaozEngine.Render2D`) - drives a `Camera2D` to keep N targets framed: each frame it
  takes the targets' padded bounding box and eases position and zoom toward the contain-fit framing
  (frame-rate-independent, separate `Stiffness` / `ZoomStiffness`), then clamps to world bounds. `PaddingFraction`
  sets the margin; `MinViewSize` floors the framed extent so a clustered or single target does not zoom to the
  max. `Warp(targets, ...)` snaps instantly; an empty target list holds the view.
- **`CameraFraming`** (`KhaozEngine.Render2D`) - the pure framing math underneath: `Bounds(targets,
  paddingFraction, minViewSize)` for the padded AABB and `Solve(bounds, vw, vh, minZoom, maxZoom)` for the
  position + contain-fit zoom. Headless, no easing - usable standalone.
```

- [ ] **Step 3: Update the three guard-checked doc declarations**

In `docs/CONSUMERS.md`, change the `**Engine current version:** \`5.52.0\`` line to `5.53.0`.
In `docs/ROADMAP.md`, change the `Current released version: **5.52.0**` line (near the top) to `5.53.0`.
In `README.md`, change every `Version="5.52.0"` in the `<PackageReference>` example block to `5.53.0` (grep `grep -n "5.52.0" README.md` first to find all ~4 lines).

- [ ] **Step 4: Update the ROADMAP camera section**

In `docs/ROADMAP.md`, in the "Camera: first-class follow / scroller camera" section:

(a) Under the `**Shipped:**` list, append:

```markdown
- 5.53.0: `GroupCamera` + `CameraFraming` (`KhaozEngine.Render2D`) - **multi-target framing** for co-op /
  shared screen: eases position + zoom to fit N targets (padded bounding box, contain-fit zoom, per-axis
  `MinViewSize` floor, world-bounds clamp, instant `Warp`).
```

(b) From the `**Still open**` list, delete the bullet beginning `- Multi-target framing (auto position + zoom to fit N targets), for co-op / shared screen.` Leave the other still-open bullets (room/region cameras, eased zoom transitions, parallax, screen shake) intact.

- [ ] **Step 5: Verify the doc-version guard passes**

Run: `bash scripts/check-doc-versions.sh`
Expected: exit 0 (declarations match `<KhaozEngineVersion>` = 5.53.0).

- [ ] **Step 6: Test and pack**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green).
Run: `dotnet pack -c Release -o ./local-feed`
Expected: builds `KhaozEngine.Render2D.5.53.0.nupkg` (and the other 5.x packages) into `local-feed` (cumulative; do not delete old versions). Confirm with `ls local-feed/KhaozEngine.Render2D.5.53.0.nupkg`.

- [ ] **Step 7: Commit and tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render2d(5.53.0): multi-target framing — GroupCamera + CameraFraming"
git tag v5.53.0
```

Pushing `main` + the tag happens at branch-finish time, not here.

---

## Self-Review Notes

- **Spec coverage:** `CameraFraming.Bounds`/`Solve` (Task 1), `GroupCamera` driver with ease + clamp + Warp + empty-hold (Task 2), all spec test-matrix items mapped (bounds/padding/min-size, solve center+fit+clamp+zero-size, two-target framing, separating zoom-out, frame-rate independence, world clamp, empty hold, snap), release incl. ROADMAP edits (Task 3). Covered.
- **Type consistency:** `CameraFraming.Bounds(IReadOnlyList<Vector2>, float, Vector2) -> Rect`, `CameraFraming.Solve(Rect, int, int, float, float) -> (Vector2, float)`; `GroupCamera` props `Stiffness`/`ZoomStiffness`/`PaddingFraction`/`MinViewSize`/`MinZoom`/`MaxZoom`, methods `Update(IReadOnlyList<Vector2>, float, int, int, Rect)` + `Warp(IReadOnlyList<Vector2>, int, int, Rect)`, used consistently across tasks. `Camera2D.ClampPosition`/`WorldToScreen` signatures match the real ones.
- **No placeholders:** every code step shows complete code; commands have expected output.
