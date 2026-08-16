using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // SpriteBatch caches one (texture,sampler) resource set per distinct texture drawn. A long-lived batch that
    // streams many textures used to leak one set per texture forever (freed only at Dispose). The NewFrame sweep
    // now evicts the set for any texture not drawn within SetEvictAfterFrames, bounding the cache to the working
    // set. Needs a Metal device, so gated behind KE_GPU_TESTS=1.
    public sealed class SpriteBatchSetEvictionTests
    {
        const int W = 32, H = 32;
        static readonly byte[] Pixel = { 255, 255, 255, 255 };   // 1x1 RGBA8

        [GpuFact]
        public void UnusedTextureSets_AreEvicted_AndRebuiltOnReturn()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            using var core = new Render2DCore(gd, fb.Outputs, ownsDevice: false);
            SpriteBatch batch = core.Batch;
            batch.SetEvictAfterFrames = 2;   // evict a texture's set after 2 frames unused (default 600)

            Texture2D a = core.CreateTexture(Pixel, 1, 1);
            Texture2D b = core.CreateTexture(Pixel, 1, 1);

            void Frame(params Texture2D[] texs)
            {
                using IGpuCommandList cl = f.CreateCommandList();
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);
                batch.Begin();
                foreach (Texture2D t in texs) batch.Draw(t, new Vector2(0, 0), new Color(1, 1, 1, 1));
                batch.End();
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            Frame(a, b);
            Assert.Equal(2, batch.CachedSetCount);   // one set per distinct texture under the default sampler

            // Draw only b for several frames; a goes unused past the 2-frame threshold and its set is evicted.
            Frame(b);
            Frame(b);
            Frame(b);
            Assert.Equal(1, batch.CachedSetCount);   // a's set freed, b's remains - cache bounded, not growing

            // Drawing a again rebuilds its set (no crash, cache rebounds) - eviction is transparent.
            Frame(a, b);
            Assert.Equal(2, batch.CachedSetCount);
        }

        /// <summary>
        /// The same scene, on a real device, either side of a full eviction cycle: same pixels out, and not one
        /// device drain to get there (#84). Both halves matter and neither implies the other. A set freed while a
        /// queued draw still named it would land as a black or garbage sprite here, and a set freed behind a
        /// <c>WaitForIdle</c> would render perfectly while stalling the frame thread, which is exactly the bug.
        /// </summary>
        [GpuFact]
        public void A_scene_renders_identically_either_side_of_an_eviction_cycle_without_draining()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice inner = gpu.GpuDevice;
            // The batch draws through the spy so the drain count is the ENGINE's, not this test's own submits.
            var gd = new SpyGpuDevice(inner);
            var f = gd.Factory;
            using IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            using var core = new Render2DCore(gd, fb.Outputs, ownsDevice: false);
            SpriteBatch batch = core.Batch;
            batch.SetEvictAfterFrames = 2;

            // Two distinct textures, drawn to two distinct corners, so a wrong binding is a visible colour swap
            // rather than a silently identical white quad.
            Texture2D a = core.CreateTexture(new byte[] { 220, 40, 40, 255 }, 1, 1);
            Texture2D b = core.CreateTexture(new byte[] { 40, 80, 220, 255 }, 1, 1);

            void Frame(bool withA)
            {
                using IGpuCommandList cl = f.CreateCommandList();
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);
                batch.Begin();
                if (withA) batch.Draw(a, new Vector4(0, 0, 16, 16), new Color(1, 1, 1, 1));
                batch.Draw(b, new Vector4(16, 16, 16, 16), new Color(1, 1, 1, 1));
                batch.End();
                cl.End();
                gd.Submit(cl);
                inner.WaitForIdle();   // the test's own drain, deliberately not through the spy
            }

            Frame(withA: true);
            byte[] before = GpuReadback.ToRgba(inner, target, W, H);

            int drainsBefore = gd.WaitForIdleCalls;
            // Long enough that a's set ages out, is retired, and is really destroyed inside this loop.
            for (int i = 0; i < 40; i++) Frame(withA: false);
            Assert.Equal(1, batch.CachedSetCount);
            Assert.Equal(drainsBefore, gd.WaitForIdleCalls);   // the whole cycle stalled the frame thread never

            Frame(withA: true);   // a returns and rebuilds its set over the freed one
            byte[] after = GpuReadback.ToRgba(inner, target, W, H);

            Assert.Equal(before, after);
        }
    }
}
