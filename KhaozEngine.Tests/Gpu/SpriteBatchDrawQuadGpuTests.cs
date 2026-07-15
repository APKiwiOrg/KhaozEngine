using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // DrawQuad exposes the batch's two-triangle emit path for an arbitrary four-corner quad. This asserts it feeds
    // the batch (FrameStats counts one quad per call) and that a DEGENERATE quad (two coincident corners == a
    // triangle, how a pie / fan slice is built) emits and submits without throwing. Needs a real device (the batch
    // cannot be constructed without one). Skipped unless KE_GPU_TESTS is set.
    public sealed class SpriteBatchDrawQuadGpuTests
    {
        const int W = 32, H = 32;
        static readonly byte[] Pixel = { 255, 255, 255, 255 };

        [GpuFact]
        public void DrawQuad_emits_one_quad_per_call_and_allows_a_degenerate_quad()
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
            var uv = new Vector4(0f, 0f, 1f, 1f);
            var col = new Color(1f, 1f, 1f, 1f);

            using IGpuCommandList cl = f.CreateCommandList();
            cl.Begin();
            cl.SetFramebuffer(fb);
            cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
            batch.NewFrame(cl, W, H);
            batch.Begin();

            // A normal (rectangular) quad.
            batch.DrawQuad(tex, new Vector2(4, 4), new Vector2(28, 4), new Vector2(28, 28), new Vector2(4, 28), uv, col);
            // A degenerate quad: the last two corners coincide, so it renders as a single triangle.
            batch.DrawQuad(tex, new Vector2(4, 4), new Vector2(28, 4), new Vector2(16, 28), new Vector2(16, 28), uv, col);

            batch.End();
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            Assert.Equal(2, batch.FrameStats.Quads);   // one quad counted per DrawQuad, degenerate included
        }
    }
}
