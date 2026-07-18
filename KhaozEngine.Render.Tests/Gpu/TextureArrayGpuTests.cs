using KhaozEngine.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    public sealed class TextureArrayGpuTests
    {
        [GpuFact]
        public void ArrayLayerUploadAndMipGenerationSucceed()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            const uint W = 4, H = 4, layers = 5, mips = 3; // floor(log2(4)) + 1 == 3
            using var tex = dev.Factory.CreateTexture(GpuTextureDescription.Texture2DArray(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, layers, mips));

            for (uint L = 0; L < layers; L++)
            {
                var px = new byte[W * H * 4];
                for (int p = 0; p < px.Length; p += 4) { px[p] = (byte)(L * 40); px[p + 3] = 255; }
                dev.UpdateTexture(tex, px, 0, 0, W, H, mipLevel: 0, arrayLayer: L);
            }

            using IGpuCommandList cl = dev.Factory.CreateCommandList();
            cl.Begin();
            cl.GenerateMipmaps(tex);
            cl.End();
            dev.Submit(cl);
            dev.WaitForIdle();

            Assert.Equal(W, tex.Width);
            Assert.Equal(H, tex.Height);
        }
    }
}
