# KhaozEngine.Gui (experimental, 5.x)

Screen-stack + widgets on the custom MonoGame-free stack.

- `ScreenStack` — owns a stack of `Screen`s; routes input top-to-bottom (input-consumption + modal layering,
  the click-through model), draws bottom-to-top, drives transitions. Exposes a shared `Pointer` + `InputState`.
- `Screen` — base UI surface: `Update(dt, receivesInput)` (returns whether it consumed input) + `Draw(SpriteBatch)`,
  with `DrawOrder` / `PassUpdateThrough` / `AlwaysReceivesInput` / transitions.
- Core widgets, all bounds-aware over `Pointer` (press-origin click-through invariant), drawn with a 1x1 white
  texture + `SpriteBatch`:
  - `Button` — click via `IsTapIn`, hover/press visuals.
  - `Label` — non-interactive text, aligned (left/center/right) and optionally word-wrapped, via `TextLayout`.
  - `Panel` — filled/bordered container; `BlocksPointer` reserves its region so lower layers skip hit-testing under it.
  - `Slider` — horizontal track; a drag started from inside jumps + tracks the value 0..1.
  - `Toggle` — two-state switch flipped by a valid tap; fires `OnChanged`.

Text wrap/alignment lives in `KhaozEngine.Render2D.TextLayout` (over the `ITextMeasurer` seam, so the layout
math is headless-testable). Ported from `KhaozEngine.Screens`/`UI`. Built on `KhaozEngine.Windowing`
(Pointer/Input) + `KhaozEngine.Render2D` (SpriteBatch/SpriteFont/Texture2D). Part of the post-MonoGame 5.x
line. The heavy widgets (ScrollablePanel/Dropdown/TextInput/PopupPanel) and pause/timescale/touch/gamepad are
follow-ups.
