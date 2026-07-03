# Camera feel layer: room / region cameras (5.x Render2D)

**Date:** 2026-06-18
**Package:** `KhaozEngine.Render2D` (5.x line — the engine)
**Status:** design approved, ready for implementation plan

## Goal

Fourth slice of the camera feel layer: **room / region cameras** (Metroidvania-style). The world is divided
into rectangular regions; the camera follows the target confined to the region it is in, and when the target
crosses into a new region the camera reframes with an eased hand-off, then resumes following in the new
region.

This is a turnkey controller that composes the pieces already shipped: `CameraFollow` (5.52.0) for the
in-room follow feel and `CameraBlend` (5.54.0) for the region hand-off.

Out of scope (later slices): screen shake, parallax.

## Approach

A turnkey `RoomCamera` controller + a `CameraRoom` region value, plus one small additive overload on the
base `Camera2D`.

- **`CameraRoom`** — a region: a world rect (both the trigger area and the camera confinement) plus an
  optional per-room zoom override.
- **`RoomCamera`** — owns the `Camera2D`, an internal `CameraFollow`, and an internal `CameraBlend`. Each
  frame it resolves the active region, hands off (blends) on a region change, and otherwise follows within
  the active region. It exposes the internal `CameraFollow` so the game tunes in-room feel.
- **`Camera2D.ClampPosition` explicit-zoom overload** — `ClampPosition(desired, bounds, vw, vh, zoom)`; the
  existing instance method delegates with `this.Zoom`. The room hand-off must clamp a position at the
  *target* room's zoom before the camera has eased to it, so the clamp needs an explicit zoom.

Rejected: a "region resolver only" unit that leaves the follow/blend wiring to the game (the user chose the
turnkey controller); modelling rooms as full `CameraState` overrides (chose bounds + optional zoom — the
common case without over-modelling).

## Components (in `KhaozEngine.Render2D`, pure System.Numerics, headless)

`Rect` is `KhaozEngine.Windowing.Rect`. `Camera2D` has public fields `Position`, `Zoom`, `Rotation`.

### `Camera2D` overload (additive)

```csharp
// New: clamp at an explicit zoom (room hand-off needs the target room's zoom, not the live one).
public Vector2 ClampPosition(Vector2 desired, Rect worldBounds, int viewportWidth, int viewportHeight, float zoom)
{
    float halfW = viewportWidth / (2f * zoom);
    float halfH = viewportHeight / (2f * zoom);
    float x = worldBounds.Width  >= 2f * halfW ? Math.Clamp(desired.X, worldBounds.X + halfW, worldBounds.Right  - halfW) : worldBounds.X + worldBounds.Width  / 2f;
    float y = worldBounds.Height >= 2f * halfH ? Math.Clamp(desired.Y, worldBounds.Y + halfH, worldBounds.Bottom - halfH) : worldBounds.Y + worldBounds.Height / 2f;
    return new Vector2(x, y);
}

// Existing method now delegates (behavior unchanged):
public Vector2 ClampPosition(Vector2 desired, Rect worldBounds, int viewportWidth, int viewportHeight)
    => ClampPosition(desired, worldBounds, viewportWidth, viewportHeight, Zoom);
```

### `CameraRoom`

```csharp
namespace KhaozEngine.Render2D;

/// A camera region: a world rect that is both the trigger area (the target must be inside for the room to be
/// active) and the camera confinement (in-room follow clamps to it), plus an optional zoom override.
public readonly struct CameraRoom
{
    public readonly Rect   Bounds;
    public readonly float? Zoom;   // null = keep the current zoom on entry

    public CameraRoom(Rect bounds, float? zoom = null);

    public bool Contains(Vector2 worldPoint);   // Bounds.Contains(worldPoint)
}
```

### `RoomCamera`

```csharp
namespace KhaozEngine.Render2D;

public sealed class RoomCamera
{
    public RoomCamera(Camera2D camera, IReadOnlyList<CameraRoom> rooms);

    public Camera2D     Camera { get; }
    public CameraFollow Follow { get; }   // exposed: tune in-room feel (Stiffness/Deadzone/LookAhead/Snap)
    public int  ActiveRoomIndex { get; }  // -1 until the first room is acquired
    public bool IsTransitioning { get; }  // true while a hand-off blend is running

    public float BlendDuration { get; set; } = 0.4f;
    public Func<float, float> BlendEasing { get; set; } = Easing.SmoothStep;

    /// Resolves the active room, hands off on a region change, otherwise follows within the active room.
    public void Update(Vector2 target, Vector2 velocity, float dt, int viewportWidth, int viewportHeight);
    public void Update(Vector2 target, float dt, int viewportWidth, int viewportHeight);   // velocity = 0

    /// Snaps instantly to the room containing target (no blend); sets zoom and the follow position.
    public void Warp(Vector2 target, int viewportWidth, int viewportHeight);
}
```

## Data flow (per `Update`)

