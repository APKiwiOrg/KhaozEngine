using System;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// THE RESOURCE HALF OF THE NATIVE DEVICE: the factory, the shared sampler pair, the two off-timeline upload
    /// paths and the staging <c>Map</c> and <c>Unmap</c> pairs. Work-breakdown row 9
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519), split from the seam surface for the same reason
    /// creation and submission are, and because a device that must stay under the file-size cap has room for none
    /// of the four.
    ///
    /// <para><b>EVERY DEVICE-LEVEL READ FLUSHES THE SETUP COMMAND BUFFER FIRST (V-M10), AND THAT HALF OF THE RULE
    /// IS WHAT MAKES "NO SUBMIT PER TEXTURE" TRUE WITHOUT A HOLE.</b> A render target created and immediately read
    /// back must still see cleared contents, and a design that only flushed at the next submit would leave that
    /// case reading memory nothing wrote. So both <c>Map</c> overloads flush, <c>WaitForIdle</c> flushes, and
    /// both <c>Submit</c> overloads flush.</para>
    ///
    /// <para><b><c>Map(staging, Read)</c> THEN WAITS ON THE TIMELINE'S LAST SUBMITTED VALUE, COUNTED AS A DRAIN
    /// (V-C8).</b> Direct3D 11's <c>Map(READ)</c> on the immediate context blocks until the resource is ready BY
    /// DEFINITION, so this is where Vulkan has to be explicit about something the other API did implicitly.
    /// Getting it wrong returns a pointer to bytes the copy has not written yet, which reads as an intermittently
    /// wrong golden rather than as a failure. A WRITE map does not wait, matching the incumbent: nothing on this
    /// backend reads a staging resource except a copy the caller ordered, and the seam's own contract for an
    /// off-timeline write is that it lands when you call it.</para>
    ///
    /// <para><b>THE MAP PAIR TAKES THE SUBMIT LOCK FOR THE MAP CALL AND NOTHING LONGER (11.4).</b> The flush and
    /// the drain both happen BEFORE it, so the lock covers the bookkeeping, the invalidate and the pointer
    /// arithmetic, which is microseconds. Holding it across the drain would serialise every submit in the process
    /// behind one readback.</para>
    /// </summary>
    internal sealed unsafe partial class VulkanGpuDevice
    {
        /// <inheritdoc/>
        /// <remarks>Live from this row. Buffers, textures, samplers, command lists and fences are real, and the
        /// members later rows own refuse by naming their own issue.</remarks>
        public IGpuResourceFactory Factory => _factory;

        /// <inheritdoc/>
        /// <remarks>WRAP on all three axes, built from <see cref="VulkanSharedSamplers.Point"/> and NOT from the
        /// identically named <see cref="GpuSamplerDescription.Point"/>, which defaults every axis to clamp.
        /// Device-owned: disposing what this returns destroys nothing.</remarks>
        public IGpuSampler PointSampler => _pointSampler;

        /// <inheritdoc/>
        /// <remarks>WRAP on all three axes, for the reason <see cref="PointSampler"/> gives.</remarks>
        public IGpuSampler LinearSampler => _linearSampler;

        /// <inheritdoc/>
        /// <remarks>Mip 0, layer 0, which is what this overload means everywhere else on the seam.</remarks>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height)
            => UpdateTexture(texture, data, x, y, width, height, 0, 0);

        /// <inheritdoc/>
        /// <remarks>
        /// THE DEVICE-OWNED STAGING POOL, WHICH IS WHAT OFF-TIMELINE MEANS FOR A TEXTURE (9.3). The bytes go into
        /// the device's own arena and a <c>vkCmdCopyBufferToImage</c> is APPENDED to the setup command buffer,
        /// between a pair of barriers that take the target subresource into <c>TRANSFER_DST_OPTIMAL</c> and back to
        /// its resting layout. No queue submit happens here.
        /// <para>
        /// A STAGING TEXTURE IS WRITTEN DIRECTLY INSTEAD, through its own persistent mapping and its own software
        /// subresource layout, because it has no image to copy into and its memory is host-visible by construction.
        /// That is what the incumbent does too, and refusing it here would be a divergence bought for nothing.
        /// </para>
        /// </remarks>
        public void UpdateTexture(IGpuTexture texture, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(data);

            VulkanTexture target = Require(texture, "uploaded to");

            if (target.IsStaging)
            {
                WriteStagingTexture(target, data, x, y, width, height, mipLevel, arrayLayer);
                return;
            }

            _setup.Upload(
                new VulkanImageUpload(target.Image, target.Plan.DepthStencil, mipLevel, arrayLayer, x, y, width,
                    height, target.Format, target.Resting),
                data);
        }

        /// <inheritdoc/>
        public MappedData Map(IGpuTexture staging, GpuMapMode mode)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            VulkanTexture target = RequireMappable(staging);
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(
                target.StagingShape, VulkanStagingMaps.Subresource, VulkanStagingMaps.Subresource);

            DrainBeforeRead(mode);

            lock (_submitLock)
            {
                _maps.Open(target, mode);

                // Free and skipped on a coherent memory type, which is the ordinary case. It is real work on the
                // CACHED type the readback ladder prefers, and that rung is exactly why row 6's invalidate is code
                // rather than a defensive branch.
                if (VulkanStagingMaps.Reads(mode)) target.Allocation.Invalidate(layout.Offset, layout.Size);

                return VulkanStagingMaps.ForTexture(target.MappedPointer, layout);
            }
        }

        /// <inheritdoc/>
        public void Unmap(IGpuTexture staging)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            VulkanTexture target = RequireMappable(staging);
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(
                target.StagingShape, VulkanStagingMaps.Subresource, VulkanStagingMaps.Subresource);

            lock (_submitLock)
            {
                if (VulkanStagingMaps.Writes(_maps.Close(target)))
                {
                    target.Allocation.Flush(layout.Offset, layout.Size);
                }
            }
        }

        /// <inheritdoc/>
        public MappedData Map(IGpuBuffer staging, GpuMapMode mode)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            VulkanBuffer target = RequireMappable(staging);

            DrainBeforeRead(mode);

            lock (_submitLock)
            {
                _maps.Open(target, mode);

                if (VulkanStagingMaps.Reads(mode)) target.Allocation.Invalidate(0, target.SizeInBytes);

                return VulkanStagingMaps.ForBuffer(target.MappedPointer, target.SizeInBytes);
            }
        }

        /// <inheritdoc/>
        public void Unmap(IGpuBuffer staging)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            VulkanBuffer target = RequireMappable(staging);

            lock (_submitLock)
            {
                if (VulkanStagingMaps.Writes(_maps.Close(target))) target.Allocation.Flush(0, target.SizeInBytes);
            }
        }

        /// <summary>
        /// Flush the device-owned setup command buffer if it has anything open (V-M10). Called at every submit and
        /// at every device-level read, and a no-op with nothing pending, which is every frame boundary after a
        /// load.
        /// </summary>
        internal void FlushSetup()
        {
            if (_liveness.IsDead) return;

            _setup.Flush();
        }

        // THE ROUTING, in ONE place rather than at each of the three overloads, and the mirror of the command
        // list's: a ring-backed buffer takes the every-segment off-timeline write, which is a memcpy and records
        // nothing, and everything else stages through the device's own arena into the setup buffer.
        void UpdateOffTimeline(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);

            if (buffer is IVulkanRingBacked { Ring: { } ring })
            {
                _rings.UpdateBuffer(ring, offsetBytes, data);
                return;
            }

            _setup.UploadBuffer(RequireUploadable(buffer), offsetBytes, data);
        }

        // V-C8's drain, and the setup flush that has to precede it: the flush queues work whose completion the
        // wait then covers, so doing them the other way round would return a pointer to bytes the clear had not
        // reached. Counted into DrainCount and DrainMs by the timeline itself.
        void DrainBeforeRead(GpuMapMode mode)
        {
            FlushSetup();

            if (!VulkanStagingMaps.Reads(mode)) return;

            _timeline.WaitForIdle();
        }

        // A staging texture's UpdateTexture: row by row into its own persistent mapping, at the software layout's
        // stride, then a flush that is free on a coherent type.
        void WriteStagingTexture(VulkanTexture target, byte[] data, uint x, uint y, uint width, uint height,
            uint mipLevel, uint arrayLayer)
        {
            VulkanSubresourceLayout layout = VulkanStagingLayout.For(target.StagingShape, mipLevel, arrayLayer);

            VulkanStagingMaps.WriteRegion(target.MappedPointer, layout,
                VulkanStagingLayout.BytesPerTexel(target.Format), x, y, width, height, data);

            target.Allocation.Flush(layout.Offset, layout.Size);
        }

        static VulkanTexture Require(IGpuTexture texture, string what)
        {
            ArgumentNullException.ThrowIfNull(texture);

            return texture as VulkanTexture
                ?? throw new ArgumentException(
                    $"A texture created by another GPU backend was {what} on the native Vulkan backend. A "
                    + $"{texture.GetType().Name} holds no VkImage and no staging VkBuffer, so there is nothing to "
                    + "write into. Create textures through the device you use them on.", nameof(texture));
        }

        static VulkanTexture RequireMappable(IGpuTexture staging)
        {
            VulkanTexture target = Require(staging, "mapped");

            if (target.IsStaging) return target;

            throw new ArgumentException(
                "That texture was not created with GpuTextureUsage.Staging, so it has no host-visible memory to "
                + "map: it is a device-local VkImage, and an image cannot be mapped at all on this backend. Copy "
                + "into a staging texture and map that, which is what GpuReadback.ToRgba does.", nameof(staging));
        }

        static VulkanBuffer RequireMappable(IGpuBuffer staging)
        {
            ArgumentNullException.ThrowIfNull(staging);

            if (staging is not VulkanBuffer target)
            {
                throw new ArgumentException(
                    "A buffer created by another GPU backend was mapped on the native Vulkan backend. A "
                    + $"{staging.GetType().Name} holds no VkBuffer, so there is nothing to map. Create buffers "
                    + "through the device you use them on.", nameof(staging));
            }

            if (target.IsStaging) return target;

            throw new ArgumentException(
                "That buffer was not created with GpuBufferUsage.Staging, so it has no host-visible memory to "
                + "map: it lives in device-local memory. GpuBufferUsage.Dynamic does not make a buffer mappable "
                + "here either, unlike on the Veldrid leg, because the only dynamic buffers this engine creates "
                + "are uniform buffers and those are ring-backed and written without a map at all. Copy into a "
                + "staging buffer and map that, which is what GpuReadback.ReadBuffer does.", nameof(staging));
        }

        static IVulkanUploadDestination RequireUploadable(IGpuBuffer buffer)
            => buffer as IVulkanUploadDestination
                ?? throw new ArgumentException(
                    "A buffer created by another GPU backend was written through the native Vulkan backend's "
                    + $"UpdateBuffer. A {buffer.GetType().Name} holds no VkBuffer, so there is nothing to copy "
                    + "into. Create buffers through the device you write them on.", nameof(buffer));
    }
}
