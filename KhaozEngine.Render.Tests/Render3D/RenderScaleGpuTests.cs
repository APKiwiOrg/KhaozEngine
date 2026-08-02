using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // End-to-end check that PixelPostProcessSettings.RenderScale drives the real RenderResources size through a
    // live Scene3D: MatchViewport resizes the internal target to the (clamped) viewport; FixedInternal ignores
    // the viewport. Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    public sealed class RenderScaleGpuTests
    {
        [GpuFact]
        public void MatchViewport_resizes_internal_target_FixedInternal_does_not()
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

            // FixedInternal (default): a large viewport must NOT change the fixed 1600x900 target.
            Render(gd, cl, scene, finalFB, 2000, 1200);
            Assert.Equal(1600, scene.RenderTargetWidth);
            Assert.Equal(900, scene.RenderTargetHeight);

            // MatchViewport below the cap: the internal target tracks the viewport exactly.
            scene.Post.RenderScale = RenderScale.MatchViewport;
            Render(gd, cl, scene, finalFB, 800, 600);
            Assert.Equal(800, scene.RenderTargetWidth);
            Assert.Equal(600, scene.RenderTargetHeight);

            // MatchViewport above the cap: clamp to the cap, aspect preserved (a 16:9 over-cap viewport -> the cap).
            scene.Post.MaxRenderWidth = 1280;
            scene.Post.MaxRenderHeight = 720;
            Render(gd, cl, scene, finalFB, 3840, 2160);
            Assert.Equal(1280, scene.RenderTargetWidth);
            Assert.Equal(720, scene.RenderTargetHeight);
        }

        static void Render(IGpuDevice gd, IGpuCommandList cl, Scene3D scene, IGpuFramebuffer target, int w, int h)
        {
            scene.Begin();
            scene.PrepareFrame();
            cl.Begin();
            scene.RenderInternal(cl, w, h, target);
            cl.End();
            gd.Submit(cl);
            gd.WaitForIdle();
        }
    }
}
