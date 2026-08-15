using System;
using System.Globalization;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE TRANSFER FAMILY OF THE COMMAND LIST: buffer copies, texture copies, mip generation and the
    /// multisample resolve. Split into its own partial per
    /// https://github.com/APKiwiOrg/KhaozEngine/issues/556, because the transfer family is a different
    /// subsystem from drawing (it shares only the end-the-pass-first rule) and the main file sits against the
    /// KESIZE cap. Row 15 (https://github.com/APKiwiOrg/KhaozEngine/issues/525) filled them in place.
    ///
    /// <para><b>EVERY MEMBER HERE FOLLOWS THE SAME FOUR STEPS.</b> End the pending render pass instance, because
    /// every one of these is illegal inside one (V-A4). Transition each side over EXACTLY the subresources it
    /// touches, through <see cref="VulkanLayoutTracker"/>, which is what makes the restore at <c>End</c> put them
    /// back (V-F7). Emit the region. And leave the images where the copy left them, because the restore is the
    /// list's and not this call's.</para>
    ///
    /// <para><b>THE ARITHMETIC IS NOT HERE.</b> Which of the four staging cases a copy is, which range each side
    /// is transitioned over and what each region contains are <see cref="VulkanTransferPlan"/>'s, and the staging
    /// side's byte offsets are <see cref="VulkanStagingLayout"/>'s, which reproduces the incumbent's software
    /// layout byte for byte (V-C7). Both are device-free and both are pinned by their own tests, because this is
    /// the highest-risk parity surface in the backend: every golden reads back through one of these copies.</para>
    /// </summary>
    internal sealed partial class VulkanCommandList
    {
        /// <inheritdoc/>
        /// <remarks>
        /// <c>vkCmdCopyBuffer</c> between two <c>VkBuffer</c>s, with a global memory barrier on either side of it.
        /// A buffer has no layout, so nothing the tracker does orders this copy against the dispatch that wrote
        /// its source or the draw that reads its destination: see <see cref="VulkanTransferBarrier"/> for the
        /// masks and for why the incumbent's single one-directional barrier is not reproduced.
        /// </remarks>
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes)
        {
            VulkanBuffer source = VulkanBuffer.Require(src, "a native Vulkan buffer copy");
            VulkanBuffer destination = VulkanBuffer.Require(dst, "a native Vulkan buffer copy");
            IVulkanTransferSink sink = RequireTransfers("Copying between buffers");

            RequireBufferWindow(source, srcOffsetBytes, sizeInBytes, "source");
            RequireBufferWindow(destination, dstOffsetBytes, sizeInBytes, "destination");

            ulong buffer = CurrentBuffer;
            EndRenderingBeforeIllegalCommand();

            sink.MemoryBarrier(buffer, toTransfer: true);
            sink.CopyBuffer(buffer, source.Handle, destination.Handle,
                VulkanTransferPlan.BufferRegion(srcOffsetBytes, dstOffsetBytes, sizeInBytes));
            sink.MemoryBarrier(buffer, toTransfer: false);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// EVERY MIP LEVEL AND EVERY ARRAY LAYER, one region each, which is what a whole-texture copy means and
        /// what the readback path needs when it copies a mipped render target into a staging texture. The two
        /// textures must agree on shape, because a copy that silently clipped would produce a golden that is
        /// subtly wrong rather than an error.
        /// </remarks>
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            VulkanTexture source = VulkanTexture.Require(src, "a native Vulkan texture copy");
            VulkanTexture destination = VulkanTexture.Require(dst, "a native Vulkan texture copy");
            RequireMatchingShape(source, destination);

            ulong buffer = PrepareTransfer(source, destination, VulkanTransferPlan.TrackedWhole(source),
                VulkanTransferPlan.TrackedWhole(destination), "Copying a texture");

            for (uint layer = 0; layer < source.ActualArrayLayers; layer++)
            {
                for (uint level = 0; level < source.MipLevels; level++)
                {
                    Region(buffer, source, level, layer, destination, level, layer,
                        VulkanStagingLayout.MipDimension(source.Width, level),
                        VulkanStagingLayout.MipDimension(source.Height, level), "Copying a texture");
                }
            }
        }

        /// <inheritdoc/>
        /// <remarks>The mip-0, layer-0 destination form, which is what reading one level of a texture array back
        /// to the CPU is.</remarks>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint width, uint height)
            => CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, 0, 0, width, height);

        /// <inheritdoc/>
        /// <remarks>
        /// THE GENERAL FORM: one mip level and one array layer on each side. Its own use is seeding the base level
        /// of a MIPPED texture from a single-mip one written by compute, because a storage-image binding must cover
        /// exactly one mip level, so a compute-written map that also needs a chain has to be two textures with a
        /// copy between them.
        /// <para>
        /// THE TRANSITION COVERS ONE LEVEL AND ONE LAYER, not the whole texture, which is what lets a mip chain
        /// mid-generation stay trackable: the tracker refuses two PARTIALLY OVERLAPPING ranges and answers
        /// disjoint ones, and a per-subresource range is disjoint from every other.
        /// </para>
        /// <para>
        /// ON THE SECOND AND EVERY LATER RECORDING OF A CHAIN INTO ONE LIST it is not disjoint from every other,
        /// and that shape is answered too. <see cref="GenerateMipmaps"/> names mip 0 over every layer, which
        /// collapses the per-layer entries these copies left into one wider entry, so the next round of copies
        /// asks for one layer of a mip the tracker now holds whole. The tracker transitions the entry it holds.
        /// </para>
        /// </remarks>
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        {
            VulkanTexture source = VulkanTexture.Require(src, "a native Vulkan subresource copy");
            VulkanTexture destination = VulkanTexture.Require(dst, "a native Vulkan subresource copy");

            ulong buffer = PrepareTransfer(source, destination,
                VulkanTransferPlan.Tracked(source, srcMipLevel, 1, srcArrayLayer, 1),
                VulkanTransferPlan.Tracked(destination, dstMipLevel, 1, dstArrayLayer, 1),
                "Copying a texture subresource");

            Region(buffer, source, srcMipLevel, srcArrayLayer, destination, dstMipLevel, dstArrayLayer,
                width, height, "Copying a texture subresource");
        }

        /// <inheritdoc/>
        /// <remarks>
        /// THE MIP CHAIN AS A BLIT CHAIN, one <c>vkCmdBlitImage</c> per level, each halving the previous one and
        /// each covering every array layer at once. The layout dance is the chain's whole subtlety: level N-1 goes
        /// to <c>TRANSFER_SRC_OPTIMAL</c> and level N to <c>TRANSFER_DST_OPTIMAL</c>, so the two ranges are
        /// DISJOINT at every step, which is exactly the shape <see cref="VulkanLayoutTracker"/> answers per level.
        /// The whole-chain sampled bind that follows then contains every one of those per-level entries, which the
        /// tracker answers with one barrier per piece and collapses into one entry.
        /// </remarks>
        public void GenerateMipmaps(IGpuTexture texture)
        {
            VulkanTexture source = VulkanTexture.Require(texture, "a native Vulkan mip generation");
            IVulkanTransferSink sink = RequireTransfers("Generating mipmaps");
            RequireMipChain(source);

            ulong buffer = CurrentBuffer;
            EndRenderingBeforeIllegalCommand();

            uint layers = source.ActualArrayLayers;
            uint width = source.Width;
            uint height = source.Height;

            for (uint level = 1; level < source.MipLevels; level++)
            {
                _layouts?.TransitionTo(buffer, VulkanTransferPlan.Tracked(source, level - 1, 1, 0, layers),
                    ImageLayout.TransferSrcOptimal);
                _layouts?.TransitionTo(buffer, VulkanTransferPlan.Tracked(source, level, 1, 0, layers),
                    ImageLayout.TransferDstOptimal);

                // LINEAR, which is what averages four texels into one and is what the incumbent selects for every
                // format this seam can express. A mip chain is a colour texture by construction: the usage bit
                // that reaches here is GenerateMipmaps, which VulkanViewPolicy only grants a sampled view.
                sink.BlitImage(buffer, source.Image,
                    VulkanTransferPlan.MipBlit(level, width, height, layers), linear: true);

                width = VulkanTransferPlan.NextMip(width);
                height = VulkanTransferPlan.NextMip(height);
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <c>vkCmdResolveImage</c> AT MIP 0 LAYER 0, OUTSIDE A RENDER PASS INSTANCE (V-C6), with both images
        /// transitioned to the transfer layouts and left there for <c>End</c> to restore (V-F7). No MSAA on the
        /// swapchain, matching the incumbent, so the destination is always a real texture.
        /// <para>
        /// AN OUT-OF-RANGE SAMPLE COUNT IS REFUSED AT TEXTURE CREATION rather than here and rather than clamped,
        /// which is C4's departure inherited for the same reason: the engine clamps upstream against
        /// <see cref="GpuCapabilities.MaxMsaaSampleCount"/> so nothing legitimate reaches the throw, and a silent
        /// MSAA downgrade presents as a golden mismatch that reads like a rendering bug.
        /// </para>
        /// </remarks>
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        {
            VulkanTexture source = VulkanTexture.Require(src, "a native Vulkan multisample resolve");
            VulkanTexture destination = VulkanTexture.Require(dst, "a native Vulkan multisample resolve");
            IVulkanTransferSink sink = RequireTransfers("Resolving a multisampled texture");
            RequireResolvable(source, destination);

            ulong buffer = CurrentBuffer;
            EndRenderingBeforeIllegalCommand();

            _layouts?.TransitionTo(buffer, VulkanTransferPlan.Tracked(source, 0, 1, 0, 1),
                ImageLayout.TransferSrcOptimal);
            _layouts?.TransitionTo(buffer, VulkanTransferPlan.Tracked(destination, 0, 1, 0, 1),
                ImageLayout.TransferDstOptimal);

            sink.ResolveImage(buffer, source.Image, destination.Image,
                VulkanTransferPlan.ResolveRegion(source.Width, source.Height, source.Plan.DepthStencil));
        }

        // THE THREE THINGS EVERY TEXTURE COPY DOES BEFORE ITS FIRST REGION: end the pass, put each side into its
        // transfer layout over exactly the range the copy touches, and answer the command buffer every region
        // names. A STAGING side is skipped, because it is a VkBuffer with no image and no layout at all (V-C7).
        ulong PrepareTransfer(VulkanTexture source, VulkanTexture destination, in VulkanTrackedImage sourceRange,
            in VulkanTrackedImage destinationRange, string what)
        {
            RequireTransfers(what);

            ulong buffer = CurrentBuffer;
            EndRenderingBeforeIllegalCommand();

            if (!source.IsStaging)
                _layouts?.TransitionTo(buffer, sourceRange, ImageLayout.TransferSrcOptimal);

            if (!destination.IsStaging)
                _layouts?.TransitionTo(buffer, destinationRange, ImageLayout.TransferDstOptimal);

            return buffer;
        }

        // ONE REGION, IN WHICHEVER OF THE FOUR SHAPES THIS PAIR IS. The staging side supplies the buffer terms out
        // of its own software layout and the image side supplies the subresource, which is the split that is easy
        // to get backwards: the row length and image height are the STAGING mip's dimensions in TEXELS and the
        // level and layer in imageSubresource are the IMAGE's, which need not be the same numbers.
        void Region(ulong buffer, VulkanTexture source, uint sourceMip, uint sourceLayer,
            VulkanTexture destination, uint destinationMip, uint destinationLayer, uint width, uint height,
            string what)
        {
            // THE CALLER'S OWN "what", threaded through rather than named again here: this member serves both
            // copy overloads and a missing-seam refusal that always said "Copying a texture" told a subresource
            // copy's caller about a member it did not call. PrepareTransfer already carries the right one.
            IVulkanTransferSink sink = RequireTransfers(what);

            switch (VulkanTransferPlan.CaseFor(source.IsStaging, destination.IsStaging))
            {
                case VulkanTransferCase.ImageToImage:
                    sink.CopyImage(buffer, source.Image, destination.Image,
                        VulkanTransferPlan.ImageRegion(sourceMip, sourceLayer, destinationMip, destinationLayer,
                            width, height, source.Plan.DepthStencil));
                    return;

                case VulkanTransferCase.ImageToBuffer:
                    sink.CopyImageToBuffer(buffer, source.Image, destination.StagingBuffer,
                        VulkanTransferPlan.BufferImageRegion(
                            VulkanStagingLayout.CopyRegion(destination.StagingShape, destinationMip,
                                destinationLayer, 0, 0, width, height),
                            sourceMip, sourceLayer, source.Plan.DepthStencil));
                    return;

                case VulkanTransferCase.BufferToImage:
                    sink.CopyBufferToImage(buffer, source.StagingBuffer, destination.Image,
                        VulkanTransferPlan.BufferImageRegion(
                            VulkanStagingLayout.CopyRegion(source.StagingShape, sourceMip, sourceLayer, 0, 0,
                                width, height),
                            destinationMip, destinationLayer, destination.Plan.DepthStencil));
                    return;

                default:
                    // BOTH SIDES ARE VkBuffers, so this is a plain byte copy between two software layouts and the
                    // subresource offsets are the whole of what it needs. Barrier-free, unlike CopyBuffer: a
                    // staging texture is only ever read through Map, which drains the timeline first (V-C8).
                    VulkanSubresourceLayout from =
                        VulkanStagingLayout.For(source.StagingShape, sourceMip, sourceLayer);
                    VulkanSubresourceLayout to =
                        VulkanStagingLayout.For(destination.StagingShape, destinationMip, destinationLayer);

                    sink.CopyBuffer(buffer, source.StagingBuffer, destination.StagingBuffer,
                        VulkanTransferPlan.BufferRegion(from.Offset, to.Offset, Math.Min(from.Size, to.Size)));
                    return;
            }
        }

        // The seam, or a named refusal for a list a test built without one.
        IVulkanTransferSink RequireTransfers(string what)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            return _transfers ?? throw new NotSupportedException(
                what + " on a native Vulkan command list needs its transfer seam, and this list was built with "
                + "none. Every list the device hands out has one: this is a list constructed directly by a "
                + "test.");
        }

        static void RequireBufferWindow(VulkanBuffer buffer, uint offsetBytes, uint sizeInBytes, string side)
        {
            // ITS OWN REFUSAL, because a zero-size copy is not a window that leaves the buffer and the
            // out-of-range message describes a mistake the caller did not make. VkBufferCopy::size must be
            // greater than zero (VUID-VkBufferCopy-size-01988), so there is nothing to narrow this to.
            if (sizeInBytes == 0)
            {
                throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                    "A native Vulkan buffer copy was asked for 0 bytes. A VkBufferCopy region's size must be "
                    + "positive, so there is no such thing as an empty copy at this level: the driver refuses the "
                    + "region rather than treating it as a no-op. Skip the call at the call site when the length "
                    + "can legitimately be zero.");
            }

            if (offsetBytes <= buffer.SizeInBytes && sizeInBytes <= buffer.SizeInBytes - offsetBytes) return;

            throw new ArgumentOutOfRangeException(nameof(sizeInBytes), sizeInBytes,
                "A native Vulkan buffer copy names " + sizeInBytes.ToString(CultureInfo.InvariantCulture)
                + " bytes at offset " + offsetBytes.ToString(CultureInfo.InvariantCulture) + " of its " + side
                + ", which is a " + buffer.Describe()
                + ". vkCmdCopyBuffer reads and writes exactly the region it is given, so a window that leaves the "
                + "buffer is a read or a write past the end of an allocation rather than a clipped copy.");
        }

        static void RequireMatchingShape(VulkanTexture source, VulkanTexture destination)
        {
            if (source.Width == destination.Width && source.Height == destination.Height
                && source.MipLevels == destination.MipLevels
                && source.ActualArrayLayers == destination.ActualArrayLayers
                && source.Format == destination.Format)
            {
                return;
            }

            throw new ArgumentException(
                "A native Vulkan whole-texture copy was asked for between a " + source.Describe() + " and a "
                + destination.Describe()
                + ", and they do not agree on width, height, mip count, array layer count and format. A whole "
                + "copy names every subresource on both sides, so a mismatch is a copy that would clip or run off "
                + "the end rather than one this backend can decide how to narrow. Use CopyTextureSubresource for "
                + "a region.",
                nameof(destination));
        }

        static void RequireMipChain(VulkanTexture texture)
        {
            if (texture.MipLevels > 1 && !texture.IsStaging && texture.Image != 0) return;

            throw new ArgumentException(
                "A native Vulkan mip generation was asked for on a " + texture.Describe()
                + ". A mip chain is generated by blitting each level down into the next, so the texture needs more "
                + "than one mip level and needs a real VkImage: a staging texture is a VkBuffer with a software "
                + "subresource layout and has no image to blit (V-C7). Create it with GpuTextureUsage."
                + nameof(GpuTextureUsage.GenerateMipmaps) + " and a mip count above 1.",
                nameof(texture));
        }

        static void RequireResolvable(VulkanTexture source, VulkanTexture destination)
        {
            if (source.SampleCount > 1 && destination.SampleCount == 1 && !source.IsStaging
                && !destination.IsStaging && source.Width == destination.Width
                && source.Height == destination.Height && source.Format == destination.Format)
            {
                return;
            }

            throw new ArgumentException(
                "A native Vulkan multisample resolve was asked for from a " + source.Describe() + " at "
                + source.SampleCount.ToString(CultureInfo.InvariantCulture) + " samples into a "
                + destination.Describe() + " at "
                + destination.SampleCount.ToString(CultureInfo.InvariantCulture)
                + ". vkCmdResolveImage averages the samples of a MULTISAMPLED image into a SINGLE-SAMPLE one of "
                + "the same width, height and format, and neither side may be a staging texture, which has no "
                + "image at all.",
                nameof(destination));
        }
    }
}
