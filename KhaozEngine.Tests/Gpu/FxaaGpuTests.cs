using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Phase 2 of the AA-options work: the FXAA post pass. Renders a high-contrast DIAGONAL-edge pattern at a 1:1
    // internal target (no supersample) with AntiAliasing.Off vs AntiAliasing.Fxaa and asserts FXAA anti-aliases the
    // staircased edges - it replaces hard fully-lit/fully-dark edge pixels with a band of INTERMEDIATE (partially
    // covered) values, so the count of mid-luma pixels rises - WITHOUT destroying the image (mean luma preserved).
    // (Total variation is the wrong metric: it is invariant to edge softness - a hard step and a smooth ramp traverse
    // the same total contrast - so it cannot see FXAA. Mid-pixel count directly measures the AA gradient FXAA adds.)
    // A relative same-session assertion, so no committed golden / per-backend bake. Skipped unless KE_GPU_TESTS=1.
    public sealed class FxaaGpuTests
    {
        const int W = 240, H = 240;
        const int Bars = 15;   // FXAA works on high-contrast DIAGONAL edges (not vertical/sub-pixel flicker), so the
                               // bars are rotated ~30 deg to give long staircased edges for it to smooth.

        static double Luma(byte[] p, int i) => 0.299 * p[i] + 0.587 * p[i + 1] + 0.114 * p[i + 2];

        // Pixels whose luma sits clearly BETWEEN the dark background and the bright bars (partial-coverage edge
        // pixels). A 1-sample-per-pixel render has few (edges are hard); FXAA blends edges, adding many more.
        static int MidCount(byte[] rgba)
        {
            int n = 0;
            for (int p = 0; p < rgba.Length / 4; p++)
            {
                double l = Luma(rgba, p * 4);
                if (l > 40 && l < 190) n++;
            }
            return n;
        }

        static double MeanLuma(byte[] rgba)
        {
            double s = 0; int n = rgba.Length / 4;
            for (int p = 0; p < n; p++) s += Luma(rgba, p * 4);
            return s / n;
        }

        static byte[] RenderFence(AntiAliasing aa)
        {
            MeshHandle bar = default;
            return Render3DSnapshot.Capture(W, H,
                setup: scene =>
                {
                    scene.Post.UseSmoothPreset();
                    scene.Post.RenderScale = RenderScale.MatchViewport;   // 1:1 internal (no supersample) so FXAA is the only AA
                    scene.Post.Quality.AntiAliasing = aa;
                    scene.Post.TransparentBackground = false;
                    scene.Camera.Azimuth = 0f; scene.Camera.Elevation = 0f;
                    scene.Camera.AspectRatio = 1f; scene.Camera.OrthoSize = 4.4f; scene.Camera.Target = Vector3.Zero;
                    bar = scene.LoadMesh(MeshPrimitives.Box(1f));
                },
                drawFrame: scene =>
                {
                    for (int i = 0; i < Bars; i++)
                    {
                        float x = -2.6f + 5.2f * i / (Bars - 1);
                        // Thin tall bar rotated ~30 deg about Z: its two long edges become diagonal -> heavy staircase
                        // at a 1:1 target, which is exactly what FXAA smooths.
                        Matrix4x4 world = Matrix4x4.CreateScale(0.08f, 6f, 0.08f)
                                        * Matrix4x4.CreateRotationZ(0.52f)
                                        * Matrix4x4.CreateTranslation(x, 0f, 0f);
                        scene.Draw(bar, world, new Color(0.92f, 0.92f, 0.95f, 1f));
                    }
                },
                frames: 2);
        }

        [GpuFact]
        public void Fxaa_softens_edges_without_destroying_the_image()
        {
            byte[] off = RenderFence(AntiAliasing.Off);
            byte[] on = RenderFence(AntiAliasing.Fxaa);
            int midOff = MidCount(off), midOn = MidCount(on);
            double mOff = MeanLuma(off), mOn = MeanLuma(on);
            string ctx = $"(midOff={midOff} midOn={midOn} ratio={(double)midOn / midOff:0.00}; meanOff={mOff:0.0} meanOn={mOn:0.0})";

            // FXAA blends the staircased diagonal edges, so it adds intermediate (partially covered) pixels. The
            // effect is structural (FXAA only ever softens, never sharpens), so it holds on every backend; measured
            // ~1.17x on Metal, the 1.05x gate is a wide cross-backend margin.
            Assert.True(midOn > midOff * 1.05, $"FXAA should add intermediate (anti-aliased) edge pixels {ctx}");
            // ...but it must be a local edge blur, not a wreck: overall brightness is preserved.
            Assert.True(Math.Abs(mOn - mOff) < Math.Max(2.0, mOff * 0.1),
                $"FXAA must preserve overall brightness (not blank/darken the image) {ctx}");
        }
    }
}
