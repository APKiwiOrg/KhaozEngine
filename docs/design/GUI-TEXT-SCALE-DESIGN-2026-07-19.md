# KhaozEngine Gui text-scale family (14.1.0)

Resolves https://github.com/APKiwiOrg/KhaozEngine/issues/232 (retained widgets cannot render a
scaled label) and https://github.com/APKiwiOrg/KhaozEngine/issues/237 (Tooltip cannot render its
lines below scale 1.0). Consumer evidence: https://github.com/APKiwiOrg/SpaceGame/issues/82 and
`SpaceGame.Core/Rendering/TooltipRenderer.cs`.

Ships as MINOR 14.1.0. Additive only. No breaking change to any existing consumer signature or
behaviour. Every default preserves today's rendering byte-for-byte at scale 1.0.

Spec baseline: engine `main` at 14.0.1 (read-only). All line numbers below are at that revision.

---

## 0. Verification corrections to the issues (read first, they shape scope)

Both issues are labelled `confidence/verified`, but two sub-claims do not hold at 14.0.1 and they
change what "the four widgets" actually means:

1. **Toggle and Slider render no text at all.** `Toggle.Draw` (`Toggle.cs:116-131`) draws a track
   plus a thumb. `Slider.Draw` (`Slider.cs:117-134`) draws a track, a fill, and a thumb. Neither has
   a `SpriteFont` field, neither calls `DrawString`, and there is no immediate-mode `GuiSurface.Toggle`
   drawing text either (`GuiSurface` has a `Slider` but no `Toggle`, and its `Slider` draws no label).
   So #232's "Toggle.cs and Slider.cs have the same gap" is true only in the narrow sense that they
   carry no `scale` reference. There is no label on them to scale. Adding a text-scale knob to Toggle
   or Slider would be dead API. They are **out of the text-scale family**, and that half of #232 should
   close as not-planned (see section 7).

2. **TabBar does NOT currently scale its text.** #232 says "TabBar is the one retained widget that
   scales its text." It does not. `TabBar.Draw` (`TabBar.cs:170-171`) positions each label with the
   4-arg `GuiDraw.AlignedTextPos(body, Font.Measure(str), Font.LineHeight, GuiAlign.Center)` (scale
   defaults to `1f`) and draws it with the no-scale `batch.DrawString(Font, str, pos, (Color)text)`
   overload. TabBar draws at native size exactly like Button. It is therefore the actual **second**
   text-drawing retained widget that needs the fix, not a precedent that already has it.

3. **Four more retained widgets draw text with the same no-scale gap, and #232/#237 name none of
   them.** Verified at 14.0.1: `Dropdown` draws its trigger label and each option label
   (`Dropdown.cs:218`, `:247`), `TextInput` draws the shown or placeholder text (`TextInput.cs:131`,
   with the caret x built from `Font.Measure(Text).X` at `:136`), `TreeView` draws each node label
   (`TreeView.cs:455`), and `ProgressBar` draws its optional `OverlayText` via the 4-arg
   `AlignedTextPos` plus the no-scale `DrawString` (`ProgressBar.cs:209-210`). All four sit outside
   the literal scope of #232 (Button/Toggle/Slider) and #237 (Tooltip) and stay OUT of this release.
   That is a deliberate scope call, not a claim of completeness. Leaving the gap unrecorded is exactly
   how #232/#237 got derived, filed, and blocked a consumer in the first place, so the release ritual
   files ONE follow-up issue covering all four (ready-to-file text in section 6).

**Net:** the text-drawing retained widgets NAMED BY the two issues are **Button and TabBar** (TabBar
via correction 2), and **Tooltip** is #237's surface. That trio is this release's family. Toggle and
Slider are excluded on the merits, and Dropdown, TextInput, TreeView, and ProgressBar carry the
identical gap but are deferred to the follow-up issue, on record, not silently.

---

## 1. Current state per widget (where text is drawn, what blocks scaling)

### Button (`KhaozEngine.Gui/Button.cs`)
- Text is drawn inside the shared helper `GuiDraw.DrawButton`, not in `Button` itself.
- `Button.Draw` (`Button.cs:75-76`):
  ```csharp
  public void Draw(SpriteBatch batch, Texture2D white) =>
      GuiDraw.DrawButton(batch, white, Font, Bounds, Content, Style, Enabled, Selected, _hover, _press);
  ```
  It calls the 10-arg form and drops the trailing `float scale = 1f` that `GuiDraw.DrawButton`
  already accepts (`GuiDraw.cs:447-448`).
- `GuiDraw.DrawButton` (`GuiDraw.cs:465-467`) already threads scale correctly:
  ```csharp
  var pos = AlignedTextPos(rect, font.Measure(s), font.LineHeight, GuiAlign.Center, scale, pad: 0f);
  batch.DrawString(font, s, pos, (Color)text, scale);
  ```
- **Blocker:** `Button` exposes no scale field, so `Draw` has nothing to forward. One missing field and
  one dropped argument. Nothing else.

### TabBar (`KhaozEngine.Gui/TabBar.cs`)
- Draws its own labels inline (`TabBar.cs:164-172`), centred per tab, via the same helper Button uses
  for positioning but with the no-scale `DrawString`:
  ```csharp
  Vector2 pos = GuiDraw.AlignedTextPos(body, Font.Measure(str), Font.LineHeight, GuiAlign.Center);
  batch.DrawString(Font, str, pos, (Color)text);
  ```
- **Blocker:** no scale field, the 4-arg `AlignedTextPos` (scale 1), and the no-scale `DrawString`.

