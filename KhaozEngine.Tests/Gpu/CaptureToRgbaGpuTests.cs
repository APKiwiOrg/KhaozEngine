using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Render2DSurface.CaptureToRgba renders an offscreen pass on the live device and returns the pixels on the
    // CPU (the on-device equivalent of Render2DSnapshot.Capture). Exercises the shared mechanism
    // (Render2DCore.RenderToRgba) on a headless device. Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    public sealed class CaptureToRgbaGpuTests
    {
        const int W = 24, H = 16;

        [GpuFact]
        public void RenderToRgba_returns_the_drawn_pixels()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            IGpuTexture whiteHandle = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(whiteHandle, new byte[] { 255, 255, 255, 255 }, 0, 0, 1, 1);
            var white = new Texture2D(whiteHandle, 1, 1);

            byte[] rgba = Render2DCore.RenderToRgba(gd, W, H, new Color(1, 0, 0, 1), batch =>
            {
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                batch.Begin(vp);
                batch.Draw(white, new Vector4(0, 0, W / 2f, H), new Color(0, 1, 0, 1)); // green left half
                batch.End();
            });

            Assert.Equal(W * H * 4, rgba.Length);
            int left = ((H / 2) * W + (W / 4)) * 4;
            int right = ((H / 2) * W + (3 * W / 4)) * 4;
            Assert.True(rgba[left] < 64 && rgba[left + 1] > 200 && rgba[left + 2] < 64, "left half should be green");
            Assert.True(rgba[right] > 200 && rgba[right + 1] < 64 && rgba[right + 2] < 64, "right half should be the red clear");

            white.Dispose();
        }
    }
}
