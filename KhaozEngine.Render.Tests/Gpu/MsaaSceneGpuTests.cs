using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // Phase 3 of the AA-options work, end-to-end through Scene3D: proves MSAA actually anti-aliases GEOMETRY edges via
    // the real multi-pass path (multisampled MRT -> resolve -> post chain). Renders a diagonal-edge fence with
    // AntiAliasing.Off vs AntiAliasing.Msaa(4) and asserts MSAA replaces hard 1-sample staircase edges with a band of
    // INTERMEDIATE (resolved) pixels - the mid-luma count rises - WITHOUT destroying the image (mean luma preserved,
    // so the multi-pass MRT-resolve did not drop the colour on the way through). A relative same-session assertion, so
    // no committed golden / per-backend bake. Skipped unless KE_GPU_TESTS=1.
    public sealed class MsaaSceneGpuTests
    {
        const int W = 240, H = 240;
        const int Bars = 15;   // rotated ~30 deg so the bars have long diagonal (staircased) edges MSAA can resolve

        static double Luma(byte[] p, int i) => 0.299 * p[i] + 0.587 * p[i + 1] + 0.114 * p[i + 2];

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
                    scene.Post.RenderScale = RenderScale.MatchViewport;   // 1:1 internal, so MSAA is the only AA at play
                    scene.Post.Quality.AntiAliasing = aa;
                    scene.Post.TransparentBackground = false;
                    // Flat white ambient so the bars render fully bright (luma > mid band) and the dark background
                    // stays dark: then mid-luma pixels are ONLY the partial-coverage EDGE pixels MSAA resolves, so the
                    // metric isn't diluted by bar-face pixels.
                    scene.Post.AmbientColor = new Color(1f, 1f, 1f, 1f);
                    scene.Camera.Azimuth = 0f; scene.Camera.Elevation = 0f;
                    scene.Camera.AspectRatio = 1f; scene.Camera.OrthoSize = 4.4f; scene.Camera.Target = Vector3.Zero;
                    bar = scene.LoadMesh(MeshPrimitives.Box(1f));
                },
                drawFrame: scene =>
                {
                    for (int i = 0; i < Bars; i++)
                    {
                        float x = -2.6f + 5.2f * i / (Bars - 1);
                        Matrix4x4 world = Matrix4x4.CreateScale(0.08f, 6f, 0.08f)
                                        * Matrix4x4.CreateRotationZ(0.52f)
                                        * Matrix4x4.CreateTranslation(x, 0f, 0f);
                        scene.Draw(bar, world, new Color(0.92f, 0.92f, 0.95f, 1f));
                    }
                },
                frames: 2);
        }

        [GpuFact]
        public void Msaa_antialiases_geometry_edges_without_destroying_the_image()
        {
            byte[] off = RenderFence(AntiAliasing.Off);
            byte[] on = RenderFence(AntiAliasing.Msaa(4));
            int midOff = MidCount(off), midOn = MidCount(on);
            double mOff = MeanLuma(off), mOn = MeanLuma(on);
            string ctx = $"(midOff={midOff} midOn={midOn} ratio={(double)midOn / midOff:0.00}; meanOff={mOff:0.0} meanOn={mOn:0.0})";

            // Without MSAA the bright bars vs dark background give hard 1-sample edges = ~0 intermediate pixels; MSAA
            // resolves the diagonal edges into a band of partial-coverage pixels (measured ~2900 on Metal). The +500
            // absolute floor is a wide cross-backend margin that also proves the multi-pass resolve ran at all.
            Assert.True(midOn - midOff > 500, $"MSAA should resolve geometry edges into intermediate pixels {ctx}");
            // ...and the multi-pass MRT -> resolve -> post flow must preserve the image (not blank/darken it).
            Assert.True(Math.Abs(mOn - mOff) < Math.Max(2.0, mOff * 0.12),
                $"MSAA must preserve overall brightness through the resolve (not drop the colour) {ctx}");
        }
    }
}