### Tooltip (`KhaozEngine.Gui/Tooltip.cs`)
- `TooltipLine` (`Tooltip.cs:12`) is `readonly record struct TooltipLine(string Text, Vector4 Color)`.
  No per-line scale. `TooltipLine.Of(LocalizedText, Vector4)` (`:15`) is the localized factory.
- The instance holds two separate fonts, `_titleFont` and `_bodyFont` (`:50`). There is no single-font
  path, so a caller that owns one font and expresses hierarchy through scale has no way in.
- `ComputeBounds` (the pure layout, fullest overload `:202-243`) sizes with a UNIFORM per-line height:
  `contentH += visual.Count * (bodyFont.LineHeight + m.LineSpacing)` (`:220`). No scale term anywhere.
  Width is `MathF.Max` over `bodyFont.Measure(visual[i].Text).X` (`:216`), also unscaled.
- `Draw` (`:298-319`) draws the title (`:300`), the optional right title value (`:304`), and every body
  line (`:317`) all through the **no-scale** `DrawString` overload.
- **Blocker:** no per-line scale on `TooltipLine`, no title scale, and the layout + draw both assume a
  single uniform font size.

### Toggle / Slider
- No text. Excluded (section 0).

### Infrastructure that already exists (the fix is small because of this)
- `GuiDraw.AlignedTextPos(rect, measured, lineHeight, align, scale = 1f, pad = 0f)` (`GuiDraw.cs:478-489`):
  scale-aware positioning, proven byte-identical at scale 1 by `GuiTextScaleTests`.
- `SpriteBatch.DrawString(font, text, pos, color)` (`SpriteBatch.cs:582-583`) is literally
  `=> DrawString(font, text, pos, color, 1f);`. So `DrawString(..., 1f)` is the same code path the
  no-scale overload runs, **byte-for-byte, by construction**. This is the load-bearing fact behind
  every "byte-identical at 1.0" claim in this spec.
- Established per-widget-scale-field convention already in the engine, same `Measure * scale` +
  `DrawString(..., scale)` idiom this spec proposes:
  - retained `Label.Scale` (`Label.cs:29`),
  - `SlotGrid.KeybindLabelScale` (`SlotGrid.cs:95,260`),
  - `DiagnosticsOverlayTheme.TitleScale` used as `font.Measure(s.Title).X * Theme.TitleScale` and
    `font.LineHeight * Theme.TitleScale` and `DrawString(..., Theme.TitleScale)` (`DiagnosticsOverlay.cs:209,218,250`),
  - `OverlayLegendTheme.TextScale`,
  - immediate `GuiSurface.Label`/`Button`/`StatChip(..., float scale = 1f)` (`GuiSurface.cs:175,225,288`).
- Test idiom: `GuiTextScaleTests.cs` (pure positioning math, fake `ITextMeasurer`, scale-1-byte-identical
  plus scale-2-correct) and the on-device `KhaozEngine.Render.Tests/Gpu/DrawStringScaleGpuTests.cs`
  (the scaled glyph raster itself).
- Test access the draw-forward nets rely on: `KhaozEngine.Gui` grants `InternalsVisibleTo` to BOTH
  `KhaozEngine.Gui.Tests` and `KhaozEngine.Render.Tests` (`KhaozEngine.Gui.csproj:18-19`), and
  `Render.Tests` already references the Gui project (`KhaozEngine.Render.Tests.csproj:34`), so a GPU
  test there can call the internal 10-arg `GuiDraw.DrawButton` directly. `Render2DSnapshot.Capture`
  plus `[GpuFact]` (Metal-gated on `KE_GPU_TESTS=1`) is the SELF-RELATIVE capture harness
  `DrawStringScaleGpuTests` uses: it compares regions within its own captures and ships no golden
  image, so it never touches the cross-platform golden bake ritual. `PatchNotesViewCloseTooltipGpuTests`
  is the precedent for Gui-widget GPU tests living in `Render.Tests/Gpu/`.

---

## 2. Chosen API shape

**A per-widget uniform scale FIELD on the single-label widgets (`Button.LabelScale`,
`TabBar.TextScale`), and a per-LINE `Scale` on `TooltipLine` plus a `Tooltip.TitleScale` for the
multi-line widget.** One idiom: an optional scale defaulting to `1f`, scaling the text only, leaving
the widget rect and the hit-test untouched, applied at whatever unit each widget's text model exposes
(the whole label for Button/TabBar, the individual line for Tooltip).

### Alternatives weighed

**(a) Per-widget scale field. CHOSEN.** A new `float` field defaulting to `1f` on each text widget,
forwarded into the existing scale-aware helpers. It is the engine's own established convention:
`Label.Scale`, `SlotGrid.KeybindLabelScale`, `DiagnosticsOverlayTheme.TitleScale`, and the immediate
`GuiSurface` sinks all do exactly this. Because the widgets are reference types, the field genuinely
defaults to `1f` (no struct-zero trap), so unscaled callers are byte-identical without any coercion.
It gives per-instance control, and it keeps text sizing orthogonal to the colour palette, which matches
how SpaceGame expresses its hierarchy (one font at 0.42 / 0.84 / 1.0, same colours).

