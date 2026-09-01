using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// THE PHANTOM LAYER, DEVICE-FREE, ON THE NATIVE DIRECT3D 11 PATH
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/695).
    ///
    /// <para><b>WHY THIS EXISTS ALONGSIDE THE GpuFact.</b>
    /// <c>TextureArrayGpuTests.ThePhantomLayerOfAOneLayerArrayCannotBeUploadedTo</c> is the real proof and it runs
    /// on WARP in the cross-platform matrix, which is a dispatch away from anyone writing this code and never
    /// available on the machine most of it is written on. What is pinned here is the arithmetic that GpuFact
    /// depends on, in the same order <c>D3D11GpuDevice.UpdateTexture</c> composes it: the slice count the
    /// description asks for, then the bound checked against it.</para>
    ///
    /// <para><b>THE BUG THIS CATCHES IS SILENCE, NOT A WRONG PIXEL.</b> <c>UpdateSubresource</c> drops an index
    /// past the end of the resource without an <c>HRESULT</c>, so before the fix the call returned normally and
    /// the bytes went nowhere. Nothing downstream could have noticed.</para>
    /// </summary>
    public sealed class D3D11UploadBoundsTests
    {
        const uint W = 4, H = 4;

        /// <summary>
        /// THE FAILING ROW, REPRODUCED WITHOUT A DEVICE: the GpuFact's own description, its own array layer, and
        /// the two calls the device path makes between them.
        /// </summary>
        [Fact]
        public void AOneLayerArrayRefusesLayerOneByName()
        {
            GpuTextureDescription description = GpuTextureDescription.Texture2DArray(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled, arrayLayers: 1, mipLevels: 1);

            uint slices = D3D11UploadBounds.ArraySlices(description);
            Assert.Equal(1u, slices);

            var thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => D3D11UploadBounds.RequireArrayLayer(1, slices));

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

            D3D11UploadBounds.RequireArrayLayer(0, D3D11UploadBounds.ArraySlices(description));
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

            uint slices = D3D11UploadBounds.ArraySlices(description);
            Assert.Equal(4u, slices);

            for (uint layer = 0; layer < 4; layer++) D3D11UploadBounds.RequireArrayLayer(layer, slices);

            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11UploadBounds.RequireArrayLayer(4, slices));
        }

        /// <summary>
        /// A CUBEMAP'S BOUND IS SLICES AND NOT CUBES. Its layer count reports cubes and the resource carries six
        /// subresource slices per cube, so checking the logical count would refuse five faces out of six that the
        /// subresource arithmetic addresses perfectly well.
        /// </summary>
        [Fact]
        public void ACubemapCountsSixSlicesPerCube()
        {
            // NOT the Texture2DArray factory, which names the 2D-array case and nothing else: a cubemap keeps its
            // own layer-count rule, and one cube is one layer of six faces.
            var description = new GpuTextureDescription(
                W, H, GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.Cubemap,
                mipLevels: 1, arrayLayers: 1);

            uint slices = D3D11UploadBounds.ArraySlices(description);
            Assert.Equal(6u, slices);

            D3D11UploadBounds.RequireArrayLayer(5, slices);
            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11UploadBounds.RequireArrayLayer(6, slices));
        }

        /// <summary>
        /// THE MIP LEVEL, the second of the three bounds and the one
        /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/697">#697</see> closed. A level past the end
        /// of the chain is the same silence the phantom layer was: <c>D3D11CalcSubresource</c> is arithmetic, not
        /// a lookup, so it happily names a subresource the resource does not have.
        /// </summary>
        [Fact]
        public void AOneMipTextureRefusesLevelOneByName()
        {
            var thrown = Assert.Throws<ArgumentOutOfRangeException>(
                () => D3D11UploadBounds.RequireMipLevel(1, 1));

            Assert.Equal("mipLevel", thrown.ParamName);
        }

        /// <summary>
        /// A REAL CHAIN ADMITS EVERY LEVEL IT DECLARED AND REFUSES THE ONE PAST THE END, the same off-by-one rule
        /// the layer bound gets.
        /// </summary>
        [Fact]
        public void AFourLevelChainAdmitsItsFourAndRefusesTheFifth()
        {
            for (uint level = 0; level < 4; level++) D3D11UploadBounds.RequireMipLevel(level, 4);

            Assert.Throws<ArgumentOutOfRangeException>(() => D3D11UploadBounds.RequireMipLevel(4, 4));
        }

        /// <summary>
        /// A LEVEL HALVES EACH DIMENSION AND STOPS AT ONE, which is what the region bound is measured against.
        /// The floor is the whole reason a deep level of a small texture is still addressable.
        /// </summary>
        [Fact]
        public void AMipLevelHalvesTheDimensionAndNeverReachesZero()
        {
            Assert.Equal(64u, D3D11UploadBounds.MipDimension(64, 0));
            Assert.Equal(32u, D3D11UploadBounds.MipDimension(64, 1));
            Assert.Equal(8u, D3D11UploadBounds.MipDimension(64, 3));
            Assert.Equal(1u, D3D11UploadBounds.MipDimension(64, 6));
            Assert.Equal(1u, D3D11UploadBounds.MipDimension(64, 9));
        }

        /// <summary>
        /// A REGION INSIDE ITS DESTINATION SUBRESOURCE IS ACCEPTED, on every shape the seam can name: the whole
        /// mip, a sub-rectangle at a non-zero origin, one that ends exactly on both edges, and the same at a
        /// non-zero level, where the bound is the level's own size rather than mip 0's.
        /// </summary>
        [Fact]
        public void ARegionInsideItsSubresourceIsAccepted()
        {
            D3D11UploadBounds.RequireRegionFits(0, 0, 0, 64, 32, 64, 32);
            D3D11UploadBounds.RequireRegionFits(0, 1, 1, 2, 2, 64, 32);
            D3D11UploadBounds.RequireRegionFits(0, 63, 31, 1, 1, 64, 32);

            // Mip 2 of a 64 by 32 texture is 16 by 8, and the region is checked against THAT.
            D3D11UploadBounds.RequireRegionFits(2, 0, 0, 16, 8, 64, 32);
            D3D11UploadBounds.RequireRegionFits(2, 8, 4, 8, 4, 64, 32);
        }

        /// <summary>
        /// ONE TEXEL PAST EITHER EDGE IS REFUSED, BY AXIS. This is worse than the phantom layer rather than the
        /// same: <c>UpdateSubresource</c> applies the box against the subresource it names, so an oversized
        /// region writes real texels in the wrong place instead of being dropped.
        /// </summary>
        [Fact]
        public void ARegionPastEitherEdgeIsRefusedByAxis()
        {
            var right = Assert.Throws<ArgumentOutOfRangeException>(
                () => D3D11UploadBounds.RequireRegionFits(0, 1, 0, 64, 32, 64, 32));
            Assert.Equal("x", right.ParamName);

            var bottom = Assert.Throws<ArgumentOutOfRangeException>(
                () => D3D11UploadBounds.RequireRegionFits(0, 0, 1, 64, 32, 64, 32));
            Assert.Equal("y", bottom.ParamName);

            // And against the MIP's dimensions rather than mip 0's: 32 by 8 fits the texture and not level 2.
            Assert.Throws<ArgumentOutOfRangeException>(
                () => D3D11UploadBounds.RequireRegionFits(2, 0, 0, 32, 8, 64, 32));
        }

        /// <summary>
        /// THE SUM IS TAKEN IN 64 BITS, so an origin near the top of the range is refused rather than wrapping
        /// back inside the texture and passing.
        /// </summary>
        [Fact]
        public void ARegionWhoseSumOverflowsThirtyTwoBitsIsRefused()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => D3D11UploadBounds.RequireRegionFits(0, uint.MaxValue, 0, 2, 1, 64, 32));
            Assert.Throws<ArgumentOutOfRangeException>(
                () => D3D11UploadBounds.RequireRegionFits(0, 0, uint.MaxValue, 1, 2, 64, 32));
        }
    }
}
