using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A single-line text field over <see cref="Pointer"/> + <see cref="TextEntry"/>: a tap inside focuses it,
    /// a tap outside unfocuses; while focused, this frame's typed keys edit <see cref="Text"/>. Draws a bordered
    /// field with the text (or <see cref="Placeholder"/>) and a blinking caret. Ported from the 4.x
    /// <c>UI.TextInput</c> (which hooked SDL's TextInput event; this uses the headless key-mapping in TextEntry).
    /// </summary>
    public sealed class TextInput
    {
        public Rect Bounds;
        public string Text = "";
        public bool IsFocused;
        public int MaxLength = 32;
        public string Placeholder = "";
        public Func<char, bool>? CharFilter;
        public SpriteFont? Font;

        /// <summary>True on the frame <see cref="Text"/> changed.</summary>
        public bool TextChanged { get; private set; }
        /// <summary>Whether the caret is currently in its visible blink phase.</summary>
        public bool CursorVisible { get; private set; } = true;

        public Vector4 Background = new(0.12f, 0.12f, 0.16f, 1f);
        public Vector4 Border = new(0.31f, 0.31f, 0.39f, 1f);
        public Vector4 BorderFocused = new(0.47f, 0.55f, 0.78f, 1f);
        public Vector4 TextColor = new(0.90f, 0.90f, 0.94f, 1f);
        public Vector4 PlaceholderColor = new(0.39f, 0.39f, 0.47f, 1f);
        public Vector4 CursorColor = new(0.70f, 0.78f, 1f, 1f);

        const float BlinkRate = 0.5f;
        const float PadX = 8f;
        float _blink;

        public TextInput(Rect bounds, SpriteFont? font = null) { Bounds = bounds; Font = font; }

        /// <summary>Update focus, typing, and caret blink. Returns whether the field is focused (consumes keyboard).</summary>
        public bool Update(Pointer pointer, InputState input, float dt)
        {
            TextChanged = false;

            pointer.BlockRegion(Bounds); // reserve the field for click-through (the click-through gate)
            if (pointer.IsTapIn(Bounds)) Focus();
            else if (pointer.IsReleasedOutside(Bounds)) IsFocused = false;

            if (IsFocused)
            {
                string before = Text;
                Text = TextEntry.Apply(Text, input, MaxLength, CharFilter);
                if (Text != before) { TextChanged = true; ResetBlink(); }

                _blink += dt;
                while (_blink >= BlinkRate) { _blink -= BlinkRate; CursorVisible = !CursorVisible; }
            }
            return IsFocused;
        }

        void Focus() { if (!IsFocused) { IsFocused = true; ResetBlink(); } }
        void ResetBlink() { CursorVisible = true; _blink = 0f; }

        /// <summary>Draw the field, text/placeholder, and caret. <paramref name="white"/> is a 1x1 white texture.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            if (Font == null) return;
            GuiDraw.Fill(batch, white, Bounds, Background);
            GuiDraw.Border(batch, white, Bounds, 1f, IsFocused ? BorderFocused : Border);

            float textX = Bounds.X + PadX;
            float textY = Bounds.Y + (Bounds.Height - Font.LineHeight) * 0.5f;
            bool empty = Text.Length == 0;
            string shown = empty ? Placeholder : Text;
            batch.DrawString(Font, shown, new Vector2(MathF.Floor(textX), MathF.Floor(textY)),
                empty ? PlaceholderColor : TextColor);

            if (IsFocused && CursorVisible)
            {
                float caretX = textX + (empty ? 0f : Font.Measure(Text).X) + 1f;
                GuiDraw.Fill(batch, white, new Rect(caretX, Bounds.Y + 4f, 2f, Bounds.Height - 8f), CursorColor);
            }
        }
    }
}
