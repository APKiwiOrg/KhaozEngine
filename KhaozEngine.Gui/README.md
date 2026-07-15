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
  sizes for pixel-parity, and defaults to `1f`, so unscaled callers are byte-identical. `StatChip`'s "label  value"
  text is memoized per surface (keyed on the resolved label/value content), so a HUD stat chip redrawn every
  frame with an unchanged value (health, ammo, gold sitting steady) is a lookup, not a fresh string every draw.
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
- **Texture skinning (`GuiStyle.Skin`, family-wide).** `GuiStyle` has an optional `Skin` (a `GuiSkin`, default
  `null` = today's flat GuiDraw primitives, byte-for-byte). Set it and EVERY widget that fills through
  `GuiDraw.FillStyled` (Panel, Button, ProgressBar, TextInput/NumberField, ScrollablePanel, Dropdown, PopupPanel,
  SlotGrid, TreeView, ...) renders a nine-slice sprite frame instead of the flat fill. `GuiSkin` rides the same
  `Texture2D` + source-UV mechanism as `IconAtlas`: a `Texture` (or atlas sub-region via `Source` + its pixel size),
  four source-pixel `Inset*` values, and `Center` (`GuiSkinCenter.Stretch` default, or `Tile`). Build one with
  `GuiSkin.NineSlice(texture, inset)` / `NineSlice(texture, l, t, r, b, center)` for a whole texture, or
  `GuiSkin.FromAtlas(...)` for one cell of a shared atlas. The four corners keep their source-pixel size (never
  scaled) while the edges + centre stretch or tile; when the destination is too small for both opposing corners the
  destination insets scale down proportionally so the corners just meet. The resolved state colour multiplies OVER
  the skin as a tint (set the style's `Fill` to white for the skin's native colours, `Hover`/`Press` as tints -
  per-state skins are a future extension, not per-state today). A skinned frame owns the silhouette, so the
  procedural `CornerRadius`/border is skipped, but `ShadowSize` still draws its drop shadow underneath.
  Interior content respects the frame through the shared seam `GuiStyle.ContentInsets(bounds)` /
  `ContentRect(bounds)`: skinned = the skin's `GuiSkin.DestinationInsets` (exactly what the nine-slice paints,
  including the too-small clamp), unskinned = the uniform `BorderThickness` (unchanged). `ProgressBar.InnerBounds`
  rides it (a skinned bar's fill sits inside the frame), `TextInput`/`NumberField` pad their text past a frame
  thicker than their fixed pad, and caller-painted content (`SlotGrid.DrawSlotContent`) can call `ContentRect` on
  the slot rect to stay inside the frame.
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
  - `SlotGrid` - a grid of uniform square slots (hotbar / inventory / equipment) over `Pointer`. `Bounds`.X/Y is
    the origin; the footprint is derived from `Columns`/`SlotSize`/`Spacing` and the slot `Count` (read `ContentSize`
    / `ContentBounds`). Each slot hit-tests through the press-origin invariant; `HoveredSlot`/`PressedSlot` expose the
    live index (-1 = none) and a valid tap fires `OnSlotClicked` (and `Update` returns the index). Empty slots draw a
    themed frame; the caller paints icons/counts through the `DrawSlotContent(index, rect, batch)` hook and optional
    per-slot `KeybindLabels` (raw input-token glyphs). `SlotRect(i)`/`SlotAt(point)` are pure geometry; `Opacity`
    fades the whole grid.
  - `ProgressBar` - a thin fill bar (health / XP / cast / load / charge pips). `Fraction` is clamped 0..1; the accent
    fill (`FillColor`) sits inside the border frame, the track is `TrackColor`, and corners/border/skin come from
    `Style`. `FillDirection` picks the edge the fill grows FROM: `LeftToRight` (default, today's look), `RightToLeft`,
    `BottomToTop`, or `TopToBottom` (the last two make a vertical bar). Set `SegmentCount` > 1 (default 0/1 = one
    continuous fill, unchanged) to split the bar into equal segments separated by `SegmentSpacing`:
    `SegmentFillMode.Continuous` clips the proportional fill into each segment so the gaps read as ticks (xp / cast
    bars), `SegmentFillMode.Discrete` lights a whole segment only once the fill fully covers it (combo points /
    ability charges). Segmentation composes with every `FillDirection` (a vertical pip stack works). Optional centered
    `OverlayText` (a `LocalizedText`, so a caption localizes; wrap a number/percentage in `Raw`) stays centered in
    `Bounds` regardless of direction. `FillRect`/`InnerBounds`/`SegmentRects()`/`FilledSegmentCount` are pure geometry;
    non-interactive (no `Update`); `Opacity` fades the whole bar.
  - `NumberField` - numeric field for editor inspectors, driven by `InputManager` (needs the keyboard). A drag
    started inside scrubs `Value` by `DragScale` value units per pixel (grab-gated, so it keeps tracking off the
    widget). A tap under 3 draw units of travel opens typing mode (`TextEntry` with a digits/one-minus/one-dot
    filter). Enter commits (parsed, clamped to [`Min`, `Max`], rounded to `Decimals`), Escape cancels, and a tap
    outside commits like Enter. `WasChanged` mirrors the `Update` return. `Opacity` fades the field for a host
    transition. `IsScrubbing` mirrors the grab-gate (true from the inside press to the release, even once the
    cursor strays off the widget) and pairs with `IsEditing`, so a host like `PropertyGrid`'s `FloatRow` can skip
    its external-value poll while either is true instead of stomping a live gesture. `CancelEdit()` exits typing
    mode without committing, leaving `Value` at its pre-edit value (the same path Escape takes, made directly
    callable), the hook a host uses to close an in-progress edit it is tearing down. Typing accepts numpad
    (keypad) keys the same as the top-row keys - digits, dot, minus - and the FIRST keypad keystroke ends the
    select-all seed exactly like a top-row one (replace the seeded value, not append to it).
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
    title, a width cap (`MaxWidth` px and/or `MaxWidthFraction` of the viewport) that word-wraps long body lines
    downward instead of overflowing the viewport (hard-breaking a token longer than the cap), and platform-aware
    dismissal via `Dismiss` (`CallerDriven` desktop-hover vs `TapOutside` touch, driven by `Update(Pointer)`) - the
    dismissal policy is a runtime value, not a compile-time platform branch.
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
    **N-button footer** (`SetFooterButtons(IReadOnlyList<PopupAction>)`): an additive alternative to the classic
    dismiss/primary pair. A `PopupAction` is a `LocalizedText` label, an optional `Action` callback, and an
    `Enabled` flag. A non-empty `FooterButtons` list REPLACES the dismiss/primary footer entirely and lays the
    buttons out right-to-left, with index 0 the rightmost slot and the default action (the one Enter fires,
    rendered in the primary green). `CancelIndex` (default -1, resolving to the last footer action) names which
    one Esc fires. `HandleKeys(InputState)` is the additive keyboard entry point a host calls alongside the
    existing pointer-only `Update` overloads to wire Enter/Esc without hand-rolling the key checks itself. A
    footer callback is safe to mutate the list mid-fire (for example closing the dialog from inside the very
    action it just ran). An empty `FooterButtons` list leaves the classic dismiss/primary footer completely
    unchanged, so every existing consumer stays byte-identical.
  - `ScrollablePanel` - wheel/drag scrolling fixed-height list; rows drawn between `BeginClip`/`EndClip` (scissor),
    hit-test with `TappedItemIndex`. Opt-in overlay chrome (all default to no-ops, so existing callers are
    byte-identical): a header band (`HeaderHeight` + `DrawHeader`) above the scroll region; a slide-up animation
    driven by an external `TransitionAlpha` from a docked bottom edge (`SlideFromBottom`); drag-to-resize the header
    within `MinHeight`/`MaxHeight` (`Resizable`); and a dimmed `Scrim` with tap-outside-to-close (`ScrimDismissed`).
    Geometry is exposed via `CurrentBounds`/`ContentBounds` (== `Bounds` with no knob set).
  - `TreeView` - scrollable outline over `TreeNode` roots (a `LocalizedText` label, children, an `Expanded`
    flag, a caller-owned `Tag`). `VisibleRows()` is the depth-first walk skipping collapsed subtrees. A tap in a
    row's caret zone (the `Indent`-wide band at its depth) toggles expansion for a node with children, a tap
    elsewhere in the row selects it (`Selected`, `OnSelected`). The wheel scrolls clamped to the content,
    `WheelRowsPerNotch` rows per notch (default 3), and rows are scissor-clipped to `Bounds`. A held press that
    clears `DragThreshold` (default 6 pixels) becomes a same-parent drag-and-drop row reorder instead of a tap:
    a valid drop fires `OnReordered(node, oldIndex, newIndex)` and `WasReordered` goes true, an insertion line
    marks the live target, Escape aborts with no drop, and a cross-parent or off-tree release is rejected. The
    widget only reports the move, never mutating `Roots`/`Children` itself, so the host applies it and rebuilds
    the tree. Wheel-scrolling while a drag is armed is not supported (the drop geometry freezes at the current
    scroll position). `ScrollTo(TreeNode)` brings a node into view: expands every collapsed ancestor so it
    rejoins the visible walk, then scrolls the minimal amount needed so its row sits fully inside `Bounds`
    (clamped to `[0, maxScroll]`, the same clamp idiom as `ScrollablePanel.ScrollTo(float)`), a no-op when
    the node is unreachable from `Roots`. `FindByTag(Func<object?, bool>)` walks `Roots` depth-first
    (regardless of `Expanded`, a collapsed subtree is still searched) and returns the first node whose `Tag`
    satisfies the predicate, or null - a host resolves a caller-owned identity (an outline reference) back
    to the live node after `Roots` is rebuilt from fresh data, then calls `ScrollTo` on the result, the pair
    a selection-sync host uses after every rebuild so the highlighted row survives instead of orphaning. The
    selected-row fill now draws via `GuiDraw.FillStyled` against `Style` (a `GuiStyle`, default
    `GuiStyle.Default`) instead of a flat `GuiDraw.Fill`, so a tree using `GuiStyle.Modern` gets the same
    rounded selection highlight as its other styled widgets.
  - `PropertyGrid` - a vertical stack of `PropertyRow`s split label/editor at `LabelFraction`, scrolling like
    `ScrollablePanel` (wheel + scissor clip). Built-in rows: `FloatRow` (a `NumberField`), `BoolRow` (a
    `Toggle`), `TextRow` (a `TextInput`), `ChoiceRow` (a `Dropdown` over a fixed set of option strings, get/set
    delegates over the selected option like `TextRow`), `ReadOnlyRow` (a polled display string, no input).
    Each row polls its getter every `Update` unless the user is mid-edit/scrub/focus on that row's child widget
    (a `ChoiceRow` polls only while its list is closed, so an in-progress pick is never stomped), so external
    changes (undo, another editor) stay in sync without a change-event bus. A `ChoiceRow`'s setter fires only on
    a real change, so re-picking the already-selected option closes the list without writing.
    `PropertyGrid.HasActiveEditor` ORs each row's own `PropertyRow.HasActiveEditor` (`false` on the base
    `PropertyRow`, `FloatRow` while its `NumberField` is `IsEditing`/`IsScrubbing`, `TextRow` while its
    `TextInput.IsFocused`, `ChoiceRow` while its `Dropdown.IsOpen`), an allocation-free aggregate a host walks
    once per frame to gate a global keyboard chord or hotkey on any row's in-progress edit generically, instead
    of naming one specific row (`KhaozEngine.MapEditor`'s shortcut handler is the reference consumer). The grid draws in
    two passes (every row's label+editor, then a late overlay pass), so a `ChoiceRow`'s open option list draws
    ABOVE the rows below the selector rather than being overpainted by them; the list still draws inside the
    grid's own scissor, so it clips at the grid bounds. A host that needs the list to spill past the grid calls
    `Dropdown.DrawOverlay` itself after the grid's `Draw`. Row labels and a
    `ReadOnlyRow`'s display string truncate to their column via `GuiDraw.TruncateWithEllipsis` (the longest
    prefix that fits plus three ASCII dots, never the single-glyph ellipsis, which may not be baked into a font
    atlas) instead of running under the neighbouring cell or getting hard-cut by the scissor mid-glyph. A row
    fully scrolled out of view is skipped entirely, so it neither hit-tests nor reserves off-view input. Wheel
    scrolling moves `WheelRowsPerNotch` rows per notch (default 3, matching `TreeView`'s knob for the same
    side-by-side feel). A row that ran last frame but is culled this frame (scrolled out of view) is
    `Deactivate()`d exactly once as it leaves: the base `PropertyRow.Deactivate()` is a no-op, but `FloatRow`
    cancels its `NumberField` edit, `TextRow` unfocuses its `TextInput`, and `ChoiceRow` closes its `Dropdown`,
    so a focused, mid-edit, or open-list row cannot keep consuming input the grid no longer routes to it.
    Each `PropertyRow` carries an optional `Description` (`LocalizedText?`, null means no tooltip), and
    `PropertyGrid` tracks the row under the pointer during `Update` as `HoveredRow` (`PropertyRow?`, null
    when the pointer is over no row or in the gap between rows) plus a public `RowLabelBounds(int)` (was
    private) returning that row's label rect. A host draws its own `Tooltip` after the grid's `Draw`
    (escaping the grid's scissor, the same pattern `PatchNotesView` and `RoomGui` use), anchored to
    `RowLabelBounds` of `HoveredRow`'s index, showing `HoveredRow.Description` immediately on hover with no
    delay infra. `HeaderRow` is a `PropertyRow` with no getter/setter and `SpansFullWidth` true: a
    full-width label band with a distinct background fill and a 24f row height (vs the default 28f), used
    to break a long inspector into named sections ("Water", "Noise", "Identity", "Transform", ...) - the
    grid skips the label/editor split for any row whose `SpansFullWidth` is true, so `HeaderRow` needs no
    special-casing in the grid beyond that flag. `PropertyGrid.EditorStyle` (`GuiStyle`, default
    `GuiStyle.Default`) is pushed into every row's inner widget (`NumberField`/`Toggle`/`TextInput`/
    `Dropdown`) as rows are added and whenever it is reassigned, so switching a grid to `GuiStyle.Modern`
    restyles every row's editor in one assignment. `ReadOnlyRow` and `HeaderRow` have no inner widget and
    ignore it. `HeaderBandColor` (default `GuiTheme.Default.Surface`) is the header row's fill, drawn via
    `GuiDraw.FillStyled` against `EditorStyle` so it picks up `GuiStyle.Modern`'s rounded corners.
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
- **Patch notes (`PatchNotesLoader`/`PatchNotesParser`/`PatchNotesDocument` + `PatchNotesView`/`PatchNotesScreen`/
  `PatchNotesTheme`/`PatchNotesStrings`).** Renders a game's player-facing `docs/PLAY_CHANGELOG.md` in-game, in
  the shared changelog style (`docs/CHANGELOG-STYLE.md`): `---`-separated dated builds grouped under
  New/Major/Minor/Rebalance/Bug, with backtick-wrapped upgrade/entity/item names. `PatchNotesParser.Parse(text)`
  turns the markdown into an immutable `PatchNotesDocument` (`Builds` of `PatchNotesBuild`, each a
  `PatchNoteGroup` list of category-labelled `PatchNote`s decomposed into plain/backtick `PatchNoteSpan`s); a bad
  or missing document parses to `PatchNotesDocument.Empty`, never throws. `PatchNotesLoader.Load()` /
  `Load(Assembly, baseDirectory?)` reads `PLAY_CHANGELOG.md` disk-first (next to the running app) then falls
  back to an embedded resource of the same name in the given assembly, mirroring
  `KhaozEngine.Content.ConfigLoader`'s disk-then-embedded convention without a Gui-to-Content dependency; every
  IO attempt is swallowed so a read failure just falls through. `PatchNotesView` is the collapsible, scrollable
  presenter (one build per header, tap to expand/collapse, wheel/drag scroll, `CloseRequested` latches on
  close-tap or Escape). Its per-note word-wrap layout is cached (keyed on content width + the measurer/font
  identity), so `Update`/`Draw`/scrollbar sizing share one computation per note per frame instead of
  re-wrapping a static document's notes on every call - the cache is cleared wholesale whenever the width or
  font changes (e.g. a resize), so it never serves a stale layout. `PatchNotesScreen` is the drop-in modal
  `Screen` wrapper (`SettingsScreen`-style 0.18s
  transitions, always modal) for `ScreenStack` games. `PatchNotesTheme` supplies the palette (panel/header
  fills, text, muted text, code-span accent, and a `CategoryColor(PatchNoteCategory)` per-category badge color -
  Rebalance a warm amber) with a `Default` preset; `PatchNotesStrings` supplies the chrome text (title/close/
  empty-state, per-category labels) as `StringId`s with a built-in English `IStringCatalog` fallback so an
  unlocalized build renders correctly.
- `DiagnosticsOverlay` (+ `DiagnosticsOverlayTheme`, `OverlayRow`/`OverlaySection`) - a reusable in-game
  telemetry HUD, a pure presenter modeled on `UpdateOverlayView`. The game assembles sections each frame and
  feeds them via `SetSections`, or registers a provider once with `SetSectionsProvider(provider, refreshInterval)`
  and lets `Update` rebuild them on that interval (a built-in throttle: polls immediately, then every interval
  seconds; interval 0 = every frame; null provider detaches) instead of a hand-rolled per-frame timer around
  `SetSections`; `Update(InputState, dt)` toggles on `Theme.ToggleKey` (default F1; optional
  gamepad button) and fades (headless-testable, `InputState.Empty` inert); `Draw` renders a corner panel
  (`Theme.Corner`) of titles + right-aligned values. `PerformanceSection(FrameStats)` /
  `PassTimingsSection(PassTimings)` / `NetworkSection(in ClientNetStats)` populators cover the common cases
  (from `KhaozEngine.Diagnostics`). `DrawStatsSection(in RenderFrameStats)` (the `Primitives` frame counters)
  adds a "Draw stats" section (draw calls, instances, triangles, quads, flushes, texture switches, upload KB).
  `PassTimingsSection` lists one row per pass name (in first-sampled order)
  with that pass's rolling avg/min/max milliseconds - CPU encode time, not true GPU time (see the
  `KhaozEngine.Render3D` README / `docs/USING-KHAOZENGINE.md`).
  `Bounds` is the last-drawn panel rect (empty when hidden/faded-out), so a caller can place an `OverlayLegend`
  at `Bounds.Right` + a gap to sit a second panel directly beside it.
- `DiagnosticsHud` - turn-key wiring of the frame-cost HUD: bundles a `FrameStats` meter, an optional
  `PassTimings` meter (3D hosts), and a `DiagnosticsOverlay` behind one object, with a throttled provider that
  assembles the Performance / Draw-stats / Pass-timings / (optional) Network sections. Call `Update(input, dt)`
  once per frame (samples FPS, handles the toggle + fade), feed `SetDrawStats(in RenderFrameStats)` the aggregate
  and (3D) sample its `PassTimings`, then `Draw`. Hidden by default, and while hidden the provider builds nothing,
  so the only cost is the surfaces' always-on counter increments. `SetNetStatsSource(Func<ClientNetStats?>)` opts a
  Network section in and out with the active screen. `GameApp`/`GameApp3D` wire one automatically (F1).
- `TextEntry` - headless key→char text-entry helper (US layout + shift), used by `TextInput`. No SDL plumbing.
  Ctrl/Super (Cmd) held suppresses character entry so shortcut chords like Ctrl+V / Cmd+V paste instead of typing.
  Acts on `InputState.WasTyped` (press edge OR OS auto-repeat tick), so a held Backspace or character key repeats at
  the OS rate; the chord suppression still blocks repeated character entry while Ctrl/Cmd is held. Keypad (numpad)
  keys type their digit/dot/operator characters shift-independently (a keypad has no symbol row) via the
  `Keypad0`..`Keypad9`/`KeypadDecimal`/`KeypadAdd`/`KeypadSubtract`/`KeypadMultiply`/`KeypadDivide`/`KeypadEqual`
  members on `KhaozEngine.Windowing.Key`. A physical keypad Enter is folded into the regular `Enter` by
  `AppWindow`, so it commits/confirms identically everywhere, with no separate `KeypadEnter` member.
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

**Editor inspector widgets (`NumberField` / `TreeView` / `PropertyGrid`).** The three widgets an inspector
panel is built from: a scrubbable/typeable number field, a hierarchy outline, and the grid that hosts typed
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
