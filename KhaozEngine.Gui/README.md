# KhaozEngine.Gui

Immediate-mode + retained UI on the custom MonoGame-free stack.

**Localized text:** the player-facing text sinks (the `Label` / `Button` widgets, `GuiSurface.Label` /
`Button` / `StatChip`, `Tooltip.Show`, and `PopupPanel` - its `TitleContent` / `DismissContent` /
`PrimaryActionContent` plus the `PopupRow.Header` / `Stat` factories) take a `LocalizedText` (from
`KhaozEngine.App`) instead of a raw `string`. Pass a `StringId` (implicitly converts) for localizable copy, or
`LocalizedText.Raw("...")` for non-localizable text (names, numbers, debug). The old `string` overloads remain
but are `[Obsolete]`; the
`KhaozEngine.Localization.Analyzers` analyzer (in the `Game2D`/`Game3D` umbrellas) flags them. `IconButton`'s
string argument is an icon-atlas key, not player text, so it is unchanged. See the App package for
`StringId` / `LocalizedText` / `LocalizationContext`.

- `GuiSurface` - immediate-mode UI for a `Run`-loop game: `Begin(batch?, pointer)` then `Panel`/`Label`/`Swatch`/
  `Button`->bool/`Slider`/hover, with a `PointerCaptured` click-through gate. `FocusNavigator` drives
  keyboard/gamepad menu focus. The text sinks (`Label`/`Button`/`StatChip`) take an optional trailing
  `float scale = 1f` that scales the label only (rect + hit-test unchanged), so one shared font renders at many
  sizes for pixel-parity; defaults to `1f`, so unscaled callers are byte-identical.
- `ScreenStack` - owns a stack of `Screen`s. Routes input top-to-bottom (input-consumption + modal layering,
  the click-through model), draws bottom-to-top, drives transitions. Exposes a shared `InputManager` (menu nav +
  action mapping; screens read `Manager.InputManager` to drive `FocusNavigator` and the keyboard/gamepad widget
  overloads), its `Pointer` (== `InputManager.Pointer`, so pointer-only and manager-driven widget updates share
  one click-through gate), and the raw `InputState`. Screens are ordered by `DrawOrder` ascending with a stable
  insert, so equal-`DrawOrder` screens keep insertion order and `Screens[^1]` is the visually-topmost.
- `Screen` - base UI surface: `Update(dt, receivesInput)` (returns whether it consumed input) + `Draw(SpriteBatch)`,
  with `DrawOrder` / `PassUpdateThrough` / `AlwaysReceivesInput` / transitions.
- Core widgets, all bounds-aware over `Pointer` (press-origin click-through invariant), drawn with a 1x1 white
  texture + `SpriteBatch`:
  - `Button` - click via `IsTapIn`, hover/press visuals.
  - `Label` - non-interactive text, aligned (left/center/right) and optionally word-wrapped, via `TextLayout`; a
    `Scale` field (default 1) uniformly scales the drawn text.
  - `Panel` - filled/bordered container; `BlocksPointer` reserves its region so lower layers skip hit-testing under it.
  - `Slider` - horizontal track; a drag started from inside jumps + tracks the value 0..1.
  - `Toggle` - two-state switch flipped by a valid tap; fires `OnChanged`.
  - `Dropdown` - trigger + option list (opens below); two-phase draw (`Draw` trigger / `DrawOverlay` list last).
    Opt-in (default off): `ShowChevron` draws an up/down caret reflecting the open state; `Opacity` fades the whole
    dropdown for a host transition.
  - **Keyboard/gamepad control (opt-in, additive)** on `Toggle`/`Slider`/`Dropdown`: each has an
    `Update(InputManager, bool focused, PlayerIndex? = null)` overload that layers `InputManager` menu actions on
    top of the pointer path (only when `focused`), mirroring `FocusNavigator`. So a settings row is fully
    keyboard/gamepad-navigable without a `MenuEntry` shell. The pointer-only `Update(Pointer)` overloads are
    unchanged. Toggle: select flips, next/previous force on/off (`Flip()`/`Set(bool)` primitives). Slider:
    next/previous nudge `Value` by `NudgeStep` (default 0.1; `Nudge(float)` primitive). Dropdown: closed, select
    opens and next/previous cycle in place; open, up/down move `HighlightedIndex`, select commits, cancel closes
    (`Open`/`Close`/`HighlightNext`/`HighlightPrevious`/`CommitHighlight`/`StepSelection` primitives, `Wrap` flag,
    `FocusColor` for the highlighted row). The pointer path never activates the highlight, so its overlay draw is
    byte-identical.
  - `TextInput` - single-line field; tap-to-focus, typed keys edit the text (via `TextEntry`), blinking caret.
    A held key auto-repeats (Backspace deletes / a character keeps typing) at the OS repeat rate.
  - `Tooltip` - auto-sized floating bubble; `ComputeBounds` (flip/clamp) is a pure, testable layout function.
    Opt-in (default off): a two-column title (`Show(title, titleRight, ...)`), a `ShowTitleSeparator` rule under the
    title, and platform-aware dismissal via `Dismiss` (`CallerDriven` desktop-hover vs `TapOutside` touch, driven by
    `Update(Pointer)`) - the dismissal policy is a runtime value, not a compile-time platform branch.
  - `PopupPanel` - modal dialog: scrim + title + `PopupRow` content + dismiss/primary footer; blocks the pointer.
    Text is `LocalizedText`: `TitleContent` / `DismissContent` / `PrimaryActionContent` and the resolve-at-build
    `PopupRow.Header(LocalizedText)` / `Stat(LocalizedText, LocalizedText, ...)` factories (rebuild the rows to pick
    up a runtime locale switch). The former `Title` / `DismissText` / `PrimaryActionText` string members and the
    `PopupRow.Header(string)` / `Stat(string, ...)` factories remain as `[Obsolete]` shims.
  - `ScrollablePanel` - wheel/drag scrolling fixed-height list; rows drawn between `BeginClip`/`EndClip` (scissor),
    hit-test with `TappedItemIndex`. Opt-in overlay chrome (all default to no-ops, so existing callers are
    byte-identical): a header band (`HeaderHeight` + `DrawHeader`) above the scroll region; a slide-up animation
    driven by an external `TransitionAlpha` from a docked bottom edge (`SlideFromBottom`); drag-to-resize the header
    within `MinHeight`/`MaxHeight` (`Resizable`); and a dimmed `Scrim` with tap-outside-to-close (`ScrimDismissed`).
    Geometry is exposed via `CurrentBounds`/`ContentBounds` (== `Bounds` with no knob set).
