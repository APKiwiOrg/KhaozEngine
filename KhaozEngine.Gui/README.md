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
- **Theming (`GuiTheme` + `GuiStyle`).** Since 10.11.0 the default widget look is crisp: a neutral-dark palette
  with a blue accent, subtle 3px corners, and 1px hairline borders (no shadow/gradient/glow). `GuiTheme` is the
  central semantic palette every retained widget reads at construction (`Surface`/`Accent`/`Border`/`Text`/... plus
  `CornerRadius`/`BorderThickness`). Set the ambient `GuiTheme.Default` ONCE at startup (before building widgets) to
  rebrand the whole UI; set it to `GuiTheme.Legacy` to keep the pre-10.11.0 flat blue-grey look. `GuiStyle` carries
  the button palette + the modern-affordance knobs, with presets: `Default` (crisp, == `Primary`), `Secondary`
  (muted), `Danger` (red), `Active` (bright-accent selected), `Modern` (rounded + glow + shadow), and `Legacy` (the
  exact old flat button). Per-widget colour fields still override the theme.
- Core widgets, all bounds-aware over `Pointer` (press-origin click-through invariant), drawn with a 1x1 white
  texture + `SpriteBatch`:
  - `Button` - click via `IsTapIn`, hover/press visuals.
  - `Label` - non-interactive text, aligned (left/center/right) and optionally word-wrapped, via `TextLayout`; a
    `Scale` field (default 1) uniformly scales the drawn text.
  - `Panel` - filled/bordered container; `BlocksPointer` reserves its region so lower layers skip hit-testing under it.
  - `Slider` - horizontal track; a drag started from inside jumps + tracks the value 0..1. `WasChanged` mirrors the
    `Update` return for callers that inspect it later in the frame; `Opacity` fades the whole slider for a host transition.
  - `Toggle` - two-state switch flipped by a valid tap; fires `OnChanged`. `WasToggled` mirrors the `Update` return;
    `Opacity` fades the whole toggle for a host transition.
  - `NumberField` - numeric field for editor inspectors, driven by `InputManager` (needs the keyboard). A drag
    started inside scrubs `Value` by `DragScale` value units per pixel (grab-gated, so it keeps tracking off the
    widget). A tap under 3 draw units of travel opens typing mode (`TextEntry` with a digits/one-minus/one-dot
    filter). Enter commits (parsed, clamped to [`Min`, `Max`], rounded to `Decimals`), Escape cancels, and a tap
    outside commits like Enter. `WasChanged` mirrors the `Update` return. `Opacity` fades the field for a host
    transition.
  - `Dropdown` - trigger + option list (opens below); two-phase draw (`Draw` trigger / `DrawOverlay` list last).
    Opt-in (default off): `ShowChevron` draws an up/down caret reflecting the open state; `Opacity` fades the whole
    dropdown for a host transition.
  - `TabBar` - horizontal tab bar / segmented control: N evenly-split tabs, exactly one active. A valid tap
    activates a tab and raises `ChangedThisFrame` for one frame (and `Update` returns true), so the caller swaps
    the panel body only on a real change; `ActiveIndex` is settable to restore/persist the selection without
    raising the change signal. Active tab uses `ActiveStyle` (`GuiStyle.Active`), inactive tabs `InactiveStyle`
    (`GuiStyle.Secondary`); labels are `LocalizedText`; `TabRect(i)` is the pure per-tab layout; `Opacity` fades
    the whole bar for a host transition.
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
    A held key auto-repeats (Backspace deletes / a character keeps typing) at the OS repeat rate. `SetText(value)`
    replaces the buffer programmatically (clamped to `MaxLength`, seen as a change by the next `Update`); `Focus()` /
    `Unfocus()` drive focus directly. The placeholder is `LocalizedText` via `PlaceholderContent` (the former
    `Placeholder` string is an `[Obsolete]` shim); `Opacity` fades the whole field for a host transition.
  - `Tooltip` - auto-sized floating bubble; `ComputeBounds` (flip/clamp) is a pure, testable layout function.
    Opt-in (default off): a two-column title (`Show(title, titleRight, ...)`), a `ShowTitleSeparator` rule under the
    title, and platform-aware dismissal via `Dismiss` (`CallerDriven` desktop-hover vs `TapOutside` touch, driven by
    `Update(Pointer)`) - the dismissal policy is a runtime value, not a compile-time platform branch.
  - `PopupPanel` - modal dialog: scrim + title + `PopupRow` content + dismiss/primary footer; blocks the pointer.
    Text is `LocalizedText`: `TitleContent` / `DismissContent` / `PrimaryActionContent` and the resolve-at-build
    `PopupRow.Header(LocalizedText)` / `Stat(LocalizedText, LocalizedText, ...)` factories (rebuild the rows to pick
    up a runtime locale switch). The former `Title` / `DismissText` / `PrimaryActionText` string members and the
    `PopupRow.Header(string)` / `Stat(string, ...)` factories remain as `[Obsolete]` shims. Overflowing content
    scrolls (wheel via the `Update(Pointer, float wheelDelta)` overload + drag-to-scroll, scissor-clipped, `ScrollOffset`
    read-only, `ScrollWheelSpeed` tunable); the two footer buttons shrink to fit a narrow panel (wide panels stay at the
    fixed width). Opt-in additive: `WrapLongLabels` wraps a stat row with an empty value across the content width (row
    grows to fit); `PopupRow.Stat(..., iconColor)` draws a small colour swatch before the label; `Opacity` fades the
    whole popup for a host transition. Defaults keep every existing consumer byte-identical.
  - `ScrollablePanel` - wheel/drag scrolling fixed-height list; rows drawn between `BeginClip`/`EndClip` (scissor),
    hit-test with `TappedItemIndex`. Opt-in overlay chrome (all default to no-ops, so existing callers are
    byte-identical): a header band (`HeaderHeight` + `DrawHeader`) above the scroll region; a slide-up animation
    driven by an external `TransitionAlpha` from a docked bottom edge (`SlideFromBottom`); drag-to-resize the header
    within `MinHeight`/`MaxHeight` (`Resizable`); and a dimmed `Scrim` with tap-outside-to-close (`ScrimDismissed`).
    Geometry is exposed via `CurrentBounds`/`ContentBounds` (== `Bounds` with no knob set).
  - `TreeView` - scrollable outline over `TreeNode` roots (a `LocalizedText` label, children, an `Expanded`
    flag, a caller-owned `Tag`). `VisibleRows()` is the depth-first walk skipping collapsed subtrees. A tap in a
    row's caret zone (the `Indent`-wide band at its depth) toggles expansion for a node with children, a tap
    elsewhere in the row selects it (`Selected`, `OnSelected`). The wheel scrolls clamped to the content, and
    rows are scissor-clipped to `Bounds`.
  - `PropertyGrid` - a vertical stack of `PropertyRow`s split label/editor at `LabelFraction`, scrolling like
    `ScrollablePanel` (wheel + scissor clip). Built-in rows: `FloatRow` (a `NumberField`), `BoolRow` (a
    `Toggle`), `TextRow` (a `TextInput`), `ReadOnlyRow` (a polled display string, no input). Each row polls its
    getter every `Update` unless the user is mid-edit/scrub/focus on that row's child widget, so external
    changes (undo, another editor) stay in sync without a change-event bus. A row fully scrolled out of view is
    skipped entirely, so it neither hit-tests nor reserves off-view input.
