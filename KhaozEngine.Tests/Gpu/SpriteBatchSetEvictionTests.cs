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
    }
}
