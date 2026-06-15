using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A non-interactive text widget: draws <see cref="Text"/> in <see cref="Font"/> aligned within
    /// <see cref="Bounds"/>, optionally word-wrapped. Pure presentation over the (tested) <see cref="TextLayout"/>
    /// helpers; no per-frame update. Ported from the 4.x <c>UI</c> label/text usage.
    /// </summary>
    public sealed class Label
    {
        public Rect Bounds;
        public string Text;
        public SpriteFont Font;
        public Vector4 Color = Vector4.One;
        public TextAlign Align = TextAlign.Left;
        /// <summary>When true, the text word-wraps to <see cref="Bounds"/>.Width; otherwise it draws on one line.</summary>
        public bool Wrap;
        /// <summary>When true, a single (unwrapped) line is centered vertically within <see cref="Bounds"/>.</summary>
        public bool VerticalCenter = true;

        public Label(Rect bounds, string text, SpriteFont font)
        {
            Bounds = bounds; Text = text; Font = font;
        }

        /// <summary>Draw the label's text into <see cref="Bounds"/>.</summary>
        public void Draw(SpriteBatch batch)
        {
            if (Wrap)
            {
                TextLayout.DrawWrapped(batch, Font, Text, new Vector2(Bounds.X, Bounds.Y), Bounds.Width, Align, Color);
                return;
            }
            float y = VerticalCenter ? Bounds.Y + (Bounds.Height - Font.LineHeight) * 0.5f : Bounds.Y;
            TextLayout.DrawAligned(batch, Font, Text, Bounds.X, Bounds.Width, y, Align, Color);
        }
    }
}
