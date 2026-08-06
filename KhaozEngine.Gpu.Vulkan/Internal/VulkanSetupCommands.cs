using System;
using System.Globalization;
using Silk.NET.Vulkan;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>What a newly created image needs recorded before anything may use it.</summary>
    /// <param name="Image">The <c>VkImage</c>.</param>
    /// <param name="DepthStencil">Whether the aspect is depth rather than colour.</param>
    /// <param name="MipLevels">Mip level count, so the transitions cover the whole image.</param>
    /// <param name="ArrayLayers">The REAL array layer count, already expanded for a cubemap.</param>
    /// <param name="ClearColor">Clear to transparent black first (a colour render target).</param>
    /// <param name="ClearDepth">Clear to depth 0, stencil 0 first (a depth target).</param>
    /// <param name="Resting">The canonical resting layout this image is left in (V-F7).</param>
    internal readonly record struct VulkanImageSetup(
        ulong Image, bool DepthStencil, uint MipLevels, uint ArrayLayers, bool ClearColor, bool ClearDepth,
        VulkanRestingLayout Resting);

    /// <summary>Where a device-level <c>UpdateTexture</c> is writing.</summary>
    /// <param name="Image">The destination <c>VkImage</c>.</param>
    /// <param name="DepthStencil">Whether the aspect is depth rather than colour.</param>
    /// <param name="MipLevel">Destination mip level.</param>
    /// <param name="ArrayLayer">Destination array layer.</param>
    /// <param name="X">Left edge of the written rectangle.</param>
    /// <param name="Y">Top edge.</param>
    /// <param name="Width">Rectangle width in texels.</param>
    /// <param name="Height">Rectangle height in texels.</param>
    /// <param name="Format">The texture's pixel format, which gives the payload's row pitch.</param>
    /// <param name="Resting">The texture's resting layout, which the upload leaves it back in.</param>
    internal readonly record struct VulkanImageUpload(
        ulong Image, bool DepthStencil, uint MipLevel, uint ArrayLayer, uint X, uint Y, uint Width, uint Height,
        GpuPixelFormat Format, VulkanRestingLayout Resting);

    /// <summary>
    /// THE DEVICE-OWNED SETUP COMMAND BUFFER of decision V-M10, and its own short lock (V-W8, 11.4). Section 9.3.
    ///
    /// <para><b>NO QUEUE SUBMIT AT TEXTURE CREATION.</b> The incumbent's texture constructor clears render targets
    /// and transitions sampled textures, and EACH of those grabs a shared pool, records one command and issues a
    /// whole <c>vkQueueSubmit</c>. Loading a scene with two hundred textures is two hundred queue submissions
    /// before a frame is drawn. Here both are appended to ONE buffer and flushed once.</para>
    ///
    /// <para><b>THE FLUSH IS LAZY, AT THE NEXT SUBMIT OR AT ANY DEVICE-LEVEL READ, AND THE READ HALF IS WHAT MAKES
    /// THE CLAIM TRUE WITHOUT A HOLE.</b> A render target created and immediately read back must still see cleared
    /// contents, and a design that only flushed at the next submit would leave that case reading memory nothing
    /// wrote. So <c>IGpuDevice.Submit</c>, both <c>Map</c> overloads and <c>WaitForIdle</c> all flush first. The
    /// clear itself is preserved deliberately, because undefined contents are not stable across runs while the
    /// goldens require stability.</para>
    ///
    /// <para><b>THE SETUP LOCK IS THE THIRD SHORT LOCK, beside the allocator's and the descriptor pool
    /// manager's.</b> A <c>VkCommandPool</c> and every buffer allocated from it are EXTERNALLY SYNCHRONISED, so
    /// two threads creating two textures may not append to one setup buffer at once. Creation stays free-threaded
    /// everywhere else and takes this lock for the append and for the flush, held for the record of one or two
    /// commands and released before creation returns.</para>
    ///
    /// <para><b>AND THE ORDER IS PINNED: SETUP LOCK FIRST, SUBMIT LOCK UNDER IT, NEVER THE REVERSE.</b> The flush
    /// holds this type's lock across <see cref="VulkanSubmitQueue.SubmitSetup"/>, which takes the device's submit
    /// lock inside it. Nothing in this backend takes the setup lock while holding the submit lock: the one path
    /// that touches both is a device <c>Submit</c>, which flushes this buffer and THEN queues the frame's list,
    /// two sequential acquisitions rather than a nested pair. That is the whole cycle argument, and
    /// <c>VulkanSetupBufferTests</c> pins the nesting by asserting from inside the submit that this lock is held.
    /// </para>
    ///
    /// <para><b>IT RIDES THE SAME SLOT MACHINERY A COMMAND LIST DOES.</b> A <see cref="VulkanCommandPoolRing"/> of
    /// the device's <see cref="VulkanFramesInFlight"/> depth, advanced once per BATCH rather than once per frame:
    /// the advance waits for that slot's own last submission before resetting its pool, so a batch never records
    /// into a buffer the GPU is still executing, and it never blocks in practice because setup batches are rare
    /// relative to frames. Reusing that type rather than writing a second one means the wait, the reset and the
    /// retirement have one implementation and one set of tests.</para>
    ///
    /// <para><b>THE STAGING ARENA IS THE DEVICE'S, WHICH IS WHAT OFF-TIMELINE MEANS (9.3).</b> A device-level
    /// <c>UpdateTexture</c> stages through it and its blocks are recycled on the SAME slot boundary the pool ring
    /// uses, so a block is handed back only after the batch that filled it has completed. That is the list arena's
    /// proof reused rather than a second rule about when staging memory is safe.</para>
    ///
    /// <para><b>DISPOSAL IS TERMINAL AND HANDS BOTH HALVES TO THE RETIRE LIST'S OWNERS.</b> The pools go to the
    /// device's retire list behind their highest submitted value, and the arena's blocks go through
    /// <see cref="IVulkanStagingSource.Destroy"/>, which defers the same way. See
    /// <see cref="VulkanTexture"/> for the depth-2 retirement discipline this backend settled on.</para>
    /// </summary>
    internal sealed class VulkanSetupCommands : IDisposable
    {
        // THE THIRD SHORT LOCK (V-W8). Held for an append or a flush, never across a creation call and never
        // across anything that could block on a caller.
        readonly object _gate;

        readonly VulkanCommandPoolRing _ring;
        readonly IVulkanSetupSink _sink;
        readonly VulkanStagingArena _arena;
        readonly VulkanSubmitQueue _submits;
        readonly IVulkanDeviceLiveness _liveness;

        int _slot = -1;
        bool _open;
        bool _disposed;

        /// <param name="ring">The setup buffer's own pools, at the device's frame depth.</param>
        /// <param name="sink">The four <c>vkCmd*</c> calls this type records.</param>
        /// <param name="arena">The DEVICE-owned staging arena a device-level upload takes its bytes from.</param>
        /// <param name="submits">The device's submit queue, whose lock this type's flush takes UNDER its
        /// own.</param>
        /// <param name="liveness">The device's liveness token: after death nothing is recorded and nothing is
        /// submitted, which is the same posture every other path in this package takes.</param>
        /// <param name="setupLock">The lock to use, or null to own one. A test passes its own so it can assert
        /// the nesting from inside the submit, which is the same reason
        /// <see cref="VulkanSubmitQueue"/> accepts one.</param>
        internal VulkanSetupCommands(VulkanCommandPoolRing ring, IVulkanSetupSink sink, VulkanStagingArena arena,
            VulkanSubmitQueue submits, IVulkanDeviceLiveness liveness, object? setupLock = null)
        {
            ArgumentNullException.ThrowIfNull(ring);
            ArgumentNullException.ThrowIfNull(sink);
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(submits);
            ArgumentNullException.ThrowIfNull(liveness);

            if (arena.Depth != ring.Depth)
            {
                throw new ArgumentException(
                    "The native Vulkan setup buffer's staging arena has "
                    + arena.Depth.ToString(CultureInfo.InvariantCulture)
                    + " slots and its command pools have "
                    + ring.Depth.ToString(CultureInfo.InvariantCulture)
                    + ". They must match, because the arena's blocks are recycled on the SAME slot boundary the "
                    + "pool ring waits at, and a mismatch would hand back blocks a submission is still reading.",
                    nameof(arena));
            }

            _ring = ring;
            _sink = sink;
            _arena = arena;
            _submits = submits;
            _liveness = liveness;
            _gate = setupLock ?? new object();
        }

        /// <summary>Whether a batch is open and unflushed. What the flush sites test before paying for a
        /// lock.</summary>
        internal bool HasPendingWork
        {
            get { lock (_gate) return _open; }
        }

        /// <summary>How many batches this buffer has flushed. A reading rather than a gate: V-M10's whole claim is
        /// that this number stays far below the texture count, and the incumbent's equivalent is one per texture
        /// created.</summary>
        internal int FlushCount { get; private set; }

        /// <summary>How many resources have been appended since construction, across every batch. Paired with
        /// <see cref="FlushCount"/>, it is the ratio V-M10 is about.</summary>
        internal int AppendCount { get; private set; }

        /// <summary>
        /// Append a newly created image's first-ever transition, and its creation-time clear when it has one
        /// (V-M10, V-F8). One or two barriers and at most one clear, and NO queue submit.
        /// </summary>
        internal void Prepare(in VulkanImageSetup setup)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            lock (_gate)
            {
                if (_liveness.IsDead) return;

                EnsureOpen();

                ulong buffer = _ring.BufferAt(_slot);
                ImageSubresourceRange range = VulkanSetupBarrier.WholeImage(
                    setup.DepthStencil, setup.MipLevels, setup.ArrayLayers);

                if (!setup.ClearColor && !setup.ClearDepth)
                {
                    // ONE BARRIER, straight from UNDEFINED to rest. The common case: a sampled texture whose
                    // pixels arrive by upload, which is every material texture in a scene.
                    Barrier(buffer, VulkanSetupBarrier.FirstUse(
                        setup.Image, range, VulkanFormats.ToImageLayout(setup.Resting)));

                    AppendCount++;
                    return;
                }

                Barrier(buffer, VulkanSetupBarrier.FirstUse(setup.Image, range, ImageLayout.TransferDstOptimal));

                if (setup.ClearColor)
                {
                    _sink.ClearColorImage(buffer, setup.Image, VulkanSetupBarrier.TransparentBlack, in range);
                }
                else
                {
                    _sink.ClearDepthStencilImage(buffer, setup.Image, VulkanSetupBarrier.ZeroDepth, in range);
                }

                Barrier(buffer, VulkanSetupBarrier.FromTransferDestination(
                    setup.Image, range, VulkanFormats.ToImageLayout(setup.Resting)));

                AppendCount++;
            }
        }

        /// <summary>
        /// Append a device-level texture upload: stage the bytes, transition the target subresource into
        /// <c>TRANSFER_DST_OPTIMAL</c>, copy, and transition it back to rest.
        /// <para>
        /// THE PAYLOAD IS TIGHTLY PACKED, which is what the seam's <c>byte[]</c> overloads document. A short array
        /// is refused by name rather than read past.
        /// </para>
        /// </summary>
        internal void Upload(in VulkanImageUpload upload, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            ulong required = VulkanStagingLayout.RequiredUploadBytes(upload.Width, upload.Height, upload.Format);
            if (required == 0) return;

            if ((ulong)data.Length < required)
            {
                throw new ArgumentException(
                    "A native Vulkan texture upload of "
                    + upload.Width.ToString(CultureInfo.InvariantCulture)
                    + " by "
                    + upload.Height.ToString(CultureInfo.InvariantCulture)
                    + " texels in "
                    + upload.Format
                    + " needs "
                    + required.ToString(CultureInfo.InvariantCulture)
                    + " tightly packed bytes and was given "
                    + data.Length.ToString(CultureInfo.InvariantCulture)
                    + ". The seam's byte[] overloads carry the region's rows with no padding between them, so a "
                    + "short array would be read past its end.", nameof(data));
            }

            lock (_gate)
            {
                if (_liveness.IsDead) return;

                EnsureOpen();

                ulong buffer = _ring.BufferAt(_slot);
                VulkanStagingLease lease = _arena.Take(required);
                lease.Write(data[..(int)required]);

                ImageSubresourceRange range = VulkanSetupBarrier.OneSubresource(
                    upload.DepthStencil, upload.MipLevel, upload.ArrayLayer);
                ImageLayout resting = VulkanFormats.ToImageLayout(upload.Resting);

                Barrier(buffer, VulkanSetupBarrier.ToTransferDestination(upload.Image, range, resting));

                VulkanBufferImageCopy region = VulkanStagingLayout.UploadRegion(
                    lease.OffsetBytes, upload.MipLevel, upload.ArrayLayer, upload.X, upload.Y, upload.Width,
                    upload.Height);

                _sink.CopyBufferToImage(buffer, lease.Buffer, upload.Image, ToVulkan(region, upload.DepthStencil));

                Barrier(buffer, VulkanSetupBarrier.FromTransferDestination(upload.Image, range, resting));

                AppendCount++;
            }
        }

        /// <summary>
        /// Append a device-level BUFFER upload: stage the bytes and record the copy, with the barrier narrowed to
        /// what actually reads the destination.
        /// <para>
        /// A RING-BACKED UNIFORM BUFFER NEVER COMES HERE. Its device-level write reaches every segment as a memcpy
        /// into persistently mapped memory and records no command at all (9.2), which is the whole contrast
        /// between the two write paths. What arrives here is a vertex, index, indirect or storage buffer, and the
        /// barrier <see cref="VulkanUploadBarrier"/> builds names the stage and access that buffer's own usage
        /// implies rather than the incumbent's one-size vertex-attribute guess.
        /// </para>
        /// </summary>
        internal void UploadBuffer(IVulkanUploadDestination destination, ulong destinationOffsetBytes,
            ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(destination);

            if (data.Length == 0) return;

            lock (_gate)
            {
                if (_liveness.IsDead) return;

                EnsureOpen();

                ulong buffer = _ring.BufferAt(_slot);
                VulkanStagingLease lease = _arena.Take((ulong)data.Length);
                lease.Write(data);

                _sink.CopyBuffer(buffer, lease.Buffer, lease.OffsetBytes, destination.DeviceBuffer,
                    destinationOffsetBytes, (ulong)data.Length);

                BufferBarrier(buffer, VulkanUploadBarrier.For(
                    destination.DeviceBuffer, destinationOffsetBytes, (ulong)data.Length,
                    destination.UploadUsage));

                AppendCount++;
            }
        }

        /// <summary>
        /// Seal the open batch and put it on the queue. A no-op with nothing open, which is the common case at
        /// every frame boundary after the first load.
        /// </summary>
        /// <returns>The timeline value the batch signals, or 0 when nothing was flushed.</returns>
        internal ulong Flush()
        {
            // NO ObjectDisposedException HERE. A flush after disposal is a teardown-order straggler rather than a
            // defect, and the same posture every Dispose on this backend takes applies: quiet and safe answers.
            if (_disposed) return 0;

            lock (_gate)
            {
                if (!_open) return 0;

                // The batch is abandoned rather than submitted on a dead device: the buffer went with the device
                // and vkQueueSubmit against it aborts the process through the loader.
                if (_liveness.IsDead)
                {
                    _open = false;
                    return 0;
                }

                _ring.EndRecording(_slot);
                _open = false;

                // THE SUBMIT LOCK IS TAKEN INSIDE THIS ONE, by SubmitSetup. See the class note for the whole
                // ordering argument.
                ulong value = _submits.SubmitSetup(_ring.BufferAt(_slot));
                if (value != 0) _ring.RecordSubmitted(_slot, value);

                FlushCount++;
                return value;
            }
        }

        /// <summary>
        /// Release the pools and the arena. Called by the device's teardown, in the window between
        /// <c>vkDeviceWaitIdle</c> and the liveness flip.
        /// <para>
        /// AN OPEN BATCH IS DISCARDED RATHER THAN FLUSHED. Teardown has already waited for the GPU, so submitting
        /// work at this point would mean waiting for it again, and the resources that batch was preparing are
        /// being destroyed in the same breath.
        /// </para>
        /// </summary>
        /// <param name="retired">The device's deferred-disposal list, which the pools are handed to behind their
        /// highest submitted value.</param>
        internal void Retire(VulkanRetireList retired)
        {
            ArgumentNullException.ThrowIfNull(retired);

            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _open = false;

                _ring.RetireInto(retired);
                _arena.Dispose();
            }
        }

        /// <summary>Disposal WITHOUT a retire list, for the tests that drive this type alone. A real device always
        /// calls <see cref="Retire"/>, because its pools have to outlive the submissions that referenced
        /// them.</summary>
        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _open = false;

                _arena.Dispose();
            }
        }

        // Called with the gate held. Opens a batch on the next slot, which waits for that slot's own last
        // submission before resetting its pool, and hands the arena the same slot so its blocks are recycled at
        // the one boundary that proves they are finished with.
        void EnsureOpen()
        {
            if (_open) return;

            _slot = _ring.Advance();
            _arena.BeginSlot(_slot);
            _open = true;
        }

        // One barrier per call, which is what a creation-time transition is. A DependencyInfo carries raw pointer
        // arrays as a matter of ABI, which is why this package is unsafe by construction rather than by choice.
        unsafe void Barrier(ulong commandBuffer, ImageMemoryBarrier2 barrier)
        {
            var dependency = new DependencyInfo(
                sType: StructureType.DependencyInfo,
                imageMemoryBarrierCount: 1,
                pImageMemoryBarriers: &barrier);

            _sink.PipelineBarrier(commandBuffer, in dependency);
        }

        // The buffer half of the same one-barrier-per-call shape.
        unsafe void BufferBarrier(ulong commandBuffer, BufferMemoryBarrier2 barrier)
        {
            var dependency = new DependencyInfo(
                sType: StructureType.DependencyInfo,
                bufferMemoryBarrierCount: 1,
                pBufferMemoryBarriers: &barrier);

            _sink.PipelineBarrier(commandBuffer, in dependency);
        }

        // The engine-shaped copy region to the real one. Kept here rather than in VulkanStagingLayout so that type
        // stays free of Silk.NET entirely and its table test can assert plain numbers.
        static BufferImageCopy ToVulkan(in VulkanBufferImageCopy region, bool depthStencil)
            => new(
                bufferOffset: region.BufferOffset,
                bufferRowLength: region.BufferRowLength,
                bufferImageHeight: region.BufferImageHeight,
                imageSubresource: new ImageSubresourceLayers(
                    VulkanFormats.ToAspect(depthStencil), region.MipLevel, region.ArrayLayer, 1),
                imageOffset: new Offset3D((int)region.X, (int)region.Y, 0),
                imageExtent: new Extent3D(region.Width, region.Height, 1));
    }
}
