# Camera Eased Blends Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a reusable one-shot eased camera blend to 5.x `KhaozEngine.Render2D`: `CameraBlend` transitions a `Camera2D` from its current state to a target `CameraState` (position+zoom+rotation) over a duration with an `Easing` curve.

**Architecture:** Three small units. `CameraState` is an immutable framing snapshot (also the room-camera "setup" type later). `Easing` is pure preset curves (`float -> float` on `[0,1]`). `CameraBlend` captures a start state, advances elapsed time per `Update`, and applies `Lerp(start, target, easing(t))` until done. Base `Camera2D` is untouched.

**Tech Stack:** C# / net10.0, `System.Numerics` (`Vector2`), xUnit. Headless, no `GraphicsDevice`.

**Spec:** `docs/superpowers/specs/2026-06-18-camera-eased-blends-design.md`

---

## File Structure

- `KhaozEngine.Render2D/CameraState.cs` (new) — immutable snapshot + From/ApplyTo/Lerp.
- `KhaozEngine.Render2D/Easing.cs` (new) — pure preset curves.
- `KhaozEngine.Render2D/CameraBlend.cs` (new) — the transition driver.
- `KhaozEngine.Tests/Render2DCameraBlendTests.cs` (new) — headless coverage (one file, sections per unit).
- Release files (Task 4): `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`.

Reference facts:
- `Camera2D` (`KhaozEngine.Render2D/Camera2D.cs`): public fields `Vector2 Position`, `float Zoom` (default 1), `float Rotation` (default 0).
- `KhaozEngine.Render2D` has `InternalsVisibleTo("KhaozEngine.Tests")`.
- No existing easing helper in the engine (confirmed by search).
- Run all `dotnet` commands from the worktree root.

---

## Task 1: `CameraState` snapshot

**Files:**
- Create: `KhaozEngine.Render2D/CameraState.cs`
- Test: `KhaozEngine.Tests/Render2DCameraBlendTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `KhaozEngine.Tests/Render2DCameraBlendTests.cs`:

```csharp
using System;
using System.Numerics;
using KhaozEngine.Render2D;
using Xunit;

namespace KhaozEngine.Tests;

/// <summary>Headless coverage for eased camera blends (CameraState, Easing, CameraBlend).</summary>
public class Render2DCameraBlendTests
{
    private const float Tol = 1e-4f;

    private static void AssertState(Camera2D cam, Vector2 pos, float zoom, float rot)
    {
        Assert.True(Vector2.Distance(pos, cam.Position) <= 1e-3f, $"pos expected {pos}, got {cam.Position}");
        Assert.Equal(zoom, cam.Zoom, 1e-3f);
        Assert.Equal(rot, cam.Rotation, 1e-3f);
    }

    // ---- CameraState ----

    [Fact]
    public void State_FromCapturesCameraFields()
    {
        var cam = new Camera2D { Position = new Vector2(3f, 4f), Zoom = 2f, Rotation = 0.5f };
        var s = CameraState.From(cam);
        Assert.Equal(new Vector2(3f, 4f), s.Position);
        Assert.Equal(2f, s.Zoom, Tol);
        Assert.Equal(0.5f, s.Rotation, Tol);
    }

    [Fact]
    public void State_ApplyToWritesCameraFields()
    {
        var cam = new Camera2D();
        new CameraState(new Vector2(7f, -2f), 3f, 1.25f).ApplyTo(cam);
        AssertState(cam, new Vector2(7f, -2f), 3f, 1.25f);
    }

