# Camera feel layer: eased camera blends (5.x Render2D)

**Date:** 2026-06-18
**Package:** `KhaozEngine.Render2D` (5.x line — the engine)
**Status:** design approved, ready for implementation plan

## Goal

Third slice of the roadmap "camera feel layer" backlog. A reusable **eased camera blend** primitive: a
one-shot, time-based transition that lerps a `Camera2D` from its current state to a target state
(position + zoom + rotation) over a duration with an easing curve. This is the building block the next slice
(room/region cameras) consumes to hand the camera off between areas; it is also useful standalone for any
scripted camera move.

This is distinct from the continuous, rate-based exponential smoothing used by `CameraFollow` /
`GroupCamera` (`1 - exp(-rate*dt)`). A blend has a definite start, end, and duration, and reshapes progress
with a curve.

Out of scope (later slices): room/region cameras (consume this), screen shake, parallax.

## Approach

Three small units in `KhaozEngine.Render2D`:

- **`CameraState`** — an immutable snapshot of a camera's framing (position, zoom, rotation). It is both the
  blend endpoint type and the "setup" type the room-cameras slice will store per region.
- **`Easing`** — pure preset curve functions (`float -> float` over `[0,1]`). Broadly reusable; a confirmed
  search found no existing easing helper in the engine.
- **`CameraBlend`** — the stateful driver: captures a start state, advances elapsed time each `Update`, and
  applies `Lerp(start, target, easing(t))` to the camera until it reaches the target.

Rejected alternatives:
- **One monolithic blend class** with inline lerp + an easing switch. `CameraState` is wanted by the
  room-cameras slice and `Easing` is generally reusable, so both are separated.
- **Blend state on `Camera2D`.** `Camera2D` is the pure matrix base; it holds no per-frame transition state.

## Components (all in `KhaozEngine.Render2D`, pure System.Numerics, headless)

`Camera2D` has public fields `Vector2 Position`, `float Zoom`, `float Rotation`.

### `CameraState`

```csharp
namespace KhaozEngine.Render2D;

/// Immutable snapshot of a camera's framing: where it looks, how far in, and its roll.
public readonly struct CameraState
{
    public readonly Vector2 Position;
    public readonly float   Zoom;
    public readonly float   Rotation;

    public CameraState(Vector2 position, float zoom, float rotation);

    /// Snapshot the camera's current Position/Zoom/Rotation.
    public static CameraState From(Camera2D camera);

    /// Write this state onto the camera.
    public void ApplyTo(Camera2D camera);

    /// Per-field linear interpolation (Position via Vector2.Lerp; Zoom/Rotation scalar lerp).
    public static CameraState Lerp(CameraState a, CameraState b, float t);
}
```

`Lerp` interpolates rotation **linearly** (no shortest-arc wrap). Camera blends use small, sane angles; a
game wanting a 350°->10° move provides sane targets. Documented as a known limitation.

### `Easing`

```csharp
namespace KhaozEngine.Render2D;

/// Pure easing curves: each reshapes progress t (clamped to [0,1]) and returns the eased value in [0,1].
public static class Easing
{
    public static float Linear(float t);      // t
    public static float SmoothStep(float t);  // t*t*(3 - 2t)  — the CameraBlend default
    public static float EaseIn(float t);      // t*t
    public static float EaseOut(float t);     // t*(2 - t)
    public static float EaseInOut(float t);   // t<0.5 ? 2t^2 : 1 - 2(1-t)^2
}
```

Every function clamps its input to `[0,1]` first, so `f(0)=0` and `f(1)=1` hold at and beyond the ends.

### `CameraBlend`