**(b) A `GuiStyle.TextScale` member (inherited, override-able).** Rejected. `GuiStyle` is a `struct`, so
a new `float TextScale` field defaults to `0f`, not `1f`. Every static preset (`Default`/`Primary`/
`Secondary`/`Danger`/`Active`/`Legacy`/`Modern`), every `default(GuiStyle)`, and every
`new GuiStyle { ... }` a consumer writes would then carry scale 0 and render invisible text unless the
draw path coerces `0 -> 1`, which is an implicit footgun that also forbids a legitimate 0. It also
couples text size to the palette, so a "compact" look could no longer reuse an existing palette, and it
cannot express Tooltip's per-line hierarchy at all (one style is one scale, but title 1.0 over body 0.84
needs two). Two of the widgets that carry a `GuiStyle` (Toggle, Slider) draw no text, so the member would
be silently dead on them.

**(c) Scale on the text value itself.** Rejected as the primary mechanism, adopted narrowly for Tooltip.
Button's label is a `LocalizedText` from the `App` package, a foundation value type resolved everywhere,
and bolting a render scale onto it would pollute that type and thread scale through every localization
sink. But the per-LINE unit is exactly right for Tooltip, whose text model is already a list of lines and
whose required hierarchy is per-line. So Tooltip takes a local, typed version of (c): a `Scale` on
`TooltipLine`. This is not a contradiction with (a), it is the same "scale field defaulting to 1f" idiom
applied at the line, which is Tooltip's natural text unit.

### Localization safety
No change to the typed path. `Button.Content`, `TabBar.Labels`, and `TooltipLine.Of` stay
`LocalizedText`. Scale is a separate numeric field and never travels through a string, so it opens no
bare-string hole and the `[LocalizationStringSink]` / `[LocalizationExempt]` surface is untouched.

---

## 3. Exact public API deltas (per file)

### `KhaozEngine.Gui/Button.cs`
Add a field (place it beside `Selected`, `Button.cs:30`):
```csharp
/// <summary>
/// Uniform scale for the caption glyphs and advances, applied about the button centre so the label
/// stays centred. Defaults to <c>1f</c> (today's rendering, byte-for-byte). This scales the LABEL
/// ONLY: <see cref="Bounds"/> and the press-origin hit-test are unchanged at any scale, so a compact
/// button draws a smaller label inside the same rect. A scale large enough to overflow the rect is the
/// caller's responsibility, exactly as for the immediate <see cref="GuiSurface.Button(SpriteFont, Rect, LocalizedText, GuiStyle, bool, bool, float)"/>.
/// </summary>
public float LabelScale = 1f;
```
Change `Draw` (`Button.cs:75-76`) to forward it:
```csharp
public void Draw(SpriteBatch batch, Texture2D white) =>
    GuiDraw.DrawButton(batch, white, Font, Bounds, Content, Style, Enabled, Selected, _hover, _press, LabelScale);
```

### `KhaozEngine.Gui/TabBar.cs`
Add a field (place it beside `Opacity`, `TabBar.cs:44`):
```csharp
/// <summary>
/// Uniform scale for every tab label (each centred within its tab). Defaults to <c>1f</c> (today's
/// rendering, byte-for-byte). Scales the LABEL text only: the tab bodies, the shared border grid, the
/// active accent outline, and <see cref="TabRect"/> hit-testing are all unchanged, so a smaller scale
/// lets long labels fit a narrow bar without shrinking the tabs.
/// </summary>
public float TextScale = 1f;
```
Change the label draw (`TabBar.cs:170-171`):
```csharp
Vector2 pos = GuiDraw.AlignedTextPos(body, Font.Measure(str), Font.LineHeight, GuiAlign.Center, TextScale);
batch.DrawString(Font, str, pos, (Color)text, TextScale);
```

### `KhaozEngine.Gui/Tooltip.cs`

**`TooltipLine` gains a defaulted per-line scale** (`Tooltip.cs:12-16`):
```csharp
/// <summary>A single line of text in a <see cref="Tooltip"/>, optionally at a per-line
/// <paramref name="Scale"/> (default <c>1f</c>) so one font can render a size hierarchy.</summary>
public readonly record struct TooltipLine(string Text, Vector4 Color, float Scale = 1f)
{
    /// <summary>Build a line from localized text (resolved now against the ambient catalog),
    /// optionally at <paramref name="scale"/>.</summary>
    public static TooltipLine Of(LocalizedText text, Vector4 color, float scale = 1f) =>
        new(text.Resolve(), color, scale);
}
```
A defaulted 3rd positional parameter is additive: every existing call site is 2-arg construction
(`new TooltipLine(s, color)`) or `TooltipLine.Of(text, color)` and there is no 2-tuple deconstruction
of `TooltipLine` anywhere in the engine, so nothing breaks.

**`Tooltip` gains a title scale** (place beside `TitleColor`, `Tooltip.cs:91`):
```csharp
/// <summary>Uniform scale for the title row (the left title and the optional right-aligned value,
/// scaled together). Defaults to <c>1f</c> so a tooltip with no scaled lines renders unchanged. The
/// body lines carry their own per-line <see cref="TooltipLine.Scale"/>.</summary>
public float TitleScale = 1f;
```

**`ComputeBounds` fullest overload gains a trailing optional title scale** (`Tooltip.cs:202-204`):
```csharp
public static Rect ComputeBounds(ITextMeasurer titleFont, string title, string titleRight,
    ITextMeasurer titleRightFont, ITextMeasurer bodyFont,
    IReadOnlyList<TooltipLine> lines, Vector2 anchor, Vector2 viewport, TooltipMetrics m,
    float maxWidth, float titleScale = 1f)
```
The three shorter overloads (`:168`, `:178`, `:188`) are unchanged in signature and forward without
passing `titleScale`, so they keep an unscaled title (back-compat). Only the fullest overload changes,
and only by one optional trailing parameter, so this is additive. The BODY-line scale needs NO new
parameter on any overload: it rides inside each `TooltipLine.Scale` through the whole pipeline.