- `UpdateOverlayView` / `UpdateOverlayScreen` (+ `UpdateOverlayTheme`) - the in-game auto-updater popup, a pure
  presenter over `KhaozEngine.Updates`' `IUpdateStatus`: it announces an available update, shows download
  progress, and prompts the restart-and-apply, driven by the theme's trigger key/button (default U / gamepad Y).
  Its dim scrim (like any opaque `Screen.BackgroundColor` via `Screen.DrawBackground`) fills the viewport's
  `WindowBounds`, so under a letterbox scale it covers the whole window instead of leaving the bars showing the
  screen below (10.38.0).
  `UpdateOverlayTheme` injects the palette, layout, trigger binding, and the per-state title/body text. The
  default `TitleFor`/`BodyFor` are **localization-aware**: each line resolves through the ambient
  `LocalizationContext.Catalog` (`KhaozEngine.App`) against the engine-owned `UpdateOverlayStrings` keys
  (`update.overlay.*`, one title + body per `UpdateState`), falling back to built-in English
  (`UpdateOverlayStrings.EnglishDefaults`) when no catalog is wired or a key is absent, so a game localizes the
  overlay just by adding those keys to its catalog with no subclass, and an unlocalized build renders exactly as
  before. A **required** update (`IUpdateStatus.IsRequired`, from the signed manifest) draws its titles through
  the `TitleFor(UpdateState, IUpdateStatus)` overload and swaps in the `update.overlay.*.required` keys, which
  convey mandatoriness and drop the keypress prompt (the client auto-advances via
  `UpdateOverlayActions.AutoAdvanceRequired`); a theme overriding `TitleFor`/`BodyFor` adds its own `IsRequired`
  branch. Overriding `TitleFor`/`BodyFor` still fully replaces the text. `theme.ToUpdaterUiOptions(...)`
  (`UpdaterUiThemeExtensions`) derives the shim's native progress-window palette (`UpdaterUiOptions`, in
  `KhaozEngine.Updates`) from the same theme (accent from `ProgressFill`, background from `PanelFill`, text from
  `BodyText`), so the in-game overlay and the apply window share one palette. See `docs/UPDATER.md` for the key
  table.
