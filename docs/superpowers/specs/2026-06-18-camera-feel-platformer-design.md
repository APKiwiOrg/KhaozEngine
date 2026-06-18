# Camera feel layer: platformer follow (5.x Render2D)

**Date:** 2026-06-18
**Package:** `KhaozEngine.Render2D` (5.x line — the engine)
**Status:** design approved, ready for implementation plan

## Goal

Bring the camera "feel" layer to the 5.x engine. Today the follow/scroller feel layer
(`CameraFollow`, `CameraController`, gesture core) exists only on the **4.x `KhaozEngine.Graphics`**
line, which is MonoGame-bound (`Microsoft.Xna.Framework`) and frozen-ish. The 5.x
`Render2D.Camera2D` has only the base matrix camera plus `CenterOn` / `Focus` / `ClampPosition` /
`PanByScreenDelta` — no follow layer at all.

This spec is the first slice of the roadmap "camera feel layer" backlog: the **foundation + single-target
platformer feel**. It ports `CameraFollow` to 5.x (`System.Numerics`, no MonoGame) and adds the three
items that make a side-scroller feel good:

1. **Per-axis follow tuning** — decouple X/Y smoothing.
2. **Look-ahead** — lead the camera in the direction the target is moving.
3. **Pixel-perfect snapping** — snap the rendered camera position to the art-pixel grid.

Out of scope (later slices, each its own spec): room/region cameras, eased zoom/position blends between
setups, multi-target framing, parallax layers, screen shake (the last lives in `KhaozEngine.Effects`).

## Approach

Approach A of three considered (see below): enrich the one follow class, keep snapping as a reusable
helper, leave the base `Camera2D` untouched.

- **`CameraFollow`** owns the follow concerns: per-axis stiffness, deadzone, look-ahead, bounds clamp.
  Look-ahead and per-axis stiffness are intrinsic to follow behavior, so they belong in the follow update.
- **`PixelSnap`** is a separate pure value type — snapping is a distinct concern usable by any camera
  (a gesture camera could snap too), so it is not welded into the follow class or the base camera.
- The base `Render2D.Camera2D` stays minimal and unchanged.

Rejected alternatives:
- **B — minimal `CameraFollow` + a separate `ScrollerCamera` wrapper.** Two follow classes with
  overlapping purpose and a "which do I use" choice; per-axis/look-ahead are core follow behavior, so the
  split is arbitrary.
- **C — fold snapping into base `Camera2D`.** Pushes a pixel-art concern into the deliberately minimal
  matrix base that non-pixel games use.

## Components

Two new files in `KhaozEngine.Render2D` and one test file in `KhaozEngine.Tests`. Pure `System.Numerics`,
headless, no `GraphicsDevice`. `Rect` comes from `KhaozEngine.Windowing` (already a `Render2D` dependency,
used by `Camera2D`).

### `CameraFollow`

```csharp
namespace KhaozEngine.Render2D;

public sealed class CameraFollow
{
    public CameraFollow(Camera2D camera);
    public Camera2D Camera { get; }

    // Per-axis smoothing rate (per second). A component <= 0 snaps that axis instantly.
    public Vector2 Stiffness { get; set; } = new(10f, 10f);
    public void SetStiffness(float both);   // convenience: sets both axes

    // Screen-space deadzone (absolute screen coords, same space as Camera2D.WorldToScreen output).
    // null (default) = no deadzone, camera centers on the target.
    public Rect? Deadzone { get; set; } = null;

    // Look-ahead config. default (LeadTime == 0) = disabled.
    public LookAheadSettings LookAhead { get; set; } = default;

    // Pixel-snap config. default (Enabled == false) = disabled.
    public PixelSnap Snap { get; set; } = default;

    // Follow step. velocity drives look-ahead (caller supplies it; the game already has it).
    public void Update(Vector2 target, Vector2 velocity, float dt,
                       int viewportWidth, int viewportHeight, Rect worldBounds);

    // Convenience overload: velocity = Vector2.Zero (look-ahead inert).
    public void Update(Vector2 target, float dt,
                       int viewportWidth, int viewportHeight, Rect worldBounds);

    // Hard-set the camera to a position, bypassing smoothing and clearing the lead offset.
    // For respawn / scene load so the camera does not ease across the level.
    public void Warp(Vector2 position);
}
```

Internal state:
- `_smoothPos` (`Vector2`) — the sub-pixel-accurate camera position the smoothing operates on. Lazy-init
  from `Camera.Position` on first `Update` (tracked by an `_initialized` flag, so a `Warp` or a manual
  `Camera.Position` set before the first frame is respected).
- `_leadOffset` (`Vector2`) — the currently-applied (eased) look-ahead offset.

### `LookAheadSettings`

```csharp
namespace KhaozEngine.Render2D;

// Lead the camera ahead of the target along its velocity.
// Per-frame lead target = clamp(velocity * LeadTime, -MaxDistance .. +MaxDistance), per axis.
// The applied offset eases toward that target at Stiffness so a direction reversal does not snap.
public readonly struct LookAheadSettings
{
    public Vector2 LeadTime;    // seconds of velocity to lead by, per axis. 0 on an axis = no lead there.
    public Vector2 MaxDistance; // clamp on lead magnitude, per axis (world units). Component <= 0 = unclamped.
    public float   Stiffness;   // easing rate of the lead offset (per second). <= 0 = apply instantly.

    public LookAheadSettings(Vector2 leadTime, Vector2 maxDistance, float stiffness);
}
```

`LeadTime` per-axis lets a platformer lead horizontally only (`LeadTime = (0.4f, 0f)`).

### `PixelSnap`