No new package, no new type file, no dependency edge. `GuiStyle` and the `App` seam are untouched.

---

## 4. Behaviour contract

### Scale 1.0 is byte-identical to today (exact for Button/TabBar, observably identical for Tooltip)
- Button/TabBar are byte-identical BY CONSTRUCTION. `GuiDraw.DrawButton`'s `scale` already defaults to
  `1f`, `AlignedTextPos(..., 1f)` reproduces the exact inline centring formula (locked by
  `GuiTextScaleTests.AlignedTextPos_scale_one_reproduces_the_unscaled_centred_layout`), and TabBar's
  `DrawString(..., 1f)` is the same method the no-scale overload delegates to (`SpriteBatch.cs:582-583`).
  No perturbing arithmetic exists at `1f` on these paths.
- Tooltip widths are exact (`X * 1f` is the identity in IEEE float). The height claim is scoped
  honestly: the new per-line sum `Σ (bodyFont.LineHeight * 1 + LineSpacing)` equals the old
  `count * (LineHeight + LineSpacing)` in real arithmetic, and an N-term float summation is not
  guaranteed bit-equal to a single multiply in general. It IS exact for integer-valued metrics, which
  covers every existing headless assertion (the `FixedFont` fake uses `LineHeight = 20`,
  `LineSpacing = 3`, `PadY = 8`), and any residue on a real font is sub-ULP and unasserted. Conclusion
  unchanged: no observable difference at scale 1, but the word for Tooltip is "observably identical",
  not "proven byte-exact". `WrapBody` at scale 1 uses `budget = maxContentWidth`, an identical fit
  test and wrap, and emits `new TooltipLine(wrapped, color, 1f)`, equal to the old 2-arg line. Draw
  uses `DrawString(..., 1f)`, the delegating path.

### Layout and bounds math at other scales
- Only measured text scales. `font.Measure(text).X * scale` for width and `font.LineHeight * scale` for
  height. **Chrome metrics never scale:** `TooltipMetrics.PadX/PadY/TitleGap/LineSpacing/AnchorOffsetY/
  Margin/TopMargin/TitleRightGap` stay in design pixels. They are frame geometry, not glyphs.
- Tooltip `ComputeBounds` exact changes:
  - Title width: `hasTitle ? titleFont.Measure(title).X * titleScale : 0`. When a right value exists,
    add `m.TitleRightGap + titleRightFont.Measure(titleRight).X * titleScale` (the gap itself unscaled).
  - Body width: `contentW = max(titleRowW, max_i bodyFont.Measure(visual[i].Text).X * visual[i].Scale)`.
  - Height: title term `hasTitle ? titleFont.LineHeight * titleScale + m.TitleGap : 0`, body term
    `Σ_i (bodyFont.LineHeight * visual[i].Scale + m.LineSpacing)` then subtract one `m.LineSpacing`
    when there is at least one body line (the existing "no trailing gap" rule).
  - The uniform-height product at `:220` becomes this per-line sum because lines can now differ in
    height. At scale 1 the two are equal.
- `WrapBody` budget: a line drawn at scale `s` occupies `Measure * s`, so to keep the bubble within the
  cap it wraps at font-space budget `maxContentWidth / s` (guard `s > 0`, fall back to `1f`). Wrapped
  pieces inherit `s`. This keeps `MaxWidth` / `MaxWidthFraction` correct under scale. The two-column
  title floor (`titleRowW + PadX*2` may exceed the cap) is unchanged in spirit, now over the scaled
  `titleRowW`.
- Tooltip `Draw`: pen origin stays `MathF.Floor(x)` / `MathF.Floor(y)` (crisp text). Scale multiplies
  glyph metrics INSIDE `DrawString` (which snaps the whole block origin in a point-space pass), so no new
  flooring is introduced. Advance `y` by `LineHeight * scale + spacing` per line, title by
  `_titleFont.LineHeight * TitleScale + TitleGap`. The separator y is computed from that scaled advance.
- Tooltip `Draw`, title-right specifics (easy to miss): the right-aligned title value scales WITH the
  title row even though it draws with the body font. Its measured width at `:303` becomes
  `rw = _bodyFont.Measure(_titleRight).X * TitleScale`, and `TitleScale` is passed to BOTH `DrawString`
  calls, the title at `:300` (`_titleFont`) and the title-right at `:304` (`_bodyFont`). Otherwise the
  right value would draw unscaled and misalign against the scaled row, and its right-edge anchor would
  be computed from the wrong width.
- **Both instance callers must pass `TitleScale`** so the drawn bounds and the dismiss bounds agree
  (the existing "one source so bounds and draw agree" invariant): `Draw` (`:292`) and `Update` (`:151`)
  each append `TitleScale` to their `ComputeBounds` call.
- Button/TabBar: the rect is caller-owned and does NOT change with scale. A label scaled up can overflow
  its rect. That is the documented, intended contract (it mirrors the immediate-mode sinks) and is the
  point of the feature for SpaceGame, whose 40-unit buttons want a 0.56-scaled label INSIDE the fixed
  rect.

### Hit-test implications (the input hard rule holds)
- `LabelScale` and `TextScale` are label-only. `Button.Update` (`Button.cs:64-71`) hit-tests `Bounds`,
  `TabBar.Update` (`TabBar.cs:103-122`) hit-tests `TabRect`, and both reserve their rect via
  `Pointer.BlockRegion`. None of that reads the scale, so the press-origin `IsTapIn` invariant and the
  click-through gate are unchanged at any scale. The bounds helpers stay the contract, untouched. Only
  `AppWindow` touches Silk.NET input, and nothing in this change goes near input plumbing.
