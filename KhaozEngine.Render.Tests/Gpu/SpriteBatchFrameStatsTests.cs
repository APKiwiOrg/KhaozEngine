using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // The always-on 2D draw counters (SpriteBatch.FrameStats): quads, draw calls, flushes, texture switches, and
    // vertex-upload bytes, plus the per-frame reset. Needs a real device to flush (the counters land in the submit
    // path), so gated behind KE_GPU_TESTS=1.
    public sealed class SpriteBatchFrameStatsTests
    {
        const int W = 32, H = 32;
        const long VertexSizeBytes = 64;   // matches SpriteBatch.V layout
        static readonly byte[] Pixel = { 255, 255, 255, 255 };

        static (IGpuDevice gd, Render2DCore core, IGpuFramebuffer fb) Setup(GpuDeviceContext gpu)
        {
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;
            IGpuTexture target = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            IGpuFramebuffer fb = f.CreateFramebuffer(null, target);
            var core = new Render2DCore(gd, fb.Outputs, ownsDevice: false);
            return (gd, core, fb);
        }

        [GpuFact]
        public void Coalesced_single_texture_run_counts_one_draw_call()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var (gd, core, fb) = Setup(gpu);
            var f = gd.Factory;
            SpriteBatch batch = core.Batch;
            Texture2D a = core.CreateTexture(Pixel, 1, 1);

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);
                batch.Begin();
                for (int i = 0; i < 5; i++) batch.Draw(a, new Vector2(0, 0), new Color(1, 1, 1, 1));
                batch.End();
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            RenderFrameStats s = batch.FrameStats;
            Assert.Equal(5, s.Quads);
            Assert.Equal(0L, s.Triangles);            // 2D quads don't count as mesh triangles (3D-only field)
            Assert.Equal(1, s.DrawCalls);            // consecutive same-texture quads coalesce
            Assert.Equal(1, s.Flushes);              // one End -> one flush
            Assert.Equal(1, s.TextureSwitches);      // the initial bind
            Assert.Equal(5L * 6 * VertexSizeBytes, s.BufferUpdateBytes);

            core.Dispose();
            fb.Dispose();
        }

        [GpuFact]
        public void Interleaved_textures_break_into_separate_draws_and_switches()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var (gd, core, fb) = Setup(gpu);
            var f = gd.Factory;
            SpriteBatch batch = core.Batch;
            Texture2D a = core.CreateTexture(Pixel, 1, 1);
            Texture2D b = core.CreateTexture(Pixel, 1, 1);

            using (IGpuCommandList cl = f.CreateCommandList())
            {
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);
                batch.Begin();
                batch.Draw(a, new Vector2(0, 0), new Color(1, 1, 1, 1));
                batch.Draw(b, new Vector2(0, 0), new Color(1, 1, 1, 1));
                batch.Draw(a, new Vector2(0, 0), new Color(1, 1, 1, 1));   // A-B-A: three runs
                batch.End();
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            RenderFrameStats s = batch.FrameStats;
            Assert.Equal(3, s.Quads);
            Assert.Equal(3, s.DrawCalls);          // A | B | A - not coalesced across the B
            Assert.Equal(3, s.TextureSwitches);    // null->A->B->A
            Assert.Equal(1, s.Flushes);

            core.Dispose();
            fb.Dispose();
        }

        [GpuFact]
        public void NewFrame_resets_the_counters()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var (gd, core, fb) = Setup(gpu);
            var f = gd.Factory;
            SpriteBatch batch = core.Batch;
            Texture2D a = core.CreateTexture(Pixel, 1, 1);

            void Frame(int quads)
            {
                using IGpuCommandList cl = f.CreateCommandList();
                cl.Begin();
                cl.SetFramebuffer(fb);
                cl.ClearColorTarget(0, new Color(0, 0, 0, 1));
                batch.NewFrame(cl, W, H);
                batch.Begin();
                for (int i = 0; i < quads; i++) batch.Draw(a, new Vector2(0, 0), new Color(1, 1, 1, 1));
                batch.End();
                cl.End();
                gd.Submit(cl);
                gd.WaitForIdle();
            }

            Frame(3);
            Assert.Equal(3, batch.FrameStats.Quads);
            Frame(1);
            Assert.Equal(1, batch.FrameStats.Quads);   // reset, not accumulated across frames

            core.Dispose();
            fb.Dispose();
        }
    }
}
