using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Scene3D's ctor needs a GPU device, so these run gated behind KE_GPU_TESTS=1 (mirrors Scene3DBeamQueueTests).
    // They prove UnloadTexture frees the texture slot so a long-lived scene that streams textured assets doesn't
    // accumulate native textures (previously they only freed at Dispose).
    public sealed class Scene3DTextureUnloadTests
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

        static readonly byte[] Pixel = new byte[] { 255, 255, 255, 255 };   // 1x1 RGBA8

        [GpuFact]
        public void Load_TracksLiveTextures() => WithScene(scene =>
        {
            Assert.Equal(0, scene.LiveTextureCount);
            scene.LoadTexture(Pixel, 1, 1);
            scene.LoadTexture(Pixel, 1, 1);
            Assert.Equal(2, scene.LiveTextureCount);
        });

        [GpuFact]
        public void UnloadTexture_FreesTheSlot() => WithScene(scene =>
        {
            var a = scene.LoadTexture(Pixel, 1, 1);
            var b = scene.LoadTexture(Pixel, 1, 1);
            Assert.Equal(2, scene.LiveTextureCount);

            scene.UnloadTexture(a);
            Assert.Equal(1, scene.LiveTextureCount);
            scene.UnloadTexture(b);
            Assert.Equal(0, scene.LiveTextureCount);
        });

        [GpuFact]
        public void LoadUnloadCycles_DoNotAccumulate() => WithScene(scene =>
        {
            // The streaming case: load and free repeatedly. Live count must return to zero each cycle, not grow.
            for (int i = 0; i < 64; i++)
            {
                var h = scene.LoadTexture(Pixel, 1, 1);
                scene.UnloadTexture(h);
                Assert.Equal(0, scene.LiveTextureCount);
            }
        });

        [GpuFact]
        public void UnloadTexture_InvalidOrDouble_IsNoOp() => WithScene(scene =>
        {
            scene.UnloadTexture(Scene3D.TextureHandle.Invalid);   // default handle: no-op
            var h = scene.LoadTexture(Pixel, 1, 1);
            scene.UnloadTexture(h);
            scene.UnloadTexture(h);                               // already unloaded: no-op, no throw
            Assert.Equal(0, scene.LiveTextureCount);
        });
    }
}
