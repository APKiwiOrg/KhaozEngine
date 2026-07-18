using System;
using KhaozEngine.Gpu;
using KhaozEngine.Render3D;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D
{
    // Scene3D's ctor needs a GPU device, so these run gated behind KE_GPU_TESTS=1 (mirrors Scene3DTextureUnloadTests).
    // They pin the fix for the "textures go all pixely at distance when the camera moves" report: model/prop textures
    // loaded via Scene3D.LoadTexture must carry a full mip chain, so the trilinear model sampler has downsampled
    // levels to blend between instead of point-minifying level 0 into sparkle. A regression that drops back to a
    // single level (MipLevels == 1) reintroduces the aliasing, and this catches it.
    public sealed class Scene3DMipmapTests
    {
        static void WithScene(Action<Scene3D> body)
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            var f = gpu.GpuDevice.Factory;
            using IGpuTexture rt = f.CreateTexture(GpuTextureDescription.Texture2D(
                16, 16, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
            using IGpuFramebuffer fb = f.CreateFramebuffer(null, rt);
            using var scene = new Scene3D(gpu.GpuDevice, fb.Outputs);
            body(scene);
        }

        static byte[] Rgba(int w, int h)
        {
            var px = new byte[w * h * 4];
            for (int i = 0; i < px.Length; i++) px[i] = (byte)(i & 0xFF);
            return px;
        }

        [GpuFact]
        public void LoadTexture_BuildsFullMipChain_ForMinifiableTexture() => WithScene(scene =>
        {
            // 64x64 -> floor(log2(64)) + 1 == 7 levels. Without the mip chain this would be 1 and distant surfaces alias.
            var h = scene.LoadTexture(Rgba(64, 64), 64, 64);
            Assert.Equal(7u, scene.MipLevelsOf(h));
        });

        [GpuFact]
        public void LoadTexture_LeavesTinyTextureSingleLevel() => WithScene(scene =>
        {
            // A 1x1 texture (e.g. a solid default) has no mip chain to build and must stay a single level - byte-for-byte
            // the pre-fix behaviour, so the model pass's 1x1-white default path is untouched.
            var h = scene.LoadTexture(new byte[] { 255, 255, 255, 255 }, 1, 1);
            Assert.Equal(1u, scene.MipLevelsOf(h));
        });
    }
}
