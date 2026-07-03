# Camera Feel Layer (Platformer Follow) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Bring the camera "feel" layer to the 5.x engine by porting `CameraFollow` to `KhaozEngine.Render2D` (System.Numerics, no MonoGame) and adding per-axis follow stiffness, look-ahead, and pixel-perfect snapping.

**Architecture:** One enriched `CameraFollow` class owns the follow concerns (per-axis stiffness, deadzone, look-ahead, bounds clamp) and drives a `Render2D.Camera2D`. Smoothing operates on an internal sub-pixel position; a separate reusable `PixelSnap` value type snaps only the rendered `Camera.Position`, so snapping causes no drift. A small `LookAheadSettings` value type groups the look-ahead knobs. The base `Camera2D` is untouched.

**Tech Stack:** C# / net10.0, `System.Numerics` (`Vector2`), `KhaozEngine.Windowing.Rect`, xUnit. All headless, no `GraphicsDevice`.

**Spec:** `docs/superpowers/specs/2026-06-18-camera-feel-platformer-design.md`

---

## File Structure

- `KhaozEngine.Render2D/PixelSnap.cs` (new) — pure value type, snaps a world position to the art-pixel grid.
- `KhaozEngine.Render2D/LookAheadSettings.cs` (new) — pure value type, look-ahead config.
- `KhaozEngine.Render2D/CameraFollow.cs` (new) — the follow driver.
- `KhaozEngine.Tests/Render2DCameraFollowTests.cs` (new) — headless coverage.
- Release files (Task 5): `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

Reference facts:
- `Camera2D` (`KhaozEngine.Render2D/Camera2D.cs`): `Position`, `Zoom` (default 1), `Rotation`; `WorldToScreen(Vector2, int vw, int vh)`; `ClampPosition(Vector2 desired, Rect worldBounds, int vw, int vh)`. The axis-aligned helpers ignore `Rotation` (exact at 0).
- `Rect` (`KhaozEngine.Windowing/Rect.cs`): `readonly record struct Rect(float X, float Y, float Width, float Height)` with `Right`, `Bottom`, `Contains`.
- `KhaozEngine.Render2D` already references `KhaozEngine.Windowing` and has `InternalsVisibleTo("KhaozEngine.Tests")`.
- Run all `dotnet` commands from the worktree root.

---

## Task 1: `PixelSnap` value type

**Files:**
- Create: `KhaozEngine.Render2D/PixelSnap.cs`
- Test: `KhaozEngine.Tests/Render2DCameraFollowTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render2DCameraFollowTests.cs`:

```csharp
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for the 5.x camera feel layer (PixelSnap, LookAheadSettings, CameraFollow).</summary>
public class Render2DCameraFollowTests
{
    private const int Vw = 800, Vh = 600;                    // center (400, 300)
    private const float Tol = 1e-2f;
    private static readonly Rect Unbounded = new(-1_000_000f, -1_000_000f, 2_000_000f, 2_000_000f);

    private static void AssertClose(Vector2 expected, Vector2 actual, float tol = Tol) =>
        Assert.True(Vector2.Distance(expected, actual) <= tol, $"expected {expected}, got {actual}");

    // ---- PixelSnap ----

    [Fact]
    public void PixelSnap_RoundsEachAxisToGrid()
    {
        var snap = new PixelSnap(10f);
        AssertClose(new Vector2(10f, 10f), snap.Apply(new Vector2(13f, 7f)));
        AssertClose(new Vector2(20f, -10f), snap.Apply(new Vector2(16f, -12f)));
    }

    [Fact]
    public void PixelSnap_DisabledIsIdentity()
    {
        var snap = default(PixelSnap);          // Enabled == false
        var p = new Vector2(13.37f, -4.2f);
        AssertClose(p, snap.Apply(p));
    }

