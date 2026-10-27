using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A single option in a <see cref="Dropdown"/>: the player-facing <see cref="Content"/> plus the caller's own
    /// <see cref="Value"/>. <see cref="Content"/> is a <see cref="LocalizedText"/>, so a <see cref="StringId"/>
    /// converts implicitly and a bare literal does not, which is what puts the option list in front of the
    /// localization analyzer. A settings selector (difficulty, display mode, quality) is exactly the shape that
    /// used to ship unlocalizable, because a plain <c>string</c> member is not a sink the analyzer can see.
    /// </summary>
    public readonly record struct DropdownOption(LocalizedText Content, int Value);

    /// <summary>
    /// A selector over <see cref="Pointer"/>. The trigger shows the current option and a tap opens its option list.
    /// tapping an option selects + closes; a release outside dismisses. Because the open list extends past the
    /// trigger, drawing is split: <see cref="Draw"/> renders the trigger (inside any clip), <see cref="DrawOverlay"/>
    /// renders the open list (call last / unclipped so it sits on top). Pointer gestures that open, close, select,
    /// or dismiss are consumed so a control behind the dropdown cannot act on the same release.
    /// </summary>
    public sealed class Dropdown
    {
        readonly List<DropdownOption> _options;
        readonly DropdownPopup _popup = new();
        readonly InputManager _navigation = new();

        /// <summary>Trigger button bounds. The popup can fit independently when a viewport is supplied. Update before <see cref="Update(Pointer)"/> if it moves.</summary>
        public Rect TriggerBounds;
        /// <summary>Optional popup canvas in the same coordinates as the trigger. Null preserves the unbounded list.</summary>
        public Rect? PopupViewport { get; set; }
        /// <summary>The resolved list bounds, excluding the unchanged trigger, even while closed.</summary>
        public Rect PopupBounds { get { ResolvePopup(); return PopupViewport.HasValue ? _popup.Bounds : LegacyPopupBounds; } }
        /// <summary>Index of the first row intersecting the popup. Zero for an unbounded list.</summary>
        public int FirstVisibleIndex { get { ResolvePopup(); return PopupViewport.HasValue ? _popup.FirstVisibleIndex : 0; } }
        /// <summary>Number of rows with visible hit geometry, including partially visible edge rows.</summary>
        public int VisibleOptionCount { get { ResolvePopup(); return PopupViewport.HasValue ? _popup.VisibleOptionCount : _options.Count; } }
        /// <summary>True for this update when a bounded open popup owns nonzero wheel input, including at a scroll limit.
        /// Feed a scroll-free snapshot to parent content when true.</summary>
        public bool WheelConsumed { get; private set; }
        public bool IsOpen { get; private set; }
        /// <summary>True on the frame the selection changed.</summary>
        public bool WasChanged { get; private set; }
        public int SelectedIndex { get; private set; }

        /// <summary>
        /// The keyboard/gamepad cursor row within the open list, or -1 when no keyboard highlight is active
        /// (the pointer-only path never activates it, so its overlay draw is byte-identical). Seeded to
        /// <see cref="SelectedIndex"/> by <see cref="Open"/>; moved by <see cref="HighlightNext"/>/
        /// <see cref="HighlightPrevious"/>; committed by <see cref="CommitHighlight"/>.
        /// </summary>
        public int HighlightedIndex { get; private set; } = -1;

        /// <summary>When true (default) keyboard highlight movement and inline stepping wrap at the ends; when false they clamp.</summary>
        public bool Wrap = true;
        public int SelectedValue => _options[SelectedIndex].Value;
        /// <summary>The selected option's text, resolved against the ambient catalog (what the trigger draws).</summary>
        public string SelectedLabel => _options[SelectedIndex].Content.Resolve();
        /// <summary>The selected option's unresolved <see cref="LocalizedText"/>, for a caller forwarding it to
        /// another sink rather than drawing it.</summary>
        public LocalizedText SelectedContent => _options[SelectedIndex].Content;
        public IReadOnlyList<DropdownOption> Options => _options;

        public Vector4 Background = GuiTheme.Default.Surface;
        public Vector4 Border = GuiTheme.Default.Border;
        public Vector4 OpenBorder = GuiTheme.Default.AccentBright;
        public Vector4 ListBackground = GuiTheme.Default.Background;
        public Vector4 HoverColor = GuiTheme.Default.SurfaceHover;
        /// <summary>Row fill under the selected option, from <see cref="GuiTheme.SelectionFill"/> at construction.</summary>
        public Vector4 SelectedColor = GuiTheme.Default.SelectionFill;
        /// <summary>Row fill under the keyboard/gamepad highlight (<see cref="HighlightedIndex"/>), from
        /// <see cref="GuiTheme.FocusFill"/> at construction. Only drawn when a keyboard highlight is active.</summary>
        public Vector4 FocusColor = GuiTheme.Default.FocusFill;
        public Vector4 TextColor = GuiTheme.Default.TextMuted;
        public Vector4 SelectedTextColor = GuiTheme.Default.AccentBright;

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow) for the trigger and the open list container; defaults
        /// to the flat <see cref="GuiStyle.Default"/> so the dropdown renders byte-identically to pre-7.8.0. The
        /// dropdown keeps its own colours; only the affordance knobs are read, and the option-row highlights stay
        /// flat. Set <c>Style = GuiStyle.Modern</c> to opt in.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        /// <summary>
        /// Opt-in: draw a chevron caret on the right of the trigger that points down when closed and up when open.
        /// Defaults to <c>false</c> so existing callers render byte-identically. Colour is <see cref="ChevronColor"/>.
        /// </summary>
        public bool ShowChevron = false;
        public Vector4 ChevronColor = GuiTheme.Default.TextMuted;

        /// <summary>
        /// Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Lets a caller fade the whole
        /// dropdown in/out with a host transition (e.g. an <see cref="ScrollablePanel"/> sliding up). Default 1 is a no-op.
        /// </summary>
        public float Opacity = 1f;

        /// <summary>
        /// Uniform scale for the trigger label AND every option row's label. Defaults to <c>1f</c> (today's
        /// rendering, byte-for-byte). Scales the TEXT only: <see cref="TriggerBounds"/>, <see cref="OptionBounds"/>,
        /// the row fills and the chevron are unchanged at any scale, so a compact dropdown draws smaller labels in
        /// the same rows. Mirrors <see cref="TabBar.TextScale"/>.
        /// </summary>
        public float TextScale = 1f;

        public Dropdown(IReadOnlyList<DropdownOption> options, Rect triggerBounds)
        {
            if (options == null || options.Count == 0) throw new ArgumentException("At least one option is required.", nameof(options));
            _options = new List<DropdownOption>(options);
            TriggerBounds = triggerBounds;
        }

        /// <summary>Select the option with this value; no-op if not found. Does not set <see cref="WasChanged"/>.</summary>
        public void SelectByValue(int value)
        {
            for (int i = 0; i < _options.Count; i++)
                if (_options[i].Value == value) { SelectedIndex = i; return; }
        }

        /// <summary>Hit-test open/close/select/dismiss and consume any gesture that performs one of those actions.
        /// Returns true if the selection changed.</summary>
        public bool Update(Pointer pointer) => Update(pointer, 0f);

        /// <summary>Update pointer gestures and bounded popup scrolling. Positive wheel scrolls upward.
        /// Check <see cref="WheelConsumed"/> before routing the wheel to parent content.</summary>
        public bool Update(Pointer pointer, float wheelDelta)
        {
            WasChanged = false;
            WheelConsumed = false;
            ResolvePopup();
            // Reserve the trigger (closed) or the whole expanded list (open) for click-through, so a layer
            // beneath can't be clicked through the dropdown.
            pointer.BlockRegion(PopupViewport.HasValue || !IsOpen ? TriggerBounds : FullBounds());
            if (IsOpen && PopupViewport.HasValue) pointer.BlockRegion(PopupBounds);
            if (!IsOpen)
            {
                // Pointer-driven open: no keyboard cursor, so the overlay draws byte-identically to pre-key-nav.
                if (pointer.IsTapIn(TriggerBounds))
                {
                    pointer.ConsumeGesture();
                    IsOpen = true;
                    HighlightedIndex = -1;
                    ResetPopup();
                    if (PopupViewport.HasValue) pointer.BlockRegion(PopupBounds);
                }
                return false;
            }

            if (pointer.IsTapIn(TriggerBounds))
            {
                pointer.ConsumeGesture();
                IsOpen = false;
                return false;
            }

            if (PopupViewport is Rect viewport)
            {
                WheelConsumed = _popup.Update(pointer, wheelDelta, TriggerBounds, viewport);
                int index = _popup.ReleasedOption(pointer);
                if (index >= 0)
                {
                    if (SelectedIndex != index) { SelectedIndex = index; WasChanged = true; }
                    IsOpen = false;
                    return WasChanged;
                }
            }
            else
            {
                for (int i = 0; i < _options.Count; i++)
                {
                    if (!pointer.IsTapIn(OptionBounds(i))) continue;
                    pointer.ConsumeGesture();
                    if (SelectedIndex != i) { SelectedIndex = i; WasChanged = true; }
                    IsOpen = false;
                    return WasChanged;
                }
            }

            if (PopupViewport.HasValue
                ? pointer.IsReleasedOutside(TriggerBounds) && pointer.IsReleasedOutside(PopupBounds)
                : pointer.IsReleasedOutside(FullBounds()))
            {
                pointer.ConsumeGesture();
                IsOpen = false;
            }
            return false;
        }

        /// <summary>
        /// Pointer hit-test (as <see cref="Update(Pointer)"/>) plus keyboard/gamepad control when
        /// <paramref name="focused"/>. Closed: menu-select (Enter/Space/A/Start) opens the list, "select next/previous"
        /// (Left/Right/D-pad) cycles the selection in place. Open: menu-up/down move the highlight, menu-select commits
        /// it, menu-cancel (Escape/B/Back) closes without changing. Opt-in and additive - the pointer-only overload is
        /// unchanged. Returns true if the selection changed this frame. <paramref name="player"/> scopes gamepad input
        /// (null = any player).
        /// </summary>
        public bool Update(InputManager input, bool focused, PlayerIndex? player = null)
        {
            // Keep stick edges current even while unfocused or unbounded. Wheel belongs to the popup,
            // so the bounded navigation snapshot excludes it without discarding simultaneous keys or buttons.
            _navigation.Update(input.State.WithoutScroll());
            bool changed = Update(input.Pointer, input.ScrollDelta);
            if (!focused) return changed;
            InputManager navigation = PopupViewport.HasValue ? _navigation : input;
            if (IsOpen)
            {
                if (navigation.IsMenuDown(player)) HighlightNext();
                else if (navigation.IsMenuUp(player)) HighlightPrevious();
                else if (input.IsMenuSelect(player, out _)) { if (CommitHighlight()) changed = true; }
                else if (input.IsMenuCancel(player, out _)) Close();
            }
            else
            {
                if (input.IsMenuSelect(player, out _)) Open();
                else if (input.IsSelectNext(player)) { if (StepSelection(1)) changed = true; }
                else if (input.IsSelectPrevious(player)) { if (StepSelection(-1)) changed = true; }
            }
            return changed;
        }

        /// <summary>Open the list and seed the keyboard highlight to the current selection.</summary>
        public void Open() { IsOpen = true; HighlightedIndex = SelectedIndex; ResetPopup(); }

        /// <summary>Close the list. Leaves the selection untouched.</summary>
        public void Close() => IsOpen = false;

        /// <summary>Move the keyboard highlight down one row (wraps or clamps per <see cref="Wrap"/>). Returns true if it moved.</summary>
        public bool HighlightNext() => MoveHighlight(1);

        /// <summary>Move the keyboard highlight up one row (wraps or clamps per <see cref="Wrap"/>). Returns true if it moved.</summary>
        public bool HighlightPrevious() => MoveHighlight(-1);

        bool MoveHighlight(int dir)
        {
            int from = HighlightedIndex >= 0 ? HighlightedIndex : SelectedIndex;
            int to = Step(from, dir);
            RevealOption(to);
            if (to == HighlightedIndex) return false;
            HighlightedIndex = to;
            return true;
        }

        /// <summary>
        /// Commit the keyboard highlight as the selection and close. Sets <see cref="WasChanged"/> and returns true
        /// only when the selection actually changed. No-op when no highlight is active.
        /// </summary>
        public bool CommitHighlight()
        {
            if (HighlightedIndex < 0) { IsOpen = false; return false; }
            bool changed = HighlightedIndex != SelectedIndex;
            SelectedIndex = HighlightedIndex;
            IsOpen = false;
            if (changed) WasChanged = true;
            return changed;
        }

        /// <summary>
        /// Step the selection by <paramref name="dir"/> (wraps or clamps per <see cref="Wrap"/>) without opening the
        /// list - the inline "cycle in place" for a focused, closed selector. Sets <see cref="WasChanged"/> and returns
        /// true only when the selection actually changed.
        /// </summary>
        public bool StepSelection(int dir)
        {
            int to = Step(SelectedIndex, dir);
            if (to == SelectedIndex) return false;
            SelectedIndex = to;
            WasChanged = true;
            return true;
        }

        int Step(int index, int dir)
        {
            int n = _options.Count;
            if (n == 0) return index;
            if (Wrap) return ((index + dir) % n + n) % n;
            int next = index + dir;
            return next < 0 ? 0 : (next >= n ? n - 1 : next);
        }

        /// <summary>The visible, hit-testable part of row <paramref name="i"/> when bounded, or the full legacy row
        /// when unbounded. A fully offscreen bounded row returns the empty default rectangle.</summary>
        public Rect OptionBounds(int i)
        {
            ResolvePopup();
            return PopupViewport.HasValue ? _popup.VisibleBounds(i)
                : new(TriggerBounds.X, TriggerBounds.Bottom + i * TriggerBounds.Height, TriggerBounds.Width, TriggerBounds.Height);
        }

        Rect LegacyPopupBounds => new(TriggerBounds.X, TriggerBounds.Bottom, TriggerBounds.Width, TriggerBounds.Height * _options.Count);

        void ResolvePopup()
        {
            if (PopupViewport is Rect viewport) _popup.Resolve(TriggerBounds, viewport, _options.Count);
        }

        void RevealOption(int index)
        {
            ResolvePopup();
            if (PopupViewport.HasValue) _popup.Reveal(index);
        }

        void ResetPopup()
        {
            ResolvePopup();
            if (PopupViewport.HasValue) _popup.Reset(SelectedIndex);
        }

        Rect FullBounds() =>
            new(TriggerBounds.X, TriggerBounds.Y, TriggerBounds.Width, TriggerBounds.Height * (1 + _options.Count));

        /// <summary>Draw the trigger (current label, and a chevron when <see cref="ShowChevron"/>). Safe to call inside a clip region.</summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font)
        {
            if (IsOpen) GuiDraw.HoverGlow(batch, white, TriggerBounds, Style);
            GuiDraw.FillStyled(batch, white, TriggerBounds, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(Background, Opacity), GuiDraw.WithOpacity(IsOpen ? OpenBorder : Border, Opacity));
            float ty = GuiDraw.CenteredTextY(TriggerBounds.Y, TriggerBounds.Height, font.LineHeight, TextScale);
            batch.DrawString(font, SelectedLabel, new Vector2(MathF.Floor(TriggerBounds.X + 6f), MathF.Floor(ty)),
                (Color)GuiDraw.WithOpacity(TextColor, Opacity), TextScale);

            if (ShowChevron)
            {
                var center = new Vector2(TriggerBounds.Right - 12f, TriggerBounds.Y + TriggerBounds.Height * 0.5f);
                GuiDraw.Caret(batch, white, center, halfWidth: 4f, halfHeight: 2f, pointingUp: IsOpen,
                    thickness: 1.5f, GuiDraw.WithOpacity(ChevronColor, Opacity));
            }
        }

        /// <summary>Draw the open option list. Call last (unclipped) so it overlays other content.</summary>
        public void DrawOverlay(SpriteBatch batch, Texture2D white, SpriteFont font, Pointer pointer)
        {
            if (!IsOpen) return;
            Rect list = PopupBounds;
            if (list.Width <= 0 || list.Height <= 0) return;
            if (PopupViewport is Rect viewport) batch.SetScissor(viewport);
            GuiDraw.FillStyled(batch, white, list, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(ListBackground, Opacity), GuiDraw.WithOpacity(Border, Opacity));
            if (PopupViewport.HasValue) _popup.BeginClip(batch);

            int first = FirstVisibleIndex;
            int end = first + VisibleOptionCount;
            for (int i = first; i < end; i++)
            {
                Rect r = PopupViewport.HasValue ? _popup.RowBounds(i) : OptionBounds(i);
                bool selected = i == SelectedIndex;
                // Precedence: current selection, then the keyboard/gamepad highlight (inactive at -1 for the
                // pointer-only path, so this stays byte-identical there), then pointer hover.
                if (selected) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(SelectedColor, Opacity));
                else if (i == HighlightedIndex) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(FocusColor, Opacity));
                else if (pointer.IsPointerIn(OptionBounds(i))) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(HoverColor, Opacity));
                float ty = GuiDraw.CenteredTextY(r.Y, r.Height, font.LineHeight, TextScale);
                batch.DrawString(font, _options[i].Content.Resolve(), new Vector2(MathF.Floor(r.X + 6f), MathF.Floor(ty)),
                    (Color)GuiDraw.WithOpacity(selected ? SelectedTextColor : TextColor, Opacity), TextScale);
            }
            if (PopupViewport.HasValue)
            {
                _popup.DrawScrollIndicator(batch, white, GuiDraw.WithOpacity(Border, Opacity));
                _popup.EndClip(batch);
                batch.ClearScissor();
            }
        }
    }
}
