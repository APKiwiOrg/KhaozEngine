using System.Numerics;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // A theme that makes the panel body, header strip, and border pixel-distinguishable for the scan below.
    sealed class BorderProbeTheme : PatchNotesTheme
    {
        public override Color PanelFill => new(0f, 0f, 1f, 1f);   // opaque blue body
        public override Color HeaderFill => new(0f, 1f, 0f, 1f);  // opaque green header strip
        public override Color MutedText => new(1f, 0f, 0f, 1f);   // border source colour (drawn at 0.5 alpha)
    }

    // Regression: a PatchNotesView panel with a title bar must keep its 1px border all the way around,
    // including the top edge behind the title strip ("Patch Notes" + close). The header fill spans the
    // panel's full width from its top-left, so it used to paint over the top (and upper-side) border
    // pixels when the border was drawn before the header - the user-reported bug (the border appeared to
    // start below the title bar). The fix re-strokes GuiDraw.Border after the header fill, mirroring
    // PopupPanelBorderGpuTests. Here the border is red and the header fill is green: scanning down the
    // panel's centre column, the FIRST non-background pixel must be the red border, and the pixel
    // immediately below it the green header - proving the border sits on top of the header row, not under it.
    public sealed class PatchNotesViewBorderGpuTests
    {
        const int W = 480, H = 480;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void Title_bar_does_not_paint_over_the_top_border()
        {
            var view = new PatchNotesView(PatchNotesDocument.Empty, new BorderProbeTheme());
            var viewport = new Rect(0, 0, W, H);
            Rect panel = view.PanelRect(viewport);
            int cx = (int)(panel.X + panel.Width * 0.5f);

            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                SpriteFont font = ctx.LoadFont(FontPath, 20f, oversample: 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                view.Draw(ctx.Batch, font, white, viewport);
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

            // The border is drawn at 0.5 alpha, so over the opaque green header it blends to a yellow-tinted
            // pixel (elevated R, near-zero B), not pure red. That blended red channel is exactly the proof the
            // border survived the header fill: with the bug, the header fully overwrote it and the top edge
            // read as pure green (R ~ 0).
            int t = (topEdge * W + cx) * 4;
            Assert.True(rgba[t] > 100 && rgba[t + 2] < 50,
                $"top edge should show the red border blended over the header, got rgba=({rgba[t]},{rgba[t + 1]},{rgba[t + 2]})");

            // One row below, the border strip has ended: pure header green, no red left in it.
            int b = ((topEdge + 1) * W + cx) * 4;
            Assert.True(rgba[b + 1] > 180 && rgba[b] < 50,
                $"row below the border should be the pure green header, got rgba=({rgba[b]},{rgba[b + 1]},{rgba[b + 2]})");
        }
    }
}
