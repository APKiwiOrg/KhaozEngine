using System;
using System.Numerics;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    // End-to-end smoke for the trail pass: the strip actually renders on the GPU (both blend pipelines' shaders
    // compile and the draw produces pixels). Non-golden and differential (trail vs no-trail brightening), so it is
    // cross-platform robust without a baked image; the strip geometry math is covered headlessly by
    // TrailGeometryTests. Runs under KE_GPU_TESTS (Metal locally, D3D11 + Vulkan in CI).
    public sealed class Scene3DTrailRenderGpuTests
    {
        const int W = 128, H = 128;

        static void DarkScene(Scene3D scene)
        {
            scene.Post.Starfield = false;
            scene.Post.Outline = false;
            scene.Post.BackgroundColor = new Color(0f, 0f, 0f, 1f);
            scene.Camera.Frame(Vector3.Zero, new Vector3(0f, 0f, 5f));   // look down -Z at a horizontal bar
        }

        // A horizontal bar of samples across the view centre; camera-facing so it presents its full width.
        static TrailSample[] BarTrail()
        {
            const int n = 8;
            var s = new TrailSample[n];
            for (int i = 0; i < n; i++)
            {
                float t = i / (float)(n - 1);
                s[i] = new TrailSample(new Vector3(-2f + 4f * t, 0f, 0f), 0.3f, 1f);
            }
            return s;
        }

        static long TotalBrightness(byte[] rgba)
        {
            long sum = 0;
            for (int i = 0; i < rgba.Length; i += 4) sum += rgba[i] + rgba[i + 1] + rgba[i + 2];
            return sum;
        }

        [GpuFact]
        public void AdditiveTrail_BrightensTheFrame()
        {
            byte[] empty = Render3DSnapshot.Capture(W, H, DarkScene, drawFrame: _ => { }, frames: 1);
            byte[] trail = Render3DSnapshot.Capture(W, H, DarkScene,
                drawFrame: scene => scene.DrawTrail(BarTrail(),
                    TrailStyle.Default with { Color = new Color(1f, 1f, 1f, 1f) }), frames: 1);

            long e = TotalBrightness(empty);
            long t = TotalBrightness(trail);
            Assert.True(t > e + 5000, $"additive trail should visibly brighten the frame: empty={e} trail={t}");
        }

        [GpuFact]
        public void AlphaTrail_CompositesOverTheFrame()
        {
            byte[] empty = Render3DSnapshot.Capture(W, H, DarkScene, drawFrame: _ => { }, frames: 1);
            byte[] trail = Render3DSnapshot.Capture(W, H, DarkScene,
                drawFrame: scene => scene.DrawTrail(BarTrail(),
                    TrailStyle.Default with { Blend = TrailBlend.Alpha, Color = new Color(1f, 1f, 1f, 1f) }), frames: 1);

            long e = TotalBrightness(empty);
            long t = TotalBrightness(trail);
            Assert.True(t > e + 5000, $"alpha trail should composite over the frame: empty={e} trail={t}");
        }
    }
}
