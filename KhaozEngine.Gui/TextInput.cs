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
        public Func<char, bool>? CharFilter;
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

        const float BlinkRate = 0.5f;
        const float PadX = 8f;
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
            float textY = Bounds.Y + (Bounds.Height - Font.LineHeight) * 0.5f;
            bool empty = Text.Length == 0;
            string shown = empty ? PlaceholderContent.Resolve() : Text;
            batch.DrawString(Font, shown, new Vector2(MathF.Floor(textX), MathF.Floor(textY)),
                (Color)GuiDraw.WithOpacity(empty ? PlaceholderColor : TextColor, Opacity));

            if (IsFocused && CursorVisible)
            {
                float caretX = textX + (empty ? 0f : Font.Measure(Text).X) + 1f;
                GuiDraw.Fill(batch, white, new Rect(caretX, Bounds.Y + 4f, 2f, Bounds.Height - 8f), GuiDraw.WithOpacity(CursorColor, Opacity));
            }
        }
    }
}
