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
    }
}
