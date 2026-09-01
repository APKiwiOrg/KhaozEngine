using System;
using System.Globalization;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// THE DEVICE-LEVEL UPLOAD'S SUBRESOURCE BOUND, CHECKED AT THE CALL (#695).
    ///
    /// <para><b>D3D11 ITSELF NEVER SAYS NO.</b> <c>ID3D11DeviceContext.UpdateSubresource</c> takes a flat
    /// subresource index, and an index past the end of the resource is not an <c>HRESULT</c> failure: the retail
    /// runtime drops the call, and only the debug layer (which the golden legs do not arm) prints anything. So a
    /// layer the texture does not have arrives as SILENCE on this backend, while native Metal refuses the same
    /// call by name. That divergence is the whole defect: the seam exists so a caller can develop against one
    /// backend and ship on another, and a phantom layer that throws on macOS and no-ops on Windows breaks exactly
    /// that promise.
    /// </para>
    ///
    /// <para><b>THE BOUND IS SLICES, NOT LOGICAL LAYERS.</b> A cubemap reports its
    /// <see cref="D3D11Texture.ArrayLayers"/> in CUBES and carries six subresource slices per cube, and the
    /// subresource arithmetic the upload and the emitter's copies both use takes the SLICE the caller means. So
    /// the number to check against is <see cref="ArraySlices"/>, which is the same <c>ArraySize</c> the resource
    /// is created with. It lives here rather than inline at the creation site so that the bound and the number
    /// the resource actually has are ONE source, and so that both are reachable without a device: neither this
    /// backend nor Vulkan runs on the machine most of this engine is written on, and the only cheap proof
    /// available before a matrix dispatch is a device-free test of the arithmetic the device path calls.
    /// </para>
    /// </summary>
    internal static class D3D11UploadBounds
    {
        /// <summary>
        /// The REAL subresource slice count a description asks for: the layer count (at least one, which is how
        /// <see cref="D3D11Texture"/> reads a zero) times six on a cubemap, whose layers count cubes.
        /// </summary>
        /// <param name="description">The seam's texture description.</param>
        internal static uint ArraySlices(in GpuTextureDescription description)
        {
            uint layers = description.ArrayLayers == 0 ? 1 : description.ArrayLayers;

            return (description.Usage & GpuTextureUsage.Cubemap) != 0 ? layers * 6 : layers;
        }

        /// <summary>
        /// Refuse an array layer the destination does not have, by the name of the parameter that carried it.
        /// </summary>
        /// <param name="arrayLayer">The destination array slice the caller asked for.</param>
        /// <param name="arraySlices">How many slices the destination actually has, cubemap faces expanded.</param>
        internal static void RequireArrayLayer(uint arrayLayer, uint arraySlices)
        {
            if (arrayLayer < arraySlices) return;

            throw new ArgumentOutOfRangeException(nameof(arrayLayer), arrayLayer,
                "Array layer "
                + arrayLayer.ToString(CultureInfo.InvariantCulture)
                + " is outside a native Direct3D 11 texture with "
                + arraySlices.ToString(CultureInfo.InvariantCulture)
                + " array "
                + (arraySlices == 1 ? "slice." : "slices.")
                + " UpdateSubresource would drop the write without an error, so the bytes would land nowhere and "
                + "the caller would never hear about it.");
        }

        /// <summary>
        /// Refuse a mip level the destination does not have, by the name of the parameter that carried it. The
        /// layer bound's sibling and the same silence: <c>D3D11CalcSubresource</c> is arithmetic rather than a
        /// lookup, so a level past the end of the chain names a subresource the resource does not have and
        /// <c>UpdateSubresource</c> drops the write.
        /// </summary>
        /// <param name="mipLevel">The destination mip level the caller asked for.</param>
        /// <param name="mipLevels">How many levels the destination actually has.</param>
        internal static void RequireMipLevel(uint mipLevel, uint mipLevels)
        {
            if (mipLevel < mipLevels) return;

            throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel,
                "Mip level "
                + mipLevel.ToString(CultureInfo.InvariantCulture)
                + " is outside a native Direct3D 11 texture with "
                + mipLevels.ToString(CultureInfo.InvariantCulture)
                + " mip "
                + (mipLevels == 1 ? "level." : "levels.")
                + " UpdateSubresource would drop the write without an error, so the bytes would land nowhere and "
                + "the caller would never hear about it.");
        }

        /// <summary>
        /// One mip level's size along one axis: the base dimension halved once per level, with a floor of one,
        /// which is the rule Direct3D 11 builds the chain by. This is the number a region is measured against.
        /// </summary>
        /// <param name="largestLevelDimension">The width or height of mip 0.</param>
        /// <param name="mipLevel">The level to measure.</param>
        internal static uint MipDimension(uint largestLevelDimension, uint mipLevel)
        {
            uint value = largestLevelDimension;
            for (uint i = 0; i < mipLevel; i++) value /= 2;

            return Math.Max(1, value);
        }

        /// <summary>
        /// REFUSE A REGION THAT DOES NOT FIT ITS DESTINATION SUBRESOURCE, the third bound and the one that costs
        /// most when it is missing. A layer or level past the end is dropped in silence, but an oversized box is
        /// APPLIED: <c>UpdateSubresource</c> writes it against the subresource it named, so real texels land
        /// outside the rectangle the caller asked for. Check the level first with <see cref="RequireMipLevel"/>,
        /// since a region cannot be compared against a subresource that does not exist.
        /// </summary>
        /// <param name="mipLevel">The destination mip level, which sets the bound.</param>
        /// <param name="x">Left edge of the region.</param>
        /// <param name="y">Top edge of the region.</param>
        /// <param name="width">Region width in texels.</param>
        /// <param name="height">Region height in texels.</param>
        /// <param name="textureWidth">Mip 0's width.</param>
        /// <param name="textureHeight">Mip 0's height.</param>
        internal static void RequireRegionFits(uint mipLevel, uint x, uint y, uint width, uint height,
            uint textureWidth, uint textureHeight)
        {
            uint mipWidth = MipDimension(textureWidth, mipLevel);
            uint mipHeight = MipDimension(textureHeight, mipLevel);

            if ((ulong)x + width > mipWidth) throw Outside(nameof(x), x, width, mipWidth, "wide", mipLevel);
            if ((ulong)y + height > mipHeight) throw Outside(nameof(y), y, height, mipHeight, "tall", mipLevel);
        }

        // One axis of the region refusal, as one sentence a caller can act on: which edge it crossed, by how
        // much, and what the mip level it was aimed at actually measures.
        static ArgumentOutOfRangeException Outside(string axis, uint origin, uint extent, uint bound,
            string dimension, uint mipLevel)
            => new(axis, origin,
                "A native Direct3D 11 texture upload of "
                + extent.ToString(CultureInfo.InvariantCulture)
                + " texels from "
                + axis
                + " = "
                + origin.ToString(CultureInfo.InvariantCulture)
                + " runs to "
                + ((ulong)origin + extent).ToString(CultureInfo.InvariantCulture)
                + ", past a mip level "
                + mipLevel.ToString(CultureInfo.InvariantCulture)
                + " that is only "
                + bound.ToString(CultureInfo.InvariantCulture)
                + " texels "
                + dimension
                + ". UpdateSubresource applies the box against the subresource it names rather than refusing it, "
                + "so the texels land outside the region the caller asked for.");
    }
}
