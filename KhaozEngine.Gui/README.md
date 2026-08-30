# KhaozEngine.Gui

Immediate-mode + retained UI on the custom MonoGame-free stack.

**Localized text:** the player-facing text sinks (the `Label` / `Button` widgets, `GuiSurface.Label` /
`Button` / `StatChip`, `Tooltip.Show`, `ContextMenu.Open` plus `ContextMenuEntry.Of`, and `PopupPanel` - its `TitleContent` / `DismissContent` /
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
  `Remove` unloads the screen and leaves it in the terminal `Hidden` state with its exit request cleared, so a
  screen pulled out mid-frame (its own transition-off completing, or another screen's `Update` removing it)
  cannot get one more `Update` out of the loop's scratch copy after its content is gone. Re-adding that same
  instance remounts it and re-runs its entry transition.
- `Screen` - base UI surface: `Update(dt, receivesInput)` (returns whether it consumed input) + `Draw(SpriteBatch)`,
  with `DrawOrder` / `PassUpdateThrough` / `AlwaysReceivesInput` / transitions. **The dormant-overlay trap:** a
  screen that stays mounted all the time but is only sometimes showing something MUST return `false` from
  `Update` while dormant (received input != consumed it), or it silently blocks every screen below for as long as
  it sits in the stack. `UpdateOverlayScreen` is the reference implementation (recomputes `PassUpdateThrough` from
  whether it must be modal each frame, a required update or the apply step rather than mere visibility, and returns
  consumed only when modal or when its trigger or its dismiss fired). See `Screen.Update`'s XML doc and
  `docs/USING-KHAOZENGINE.md` for the full contract.
- `IScreenComponent` + `ScreenComponentList` (13.7.0) - the composition unit BELOW `Screen`, mirroring what
  `Ecs.ISystem` is below `World`. A component is one HUD element / overlay / input controller / presenter:
  `bool Update(dt, receivesInput, bounds, input)` + `void Draw(batch, bounds)`, with `LoadContent`/`UnloadContent`
  as DEFAULT interface members so a component owning no assets omits them. A host holds a `ScreenComponentList`
  and fans out to it once per lifecycle moment, so a screen's size stops being a function of how many
  collaborators it has. Registration order IS z order (first added draws underneath and is offered input last),
  `Update` runs top-down and stops at the first component that consumes input, `Draw` runs bottom-up: the same
  routing `ScreenStack` applies between screens, one level down, guarded latch included. **The `Update` bool means
  CONSUMED INPUT, not "am I visible"** - the same dormant-overlay trap as `Screen` above, one level down, and a
  component that returns a bare `true` starves every component below it and then every screen below its screen.
  `bounds` is a per-call parameter rather than a property, so a host that re-reads its viewport each frame needs no
  resize hook. It is an INTERFACE, not a base class, so a consumer keeps its own domain base and adds this on top.
  Not a widget layer: the retained widgets and `GuiSurface` are still the leaf level and a component typically owns
  several of them. `Screen` itself is unchanged, hosts compose the list as a field.
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
  - `Button` - click via `IsTapIn`, hover/press visuals. `LabelScale` (default `1f`) scales the caption only
    (`Bounds` and the hit-test are unchanged), forwarded into the shared `GuiDraw.DrawButton`.
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
    fades the whole grid. Built-in slot content is available too: set an `IconAtlas` on the grid and hand each
    slot a `SlotContent` (icon id, tint, cooldown fraction 0..1, stack count, disabled flag) through
    `SetContent`/`ClearContent`/`ClearAllContent`. `Draw` then paints, per slot, the icon (greyed when disabled),
    a radial cooldown sweep (12 o'clock clockwise, boundary along the slot edge), and the stack count
    bottom-right, between the slot frame and the `DrawSlotContent` hook (so custom painting still composes on
    top). Content is stored sparsely, so it survives `Count` changes. Two knobs let a game override what the
    built-in painter draws without giving it up: `CountFormatter` (a `Func<int, SlotContent, string?>` invoked as
    slot index + content, its return drawn verbatim in place of the raw `Count`, invoked for every slot with
    content regardless of `Count` so a formatter can suppress or show a zero count on purpose, null keeps today's
    "digits only when `Count` is greater than zero" behaviour) and `FallbackIconId` (an icon id drawn when a
    slot's `IconId` is set but the atlas cannot resolve it, for a roster that names icons the atlas has not
    registered yet. A null `IconId` never falls back, it still means no icon). The immediate-mode `GuiSurface`
    exposes the same primitives standalone: `Image` (an arbitrary texture, bypassing the icon atlas) and
    `CooldownOverlay` (the radial sweep). Drag-and-drop is opt-in through the `Update(Pointer, GuiDragContext?)`
    overload (below): `BeginDragPayload(slot)` makes the grid a source (return null for a slot that cannot be
    picked up), `CanAcceptDrop(slot, payload)` makes it a target and refuses before the release,
    `DropTargetSlot`/`DropTargetAccepted` drive the drop highlight, `DroppedSlot`/`DroppedPayload`/`OnSlotDropped`
    report a commit, and `DraggingSlot` is the origin slot for the life of the drag (dim it while its contents
    are in the air). `PressOriginSlot` is the slot the held press BEGAN in and, unlike `PressedSlot`, survives the
    pointer leaving that slot.
  - `GuiDragContext` - drag-and-drop that SPANS widgets (pick an icon out of one `SlotGrid` slot, drop it on
    another grid, an equipment rack, or a bare "destroy" rect). One live drag at a time, in its own object the
    participating widgets consult rather than state on any one of them. Per frame: `BeginFrame(pointer, dt)`,
    your widget updates, `EndFrame()`, then `Draw(batch, white, font)` last so the ghost floats over everything.
    A `DragPayload` carries an OPAQUE game `Token` the engine never inspects (same discipline as `SlotContent`)
    plus `SourceId`/`SourceIndex` and an optional `DragGhostPainter`. A target calls
    `OfferTarget(id, index, accepted)` every frame the drag hovers it, so a refusal shows BEFORE the release
    (`ShowRejectOverlay`) instead of being accepted and undone. The first offer of the frame wins, matching
    `ScreenStack`'s top-to-bottom routing. `OfferTargetIn(rect, ...)` hit-tests for you, which makes a bare rect a
    drop target with no widget. A commit sets `WasDropped`/`LastDrop` and fires `OnDropped`. A release over
    nothing (or `Cancel()`, the Escape path) sets `WasCancelled` and eases the ghost back to its source rect over
    `ReturnDuration` (0 disables). Grabbing calls `Pointer.ConsumeGesture`, so the release that drops cannot also
    tap what is underneath. `DragThreshold` (6 px, matching `TreeView`) is the shared arm rule via
    `ShouldBeginDrag`. `TreeView`'s own reorder is a different gesture and stays as it is: same-widget ordinal
    reorder is `TreeView`, cross-widget payload transfer is this.
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
    widget). A tap under 3 draw units of travel, with no real value change already scrubbed this gesture, opens
    typing mode (`TextEntry` with a digits/one-minus/one-dot filter validated against the buffer TextEntry is
    accumulating THIS call, so a multi-key frame or a paste admits at most one dot, not the stale pre-frame
    buffer). Enter commits (parsed, clamped to [`Min`, `Max`], rounded to `Decimals`), Escape cancels, and a tap
    outside commits like Enter. Disabling the field while it is editing also cancels (buffer discarded, `Value`
    unchanged), never commits. `Value`'s setter clamps to [`Min`, `Max`] on every assignment, not only through
    `SetValue` - a direct write (e.g. a host polling in an external value) now always displays clamped without
    raising `WasChanged`/`OnChanged`. `WasChanged` mirrors the `Update` return. `Opacity` fades the field for a
    host transition. `IsScrubbing` mirrors the grab-gate (true from the inside press to the release, even once the
    cursor strays off the widget) and pairs with `IsEditing`, so a host like `PropertyGrid`'s `FloatRow` can skip
    its external-value poll while either is true instead of stomping a live gesture. `GestureEnded` fires once a
    scrub that moved `Value` releases, or a typed edit commits (never on a cancel), so a host can seal an undo
    gesture at the same boundary. `CancelEdit()` exits typing mode without committing, leaving `Value` at its
    pre-edit value (the same path Escape takes, made directly callable), the hook a host uses to close an
    in-progress edit it is tearing down. Typing accepts numpad (keypad) keys the same as the top-row keys - digits,
    dot, minus - and the FIRST keypad keystroke ends the select-all seed exactly like a top-row one (replace the
    seeded value, not append to it).
  - `Dropdown` - trigger + option list (opens below); two-phase draw (`Draw` trigger / `DrawOverlay` list last).
    Opt-in (default off): `ShowChevron` draws an up/down caret reflecting the open state; `Opacity` fades the whole
    dropdown for a host transition.
  - `TabBar` - horizontal tab bar / segmented control: N evenly-split tabs, exactly one active. A valid tap
    activates a tab and raises `ChangedThisFrame` for one frame (and `Update` returns true), so the caller swaps
    the panel body only on a real change. `ActiveIndex` is settable to restore/persist the selection without
    raising the change signal. Active tab uses `ActiveStyle` (`GuiStyle.Active`), inactive tabs `InactiveStyle`
    (`GuiStyle.Secondary`), labels are `LocalizedText`, and `TabRect(i)` is the pure per-tab layout, independent of
    `TextScale` (default `1f`, scales every tab label only). `Opacity` fades the whole bar for a host transition.
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
    `Placeholder` string is an `[Obsolete]` shim); `Opacity` fades the whole field for a host transition. Text
    wider than the box is clipped to it (caret included) rather than painting over whatever is beside the field,
    and the clip only engages when the content actually overflows, so a field that fits costs no extra flush.
    `NumberField` clips the same way. Neither scrolls the visible window with the caret yet.
  - `Tooltip` - auto-sized floating bubble; `ComputeBounds` (flip/clamp) is a pure, testable layout function.
    Opt-in (default off): a two-column title (`Show(title, titleRight, ...)`), a `ShowTitleSeparator` rule under the
    title, a width cap (`MaxWidth` px and/or `MaxWidthFraction` of the viewport) that word-wraps long body lines
    downward instead of overflowing the viewport (hard-breaking a token longer than the cap), and platform-aware
    dismissal via `Dismiss` (`CallerDriven` desktop-hover vs `TapOutside` touch, driven by `Update(Pointer)`) - the
    dismissal policy is a runtime value, not a compile-time platform branch. Each `TooltipLine` carries its own
    `Scale` (default `1f`, a third optional positional field), and `Tooltip.TitleScale` (default `1f`) scales the
    title row, so one shared font can render a size hierarchy (e.g. a bright 1.0 title over 0.84/0.42 body lines).
    A scaled line still wraps within `MaxWidth` (the word-wrap budget divides by the line's own scale).
  - `ContextMenu` (18.2.0) - a right-click option menu anchored at a screen point: a title band over a stack of
    selectable rows, the OSRS-style option list. `Open(title, entries, screenPoint)` shows it (reopening while
    open just replaces the content and the anchor), `Update(Pointer)` drives one frame, `Draw(batch, white)`
    paints it. Text is `LocalizedText` throughout, resolved ONCE: the title in `Open`, each row through
    `ContextMenuEntry.Of(label, rightDetail, ...)` at construction (the `TooltipLine.Of` precedent), so the draw
    path never re-resolves and a runtime locale switch means rebuilding the entries. A row carries an optional
    right-aligned `RightDetail`, per-row `LabelColor` / `DetailColor` overrides, an opaque `long Tag` that rides
    through selection, and an `Enabled` flag (a disabled row draws at `DisabledColor`, whatever the caller
    tinted it, and refuses both hover and selection). Results are one-frame flags in the `Dropdown.WasChanged`
    mould, all cleared at the top of the next `Update`: `WasSelected` (also `Update`'s return value) with
    `SelectedTag` / `SelectedIndex`, and `WasDismissed` with `DismissPress`, the position the dismissing gesture
    RELEASED at (null for a menu-cancel), which is what a caller reopening on the dismissing gesture anchors the
    next menu at. The pointer dismissal is a LEFT release outside the menu and nothing else, so a right press
    outside an open menu does nothing to it and a caller wanting right-click-to-reopen watches
    `Pointer.IsRightTapIn` itself and calls `Open` again at the new point.
    `Update(InputManager, PlayerIndex? = null)` layers menu-cancel (Escape / gamepad B / Back)
    dismissal on top, and cancel is live from the first frame. `ComputeBounds` / `RowBounds` are pure layout over
    `ITextMeasurer`: the menu's top-left sits at the point and opens down-right, flips to put its BOTTOM at the
    point when the bottom would overflow, and the viewport clamp runs LAST and wins over the flip, so a point too
    close to an edge yields a menu pinned inside the `ContextMenuMetrics.Margin` box that may cover the point
    rather than sit at it. A menu too big for that box pins to the left and top margins and overflows the right
    and bottom edges. An open menu reserves its bounds through `Pointer.BlockRegion` (the `Dropdown`
    precedent) so the world beneath cannot be clicked through it, and the gesture that OPENED the menu can
    neither dismiss it nor select a row in it. Assign `Viewport` (the design size) before updating or drawing:
    `Draw` throws while it is `Vector2.Zero` rather than silently pinning the menu into the corner, and throws
    too on a menu built through the measure-only `ContextMenu(ITextMeasurer, ITextMeasurer)` constructor, which
    exists so a headless test can drive the whole interaction with no GPU device and no baked font.
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
    Geometry is exposed via `CurrentBounds`/`ContentBounds` (== `Bounds` with no knob set). Opt-in height glide
    (10.121.0, `HeightGlideSeconds`, default 0 = off): when the caller-owned `Bounds` height changes while the
    panel is visible (e.g. a content-driven height recompute after ItemCount changes), `EffectiveHeight` eases
    toward the new target over that many seconds instead of snapping, via the new dt-fed
    `Update(Pointer, InputState, float dt)` overload (the legacy no-dt `Update(Pointer, InputState)` overload never
    glides). Always snaps on the first update and whenever the panel is fully hidden (`TransitionAlpha <= 0`), and
    never fights an active drag-resize.
  - `TreeView` - scrollable outline over `TreeNode` roots (a `LocalizedText` label, children, an `Expanded`
    flag, a caller-owned `Tag`). `VisibleRows()` is the depth-first walk skipping collapsed subtrees, rebuilt into
    a shared cached list on every call - materialize the result (`ToArray`/`ToList`) before the next call if you
    need to keep it. A tap in a row's caret zone (the `Indent`-wide band at its depth) toggles expansion for a
    node with children, a tap elsewhere in the row selects it (`Selected`, `OnSelected`). The wheel scrolls
    clamped to the content, continuous (`ScrollDelta * WheelSpeed` pixels, no per-notch rounding, the
    `ScrollablePanel.WheelSpeed` idiom) - `WheelSpeed` is `RowHeight * WheelRowsPerNotch` (default 3 rows per
    wheel unit) - and rows are scissor-clipped to `Bounds`. A held press that clears `DragThreshold` (default 6
    pixels) becomes a same-parent drag-and-drop row reorder instead of a tap: a valid drop fires
    `OnReordered(node, oldIndex, newIndex)` and `WasReordered` goes true, an insertion line marks the live target,
    Escape aborts with no drop, and a cross-parent or off-tree release is rejected. The widget only reports the
    move, never mutating `Roots`/`Children` itself, so the host applies it and rebuilds the tree. The wheel scrolls
    even while a drag is armed: the drop geometry (`RowAt`/`RowBounds`) reads the live `ScrollOffset`, recomputed
    the same frame right after the wheel updates it, so a long list can scroll mid-reorder instead of freezing.
    Setting `CanReorder` (a `Func<TreeNode, bool>?`, default null) gates which rows may drag, consulted on the
    press-origin row before arming: a rejected row never grabs, shows no insertion line, and fires no `OnReordered`,
    while null leaves every row reorderable.
    `ScrollTo(TreeNode)` brings a node into view: expands every collapsed ancestor so it
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
    fully scrolled out of view is skipped entirely, so it neither hit-tests nor reserves off-view input. A row
    only PARTIALLY visible (its cell straddles `Bounds`' top or bottom edge) still runs, but the cell handed to
    its `Update` is clamped to `Bounds` first, so a sliver a Draw-time scissor already clips cannot still claim a
    tap or drag beyond it. `Draw` is unaffected, it re-sets the row's widget bounds from the full (unclamped)
    `RowEditorBounds` right before drawing. Wheel scrolling is continuous (`ScrollDelta * WheelSpeed` pixels, no
    per-notch rounding, the `ScrollablePanel.WheelSpeed` idiom) - `WheelSpeed` is `(average row height) *
    WheelRowsPerNotch` (default 3 rows per wheel unit, matching `TreeView`'s knob for the same side-by-side feel).
    A row that ran last frame but is culled this frame (scrolled out of view) is
    `Deactivate()`d exactly once as it leaves: the base `PropertyRow.Deactivate()` is a no-op, but `FloatRow`
    cancels its `NumberField` edit, `TextRow` unfocuses its `TextInput`, and `ChoiceRow` closes its `Dropdown`,
    so a focused, mid-edit, or open-list row cannot keep consuming input the grid no longer routes to it.
    `FloatRow.GestureEnded` (a direct pass-through of `NumberField.GestureEnded`) fires once a scrub or typed-edit
    commit on its field finishes, so a host can seal an undo gesture at that boundary - `MapEditorScene` wires
    every terrain/transform/scatter `FloatRow` it builds to `EditorDocument.SealGesture` through this hook, so
    scrubbing one field then another produces two undo steps instead of coalescing into one through the underlying
    command's same-gesture merge.
    Each `PropertyRow` carries an optional `Description` (`LocalizedText?`, null means no tooltip), and
    `PropertyGrid` tracks the row under the pointer during `Update` as `HoveredRow` (`PropertyRow?`, null
    when the pointer is over no row or in the gap between rows) plus a public `RowLabelBounds(int)` (was
    private) returning that row's label rect. A host draws its own `Tooltip` after the grid's `Draw`
    (escaping the grid's scissor, the same pattern `PatchNotesView` and `Room2DGui` use), anchored to
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
  branch. Overriding `TitleFor`/`BodyFor` still fully replaces the text.
  A second bound key (`Theme.DismissKey`, default `Escape`, plus `DismissButton` / `DismissKeyLabel`) **dismisses**
  the panel for a state the player may decline (`UpdateOverlayView.IsDismissible`: `UpdateAvailable`,
  `ReadyToApply`, `Failed`, never the in-flight `Downloading`/`Applying` and never a required update), which is
  the way out of a repeatedly failing apply. The dismissal is remembered per state, so a recheck landing back on
  the same offer stays hidden and the panel returns only at a state that was not declined.
  `View.ResetDismissed()` clears it, and the theme draws a third `HintFor` line advertising the key
  (`update.overlay.dismiss.hint`). `theme.ToUpdaterUiOptions(...)`
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
- **Connection-outage screen (`ConnectionStatusController`/`ConnectionStatusPolicyOptions` +
  `ConnectionStatusSignals`/`ConnectionStatusView` + `ReconnectScreen`/`ReconnectAction` +
  `ReconnectScreenTheme`/`ReconnectStrings`).** A reusable takeover for a dropped or updating server connection,
  split model/view like `PatchNotesLoader`/`PatchNotesView`. `ConnectionStatusController` is the headless brain:
  feed it a primitive `ConnectionStatusSignals` (`Phase`, `PlannedUpdate`, `EtaUtc`, `Attempt`,
  `SecondsUntilRetry`, an optional message `StringId`) every frame via `Update(signals, dt)`, and it returns a
  `ConnectionStatusView` whose `Mode` is `None`, `Banner` (the consumer draws its own, none ships), or `Screen`.
  A planned update escalates to `Screen` immediately, a generic drop shows `Banner` and escalates only past
  `ConnectionStatusPolicyOptions.EscalateAfterSeconds` (default 6s), and `MinScreenDurationSeconds` (default
  1.5s) holds the screen once shown so a sub-second reflap cannot flicker it away and back. The controller is
  netcode-free, with no dependency on `NetWorld`/`Netcode`/`ServerStatus`. `ReconnectScreen` is the matching
  `Screen` for `ScreenStack`, drawn from only a 1x1 white texture and a `SpriteFont`: a full-window scrim, a
  title chosen by `Kind`, a large m:ss countdown while a future `EtaUtc` holds (clamped at zero back to the
  reconnecting title), attempt/retry lines otherwise, a reassurance line, an asset-free dot-ring spinner, and an
  optional `ReconnectAction` button row. `Create(white, font, viewport, currentView, theme?, actions?)` polls
  `currentView` every frame rather than gating on `Mode`, so the caller controls visibility by push/pop on the
  stack. `ReconnectScreenTheme` (a settable class, `Default` derived from `GuiTheme.Default`) carries the scrim
  colour and alpha, every text and spinner colour, the titles and reassurance as `LocalizedText`, the
  attempt/retry format keys, an optional `DrawBackground` hook, and the button/layout metrics.
  `ReconnectStrings` supplies the engine-owned `reconnect.*` keys with a built-in English fallback.
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
  `AppWindow`, so it commits/confirms identically everywhere, with no separate `KeypadEnter` member. The optional
  `filter` is a `Func<string, char, bool>` (buffer accumulated so far THIS call, candidate char) rather than a bare
  `Func<char, bool>`, so a stateful filter (e.g. `NumberField`'s "at most one dot") is validated against every char
  this same call already admitted - a multi-key frame or a paste - instead of a stale pre-call buffer.
- `GuiDraw.TruncateWithEllipsis(text, maxWidth, measureWidth)` - fit a single line of text to a width: returns
  the text unchanged when it fits, otherwise the longest prefix that fits with a trailing `"..."` appended
  (three ASCII dots, binary-searched against the caller-supplied measure function, e.g.
  `s => font.Measure(s).X`, so it is pure and headless-testable). When not even the dots fit, `"..."` is still
  returned. `PropertyGrid` cell/label text and the map editor's status strip draw through it. The only public
  member of `GuiDraw` (the rest is internal widget plumbing).
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
- **Toast notifications (`Toast`/`ToastKind` + `ToastStack`/`ToastTheme`/`ToastView`).** A headless, retained
  stack of transient/sticky notification popups (status messages, loot pickups, connection state), drawn
  corner-anchored with tap-to-dismiss. `ToastStack` is the model: `Show(LocalizedText, ToastKind = Standard,
  float? duration = null, string? key = null)` adds a toast (`ToastKind.Standard`/`Warning`/`Danger` pick the
  palette). `duration` `null` defaults to `DefaultDuration` (6s), `<= 0` makes it sticky (`ShowSticky` is the
  shorthand - never expires on its own, only a tap dismisses it, and it draws no countdown timer bar). A
  non-null `key` shared with an already-active toast REPLACES that toast in place, at its current index, with
  fresh state, instead of stacking a new one - the mechanism for a repeated status line ("reconnecting" then
  "reconnected") that stays pinned at one slot without reordering or growing the stack. `Dismiss(Toast)` /
  `Dismiss(int)` remove one toast, `Clear(string key)` removes by key, `ClearAll()` empties the stack.
  `MaxVisible` (default 5) caps how many toasts show at once. Over the cap the oldest NON-STICKY toast is
  evicted first, and only once every remaining toast is sticky does the oldest sticky one get evicted, so a
  flood of transient toasts never buries a sticky warning. `Update(float realDt)` counts down every non-sticky
  toast's `Remaining` and enforces `MaxVisible`. It MUST be fed a raw, unscaled frame delta (`Frame.Dt` /
  `GameClock.RealDeltaSeconds`), never a sim-scaled dt, so toasts keep counting down at real speed while the
  game is paused or slowed. `ToastView` is the corner-anchored layout/input/draw widget: `GetToastBounds(index)`
  is the shared layout both `Update(Pointer)` (tap-dismiss via the press-origin invariant, blocking each
  toast's region for a beneath layer via `Pointer.BlockRegion`, and consuming the gesture on a dismissing tap
  so it can't also fire a widget underneath) and `Draw(SpriteBatch, Texture2D, SpriteFont)` (themed fill/border
  per `ToastKind`, word-wrapped vertically centered text, and a shrinking countdown timer bar on non-sticky
  toasts only) read, so hit-testing and pixels always agree. `ToastTheme` supplies the per-`ToastKind` palette
  (`ToastPalette`: background/border/timer-bar/text) and the layout metrics (`Width`, `MinHeight`, `Gap`,
  padding, `TimerBarHeight`, `BorderThickness`, margins, and the anchor `Corner`, default
  `OverlayCorner.TopRight`). Unlike `GuiTheme.Default`, `ToastTheme.Default` is not a shared, assignable
  instance - it hands back a fresh default `ToastTheme` on every access, so reskinning every toast in a game
  means building one `ToastTheme` instance and passing it into every `ToastView` you construct. See
  `docs/USING-KHAOZENGINE.md` for both hosting patterns (a plain single-`Pointer` host and a permanent
  `ScreenStack` overlay screen).

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
borderless style stays borderless. Text drawn through the batch in that pass also snaps its origin (the whole
block's ascent baseline, once per `DrawString`, so every glyph of a word stays on one baseline - pair with a
`DpiFont` from `KhaozEngine.Render2D` for a crisp atlas).

Text wrap/alignment lives in `KhaozEngine.Render2D.TextLayout` (over the `ITextMeasurer` seam, so the layout
math is headless-testable). Clipping uses `SpriteBatch` scissor (`SetScissor`/`ClearScissor`, DPI-aware, and
nesting: a clipping widget drawn inside another clipping widget is bounded by both). Ported
from `KhaozEngine.Screens`/`UI` (game-specific layout coupling dropped). Built on `KhaozEngine.Windowing`
(Pointer/Input) + `KhaozEngine.Render2D` (SpriteBatch/SpriteFont/Texture2D). Part of the MonoGame-free engine.