    [Fact]
    public void PixelSnap_NonPositiveGridIsIdentity()
    {
        var snap = new PixelSnap(0f);           // ctor sets Enabled = true but grid is 0
        var p = new Vector2(13.37f, -4.2f);
        AssertClose(p, snap.Apply(p));
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests.PixelSnap"`
Expected: FAIL — `PixelSnap` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/PixelSnap.cs`:

```csharp
using System;
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Snaps a world position to the art-pixel grid. Reusable by any camera (a gesture camera could snap
    /// too), not just <see cref="CameraFollow"/>. Snaps camera <i>translation</i> to the grid, which kills
    /// camera-induced sub-pixel shimmer; full pixel-perfect rendering also needs integer zoom + a fixed-
    /// resolution render target, which is the game's render-target responsibility, not this layer's.
    /// </summary>
    public readonly struct PixelSnap
    {
        /// <summary>When false, <see cref="Apply"/> returns the input unchanged.</summary>
        public readonly bool Enabled;

        /// <summary>Grid size in world units; <see cref="Apply"/> rounds each axis to a multiple of this.</summary>
        public readonly float WorldUnitsPerPixel;

        /// <summary>Creates an enabled snap with the given grid size (world units per art pixel).</summary>
        public PixelSnap(float worldUnitsPerPixel)
        {
            Enabled = true;
            WorldUnitsPerPixel = worldUnitsPerPixel;
        }

        /// <summary>Rounds each axis to the nearest multiple of <see cref="WorldUnitsPerPixel"/>.
        /// No-op when disabled or the grid size is non-positive.</summary>
        public Vector2 Apply(Vector2 worldPos)
        {
            if (!Enabled || WorldUnitsPerPixel <= 0f) return worldPos;
            float u = WorldUnitsPerPixel;
            return new Vector2(MathF.Round(worldPos.X / u) * u, MathF.Round(worldPos.Y / u) * u);
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests.PixelSnap"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/PixelSnap.cs KhaozEngine.Tests/Render2DCameraFollowTests.cs
git commit -m "feat(render2d): PixelSnap value type — snap world position to art-pixel grid"
```

---

## Task 2: `CameraFollow` foundation (port + per-axis stiffness + Warp)

Ports the 4.x `CameraFollow` to 5.x with per-axis `Stiffness`, an internal sub-pixel position, and `Warp`. No look-ahead or snap yet (added in Tasks 3 and 4).

**Files:**
- Create: `KhaozEngine.Render2D/CameraFollow.cs`
- Test: `KhaozEngine.Tests/Render2DCameraFollowTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append these methods inside the `Render2DCameraFollowTests` class (before the closing brace):

```csharp
    // ---- CameraFollow foundation ----

    [Fact]
    public void Follow_SnapsWhenStiffnessNonPositive()
    {
        var cam = new Camera2D();
        var follow = new CameraFollow(cam);
        follow.SetStiffness(0f);

        follow.Update(new Vector2(100f, -50f), 0.016f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(100f, -50f), cam.Position);
    }

    [Fact]
    public void Follow_SmoothedStepMovesFractionTowardTarget()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam) { Stiffness = new Vector2(10f, 10f) };

        follow.Update(new Vector2(100f, 0f), 0.1f, Vw, Vh, Unbounded);

        // t = 1 - exp(-10 * 0.1) = 0.6321
        AssertClose(new Vector2(63.21f, 0f), cam.Position);
    }

    [Fact]
    public void Follow_SmoothingIsFrameRateIndependent()
    {
        var target = new Vector2(100f, 0f);

        var camOne = new Camera2D { Position = Vector2.Zero };
        var followOne = new CameraFollow(camOne) { Stiffness = new Vector2(10f, 10f) };
        followOne.Update(target, 0.2f, Vw, Vh, Unbounded);

        var camTwo = new Camera2D { Position = Vector2.Zero };
        var followTwo = new CameraFollow(camTwo) { Stiffness = new Vector2(10f, 10f) };
        followTwo.Update(target, 0.1f, Vw, Vh, Unbounded);
        followTwo.Update(target, 0.1f, Vw, Vh, Unbounded);

        AssertClose(camOne.Position, camTwo.Position, 0.1f);
        Assert.True(camOne.Position.X is > 86f and < 87f);   // 1 - exp(-2) = 0.8647
    }

    [Fact]
    public void Follow_PerAxisStiffnessIsIndependent()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        // X eases (stiffness 10), Y snaps (stiffness 0).
        var follow = new CameraFollow(cam) { Stiffness = new Vector2(10f, 0f) };

        follow.Update(new Vector2(100f, 100f), 0.1f, Vw, Vh, Unbounded);

        Assert.Equal(63.21f, cam.Position.X, Tol);   // eased
        Assert.Equal(100f, cam.Position.Y, Tol);     // snapped
    }

    [Fact]
    public void Follow_DeadzoneHoldsTargetWithoutMoving()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var follow = new CameraFollow(cam)
        {
            Stiffness = new Vector2(10f, 10f),
            Deadzone = new Rect(300f, 200f, 200f, 200f),
        };

        follow.Update(new Vector2(50f, 0f), 0.1f, Vw, Vh, Unbounded);   // screen (450,300), inside

        AssertClose(Vector2.Zero, cam.Position);
    }

    [Fact]
    public void Follow_DeadzoneChasesOnceTargetLeaves()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f };
        var follow = new CameraFollow(cam) { Deadzone = new Rect(300f, 200f, 200f, 200f) };
        follow.SetStiffness(0f);

        // world (200,0) -> screen (600,300); 100px past the right edge (500).
        follow.Update(new Vector2(200f, 0f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(100f, 0f), cam.Position);
        AssertClose(new Vector2(500f, 300f), cam.WorldToScreen(new Vector2(200f, 0f), Vw, Vh));
    }

    [Fact]
    public void Follow_ClampsToWorldBounds()
    {
        var cam = new Camera2D { Zoom = 1f };
        var follow = new CameraFollow(cam);
        follow.SetStiffness(0f);
        var bounds = new Rect(0f, 0f, 1000f, 1000f);   // X[400,600], Y[300,700]

        follow.Update(new Vector2(9999f, 500f), 0.016f, Vw, Vh, bounds);

        Assert.Equal(600f, cam.Position.X, Tol);
        Assert.Equal(500f, cam.Position.Y, Tol);
    }

    [Fact]
    public void Follow_WarpHardSetsPositionBypassingSmoothing()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam) { Stiffness = new Vector2(10f, 10f) };

        follow.Warp(new Vector2(500f, 500f));
        Assert.Equal(new Vector2(500f, 500f), cam.Position);

        // One small step toward a far target eases from (500,500), not from the origin.
        follow.Update(new Vector2(1500f, 500f), 0.1f, Vw, Vh, Unbounded);
        Assert.Equal(500f + 1000f * (1f - MathF.Exp(-1f)), cam.Position.X, 0.5f);
    }
```

Add `using System;` to the file's usings if not already present (for `MathF`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests.Follow_"`
Expected: FAIL — `CameraFollow` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/CameraFollow.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Windowing;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Drives a <see cref="Camera2D"/> to follow a moving target, with per-axis frame-rate-independent
    /// smoothing, an optional screen-space deadzone, look-ahead, a world-bounds clamp, and optional
    /// pixel-snap on the rendered position. The game decides <i>what</i> to follow and supplies the target's
    /// velocity; this owns only the feel. Pure <see cref="System.Numerics"/>, headless, no GPU.
    ///
    /// <para>Smoothing runs on an internal sub-pixel position; the pixel snap only touches the rendered
    /// <see cref="Camera2D.Position"/>, so snapping introduces no drift. Call <see cref="Update(Vector2,
    /// Vector2, float, int, int, Rect)"/> once per frame.</para>
    /// </summary>
    public sealed class CameraFollow
    {
        private readonly Camera2D _camera;
        private Vector2 _smoothPos;     // sub-pixel-accurate truth the smoothing operates on
        private bool _initialized;      // false until the first Update / Warp seeds _smoothPos

        /// <summary>Creates a follow controller for the given camera.</summary>
        public CameraFollow(Camera2D camera) => _camera = camera;

        /// <summary>The camera this controller drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>Per-axis smoothing rate (per second): higher is snappier. The per-frame catch-up on each
        /// axis is <c>1 - exp(-Stiffness.axis * dt)</c>, independent of frame rate. A component &lt;= 0 snaps
        /// that axis instantly.</summary>
        public Vector2 Stiffness { get; set; } = new(10f, 10f);

        /// <summary>Convenience: sets both axes of <see cref="Stiffness"/> to the same value.</summary>
        public void SetStiffness(float both) => Stiffness = new Vector2(both, both);

        /// <summary>An absolute screen-space rectangle the target may move within before the camera chases
        /// (same space as <see cref="Camera2D.WorldToScreen(Vector2, int, int)"/> output, rotation assumed 0).
        /// While the target's screen position stays inside it the camera holds; crossing an edge moves the
        /// camera just enough to put the target back on that edge. <c>null</c> (default) centers on the
        /// target.</summary>
        public Rect? Deadzone { get; set; }

        /// <summary>
        /// Follow step. Eases the camera toward <paramref name="target"/> (held within <see cref="Deadzone"/>
        /// if set), then clamps so the view stays inside <paramref name="worldBounds"/>.
        /// <paramref name="velocity"/> drives look-ahead.
        /// </summary>
        public void Update(Vector2 target, Vector2 velocity, float dt,
                           int viewportWidth, int viewportHeight, Rect worldBounds)
        {
            if (!_initialized) { _smoothPos = _camera.Position; _initialized = true; }

            Vector2 desired = ComputeDesired(target, viewportWidth, viewportHeight);

            _smoothPos = new Vector2(
                EaseAxis(_smoothPos.X, desired.X, Stiffness.X, dt),
                EaseAxis(_smoothPos.Y, desired.Y, Stiffness.Y, dt));

            _smoothPos = _camera.ClampPosition(_smoothPos, worldBounds, viewportWidth, viewportHeight);

            _camera.Position = _smoothPos;
        }

        /// <summary>Convenience overload with zero velocity (look-ahead inert).</summary>
        public void Update(Vector2 target, float dt, int viewportWidth, int viewportHeight, Rect worldBounds)
            => Update(target, Vector2.Zero, dt, viewportWidth, viewportHeight, worldBounds);

        /// <summary>Hard-sets the camera to <paramref name="position"/>, bypassing smoothing and clearing the
        /// accumulated lead. For respawn / scene load so the camera does not ease across the level.</summary>
        public void Warp(Vector2 position)
        {
            _smoothPos = position;
            _initialized = true;
            _camera.Position = position;
        }

        private static float EaseAxis(float current, float desired, float stiffness, float dt)
            => stiffness <= 0f || dt <= 0f
                ? desired
                : current + (desired - current) * (1f - MathF.Exp(-stiffness * dt));

        // The position that satisfies the follow rule: center on the target (no deadzone), or shift by the
        // target's screen overflow past the deadzone edges (converted back to world via zoom). Rotation 0.
        private Vector2 ComputeDesired(Vector2 target, int vw, int vh)
        {
            if (Deadzone is not Rect dz) return target;

            // Screen position of the target relative to the current sub-pixel camera position (rotation 0).
            float zoom = _camera.Zoom;
            var screen = new Vector2(
                (target.X - _smoothPos.X) * zoom + vw * 0.5f,
                (target.Y - _smoothPos.Y) * zoom + vh * 0.5f);

            float dx = screen.X < dz.X ? screen.X - dz.X
                     : screen.X > dz.Right ? screen.X - dz.Right : 0f;
            float dy = screen.Y < dz.Y ? screen.Y - dz.Y
                     : screen.Y > dz.Bottom ? screen.Y - dz.Bottom : 0f;

            if ((dx == 0f && dy == 0f) || zoom <= 0f) return _smoothPos;

            return _smoothPos + new Vector2(dx, dy) / zoom;
        }
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests.Follow_"`
Expected: PASS (8 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/CameraFollow.cs KhaozEngine.Tests/Render2DCameraFollowTests.cs
git commit -m "feat(render2d): CameraFollow port to 5.x — per-axis stiffness, deadzone, bounds clamp, Warp"
```

---

## Task 3: Look-ahead

Adds `LookAheadSettings` and wires the eased lead offset into `CameraFollow.Update`.

**Files:**
- Create: `KhaozEngine.Render2D/LookAheadSettings.cs`
- Modify: `KhaozEngine.Render2D/CameraFollow.cs`
- Test: `KhaozEngine.Tests/Render2DCameraFollowTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append inside the `Render2DCameraFollowTests` class:

```csharp
    // ---- Look-ahead ----

    [Fact]
    public void LookAhead_DisabledByDefaultProducesNoOffset()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam);
        follow.SetStiffness(0f);                            // snap, so position == desired

        follow.Update(new Vector2(0f, 0f), new Vector2(100f, 0f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(Vector2.Zero, cam.Position);            // default LookAhead -> no lead
    }

    [Fact]
    public void LookAhead_LeadsAlongVelocity()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam)
        {
            // Lead 0.4s horizontally, generous clamp, instant lead easing (Stiffness 0).
            LookAhead = new LookAheadSettings(new Vector2(0.4f, 0f), new Vector2(1000f, 0f), 0f),
        };
        follow.SetStiffness(0f);

        follow.Update(new Vector2(0f, 0f), new Vector2(100f, 0f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(40f, 0f), cam.Position);    // 100 * 0.4 = 40 ahead, +x
    }

    [Fact]
    public void LookAhead_ClampsToMaxDistance()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam)
        {
            LookAhead = new LookAheadSettings(new Vector2(0.4f, 0f), new Vector2(30f, 0f), 0f),
        };
        follow.SetStiffness(0f);

        follow.Update(new Vector2(0f, 0f), new Vector2(100f, 0f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(30f, 0f), cam.Position);    // 40 desired, clamped to 30
    }

    [Fact]
    public void LookAhead_PerAxisLeavesUnleadAxisOnTarget()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam)
        {
            LookAhead = new LookAheadSettings(new Vector2(0.4f, 0f), new Vector2(1000f, 1000f), 0f),
        };
        follow.SetStiffness(0f);

        follow.Update(new Vector2(0f, 0f), new Vector2(100f, 100f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(40f, 0f), cam.Position);    // x leads, y has LeadTime 0 -> no lead
    }

    [Fact]
    public void LookAhead_EasesOnDirectionReversal()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam)
        {
            // Finite lead easing so a reversal does not jump the offset in one frame.
            LookAhead = new LookAheadSettings(new Vector2(0.4f, 0f), new Vector2(1000f, 0f), 10f),
        };
        follow.SetStiffness(0f);

        // Build a positive lead over several frames moving +x.
        for (int i = 0; i < 20; i++)
            follow.Update(new Vector2(0f, 0f), new Vector2(100f, 0f), 0.1f, Vw, Vh, Unbounded);
        Assert.True(cam.Position.X > 30f);                  // lead settled near +40

        float before = cam.Position.X;
        // One frame of reversed velocity: the lead eases toward -40, it must not snap there.
        follow.Update(new Vector2(0f, 0f), new Vector2(-100f, 0f), 0.1f, Vw, Vh, Unbounded);

        Assert.True(cam.Position.X < before);               // moved toward the new target
        Assert.True(cam.Position.X > -40f);                 // but did not jump all the way
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests.LookAhead_"`
Expected: FAIL — `LookAheadSettings` does not exist / `CameraFollow.LookAhead` not defined (compile error).

- [ ] **Step 3a: Create `LookAheadSettings`**

Create `KhaozEngine.Render2D/LookAheadSettings.cs`:

```csharp
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Look-ahead configuration: lead the camera ahead of the target along its velocity. Per frame the lead
    /// target is <c>clamp(velocity * LeadTime, -MaxDistance .. +MaxDistance)</c> per axis; the applied offset
    /// eases toward that target at <see cref="Stiffness"/> so a direction reversal does not snap. The
    /// <c>default</c> value (all zero) is disabled — <see cref="LeadTime"/> of 0 on an axis means no lead.
    /// </summary>
    public readonly struct LookAheadSettings
    {
        /// <summary>Seconds of velocity to lead by, per axis. 0 on an axis = no lead there.</summary>
        public readonly Vector2 LeadTime;

        /// <summary>Clamp on lead magnitude, per axis (world units). A component &lt;= 0 = unclamped.</summary>
        public readonly Vector2 MaxDistance;

        /// <summary>Easing rate of the lead offset (per second). &lt;= 0 = apply instantly.</summary>
        public readonly float Stiffness;

        /// <summary>Creates look-ahead settings.</summary>
        public LookAheadSettings(Vector2 leadTime, Vector2 maxDistance, float stiffness)
        {
            LeadTime = leadTime;
            MaxDistance = maxDistance;
            Stiffness = stiffness;
        }
    }
}
```

- [ ] **Step 3b: Wire look-ahead into `CameraFollow`**

In `KhaozEngine.Render2D/CameraFollow.cs`, add the `_leadOffset` field next to `_smoothPos`:

```csharp
        private Vector2 _smoothPos;     // sub-pixel-accurate truth the smoothing operates on
        private Vector2 _leadOffset;    // currently-applied (eased) look-ahead offset
        private bool _initialized;      // false until the first Update / Warp seeds _smoothPos
```

Add the `LookAhead` property after the `Deadzone` property:

```csharp
        /// <summary>Look-ahead configuration. <c>default</c> (zero lead time) disables it.</summary>
        public LookAheadSettings LookAhead { get; set; }
```

Replace the body of `Update(Vector2 target, Vector2 velocity, ...)` so the lead is applied to `desired`
before smoothing:

```csharp
        public void Update(Vector2 target, Vector2 velocity, float dt,
                           int viewportWidth, int viewportHeight, Rect worldBounds)
        {
            if (!_initialized) { _smoothPos = _camera.Position; _initialized = true; }

            Vector2 desired = ComputeDesired(target, viewportWidth, viewportHeight);
            desired += UpdateLeadOffset(velocity, dt);

            _smoothPos = new Vector2(
                EaseAxis(_smoothPos.X, desired.X, Stiffness.X, dt),
                EaseAxis(_smoothPos.Y, desired.Y, Stiffness.Y, dt));

            _smoothPos = _camera.ClampPosition(_smoothPos, worldBounds, viewportWidth, viewportHeight);

            _camera.Position = _smoothPos;
        }
```

Add the lead helper (e.g. below `ComputeDesired`):

```csharp
        // Eases _leadOffset toward clamp(velocity * LeadTime, +/-MaxDistance) per axis, returns the new offset.
        private Vector2 UpdateLeadOffset(Vector2 velocity, float dt)
        {
            var leadTarget = new Vector2(
                ClampAxis(velocity.X * LookAhead.LeadTime.X, LookAhead.MaxDistance.X),
                ClampAxis(velocity.Y * LookAhead.LeadTime.Y, LookAhead.MaxDistance.Y));

            _leadOffset = new Vector2(
                EaseAxis(_leadOffset.X, leadTarget.X, LookAhead.Stiffness, dt),
                EaseAxis(_leadOffset.Y, leadTarget.Y, LookAhead.Stiffness, dt));

            return _leadOffset;
        }

        // Clamps value to [-max, max]; max <= 0 means unclamped.
        private static float ClampAxis(float value, float max)
            => max <= 0f ? value : Math.Clamp(value, -max, max);
```

Update `Warp` to also clear the lead:

```csharp
        public void Warp(Vector2 position)
        {
            _smoothPos = position;
            _leadOffset = Vector2.Zero;
            _initialized = true;
            _camera.Position = position;
        }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests.LookAhead_"`
Expected: PASS (5 tests). Then run the whole class to confirm no regressions:
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests"`
Expected: PASS (16 tests so far).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/LookAheadSettings.cs KhaozEngine.Render2D/CameraFollow.cs KhaozEngine.Tests/Render2DCameraFollowTests.cs
git commit -m "feat(render2d): CameraFollow look-ahead — eased, clamped, per-axis lead along velocity"
```

---

## Task 4: Pixel-snap on output

Wires `PixelSnap` into `CameraFollow` so it snaps only the rendered `Camera.Position` while smoothing keeps the sub-pixel truth.

**Files:**
- Modify: `KhaozEngine.Render2D/CameraFollow.cs`
- Test: `KhaozEngine.Tests/Render2DCameraFollowTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append inside the `Render2DCameraFollowTests` class:

```csharp
    // ---- Pixel snap integration ----

    [Fact]
    public void FollowSnap_RoundsRenderedPositionToGrid()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam) { Snap = new PixelSnap(10f) };
        follow.SetStiffness(0f);

        follow.Update(new Vector2(13f, 7f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(10f, 10f), cam.Position);   // snapped to the 10-unit grid
    }

    [Fact]
    public void FollowSnap_DisabledLeavesPositionExact()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam);                 // default Snap disabled
        follow.SetStiffness(0f);

        follow.Update(new Vector2(13f, 7f), 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(13f, 7f), cam.Position);
    }

    [Fact]
    public void FollowSnap_SmoothingHasNoCumulativeDrift()
    {
        var cam = new Camera2D { Position = Vector2.Zero };
        var follow = new CameraFollow(cam)
        {
            Stiffness = new Vector2(10f, 10f),
            Snap = new PixelSnap(10f),
        };
        var target = new Vector2(100f, 0f);

        // Many smoothing frames toward a fixed target: sub-pixel truth converges, output stays on the grid.
        for (int i = 0; i < 200; i++)
            follow.Update(target, 0.1f, Vw, Vh, Unbounded);

        AssertClose(new Vector2(100f, 0f), cam.Position);   // snap(100) == 100, no drift accumulated

        // A few more frames at convergence keep the output stable (no shimmer from re-snapping).
        var settled = cam.Position;
        follow.Update(target, 0.1f, Vw, Vh, Unbounded);
        AssertClose(settled, cam.Position, 1e-4f);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests.FollowSnap_"`
Expected: FAIL — `CameraFollow.Snap` not defined (compile error).

- [ ] **Step 3: Wire snap into `CameraFollow`**

In `KhaozEngine.Render2D/CameraFollow.cs`, add the `Snap` property after `LookAhead`:

```csharp
        /// <summary>Pixel-snap applied to the rendered <see cref="Camera2D.Position"/> only; smoothing keeps
        /// the sub-pixel truth, so there is no drift. <c>default</c> (disabled) leaves the position exact.</summary>
        public PixelSnap Snap { get; set; }
```

Change the final output line of `Update(Vector2 target, Vector2 velocity, ...)` from:

```csharp
            _camera.Position = _smoothPos;
```

to:

```csharp
            _camera.Position = Snap.Enabled ? Snap.Apply(_smoothPos) : _smoothPos;
```

And apply the same snap in `Warp` so a warped position lands on the grid too. Change `Warp`'s final line
from:

```csharp
            _camera.Position = position;
```

to:

```csharp
            _camera.Position = Snap.Enabled ? Snap.Apply(position) : position;
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraFollowTests"`
Expected: PASS (19 tests).

- [ ] **Step 5: Run the full suite for regressions**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green; prior count + 19 new).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render2D/CameraFollow.cs KhaozEngine.Tests/Render2DCameraFollowTests.cs
git commit -m "feat(render2d): CameraFollow pixel snap on output — sub-pixel smoothing preserved"
```

---

## Task 5: Release ritual (5.52.0)

Additive change → minor bump. Follows `KhaozEngine/CLAUDE.md` release order.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the 5.x version**

In `Directory.Build.props`, change:

```xml
   <KhaozEngineVersion>5.51.0</KhaozEngineVersion>
```

to:

```xml
   <KhaozEngineVersion>5.52.0</KhaozEngineVersion>
```

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert a new newest-first entry directly above the `## 5.51.0 (custom 5.x line)` heading:

```markdown
## 5.52.0 (custom 5.x line)

Camera feel layer for 2D / platformer games arrives on the 5.x engine: `CameraFollow` (previously 4.x-only,
MonoGame-bound) is ported to `KhaozEngine.Render2D` (System.Numerics, headless) and enriched for side-scroller
feel.

- **`CameraFollow`** (`KhaozEngine.Render2D`) - drives a `Camera2D` to follow a target with per-axis,
  frame-rate-independent smoothing (`1 - exp(-Stiffness.axis * dt)`), an optional absolute screen-space
  `Deadzone` (`Rect?`), a world-bounds clamp, and `Warp(position)` for instant respawn / scene-load placement.
  `Stiffness` is a per-axis `Vector2` (a component `<= 0` snaps that axis); `SetStiffness(float)` sets both.
- **Look-ahead** - `CameraFollow.LookAhead` (`LookAheadSettings`) leads the camera ahead of the target along a
  caller-supplied velocity: `clamp(velocity * LeadTime, +/-MaxDistance)` per axis, eased by its own
  `Stiffness` so a direction reversal does not snap. Per-axis `LeadTime` allows horizontal-only lead.
- **Pixel snap** - `CameraFollow.Snap` (`PixelSnap`, also usable standalone) snaps the rendered
  `Camera.Position` to an art-pixel grid (`WorldUnitsPerPixel`) while smoothing keeps the sub-pixel truth, so
  there is no drift. Snaps camera translation only; integer zoom + a fixed-resolution render target remain the
  game's responsibility.
```

- [ ] **Step 3: Update the three guard-checked doc declarations**

In `docs/CONSUMERS.md`, change the `**Engine current version:** \`5.51.0\`` line to `5.52.0`.

In `docs/ROADMAP.md`, change the `Current released version: **5.51.0**` line (near the top) to `5.52.0`.

In `README.md`, change every `KhaozEngine.*` `<PackageReference>` example version from `5.51.0` to `5.52.0`
(lines around 98-101: `KhaozEngine.Game2D`, `KhaozEngine.Game3D`, `KhaozEngine.Server`,
`KhaozEngine.Foundation`).

- [ ] **Step 4: Update the ROADMAP camera section**

In `docs/ROADMAP.md`, in the "Camera: first-class follow / scroller camera" section, move three items out of
"Still open" and record them as shipped. Under the `**Shipped:**` list add:

```markdown
- `CameraFollow` ported to the 5.x `KhaozEngine.Render2D` line (5.52.0) with **per-axis follow tuning**
  (`Stiffness` is a `Vector2`), **look-ahead** (`LookAheadSettings`: eased, clamped, per-axis lead along a
  caller-supplied velocity), and **pixel-perfect snapping** (`PixelSnap` on the rendered position). The 4.x
  `KhaozEngine.Graphics` `CameraFollow` stays for SpaceGame until it migrates.
```

Then delete these three now-shipped bullets from the `**Still open**` list: the "Per-axis follow tuning"
bullet, the "Look-ahead" bullet, and the "Pixel-perfect snapping" bullet. Leave the remaining open items
(multi-target framing, room/region cameras, eased zoom transitions, parallax, screen shake).

- [ ] **Step 5: Verify the doc-version guard passes**

Run: `bash scripts/check-doc-versions.sh`
Expected: exit 0 (the three declarations now match `<KhaozEngineVersion>` = 5.52.0).

- [ ] **Step 6: Pack and run the full suite**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green).
Run: `dotnet pack -c Release -o ./local-feed`
Expected: builds `KhaozEngine.Render2D.5.52.0.nupkg` (and the other 5.x packages) into `local-feed` (cumulative; do not delete old versions).

- [ ] **Step 7: Commit and tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render2d(5.52.0): camera feel layer — CameraFollow + look-ahead + pixel snap on 5.x"
git tag v5.52.0
```

Pushing `main` + the tag happens at branch-finish time (per the finishing-a-development-branch flow), not here.

---

## Self-Review Notes

- **Spec coverage:** foundation port (Task 2), per-axis stiffness (Task 2), look-ahead (Task 3), pixel snap (Task 4), all tests from the spec's matrix mapped to facts; release ritual incl. ROADMAP edits (Task 5). All spec sections covered.
- **Type consistency:** `CameraFollow.Update(Vector2 target, Vector2 velocity, float dt, int vw, int vh, Rect worldBounds)` + zero-velocity overload, `Stiffness` (`Vector2`), `SetStiffness(float)`, `Deadzone` (`Rect?`), `LookAhead` (`LookAheadSettings`), `Snap` (`PixelSnap`), `Warp(Vector2)` used consistently across tasks. `PixelSnap(float)` ctor + `Apply(Vector2)` + `Enabled`/`WorldUnitsPerPixel`. `LookAheadSettings(Vector2 leadTime, Vector2 maxDistance, float stiffness)`. `Camera2D.ClampPosition(desired, Rect, int, int)` and `WorldToScreen(Vector2, int, int)` match the real signatures.
- **No placeholders:** every code step shows complete code; commands have expected output.