    [Fact]
    public void State_LerpInterpolatesPerField()
    {
        var a = new CameraState(new Vector2(0f, 0f), 1f, 0f);
        var b = new CameraState(new Vector2(100f, 50f), 3f, 1f);

        var at0 = CameraState.Lerp(a, b, 0f);
        Assert.Equal(0f, at0.Zoom, Tol);

        var at1 = CameraState.Lerp(a, b, 1f);
        Assert.Equal(new Vector2(100f, 50f), at1.Position);
        Assert.Equal(3f, at1.Zoom, Tol);
        Assert.Equal(1f, at1.Rotation, Tol);

        var mid = CameraState.Lerp(a, b, 0.5f);
        Assert.Equal(new Vector2(50f, 25f), mid.Position);
        Assert.Equal(2f, mid.Zoom, Tol);
        Assert.Equal(0.5f, mid.Rotation, Tol);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraBlendTests.State_"`
Expected: FAIL — `CameraState` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/CameraState.cs`:

```csharp
using System.Numerics;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Immutable snapshot of a <see cref="Camera2D"/>'s framing: where it looks (<see cref="Position"/>),
    /// how far in (<see cref="Zoom"/>), and its roll (<see cref="Rotation"/>). Used as the endpoint of a
    /// <see cref="CameraBlend"/> and as a reusable camera "setup" value.
    /// </summary>
    public readonly struct CameraState
    {
        public readonly Vector2 Position;
        public readonly float   Zoom;
        public readonly float   Rotation;

        public CameraState(Vector2 position, float zoom, float rotation)
        {
            Position = position;
            Zoom = zoom;
            Rotation = rotation;
        }

        /// <summary>Snapshots the camera's current Position/Zoom/Rotation.</summary>
        public static CameraState From(Camera2D camera) => new(camera.Position, camera.Zoom, camera.Rotation);

        /// <summary>Writes this state onto <paramref name="camera"/>.</summary>
        public void ApplyTo(Camera2D camera)
        {
            camera.Position = Position;
            camera.Zoom = Zoom;
            camera.Rotation = Rotation;
        }

        /// <summary>Per-field linear interpolation (Position via <see cref="Vector2.Lerp"/>; Zoom/Rotation
        /// scalar). Rotation is interpolated linearly - no shortest-arc wrap; callers supply sane angles.</summary>
        public static CameraState Lerp(CameraState a, CameraState b, float t) => new(
            Vector2.Lerp(a.Position, b.Position, t),
            a.Zoom + (b.Zoom - a.Zoom) * t,
            a.Rotation + (b.Rotation - a.Rotation) * t);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraBlendTests.State_"`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/CameraState.cs KhaozEngine.Tests/Render2DCameraBlendTests.cs
git commit -m "feat(render2d): CameraState — immutable camera framing snapshot (From/ApplyTo/Lerp)"
```

---

## Task 2: `Easing` preset curves

**Files:**
- Create: `KhaozEngine.Render2D/Easing.cs`
- Test: `KhaozEngine.Tests/Render2DCameraBlendTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append these methods inside the `Render2DCameraBlendTests` class (before the closing brace):

```csharp
    // ---- Easing ----

    [Fact]
    public void Easing_EndpointsAreZeroAndOne()
    {
        Func<float, float>[] curves = { Easing.Linear, Easing.SmoothStep, Easing.EaseIn, Easing.EaseOut, Easing.EaseInOut };
        foreach (var f in curves)
        {
            Assert.Equal(0f, f(0f), Tol);
            Assert.Equal(1f, f(1f), Tol);
        }
    }

    [Fact]
    public void Easing_ClampsInputOutsideUnitInterval()
    {
        Func<float, float>[] curves = { Easing.Linear, Easing.SmoothStep, Easing.EaseIn, Easing.EaseOut, Easing.EaseInOut };
        foreach (var f in curves)
        {
            Assert.Equal(0f, f(-1f), Tol);
            Assert.Equal(1f, f(2f), Tol);
        }
    }

    [Fact]
    public void Easing_HasExpectedMidpointShapes()
    {
        Assert.Equal(0.3f, Easing.Linear(0.3f), Tol);
        Assert.Equal(0.5f, Easing.SmoothStep(0.5f), Tol);
        Assert.Equal(0.25f, Easing.EaseIn(0.5f), Tol);
        Assert.Equal(0.75f, Easing.EaseOut(0.5f), Tol);
        Assert.Equal(0.5f, Easing.EaseInOut(0.5f), Tol);
    }

    [Fact]
    public void Easing_IsMonotonicNonDecreasing()
    {
        Func<float, float>[] curves = { Easing.Linear, Easing.SmoothStep, Easing.EaseIn, Easing.EaseOut, Easing.EaseInOut };
        foreach (var f in curves)
        {
            float prev = f(0f);
            for (float t = 0.05f; t <= 1f; t += 0.05f)
            {
                float cur = f(t);
                Assert.True(cur >= prev - 1e-5f, $"curve not monotonic at t={t}: {cur} < {prev}");
                prev = cur;
            }
        }
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraBlendTests.Easing_"`
Expected: FAIL — `Easing` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/Easing.cs`:

```csharp
namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Pure easing curves for time-based transitions (e.g. <see cref="CameraBlend"/>). Each reshapes a
    /// progress value <c>t</c> (clamped to <c>[0,1]</c>) and returns the eased value in <c>[0,1]</c>, with
    /// <c>f(0)=0</c> and <c>f(1)=1</c>.
    /// </summary>
    public static class Easing
    {
        /// <summary>No easing: returns <c>t</c> unchanged (clamped).</summary>
        public static float Linear(float t) => Clamp01(t);

        /// <summary>Smooth acceleration and deceleration: <c>t*t*(3 - 2t)</c>. The <see cref="CameraBlend"/> default.</summary>
        public static float SmoothStep(float t) { t = Clamp01(t); return t * t * (3f - 2f * t); }

        /// <summary>Accelerating from zero: <c>t*t</c>.</summary>
        public static float EaseIn(float t) { t = Clamp01(t); return t * t; }

        /// <summary>Decelerating to one: <c>t*(2 - t)</c>.</summary>
        public static float EaseOut(float t) { t = Clamp01(t); return t * (2f - t); }

        /// <summary>Quadratic ease-in then ease-out: <c>t&lt;0.5 ? 2t^2 : 1 - 2(1-t)^2</c>.</summary>
        public static float EaseInOut(float t)
        {
            t = Clamp01(t);
            return t < 0.5f ? 2f * t * t : 1f - 2f * (1f - t) * (1f - t);
        }

        private static float Clamp01(float t) => t < 0f ? 0f : t > 1f ? 1f : t;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraBlendTests.Easing_"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add KhaozEngine.Render2D/Easing.cs KhaozEngine.Tests/Render2DCameraBlendTests.cs
git commit -m "feat(render2d): Easing — pure preset curves (Linear/SmoothStep/EaseIn/EaseOut/EaseInOut)"
```

---

## Task 3: `CameraBlend` driver

**Files:**
- Create: `KhaozEngine.Render2D/CameraBlend.cs`
- Test: `KhaozEngine.Tests/Render2DCameraBlendTests.cs:end-of-class`

- [ ] **Step 1: Write the failing tests**

Append these methods inside the `Render2DCameraBlendTests` class (before the closing brace):

```csharp
    // ---- CameraBlend ----

    private static CameraState TargetState => new(new Vector2(100f, 50f), 3f, 1f);

    [Fact]
    public void Blend_ReachesTargetExactlyAndClearsBlending()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = 0f };
        var blend = new CameraBlend(cam);

        blend.To(TargetState, 1f, Easing.Linear);
        Assert.True(blend.IsBlending);

        for (int i = 0; i < 10; i++) blend.Update(0.1f);   // 1.0s total

        AssertState(cam, new Vector2(100f, 50f), 3f, 1f);
        Assert.False(blend.IsBlending);
        Assert.Equal(1f, blend.Progress, Tol);
    }

    [Fact]
    public void Blend_LinearHalfwayIsMidpoint()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = 0f };
        var blend = new CameraBlend(cam);

