# Camera Room/Region Cameras Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add Metroidvania-style room/region cameras to 5.x `KhaozEngine.Render2D`: a turnkey `RoomCamera` that follows a target confined to the region it is in and eases (blends) to reframe when the target crosses into a new region.

**Architecture:** `CameraRoom` is a region value (world rect + optional zoom). `RoomCamera` owns the `Camera2D` plus an internal `CameraFollow` (in-room feel) and `CameraBlend` (region hand-off); each frame it resolves the active region, blends on a change, otherwise follows within the region. A small additive `Camera2D.ClampPosition` overload takes an explicit zoom (the hand-off clamps at the target room's zoom). Base `Camera2D` is otherwise untouched.

**Tech Stack:** C# / net10.0, `System.Numerics` (`Vector2`), `KhaozEngine.Windowing.Rect`, xUnit. Headless, no `GraphicsDevice`.

**Spec:** `docs/superpowers/specs/2026-06-18-camera-room-regions-design.md`

---

## File Structure

- `KhaozEngine.Render2D/Camera2D.cs` (modify) — add the explicit-zoom `ClampPosition` overload; existing method delegates.
- `KhaozEngine.Render2D/CameraRoom.cs` (new) — region value (Bounds + optional Zoom + Contains).
- `KhaozEngine.Render2D/RoomCamera.cs` (new) — turnkey controller.
- `KhaozEngine.Tests/Render2DRoomCameraTests.cs` (new) — headless coverage (one file, sections per unit).
- Release files (Task 4): `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

Reference facts:
- `Camera2D` (`KhaozEngine.Render2D/Camera2D.cs`): public fields `Vector2 Position`, `float Zoom`, `float Rotation`; `Vector2 WorldToScreen(Vector2, int, int)`. Its `ClampPosition(desired, Rect, vw, vh)` currently computes `halfW/halfH` from `this.Zoom`.
- `CameraFollow` (already shipped): `CameraFollow(Camera2D)`, `Vector2 Stiffness`, `SetStiffness(float)`, `Rect? Deadzone`, `Update(target, velocity, dt, vw, vh, Rect worldBounds)`, `Warp(Vector2)`.
- `CameraBlend` (already shipped): `CameraBlend(Camera2D)`, `To(CameraState, float duration, Func<float,float>? easing)`, `Update(dt)`, `bool IsBlending`.
- `CameraState` (already shipped): `CameraState(Vector2 position, float zoom, float rotation)`.
- `Easing.SmoothStep` (already shipped).
- `Rect` (`KhaozEngine.Windowing`): `readonly record struct Rect(float X, float Y, float Width, float Height)` with `Right`, `Bottom`, `Contains` (left/top inclusive, right/bottom exclusive).
- `KhaozEngine.Render2D` has `InternalsVisibleTo("KhaozEngine.Tests")`.
- Run all `dotnet` commands from the worktree root.

---

## Task 1: `Camera2D.ClampPosition` explicit-zoom overload

**Files:**
- Modify: `KhaozEngine.Render2D/Camera2D.cs`
- Test: `KhaozEngine.Tests/Render2DRoomCameraTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render2DRoomCameraTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for room/region cameras (Camera2D zoom-clamp overload, CameraRoom, RoomCamera).</summary>
public class Render2DRoomCameraTests
{
    private const int Vw = 800, Vh = 600;
    private const float Tol = 1e-2f;

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    // ---- Camera2D explicit-zoom ClampPosition overload ----

    [Fact]
    public void Clamp_ExplicitZoomMatchesInstanceZoom()
    {
        var cam = new Camera2D { Zoom = 2f };
        var bounds = new Rect(0f, 0f, 2000f, 1000f);
        var desired = new Vector2(50f, 500f);

        var viaField = cam.ClampPosition(desired, bounds, Vw, Vh);
        var viaArg = cam.ClampPosition(desired, bounds, Vw, Vh, 2f);

        AssertClose(viaField, viaArg);
    }

    [Fact]
    public void Clamp_HigherZoomAllowsPositionNearerEdge()
    {
        var cam = new Camera2D();
        var bounds = new Rect(0f, 0f, 2000f, 1000f);
        var desired = new Vector2(50f, 500f);   // near the left edge

        var atZoom1 = cam.ClampPosition(desired, bounds, Vw, Vh, 1f);   // halfW 400 -> x clamps to 400
        var atZoom2 = cam.ClampPosition(desired, bounds, Vw, Vh, 2f);   // halfW 200 -> x clamps to 200

        Assert.Equal(400f, atZoom1.X, Tol);
        Assert.Equal(200f, atZoom2.X, Tol);
        Assert.True(atZoom2.X < atZoom1.X, "higher zoom should sit nearer the bound edge");
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DRoomCameraTests.Clamp_"`
Expected: FAIL — the 5-arg `ClampPosition` overload does not exist (compile error).

- [ ] **Step 3: Add the overload and delegate**

In `KhaozEngine.Render2D/Camera2D.cs`, replace the entire existing `ClampPosition` method (the one with signature `ClampPosition(Vector2 desired, Rect worldBounds, int viewportWidth, int viewportHeight)`) with these two methods:

```csharp
        /// <summary>
        /// Returns <paramref name="desired"/> clamped so the visible world rectangle
        /// (viewport size divided by <see cref="Zoom"/>) stays inside <paramref name="worldBounds"/>.
        /// On an axis where the world is smaller than the view, the result is centred on that axis. Does
        /// not mutate <see cref="Position"/>; the caller assigns the result if wanted. Ignores
        /// <see cref="Rotation"/> (exact when it is 0); requires <see cref="Zoom"/> &gt; 0.
        /// </summary>
        public Vector2 ClampPosition(Vector2 desired, Rect worldBounds, int viewportWidth, int viewportHeight)
            => ClampPosition(desired, worldBounds, viewportWidth, viewportHeight, Zoom);

        /// <summary>
        /// As <see cref="ClampPosition(Vector2, Rect, int, int)"/> but clamps for an explicit
        /// <paramref name="zoom"/> instead of <see cref="Zoom"/>. Used when framing for a zoom the camera has
        /// not eased to yet (e.g. a room hand-off targeting the next room's zoom). Requires <paramref name="zoom"/> &gt; 0.
        /// </summary>
        public Vector2 ClampPosition(Vector2 desired, Rect worldBounds, int viewportWidth, int viewportHeight, float zoom)
        {
            float halfW = viewportWidth / (2f * zoom);
            float halfH = viewportHeight / (2f * zoom);

            float x = worldBounds.Width >= 2f * halfW
                ? Math.Clamp(desired.X, worldBounds.X + halfW, worldBounds.Right - halfW)
                : worldBounds.X + worldBounds.Width / 2f;

            float y = worldBounds.Height >= 2f * halfH
                ? Math.Clamp(desired.Y, worldBounds.Y + halfH, worldBounds.Bottom - halfH)
                : worldBounds.Y + worldBounds.Height / 2f;

            return new Vector2(x, y);
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DRoomCameraTests.Clamp_"`
Expected: PASS (2 tests).

- [ ] **Step 5: Run existing Camera-related tests for regressions**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests|FullyQualifiedName~Render2DGroupCameraTests"`
Expected: PASS (the existing 4-arg `ClampPosition` callers still work via the delegate).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render2D/Camera2D.cs KhaozEngine.Tests/Render2DRoomCameraTests.cs
git commit -m "feat(render2d): Camera2D.ClampPosition overload taking an explicit zoom"
```

---

## Task 2: `CameraRoom` region value

**Files:**
- Create: `KhaozEngine.Render2D/CameraRoom.cs`
- Test: `KhaozEngine.Tests/Render2DRoomCameraTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append these methods inside the `Render2DRoomCameraTests` class (before the closing brace):

```csharp
    // ---- CameraRoom ----

    [Fact]
    public void Room_ContainsRespectsBounds()
    {
        var room = new CameraRoom(new Rect(0f, 0f, 100f, 100f));
        Assert.True(room.Contains(new Vector2(50f, 50f)));
        Assert.False(room.Contains(new Vector2(150f, 50f)));
    }

    [Fact]
    public void Room_ZoomDefaultsToNull()
    {
        var noZoom = new CameraRoom(new Rect(0f, 0f, 100f, 100f));
        Assert.Null(noZoom.Zoom);

        var withZoom = new CameraRoom(new Rect(0f, 0f, 100f, 100f), 2f);
        Assert.Equal(2f, withZoom.Zoom);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DRoomCameraTests.Room_"`
Expected: FAIL — `CameraRoom` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/CameraRoom.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// A camera region: a world rectangle that is both the trigger area (the followed target must be inside
    /// for the room to be active) and the camera confinement (in-room follow clamps to it), plus an optional
    /// per-room zoom override. Used by <see cref="RoomCamera"/>.
    /// </summary>
    public readonly struct CameraRoom
    {
        /// <summary>The region rectangle, in world units.</summary>
        public readonly Rect Bounds;

        /// <summary>Optional zoom override applied on entry; <c>null</c> keeps the current zoom.</summary>
        public readonly float? Zoom;

        public CameraRoom(Rect bounds, float? zoom = null)
        {
            Bounds = bounds;
            Zoom = zoom;
        }

        /// <summary>True when <paramref name="worldPoint"/> is inside <see cref="Bounds"/>.</summary>
        public bool Contains(Vector2 worldPoint) => Bounds.Contains(worldPoint);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DRoomCameraTests.Room_"`
Expected: PASS (2 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/CameraRoom.cs KhaozEngine.Tests/Render2DRoomCameraTests.cs
git commit -m "feat(render2d): CameraRoom — region rect + optional zoom override"
```

---

## Task 3: `RoomCamera` controller

**Files:**
- Create: `KhaozEngine.Render2D/RoomCamera.cs`
- Test: `KhaozEngine.Tests/Render2DRoomCameraTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append these methods inside the `Render2DRoomCameraTests` class (before the closing brace):

```csharp
    // ---- RoomCamera ----

    // Two rooms side by side: A = [0,2000) x [0,1000) zoom 1; B = [2000,4000) x [0,1000) zoom 2.
    private static CameraRoom[] TwoRooms() => new[]
    {
        new CameraRoom(new Rect(0f, 0f, 2000f, 1000f), 1f),
        new CameraRoom(new Rect(2000f, 0f, 2000f, 1000f), 2f),
    };

    [Fact]
    public void Room_FirstUpdateAcquiresContainingRoomNoTransition()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms());

        rc.Update(new Vector2(500f, 500f), 0.016f, Vw, Vh);

        Assert.Equal(0, rc.ActiveRoomIndex);
        Assert.False(rc.IsTransitioning);
        Assert.Equal(1f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_CrossingIntoNewRoomTransitionsThenSettles()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms()) { BlendDuration = 0.4f };

        rc.Update(new Vector2(500f, 500f), 0.016f, Vw, Vh);   // acquire A
        Assert.Equal(0, rc.ActiveRoomIndex);

        var inB = new Vector2(2500f, 500f);
        rc.Update(inB, 0.016f, Vw, Vh);                       // cross into B -> begins hand-off
        Assert.Equal(1, rc.ActiveRoomIndex);
        Assert.True(rc.IsTransitioning);

        for (int i = 0; i < 10; i++) rc.Update(inB, 0.1f, Vw, Vh);   // 1.0s, past BlendDuration

        Assert.False(rc.IsTransitioning);
        Assert.Equal(2f, cam.Zoom, Tol);                      // B's zoom applied
        var s = cam.WorldToScreen(inB, Vw, Vh);
        Assert.True(s.X is >= 0f and <= Vw && s.Y is >= 0f and <= Vh, $"target {inB} -> screen {s} offscreen");
    }

    [Fact]
    public void Room_WarpClampsInRoomBounds()
    {
        var cam = new Camera2D();
        var rc = new RoomCamera(cam, TwoRooms());

        // Target near A's left edge; A zoom 1 -> halfW 400 -> X clamps to 400; halfH 300 -> Y in [300,700].
        rc.Warp(new Vector2(50f, 500f), Vw, Vh);

        Assert.Equal(0, rc.ActiveRoomIndex);
        Assert.False(rc.IsTransitioning);
        AssertClose(new Vector2(400f, 500f), cam.Position);
    }

    [Fact]
    public void Room_NullZoomKeepsCurrentZoom()
    {
        var cam = new Camera2D { Zoom = 3f };
        var rooms = new[] { new CameraRoom(new Rect(0f, 0f, 2000f, 1000f)) };   // null zoom
        var rc = new RoomCamera(cam, rooms);

        rc.Warp(new Vector2(1000f, 500f), Vw, Vh);

        Assert.Equal(3f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_TargetInNoRoomHoldsActiveRoom()
    {
        var cam = new Camera2D();
        var rooms = new[] { new CameraRoom(new Rect(0f, 0f, 2000f, 1000f), 1f) };
        var rc = new RoomCamera(cam, rooms);

        rc.Update(new Vector2(1000f, 500f), 0.016f, Vw, Vh);   // acquire room 0
        Assert.Equal(0, rc.ActiveRoomIndex);

        rc.Update(new Vector2(5000f, 5000f), 0.016f, Vw, Vh);  // outside every room
        Assert.Equal(0, rc.ActiveRoomIndex);                   // holds
    }

    [Fact]
    public void Room_OverlappingRoomsLowestIndexWins()
    {
        var cam = new Camera2D();
        var rooms = new[]
        {
            new CameraRoom(new Rect(0f, 0f, 3000f, 1000f), 1f),
            new CameraRoom(new Rect(1000f, 0f, 3000f, 1000f), 2f),
        };
        var rc = new RoomCamera(cam, rooms);

        rc.Warp(new Vector2(1500f, 500f), Vw, Vh);   // inside both -> room 0

        Assert.Equal(0, rc.ActiveRoomIndex);
        Assert.Equal(1f, cam.Zoom, Tol);
    }

    [Fact]
    public void Room_ExposedFollowStiffnessDrivesInRoomEase()
    {
        // One huge room so nothing clamps; compare a stiff vs a slack in-room follow after one step.
        var room = new[] { new CameraRoom(new Rect(-10000f, -10000f, 20000f, 20000f), 1f) };

        var camStiff = new Camera2D();
        var stiff = new RoomCamera(camStiff, room);
        stiff.Follow.SetStiffness(20f);
        stiff.Warp(new Vector2(0f, 0f), Vw, Vh);

        var camSlack = new Camera2D();
        var slack = new RoomCamera(camSlack, room);
        slack.Follow.SetStiffness(2f);
        slack.Warp(new Vector2(0f, 0f), Vw, Vh);

        stiff.Update(new Vector2(1000f, 0f), 0.1f, Vw, Vh);
        slack.Update(new Vector2(1000f, 0f), 0.1f, Vw, Vh);

        Assert.True(camStiff.Position.X > camSlack.Position.X,
            $"stiff {camStiff.Position.X} should lead slack {camSlack.Position.X}");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DRoomCameraTests.Room_FirstUpdate|FullyQualifiedName~Render2DRoomCameraTests.Room_Crossing|FullyQualifiedName~Render2DRoomCameraTests.Room_Warp|FullyQualifiedName~Render2DRoomCameraTests.Room_Null|FullyQualifiedName~Render2DRoomCameraTests.Room_Target|FullyQualifiedName~Render2DRoomCameraTests.Room_Overlapping|FullyQualifiedName~Render2DRoomCameraTests.Room_Exposed"`
Expected: FAIL — `RoomCamera` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/RoomCamera.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Turnkey Metroidvania-style room camera: follows a target confined to the region (<see cref="CameraRoom"/>)
    /// it is in, and eases (blends) to reframe when the target crosses into a new region, then resumes
    /// following. Composes an internal <see cref="CameraFollow"/> (in-room feel, exposed via <see cref="Follow"/>)
    /// and <see cref="CameraBlend"/> (region hand-off). Headless, no GPU.
    /// </summary>
    public sealed class RoomCamera
    {
        private readonly Camera2D _camera;
        private readonly IReadOnlyList<CameraRoom> _rooms;
        private readonly CameraFollow _follow;
        private readonly CameraBlend _blend;
        private int _activeIndex = -1;

        /// <summary>Creates a room camera over <paramref name="camera"/> with the given rooms (priority is list order).</summary>
        public RoomCamera(Camera2D camera, IReadOnlyList<CameraRoom> rooms)
        {
            _camera = camera;
            _rooms = rooms;
            _follow = new CameraFollow(camera);
            _blend = new CameraBlend(camera);
        }

        /// <summary>The camera this controller drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>The internal in-room follow - tune its Stiffness/Deadzone/LookAhead/Snap for the in-room feel.</summary>
        public CameraFollow Follow => _follow;

        /// <summary>Index of the active room, or -1 until the first room is acquired.</summary>
        public int ActiveRoomIndex => _activeIndex;

        /// <summary>True while a region hand-off blend is running.</summary>
        public bool IsTransitioning { get; private set; }

        /// <summary>Duration (seconds) of the hand-off blend on a region change.</summary>
        public float BlendDuration { get; set; } = 0.4f;

        /// <summary>Easing curve for the hand-off blend.</summary>
        public Func<float, float> BlendEasing { get; set; } = Easing.SmoothStep;

        /// <summary>
        /// Resolves the active room from <paramref name="target"/>, hands off (blends) on a region change,
        /// otherwise follows within the active room. <paramref name="velocity"/> drives the follow look-ahead.
        /// </summary>
        public void Update(Vector2 target, Vector2 velocity, float dt, int viewportWidth, int viewportHeight)
        {
            int resolved = Resolve(target);
            if (resolved < 0) return;   // no room contains the target and none is active yet

            if (resolved != _activeIndex)
            {
                if (_activeIndex < 0)
                    SnapTo(resolved, target, viewportWidth, viewportHeight);
                else
                    BeginHandoff(resolved, target, viewportWidth, viewportHeight);
            }

            if (IsTransitioning)
            {
                _blend.Update(dt);
                if (!_blend.IsBlending)
                {
                    _follow.Warp(_camera.Position);   // resume following from the blended frame
                    IsTransitioning = false;
                }
                return;
            }

            _follow.Update(target, velocity, dt, viewportWidth, viewportHeight, _rooms[_activeIndex].Bounds);
        }

        /// <summary>Convenience overload with zero velocity.</summary>
        public void Update(Vector2 target, float dt, int viewportWidth, int viewportHeight)
            => Update(target, Vector2.Zero, dt, viewportWidth, viewportHeight);

        /// <summary>Snaps instantly to the room containing <paramref name="target"/> (no blend); applies the
        /// room's zoom and positions the follow. No-op if no room contains the target.</summary>
        public void Warp(Vector2 target, int viewportWidth, int viewportHeight)
        {
            int resolved = Resolve(target);
            if (resolved < 0) return;
            SnapTo(resolved, target, viewportWidth, viewportHeight);
        }

        // Lowest-index room containing target; else the current active room; else -1.
        private int Resolve(Vector2 target)
        {
            for (int i = 0; i < _rooms.Count; i++)
                if (_rooms[i].Contains(target)) return i;
            return _activeIndex;
        }

        private void SnapTo(int index, Vector2 target, int vw, int vh)
        {
            CameraRoom room = _rooms[index];
            float zoom = room.Zoom ?? _camera.Zoom;
            _camera.Zoom = zoom;
            _follow.Warp(_camera.ClampPosition(target, room.Bounds, vw, vh, zoom));
            _activeIndex = index;
            IsTransitioning = false;
        }

        private void BeginHandoff(int index, Vector2 target, int vw, int vh)
        {
            CameraRoom room = _rooms[index];
            float zoom = room.Zoom ?? _camera.Zoom;
            Vector2 pos = _camera.ClampPosition(target, room.Bounds, vw, vh, zoom);
            _blend.To(new CameraState(pos, zoom, _camera.Rotation), BlendDuration, BlendEasing);
            _activeIndex = index;
            IsTransitioning = true;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DRoomCameraTests.Room_"`
Expected: PASS (9 tests: 2 CameraRoom + 7 RoomCamera). Then the whole class:
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DRoomCameraTests"`
Expected: PASS (11 tests: 2 Clamp + 2 Room value + 7 RoomCamera).

- [ ] **Step 5: Run the full suite for regressions**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (prior 1251 + 11 new = 1262, 6 GPU-skipped).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render2D/RoomCamera.cs KhaozEngine.Tests/Render2DRoomCameraTests.cs
git commit -m "feat(render2d): RoomCamera — Metroidvania region camera (follow + eased hand-off)"
```

---

## Task 4: Release ritual (5.55.0)

Additive change → minor bump. Follows `KhaozEngine/CLAUDE.md` release order.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the 5.x version**

In `Directory.Build.props`, change `<KhaozEngineVersion>5.54.0</KhaozEngineVersion>` to `5.55.0`.

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert directly above the `## 5.54.0 (custom 5.x line)` heading:

```markdown
## 5.55.0 (custom 5.x line)

Room / region cameras on the 5.x engine - Metroidvania-style per-area cameras, the next slice of the camera
feel layer (composing the follow + blend pieces already shipped).

- **`RoomCamera`** (`KhaozEngine.Render2D`) - a turnkey controller: `Update(target, velocity, dt, vw, vh)`
  follows the target confined to the region it is in (an internal `CameraFollow`, exposed via `Follow` for
  feel tuning) and eases (an internal `CameraBlend`) to reframe when the target crosses into a new region,
  then resumes following. `BlendDuration` / `BlendEasing` shape the hand-off; `ActiveRoomIndex` /
  `IsTransitioning` expose state; `Warp(target, vw, vh)` snaps to the target's room instantly.
- **`CameraRoom`** (`KhaozEngine.Render2D`) - a region: a world rect (both the trigger area and the camera
  confinement) plus an optional per-room zoom override (`null` keeps the current zoom). Overlaps resolve by
  list order; a target in no room holds the current room.
- **`Camera2D.ClampPosition`** gains an overload taking an explicit zoom, so a hand-off can clamp the framing
  at the next room's zoom before the camera has eased there. The existing overload delegates with `Zoom`
  (behaviour unchanged).
```

- [ ] **Step 3: Update the three guard-checked doc declarations**

In `docs/CONSUMERS.md`, change the `**Engine current version:** \`5.54.0\`` line to `5.55.0`.
In `docs/ROADMAP.md`, change the `Current released version: **5.54.0**` line (near the top) to `5.55.0`.
In `README.md`, change every `Version="5.54.0"` in the `<PackageReference>` example block to `5.55.0` (grep `grep -n "5.54.0" README.md` first to find all ~4 lines).

- [ ] **Step 4: Update the ROADMAP camera section**

In `docs/ROADMAP.md`, in the "Camera: first-class follow / scroller camera" section:

(a) Under the `**Shipped:**` list, append:

```markdown
- 5.55.0: `RoomCamera` + `CameraRoom` (`KhaozEngine.Render2D`) - **room / region cameras**: per-area bounds
  (+ optional zoom), in-room follow confined to the region, and an eased hand-off (CameraBlend) when the
  target crosses into a new region. Composes the follow + blend layers.
```

(b) From the `**Still open**` list, delete the bullet beginning `- Room / region cameras` (it spans the line ending `...Metroidvania-style.`). Leave the other still-open bullets (parallax background layers, screen shake) intact. If the exact wording differs, report the discrepancy rather than guessing — remove the bullet that clearly corresponds to room/region cameras.

- [ ] **Step 5: Verify the doc-version guard passes**

Run: `bash scripts/check-doc-versions.sh`
Expected: exit 0 (declarations match `<KhaozEngineVersion>` = 5.55.0).

- [ ] **Step 6: Test and pack**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green).
Run: `dotnet pack -c Release -o ./local-feed`
Expected: builds `KhaozEngine.Render2D.5.55.0.nupkg` (and the other 5.x packages) into `local-feed` (cumulative; do not delete old versions). Confirm with `ls local-feed/KhaozEngine.Render2D.5.55.0.nupkg`.

- [ ] **Step 7: Commit and tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render2d(5.55.0): room/region cameras — RoomCamera + CameraRoom"
git tag v5.55.0
```

Pushing `main` + the tag happens at branch-finish time, not here.

---

## Self-Review Notes

- **Spec coverage:** explicit-zoom `ClampPosition` overload (Task 1), `CameraRoom` Bounds/Zoom/Contains (Task 2), `RoomCamera` resolve/snap/handoff/settle + Warp + exposed Follow + overlap + hold-on-no-room (Task 3), release incl. ROADMAP edits (Task 4). All spec sections + test-matrix items mapped.
- **Type consistency:** `Camera2D.ClampPosition(Vector2, Rect, int, int, float)` overload + delegating 4-arg; `CameraRoom(Rect, float?)` + `Contains(Vector2)` + readonly `Bounds`/`Zoom`; `RoomCamera(Camera2D, IReadOnlyList<CameraRoom>)` + `Camera`/`Follow`/`ActiveRoomIndex`/`IsTransitioning`/`BlendDuration`/`BlendEasing` + `Update(Vector2, Vector2, float, int, int)` + zero-velocity overload + `Warp(Vector2, int, int)`. Uses `CameraFollow.Update(target, velocity, dt, vw, vh, Rect)`, `CameraFollow.Warp`, `CameraBlend.To(CameraState, float, Func<float,float>)`, `CameraBlend.Update`, `CameraBlend.IsBlending`, `CameraState(Vector2, float, float)`, `Easing.SmoothStep` — all matching the shipped signatures.
- **No placeholders:** every code step shows complete code; commands have expected output.
```
