using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // White-box coverage of the per-Begin view-projection UBO slots. Since the quad-corner transform moved from the
    // CPU into the vertex shader, each Begin's view-projection rides in its OWN 256-byte UBO slot, selected at draw
    // time by a dynamic offset. Distinct slots per Begin are what makes it safe on Metal/Veldrid, where overwriting a
    // single shared slot mid-command-list can bind the last-written matrix to every draw. This asserts the slot
    // offset advances per Begin, resets per frame, and the UBO grows (never wraps onto a live slot) when a frame runs
    // more Begins than the initial capacity. Needs a real device (the batch cannot be built without one).
    public sealed class SpriteBatchViewProjSlotTests
    {
        const int W = 32, H = 32;
        static readonly byte[] Pixel = { 255, 255, 255, 255 };

        [GpuFact]
        public void ViewProjOffset_AdvancesPerBegin_ResetsPerFrame_AndGrowsPastInitialCapacity()
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
            uint slot = (uint)batch.ViewProjSlotBytes;
            Assert.True(slot % 256 == 0, "the dynamic-offset slot stride must be 256-byte aligned for Metal/D3D11/Vulkan");

            // Frame 1: each Begin claims the next slot, so the bound offset steps by one slot each time.
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);

                batch.Begin();
                Assert.Equal(0u, batch.CurrentViewProjOffset);
                batch.Draw(tex, new Vector2(0, 0), new Color(1, 1, 1, 1));
                batch.End();

                batch.Begin();
                Assert.Equal(slot, batch.CurrentViewProjOffset);
                batch.End();

                batch.Begin();
                Assert.Equal(2u * slot, batch.CurrentViewProjOffset);
                batch.End();

                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            // Frame 2: NewFrame resets the slot counter, so the first Begin is back at offset 0 (reusing frame 1's
            // slot 0, safe because cl.UpdateBuffer sequences the write behind the prior frame's reads on the queue).
            int initialCapacity = batch.ViewProjSlotCapacity;
            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);

                batch.Begin();
                Assert.Equal(0u, batch.CurrentViewProjOffset);

                // Run well past the initial capacity in ONE frame: the UBO must grow, and each Begin must keep getting
                // a fresh, strictly increasing offset (never wrapping onto an already-used, still-live slot).
                uint prev = batch.CurrentViewProjOffset;
                int begins = initialCapacity + 5;
                for (int i = 1; i < begins; i++)
                {
                    batch.End();
                    batch.Begin();
                    uint off = batch.CurrentViewProjOffset;
                    Assert.Equal(prev + slot, off);
                    prev = off;
                }
                batch.End();

                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            Assert.True(batch.ViewProjSlotCapacity > initialCapacity,
                $"the view-projection UBO should have grown past its initial {initialCapacity} slots for a {initialCapacity + 5}-Begin frame");
        }
    }
}
