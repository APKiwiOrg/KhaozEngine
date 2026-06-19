using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // SpriteBatch.DrawString(..., scale) scales the whole glyph layout uniformly about the top-left, so a caller
    // can measure at `scale` for positioning and draw at the same `scale`. Renders the same glyph at scale 1 and
    // scale 2 into one image and asserts the scaled text's lit-pixel extent is ~2x in both width and height.
    // Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    public sealed class DrawStringScaleGpuTests
    {
        const int W = 480, H = 320;

        static readonly string FontPath = System.IO.Path.Combine(
            System.AppContext.BaseDirectory, "Assets", "Roboto-Regular.ttf");

        [GpuFact]
        public void DrawString_scale_grows_the_glyph_extent_proportionally()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                SpriteFont font = ctx.LoadFont(FontPath, 28f, oversample: 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                // Same glyph, top region scale 1, bottom region scale 2, both left-anchored at x = 40.
                ctx.Batch.DrawString(font, "8", new Vector2(40, 40), new Color(1, 1, 1, 1), 1f);
                ctx.Batch.DrawString(font, "8", new Vector2(40, 170), new Color(1, 1, 1, 1), 2f);
                ctx.Batch.End();
            });

            (int w1, int h1) = LitExtent(rgba, yFrom: 0, yTo: 150);
            (int w2, int h2) = LitExtent(rgba, yFrom: 150, yTo: H);

            Assert.True(w1 > 2 && h1 > 2, $"scale-1 glyph should be visible (w={w1} h={h1})");
            // ~2x within a generous tolerance (rasterisation / hinting differ slightly between sizes).
            Assert.InRange((double)w2 / w1, 1.7, 2.3);
            Assert.InRange((double)h2 / h1, 1.7, 2.3);
        }

        // Width/height of the lit (non-black) pixels within the row band [yFrom, yTo).
        private static (int Width, int Height) LitExtent(byte[] rgba, int yFrom, int yTo)
        {
            int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;
            for (int y = yFrom; y < yTo; y++)
            {
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i] > 80 || rgba[i + 1] > 80 || rgba[i + 2] > 80)
                    {
                        if (x < minX) minX = x;
                        if (x > maxX) maxX = x;
                        if (y < minY) minY = y;
                        if (y > maxY) maxY = y;
                    }
                }
            }

            if (maxX < minX) return (0, 0);
            return (maxX - minX + 1, maxY - minY + 1);
        }
    }
}
