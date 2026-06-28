# KhaozEngine.Gui

Immediate-mode + retained UI on the custom MonoGame-free stack.

- `GuiSurface` — immediate-mode UI for a `Run`-loop game: `Begin(batch?, pointer)` then `Panel`/`Label`/`Swatch`/
  `Button`->bool/`Slider`/hover, with a `PointerCaptured` click-through gate. `FocusNavigator` drives
  keyboard/gamepad menu focus.
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
  - `Dropdown` — trigger + option list (opens below); two-phase draw (`Draw` trigger / `DrawOverlay` list last).
  - `TextInput` — single-line field; tap-to-focus, typed keys edit the text (via `TextEntry`), blinking caret.
    A held key auto-repeats (Backspace deletes / a character keeps typing) at the OS repeat rate.
  - `Tooltip` — auto-sized floating bubble; `ComputeBounds` (flip/clamp) is a pure, testable layout function.
  - `PopupPanel` — modal dialog: scrim + title + `PopupRow` content + dismiss/primary footer; blocks the pointer.
  - `ScrollablePanel` — wheel/drag scrolling fixed-height list; rows drawn between `BeginClip`/`EndClip` (scissor),
    hit-test with `TappedItemIndex`.
- `TextEntry` — headless key→char text-entry helper (US layout + shift), used by `TextInput`. No SDL plumbing.
  Ctrl/Super (Cmd) held suppresses character entry so shortcut chords like Ctrl+V / Cmd+V paste instead of typing.
  Acts on `InputState.WasTyped` (press edge OR OS auto-repeat tick), so a held Backspace or character key repeats at
  the OS rate; the chord suppression still blocks repeated character entry while Ctrl/Cmd is held.

Text wrap/alignment lives in `KhaozEngine.Render2D.TextLayout` (over the `ITextMeasurer` seam, so the layout
math is headless-testable); clipping uses `SpriteBatch` scissor (`SetScissor`/`ClearScissor`, DPI-aware). Ported
from `KhaozEngine.Screens`/`UI` (game-specific layout coupling dropped). Built on `KhaozEngine.Windowing`
(Pointer/Input) + `KhaozEngine.Render2D` (SpriteBatch/SpriteFont/Texture2D). Part of the 5.x engine.
