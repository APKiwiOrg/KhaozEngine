using System.Collections.Generic;
using System.Globalization;
using KhaozEngine.Gpu.Vulkan.Internal;
using Silk.NET.Vulkan;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>ONE <c>vkCmdCopyBuffer</c> AS THE DRIVER WOULD HAVE RECEIVED IT.</summary>
    /// <param name="CommandBuffer">The buffer it was recorded into.</param>
    /// <param name="Source">The source <c>VkBuffer</c>.</param>
    /// <param name="Destination">The destination <c>VkBuffer</c>.</param>
    /// <param name="Region">The one region.</param>
    internal readonly record struct VulkanRecordedBufferCopy(
        ulong CommandBuffer, ulong Source, ulong Destination, BufferCopy Region);

    /// <summary>ONE <c>vkCmdCopyImage</c>.</summary>
    /// <param name="Source">The source <c>VkImage</c>.</param>
    /// <param name="Destination">The destination <c>VkImage</c>.</param>
    /// <param name="Region">The one region.</param>
    internal readonly record struct VulkanRecordedImageCopy(ulong Source, ulong Destination, ImageCopy Region);

    /// <summary>ONE <c>vkCmdCopyImageToBuffer</c> or <c>vkCmdCopyBufferToImage</c>.</summary>
    /// <param name="Image">The <c>VkImage</c> side.</param>
    /// <param name="Buffer">The <c>VkBuffer</c> side.</param>
    /// <param name="ToBuffer">True for the readback direction.</param>
    /// <param name="Region">The one region.</param>
    internal readonly record struct VulkanRecordedBufferImageCopy(
        ulong Image, ulong Buffer, bool ToBuffer, BufferImageCopy Region);

    /// <summary>ONE <c>vkCmdBlitImage</c> of the mip chain.</summary>
    /// <param name="Image">The image, on both sides.</param>
    /// <param name="Region">The one region.</param>
    /// <param name="Linear">The filter.</param>
    internal readonly record struct VulkanRecordedBlit(ulong Image, ImageBlit Region, bool Linear);

    /// <summary>ONE <c>vkCmdResolveImage</c>.</summary>
    /// <param name="Source">The multisampled source.</param>
    /// <param name="Destination">The single-sample destination.</param>
    /// <param name="Region">The one region.</param>
    internal readonly record struct VulkanRecordedResolve(
        ulong Source, ulong Destination, ImageResolve Region);

    /// <summary>
    /// AN <see cref="IVulkanTransferSink"/> WITH NO DEVICE BEHIND IT, so the four staging cases, every region's
    /// arithmetic, the mip chain's per-level extents and the resolve all run under a plain <c>[Fact]</c>.
    /// Work-breakdown row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525).
    ///
    /// <para><b>IT KEEPS THE REGIONS AND NOT ONLY THE COUNTS.</b> The count is the cheap half: what actually
    /// garbles all 36 goldens at once is a buffer offset, a row length or an extent that is subtly wrong, and none
    /// of those is visible from a tally. See <see cref="VulkanStagingLayout"/> for why this is the highest-risk
    /// parity surface in the backend.</para>
    ///
    /// <para><b>IT SHARES THE ORDERING TRACE</b> with the barrier recorder and the draw emitter, so a test can
    /// pin that a copy's layout transitions precede its region and that the pass was ended before either.</para>
    /// </summary>
    internal sealed class FakeVulkanTransferSink : IVulkanTransferSink
    {
        readonly List<VulkanRecordedBufferCopy> _bufferCopies = new();
        readonly List<VulkanRecordedImageCopy> _imageCopies = new();
        readonly List<VulkanRecordedBufferImageCopy> _bufferImageCopies = new();
        readonly List<VulkanRecordedBlit> _blits = new();
        readonly List<VulkanRecordedResolve> _resolves = new();
        readonly List<bool> _memoryBarriers = new();
        readonly List<string> _trace;

        /// <param name="trace">A trace list to append to rather than own. Its own list when null.</param>
        internal FakeVulkanTransferSink(List<string>? trace = null) => _trace = trace ?? new List<string>();

        /// <summary>Every <c>vkCmdCopyBuffer</c>, in order.</summary>
        internal IReadOnlyList<VulkanRecordedBufferCopy> BufferCopies => _bufferCopies;

        /// <summary>Every <c>vkCmdCopyImage</c>, in order.</summary>
        internal IReadOnlyList<VulkanRecordedImageCopy> ImageCopies => _imageCopies;

        /// <summary>Every buffer-image copy in either direction, in order.</summary>
        internal IReadOnlyList<VulkanRecordedBufferImageCopy> BufferImageCopies => _bufferImageCopies;

        /// <summary>Every <c>vkCmdBlitImage</c>, in order, which for a mip chain is one per level.</summary>
        internal IReadOnlyList<VulkanRecordedBlit> Blits => _blits;

        /// <summary>Every <c>vkCmdResolveImage</c>, in order.</summary>
        internal IReadOnlyList<VulkanRecordedResolve> Resolves => _resolves;

        /// <summary>Every global memory barrier, true for the one BEFORE a buffer copy and false for the one
        /// after.</summary>
        internal IReadOnlyList<bool> MemoryBarriers => _memoryBarriers;

        /// <summary>Every call in order, as text.</summary>
        internal IReadOnlyList<string> Trace => _trace;

        /// <inheritdoc/>
        public void CopyBuffer(ulong commandBuffer, ulong source, ulong destination, in BufferCopy region)
        {
            _bufferCopies.Add(new VulkanRecordedBufferCopy(commandBuffer, source, destination, region));
            _trace.Add("CopyBuffer(" + region.Size.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void MemoryBarrier(ulong commandBuffer, bool toTransfer)
        {
            _memoryBarriers.Add(toTransfer);
            _trace.Add(toTransfer ? "MemoryBarrier(toTransfer)" : "MemoryBarrier(fromTransfer)");
        }

        /// <inheritdoc/>
        public void CopyImage(ulong commandBuffer, ulong source, ulong destination, in ImageCopy region)
        {
            _imageCopies.Add(new VulkanRecordedImageCopy(source, destination, region));
            _trace.Add("CopyImage(mip=" + region.SrcSubresource.MipLevel.ToString(CultureInfo.InvariantCulture)
                + ")");
        }

        /// <inheritdoc/>
        public void CopyImageToBuffer(ulong commandBuffer, ulong image, ulong buffer, in BufferImageCopy region)
        {
            _bufferImageCopies.Add(new VulkanRecordedBufferImageCopy(image, buffer, ToBuffer: true, region));
            _trace.Add("CopyImageToBuffer(offset="
                + region.BufferOffset.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void CopyBufferToImage(ulong commandBuffer, ulong buffer, ulong image, in BufferImageCopy region)
        {
            _bufferImageCopies.Add(new VulkanRecordedBufferImageCopy(image, buffer, ToBuffer: false, region));
            _trace.Add("CopyBufferToImage(offset="
                + region.BufferOffset.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void BlitImage(ulong commandBuffer, ulong image, in ImageBlit region, bool linear)
        {
            _blits.Add(new VulkanRecordedBlit(image, region, linear));
            _trace.Add("BlitImage(level="
                + region.DstSubresource.MipLevel.ToString(CultureInfo.InvariantCulture) + ")");
        }

        /// <inheritdoc/>
        public void ResolveImage(ulong commandBuffer, ulong source, ulong destination, in ImageResolve region)
        {
            _resolves.Add(new VulkanRecordedResolve(source, destination, region));
            _trace.Add("ResolveImage(" + region.Extent.Width.ToString(CultureInfo.InvariantCulture) + "x"
                + region.Extent.Height.ToString(CultureInfo.InvariantCulture) + ")");
        }
    }
}