```csharp
namespace KhaozEngine.Render2D;

// Snaps a world position to the art-pixel grid. Reusable by any camera, not just CameraFollow.
public readonly struct PixelSnap
{
    public bool  Enabled;
    public float WorldUnitsPerPixel;   // grid size in world units; Position is rounded to a multiple of this.

    public PixelSnap(float worldUnitsPerPixel);   // sets Enabled = true

    // Rounds each axis to the nearest multiple of WorldUnitsPerPixel. No-op when disabled
    // or WorldUnitsPerPixel <= 0.
    public Vector2 Apply(Vector2 worldPos);
}
```

**Scope caveat (documented honestly):** `PixelSnap` snaps camera *translation* to the art grid, which kills
camera-induced sub-pixel shimmer. Full pixel-perfect rendering also requires integer zoom and a fixed-
resolution render target — that is the game's render-target responsibility, not this layer's. This layer
does the camera half only.

## Data flow (per `Update`)

1. **Deadzone.** `desired = Deadzone is null ? target : HoldWithinDeadzone(target)`. The deadzone path is
   ported verbatim from the 4.x `CameraFollow.ComputeDesired`: project the target to screen space via
   `Camera.WorldToScreen`, measure overflow past each deadzone edge, convert back to world via `Zoom`, add
   to the current position. (5.x `WorldToScreen` takes `viewportWidth, viewportHeight` rather than a
   `Viewport`.)
2. **Look-ahead.** `leadTarget = clamp(velocity * LookAhead.LeadTime, ±LookAhead.MaxDistance)` per axis;
   ease `_leadOffset` toward `leadTarget` by `1 - exp(-LookAhead.Stiffness * dt)` (or set directly when
   `Stiffness <= 0` / `dt <= 0`); `desired += _leadOffset`.
3. **Per-axis smoothing.** For each axis independently:
   `_smoothPos.a += (desired.a - _smoothPos.a) * (1 - exp(-Stiffness.a * dt))`, or
   `_smoothPos.a = desired.a` when `Stiffness.a <= 0` or `dt <= 0`.
4. **Bounds clamp.** `_smoothPos = Camera.ClampPosition(_smoothPos, worldBounds, vw, vh)`.
5. **Output.** `Camera.Position = Snap.Enabled ? Snap.Apply(_smoothPos) : _smoothPos`.

Smoothing always reads and writes `_smoothPos` (the sub-pixel truth); the pixel snap only touches the
rendered `Camera.Position`. So snapping introduces no cumulative drift and no smoothing stutter.

`Warp(position)`: sets `_smoothPos = position`, `_leadOffset = Vector2.Zero`, `_initialized = true`, and
`Camera.Position = Snap.Enabled ? Snap.Apply(position) : position`.

## Testing

All headless, frame-by-frame with explicit `dt`, in the style of the existing 4.x `CameraFollowTests`
(`GameTime` is not needed — these take a `float dt`). New file
`KhaozEngine.Tests/Render2DCameraFollowTests.cs`.

Foundation parity (ported behavior):
- Smoothing converges toward the target over time.
- Frame-rate independence: the same total simulated time reached in different `dt` step sizes lands at the
  same position within a tolerance.
- Deadzone: target moving inside the deadzone holds the camera still; crossing an edge moves the camera
  just enough to put the target back on that edge.
- Bounds clamp keeps the visible rect inside `worldBounds`; on an axis smaller than the view the camera
  centers on that axis.
- Non-positive stiffness snaps instantly.

New behavior:
- **Per-axis:** `Stiffness = (k>0, 0)` eases X while snapping Y, independently; and the mirror.
- **Look-ahead:** positive velocity leads the camera ahead of the target along the velocity direction;
  the lead is clamped at `MaxDistance`; the applied lead eases (a direction reversal does not jump the
  offset in one frame); per-axis `LeadTime = (k, 0)` leaves Y on the target; `default` settings produce no
  offset.
- **Pixel snap:** `Camera.Position` lands on the grid; running many frames toward a fixed target shows no
  cumulative drift and a final position equal to the snapped target; disabled snap leaves the position
  exact.
- **`Warp`:** hard-sets the position with no easing and clears any accumulated lead.

## Shipping (engine release ritual)

Additive change → minor bump. Next 5.x version is **5.52.0**.

1. Bump `<KhaozEngine5xVersion>` in `Directory.Build.props` to `5.52.0`.
2. Add a newest-first `CHANGELOG.md` entry (same commit as the bump).
3. Update the three doc-version declarations the guard checks: `docs/CONSUMERS.md` "Engine current
   version", `docs/ROADMAP.md` "Current released version", and the `README.md` `<PackageReference>`
   example.
4. In `docs/ROADMAP.md`, move per-axis follow tuning, look-ahead, and pixel-perfect snapping from the
   camera section's "Still open" list to "Shipped"; also fix the stale 5.x table version (`5.47.0` →
   current) while there.
5. `dotnet pack -c Release -o ./local-feed` (cumulative; do not delete old versions).
6. Commit, `git tag v5.52.0`, push `main` + the tag (CI publishes to GitHub Packages on `v*`).

No consumer adopts immediately; the motivating platformer is a future 5.x game. Hardpoint (3D iso) does not
use a 2D follow camera, so this is non-breaking for it.

## Files

- `KhaozEngine.Render2D/CameraFollow.cs` (new)
- `KhaozEngine.Render2D/LookAheadSettings.cs` (new) — or co-located with `CameraFollow`
- `KhaozEngine.Render2D/PixelSnap.cs` (new)
- `KhaozEngine.Tests/Render2DCameraFollowTests.cs` (new)
- `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` (release)
