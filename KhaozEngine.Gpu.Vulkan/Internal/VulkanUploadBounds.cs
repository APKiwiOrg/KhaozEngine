using System;
using System.Globalization;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE DEVICE-LEVEL UPLOAD'S SUBRESOURCE BOUND, CHECKED AT THE CALL (#695).
    ///
    /// <para><b>VULKAN ITSELF NEVER SAYS NO EITHER.</b> A <c>vkCmdCopyBufferToImage</c> whose
    /// <c>baseArrayLayer</c> is past the image's own layer count is undefined behaviour, not a returned error:
    /// there is no result code on a recorded command, the validation layers are the only thing that would name
    /// it, and a software rasterizer happily executes it. So the call was accepted in silence on this backend
    /// while native Metal refused it by name. That divergence is the defect: the seam exists so a caller can
    /// develop against one backend and ship on another.
    /// </para>
    ///
    /// <para><b>THE STAGING-TEXTURE ARM ALREADY REFUSED IT, WHICH IS WHY THE CHECK SITS ABOVE THE BRANCH.</b>
    /// <c>VulkanStagingLayout.For</c> validates the subresource against the staging shape, so a staging upload
    /// aimed at a phantom layer already threw and an image upload did not. Checking once, before the two arms
    /// part, is what makes the whole entry point answer the same way, and it names the parameter that carried the
    /// bad value rather than the mip level beside it.
    /// </para>
    ///
    /// <para><b>THE BOUND IS THE REAL LAYER COUNT, cubemap faces expanded</b>
    /// (<see cref="VulkanTexture.ActualArrayLayers"/>), because a cube face IS an array layer to Vulkan and that
    /// is the number the image was created with. A staging texture can never be a cubemap
    /// (<see cref="VulkanViewPolicy.ForTexture"/> refuses the staging bit in any combination), so on that arm the
    /// expanded count and the logical one are the same number.
    /// </para>
    /// </summary>
    internal static class VulkanUploadBounds
    {
        /// <summary>
        /// Refuse an array layer the destination does not have, by the name of the parameter that carried it.
        /// </summary>
        /// <param name="arrayLayer">The destination array layer the caller asked for.</param>
        /// <param name="arrayLayers">How many layers the destination actually has, cubemap faces expanded.</param>
        internal static void RequireArrayLayer(uint arrayLayer, uint arrayLayers)
        {
            if (arrayLayer < arrayLayers) return;

            throw new ArgumentOutOfRangeException(nameof(arrayLayer), arrayLayer,
                "Array layer "
                + arrayLayer.ToString(CultureInfo.InvariantCulture)
                + " is outside a native Vulkan texture with "
                + arrayLayers.ToString(CultureInfo.InvariantCulture)
                + " array "
                + (arrayLayers == 1 ? "layer." : "layers.")
                + " The copy it would record is undefined rather than refused, so the bytes would land wherever "
                + "the driver put them and the caller would never hear about it.");
        }

        /// <summary>
        /// Refuse a mip level the destination does not have, by the name of the parameter that carried it. The
        /// layer bound's sibling and the same silence: a recorded <c>vkCmdCopyBufferToImage</c> carries no result
        /// code, so a mip level past the end of the chain is undefined rather than refused. The staging arm
        /// already caught it through the layout arithmetic, so this is what makes both arms answer alike.
        /// </summary>
        /// <param name="mipLevel">The destination mip level the caller asked for.</param>
        /// <param name="mipLevels">How many levels the destination actually has.</param>
        internal static void RequireMipLevel(uint mipLevel, uint mipLevels)
        {
            if (mipLevel < mipLevels) return;

            throw new ArgumentOutOfRangeException(nameof(mipLevel), mipLevel,
                "Mip level "
                + mipLevel.ToString(CultureInfo.InvariantCulture)
                + " is outside a native Vulkan texture with "
                + mipLevels.ToString(CultureInfo.InvariantCulture)
                + " mip "
                + (mipLevels == 1 ? "level." : "levels.")
                + " The copy it would record is undefined rather than refused, so the bytes would land wherever "
                + "the driver put them and the caller would never hear about it.");
        }

        /// <summary>
        /// One mip level's size along one axis: the base dimension halved once per level, with a floor of one,
        /// which is the rule Vulkan builds the chain by. This is the number a region is measured against.
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
        /// most when it is missing. The image arm validated the payload LENGTH and nothing about where the bytes
        /// land, and the staging arm validated the subresource and not the rectangle inside it, so an oversized
        /// region wrote past the subresource on one arm and recorded an undefined copy on the other. Check the
        /// level first with <see cref="RequireMipLevel"/>, since a region cannot be compared against a
        /// subresource that does not exist.
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
                "A native Vulkan texture upload of "
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
                + ". On a staging texture that writes past the subresource into whatever follows it, and on an image "
                + "the copy it records is undefined.");
    }
}
