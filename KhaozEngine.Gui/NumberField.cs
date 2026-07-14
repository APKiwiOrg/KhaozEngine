using System;
using System.Globalization;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A numeric field for editor inspectors, driven by <see cref="InputManager"/> (it needs the keyboard, so it
    /// takes the manager rather than a bare <see cref="Pointer"/>). A horizontal drag whose press began inside
    /// <see cref="Bounds"/> scrubs <see cref="Value"/> by <see cref="DragScale"/> value units per pixel of pointer
    /// movement (the slider grab-gate, so the scrub keeps tracking after the cursor strays off). A tap - press and
    /// release inside with under 3 draw units of travel - enters typing mode: this frame's typed keys edit a buffer
    /// through <see cref="TextEntry"/> with a numeric filter (digits, one leading minus, one dot), Enter commits,
    /// Escape cancels, and a tap outside <see cref="Bounds"/> commits like Enter. Commit parses with
    /// <see cref="CultureInfo.InvariantCulture"/>, clamps to [<see cref="Min"/>, <see cref="Max"/>] and rounds to
    /// <see cref="Decimals"/>; unparseable text reverts to the value that was showing when editing began.
    /// <see cref="Value"/> is always clamped on a real mutation. Follows the <see cref="Toggle"/>/<see cref="Slider"/>
    /// anatomy: reserve the region first (even when disabled), <see cref="Pointer"/> bounds helpers only,
    /// <see cref="GuiTheme"/> colours captured at construction, and the <see cref="GuiStyle"/> affordance knobs.
    /// </summary>
    public sealed class NumberField
    {
        /// <summary>The interactive rectangle: the scrub surface and the typing box.</summary>
        public Rect Bounds;

        /// <summary>Current value, clamped to [<see cref="Min"/>, <see cref="Max"/>] on every mutation through
        /// <see cref="SetValue"/> (the field is public for direct reads and initialiser assignment, like
        /// <see cref="Slider.Value"/>).</summary>
        public float Value;

        /// <summary>Lower clamp bound. Default <see cref="float.MinValue"/>.</summary>
        public float Min { get; set; } = float.MinValue;

        /// <summary>Upper clamp bound. Default <see cref="float.MaxValue"/>.</summary>
        public float Max { get; set; } = float.MaxValue;

        /// <summary>Value units added per pixel of horizontal drag while scrubbing. Default 0.01.</summary>
        public float DragScale { get; set; } = 0.01f;

        /// <summary>Fractional digits used for the display text and for the round applied on commit. Default 2.</summary>
        public int Decimals { get; set; } = 2;

        /// <summary>When false, the field neither scrubs nor edits; any in-progress edit is closed.</summary>
        public bool Enabled = true;

        /// <summary>Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Default 1 is a
        /// no-op. Mirrors <see cref="Slider.Opacity"/>.</summary>
        public float Opacity = 1f;

        /// <summary>Modern-look knobs (rounded/shadow/gradient/glow) for the field box; defaults to the flat
        /// <see cref="GuiStyle.Default"/>. Set <c>Style = GuiStyle.Modern</c> to opt in.</summary>
        public GuiStyle Style = GuiStyle.Default;

        /// <summary>True on the frame <see cref="Value"/> changed (by a scrub step or a commit). A convenience
        /// mirror of the <see cref="Update"/> return value, matching <see cref="Slider.WasChanged"/>.</summary>
        public bool WasChanged { get; private set; }

        /// <summary>True while the field is in typing mode (a tap opened it, no commit/cancel yet).</summary>
        public bool IsEditing { get; private set; }

        /// <summary>
        /// True while a scrub is in progress: a held drag whose press began inside <see cref="Bounds"/> (the
        /// grab-gate), so it stays true even after the cursor strays off the field, and drops on release. A caller
        /// (e.g. a <see cref="PropertyGrid"/> row) reads this instead of re-deriving the press-origin rule to know
        /// whether the field is mid-gesture and must not be stomped by an external poll.
        /// </summary>
        public bool IsScrubbing { get; private set; }

        /// <summary>Fired on a real change to <see cref="Value"/> (scrub step or commit), with the new value.</summary>
        public Action<float>? OnChanged;

        public Vector4 BackgroundColor = GuiTheme.Default.Surface;
        public Vector4 BorderColor = GuiTheme.Default.Border;
        public Vector4 BorderEditingColor = GuiTheme.Default.AccentBright;
        public Vector4 TextColor = GuiTheme.Default.Text;
        public Vector4 CaretColor = GuiTheme.Default.AccentBright;
        public Vector4 DisabledColor = GuiTheme.Default.SurfaceDisabled;
        public Vector4 DisabledTextColor = GuiTheme.Default.TextDisabled;

        // Travel (draw units) below which a press+release counts as a tap rather than a scrub.
        const float TapThreshold = 3f;
        // Cap on the typed buffer, matching the brief's TextEntry.Apply length.
        const int MaxEditLength = 16;
        const float BlinkRate = 0.5f;
        const float PadX = 6f;

        readonly Func<char, bool> _numericFilter;   // cached so Update allocates no delegate per frame
        string _editBuffer = "";
        float _preEditValue;
        bool _selectAll;   // the seeded value is "selected", so the first keystroke replaces it
        float _blink;
        bool _caretVisible = true;

        public NumberField(Rect bounds, float value = 0f, Action<float>? onChanged = null)
        {
            Bounds = bounds;
            OnChanged = onChanged;
            _numericFilter = NumericFilter;
            Value = Math.Clamp(value, Min, Max);
        }

        /// <summary>
        /// Scrub, tap-to-edit, or type this frame. Reserves <see cref="Bounds"/> on the pointer first (the
        /// click-through gate) even when disabled, then: while editing, applies typed keys and watches for
        /// Enter/Escape/tap-outside; otherwise scrubs on a held drag that began inside, and opens typing mode on a
        /// low-travel tap. Returns whether <see cref="Value"/> changed this frame.
        /// </summary>
        public bool Update(InputManager input, float dt)
        {
            WasChanged = false;
            input.BlockInputRegion(Bounds);   // reserve the region even when disabled

            if (!Enabled)
            {
                IsEditing = false;
                IsScrubbing = false;
                _selectAll = false;
                return false;
            }

            Pointer pointer = input.Pointer;

            if (IsEditing)
            {
                IsScrubbing = false;   // typing mode never scrubs
                // The first edit after entering replaces the seeded value (select-all-on-focus): clear the buffer
                // before applying so the numeric filter validates against an empty buffer, then feed this frame's
                // typed keys through TextEntry with the numeric filter.
                if (_selectAll && HasEditKeystroke(input.State))
                {
                    _editBuffer = "";
                    _selectAll = false;
                }
                _editBuffer = TextEntry.Apply(_editBuffer, input, MaxEditLength, _numericFilter);

                _blink += dt;
                while (_blink >= BlinkRate) { _blink -= BlinkRate; _caretVisible = !_caretVisible; }

                if (input.State.WasPressed(Key.Enter)) CommitEdit();
                else if (input.State.WasPressed(Key.Escape)) CancelEdit();
                else if (pointer.IsReleasedOutside(Bounds)) CommitEdit();   // a tap outside commits like Enter

                return WasChanged;
            }

            // Scrub: a drag whose press began inside keeps tracking (the grab-gate), moving Value by the pointer's
            // horizontal delta scaled by DragScale, clamped. IsScrubbing mirrors the grab-gate so it holds even once
            // the cursor strays off the field, and clears the moment the button releases.
            if (pointer.IsDragStartIn(Bounds))
            {
                IsScrubbing = true;
                float dx = pointer.Delta.X;
                if (dx != 0f) SetValue(Value + dx * DragScale);
            }
            else IsScrubbing = false;

            // Tap-to-edit: a press+release inside with negligible travel (distinguishes a tap from a scrub) opens
            // typing mode, seeding the buffer with the current value.
            if (pointer.IsTapIn(Bounds))
            {
                Vector2 travel = pointer.Position - pointer.PressOrigin;
                if (travel.LengthSquared() < TapThreshold * TapThreshold) BeginEdit();
            }

            return WasChanged;
        }

        /// <summary>
        /// Set <see cref="Value"/>, clamped to [<see cref="Min"/>, <see cref="Max"/>]. On a real change it raises
        /// <see cref="WasChanged"/>, fires <see cref="OnChanged"/>, and returns true. Usable from keyboard/scrub
        /// paths and from callers driving the field programmatically.
        /// </summary>
        public bool SetValue(float value)
        {
            float clamped = Math.Clamp(value, Min, Max);
            if (clamped == Value) return false;
            Value = clamped;
            WasChanged = true;
            OnChanged?.Invoke(Value);
            return true;
        }

        void BeginEdit()
        {
            IsEditing = true;
            _preEditValue = Value;
            _editBuffer = FormatValue(Value);
            _selectAll = true;
            ResetBlink();
        }

        void CommitEdit()
        {
            if (float.TryParse(_editBuffer, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                SetValue(MathF.Round(parsed, RoundDigits));
            else
                SetValue(_preEditValue);   // unparseable text reverts (no-op when it equals the current value)

            IsEditing = false;
            _selectAll = false;
        }

        /// <summary>
        /// Exit typing mode WITHOUT committing the buffer, leaving <see cref="Value"/> at the pre-edit value (the
        /// field never mutates <see cref="Value"/> while editing, so no revert is needed). Mirrors the Escape path
        /// and is the hook a host uses to close an edit it is tearing down (e.g. a <see cref="PropertyGrid"/> row
        /// scrolling out of view). No-op when not editing.
        /// </summary>
        public void CancelEdit()
        {
            // Value was never mutated while editing, so exiting leaves it at the pre-edit value.
            IsEditing = false;
            _selectAll = false;
        }

        // Digits used for both the display format and the commit round, clamped to the Single rounding range.
        int RoundDigits => Math.Clamp(Decimals, 0, 6);

        string FormatValue(float value)
        {
            string spec = "F" + RoundDigits.ToString(CultureInfo.InvariantCulture);
            return value.ToString(spec, CultureInfo.InvariantCulture);
        }

        // The numeric filter: accept digits, a single leading minus, and a single dot. The candidate char is
        // validated against the current buffer (single-key frames, so the field reflects the pre-char state).
        bool NumericFilter(char c)
        {
            if (c >= '0' && c <= '9') return true;
            if (c == '-') return _editBuffer.Length == 0;          // minus only as the first char (so only one)
            if (c == '.') return !_editBuffer.Contains('.');       // one dot
            return false;
        }

        // True if this frame carries a text-edit keystroke the field accepts (a digit, minus, dot, or backspace,
        // from the top row or the keypad). Used to end the select-all seed so the first accepted key replaces the
        // seeded value rather than appending to it.
        static bool HasEditKeystroke(InputState state)
        {
            if (state.WasTyped(Key.Backspace) || state.WasTyped(Key.Minus) || state.WasTyped(Key.Period)
                || state.WasTyped(Key.KeypadSubtract) || state.WasTyped(Key.KeypadDecimal))
                return true;
            for (Key k = Key.D0; k <= Key.D9; k++)
                if (state.WasTyped(k)) return true;
            for (Key k = Key.Keypad0; k <= Key.Keypad9; k++)
                if (state.WasTyped(k)) return true;
            return false;
        }

        void ResetBlink() { _caretVisible = true; _blink = 0f; }

        /// <summary>Draw the field box and the value (or the edit buffer while typing). <paramref name="white"/> is
        /// a 1x1 white texture and <paramref name="font"/> renders the numeric text.</summary>
        public void Draw(SpriteBatch batch, Texture2D white, SpriteFont font)
        {
            Vector4 body = !Enabled ? DisabledColor : BackgroundColor;
            Vector4 border = IsEditing && Enabled ? BorderEditingColor : BorderColor;
            if (IsEditing && Enabled) GuiDraw.HoverGlow(batch, white, Bounds, Style);
            GuiDraw.FillStyled(batch, white, Bounds, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(body, Opacity), GuiDraw.WithOpacity(border, Opacity));

            string shown = IsEditing ? _editBuffer : FormatValue(Value);
            Vector4 textColor = !Enabled ? DisabledTextColor : TextColor;
            // A nine-slice skin's frame can be thicker than the fixed pad, so clear it (no-skin: PadX, unchanged).
            float pad = Style.Skin != null ? MathF.Max(PadX, Style.ContentInsets(Bounds).X) : PadX;
            Vector2 pos = GuiDraw.AlignedTextPos(Bounds, font.Measure(shown), font.LineHeight, GuiAlign.Left, 1f, pad);
            batch.DrawString(font, shown, new Vector2(MathF.Floor(pos.X), MathF.Floor(pos.Y)),
                (Color)GuiDraw.WithOpacity(textColor, Opacity));

            if (IsEditing && _caretVisible)
            {
                float caretX = pos.X + font.Measure(shown).X + 1f;
                GuiDraw.Fill(batch, white, new Rect(caretX, Bounds.Y + 4f, 2f, Bounds.Height - 8f),
                    GuiDraw.WithOpacity(CaretColor, Opacity));
            }
        }
    }
}
