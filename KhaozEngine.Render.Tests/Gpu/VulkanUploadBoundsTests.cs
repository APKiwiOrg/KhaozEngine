using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Vulkan.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PHANTOM LAYER, DEVICE-FREE, ON THE NATIVE VULKAN PATH
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/695).
    ///
    /// <para><b>WHY THIS EXISTS ALONGSIDE THE GpuFact.</b>
    /// <c>TextureArrayGpuTests.ThePhantomLayerOfAOneLayerArrayCannotBeUploadedTo</c> is the real proof and it runs
    /// on lavapipe in the cross-platform matrix, which is a dispatch away from anyone writing this code and never
    /// available on the machine most of it is written on. What is pinned here is the arithmetic that GpuFact
    /// depends on, in the same order <c>VulkanGpuDevice.UpdateTexture</c> composes it: the view plan derived from
    /// the usage, the real layer count that plan expands, then the bound checked against it.</para>
    ///
    /// <para><b>THE BUG THIS CATCHES IS SILENCE, NOT A WRONG PIXEL.</b> A recorded
    /// <c>vkCmdCopyBufferToImage</c> carries no result code, so a base array layer past the image's own count is
    /// undefined rather than refused. Before the fix the call returned normally and a software rasterizer
    /// executed it without a word.</para>
    /// </summary>
    public sealed class VulkanUploadBoundsTests
    {
        const uint W = 4, H = 4;

        /// <summary>
        /// THE FAILING ROW, REPRODUCED WITHOUT A DEVICE: the GpuFact's own description, its own array layer, and
        /// the three calls the device path makes between them.
        /// </summary>
        [Fact]
        public void AOneLayerArrayRefusesLayerOneByName()
        {
            GpuTextureDescription description = GpuTextureDescription.Texture2DArray(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, arrayLayers: 1, mipLevels: 1);

            uint layers = ActualLayers(description);
            Assert.Equal(1u, layers);

            var thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanUploadBounds.RequireArrayLayer(1, layers));

            Assert.Equal("arrayLayer", thrown.ParamName);
        }

        /// <summary>
        /// THE ACCEPTED PATH IS UNTOUCHED. Layer 0 of the same one-layer array is what every real caller writes,
        /// and the check has to stay silent for it or the fix would be a worse bug than the one it closes.
        /// </summary>
        [Fact]
        public void AOneLayerArrayStillAcceptsLayerZero()
        {
            GpuTextureDescription description = GpuTextureDescription.Texture2DArray(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, arrayLayers: 1, mipLevels: 1);

            VulkanUploadBounds.RequireArrayLayer(0, ActualLayers(description));
        }

        /// <summary>
        /// A REAL ARRAY ADMITS EVERY LAYER IT DECLARED AND REFUSES THE ONE PAST THE END, which is the same rule
        /// at a count where an off-by-one would otherwise hide.
        /// </summary>
        [Fact]
        public void AFourLayerArrayAdmitsItsFourAndRefusesTheFifth()
        {
            GpuTextureDescription description = GpuTextureDescription.Texture2DArray(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, arrayLayers: 4, mipLevels: 1);

            uint layers = ActualLayers(description);
            Assert.Equal(4u, layers);

            for (uint layer = 0; layer < 4; layer++) VulkanUploadBounds.RequireArrayLayer(layer, layers);

            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanUploadBounds.RequireArrayLayer(4, layers));
        }

        /// <summary>
        /// A CUBEMAP'S BOUND IS THE EXPANDED COUNT. A cube face IS an array layer to Vulkan, so checking the
        /// logical count would refuse five faces out of six that the image genuinely carries.
        /// </summary>
        [Fact]
        public void ACubemapCountsSixLayersPerCube()
        {
            // NOT the Texture2DArray factory, which names the 2D-array case and nothing else: a cubemap keeps its
            // own layer-count rule, and one cube is one layer of six faces.
            var description = new GpuTextureDescription(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap,
                mipLevels: 1, arrayLayers: 1);

            uint layers = ActualLayers(description);
            Assert.Equal(6u, layers);

            VulkanUploadBounds.RequireArrayLayer(5, layers);
            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanUploadBounds.RequireArrayLayer(6, layers));
        }

        // VulkanTexture.ActualArrayLayers, spelled out: the view plan comes from the usage bits and the plan is
        // what knows a cube face counts as a layer. Both halves are device-free, so this is the device path's own
        // number rather than a second copy of the rule.
        static uint ActualLayers(in GpuTextureDescription description)
            => VulkanViewPolicy.ForTexture(description.Usage).ActualArrayLayers(description.ArrayLayers);
    }
}
