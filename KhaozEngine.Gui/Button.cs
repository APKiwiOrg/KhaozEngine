using System;
using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// A bounds-aware button over <see cref="Pointer"/>: clicks fire through the press-origin
    /// <see cref="Pointer.IsTapIn"/> invariant (a click that began elsewhere can't trigger it), with
    /// hover/press visuals. Call <see cref="Update"/> then <see cref="Draw"/> each frame.
    /// </summary>
    public sealed class Button
    {
        public Rect Bounds;
        public string Label;
        public SpriteFont Font;
        public Action? OnClick;

        public Vector4 Color = new(0.18f, 0.30f, 0.42f, 1f);
        public Vector4 HoverColor = new(0.26f, 0.50f, 0.66f, 1f);
        public Vector4 PressColor = new(0.20f, 0.40f, 0.55f, 1f);
        public Vector4 TextColor = Vector4.One;

        bool _hover, _press;

        public Button(Rect bounds, string label, SpriteFont font, Action? onClick = null)
        {
            Bounds = bounds; Label = label; Font = font; OnClick = onClick;
        }

        /// <summary>Hit-test against the pointer; fires <see cref="OnClick"/> on a valid tap. Returns true if clicked.</summary>
        public bool Update(Pointer pointer)
        {
            _hover = pointer.IsHoveringIn(Bounds);
            _press = pointer.IsPressingIn(Bounds);
            if (pointer.IsTapIn(Bounds)) { OnClick?.Invoke(); return true; }
            return false;
        }

        /// <summary>Draw the button. <paramref name="white"/> is a 1x1 white texture for the fill.</summary>
        public void Draw(SpriteBatch batch, Texture2D white)
        {
            Vector4 c = _press ? PressColor : _hover ? HoverColor : Color;
            batch.Draw(white, new Vector4(Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height), c);
            Vector2 size = Font.Measure(Label);
            var pos = new Vector2(Bounds.X + (Bounds.Width - size.X) * 0.5f, Bounds.Y + (Bounds.Height - Font.LineHeight) * 0.5f);
            batch.DrawString(Font, Label, pos, TextColor);
        }
    }
}
