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

        /// <summary>
        /// THE MIP LEVEL, the second of the three bounds and the one
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/697">#697</see> closed. A level past the end
        /// of the chain is the same silence the phantom layer was: the recorded copy carries no result code, so a
        /// level past the end of the chain is undefined rather than refused.
        /// </summary>
        [Fact]
        public void AOneMipTextureRefusesLevelOneByName()
        {
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanUploadBounds.RequireMipLevel(1, 1));

            Assert.Equal("mipLevel", thrown.ParamName);
        }

        /// <summary>
        /// A REAL CHAIN ADMITS EVERY LEVEL IT DECLARED AND REFUSES THE ONE PAST THE END, the same off-by-one rule
        /// the layer bound gets.
        /// </summary>
        [Fact]
        public void AFourLevelChainAdmitsItsFourAndRefusesTheFifth()
        {
            for (uint level = 0; level < 4; level++) VulkanUploadBounds.RequireMipLevel(level, 4);

            Assert.Throws<ArgumentOutOfRangeException>(() => VulkanUploadBounds.RequireMipLevel(4, 4));
        }

        /// <summary>
        /// A LEVEL HALVES EACH DIMENSION AND STOPS AT ONE, which is what the region bound is measured against.
        /// The floor is the whole reason a deep level of a small texture is still addressable.
        /// </summary>
        [Fact]
        public void AMipLevelHalvesTheDimensionAndNeverReachesZero()
        {
            Assert.Equal(64u, VulkanUploadBounds.MipDimension(64, 0));
            Assert.Equal(32u, VulkanUploadBounds.MipDimension(64, 1));
            Assert.Equal(8u, VulkanUploadBounds.MipDimension(64, 3));
            Assert.Equal(1u, VulkanUploadBounds.MipDimension(64, 6));
            Assert.Equal(1u, VulkanUploadBounds.MipDimension(64, 9));
        }

        /// <summary>
        /// A REGION INSIDE ITS DESTINATION SUBRESOURCE IS ACCEPTED, on every shape the seam can name: the whole
        /// mip, a sub-rectangle at a non-zero origin, one that ends exactly on both edges, and the same at a
        /// non-zero level, where the bound is the level's own size rather than mip 0's.
        /// </summary>
        [Fact]
        public void ARegionInsideItsSubresourceIsAccepted()
        {
            VulkanUploadBounds.RequireRegionFits(0, 0, 0, 64, 32, 64, 32);
            VulkanUploadBounds.RequireRegionFits(0, 1, 1, 2, 2, 64, 32);
            VulkanUploadBounds.RequireRegionFits(0, 63, 31, 1, 1, 64, 32);

            // Mip 2 of a 64 by 32 texture is 16 by 8, and the region is checked against THAT.
            VulkanUploadBounds.RequireRegionFits(2, 0, 0, 16, 8, 64, 32);
            VulkanUploadBounds.RequireRegionFits(2, 8, 4, 8, 4, 64, 32);
        }

        /// <summary>
        /// ONE TEXEL PAST EITHER EDGE IS REFUSED, BY AXIS. The image arm had no region bound at all: the setup
        /// command validated the payload LENGTH and nothing about where the bytes land, and the staging arm
        /// validated the subresource and not the rectangle inside it.
        /// </summary>
        [Fact]
        public void ARegionPastEitherEdgeIsRefusedByAxis()
        {
            var right = Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanUploadBounds.RequireRegionFits(0, 1, 0, 64, 32, 64, 32));
            Assert.Equal("x", right.ParamName);

            var bottom = Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanUploadBounds.RequireRegionFits(0, 0, 1, 64, 32, 64, 32));
            Assert.Equal("y", bottom.ParamName);

            // And against the MIP's dimensions rather than mip 0's: 32 by 8 fits the texture and not level 2.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanUploadBounds.RequireRegionFits(2, 0, 0, 32, 8, 64, 32));
        }

        /// <summary>
        /// THE SUM IS TAKEN IN 64 BITS, so an origin near the top of the range is refused rather than wrapping
        /// back inside the texture and passing.
        /// </summary>
        [Fact]
        public void ARegionWhoseSumOverflowsThirtyTwoBitsIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanUploadBounds.RequireRegionFits(0, uint.MaxValue, 0, 2, 1, 64, 32));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => VulkanUploadBounds.RequireRegionFits(0, 0, uint.MaxValue, 1, 2, 64, 32));
        }
    }
}