- Tooltip has no text hit-test. Its only pointer interaction is `TooltipDismiss.TapOutside`, which tests
  the bubble bounds from `ComputeBounds`. Those bounds now reflect scale, which is correct: the user
  taps the bubble they see, and `Draw` and `Update` size it identically because both pass the same
  `TitleScale` and the same per-line scales.

---

## 5. Ordered task list

Each task is independently buildable at zero warnings (`TreatWarningsAsErrors`), independently
committable, and lands its headless tests in the same commit (test-first where the seam is pure). Files
are disjoint across tasks 1-3, so they can be done in any order or in parallel. All land under the one
14.1.0 bump (task 4), per the one-bump-per-batch rule.

**Task 1: Button label scale.**
- Headless first, in `KhaozEngine.Gui.Tests/Gui/` (extend `GuiTextScaleTests.cs` or add
  `ButtonScaleTests.cs`): assert `new Button(...).LabelScale == 1f`, the default that keeps every
  existing caller byte-identical. The positioning math itself is already locked by `GuiTextScaleTests`
  (scale 1 reproduces the unscaled layout, non-1 scales width and centring). There is still no headless
  recording `SpriteBatch` (a concrete GPU type, not a seam), and inventing one for a one-line forward
  stays out of scope.
- The FORWARD gets its own regression net, because dropping the forward is exactly the pre-existing bug
  #232 reports and no pure test can see it. Add `ButtonLabelScaleGpuTests` to
  `KhaozEngine.Render.Tests/Gpu/`, self-relative captures only, NO pixel golden (a new golden would
  trigger the cross-platform bake ritual, out of proportion here):
  - Capture A: `GuiDraw.DrawButton(batch, white, font, rect, label, style, enabled: true,
    selected: false, hover: false, press: false)`, the internal 10-arg form with `scale` defaulted.
    Callable from `Render.Tests` via `InternalsVisibleTo` (`KhaozEngine.Gui.csproj:18`). This IS
    today's exact call.
  - Capture B: a retained `new Button(rect, label, font)` with defaults (never `Update`d, so
    `_hover`/`_press` are false, `Style` is `GuiStyle.Default`, `LabelScale` is `1f`), drawn via
    `Draw`. Assert A and B are byte-identical RGBA buffers. That pins "`LabelScale = 1f` forwards
    today's exact call".
  - Capture C: the same button with `LabelScale = 0.5f`. Assert C differs from B, and the label's
    lit-pixel extent inside the button interior is roughly half of B's (the `LitExtent` idiom of
    `DrawStringScaleGpuTests`, generous tolerance). That pins "a non-1f forwards the scale".
  - `[GpuFact]` is Metal-gated on `KE_GPU_TESTS=1` and runs on the self-hosted mac leg, same as every
    existing GPU test. No golden file, no bake.
- Impl: add `LabelScale`, forward it in `Draw`.

**Task 2: TabBar text scale.**
- Headless first, in `TabBarTests.cs`: assert `TextScale` defaults to `1f`, and assert `TabRect` is
  independent of `TextScale` (the hit-geometry contract, trivially true today and pinned so it stays
  true).
- The forward gets the same GPU net, `TabBarTextScaleGpuTests` in `KhaozEngine.Render.Tests/Gpu/`.
  TabBar draws inline with no single internal whole-draw helper to byte-compare against (unlike
  Button), so the assertion is relative between two regions of one capture: an identical bar drawn at
  `TextScale = 1f` and at `0.5f` in disjoint regions. Assert the label lit extent in the 0.5f bar is
  roughly half the 1f bar's, and assert the frame/border chrome pixels (rows and columns outside the
  text band) are byte-identical between the two regions, pinning "scale touches the label only". Same
  harness, no golden.
- Impl: add `TextScale`, forward it into `AlignedTextPos` and `DrawString`.

**Task 3: Tooltip per-line and title scale (the substantive one, fully headless-testable).**
- Test first, in `TooltipTests.cs`, using the existing `FixedFont` fake `ITextMeasurer` (10px/char,
  20px line). The `ComputeBounds` seam is pure, so this is real coverage, not a proxy:
  - Byte-identical at 1.0: an all-default-scale line list produces the SAME `Rect` as today (reuse the
    existing width/height expectations at `TooltipTests.cs:49-52`, now via `new TooltipLine(s, color)`
    which still compiles).
  - A body line at scale 0.5 contributes half its width and half its line height. Example with the fake
    font: `new TooltipLine("longline", color, 0.5f)` measures `80 * 0.5 = 40` wide and `20 * 0.5 = 10`
    tall, so assert the bubble narrows and shortens by the expected delta versus scale 1.
  - `TitleScale` scales the title row width and height (assert against a hand-computed `Rect`).
  - Wrap-at-scale: with `maxWidth` set, a line at scale `s` wraps at budget `maxWidth_content / s`.
    Assert a line that fits at `s = 1` but not at `s = 2` wraps into more visual lines and the bubble
    stays within the cap.
  - `TooltipLine.Of(text, color, scale)` carries the scale through.
- Impl: `TooltipLine.Scale`, `Tooltip.TitleScale`, thread `titleScale` and `visual[i].Scale` through
  `ComputeBounds`, `WrapBody`, and `Draw`, and pass `TitleScale` from both `Draw` and `Update`.
