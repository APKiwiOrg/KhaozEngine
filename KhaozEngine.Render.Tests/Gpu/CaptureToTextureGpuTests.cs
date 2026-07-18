using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Render2DSurface.CaptureToTexture renders an offscreen 2D pass into a sampleable texture on the SAME live
    // device (so the callback can draw textures already created on it) instead of the throwaway headless device
    // Render2DSnapshot owns. The surface itself needs a window, so this exercises the shared offscreen mechanism
    // (Render2DCore.RenderToTexture) on a headless device + reads the result back to assert the captured content.
    // Skipped unless KE_GPU_TESTS=1 (needs a Metal device).
    public sealed class CaptureToTextureGpuTests
    {
        const int W = 32, H = 24;

        [GpuFact]
        public void RenderToTexture_captures_a_drawn_quad_onto_a_sampleable_texture()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            // A 1x1 white texture on the same device the capture uses, proving the callback can draw assets that
            // already live on the live device (the whole point versus the throwaway-device snapshot path).
            IGpuTexture whiteHandle = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(whiteHandle, new byte[] { 255, 255, 255, 255 }, 0, 0, 1, 1);
            var white = new Texture2D(whiteHandle, 1, 1);

            // Clear red; draw a green quad over the left half only, so the captured texture has both colours.
            Texture2D captured = Render2DCore.RenderToTexture(gd, W, H, new Color(1, 0, 0, 1), batch =>
            {
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                batch.Begin(vp);
                batch.Draw(white, new Vector4(0, 0, W / 2f, H), new Color(0, 1, 0, 1));
                batch.End();
            });

            Assert.Equal(W, captured.Width);
            Assert.Equal(H, captured.Height);

            byte[] rgba = GpuReadback.ToRgba(gd, captured.Handle, W, H);

            // Left half = the green quad; right half = the untouched red clear.
            int left = ((H / 2) * W + (W / 4)) * 4;
            int right = ((H / 2) * W + (3 * W / 4)) * 4;
            Assert.True(rgba[left] < 64 && rgba[left + 1] > 200 && rgba[left + 2] < 64, "left half should be green");
            Assert.True(rgba[right] > 200 && rgba[right + 1] < 64 && rgba[right + 2] < 64, "right half should be the red clear");

            captured.Dispose();
            white.Dispose();
        }
    }
}
