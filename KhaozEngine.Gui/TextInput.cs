using System;
using System.Numerics;
using KhaozEngine.App;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A single-line text field over <see cref="Pointer"/> + <see cref="TextEntry"/>: a tap inside focuses it,
    /// a tap outside unfocuses; while focused, this frame's typed keys edit <see cref="Text"/> (and Ctrl+V / Cmd+V
    /// pastes the clipboard). Draws a bordered field with the text (or the <see cref="PlaceholderContent"/>) and a
    /// blinking caret. Typed input comes from the headless key-mapping in TextEntry. <see cref="SetText"/> replaces
    /// the buffer programmatically and is picked up (with <see cref="TextChanged"/>) on the next <see cref="Update"/>.
    /// </summary>
    public sealed class TextInput
    {
        public Rect Bounds;
        public string Text = "";
        public bool IsFocused;
        public int MaxLength = 32;
        /// <summary>Optional per-char admission filter passed through to <see cref="TextEntry"/>, receiving the
        /// buffer accumulated so far this call alongside the candidate char (not a pre-call snapshot), so a
        /// stateful filter (e.g. "at most one dot") sees every char already admitted earlier in the same
        /// multi-key frame or paste.</summary>
        public Func<string, char, bool>? CharFilter;
        public SpriteFont? Font;

        /// <summary>The (lazily resolved) placeholder text drawn when the field is empty. Defaults to empty.</summary>
        public LocalizedText PlaceholderContent;

        /// <summary>Obsolete shim for the former string field. Setting <c>Placeholder</c> stores a raw, non-localized value.</summary>
        [Obsolete("Use PlaceholderContent (LocalizedText). Setting Placeholder stores a raw, non-localized value.")]
        [LocalizationExempt]
        public string Placeholder
        {
            get => PlaceholderContent.Resolve();
            set => PlaceholderContent = LocalizedText.Raw(value);
        }

        /// <summary>True on the frame <see cref="Text"/> changed (by typing or a <see cref="SetText"/> call).</summary>
        public bool TextChanged { get; private set; }
        /// <summary>Whether the caret is currently in its visible blink phase.</summary>
        public bool CursorVisible { get; private set; } = true;

        public Vector4 Background = new(0.12f, 0.12f, 0.16f, 1f);
        public Vector4 Border = new(0.31f, 0.31f, 0.39f, 1f);
        public Vector4 BorderFocused = new(0.47f, 0.55f, 0.78f, 1f);
        public Vector4 TextColor = new(0.90f, 0.90f, 0.94f, 1f);
        public Vector4 PlaceholderColor = new(0.39f, 0.39f, 0.47f, 1f);
        public Vector4 CursorColor = new(0.70f, 0.78f, 1f, 1f);

        /// <summary>
        /// Modern-look knobs (rounded/shadow/gradient/glow) for the field box; defaults to the flat
        /// <see cref="GuiStyle.Default"/> so the field renders byte-identically to pre-7.8.0. The field keeps its
        /// own colours (<see cref="Background"/>/<see cref="Border"/>/<see cref="BorderFocused"/> etc.); the caret
        /// stays a flat sliver. A focused field with a glowing style draws a focus halo. Set
        /// <c>Style = GuiStyle.Modern</c> to opt in.
        /// </summary>
        public GuiStyle Style = GuiStyle.Default;

        /// <summary>
        /// Uniform fade multiplied into every colour's alpha at draw time (1 = opaque). Lets a caller fade the whole
        /// field in/out with a host transition. Default 1 is a no-op. Mirrors <see cref="Dropdown.Opacity"/>.
        /// </summary>
        public float Opacity = 1f;

        /// <summary>
        /// Uniform scale for the field's text and placeholder. Defaults to <c>1f</c> (today's rendering,
        /// byte-for-byte). Scales the TEXT only: <see cref="Bounds"/>, the box chrome, the caret sliver's own width
        /// and height, and all hit-testing are unchanged at any scale, so a compact field draws smaller text in the
        /// same rect. Mirrors <see cref="TabBar.TextScale"/>. Every width term the draw derives rides the scale
        /// (<see cref="DrawLayout"/>), so the caret still trails the last glyph and the overflow clip still engages
        /// at the point the drawn text actually reaches the right border.
        /// </summary>
        public float TextScale = 1f;

        const float BlinkRate = 0.5f;
        const float PadX = 8f;
        const float CaretWidth = 2f;
        float _blink;
        string _previousText = "";

        public TextInput(Rect bounds, SpriteFont? font = null) { Bounds = bounds; Font = font; }

        /// <summary>
        /// Replace the buffer programmatically (e.g. a suggested value), clamped to <see cref="MaxLength"/>. The next
        /// <see cref="Update"/> sees the change and raises <see cref="TextChanged"/>, so consumers re-validate exactly
        /// as they do for typed input.
        /// </summary>
        public void SetText(string value)
        {
            string next = value ?? "";
            if (next.Length > MaxLength) next = next.Substring(0, MaxLength);
            Text = next;
        }

        /// <summary>Update focus, typing, and caret blink. Returns whether the field is focused (consumes keyboard).</summary>
        public bool Update(Pointer pointer, InputState input, float dt)
        {
            pointer.BlockRegion(Bounds); // reserve the field for click-through (the click-through gate)
            if (pointer.IsTapIn(Bounds)) Focus();
            else if (pointer.IsReleasedOutside(Bounds)) Unfocus();

            if (IsFocused)
            {
                Text = TextEntry.Apply(Text, input, MaxLength, CharFilter);

                _blink += dt;
                while (_blink >= BlinkRate) { _blink -= BlinkRate; CursorVisible = !CursorVisible; }
            }

            // Detect change from either typing or a SetText call (compared against last frame's buffer).
            TextChanged = Text != _previousText;
            if (TextChanged) ResetBlink();
            _previousText = Text;
            return IsFocused;
        }

        /// <summary>Give the field keyboard focus (resets the caret blink). No-op if already focused.</summary>
        public void Focus() { if (!IsFocused) { IsFocused = true; ResetBlink(); } }

        /// <summary>Remove keyboard focus. No-op if not focused.</summary>
        public void Unfocus() { IsFocused = false; }

        void ResetBlink() { CursorVisible = true; _blink = 0f; }

        /// <summary>Draw the field, text/placeholder, and caret. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            if (Font == null) return;
            if (IsFocused) GuiDraw.HoverGlow(batch, white, Bounds, Style);
            GuiDraw.FillStyled(batch, white, Bounds, Style with { BorderThickness = 1f },
                GuiDraw.WithOpacity(Background, Opacity), GuiDraw.WithOpacity(IsFocused ? BorderFocused : Border, Opacity));

            // A nine-slice skin's frame can be thicker than the fixed pad, so clear it (no-skin: PadX, unchanged).
            float textX = Bounds.X + (Style.Skin != null ? MathF.Max(PadX, Style.ContentInsets(Bounds).X) : PadX);
            bool empty = Text.Length == 0;
            string shown = empty ? PlaceholderContent.Resolve() : Text;
            TextInputLayout layout = DrawLayout(Font, Bounds, textX, shown, Text, TextScale);

            if (layout.Clip) batch.SetScissor(Bounds);
            batch.DrawString(Font, shown, new Vector2(MathF.Floor(layout.TextX), MathF.Floor(layout.TextY)),
                (Color)GuiDraw.WithOpacity(empty ? PlaceholderColor : TextColor, Opacity), TextScale);

            if (IsFocused && CursorVisible)
                GuiDraw.Fill(batch, white, new Rect(layout.CaretX, Bounds.Y + 4f, CaretWidth, Bounds.Height - 8f),
                    GuiDraw.WithOpacity(CursorColor, Opacity));
            if (layout.Clip) batch.ClearScissor();
        }

        /// <summary>Where <see cref="Draw"/> puts the text and the caret, and whether it has to scissor.</summary>
        internal readonly record struct TextInputLayout(float TextX, float TextY, float CaretX, bool Clip);

        /// <summary>
        /// The pure draw layout for one field, so the three places a text scale has to reach are one expression each
        /// and testable without a device. <paramref name="textX"/> is the already-resolved left inset (fixed pad, or a
        /// nine-slice skin's frame), which no scale touches.
        /// <list type="bullet">
        /// <item>the vertical centring, on <c>LineHeight * scale</c></item>
        /// <item>the caret x, which trails the DRAWN width of <paramref name="text"/> (0 when the field is empty and
        /// the placeholder is what is showing, so the caret sits at the text origin)</item>
        /// <item>the overflow test, which asks whether the DRAWN text plus the caret crosses the right border. The
        /// field draws at a fixed offset and lets its text run as wide as it measures, so a value wider than the box
        /// would otherwise paint straight past the border into whatever sits beside it. Only an actual overflow
        /// scissors: <c>SetScissor</c> flushes the batch, and two extra flushes per field per frame to clip nothing
        /// would be a poor trade in a form full of fields.</item>
        /// </list>
        /// Getting the overflow test wrong is silent (it under-clips or over-flushes rather than throwing), which is
        /// exactly why it lives here beside the other two rather than inline in the draw.
        /// </summary>
        internal static TextInputLayout DrawLayout(ITextMeasurer font, Rect bounds, float textX,
            string shown, string text, float scale)
        {
            float textY = GuiDraw.CenteredTextY(bounds.Y, bounds.Height, font.LineHeight, scale);
            bool clip = textX + font.Measure(shown).X * scale + CaretWidth + 1f > bounds.Right;
            float caretX = textX + (text.Length == 0 ? 0f : font.Measure(text).X * scale) + 1f;
            return new TextInputLayout(textX, textY, caretX, clip);
        }
    }
}
