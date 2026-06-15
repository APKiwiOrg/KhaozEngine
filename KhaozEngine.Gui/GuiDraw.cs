using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;

namespace KhaozEngine.Gui
{
    /// <summary>
    /// Small rectangle-drawing helpers shared by the widgets, drawn with a 1x1 white texture through the
    /// <see cref="SpriteBatch"/> (Render2D has no primitive renderer; this fills that gap for Gui).
    /// </summary>
    internal static class GuiDraw
    {
        /// <summary>Fill <paramref name="r"/> with a solid color.</summary>
        public static void Fill(SpriteBatch batch, Texture2D white, Rect r, Vector4 color) =>
            batch.Draw(white, new Vector4(r.X, r.Y, r.Width, r.Height), color);

        /// <summary>Draw a <paramref name="thickness"/>-px outline just inside <paramref name="r"/>.</summary>
        public static void Border(SpriteBatch batch, Texture2D white, Rect r, float thickness, Vector4 color)
        {
            if (thickness <= 0f) return;
            float t = thickness;
            Fill(batch, white, new Rect(r.X, r.Y, r.Width, t), color);                       // top
            Fill(batch, white, new Rect(r.X, r.Bottom - t, r.Width, t), color);              // bottom
            Fill(batch, white, new Rect(r.X, r.Y, t, r.Height), color);                      // left
            Fill(batch, white, new Rect(r.Right - t, r.Y, t, r.Height), color);              // right
        }
    }
}