- Optional on-device: a single self-relative `GpuFact` in `KhaozEngine.Render.Tests/Gpu/` rendering
  the same tooltip content at line scale 1.0 and 0.5 and asserting the 0.5 capture's bubble and text
  extents shrink accordingly (the `LitExtent` idiom). NO pixel golden, same constraint as tasks 1-2.
  Not required for correctness: `ComputeBounds` is pure-covered above and `DrawStringScaleGpuTests`
  already proves the scaled glyph raster.

**Task 4: release ritual (one 14.1.0 bump for tasks 1-3).** See section 6.

---

## 6. Release-ritual checklist (instantiated)

Re-read the version and tags on up-to-date `main` right before bumping (concurrent dev is heavy here).
At baseline `<KhaozEngineVersion>` is `14.0.1` and the newest tag is `v14.0.1`, so the version is
already tagged and nothing is in flight: cut a fresh **14.1.0** without asking (the batch gate does NOT
fire, because the current version is not ahead of the newest tag). If 14.1.0 is taken by the time you
bump, take the next free version and rebase the CHANGELOG entry onto it, automatically.

- **`Directory.Build.props`**: `<KhaozEngineVersion>14.0.1</KhaozEngineVersion>` -> `14.1.0` (`:25`).
- **`CHANGELOG.md`**: new `## 14.1.0` block, newest-first, first sentence is the digest. Summary line:
  > Retained `Button` and `TabBar` and the `Tooltip` can now draw their text at a caller-set scale
  > (issues #232, #237), via `Button.LabelScale`, `TabBar.TextScale`, per-line `TooltipLine.Scale`, and
  > `Tooltip.TitleScale`, each defaulting to `1f` so every existing tooltip and button renders
  > byte-for-byte identically. `Toggle` and `Slider` are untouched because they render no text.

  Body: note the additive `TooltipLine` positional param and the one added optional `ComputeBounds`
  parameter, and that scale is label-only (rect and hit-test unchanged).
- **`README.md` package table, Gui row (`:19`)**: add a brief scale mention to the row. The row
  currently describes the `LocalizedText` sinks and has NO scale note to sit next to (the existing
  immediate-mode scale prose lives in `KhaozEngine.Gui/README.md` and the USING doc, not here).
  Mention `Button.LabelScale`, `TabBar.TextScale`, `TooltipLine.Scale`, `Tooltip.TitleScale`.
- **`README.md` PackageReference example lines (`:157-160`)**: bump all four umbrellas from `14.0.1` to
  `14.1.0`. `scripts/check-doc-versions.sh` enforces every one of these matches `<KhaozEngineVersion>`.
- **`KhaozEngine.Gui/README.md`**: extend the scale note (it already documents the immediate-mode
  `float scale = 1f` idiom) with the retained equivalents and the Tooltip per-line scale. This is the
  `PackageReadmeFile` shipped inside the nupkg, so it must stand alone.
- **`docs/USING-KHAOZENGINE.md`**: extend the "Scaling Gui text" block (`:752-761`) to add
  `Button.LabelScale`, `TabBar.TextScale`, and Tooltip `TooltipLine.Scale` / `Tooltip.TitleScale`, with
  a one-line example (a compact button and a two-size tooltip). Add one line to the Tooltip section
  (`:963-967`) that lines carry a per-line scale.
- **`docs/DEPENDENCY-SEAMS.md`**: no change (no dependency edge or seam member changed). State this in
  the PR so the reviewer knows it was checked, not forgotten.
- **Package inventory** (the other half of `check-doc-versions.sh`): no new packable package, so no new
  catalog row and no new `<Package>/README.md`. Unaffected.
- **Close issues from the landing commit**: `Closes #232` (Button + TabBar half) and `Closes #237`. For
  the Toggle/Slider half of #232 see section 7 (close as not-planned, do not let it hang on `#232`).
- **File the scale-family completion issue, in the same sitting as the release.** ONE issue covering
  the four remaining text-drawing widgets (section 0 item 3), so the deferral is on record instead of
  being re-derived by the next consumer that hits it. Ready to file:

  Title: `Dropdown, TextInput, TreeView and ProgressBar still cannot render scaled text`

  Labels: `kind/backlog`, `confidence/verified`, `parity`, `priority/low`. The `gh` CLI bypasses the
  issue form, and `.github/workflows/issue-confidence.yml` enforces the confidence label server-side,
  so pass every label explicitly.

  Body, file verbatim:

  > 14.1.0 gave Button, TabBar and Tooltip a text scale (`Button.LabelScale`, `TabBar.TextScale`,
  > per-line `TooltipLine.Scale` plus `Tooltip.TitleScale`, each defaulting to `1f`, label-only, rect
  > and hit-test untouched). Four retained widgets still draw text through the no-scale
  > `SpriteBatch.DrawString` overload and expose no scale knob. Verified at 14.0.1, and none of these
  > files changed in 14.1.0:
  >
  > - `Dropdown`: trigger label at `Dropdown.cs:218`, option labels at `Dropdown.cs:247`, each with a
  >   `(Height - font.LineHeight) * 0.5f` centring term beside it
  > - `TextInput`: shown/placeholder text at `TextInput.cs:131`. The caret x is
  >   `textX + Font.Measure(Text).X + 1f` at `TextInput.cs:136`, so a scale must multiply that term too
  > - `TreeView`: node labels at `TreeView.cs:455`, row-centred via `(RowHeight - font.LineHeight) * 0.5f`
  > - `ProgressBar`: `OverlayText` at `ProgressBar.cs:209-210`, the same 4-arg `AlignedTextPos` plus
  >   no-scale `DrawString` shape `TabBar` had before 14.1.0
  >
  > Same fit-failure class as #232 and #237. A consumer wanting a compact dropdown or a small overlay
  > readout hits the identical wall and has to re-derive this finding. Filed so the gap is on record.
  >
  > Ask: mirror the 14.1.0 idiom. A `float` field defaulting to `1f` per widget (`Dropdown.TextScale`
  > covering trigger and option rows, `TextInput.TextScale`, `TreeView.TextScale`,
  > `ProgressBar.OverlayTextScale`), forwarded into `AlignedTextPos` where used, into the scaled
  > `DrawString` everywhere, and into every `font.LineHeight` centring term. Scale 1 stays
  > byte-identical by construction (the no-scale `DrawString` delegates to the scaled overload with
  > `1f`, `SpriteBatch.cs:582-583`). Tests per the 14.1.0 pattern: headless field-default and
  > hit-geometry tests, plus the self-relative capture GpuFact net for the forwards, no pixel goldens.
  > `TreeView` and `ProgressBar` are mechanical forwards mirroring `TabBar`'s. `Dropdown` is two
  > sites. `TextInput` must carry the caret x with the scale.

  Then add it to the org board, which has NO auto-add, and mirror the fields:
  ```bash
  gh project item-add 1 --owner APKiwiOrg --url <the new issue URL>
  ```
  Board Priority = Low (mirroring `priority/low`), Status = Todo. Do the same board add + field mirror
  for the two Tooltip parity issues filed from section 7 (items 4 and 5) in this same sitting.
- **Docs sweep grep** (across ALL `*.md` recursively plus `AGENTS.md`): `LabelScale`, `TextScale`,
  `TitleScale`, `TooltipLine`, and re-grep `scale`/`Scale` in the Gui docs to confirm the retained
  widgets are now covered and no doc still implies the retained widgets cannot scale.
- **Pack + tag + push**: `dotnet pack -c Release -o ./local-feed`, commit, then `scripts/tag-release.sh`
  (it reads `<KhaozEngineVersion>` and writes the canonical `gui(14.1.0): ...` annotated tag). Never
  hand-type `git tag`. Push `main` and the tag right away (CI publishes to GitHub Packages on `v*`).

---

## 7. Risks and open questions

**Discovered follow-ups (raise as GitHub issues at the checkpoint, do not action mid-task).**
1. **#232 Toggle/Slider half has no text to scale.** Close that portion as not-planned,
   `confidence/refuted`, with the reason: "retained `Toggle` and `Slider` render no text (track + thumb
   only, no `SpriteFont`, no `DrawString`), so there is no label to scale. The text-scale family is
   Button, TabBar, Tooltip." Either narrow #232 to Button + TabBar before closing it via the commit, or
   file a short refuted note so the decline is greppable and not re-raised. If it is not written down, a
   later agent re-reads "Toggle and Slider have the same gap" and re-opens it.