- `DiagnosticsOverlay` (+ `DiagnosticsOverlayTheme`, `OverlayRow`/`OverlaySection`) - a reusable in-game
  telemetry HUD, a pure presenter modeled on `UpdateOverlayView`. The game assembles sections each frame and
  feeds them via `SetSections`; `Update(InputState, dt)` toggles on `Theme.ToggleKey` (default F1; optional
  gamepad button) and fades (headless-testable, `InputState.Empty` inert); `Draw` renders a corner panel
  (`Theme.Corner`) of titles + right-aligned values. `PerformanceSection(FrameStats)` /
  `NetworkSection(in ClientNetStats)` populators cover the common cases (from `KhaozEngine.Diagnostics`).
  `Bounds` is the last-drawn panel rect (empty when hidden/faded-out), so a caller can place an `OverlayLegend`
  at `Bounds.Right` + a gap to sit a second panel directly beside it.
- `TextEntry` - headless key→char text-entry helper (US layout + shift), used by `TextInput`. No SDL plumbing.
  Ctrl/Super (Cmd) held suppresses character entry so shortcut chords like Ctrl+V / Cmd+V paste instead of typing.
  Acts on `InputState.WasTyped` (press edge OR OS auto-repeat tick), so a held Backspace or character key repeats at
  the OS rate; the chord suppression still blocks repeated character entry while Ctrl/Cmd is held.
- `OverlayLegend` (+ `LegendEntry`, `OverlayLegendTheme`) - a domain-agnostic color-swatch + label panel for
  debug overlays: `SetEntries(IReadOnlyList<LegendEntry>)`, `EntryCount`, `Measure(SpriteFont)` -> `Rect` (empty
  when no entries), and two `Draw` overloads - `Draw(SpriteBatch, SpriteFont, Texture2D, Rect viewport)`
  (anchors to `Theme.Corner`/`Theme.Margin`) and `Draw(..., Vector2 topLeft)` (places the panel at an explicit
  top-left, e.g. beside another panel). `Bounds` is the last-drawn panel rect. No `Visible`/fade state of its
  own - the caller only calls `Draw` while its own overlay is on. `Theme` (`OverlayLegendTheme`) injects the
  look + layout (fill, border, label colour, thickness, padding, swatch size/gap, row spacing, text scale,
  anchor); `OverlayLegendTheme.Default` is the neutral grey palette it shipped with, and
  `OverlayLegendTheme.FromDiagnostics(DiagnosticsOverlayTheme)` derives a matching palette so a legend sits
  beside a `DiagnosticsOverlay` in the same style. `LegendEntry(Color Swatch, string Label)` is one row. The
  collision-shape debug overlay (`KhaozEngine.Render3D.Debug.CollisionShapeOverlay`) is the first consumer,
  and it is reusable by any future overlay layer.

Text wrap/alignment lives in `KhaozEngine.Render2D.TextLayout` (over the `ITextMeasurer` seam, so the layout
math is headless-testable); clipping uses `SpriteBatch` scissor (`SetScissor`/`ClearScissor`, DPI-aware). Ported
from `KhaozEngine.Screens`/`UI` (game-specific layout coupling dropped). Built on `KhaozEngine.Windowing`
(Pointer/Input) + `KhaozEngine.Render2D` (SpriteBatch/SpriteFont/Texture2D). Part of the MonoGame-free engine.
