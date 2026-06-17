using System.Numerics;
using KhaozEngine.Render2D;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Regression: a draw BEFORE SetScissor forces an intermediate Flush. The vertices of that flush must not be
    // overwritten by a later flush in the same frame (a same-buffer GPU hazard), and the scissor must clip the
    // later draws to the asked region. Before the per-flush-buffer fix, the pre-flush draw rendered the LATER
    // flush's (full-height, unclipped) geometry -> panels using SetScissor showed garbled / misplaced content.
    public sealed class ScissorClipGpuTests
    {
        const int W = 440, H = 956;

        [GpuFact]
        public void Scissor_after_a_prior_draw_still_clips_to_the_asked_band()
        {
            byte[] rgba = Render2DSnapshot.Capture(W, H, new Vector4(0, 0, 0, 1), ctx =>
            {
                Texture2D white = ctx.CreateTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                ctx.Batch.Begin(vp);
                // Prior draw (like a panel's scrim/bg): a dark band over the TOP half. SetScissor flushes it.
                ctx.Batch.Draw(white, new Vector4(0, 0, W, 520), new Vector4(0.15f, 0.15f, 0.15f, 1f));
                // Clip the following column to a middle band, like a scrollable panel's content area.
                ctx.Batch.SetScissor(new Rect(0, 532, W, 372));
                ctx.Batch.Draw(white, new Vector4(0, 0, W, H), new Vector4(1, 1, 0.2f, 1f)); // bright yellow, full height
                ctx.Batch.ClearScissor();
                ctx.Batch.End();
            });

            // The bright-yellow column must appear ONLY in the clipped band [532, 904); the prior dark band must
            // NOT have been corrupted into a full-height yellow fill.
            int firstYellow = -1, lastYellow = -1;
            for (int y = 0; y < H; y++)
            {
                int i = (y * W + W / 2) * 4;
                bool yellow = rgba[i] > 200 && rgba[i + 1] > 200 && rgba[i + 2] < 128;
                if (yellow) { if (firstYellow < 0) firstYellow = y; lastYellow = y; }
            }
            Assert.Equal((532, 903), (firstYellow, lastYellow));
        }
    }
}
