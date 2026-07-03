# Camera feel layer: multi-target framing (5.x Render2D)

**Date:** 2026-06-18
**Package:** `KhaozEngine.Render2D` (5.x line — the engine)
**Status:** design approved, ready for implementation plan

## Goal

Second slice of the roadmap "camera feel layer" backlog (after the platformer follow shipped in 5.52.0):
**multi-target framing** for co-op / shared-screen games. Automatically position and zoom a `Camera2D` to
keep N targets on screen, with smoothing on both position and zoom.

The existing `CameraFollow` follows a single point and eases position only (no zoom control), so framing a
group needs new code regardless. The base `Camera2D.Focus(rect, ...)` already does instant contain-fit
framing of one rect; this slice builds the group bounding-box + smoothed driver on top of that idea.

Out of scope (later slices): eased camera blends between setups, room/region cameras, screen shake,
parallax.

## Approach

`GroupCamera` driver + pure `CameraFraming` helper — parallels how `CameraFollow` sits on `Camera2D`.

- **`CameraFraming`** is a pure static helper: bounding box of the targets and the position+zoom that frames
  it. No easing, no state — trivially testable.
- **`GroupCamera`** is the stateful driver: each frame it asks `CameraFraming` for the desired position+zoom
  and eases the camera toward it (frame-rate-independent), then clamps to world bounds.

Rejected alternatives:
- **Fold a target-list overload into `CameraFollow`.** `CameraFollow` is single-target and position-only;
  adding group zoom control muddies its responsibility.
- **Put easing state on `Camera2D`.** `Camera2D` is the pure matrix base; it must not hold per-frame easing
  state.

## Components (both in `KhaozEngine.Render2D`, pure System.Numerics, headless)

`Rect` is `KhaozEngine.Windowing.Rect` (already a `Render2D` dependency). `Camera2D` provides
`Position`, `Zoom`, and `ClampPosition(desired, Rect worldBounds, int vw, int vh)`.

### `CameraFraming` (pure static)

```csharp
namespace KhaozEngine.Render2D;

public static class CameraFraming
{
    /// Tight axis-aligned bounding box of the targets, expanded by paddingFraction on each side and to at
    /// least minViewSize (centered). Empty list throws ArgumentException (callers guard for empty before
    /// calling; GroupCamera holds on empty without calling this).
    public static Rect Bounds(IReadOnlyList<Vector2> targets, float paddingFraction, Vector2 minViewSize);

    /// Position + zoom that frames `bounds` in the viewport: Position = bounds center,
    /// Zoom = clamp(min(vw / bounds.Width, vh / bounds.Height), minZoom, maxZoom).
    public static (Vector2 Position, float Zoom) Solve(Rect bounds, int vw, int vh, float minZoom, float maxZoom);
}
```

`Bounds` detail: compute min/max X and Y over the points → raw AABB. Expand each side by
`paddingFraction * extent` (so total width becomes `width * (1 + 2*paddingFraction)`), matching the padding
convention in `Camera2D.Focus`. Then, per axis, if the padded extent is below `minViewSize.{X,Y}`, grow it
to `minViewSize` keeping the same center. Guards: `Width`/`Height` floored at a tiny epsilon so `Solve` never
divides by zero even if `minViewSize` is zero.

### `GroupCamera` (stateful driver)

```csharp
namespace KhaozEngine.Render2D;

public sealed class GroupCamera
{
    public GroupCamera(Camera2D camera);
    public Camera2D Camera { get; }

    public float   Stiffness     { get; set; } = 8f;            // position ease rate (per sec); <= 0 snaps
    public float   ZoomStiffness { get; set; } = 8f;            // zoom ease rate (per sec); <= 0 snaps
    public float   PaddingFraction { get; set; } = 0.15f;       // margin around the targets
    public Vector2 MinViewSize   { get; set; } = new(1f, 1f);   // floor on framed extent (no extreme zoom on cluster)
    public float   MinZoom       { get; set; } = 0.0001f;
    public float   MaxZoom       { get; set; } = float.MaxValue;

    /// Eases toward the framing of `targets`, then clamps position to `worldBounds`.
    /// Empty `targets` holds the current view (no-op).
    public void Update(IReadOnlyList<Vector2> targets, float dt, int vw, int vh, Rect worldBounds);

    /// Snaps directly to the framing of `targets` (no easing), then clamps. Empty = no-op.
    public void Warp(IReadOnlyList<Vector2> targets, int vw, int vh, Rect worldBounds);
}
```

