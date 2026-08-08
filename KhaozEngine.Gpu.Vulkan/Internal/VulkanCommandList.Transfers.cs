using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE TRANSFER FAMILY OF THE COMMAND LIST: buffer copies, texture copies, mip generation and the
    /// multisample resolve. Split into its own partial per
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/556, because the transfer family is a different
    /// subsystem from drawing (it shares only the end-the-pass-first rule) and the main file sits against the
    /// KESIZE cap. Every member here still refuses naming the draw and dispatch row: row 15
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) fills them in place.
    /// </summary>
    internal sealed partial class VulkanCommandList
    {
        /// <inheritdoc/>
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes)
            => throw NotBuiltYet("Copying between buffers", DrawRow);

        /// <inheritdoc/>
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
            => throw NotBuiltYet("Copying a texture", DrawRow);

        /// <inheritdoc/>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint width, uint height)
            => throw NotBuiltYet("Copying a texture subresource", DrawRow);

        /// <inheritdoc/>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
            => throw NotBuiltYet("Copying a texture subresource", DrawRow);

        /// <inheritdoc/>
        public void GenerateMipmaps(IGpuTexture texture) => throw NotBuiltYet("Generating mipmaps", DrawRow);

        /// <inheritdoc/>
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
            => throw NotBuiltYet("Resolving a multisampled texture", DrawRow);
    }
}
