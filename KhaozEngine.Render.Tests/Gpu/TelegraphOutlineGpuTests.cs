using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Telegraphs;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // #120 regression tests: a partial-sweep Arc in Outline mode rendered nothing at all, and a Cone
    // outline never closed the far rim. Pixel-presence assertions on a black background, deliberately
    // not golden grids, so no cross-backend CI bake is needed. Skipped unless KE_GPU_TESTS=1.
    public sealed class TelegraphOutlineGpuTests
    {
        const int W = 200, H = 200;

        private static byte[] Draw(Action<TelegraphRenderer2D> draw)
        {
            return Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                var prim = new PrimitiveRenderer(ctx);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                var tg = new TelegraphRenderer2D();
                tg.Begin(ctx.Batch, prim);
                draw(tg);
                tg.End();
                ctx.Batch.End();
            });
        }

        private static int CountLit(byte[] rgba, int xFrom, int xTo, int yFrom, int yTo)
        {
            int lit = 0;
            for (int y = yFrom; y < yTo; y++)
                for (int x = xFrom; x < xTo; x++)
                {
                    int i = (y * W + x) * 4;
                    if (rgba[i] > 40 || rgba[i + 1] > 40 || rgba[i + 2] > 40) lit++;
                }
            return lit;
        }

        private static TelegraphStyle OutlineOnly()
        {
            var style = TelegraphStyle.Generic;
            style.FillMode = FillMode.Outline;
            return style;
        }

        [GpuFact]
        public void Partial_sweep_arc_outline_draws_in_outline_mode()
        {
            byte[] rgba = Draw(tg =>
                tg.Arc(new Vector2(100, 100), 60f, 16f, -MathF.PI / 2f, MathF.PI / 2f, 0.5f, OutlineOnly()));
            int lit = CountLit(rgba, 0, W, 0, H);
            Assert.True(lit > 50, $"a partial-sweep Outline arc must render its outline, drew {lit} lit pixels (zero before the fix)");
        }

        [GpuFact]
        public void Full_sweep_arc_outline_still_draws_both_rings()
        {
            byte[] rgba = Draw(tg =>
                tg.Arc(new Vector2(100, 100), 60f, 16f, 0f, MathF.Tau, 0.5f, OutlineOnly()));
            // Sample on the outer ring (radius 68) at 3 o'clock and the inner ring (radius 52) at 9 o'clock.
            Assert.True(CountLit(rgba, 164, 174, 96, 104) > 0, "outer ring edge missing on a full sweep");
            Assert.True(CountLit(rgba, 44, 54, 96, 104) > 0, "inner ring edge missing on a full sweep");
        }

        [GpuFact]
        public void Cone_outline_closes_the_far_rim()
        {
            byte[] rgba = Draw(tg =>
                tg.Cone(new Vector2(60, 100), new Vector2(1f, 0f), MathF.PI / 4f, 80f, 0.5f, OutlineOnly()));
            // The far rim crosses the +X axis at (60 + 80, 100), midway between the two spokes at +-45
            // degrees. Before the fix only the spokes drew, so this window was empty.
            int lit = CountLit(rgba, 132, 148, 92, 108);
            Assert.True(lit > 0, "the cone outline must stroke the far rim between the spokes");
        }
    }
}
