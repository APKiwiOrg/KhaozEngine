using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Phase 1 of the AA-options work: proves the MatchViewport supersample downscale is a CORRECT mip-filtered box at
    // ANY factor, not just 2. Renders a dense picket fence of thin box geometry (screen-space GEOMETRY aliasing - the
    // thing the internal-target -> window blit downsamples; texture minification is separately handled by the model
    // pass's own mip chain) at Supersample 1 / 2 / 3 into the same framebuffer, and asserts the high-frequency energy
    // (total variation of luma) drops MONOTONICALLY as the factor rises. Before the mip fix, a single bilinear tap only
    // box-filtered correctly at exactly 2:1, so 3x under-sampled and did NOT reduce aliasing further; now it does.
    // A relative same-session assertion, so it needs no committed golden and no per-backend bake. Skipped unless
    // KE_GPU_TESTS=1.
    public sealed class SupersampleDownsampleGpuTests
    {
        const int W = 240, H = 240;
        const int Bars = 81;   // dense enough that a 1x render sub-samples the fence (~1px bars) and aliases hard

        // Total variation of the luma channel: sum of |adjacent-pixel luma diff| across the image. An aliased render
        // (sub-pixel geometry sampled once per pixel) flickers hard between lit/dark neighbours -> high TV; a properly
        // supersampled + downsampled render softens those edges -> lower TV. Monotone in the amount of anti-aliasing.
        static double TotalVariation(byte[] rgba, int w, int h)
        {
            static double Luma(byte[] p, int i) => 0.299 * p[i] + 0.587 * p[i + 1] + 0.114 * p[i + 2];
            double tv = 0;
            for (int y = 0; y < h; y++)
                for (int x = 0; x < w; x++)
                {
                    int i = (y * w + x) * 4;
                    if (x + 1 < w) tv += Math.Abs(Luma(rgba, i) - Luma(rgba, i + 4));
                    if (y + 1 < h) tv += Math.Abs(Luma(rgba, i) - Luma(rgba, i + w * 4));
                }
            return tv;
        }

        static double RenderFenceTV(float supersample)
        {
            MeshHandle bar = default;
            byte[] rgba = Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.UseSmoothPreset();                        // no starfield/outline/quantize: the only HF is bar edges
                    scene.Post.RenderScale = RenderScale.MatchViewport;
                    scene.Post.Supersample = supersample;
                    scene.Post.TransparentBackground = false;
                    // Head-on orthographic framing of the fence plane (bars in X, tall in Y, at z=0). Azimuth/elevation
                    // 0 looks straight down -Z; a square viewport with OrthoSize 4.4 shows x,y in [-2.2, 2.2].
                    scene.Camera.Azimuth = 0f;
                    scene.Camera.Elevation = 0f;
                    scene.Camera.AspectRatio = 1f;
                    scene.Camera.OrthoSize = 4.4f;
                    scene.Camera.Target = Vector3.Zero;
                    bar = scene.LoadMesh(MeshPrimitives.Box(1f));
                },
                drawFrame: scene =>
                {
                    for (int i = 0; i < Bars; i++)
                    {
                        float x = -2f + 4f * i / (Bars - 1);
                        Matrix4x4 world = Matrix4x4.CreateScale(0.02f, 4f, 0.02f) * Matrix4x4.CreateTranslation(x, 0f, 0f);
                        scene.Draw(bar, world, new Color(0.92f, 0.92f, 0.95f, 1f));
                    }
                },
                frames: 2);
            return TotalVariation(rgba, W, H);
        }

        [GpuFact]
        public void Supersample_reduces_high_frequency_energy_monotonically_at_any_factor()
        {
            double tv1 = RenderFenceTV(1f);
            double tv2 = RenderFenceTV(2f);
            double tv3 = RenderFenceTV(3f);
            string ctx = $"(tv1={tv1:0} tv2={tv2:0} tv3={tv3:0}; 2/1={tv2 / tv1:0.00} 3/2={tv3 / tv2:0.00})";

            // 2x supersampling reduces the fence's aliasing vs a 1:1 (Supersample 1) render.
            Assert.True(tv2 < tv1, $"2x should anti-alias vs 1x {ctx}");
            // The point of Phase 1: 3x must anti-alias CLEARLY further than 2x. A single bilinear tap only box-filters
            // correctly at exactly 2:1 - a 3:1 downscale under-samples and plateaus near 2x. The mip-filtered blit does
            // not, so 3x drops well below 2x here (measured ~0.58x on Metal; the 0.8x gate is a wide cross-backend
            // margin that an under-sampling regression would fail).
            Assert.True(tv3 < tv2 * 0.8, $"3x should anti-alias clearly further than 2x (the mip fix) {ctx}");
        }
    }
}
