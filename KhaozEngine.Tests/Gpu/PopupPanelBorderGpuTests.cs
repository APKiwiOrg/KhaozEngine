using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Regression: a PopupPanel with a title bar must keep its 1px border all the way around, including the top
    // edge behind the title bar. The title-bar fill spans the panel's full width from its top-left, so it used to
    // paint over the top (and upper-side) border pixels that FillStyled laid down first, leaving the border only
    // below the title bar. The fix re-strokes GuiDraw.Border after the title-bar fill. Here PanelBorder is red and
    // TitleBarColor is green: scanning down the panel's centre column, the FIRST non-background pixel must be the
    // red border, and the pixel immediately below it the green title bar - proving the border sits on top of the
    // title-bar row, not under it.
    public sealed class PopupPanelBorderGpuTests
    {
        const int W = 400, H = 400;

        [GpuFact]
        public void Title_bar_does_not_paint_over_the_top_border()
        {
            var panel = new PopupPanel
            {
                Viewport = new Vector2(W, H),
                ScrimOpacity = 0f,                       // no scrim: background stays the black clear colour
                PanelColor = new Vector4(0f, 0f, 1f, 1f), // opaque blue body
                PanelBorder = new Vector4(1f, 0f, 0f, 1f), // red border
                TitleBarColor = new Vector4(0f, 1f, 0f, 1f), // green title bar
            };
            // Empty body, no fonts: DrawButton falls back to a plain fill and never reads the pointer.
            panel.SetRows(System.Array.Empty<PopupRow>());

            Rect p = panel.PanelRect();
            int cx = (int)(p.X + p.Width * 0.5f);

            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                panel.Draw(ctx.Batch, white, new Pointer());
                ctx.Batch.End();
            });

            // Walk the centre column from the top; the first non-black pixel is the panel's top edge.
            int topEdge = -1;
            for (int y = 0; y < H; y++)
            {
                int i = (y * W + cx) * 4;
                bool black = rgba[i] < 40 && rgba[i + 1] < 40 && rgba[i + 2] < 40;
                if (!black) { topEdge = y; break; }
            }
            Assert.True(topEdge >= 0, "panel never rendered into the centre column");

            // Top edge must be the red border (R dominant, G low), not the green title bar.
            int t = (topEdge * W + cx) * 4;
            Assert.True(rgba[t] > 180 && rgba[t + 1] < 100,
                $"top edge should be the red border, got rgba=({rgba[t]},{rgba[t + 1]},{rgba[t + 2]})");

            // The row just below the border must be the green title bar (G dominant, R low).
            int b = ((topEdge + 1) * W + cx) * 4;
            Assert.True(rgba[b + 1] > 180 && rgba[b] < 100,
                $"row below the border should be the green title bar, got rgba=({rgba[b]},{rgba[b + 1]},{rgba[b + 2]})");
        }
    }
}
