# Radial menu and source-target use context

Date: 2026-09-08

Status: Complete in 18.36.0

Program: [#854](https://github.com/APKiwiOrg/KhaozEngine/issues/854)

First consumer: [Grimhollow #155](https://github.com/APKiwiOrg/Grimhollow/issues/155)

## Goal

Add two game-agnostic GUI mechanisms needed by Grimhollow and plausible in any KhaozEngine game:

- an interaction-anchored radial option menu with a generic bottom choice strip
- an opaque source-target use context that carries one selected thing into a later target gesture

The engine owns presentation and input mechanics only. It does not gain recipes, crafting, item catalogs,
stations, skill requirements, quantities, action timing, inventory mutations, persistence, or economy rules.

## Why this belongs in KhaozEngine

The existing `ContextMenu`, `Dropdown`, `GuiDragContext`, and `SlotGrid` already centralize equivalent interaction
mechanics. A radial menu has the same cross-game responsibilities: polar hit testing, viewport clamping, opening
gesture latching, click-through blocking, keyboard and gamepad navigation, localization, icons, disabled choices,
and themeable drawing. Reimplementing those in each game would repeat input and layout code rather than game rules.

The source-target context is the click counterpart of `GuiDragContext`. It tracks a relationship between two
gestures while carrying opaque caller data. It can support using one inventory item on another, applying a dye,
selecting a key for a door, targeting a spell component, or Grimhollow choosing a knife and then a log. None of
those meanings enters the engine.

Crafting itself stays game-side. `KhaozEngine.Items` deliberately owns only container arithmetic and leaves item
identity, use effects, equipment, economy, and other meaning to consumers. A `KhaozEngine.Crafting` package would
cross that boundary before two games had proved a common rules contract.

## Existing seams

The implementation builds on shipped APIs:

- `Pointer.IsTapIn`, `Pointer.IsReleasedOutside`, gesture consumption, and blocked regions preserve the press-origin
  invariant and prevent click-through.
- `InputManager` supplies keyboard and gamepad navigation without reading raw Silk.NET state.
- `LocalizedText` and `IconAtlas` keep player-facing labels localizable and icons caller-owned.
- `PrimitiveRenderer.DrawFilledArcBand`, `DrawArc`, and `SpriteBatch.DrawQuad` can draw the wheel through existing
  Render2D primitives. No shader or GPU backend change is required.
- `GuiTheme` supplies semantic defaults while a caller can replace every radial colour.
- `GuiDragContext` is the precedent for opaque payloads, per-frame completion flags, and caller-owned meaning.

## Radial menu data model

The public value types are:

```csharp
public readonly record struct RadialMenuEntry(
    LocalizedText Content,
    long Tag,
    string? IconId = null,
    bool Enabled = true,
    LocalizedText Detail = default,
    long InitialChoiceTag = 0);

public readonly record struct RadialMenuChoice(
    LocalizedText Content,
    long Tag,
    bool Enabled = true);

public readonly record struct RadialMenuSelection(long EntryTag, long ChoiceTag);

public readonly record struct RadialMenuChoiceChange(long EntryTag, long ChoiceTag);
```

`Tag`, `IconId`, and the relationship between an entry and a choice are opaque caller data. `Content` and `Detail`
are localization sinks. Labels resolve when a menu opens, matching `ContextMenuEntry.Of`, so the draw loop never
resolves resources repeatedly.

A page accepts one to eight entries. Zero entries is invalid because an empty radial menu has no interaction.
More than eight is invalid because shrinking wedges until labels and hit regions fail is not a useful fallback.
The caller groups a larger catalog into categories and opens another page after a category selection. That keeps
category meaning outside the widget.

The choice strip is optional and accepts zero through eight caller-defined choices. More than eight is rejected for
the same fixed-budget reason as the wheel. Each entry carries its own
initial choice tag, which lets a recipe wheel remember a separate quantity for every recipe without teaching the
widget what a quantity is. Changing the strip updates only the active entry. Selecting a wedge returns both tags in
one `RadialMenuSelection`, so the ordinary path remains one click.

## Radial menu public surface

The retained widget has this shape:

```csharp
public sealed partial class RadialMenu
{
    public RadialMenuMetrics Metrics { get; set; } = RadialMenuMetrics.Default;
    public RadialMenuTheme Theme { get; set; } = RadialMenuTheme.Default;
    public Rect SafeBounds { get; set; }
    public bool IsOpen { get; }
    public int HoverIndex { get; }
    public int ActiveIndex { get; }
    public bool WasSelected { get; }
    public RadialMenuSelection Selection { get; }
    public bool WasChoiceChanged { get; }
    public RadialMenuChoiceChange ChoiceChange { get; }
    public bool WasDismissed { get; }
    public Rect Bounds { get; }

    public void Open(
        LocalizedText title,
        IReadOnlyList<RadialMenuEntry> entries,
        Vector2 anchor,
        IReadOnlyList<RadialMenuChoice>? choices = null);

    public bool SetEntryChoice(long entryTag, long choiceTag);
    public bool Update(Pointer pointer, float dt);
    public bool Update(InputManager input, float dt, bool focused, PlayerIndex? player = null);
    public void Close();
    public void Draw(
        SpriteBatch batch,
        Texture2D white,
        SpriteFont font,
        IconAtlas? icons = null);
}
```

The declarations above are the accepted core shape. The complete shipped surface, including retained-label accessors,
pure geometry helpers, metrics, and theme fields, lives in `KhaozEngine.Gui/README.md` and
`docs/USING-KHAOZENGINE.md`. `SetEntryChoice` changes stored widget state without firing a player-change event. It
is the server-sync path for a consumer that receives preferences after constructing entries.

## Geometry and anchoring

The requested anchor is normally the interaction pointer or projected world target. The wheel computes one centre
that keeps its outer radius, detail line, and entire choice strip inside `SafeBounds` plus the configured margin.
Clamping moves the whole composition together. It never clips the footer separately or changes wedge order.

Entry zero begins at twelve o'clock and entries proceed clockwise in caller order. Every wedge shares one inner
radius, outer radius, and angular gap. The inner disc carries the title and the active entry detail. Icons sit near
the middle radius and labels sit below them within the same wedge. A missing icon leaves the text centred rather
than drawing a fallback. Icon fallback remains the caller's `IconAtlas` policy.

`RadialMenuMetrics` owns the inner and outer radii, angular gap, icon size, label scale, detail gap, footer gap,
footer button size, composition margin, border thickness, shadow offset, and sheen speed. Defaults must fit inside
a 960 by 540 design surface with eight entries and four footer choices.

Pure helpers calculate the clamped centre, complete bounds, wedge angle range, wedge label point, footer button
rectangles, and entry at a point. The draw and update paths consume those helpers rather than restating geometry.

## Pointer interaction

Opening arms an opening-gesture latch matching `ContextMenu`. The gesture that caused `Open` can neither select a
wedge nor dismiss the new menu. The first fresh later gesture disarms it.

While open, the complete composition blocks the shared pointer. A point between the radii and inside a wedge's
angular range activates that wedge. Disabled wedges can be active for detail display but cannot be selected.
A tap on an enabled wedge closes the menu and raises `WasSelected` for that frame. The returned selection includes
the wedge tag and that wedge's current choice tag.

A tap on an enabled footer choice changes the active entry's choice, leaves the menu open, and raises
`WasChoiceChanged`. A disabled footer choice does nothing. A release outside the complete bounds dismisses the
menu and consumes the gesture. `Close` is idempotent.

All one-frame flags clear at the next update, including while closed. The menu allocates when it opens and not on
steady-state update or draw after warming.

## Keyboard and gamepad interaction

The opt-in `InputManager` overload keeps pointer behaviour live and adds focused navigation:

- Left and Right cycle enabled wedges clockwise or counter-clockwise.
- Down moves focus from the wheel to the choice strip when a strip exists.
- Up returns focus to the wheel.
- Left and Right cycle enabled footer choices while the footer owns focus.
- menu-select chooses the focused wedge or applies the focused footer choice
- menu-cancel closes the menu

Navigation wraps. Disabled entries and choices are skipped. Opening seeds wedge focus to the first enabled entry,
or index zero when every entry is disabled so their details remain inspectable. Pointer hover takes temporary
visual precedence without destroying the keyboard focus position.

## Glass-like presentation

The requested look is a themed illusion, not a backdrop compositor. The widget draws:

1. a soft offset shadow beneath every wedge and the centre disc
2. a translucent surface band
3. a faint upper-edge highlight and inner rim
4. the configured border
5. an accent wash on hover or focus
6. a slow, low-alpha sheen travelling around the ring

The footer uses the same translucent surface, border, selected accent, and shadow. Disabled options reduce text,
icon, border, and fill alpha together. The default palette comes from `GuiTheme`. Grimhollow will supply its own
stone, timber, and brass colours.

There is no framebuffer sampling, background blur, refraction, distortion pass, new blend mode, or shader. The
result must render through every existing backend because it is ordinary Render2D geometry.

## Opaque source-target use context

The second public primitive mirrors the state shape of `GuiDragContext` without drag geometry:

```csharp
public readonly record struct UsePayload(
    object? Token,
    object? SourceId = null,
    int SourceIndex = -1);

public readonly record struct UseTarget(
    object? TargetId,
    int TargetIndex = -1);

public readonly record struct UseResult(UsePayload Source, UseTarget Target);

public sealed class GuiUseContext
{
    public bool IsActive { get; }
    public UsePayload Payload { get; }
    public bool WasCompleted { get; }
    public UseResult LastUse { get; }
    public bool WasCancelled { get; }
    public UsePayload CancelledPayload { get; }

    public void BeginFrame();
    public bool Begin(Pointer pointer, in UsePayload payload);
    public bool Complete(
        Pointer pointer,
        object? targetId,
        int targetIndex = -1,
        bool accepted = true);
    public bool CompleteIn(
        Pointer pointer,
        Rect bounds,
        object? targetId,
        int targetIndex = -1,
        bool accepted = true);
    public void Cancel();
}
```

`Begin` replaces any active source and consumes the source gesture. `Complete` succeeds only while active and when
the caller's pre-validation says the target is accepted. Success consumes the target gesture, records both opaque
halves, clears the active source, and raises a one-frame flag. A refused target leaves the source active and does
not consume. `CompleteIn` adds the press-origin-safe rectangle hit test. A world raycast caller uses `Complete`
after its own hit test.

`Cancel` records the cancelled payload, clears the source, and is safe when idle. The context draws nothing. Source
highlighting, hover text, invalid-combination feedback, target meaning, and every resulting action remain caller
responsibilities.

## Localization and accessibility

Every displayed title, entry, detail, and footer choice is a `LocalizedText` sink. Icon keys and opaque tags are
explicitly non-localized. Text is measured using the supplied font and placed at the fixed entry and footer positions,
so callers choose copy that fits their configured metrics. No raw display string overload ships in the new API.

Disabled state is communicated through more than colour. Its label and detail remain readable, and the widget can
show the caller's reason text. Keyboard and gamepad navigation can inspect disabled entries even though selection
skips them. The sheen is decorative and no interaction depends on motion.

## Testing

Headless tests cover:

- one through eight entry geometry, zero through eight choices, and both upper-bound refusals
- twelve-o'clock entry zero and clockwise stable ordering
- clamping of the wheel and footer as one composition on every edge and corner
- polar hit testing at boundaries, gaps, the inner disc, and outside the ring
- disabled entry and choice refusal
- opening-gesture latching, outside dismissal, click-through blocking, and gesture consumption
- entry-local choice memory, `SetEntryChoice`, and the combined selection result
- keyboard and gamepad focus movement, wrapping, disabled-option skipping, select, and cancel
- every one-frame flag clearing on the next update
- `GuiUseContext` replacement, completion, refusal, cancellation, rectangle targeting, and opaque payload identity
- zero steady-state update allocation after warmup

A focused render test draws the default wheel with icons, disabled state, selected footer choice, hover accent, and
nonzero sheen time. It asserts visible alpha coverage and distinct enabled, disabled, and selected samples through
the existing Render2D snapshot path. No cross-backend golden family is needed because no backend or shader changes.

## Documentation and release

The public API and examples live in `KhaozEngine.Gui/README.md`, the Gui catalog row in the root `README.md`, and
the Gui usage section in `docs/USING-KHAOZENGINE.md`. This document retains the rationale and accepted boundaries.

The additive public API is implemented in engine version `18.36.0`. Grimhollow is pinned and waiting, which activates
the engine repository's sanctioned immediate tag rule after the implementation is merged, packed, and verified.

## Non-goals

This program does not add crafting, recipes, item catalogs, inventories, stations, skill checks, action queues,
network messages, persistence, per-character settings, backdrop blur, refraction, arbitrary wedge counts, radial
drag selection, or a new GUI framework. It does not alter `ContextMenu`, `Dropdown`, `GuiDragContext`, `SlotGrid`,
`Screen`, or `ScreenStack` behaviour.

The caller owns category paging beyond eight entries, persistence of choices or active source state, and every action
produced from the returned tags and opaque payloads. The Render2D presentation performs no blur, refraction,
distortion, or framebuffer sampling.
