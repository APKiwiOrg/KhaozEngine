using KhaozEngine.App;
using KhaozEngine.Gui;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Regression for the TabBar border fix: drawn through a non-snapping DesignViewport (SnapsToDevicePixels ==
    // false, the AdaptiveViewport repro), the strip must render a single crisp 1px divider per interior seam (not
    // the old doubled/soft per-tab ring) and the active tab's accent border must read distinctly over the shared
    // frame. Solid quads at whole-unit edges (GuiDraw.TabStripDrawGeometry) rasterise identically on every backend,
    // so these assertions are pixel-structural, not a committed golden. Skipped unless KE_GPU_TESTS is set.
    public sealed class TabBarBorderGpuTests
    {
        const int W = 480, H = 160;
        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void Shared_grid_draws_a_single_crisp_divider_and_the_active_accent_reads()
        {
            // 3 tabs over a fractional-splitting width (380 / 3), active = tab 0. Edges round to [40,167,293,420]:
            // the Tree|Gates seam at 293 is a plain inactive divider; the Goals|Tree seam at 167 is owned by tab 0's
            // accent border (one column just inside, at 166).
            var bounds = new Rect(40, 40, 380, 48);
            var labels = new[] { LocalizedText.Raw("Goals"), LocalizedText.Raw("Tree"), LocalizedText.Raw("Gates") };
            var (_, edges) = GuiDraw.TabStripDrawGeometry(bounds, 3);
            int inactiveSeam = (int)edges[2];   // 293
            int accentSeam = (int)edges[1];     // 167

            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0.08f, 0.09f, 0.11f, 1f), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 18f, oversample: 1);
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);                 // design == framebuffer, still a design pass => NO device snapping
                Assert.Equal(System.Numerics.Vector2.Zero, ctx.Batch.DeviceScale);   // guard: really a non-snapping pass
                ctx.Batch.Begin(vp);
                new TabBar(labels, font, bounds) { ActiveIndex = 0 }.Draw(ctx.Batch, white);
                ctx.Batch.End();
            });

            const int y = 64;                    // vertical middle of the strip (40..88), clear of the label glyphs
            int Lum(int x) { int i = (y * W + x) * 4; return rgba[i] + rgba[i + 1] + rgba[i + 2]; }
            int fill = Lum(inactiveSeam - 4);    // reference: inactive (Tree) tab fill, away from any seam

            // 1) The inactive divider is a SINGLE thin column: the brightest of the three columns straddling the seam
            //    is clearly above the fill, and two units to either side is back to fill (no ~2px doubled smear).
            int div = System.Math.Max(Lum(inactiveSeam - 1), System.Math.Max(Lum(inactiveSeam), Lum(inactiveSeam + 1)));
            Assert.True(div > fill + 20, $"divider should be brighter than the tab fill (div={div}, fill={fill})");
            Assert.True(Lum(inactiveSeam - 2) <= fill + 8 && Lum(inactiveSeam + 2) <= fill + 8,
                "divider must be a thin single line, not a doubled seam");

            // 2) The top frame is a single crisp row: bright at the frame top, background just above, fill just below.
            int Px(int x, int yy, int c) => rgba[(yy * W + x) * 4 + c];
            int fx = 200;                        // over the middle (Tree) tab
            int frameTop = -1;
            for (int yy = 35; yy < 50; yy++)
                if (Px(fx, yy, 0) + Px(fx, yy, 1) + Px(fx, yy, 2) > 60 + 20) { frameTop = yy; break; }
            Assert.True(frameTop >= 38, $"top frame row not found (got {frameTop})");
            int belowFill = Px(fx, frameTop + 2, 0) + Px(fx, frameTop + 2, 1) + Px(fx, frameTop + 2, 2);
            int frameLum = Px(fx, frameTop, 0) + Px(fx, frameTop, 1) + Px(fx, frameTop, 2);
            Assert.True(frameLum > belowFill + 15, "top frame should be a distinct 1px line above the tab fill");

            // 3) The active tab's accent border reads: a bright, blue-dominant column at the accent seam that is
            //    plainly brighter and bluer than the muted inactive divider.
            int ax = -1;
            for (int x = accentSeam - 2; x <= accentSeam; x++)
                if (Px(x, y, 2) > 150 && Px(x, y, 2) > Px(x, y, 0) + 60) { ax = x; break; }
            Assert.True(ax >= 0, "active accent border column (bright cyan) not found near the accent seam");
            int accentB = Px(ax, y, 2), dividerB = rgba[(y * W + inactiveSeam) * 4 + 2];
            Assert.True(accentB > dividerB + 60, "accent border must read distinctly brighter than the inactive divider");
        }
    }
}
