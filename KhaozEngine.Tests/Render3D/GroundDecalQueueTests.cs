using System;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // The ground-decal queue lives on a live Scene3D (its ctor needs a GPU device), so this runs gated behind
    // KE_GPU_TESTS=1, mirroring Scene3DFillTests / Scene3DBeamQueueTests (no headless non-GPU Scene3D ctor exists).
    // It asserts the queue accounting only; GPU output is covered by the golden snapshot.
    public sealed class GroundDecalQueueTests
    {
        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture tex = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, tex);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        static GroundDecal SampleCircle() => new()
        {
            Shape = DecalShape.Circle,
            Center = new Vector3(1f, 0f, 2f),
            Rotation = 0f,
            Size = new Vector4(3f, 0, 0, 0),
            FillColor = new Color(1f, 0f, 0f, 0.5f),
            OutlineColor = new Color(1f, 1f, 0f, 1f),
            EdgeThickness = 0.1f,
            FillFraction = 1f,
            FlashAdd = 0f,
            Blend = DecalBlend.Alpha,
            YTolerance = 0.5f,
            MaxStep = 1f,
        };

        [GpuFact]
        public void DrawGroundDecal_enqueues_and_Begin_clears() => WithScene(scene =>
        {
            Assert.Equal(0, scene.DecalCount);
            scene.DrawGroundDecal(SampleCircle());
            scene.DrawGroundDecal(SampleCircle());
            Assert.Equal(2, scene.DecalCount);
            scene.Begin();
            Assert.Equal(0, scene.DecalCount);
        });
    }
}
