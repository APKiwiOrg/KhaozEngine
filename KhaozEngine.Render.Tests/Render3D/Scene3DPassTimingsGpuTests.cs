using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // End-to-end check that Scene3D.EnableTiming actually populates PassTimingsMs through a live render, and that
    // leaving it off (the default) leaves every field at 0. Needs a real device (Metal/D3D11/Vulkan), so it is
    // skipped unless KE_GPU_TESTS=1. Byte-stability of rendered pixels with timing on/off is covered by the
    // existing golden suite (timing brackets a Stopwatch only; they never touch what gets drawn).
    public sealed class Scene3DPassTimingsGpuTests
    {
        [GpuFact]
        public void Timing_off_by_default_leaves_every_pass_at_zero()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            using IGpuCommandList cl = f.CreateCommandList();

            Assert.False(scene.EnableTiming);
            Render(gd, cl, scene, finalFB, W, H);

            Scene3DPassTimingsMs t = scene.PassTimingsMs;
            Assert.Equal(0f, t.ShadowDepthMs);
            Assert.Equal(0f, t.ModelMs);
            Assert.Equal(0f, t.TransparentsMs);
            Assert.Equal(0f, t.PostMs);
        }

        [GpuFact]
        public void Enabling_timing_populates_model_transparents_and_post()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs) { EnableTiming = true };
            using IGpuCommandList cl = f.CreateCommandList();

            // Shadows stay Off (default), so ShadowDepthMs legitimately stays 0 - the pass never runs.
            Render(gd, cl, scene, finalFB, W, H);

            Scene3DPassTimingsMs t = scene.PassTimingsMs;
            Assert.Equal(0f, t.ShadowDepthMs);
            Assert.True(t.ModelMs >= 0f);
            Assert.True(t.TransparentsMs >= 0f);
            Assert.True(t.PostMs > 0f, $"expected a nonzero post-chain encode time, got {t.PostMs}");
        }

        // Issue #374's other exposure: LastClipmapRebuilds was internal-only on WaterRenderer before this, reachable
        // from KhaozEngine's own tests but not from a consuming game. A fresh clipmap grid always rebuilds on the
        // first Draw that uses it (nothing cached yet), so that is enough to prove the public passthrough is wired
        // to the real counter rather than always reading 0.
        [GpuFact]
        public void LastWaterStats_ClipmapRebuilds_ReflectsAFreshGridsFirstRebuild()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs);
            scene.Post.Water.GridMode = WaterGridMode.Clipmap;
            using IGpuCommandList cl = f.CreateCommandList();

            scene.Begin();
            scene.DrawWater(new WaterPlane(centerX: 0f, surfaceY: 0f, centerZ: 0f, halfExtentX: 400f));
            cl.Begin();
            scene.RenderInternal(cl, W, H, finalFB);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();

            Assert.True(scene.LastWaterStats.ClipmapRebuilds > 0,
                "expected the first-ever clipmap Draw to rebuild at least one grid");
        }

        [GpuFact]
        public void Enabling_timing_with_shadow_map_populates_shadow_depth()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = gpu.GpuDevice;
            var f = gd.Factory;

            const int W = 64, H = 48;
            using IGpuTexture finalTex = f.CreateTexture(GpuTextureDescription.Texture2D(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer finalFB = f.CreateFramebuffer(null, finalTex);

            using var scene = new Scene3D(gd, finalFB.Outputs) { EnableTiming = true };
            scene.Post.Quality.Shadows.Mode = ShadowMode.ShadowMap;
            using IGpuCommandList cl = f.CreateCommandList();

            Render(gd, cl, scene, finalFB, W, H);

            Assert.True(scene.PassTimingsMs.ShadowDepthMs > 0f);
        }

        static void Render(IGpuDevice gd, IGpuCommandList cl, Scene3D scene, IGpuFramebuffer target, int w, int h)
        {
            scene.Begin();
            cl.Begin();
            scene.RenderInternal(cl, w, h, target);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();
        }
    }
}
