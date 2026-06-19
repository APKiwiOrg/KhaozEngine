using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // SpriteBatch.Begin(..., SamplerMode.Point) selects nearest-neighbour filtering (crisp pixel art) instead of
    // the default bilinear. Upscales a 2x2 checker and asserts the Point pass has hard edges (few mid-grey
    // pixels) while the Linear pass blends (many mid-grey pixels along the cell boundaries).
    // Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    public sealed class SamplerModeGpuTests
    {
        const int W = 200, H = 100;

        [GpuFact]
        public void Point_sampling_keeps_hard_edges_where_linear_blends()
        {
            int linearMid = 0, pointMid = 0;
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                // 2x2 checker: TL/BR white, TR/BL black.
                byte[] checker =
                {
                    255, 255, 255, 255,   0,   0,   0, 255,
                      0,   0,   0, 255, 255, 255, 255, 255,
                };
                Texture2D tex = ctx.CreateTexture(checker, 2, 2);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);

                // Left half: Linear (blended edges). Right half: Point (hard edges).
                ctx.Batch.Begin(vp, SamplerMode.Linear);
                ctx.Batch.Draw(tex, new Vector4(10, 10, 80, 80), new Color(1, 1, 1, 1));
                ctx.Batch.End();

                ctx.Batch.Begin(vp, SamplerMode.Point);
                ctx.Batch.Draw(tex, new Vector4(110, 10, 80, 80), new Color(1, 1, 1, 1));
                ctx.Batch.End();
            });

            // Count mid-grey pixels (neither near-black nor near-white) in each quad.
            linearMid = CountMidGrey(rgba, 10, 90);
            pointMid = CountMidGrey(rgba, 110, 190);

            Assert.True(linearMid > 50, $"linear pass should blend along edges (mid-grey={linearMid})");
            // Point sampling produces hard cell edges, so far fewer blended pixels than linear.
            Assert.True(pointMid * 4 < linearMid, $"point pass should be crisp (point mid-grey={pointMid}, linear={linearMid})");
        }

        private static int CountMidGrey(byte[] rgba, int xFrom, int xTo)
        {
            int count = 0;
            for (int y = 10; y < 90; y++)
            {
                for (int x = xFrom; x < xTo; x++)
                {
                    int i = (y * W + x) * 4;
                    byte v = rgba[i];
                    if (v > 60 && v < 195) count++;
                }
            }
            return count;
        }
    }
}