Let `vw`/`vh` be the viewport. The controller holds `_activeIndex` (start -1) and the internal `Follow` and
`Blend`.

1. **Resolve.** `resolved` = the lowest-index room whose `Contains(target)` is true. If none contains the
   target, `resolved = _activeIndex` (hold the current room). If `_activeIndex == -1` and none contains the
   target, return (no room applies yet).
2. **Room change** (`resolved != _activeIndex`):
   - First acquisition (`_activeIndex == -1`): snap. `desiredZoom = room.Zoom ?? Camera.Zoom`;
     `Camera.Zoom = desiredZoom`; `Follow.Warp(Camera.ClampPosition(target, room.Bounds, vw, vh, desiredZoom))`.
     `_activeIndex = resolved`. (No blend.)
   - Otherwise: hand off. `desiredZoom = room.Zoom ?? Camera.Zoom`;
     `targetPos = Camera.ClampPosition(target, room.Bounds, vw, vh, desiredZoom)`;
     `Blend.To(new CameraState(targetPos, desiredZoom, Camera.Rotation), BlendDuration, BlendEasing)`;
     `_activeIndex = resolved`; `IsTransitioning = true`.
3. **Transitioning.** If `Blend.IsBlending`: `Blend.Update(dt)`; when it finishes, `Follow.Warp(Camera.Position)`
   (so follow resumes from the blended frame) and `IsTransitioning = false`. Following is suspended this frame.
4. **Settled.** Else `Follow.Update(target, velocity, dt, vw, vh, rooms[_activeIndex].Bounds)` — in-room follow
   clamped to the active room. Zoom is unchanged by follow, so it stays at the room's zoom.

`Warp(target, vw, vh)`: resolve the room containing target (lowest index; if none, no-op), then do the
first-acquisition snap unconditionally (set zoom, `Follow.Warp(clamped)`, `_activeIndex = resolved`,
`IsTransitioning = false`).

Notes:
- The hand-off blend captures its target once (it does not re-aim at the moving target during the ~0.4s
  blend); `Follow` catches up to the live target after the blend. Documented.
- `RoomCamera` constructs its internal `CameraFollow(camera)` and `CameraBlend(camera)` over the same camera
  in the constructor; both drive `Camera.Position`/`Zoom`, and only one is active at a time (blend during a
  transition, follow when settled), so they never fight.

## Testing (headless, frame-by-frame)

New file `KhaozEngine.Tests/Render2DRoomCameraTests.cs`.

`Camera2D` overload:
- `ClampPosition(..., zoom)` matches the existing instance method when `zoom == Camera.Zoom`; clamps tighter
  at higher zoom (smaller half-view) than at lower zoom.

`CameraRoom`:
- `Contains` true inside, false outside; `Zoom` defaults to null.

`RoomCamera`:
- First `Update` acquires the room containing the target (`ActiveRoomIndex` set, no transition) and applies
  the room's zoom.
- Two adjacent rooms with different zoom: target starts in A (index 0, A's zoom); move target into B →
  `IsTransitioning` true; advance frames past `BlendDuration` → `IsTransitioning` false, `ActiveRoomIndex` 1,
  `Camera.Zoom` == B's zoom; after settling the target is inside the viewport.
- In-room follow clamps to the active room bounds (target pushed past a room edge → camera clamped, room edge
  visible).
- Room with null `Zoom` → entering it keeps the prior zoom.
- Target in no room → `ActiveRoomIndex` unchanged (holds).
- Overlapping rooms → lowest index wins.
- `Warp` snaps to the target's room instantly (`ActiveRoomIndex` set, `IsTransitioning` false, zoom applied,
  no blend frames needed).
- Exposed `Follow`: setting `Follow.Stiffness` changes the in-room ease (a stiffer follow is closer to the
  target after one frame than a slacker one).

## Shipping (engine release ritual)

Additive → minor. Next 5.x version is **5.55.0**.

1. Bump `<KhaozEngineVersion>` to `5.55.0`.
2. Newest-first `CHANGELOG.md` entry (same commit).
3. Update the three guard-checked declarations (CONSUMERS, ROADMAP, README package refs).
4. In `docs/ROADMAP.md` camera section, move the room/region cameras item from "Still open" to "Shipped".
5. `dotnet pack -c Release -o ./local-feed` (cumulative).
6. Commit, `git tag v5.55.0`. (Push at branch-finish.)

No consumer adopts immediately; the planned platformer / Metroidvania is the first user.

## Files

- `KhaozEngine.Render2D/Camera2D.cs` (modify: add the explicit-zoom `ClampPosition` overload + delegate)
- `KhaozEngine.Render2D/CameraRoom.cs` (new)
- `KhaozEngine.Render2D/RoomCamera.cs` (new)
- `KhaozEngine.Tests/Render2DRoomCameraTests.cs` (new)
- `Directory.Build.props`, `CHANGELOG.md`, `docs/CONSUMERS.md`, `docs/ROADMAP.md`, `README.md` (release)
