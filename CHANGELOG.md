# Changelog

All notable changes to KhaozEngine. Versions are shared across the four packages.

## KhaozEngine.Ecs 1.0.0

- Rewrite as a struct-based archetype ECS: versioned `Entity`, archetype/column storage, `ref`
  `Get<T>`, `With`/`Without` queries, `ForEach` arities 1-8, `EntityCommandBuffer`, typed `Resources`.
- Breaking vs 0.1.x: components are now `struct : IComponent`; `Get<T>` returns `ref T`; the
  `List<Entity> Query<T>()` overloads are replaced by `ForEach`. Versioned independently of the
  other KhaozEngine packages (which stay on 0.2.x).

## 0.2.0

- `InputManager`: middle/right mouse-button edges (`IsMiddle/RightDown/JustPressed/JustReleased`).
- `InputManager.Touches` — active touches in virtual coordinates with stable ids (`TouchPoint.Id`).
- `InputManager.TryGetPinch(out Pinch)` — virtual midpoint, distance, per-frame delta, scale ratio.
- Optional gamepad/keyboard controller cursor via `cursorSpeed` ctor arg + `Update(raw, isActive, dt)`.
- All additive; 0.1.x consumers are unaffected until they bump.

## 0.1.3

- Fix: desktop clicks were suppressed whenever the game window was not at the screen
  origin. `InputManager`'s in-window check compared window-relative mouse coords against
  `WindowBounds` carrying the window's screen offset, so `Contains` rejected every click.
  The check now ignores `WindowBounds.Location` (uses Width/Height only), and
  `MonoGameRawInput` reports the client area at the origin. Adds headless regression tests.

## 0.1.2

- Add per-package README files (shown on the NuGet package pages).
- Add this changelog.

## 0.1.1

- XML documentation comments across the public API of `KhaozEngine.Input`, `.Screens`, and `.Ecs`.
- Enable `GenerateDocumentationFile` so docs ship in the packages for IntelliSense.
- No functional change from 0.1.0.

## 0.1.0

Initial release. Four packages extracted from Hardpoint/Nullwake/SpaceGame:

- **KhaozEngine.Input** — unified pointer (mouse+touch), `IsTapIn` press-origin invariant
  (click-through fix), region blocking, drag/scroll/pinch, keyboard + gamepad + menu-navigation,
  coordinate-transform seam (`Identity` / `Matrix` / `VirtualResolution`), all behind the testable
  `IRawInput` seam.
- **KhaozEngine.Screens** — screen stack with top-to-bottom routing, `ConsumeWhenVisible` /
  `ConsumeWhenHandled` policies, and transitions.
- **KhaozEngine.UI** — widget library, `PrimitiveRenderer`, `TextInputHandler`.
- **KhaozEngine.Ecs** — minimal `World` / `Entity` / `ISystem`.

30 headless tests. Hardpoint migrated onto it.
