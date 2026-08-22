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
    }
}
