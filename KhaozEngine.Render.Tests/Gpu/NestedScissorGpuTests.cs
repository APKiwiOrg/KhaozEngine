using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Nested clips (#106). SetScissor used to compute its region from the passed rect alone and ClearScissor used
    // to reset straight to the full framebuffer, so the moment one clipping widget was drawn inside another (a
    // ScrollablePanel or PropertyGrid as a PopupPanel's body) the inner one widened the outer region, and its
    // ClearScissor dropped the outer clip entirely for the rest of that widget's draw.
    public sealed class NestedScissorGpuTests
    {
        const int W = 200, H = 800;

        [GpuFact]
        public void An_inner_clip_intersects_the_outer_one_and_clearing_it_restores_the_outer()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);

                ctx.Batch.SetScissor(new Rect(0, 200, W, 200));          // outer: rows 200..399
                ctx.Batch.SetScissor(new Rect(0, 300, W, 400));          // inner: rows 300..699, overlapping to 399
                // Full-height yellow. Bounded by the OVERLAP, it may only reach rows 300..399.
                ctx.Batch.Draw(white, new Vector4(0, 0, W, H), new Color(1f, 1f, 0.2f, 1f));
                ctx.Batch.ClearScissor();                                 // pops the inner clip, outer stays

                // Rows 0..299 in magenta. Bounded by the restored OUTER clip, it may only reach rows 200..299,
                // and it cannot touch the yellow band below it.
                ctx.Batch.Draw(white, new Vector4(0, 0, W, 300), new Color(1f, 0.2f, 1f, 1f));
                ctx.Batch.ClearScissor();
                ctx.Batch.End();
            });

            Assert.Equal((300, 399), RowSpan(rgba, IsYellow));
            Assert.Equal((200, 299), RowSpan(rgba, IsMagenta));
        }

        [GpuFact]
        public void A_single_clip_still_clips_exactly_as_it_did_before_nesting()
        {
            // The one-level case is the degenerate stack, and it is what every widget in the tree uses today.
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Color(0, 0, 0, 1), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                ctx.Batch.SetScissor(new Rect(0, 100, W, 250));
                ctx.Batch.Draw(white, new Vector4(0, 0, W, H), new Color(1f, 1f, 0.2f, 1f));
                ctx.Batch.ClearScissor();
                // Unclipped again: a stripe well outside the band above must draw in full.
                ctx.Batch.Draw(white, new Vector4(0, 600, W, 40), new Color(1f, 0.2f, 1f, 1f));
                ctx.Batch.End();
            });

            Assert.Equal((100, 349), RowSpan(rgba, IsYellow));
            Assert.Equal((600, 639), RowSpan(rgba, IsMagenta));
        }

        // First and last row (inclusive) down the middle column whose pixel matches, or (-1, -1) for none.
        static (int First, int Last) RowSpan(byte[] rgba, System.Func<byte, byte, byte, bool> match)
        {
            int first = -1, last = -1;
            for (int y = 0; y < H; y++)
            {
                int i = (y * W + W / 2) * 4;
                if (!match(rgba[i], rgba[i + 1], rgba[i + 2])) continue;
                if (first < 0) first = y;
                last = y;
            }
            return (first, last);
        }

        static bool IsYellow(byte r, byte g, byte b) => r > 200 && g > 200 && b < 128;
        static bool IsMagenta(byte r, byte g, byte b) => r > 200 && g < 128 && b > 200;
    }
}