2. **#232 mis-states TabBar as already scaling.** The commit that adds `TabBar.TextScale` corrects this
   in passing. Worth a one-line note on the issue so the record is not left wrong.
3. **The scale-family completion issue (Dropdown/TextInput/TreeView/ProgressBar).** Filed at release,
   ONE issue, ready-to-file text in section 6. Recorded here so the checkpoint list is complete.

**TooltipRenderer parity: THREE discrepancies, not two (file the first two, accept the third).**
4. **Tooltip has no alpha / opacity fade.** Ready to file at the release checkpoint:

   Title: `Tooltip has no Opacity fade, blocking the SpaceGame TooltipRenderer retirement`

   Labels: `kind/backlog`, `confidence/verified`, `parity`, `priority/low`.

   Body:
   > `Dropdown`, `TabBar`, `Toggle`, `Slider` and `PopupPanel` all carry a `float Opacity = 1f`
   > multiplied into every colour's alpha at draw. `Tooltip` does not: `Draw` paints `Background`,
   > `Border`, `TitleColor`, `TitleRightColor`, `SeparatorColor` and each line colour raw. SpaceGame's
   > `TooltipRenderer.Draw` takes a `float alpha` and fades every colour with it, used by both its
   > tooltips. Additive ask: a `Tooltip.Opacity` field defaulting to `1f`, applied to every colour via
   > `GuiDraw.WithOpacity`, mirroring `Dropdown.Opacity`. Default is a no-op so existing tooltips are
   > byte-identical. Needed together with the anchor-mode issue before
   > https://github.com/APKiwiOrg/SpaceGame/issues/82 can retire `TooltipRenderer`, and even with both
   > landed that retirement is a near-parity re-baseline, not byte-exact (the bounds-rounding models
   > differ by up to about 1px, see the 14.1.0 spec's audit).
5. **Tooltip anchors by centring, SpaceGame offsets right of the pointer.** Ready to file at the
   release checkpoint:

   Title: `Tooltip anchor mode: offset-from-pointer placement alongside the centred default`

   Labels: `kind/backlog`, `confidence/verified`, `parity`, `priority/low`.

   Body:
   > `Tooltip.ComputeBounds` places `x = anchor.X - w * 0.5f` (`Tooltip.cs:236`) and clamps both
   > sides. SpaceGame's `TooltipRenderer.ComputeTooltipBounds` places `x = pointer.X + offset`
   > (cursor-style, to the RIGHT of the pointer), prefers above the pointer by `yGap`, falls back to
   > `pointer.Y + offset` below, and clamps only the right and top insets
   > (`TooltipRenderer.cs:71-81`). `GameOverScreen` depends on that offset-right placement plus the
   > preserved fractional pointer. The engine cannot express offset-right today. Additive ask: an
   > anchor-mode knob, default `Centered` (today's behaviour, byte-identical), plus an offset-right
   > mode with a per-instance offset. Needed together with the Opacity issue before
   > https://github.com/APKiwiOrg/SpaceGame/issues/82 can retire `TooltipRenderer`, and even with both
   > landed that retirement is a near-parity re-baseline, not byte-exact (the bounds-rounding models
   > differ by up to about 1px, see the 14.1.0 spec's audit).
6. **Residual rounding + height-model divergence: accepted at adoption, NOT an engine issue.**
   `TooltipRenderer.ComputeTooltipBounds` ceils the content extents before padding
   (`MathF.Ceiling(maxLineWidth)` / `MathF.Ceiling(totalTextHeight)`, `TooltipRenderer.cs:68-69`)
   where engine `ComputeBounds` does not ceil (`w = contentW + PadX * 2`). It also sums per-line
   `font.Measure(text).Y * Scale` (`TooltipRenderer.cs:62-67`) where the engine sums
   `bodyFont.LineHeight * scale` per line. The height half is structural only: for the engine
   `SpriteFont`, `Measure(text).Y` IS `LineHeight` (`SpriteFont.cs:73`), so with the real font the two
   models coincide and the observable residue is the ceiling, up to about 1px per dimension. Making
   the engine ceil for parity would shift every existing engine tooltip, so this stays a
   consumer-side re-baseline: record it on https://github.com/APKiwiOrg/SpaceGame/issues/82 at
   adoption time rather than filing an engine issue.

**Governance call: this ships as `kind/backlog`, consciously.** AGENTS.md says "if it needs a spec, it
is a roadmap item", and this document is substantial, so the call is made explicitly rather than by
default: the change is additive, four small fields plus threading, no new architecture, no new package,
and the alternatives-weighing above is implementation diligence, not a program design. Decision: keep
#232/#237 `kind/backlog`, land NO `docs/design/*-DESIGN.md` and no `docs/INDEX.md` row. This file is a
coordination artifact, not a landed design doc. If the maintainer disagrees, sections 2-4 are ready to
land as `docs/design/GUI-TEXT-SCALE-DESIGN-2026-07-19.md` with an INDEX row, and the issues get
relabelled `kind/roadmap` first.

**Risk: WrapBody scale interaction.** The `budget = cap / scale` transform is the one non-obvious piece.
It is fully covered by the headless wrap-at-scale test in task 3, and it is inert at scale 1 (budget ==
cap). SpaceGame's own tooltips do not wrap (they pre-wrap upstream), so this is engine-completeness, not
a consumer requirement, but it must be correct because the engine `Tooltip` advertises `MaxWidth` wrap.

**Risk: title vs body font under scale.** The engine `Tooltip` keeps two fonts. A consumer with one font
passes the same `SpriteFont` as both `titleFont` and `bodyFont`, or uses `title = ""` and puts
everything in scaled lines. Both work. The two-font model is retained (no removal, additive only).

---

## 8. SpaceGame TooltipRenderer coverage (honest audit)

`TooltipRenderer` members vs this design:

| TooltipRenderer element | This design | Covered |
| --- | --- | --- |
| `lines: (Text, Scale, Color)` per line | `TooltipLine(Text, Color, Scale)` | Yes |
| Per-line scale 0.42 / 0.84 / 1.0 | `TooltipLine.Scale` (and `title=""` so it is all lines) | Yes |
| Single shared font | pass one `SpriteFont` as both ctor fonts, or `title=""` | Yes |
| `TooltipMetrics(PadX, PadY, LineGap, Offset, YGap)` | engine `TooltipMetrics` PadX/PadY/LineSpacing/AnchorOffsetY | Mostly (naming differs, `Offset` maps to the anchor model, see below) |
| Fractional pointer preserved (no internal floor of the anchor) | engine `ComputeBounds` clamps but never truncates the anchor | Yes |
| `ViewportInset = 4` | engine `TooltipMetrics.Margin = 4` | Yes |
| `alpha` fade on every colour | no `Tooltip.Opacity` today | **No** (follow-up 4) |
| x at `pointer.X + Offset` (offset-right), clamp to right edge | engine centres x on the anchor | **No** (follow-up 5) |
| `MathF.Ceiling` of content width/height before padding (`:68-69`) | engine does not ceil (`w = contentW + PadX * 2`) | **No** (follow-up 6, accepted at adoption, up to about 1px per dimension) |
| Per-line height `font.Measure(text).Y * Scale` (`:62-67`) | engine sums `bodyFont.LineHeight * scale` per line | Structurally different, numerically equal for the engine `SpriteFont` (`Measure(text).Y` IS `LineHeight`, `SpriteFont.cs:73`) |

**Verdict:** this design closes the scale blocker that #237 and #82 are titled on, so a future
SpaceGame repin can delete `TooltipRenderer`'s measure/scale logic. Full retirement needs the two
engine follow-ups (the `Opacity` fade and the anchor mode, items 4 and 5), and even with both landed
the retirement is a NEAR-parity re-baseline, never byte-exact: the bounds-rounding models differ (the
ceiling divergence, item 6), so adopting the engine widget shifts the bubble by up to about 1px per
dimension, which is accepted and recorded on the SpaceGame side at adoption.
