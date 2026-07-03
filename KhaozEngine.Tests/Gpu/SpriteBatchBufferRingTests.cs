using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The SpriteBatch reuses a growable vertex buffer per flush. It used to reuse the SAME buffer every frame, so a
    // frame's CPU write could race the GPU still reading it for an earlier, still-in-flight present (the loop
    // submits+presents with no WaitForIdle) - a 1-frame tear that showed only when the contents changed frame to
    // frame (a moving/resizing widget). The buffers are now RING-BUFFERED: each NewFrame rotates to the next of
    // VertexBufferRingDepth slots, so a slot is not rewritten until that many frames later, by which point its GPU
    // reads have retired. This asserts the rotation invariant. Needs a Metal device, so gated behind KE_GPU_TESTS=1.
    public sealed class SpriteBatchBufferRingTests
    {
        const int W = 32, H = 32;
        static readonly byte[] Pixel = { 255, 255, 255, 255 };   // 1x1 RGBA8

        [GpuFact]
        public void PerFlushVertexBuffer_RotatesAcrossFrames_AndRepeatsEveryRingDepth()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            using var core = new Render2DCore(gd, fb.Outputs, ownsDevice: false);
            SpriteBatch batch = core.Batch;
            Texture2D tex = core.CreateTexture(Pixel, 1, 1);

            int depth = batch.VertexBufferRingDepth;
            Assert.True(depth >= 2, "ring must be at least double-buffered to cover a frame in flight");

            // One small, fixed-size quad per frame: the flush-0 buffer never needs to grow, so a reused ring slot
            // returns the SAME buffer instance - which is exactly what lets us observe the rotation.
            IGpuBuffer BufferForOneFrame()
            {
                using IGpuCommandList cl = f.CreateCommandList();
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);
                batch.Begin();
                batch.Draw(tex, new Vector2(0, 0), new Color(1, 1, 1, 1));
                batch.End();
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
                IGpuBuffer? vb = batch.CurrentFlushBuffer(0);
                Assert.NotNull(vb);
                return vb!;
            }

            // Capture the flush-0 buffer for depth+1 frames.
            var seen = new List<IGpuBuffer>();
            for (int i = 0; i <= depth; i++) seen.Add(BufferForOneFrame());

            // The first `depth` frames each land on a distinct slot -> distinct buffer instances.
            for (int a = 0; a < depth; a++)
                for (int b = a + 1; b < depth; b++)
                    Assert.False(ReferenceEquals(seen[a], seen[b]),
                        $"frames {a} and {b} shared a vertex buffer; the ring did not rotate");

            // Frame `depth` wraps back onto frame 0's slot and reuses its buffer (proves it is a bounded ring, not
            // an ever-growing set of buffers).
            Assert.Same(seen[0], seen[depth]);
        }
    }
}
