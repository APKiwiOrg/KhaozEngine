# Immediate-mode Gui surface (`GuiSurface`) design

**Goal:** an immediate-mode UI surface in `KhaozEngine.Gui` so a game running a single `window.Run(frame => ...)`
loop can author both an in-game HUD over 3D and full-screen menus with one call site per widget
(`if (ui.Button(font, rect, "Play")) {...}`), instead of hand-rolling SpriteBatch fills + per-widget
`Pointer.BlockRegion`/`IsBlocked` bookkeeping.

**Context / why:** the Gui package already has retained-mode widgets (`Button`, `Panel`, ...) bound to the
`Screen`/`ScreenStack` model. Those are awkward inside a live render lambda: you would have to keep widget
instances alive across frames and wire `OnClick` callbacks. The Hardpoint testbed (5.x stack) hand-rolled its
HUD with bare `SpriteBatch` + a second design-space `Pointer`, manually reserving every widget rect with
`BlockRegion` for the board click-through gate. That pattern is generic and belongs centralized. This is the
flagged "immediate-mode Gui surface" engine-first candidate. Track B: build it in the engine, validate live
in Hardpoint.

## Scope (YAGNI)

Exactly enough to retrofit the Hardpoint HUD and build title/pause/win-lose screens:

- `GuiStyle` — a struct of default colors so a button looks right with no styling args.
- `GuiSurface` — the immediate-mode surface:
  - `Panel(rect, fill, border?, borderThickness?)` — filled rect + optional outline.
  - `Label(font, text, pos, color)` and `Label(font, rect, text, color, align)` — positioned / box-aligned text.
  - `Swatch(rect, color)` — a plain filled rect (colour chip primitive; the flagged swatch).
  - `Button(font, rect, label, style?, enabled = true, selected = false)` -> `bool` clicked this frame.
  - `PointerCaptured` — true if the pointer's **press-origin** lies in any widget drawn this frame
    (the click-through gate, replacing manual BlockRegion).

No layout engine, no docking, no IDs/focus stack, no text input (the retained `TextInput` covers that).

## API

```csharp
namespace KhaozEngine.Gui;

public struct GuiStyle
{
    public Vector4 Fill, Hover, Press, Border, Text, DisabledFill, DisabledText, SelectedFill, SelectedBorder;
    public float BorderThickness;          // default 1.5f
    public static GuiStyle Default { get; } // sensible blue-grey palette matching the existing Button defaults
}

public sealed class GuiSurface
{
    public GuiSurface(Texture2D white, GuiStyle? style = null);

    public GuiStyle Style { get; set; }

    // Begin a UI frame: capture the already-begun batch + the design-space pointer. Resets PointerCaptured.
    public void Begin(SpriteBatch batch, Pointer pointer);

    public void Panel(Rect rect, Vector4 fill);
    public void Panel(Rect rect, Vector4 fill, Vector4 border, float borderThickness = 1.5f);
    public void Swatch(Rect rect, Vector4 color);
    public void Label(SpriteFont font, string text, Vector2 pos, Vector4 color);
    public void Label(SpriteFont font, Rect rect, string text, Vector4 color, GuiAlign align = GuiAlign.Center);

    // Hover/press/disabled/selected visuals; returns true on a valid press-origin tap (IsTapIn invariant).
    public bool Button(SpriteFont font, Rect rect, string label);
    public bool Button(SpriteFont font, Rect rect, string label, GuiStyle style, bool enabled = true, bool selected = false);

    // True when the pointer press-origin is inside any widget reserved this frame. Use to gate world input.
    public bool PointerCaptured { get; }
}

public enum GuiAlign { Left, Center, Right }   // horizontal; text is always vertically centered in the rect
```

### Behaviour rules

1. **Capture / click-through.** Every `Button`, `Panel`, and `Swatch` call records its rect into a per-frame
   blocked set. `PointerCaptured` returns `pointer.IsBlocked(pressOrigin)` against that set. `Begin` clears the
   set. A disabled button still reserves its rect (it blocks, it just never returns true). This reproduces the
   Stage 2 invariant: a tap or drag that *began* on a widget never leaks to the world.
2. **Button interaction.** Clicked = `enabled && pointer.IsTapIn(rect)`. Fill = `!enabled` -> DisabledFill;
   `selected` -> SelectedFill; pressing-in -> Press; hovering-in -> Hover; else Fill. Border = `selected` ->
   SelectedBorder else Border. Text colour = `enabled` -> Text else DisabledText. Label is centered both axes.
3. **Drawing** reuses the internal `GuiDraw.Fill`/`Border`. The surface holds the 1x1 white texture; the caller
   owns `batch.Begin(viewport)` / `End()` (so the surface composes with the design viewport for free).
4. **Headless-testable.** `Begin` accepts a `null` batch; every draw helper no-ops when batch is null, so
   interaction + `PointerCaptured` + `Button` return values are testable over a fed `RawInputState`/`Pointer`
   with no GPU. (Mirror the existing `ScrollablePanelTests` input pattern.)

## Files

- Create `KhaozEngine.Gui/GuiStyle.cs` — the style struct + `Default` + `GuiAlign` enum.
- Create `KhaozEngine.Gui/GuiSurface.cs` — the surface.
- Create `KhaozEngine.Tests/Gui/GuiSurfaceTests.cs` — headless interaction/capture/visual-state tests.
- Modify `GuiSample` — add an immediate-mode demo screen exercising Panel/Label/Swatch/Button + a disabled +
  selected button, proving it on screen.
- Release: bump `<KhaozEngine5xVersion>` 5.16.0 -> 5.17.0-experimental, CHANGELOG entry, pack all 5.x packages.

## Testing

Headless xUnit (no GPU): feed `InputState` frames through a `Pointer`, call `Begin(null, pointer)` + widget
calls, assert:
- A press+release inside a button rect returns true once (on release), false while held.
- A tap whose press-origin is outside the rect returns false (press-origin invariant).
- `enabled: false` never returns true but still sets `PointerCaptured`.
- `PointerCaptured` is true when press-origin is inside a drawn Panel/Button, false otherwise.
- Visual-state selection is covered indirectly via the return-value/Capture asserts (no pixel assertions).

GuiSample provides the on-screen visual check.
