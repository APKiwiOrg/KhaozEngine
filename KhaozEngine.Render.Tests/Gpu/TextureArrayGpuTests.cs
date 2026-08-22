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

        /// <summary>
        /// A ONE-LAYER ARRAY IS CREATED, UPLOADED AND MIPPED LIKE ANY OTHER (#666), on whichever backend is
        /// running. It is not a formality on three of the four: every eager view is built inside
        /// <c>CreateTexture</c>, so a Direct3D 11 <c>Texture2DArray</c> shader resource view over a one-slice
        /// resource and a Vulkan <c>VK_IMAGE_VIEW_TYPE_2D_ARRAY</c> over a one-layer image are both ACCEPTED BY
        /// THE DRIVER here or not at all, and Metal's descriptor takes <c>Type2DArray</c> with
        /// <c>arrayLength</c> 1. The incumbent has no way to say it and pads to a second, never-addressed slice,
        /// which this exercises from the other side: the upload and the generate below name layer 0 only.
        /// <para>
        /// That such a texture then DRAWS correctly under an array-declaring fragment is
        /// <c>TileGroundMaterialGpuTests.Single_flat_layer_reproduces_a_vertex_colour_look</c>, which is where
        /// the pixel is read back.
        /// </para>
        /// </summary>
        [GpuFact]
        public void AOneLayerArrayIsCreatedUploadedAndMippedLikeAnyOther()
        {
            using GpuDeviceContext gpu = GpuDeviceContext.CreateHeadless();
            IGpuDevice dev = gpu.GpuDevice;
            const uint W = 4, H = 4, mips = 3;
            using var tex = dev.Factory.CreateTexture(GpuTextureDescription.Texture2DArray(
                W, H, GpuPixelFormat.R8G8B8A8UNorm,
                GpuTextureUsage.Sampled | GpuTextureUsage.GenerateMipmaps, arrayLayers: 1, mipLevels: mips));

            var px = new byte[W * H * 4];
            for (int p = 0; p < px.Length; p += 4) { px[p + 1] = 200; px[p + 3] = 255; }
            dev.UpdateTexture(tex, px, 0, 0, W, H, mipLevel: 0, arrayLayer: 0);

            using IGpuCommandList cl = dev.Factory.CreateCommandList();
            cl.Begin();
            cl.GenerateMipmaps(tex);
            cl.End();
            dev.Submit(cl);
            dev.WaitForIdle();

            Assert.Equal(W, tex.Width);
            Assert.Equal(mips, tex.MipLevels);
        }
    }
}