```csharp
namespace KhaozEngine.Render2D;

/// Drives a one-shot, time-based transition of a Camera2D from its current state to a target state.
public sealed class CameraBlend
{
    public CameraBlend(Camera2D camera);
    public Camera2D Camera { get; }

    /// True from a positive-duration To() until t reaches 1.
    public bool IsBlending { get; }

    /// Raw progress 0..1 (pre-easing); 0 when idle, 1 when complete.
    public float Progress { get; }

    /// Captures the current camera as the start state and blends to `target` over `duration` seconds with
    /// `easing` (default Easing.SmoothStep). duration <= 0 snaps to target immediately (no blend). Calling
    /// To() mid-blend re-captures the current (mid-blend) camera as the new start.
    public void To(CameraState target, float duration, Func<float, float>? easing = null);

    /// Advances the active blend by dt seconds, applying Lerp(start, target, easing(t)). No-op when idle.
    public void Update(float dt);

    /// Cancels an active blend in place: IsBlending becomes false, the camera stays where it is.
    public void Stop();
}
```

## Data flow

`To(target, duration, easing)`:
1. `_start = CameraState.From(Camera)`, `_target = target`, `_easing = easing ?? Easing.SmoothStep`,
   `_duration = duration`, `_elapsed = 0`.
2. If `duration <= 0`: `target.ApplyTo(Camera)`, `_progress = 1`, `IsBlending = false`. Done.
3. Else `IsBlending = true`, `_progress = 0`.

`Update(dt)`:
1. If not `IsBlending`, return.
2. `_elapsed += dt`; `t = Clamp(_elapsed / _duration, 0, 1)`; `_progress = t`.
3. `CameraState.Lerp(_start, _target, _easing(t)).ApplyTo(Camera)`.
4. If `t >= 1`: `IsBlending = false` (the camera now sits exactly on `_target`, since `easing(1)=1`).

`Stop()`: `IsBlending = false` (leaves `Camera` and `_progress` as they are).

## Testing (headless, frame-by-frame with explicit `dt`)

New file `KhaozEngine.Tests/Render2DCameraBlendTests.cs`.

`Easing`:
- Each preset: `f(0)=0`, `f(1)=1`; inputs below 0 / above 1 clamp to those endpoints.
- Shape checks: `SmoothStep(0.5)=0.5`, `EaseIn(0.5)=0.25`, `EaseOut(0.5)=0.75`, `EaseInOut(0.5)=0.5`,
  `Linear(0.3)=0.3`.
- Monotonic non-decreasing across a sampled range.

`CameraState`:
- `From` captures Position/Zoom/Rotation; `ApplyTo` writes them back (round-trip).
- `Lerp` at `t=0` = a, `t=1` = b, `t=0.5` = per-field midpoint.

`CameraBlend`:
- `To` + enough `Update`s to exceed duration → camera equals target exactly; `IsBlending` true mid, false
  after; `Progress` reaches 1.
- Linear easing, half the duration → camera at the per-field midpoint of start/target.
- `duration <= 0` → camera snaps to target in `To` itself, `IsBlending` false, no `Update` needed.
- Determinism on elapsed time: one `Update(D)` vs N `Update(D/N)` reach the same camera state.
- Mid-blend `To()` re-captures: start a blend, half-advance, retarget; the new blend eases from the
  mid-blend position (not the original start).
- `Stop()` mid-blend: `IsBlending` false, camera unchanged by subsequent `Update`s.
- `Update` when idle is a no-op.

## Shipping (engine release ritual)

Additive → minor. Next 5.x version is **5.54.0**.

1. Bump `<KhaozEngine5xVersion>` to `5.54.0`.
2. Newest-first `CHANGELOG.md` entry (same commit).
3. Update the three guard-checked declarations (CONSUMERS "Engine current version", ROADMAP "Current
   released version", README `<PackageReference>` examples).
4. In `docs/ROADMAP.md` camera section, move the eased zoom/position blend item from "Still open" to
   "Shipped".
5. `dotnet pack -c Release -o ./local-feed` (cumulative).
6. Commit, `git tag v5.54.0`. (Push at branch-finish.)

No consumer adopts immediately; the room-cameras slice (next) is the first user.

## Files

- `KhaozEngine.Render2D/CameraState.cs` (new)
- `KhaozEngine.Render2D/Easing.cs` (new)
- `KhaozEngine.Render2D/CameraBlend.cs` (new)
- `KhaozEngine.Tests/Render2DCameraBlendTests.cs` (new)
- `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` (release)
