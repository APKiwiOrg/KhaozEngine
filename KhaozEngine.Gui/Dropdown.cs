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
    public readonly record struct DropdownOption(LocalizedText Content, int Value)
    {
        /// <summary>Obsolete: pass a <see cref="LocalizedText"/>. A raw string bypasses localization.</summary>
        [Obsolete("Pass a LocalizedText; a raw string bypasses localization. Use a StringId or LocalizedText.Raw(...) for non-localizable text.")]
        [LocalizationStringSink]
        [LocalizationExempt]
        public DropdownOption(string label, int value) : this(LocalizedText.Raw(label), value) { }

        /// <summary>Obsolete shim for the former string member: resolves <see cref="Content"/> against the
        /// ambient catalog.</summary>
        [Obsolete("Use Content (LocalizedText), or the Dropdown's SelectedLabel for the resolved string.")]
        [LocalizationExempt]
        public string Label => Content.Resolve();
    }

    /// <summary>
    /// A selector over <see cref="Pointer"/>: the trigger shows the current option; a tap opens a list below it;
    /// tapping an option selects + closes; a release outside dismisses. Because the open list extends past the
    /// trigger, drawing is split: <see cref="Draw"/> renders the trigger (inside any clip), <see cref="DrawOverlay"/>
    /// renders the open list (call last / unclipped so it sits on top).
    /// </summary>
    public sealed class Dropdown
    {
        readonly List<DropdownOption> _options;

        /// <summary>Trigger button bounds; option rows render below it. Update before <see cref="Update(Pointer)"/> if it moves.</summary>
        public Rect TriggerBounds;
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

        /// <summary>Hit-test open/close/select/dismiss. Returns true if the selection changed.</summary>
        public bool Update(Pointer pointer)
        {
            WasChanged = false;
            // Reserve the trigger (closed) or the whole expanded list (open) for click-through, so a layer
            // beneath can't be clicked through the dropdown.
            pointer.BlockRegion(IsOpen ? FullBounds() : TriggerBounds);
            if (!IsOpen)
            {
                // Pointer-driven open: no keyboard cursor, so the overlay draws byte-identically to pre-key-nav.
                if (pointer.IsTapIn(TriggerBounds)) { IsOpen = true; HighlightedIndex = -1; }
                return false;
            }

            if (pointer.IsTapIn(TriggerBounds)) { IsOpen = false; return false; }

            for (int i = 0; i < _options.Count; i++)
            {
                if (!pointer.IsTapIn(OptionBounds(i))) continue;
                if (SelectedIndex != i) { SelectedIndex = i; WasChanged = true; }
                IsOpen = false;
                return WasChanged;
            }

            if (pointer.IsReleasedOutside(FullBounds())) IsOpen = false;
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
            bool changed = Update(input.Pointer);
            if (!focused) return changed;
            if (IsOpen)
            {
                if (input.IsMenuDown(player)) HighlightNext();
                else if (input.IsMenuUp(player)) HighlightPrevious();
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
        public void Open() { IsOpen = true; HighlightedIndex = SelectedIndex; }

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

        /// <summary>The bounds of option row <paramref name="i"/> in the open list.</summary>
        public Rect OptionBounds(int i) =>
            new(TriggerBounds.X, TriggerBounds.Bottom + i * TriggerBounds.Height, TriggerBounds.Width, TriggerBounds.Height);

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
            var list = new Rect(TriggerBounds.X, TriggerBounds.Bottom, TriggerBounds.Width, TriggerBounds.Height * _options.Count);
            GuiDraw.FillStyled(batch, white, list, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(ListBackground, Opacity), GuiDraw.WithOpacity(Border, Opacity));

            for (int i = 0; i < _options.Count; i++)
            {
                Rect r = OptionBounds(i);
                bool selected = i == SelectedIndex;
                // Precedence: current selection, then the keyboard/gamepad highlight (inactive at -1 for the
                // pointer-only path, so this stays byte-identical there), then pointer hover.
                if (selected) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(SelectedColor, Opacity));
                else if (i == HighlightedIndex) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(FocusColor, Opacity));
                else if (pointer.IsPointerIn(r)) GuiDraw.Fill(batch, white, r, GuiDraw.WithOpacity(HoverColor, Opacity));
                float ty = GuiDraw.CenteredTextY(r.Y, r.Height, font.LineHeight, TextScale);
                batch.DrawString(font, _options[i].Content.Resolve(), new Vector2(MathF.Floor(r.X + 6f), MathF.Floor(ty)),
                    (Color)GuiDraw.WithOpacity(selected ? SelectedTextColor : TextColor, Opacity), TextScale);
            }
        }
    }
}
