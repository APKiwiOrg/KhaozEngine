using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // DrawTrail queues onto a live Scene3D (its ctor needs a GPU device), so these run gated behind
    // KE_GPU_TESTS=1, mirroring Scene3DBeamQueueTests. They assert queue accounting + style capture only;
    // the strip geometry is covered headlessly by TrailGeometryTests and the on-screen look by a golden.
    public sealed class Scene3DTrailQueueTests
    {
        static void WithScene(System.Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        static TrailSample[] Trail(int n)
        {
            var s = new TrailSample[n];
            for (int i = 0; i < n; i++)
                s[i] = new TrailSample(new Vector3(i, 0, 0), 0.25f, (float)i / System.Math.Max(1, n - 1));
            return s;
        }

        [GpuFact]
        public void DrawTrail_Queues_Then_Begin_Clears() => WithScene(scene =>
        {
            scene.Begin();
            Assert.Equal(0, scene.TrailCount);

            scene.DrawTrail(Trail(4), TrailStyle.Default);
            Assert.Equal(1, scene.TrailCount);

            scene.DrawTrail(Trail(3), TrailStyle.Default with { Blend = TrailBlend.Alpha });
            Assert.Equal(2, scene.TrailCount);

            scene.Begin();
            Assert.Equal(0, scene.TrailCount);
        });

        [GpuFact]
        public void DrawTrail_FewerThanTwoSamples_IsNoOp() => WithScene(scene =>
        {
            scene.Begin();
            scene.DrawTrail(Trail(1), TrailStyle.Default);
            scene.DrawTrail(System.Array.Empty<TrailSample>(), TrailStyle.Default);
            Assert.Equal(0, scene.TrailCount);
        });

        [GpuFact]
        public void DrawTrail_CapturesSamplesAndStyle() => WithScene(scene =>
        {
            scene.Begin();
            var style = TrailStyle.Default with
            {
                Color = new Color(0f, 1f, 0f, 0.8f),
                Blend = TrailBlend.Alpha,
                SoftEdge = 0.25f,
            };
            scene.DrawTrail(Trail(5), style);

            var item = scene.TrailItems[0];
            Assert.Equal(5, item.Count);                       // all samples copied
            Assert.Equal(TrailBlend.Alpha, item.Style.Blend);
            Assert.Equal(0.25f, item.Style.SoftEdge, 4);
            Assert.Equal(1f, ((Vector4)item.Style.Color).Y, 4);
            Assert.Equal(0.8f, ((Vector4)item.Style.Color).W, 4);
        });
    }
}