        blend.To(TargetState, 1f, Easing.Linear);
        blend.Update(0.5f);

        AssertState(cam, new Vector2(50f, 25f), 2f, 0.5f);
        Assert.True(blend.IsBlending);
    }

    [Fact]
    public void Blend_ZeroDurationSnapsInstantly()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = 0f };
        var blend = new CameraBlend(cam);

        blend.To(TargetState, 0f);

        AssertState(cam, new Vector2(100f, 50f), 3f, 1f);
        Assert.False(blend.IsBlending);
        Assert.Equal(1f, blend.Progress, Tol);
    }

    [Fact]
    public void Blend_IsDeterministicOnElapsedTime()
    {
        var camA = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = 0f };
        var blendA = new CameraBlend(camA);
        blendA.To(TargetState, 1f);             // default SmoothStep
        blendA.Update(0.5f);

        var camB = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = 0f };
        var blendB = new CameraBlend(camB);
        blendB.To(TargetState, 1f);
        blendB.Update(0.25f);
        blendB.Update(0.25f);

        Assert.True(Vector2.Distance(camA.Position, camB.Position) <= Tol);
        Assert.Equal(camA.Zoom, camB.Zoom, Tol);
        Assert.Equal(camA.Rotation, camB.Rotation, Tol);
    }

    [Fact]
    public void Blend_MidBlendRetargetRecapturesStart()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = 0f };
        var blend = new CameraBlend(cam);

        blend.To(new CameraState(new Vector2(100f, 0f), 1f, 0f), 1f, Easing.Linear);
        blend.Update(0.5f);   // now at x=50
        float midX = cam.Position.X;
        Assert.Equal(50f, midX, 1e-3f);

        // Retarget from the mid-blend position to x=150; new start is the current (50) position.
        blend.To(new CameraState(new Vector2(150f, 0f), 1f, 0f), 1f, Easing.Linear);
        blend.Update(0.5f);   // halfway from 50 to 150 -> 100

        Assert.Equal(100f, cam.Position.X, 1e-3f);
    }

    [Fact]
    public void Blend_StopHaltsInPlace()
    {
        var cam = new Camera2D { Position = Vector2.Zero, Zoom = 1f, Rotation = 0f };
        var blend = new CameraBlend(cam);

        blend.To(TargetState, 1f, Easing.Linear);
        blend.Update(0.3f);
        var held = cam.Position;
        float heldZoom = cam.Zoom;

        blend.Stop();
        Assert.False(blend.IsBlending);

        blend.Update(0.5f);   // ignored once stopped
        Assert.True(Vector2.Distance(held, cam.Position) <= Tol);
        Assert.Equal(heldZoom, cam.Zoom, Tol);
    }

    [Fact]
    public void Blend_UpdateWhenIdleIsNoOp()
    {
        var cam = new Camera2D { Position = new Vector2(5f, 5f), Zoom = 2f, Rotation = 0.1f };
        var blend = new CameraBlend(cam);

        blend.Update(0.5f);   // never called To

        AssertState(cam, new Vector2(5f, 5f), 2f, 0.1f);
        Assert.False(blend.IsBlending);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraBlendTests.Blend_"`
Expected: FAIL — `CameraBlend` does not exist (compile error).

- [ ] **Step 3: Write the implementation**

Create `KhaozEngine.Render2D/CameraBlend.cs`:

```csharp
using System;

namespace KhaozEngine.Render2D
{
    /// <summary>
    /// Drives a one-shot, time-based transition of a <see cref="Camera2D"/> from its current state to a
    /// target <see cref="CameraState"/> over a duration, reshaped by an easing curve. Distinct from the
    /// continuous exponential smoothing of <c>CameraFollow</c>/<c>GroupCamera</c>: a blend has a definite
    /// start, end, and duration. Headless, no GPU.
    /// </summary>
    public sealed class CameraBlend
    {
        private readonly Camera2D _camera;
        private CameraState _start;
        private CameraState _target;
        private Func<float, float> _easing = Easing.SmoothStep;
        private float _duration;
        private float _elapsed;

        /// <summary>Creates a blend driver for the given camera.</summary>
        public CameraBlend(Camera2D camera) => _camera = camera;

        /// <summary>The camera this blend drives.</summary>
        public Camera2D Camera => _camera;

        /// <summary>True from a positive-duration <see cref="To"/> until progress reaches 1.</summary>
        public bool IsBlending { get; private set; }

        /// <summary>Raw progress 0..1 (pre-easing); 1 when complete or after an instant snap.</summary>
        public float Progress { get; private set; }

        /// <summary>
        /// Captures the current camera as the start state and blends to <paramref name="target"/> over
        /// <paramref name="duration"/> seconds with <paramref name="easing"/> (default
        /// <see cref="Easing.SmoothStep"/>). <paramref name="duration"/> &lt;= 0 snaps to the target
        /// immediately (no blend). Calling this mid-blend re-captures the current camera as the new start.
        /// </summary>
        public void To(CameraState target, float duration, Func<float, float>? easing = null)
        {
            _start = CameraState.From(_camera);
            _target = target;
            _easing = easing ?? Easing.SmoothStep;
            _duration = duration;
            _elapsed = 0f;

            if (duration <= 0f)
            {
                target.ApplyTo(_camera);
                Progress = 1f;
                IsBlending = false;
                return;
            }

            Progress = 0f;
            IsBlending = true;
        }

        /// <summary>Advances the active blend by <paramref name="dt"/> seconds, applying the eased
        /// interpolation to the camera. No-op when idle.</summary>
        public void Update(float dt)
        {
            if (!IsBlending) return;

            _elapsed += dt;
            float t = _elapsed / _duration;
            if (t < 0f) t = 0f; else if (t > 1f) t = 1f;
            Progress = t;

            CameraState.Lerp(_start, _target, _easing(t)).ApplyTo(_camera);

            if (t >= 1f) IsBlending = false;
        }

        /// <summary>Cancels an active blend in place: the camera stays where it is and
        /// <see cref="IsBlending"/> becomes false.</summary>
        public void Stop() => IsBlending = false;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraBlendTests.Blend_"`
Expected: PASS (7 tests). Then the whole class:
Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj --filter "FullyQualifiedName~Render2DCameraBlendTests"`
Expected: PASS (14 tests: 3 State + 4 Easing + 7 Blend).

- [ ] **Step 5: Run the full suite for regressions**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (prior 1237 + 14 new = 1251, 6 GPU-skipped).

- [ ] **Step 6: Commit**

```bash
git add KhaozEngine.Render2D/CameraBlend.cs KhaozEngine.Tests/Render2DCameraBlendTests.cs
git commit -m "feat(render2d): CameraBlend — one-shot eased camera transition over Camera2D"
```

---

## Task 4: Release ritual (5.54.0)

Additive change → minor bump. Follows `KhaozEngine/CLAUDE.md` release order.

**Files:**
- Modify: `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md`

- [ ] **Step 1: Bump the 5.x version**

In `Directory.Build.props`, change `<KhaozEngine5xVersion>5.53.0</KhaozEngine5xVersion>` to `5.54.0`.

- [ ] **Step 2: Add the CHANGELOG entry**

In `CHANGELOG.md`, insert directly above the `## 5.53.0 (custom 5.x line)` heading:

```markdown
## 5.54.0 (custom 5.x line)

Eased camera blends on the 5.x engine - a reusable one-shot camera transition primitive, the next slice of
the camera feel layer (and the building block the room/region camera slice will consume).

- **`CameraBlend`** (`KhaozEngine.Render2D`) - transitions a `Camera2D` from its current framing to a target
  over a duration: `To(target, duration, easing)` captures the start, `Update(dt)` advances it, and the
  camera lands exactly on the target at the end. `duration <= 0` snaps instantly; `IsBlending` / `Progress`
  expose state; `Stop()` cancels in place; calling `To` mid-blend cleanly re-targets from the current frame.
- **`CameraState`** (`KhaozEngine.Render2D`) - an immutable framing snapshot (position + zoom + rotation) with
  `From(camera)` / `ApplyTo(camera)` / `Lerp(a, b, t)`. The blend endpoint type, and a reusable camera "setup"
  value.
- **`Easing`** (`KhaozEngine.Render2D`) - pure preset curves (`Linear`, `SmoothStep`, `EaseIn`, `EaseOut`,
  `EaseInOut`), each clamping `t` to `[0,1]`. `CameraBlend` defaults to `SmoothStep`; callers can pass any
  `Func<float,float>`.
```

- [ ] **Step 3: Update the three guard-checked doc declarations**

In `docs/CONSUMERS.md`, change the `**Engine current version:** \`5.53.0\`` line to `5.54.0`.
In `docs/ROADMAP.md`, change the `Current released version: **5.53.0**` line (near the top) to `5.54.0`.
In `README.md`, change every `Version="5.53.0"` in the `<PackageReference>` example block to `5.54.0` (grep `grep -n "5.53.0" README.md` first to find all ~4 lines).

- [ ] **Step 4: Update the ROADMAP camera section**

In `docs/ROADMAP.md`, in the "Camera: first-class follow / scroller camera" section:

(a) Under the `**Shipped:**` list, append:

```markdown
- 5.54.0: `CameraBlend` + `CameraState` + `Easing` (`KhaozEngine.Render2D`) - **eased camera blends**: a
  one-shot, time-based transition that lerps position/zoom/rotation between setups over a duration with a
  preset or custom easing curve (instant snap on duration 0). The primitive room/region cameras hand off with.
```

(b) From the `**Still open**` list, delete the bullet beginning `- Smooth / eased zoom transitions and camera blends` (it spans two lines, ending `...instant snap on respawn / scene load.`). Leave the other still-open bullets (room/region cameras, parallax, screen shake, pixel-perfect snapping if still listed) intact. If the exact wording differs from this, report the discrepancy rather than guessing.

- [ ] **Step 5: Verify the doc-version guard passes**

Run: `bash scripts/check-doc-versions.sh`
Expected: exit 0 (declarations match `<KhaozEngine5xVersion>` = 5.54.0).

- [ ] **Step 6: Test and pack**

Run: `dotnet test KhaozEngine.Tests/KhaozEngine.Tests.csproj`
Expected: PASS (all green).
Run: `dotnet pack -c Release -o ./local-feed`
Expected: builds `KhaozEngine.Render2D.5.54.0.nupkg` (and the other 5.x packages) into `local-feed` (cumulative; do not delete old versions). Confirm with `ls local-feed/KhaozEngine.Render2D.5.54.0.nupkg`. (This worktree's `local-feed` is a symlink to the canonical `~/KhaozEngine/local-feed` — packing here writes to the canonical feed, which is intended.)

- [ ] **Step 7: Commit and tag**

```bash
git add Directory.Build.props CHANGELOG.md docs/CONSUMERS.md docs/ROADMAP.md README.md
git commit -m "render2d(5.54.0): eased camera blends — CameraBlend + CameraState + Easing"
git tag v5.54.0
```

Pushing `main` + the tag happens at branch-finish time, not here.

---

## Self-Review Notes

- **Spec coverage:** `CameraState` From/ApplyTo/Lerp (Task 1), `Easing` 5 presets + clamp (Task 2), `CameraBlend` To/Update/Stop/IsBlending/Progress + instant snap + retarget + determinism + idle no-op (Task 3), release incl. ROADMAP edits (Task 4). All spec sections + test-matrix items mapped.
- **Type consistency:** `CameraState(Vector2, float, float)` + `From(Camera2D)`/`ApplyTo(Camera2D)`/`Lerp(CameraState, CameraState, float)`; `Easing.Linear/SmoothStep/EaseIn/EaseOut/EaseInOut(float)->float`; `CameraBlend(Camera2D)` + `To(CameraState, float, Func<float,float>?)`/`Update(float)`/`Stop()`/`IsBlending`/`Progress`/`Camera`. Names consistent across tasks. `Camera2D.Position/Zoom/Rotation` are public fields.
- **No placeholders:** every code step shows complete code; commands have expected output.