- `DiagnosticsOverlay` (+ `DiagnosticsOverlayTheme`, `OverlayRow`/`OverlaySection`) - a reusable in-game
  telemetry HUD, a pure presenter modeled on `UpdateOverlayView`. The game assembles sections each frame and
  feeds them via `SetSections`, or registers a provider once with `SetSectionsProvider(provider, refreshInterval)`
  and lets `Update` rebuild them on that interval (a built-in throttle: polls immediately, then every interval
  seconds; interval 0 = every frame; null provider detaches) instead of a hand-rolled per-frame timer around
  `SetSections`; `Update(InputState, dt)` toggles on `Theme.ToggleKey` (default F1; optional
  gamepad button) and fades (headless-testable, `InputState.Empty` inert); `Draw` renders a corner panel
  (`Theme.Corner`) of titles + right-aligned values. `PerformanceSection(FrameStats)` /
  `PassTimingsSection(PassTimings)` / `NetworkSection(in ClientNetStats)` populators cover the common cases
  (from `KhaozEngine.Diagnostics`). `PassTimingsSection` lists one row per pass name (in first-sampled order)
  with that pass's rolling avg/min/max milliseconds - CPU encode time, not true GPU time (see the
  `KhaozEngine.Render3D` README / `docs/USING-KHAOZENGINE.md`).
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

**Editor inspector widgets (`NumberField` / `TreeView` / `PropertyGrid`).** The trio a `PropertyGrid`-based
inspector is built from: a scrubbable/typeable number field, a hierarchy outline, and the grid that hosts typed
rows over get/set delegates (no reflection). `NumberField` also stands alone outside a grid.

```csharp
// A property grid over a selected scene object: label left, typed editor right, split at LabelFraction.
var grid = new PropertyGrid(inspectorRect);
grid.Rows.Add(new FloatRow(Strings.PosX, () => obj.Position.X, v => obj.Position = obj.Position with { X = v },
    dragScale: 0.1f));
grid.Rows.Add(new BoolRow(Strings.Visible, () => obj.Visible, v => obj.Visible = v));
grid.Rows.Add(new ReadOnlyRow(Strings.ObjectId, () => obj.Id.ToString()));
grid.Update(input, dt);   // polls getters, runs the focused row's child widget, writes back on a real change
grid.Draw(batch, white, font);

// A scene outline beside it: depth-first rows, caret zone toggles a parent, body tap selects.
var tree = new TreeView(outlineRect);
tree.Roots.Add(sceneRoot);            // TreeNode: label + children + Tag (e.g. the scene object)
tree.OnSelected = node => Select((SceneObject)node.Tag!);
tree.Update(input);
tree.Draw(batch, white, font);

// NumberField also stands alone (drag to scrub, tap to type):
var speed = new NumberField(fieldRect, value: 12f) { Min = 0f, Max = 200f, DragScale = 0.25f, Decimals = 1 };
speed.Update(input, dt);   // InputManager, not Pointer - typing needs the keyboard
speed.Draw(batch, white, font);
```

**DPI-aware pixel snapping** (since 10.12.0): when a widget is drawn inside a point-space UI pass (a
`UiViewport` `SpriteBatch.Begin`), the retained widgets and `GuiDraw` snap their rect and border thickness
to whole device pixels, so rounded/box borders render as a uniform 1px on HiDPI and at non-integer window
scales instead of straddling a fractional device-pixel phase (the old "thicker on one side" artifact). The
snapping is a no-op outside a point-space pass, so screen/design/world rendering is byte-identical and a
borderless style stays borderless. Text drawn through the batch in that pass also snaps its glyph origins
(pair with a `DpiFont` from `KhaozEngine.Render2D` for a crisp atlas).

Text wrap/alignment lives in `KhaozEngine.Render2D.TextLayout` (over the `ITextMeasurer` seam, so the layout
math is headless-testable); clipping uses `SpriteBatch` scissor (`SetScissor`/`ClearScissor`, DPI-aware). Ported
from `KhaozEngine.Screens`/`UI` (game-specific layout coupling dropped). Built on `KhaozEngine.Windowing`
(Pointer/Input) + `KhaozEngine.Render2D` (SpriteBatch/SpriteFont/Texture2D). Part of the MonoGame-free engine.
