# KhaozEngine.Graphics — Camera2D (Batch 2, item 12)

Promote SpaceGame's generic matrix camera into a new engine package. Base camera only:
position/zoom/rotation to view matrix, plus screen<->world helpers and an optional
world-bounds clamp. Game-specific follow-cam logic (Nullwake) stays game-side and composes
this base later.

## Scope

In scope:
- New package `KhaozEngine.Graphics` with one public type, `Camera2D`.
- Headless, device-free matrix math + screen<->world round-trip.
- Optional pure `ClampPosition` helper.
- Wiring: add the project to `KhaozEngine.slnx` and a `ProjectReference` from
  `KhaozEngine.Tests`.

Out of scope (do NOT promote):
- Nullwake `Camera.cs` follow logic (coupled to `OreField.FieldCenter`, pan snap-back,
  zoom lerp, `FollowSpeed`, `IsStatic`). Layers on top of this base via composition later.
- SpaceGame `WorldToScreenUtil` and `SpaceForgeScreen` local helpers.
- Version bump, CHANGELOG entry, `dotnet pack` into the shared feed. The coordinating chat
  owns the batched 3.3.0 release.

## Source (re-verified)

`~/SpaceGame/SpaceGame/SpaceGame.Core/Camera2D.cs` — `internal sealed class Camera2D`:
- State: `Position` (get/set, default `Zero`), `Zoom` (get/set, default `1`). No rotation.
- Holds a `GraphicsDevice`, reads `Viewport` off it on each `GetViewMatrix()`.
- `GetViewMatrix()`, `WorldToScreen(Vector2)`, `ScreenToWorld(Vector2)`.

Consumers call: `new Camera2D(GraphicsDevice)`, `GetViewMatrix()`, `WorldToScreen`,
`ScreenToWorld`, `Position`, `Zoom`. SpaceGame adopts the new API later (not this item).

The promoted base differs from the source in two deliberate ways, both decided with the
coordinator: it adds `Rotation`, and it drops the `GraphicsDevice` dependency in favour of a
plain `Viewport` so the math is headless-testable with no graphics context.

## Package

- `KhaozEngine.Graphics`, net10.0, `MonoGame.Framework.DesktopGL` `3.8.*`, README packed.
- csproj mirrors `KhaozEngine.Time` (PackageId, Description, PackageReadmeFile, MonoGame
  PackageReference, README `None` include). No version in the csproj — inherited from
  `Directory.Build.props` (untouched).
- Repo has `ImplicitUsings` disabled: explicit `using Microsoft.Xna.Framework;` (Matrix,
  Vector2, Rectangle, MathHelper) and `using Microsoft.Xna.Framework.Graphics;` (Viewport).

## API: `public sealed class Camera2D`

State (all public get/set):

| Member | Type | Default | Meaning |
| --- | --- | --- | --- |
| `Position` | `Vector2` | `Zero` | World point shown at screen center. Publicly settable so a future follow-cam can drive it each frame. |
| `Zoom` | `float` | `1f` | Uniform scale. >1 zooms in. Must be `> 0`: `Zoom <= 0` makes the scale term singular, so `Matrix.Invert` (used by `ScreenToWorld`) yields NaN. Documented on the property as an invariant; not clamped or thrown in the setter (a base type should not silently rewrite caller values, and a guard can be added additively later if a consumer needs it). |
| `Rotation` | `float` | `0f` | Camera roll in radians, CCW. |
| `Viewport` | `Viewport` | `default` | Convenience target for the no-arg overloads. |

`sealed` — game follow-cams compose (own a `Camera2D`, drive its `Position`/`Zoom`/
`Rotation`, read its matrix), they do not subclass.

### View matrix

`Matrix GetViewMatrix(Viewport viewport)`:

```
T(-Position) * R(Rotation) * S(Zoom, Zoom, 1) * T(viewport.Width/2, viewport.Height/2)
```

(XNA row-vector convention: composition reads left-to-right in application order.) Recenter
on `Position`, rotate, scale, then translate to screen center — so the world rotates and
scales about `Position`, which lands at the viewport center. At `Position=Zero, Zoom=1,
Rotation=0` this maps world origin to `(W/2, H/2)`.

### Core methods (headless, explicit viewport)

- `Matrix GetViewMatrix(Viewport viewport)`
- `Vector2 WorldToScreen(Vector2 world, Viewport viewport)` -> `Vector2.Transform(world, GetViewMatrix(viewport))`
- `Vector2 ScreenToWorld(Vector2 screen, Viewport viewport)` -> `Vector2.Transform(screen, Matrix.Invert(GetViewMatrix(viewport)))`
- `Vector2 ClampPosition(Vector2 desired, Rectangle worldBounds, Viewport viewport)`