## Data flow (per `Update`)

1. If `targets.Count == 0` → return (hold the current view).
2. `bounds = CameraFraming.Bounds(targets, PaddingFraction, MinViewSize)`.
3. `(desiredPos, desiredZoom) = CameraFraming.Solve(bounds, vw, vh, MinZoom, MaxZoom)`.
4. Ease zoom: `Camera.Zoom = Ease(Camera.Zoom, desiredZoom, ZoomStiffness, dt)`, then ease position:
   `Camera.Position = Ease(Camera.Position, desiredPos, Stiffness, dt)`, where `Ease` is the shared
   `1 - exp(-rate*dt)` step (snap when `rate <= 0` or `dt <= 0`).
5. `Camera.Position = Camera.ClampPosition(Camera.Position, worldBounds, vw, vh)` — uses the just-eased zoom.

`Warp`: steps 1-3, then assign `desiredZoom` and `desiredPos` directly, then the clamp. No easing.

Zoom is eased before position so the bounds clamp in step 5 uses the new zoom (the half-view size depends on
zoom). `GroupCamera` keeps no sub-pixel state — it eases `Camera.Position`/`Camera.Zoom` in place (pixel-snap
is a `CameraFollow` concern, not relevant to group framing). The shared exponential-ease helper may be a small
private static on `GroupCamera` (the existing one lives inside `CameraFollow` and is private there; duplicating
a 3-line frame-rate-independent ease is acceptable and avoids widening `CameraFollow`'s surface).

## Testing (headless, frame-by-frame with explicit `dt`)

New file `KhaozEngine.Tests/Render2DGroupCameraTests.cs`.

`CameraFraming`:
- `Bounds` is the tight AABB of N points; `paddingFraction` expands each side symmetrically; `MinViewSize`
  floors a clustered or single-point set (center preserved).
- `Solve`: `Position` = bounds center; `Zoom` = `clamp(min(vw/w, vh/h), min, max)`. Tiny/zero-size bounds do
  not divide by zero.

`GroupCamera`:
- Two spread targets → after framing, both points map inside the viewport (use `Camera.WorldToScreen`).
- Targets separating → zoom eases out over frames; converging → zoom eases in.
- Frame-rate independence: same total time in different `dt` steps → same end position and zoom within a
  tolerance.
- Bounds clamp keeps the visible rect inside `worldBounds`.
- Empty `targets` → camera unchanged.
- `Warp` snaps to the framing in one call (position = bounds center, zoom = solved zoom), no easing.
- Non-positive `Stiffness` / `ZoomStiffness` snaps that quantity.

## Shipping (engine release ritual)

Additive → minor. Next 5.x version is **5.53.0**.

1. Bump `<KhaozEngineVersion>` to `5.53.0`.
2. Newest-first `CHANGELOG.md` entry (same commit).
3. Update the three guard-checked declarations (`docs/CONSUMERS.md` "Engine current version",
   `docs/ROADMAP.md` "Current released version", `README.md` `<PackageReference>` examples).
4. In `docs/ROADMAP.md` camera section, move "Multi-target framing" from "Still open" to "Shipped".
5. `dotnet pack -c Release -o ./local-feed` (cumulative).
6. Commit, `git tag v5.53.0`. (Push happens at branch-finish.)

No consumer adopts immediately (no co-op game on 5.x yet).

## Files

- `KhaozEngine.Render2D/CameraFraming.cs` (new)
- `KhaozEngine.Render2D/GroupCamera.cs` (new)
- `KhaozEngine.Tests/Render2DGroupCameraTests.cs` (new)
- `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` (release)
