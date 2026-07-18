using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render2D;
using KhaozEngine.Render2D.Internal;
using KhaozEngine.Windowing;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // SpriteBatch.Begin(viewport, Matrix4x4 transform) applies a model transform to every quad in the pass, so a
    // composed card rotates/translates as one. Draws a small rect at the origin under a pure translation and
    // asserts the lit pixels landed where the transform put them (not at the origin). Skipped unless
    // KE_GPU_TESTS=1 (needs a Metal device).
    public sealed class SpriteBatchTransformGpuTests
    {
        const int W = 40, H = 40;

        [GpuFact]
        public void Begin_with_a_translation_moves_the_whole_pass()
        {
            using GpuDeviceContext ctx = GpuDeviceContext.CreateHeadless();
            IGpuDevice gd = ctx.GpuDevice;

            IGpuTexture whiteHandle = gd.Factory.CreateTexture(GpuTextureDescription.Texture2D(
                1, 1, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled));
            gd.UpdateTexture(whiteHandle, new byte[] { 255, 255, 255, 255 }, 0, 0, 1, 1);
            var white = new Texture2D(whiteHandle, 1, 1);

            byte[] rgba = Render2DCore.RenderToRgba(gd, W, H, new Color(0, 0, 0, 1), batch =>
            {
                var vp = new DesignViewport(W, H, ScaleMode.Fit);
                vp.Update(W, H);
                // Same rect at the design origin, but translated +20,+20 by the model transform.
                batch.Begin(vp, Matrix4x4.CreateTranslation(20, 20, 0));
                batch.Draw(white, new Vector4(0, 0, 10, 10), new Color(1, 1, 1, 1));
                batch.End();
            });

            // The rect now sits at (20..30, 20..30); the design origin (5,5) is back to the black clear.
            Assert.True(Lit(rgba, 25, 25), "translated rect should light the moved region");
            Assert.False(Lit(rgba, 5, 5), "origin should be empty after the translation");

            white.Dispose();
        }

        static bool Lit(byte[] rgba, int x, int y)
        {
            int i = (y * W + x) * 4;
            return rgba[i] > 128 && rgba[i + 1] > 128 && rgba[i + 2] > 128;
        }
    }
}