These take the viewport as a parameter and touch no device state, so tests construct
`new Viewport(0, 0, w, h)` directly.

### ClampPosition semantics

Half-extents of the visible world rect: `halfW = viewport.Width / (2 * Zoom)`,
`halfH = viewport.Height / (2 * Zoom)`. Per axis:

- If the world is at least as wide as the view (`worldBounds.Width >= 2*halfW`): clamp
  `desired.X` to `[worldBounds.Left + halfW, worldBounds.Right - halfW]`.
- Else (world narrower than view on that axis): return the world center on that axis
  (`worldBounds.Left + worldBounds.Width / 2f`).

Same for Y with `halfH` / `Top` / `Bottom` / `Height`. Zoom- and viewport-aware.

Rotation handling: the clamp uses the **axis-aligned** visible rect and ignores `Rotation`.
Exact when `Rotation == 0` (the platformer/scroller case). With non-zero rotation the true
visible area is a rotated quad, so the clamp is approximate; documented on the method, not
engineered for rotation-exactness (YAGNI).

`ClampPosition` returns a value and does not mutate `Position` — the caller assigns it (or
not) at its discretion.

### Turn-key no-arg overloads

Delegate to the core using the `Viewport` property:

- `Matrix GetViewMatrix()` -> `GetViewMatrix(Viewport)`
- `Vector2 WorldToScreen(Vector2 world)` -> `WorldToScreen(world, Viewport)`
- `Vector2 ScreenToWorld(Vector2 screen)` -> `ScreenToWorld(screen, Viewport)`

Doc-comment: single-viewport games set `camera.Viewport` once and refresh it on
`ClientSizeChanged`; split-screen / minimap / test code uses the per-call overloads and
ignores the property. The property defaults to `default(Viewport)` (zero size) — the no-arg
overloads are only meaningful once it is set.

## Tests (`KhaozEngine.Tests/CameraTests.cs`, xUnit, headless)

All use `new Viewport(0, 0, 800, 600)`; no `GraphicsDevice`. Float comparisons with a small
tolerance (helper or `Assert.Equal(expected, actual, precision)` on components).

1. Center map: `Position=Zero, Zoom=1, Rotation=0` -> `WorldToScreen(Zero) == (400, 300)`.
1b. Defining invariant: for a non-zero `Position` and arbitrary `Zoom`/`Rotation`,
   `WorldToScreen(Position) == (400, 300)`. Directly pins "Position always lands at screen
   center" (the recenter term) rather than relying on the round-trip to cover it indirectly.
2. Round-trip: `ScreenToWorld(WorldToScreen(p)) ~= p` over several `p`, across non-default
   Position / Zoom / Rotation combinations.
3. Zoom scale: `Position=Zero, Zoom=2`, world `(10,0)` -> `(400,300) + (20,0) = (420,300)`.
4. Rotation: `Rotation = pi/2`, a world +X offset from `Position` maps to a screen offset
   along ∓Y — pins the rotation direction and matrix fold.
5. No-arg parity: with `camera.Viewport` set, `GetViewMatrix() == GetViewMatrix(vp)` and the
   `WorldToScreen`/`ScreenToWorld` no-arg results equal their per-call equivalents.
6. Clamp, world larger than view: `desired` past an edge -> `worldBounds.Left + halfW`
   (and the far-edge case), at a chosen Zoom.
7. Clamp, world smaller than view on an axis: result is centered on that axis.
8. Clamp zoom-awareness: same `desired` and bounds, two `Zoom` values -> different clamped
   results (larger `halfW` at lower zoom pulls the clamp inward).

## Files

Added:
- `KhaozEngine.Graphics/KhaozEngine.Graphics.csproj`
- `KhaozEngine.Graphics/Camera2D.cs`
- `KhaozEngine.Graphics/README.md`
- `KhaozEngine.Tests/CameraTests.cs`

Edited (one line each):
- `KhaozEngine.slnx` — add the project entry.
- `KhaozEngine.Tests/KhaozEngine.Tests.csproj` — add the `ProjectReference`.

## Open questions for coordinator

- Package name `KhaozEngine.Graphics` confirmed (vs Camera/Rendering).
- Public API shape (per-call core + no-arg sugar + `ClampPosition`) confirmed; another item
  or game depending on this should target these signatures.
- No shared-package edits beyond the two one-line wiring additions above.
