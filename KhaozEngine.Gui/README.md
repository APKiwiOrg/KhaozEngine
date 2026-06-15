# KhaozEngine.Gui (experimental, 5.x)

Screen-stack + widgets on the custom MonoGame-free stack.

- `ScreenStack` — owns a stack of `Screen`s; routes input top-to-bottom (input-consumption + modal layering,
  the click-through model), draws bottom-to-top, drives transitions. Exposes a shared `Pointer` + `InputState`.
- `Screen` — base UI surface: `Update(dt, receivesInput)` (returns whether it consumed input) + `Draw(SpriteBatch)`,
  with `DrawOrder` / `PassUpdateThrough` / `AlwaysReceivesInput` / transitions.
- `Button` — bounds-aware widget over `Pointer.IsTapIn` (press-origin click-through invariant), hover/press visuals.

Ported from `KhaozEngine.Screens`/`UI`. Built on `KhaozEngine.Windowing` (Pointer/Input) + `KhaozEngine.Render2D`
(SpriteBatch/SpriteFont/Texture2D). Part of the post-MonoGame 5.x line. The wider widget set
(Slider/Dropdown/ScrollablePanel/...) and pause/timescale/touch/gamepad are follow-ups.
