using System;
using KhaozEngine.Primitives;

namespace KhaozEngine.Gpu.Vulkan.Internal
{
    /// <summary>
    /// The seam's <see cref="IGpuCommandList"/> on the native Vulkan backend: a <see cref="VulkanCommandPoolRing"/>
    /// and a recording state machine over it, recording into a real <c>VkCommandBuffer</c> at RECORD TIME.
    ///
    /// <para><b>THERE IS NO OP STREAM, AND THAT IS A DECISION RATHER THAN AN OMISSION (V-R1).</b> No second
    /// driver, no <c>KE_VULKAN_RECORD</c> and no A/B. Phase 2's own section 16 predicted it: the CPU op stream on
    /// the Direct3D 11 backend is an adapter for an API whose immediate context has no usable deferred recording,
    /// and a <c>VkCommandBuffer</c> between <c>vkBeginCommandBuffer</c> and <c>vkEndCommandBuffer</c> IS an
    /// engine-invisible op stream that the driver encodes into its own format. Recording into a managed array
    /// first would encode twice, allocate once more, and move the driver-side encode inside the submit lock, which
    /// is the one serialised point in the frame. The largest unproven bet of phase 2 is simply absent here.</para>
    ///
    /// <para><b>NOT EVERY MEMBER IS BUILT YET, and each one that is not names the row that builds it.</b> This is
    /// work-breakdown row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/517), which owns the list's
    /// LIFECYCLE: the pools, the slot machinery, <see cref="Begin"/>, <see cref="End"/>, disposal, and the
    /// submit path on the device. The RECORDING CONTENT belongs to four later rows and each unbuilt member throws
    /// a message naming its own. This paragraph is a ledger and a stale one is worse than none, which is the same
    /// discipline <c>VulkanGpuDevice</c>'s equivalent paragraph is under.</para>
    ///
    /// <para><b>THE MEMBERS THAT ARE LIVE:</b> <see cref="Begin"/>, <see cref="End"/> and <see cref="Dispose"/>,
    /// plus everything the device's <c>Submit</c> reaches through this type.</para>
    ///
    /// <para><b>N LISTS RECORD CONCURRENTLY ON THIS BACKEND, AND THE PORTABLE CONTRACT IS UNCHANGED (V-R4).</b>
    /// The seam documents exactly one open recording per device, and that rule is what portable code is written
    /// against. This backend is more permissive as a BACKEND PROPERTY: a <c>VkCommandPool</c> and its buffers are
    /// externally synchronised one thread at a time, per-list pools mean two lists on two threads never touch the
    /// same pool, and layout tracking is LIST-LOCAL (V-F7, row 14), so nothing shared is read or written during
    /// recording at all. That holds for a reason a reader has to KNOW rather than one they can see, which is why
    /// it is written here and in the package README rather than left to be inferred. It is not a promise of the
    /// interface and code that relies on it does not port.</para>
    ///
    /// <para><b>THIS TYPE TRACKS NO OPEN-RECORDING COUNT, deliberately, and neither does the device.</b> The
    /// native Direct3D 11 backend does not either. A per-device counter would enforce a portable rule that this
    /// backend does not need and that its own layout model eliminates, and enforcing it here would make a
    /// legal-on-this-backend program throw on this backend only. What a reader must not do is read
    /// <c>OpenListTrackingGpuDevice</c> passing on the Vulkan leg as evidence ABOUT this backend: under V-F7 it
    /// passes trivially, exactly as it does on the Direct3D 11 native leg, and section 2.5 names that as the
    /// decision's own decay mode.</para>
    ///
    /// <para><b>ONE LIST, ONE THREAD AT A TIME.</b> Nothing in this type is synchronised, because there is nothing
    /// to synchronise against: the pools are this list's, the slot index is this list's, and the driver requires
    /// external synchronisation over both anyway. Driving ONE list from two threads is a data race here and would
    /// be one inside the driver too.</para>
    /// </summary>
    internal sealed class VulkanCommandList : IGpuCommandList
    {
        readonly VulkanCommandPoolRing _ring;
        readonly VulkanRetireList _retired;

        bool _recording;
        bool _disposed;

        // The slot End sealed, and therefore the slot Submit reads its buffer out of and writes its value back
        // into. Captured at End rather than read at submit time so that a list re-Begun before it was submitted
        // (a caller error, since Begin discards the recording) cannot make Submit name the wrong buffer.
        int _sealedSlot = NoSeal;

        const int NoSeal = -1;

        /// <param name="ring">This list's own pools. Built by the device, because the depth is the device's
        /// <see cref="VulkanFramesInFlight"/> and the backpressure accumulator it stalls into is the device's
        /// too.</param>
        /// <param name="retired">The device's deferred-disposal list, which this list's pools go to when it is
        /// disposed with submissions outstanding.</param>
        internal VulkanCommandList(VulkanCommandPoolRing ring, VulkanRetireList retired)
        {
            ArgumentNullException.ThrowIfNull(ring);
            ArgumentNullException.ThrowIfNull(retired);

            _ring = ring;
            _retired = retired;
        }

        /// <summary>The pools, exposed for the submit path and for tests. Every other caller names slots.</summary>
        internal VulkanCommandPoolRing Ring => _ring;

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        internal bool IsRecording => _recording;

        /// <summary>True once <see cref="End"/> has sealed a record that has not been superseded by a later
        /// <see cref="Begin"/>. What the submit path requires before it will queue anything.</summary>
        internal bool IsSealed => _sealedSlot != NoSeal && !_recording;

        /// <summary>The <c>VkCommandBuffer</c> the sealed record lives in, as the submit path names it.</summary>
        /// <exception cref="InvalidOperationException">Nothing is sealed.</exception>
        internal ulong SealedBuffer => _ring.BufferAt(SealedSlot);

        /// <summary>The slot the sealed record lives in.</summary>
        /// <exception cref="InvalidOperationException">Nothing is sealed.</exception>
        internal int SealedSlot
        {
            get
            {
                if (!IsSealed)
                {
                    throw new InvalidOperationException(
                        "A native Vulkan command list was submitted without a sealed recording. Call Begin, "
                        + "record, then End before submitting: the seam documents that a list submitted without "
                        + "End is a half-recorded frame and that a backend is free to refuse it, and this backend "
                        + "does, because a VkCommandBuffer that vkEndCommandBuffer never saw cannot legally be "
                        + "named in a vkQueueSubmit at all.");
                }

                return _sealedSlot;
            }
        }

        /// <summary>
        /// Record that the sealed slot's buffer went to the queue at timeline value <paramref name="value"/>, so
        /// the next <see cref="Begin"/> that wraps onto it waits for that submission rather than resetting a pool
        /// the GPU is still reading. Called by <see cref="VulkanSubmitQueue"/> inside its submit lock.
        /// </summary>
        internal void RecordSubmitted(ulong value) => _ring.RecordSubmitted(SealedSlot, value);

        /// <inheritdoc/>
        /// <remarks>
        /// Advances to this list's next pool slot, WAITS for that slot's last submission to complete (counted as
        /// backpressure only when it actually blocks, MV3), resets the whole pool with <c>vkResetCommandPool</c>,
        /// and begins its buffer with <c>ONE_TIME_SUBMIT</c>.
        /// <para>
        /// A SECOND <c>Begin</c> WITHOUT AN <c>End</c> IS REFUSED rather than silently restarting the recording.
        /// The driver would refuse it too (a command buffer already in the recording state may not be begun
        /// again), and refusing here names the sequencing error instead of surfacing it as a bare
        /// <c>VK_ERROR</c> from a call the caller did not make. It is also the one shape a validation layer
        /// reports at the NEXT call rather than at this one.
        /// </para>
        /// <para>
        /// THE RECORDER STATE RESET GOES HERE, and today there is none to do. Section 6.1 lists what a
        /// <c>Begin</c> resets on this backend: the framebuffer, both pipelines, both dirty arrays, the scissor,
        /// and the list-local layout map. Not one of those exists yet, because they are created by rows 11
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521), 12
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522) and 14
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/524). Each of those rows adds its reset immediately
        /// after the native calls below and before the recording flag flips, and this paragraph is the hook they
        /// are looking for. A reset added anywhere else is a reset that a re-Begun list can be observed without.
        /// </para>
        /// </remarks>
        public void Begin()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recording)
            {
                throw new InvalidOperationException(
                    "Begin was called on a native Vulkan command list that is already recording. Call End first. "
                    + "A second Begin cannot restart the recording: the VkCommandBuffer is already in the "
                    + "recording state, so the driver would refuse it, and the pool cannot be reset underneath a "
                    + "record that has not been sealed.");
            }

            // Cleared BEFORE Advance, not after: a native throw mid-advance must not leave a stale record marked
            // sealed.
            _sealedSlot = NoSeal;

            _ring.Advance();

            // ROWS 11, 12 AND 14 RESET THEIR RECORDER STATE HERE, between the native begin and the flag. See the
            // remarks above for the full list and for why this is the only correct place for it.

            _recording = true;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <c>vkEndCommandBuffer</c>, and the seal that makes the record submittable.
        /// <para>
        /// THE RESTING-LAYOUT RESTORE GOES HERE, and today there are no layouts to restore. Under V-F7 every
        /// texture has a canonical resting layout assigned at creation, a list tracks its transitions LOCALLY, and
        /// <c>End</c> restores every texture it touched to rest before sealing, which is what makes two lists
        /// composable in any submit order. Row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/524) owns the
        /// tracker and adds that restore immediately BEFORE the native end below. It has to be before: a barrier
        /// recorded after <c>vkEndCommandBuffer</c> is a call against a sealed buffer.
        /// </para>
        /// <para>
        /// AN <c>End</c> WITHOUT A <c>Begin</c> IS REFUSED, including a second <c>End</c> on an already sealed
        /// list, and the message says which of the two happened.
        /// </para>
        /// </remarks>
        public void End()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_recording)
            {
                throw new InvalidOperationException(_sealedSlot == NoSeal
                    ? "End was called on a native Vulkan command list that is not recording. Call Begin first."
                    : "End was called twice on a native Vulkan command list. The recording is already sealed and "
                        + "ready to submit, and a second vkEndCommandBuffer on a buffer in the executable state "
                        + "is a call the driver refuses.");
            }

            // ROW 14 RESTORES EVERY TOUCHED TEXTURE TO ITS RESTING LAYOUT HERE, before the native end. See the
            // remarks above for why the order is fixed.

            int slot = _ring.Slot;
            _ring.EndRecording(slot);

            _recording = false;
            _sealedSlot = slot;
        }

        /// <inheritdoc/>
        public void SetFramebuffer(IGpuFramebuffer fb) => throw NotBuiltYet("Binding a framebuffer", RenderingRow);

        /// <inheritdoc/>
        public void ClearColorTarget(uint index, Color rgba)
            => throw NotBuiltYet("Clearing a colour target", RenderingRow);

        /// <inheritdoc/>
        public void ClearDepthStencil(float depth)
            => throw NotBuiltYet("Clearing the depth attachment", RenderingRow);

        /// <inheritdoc/>
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
            => throw NotBuiltYet("Setting a scissor rect", RenderingRow);

        /// <inheritdoc/>
        public void SetFullScissorRects() => throw NotBuiltYet("Resetting the scissor rects", RenderingRow);

        /// <inheritdoc/>
        public void SetPipeline(IGpuPipeline p) => throw NotBuiltYet("Binding a graphics pipeline", BindRow);

        /// <inheritdoc/>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
            => throw NotBuiltYet("Binding a graphics resource set", BindRow);

        /// <inheritdoc/>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => throw NotBuiltYet("Binding a graphics resource set with a dynamic offset", BindRow);

        /// <inheritdoc/>
        public void SetComputePipeline(IGpuComputePipeline p)
            => throw NotBuiltYet("Binding a compute pipeline", BindRow);

        /// <inheritdoc/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set)
            => throw NotBuiltYet("Binding a compute resource set", BindRow);

        /// <inheritdoc/>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => throw NotBuiltYet("Binding a compute resource set with a dynamic offset", BindRow);

        /// <inheritdoc/>
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => throw NotBuiltYet("Binding a vertex buffer", DrawRow);

        /// <inheritdoc/>
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes)
            => throw NotBuiltYet("Binding a vertex buffer at an offset", DrawRow);

        /// <inheritdoc/>
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt)
            => throw NotBuiltYet("Binding an index buffer", DrawRow);

        /// <inheritdoc/>
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
            => throw NotBuiltYet("Drawing", DrawRow);

        /// <inheritdoc/>
        public void Draw(uint vertexCount) => throw NotBuiltYet("Drawing", DrawRow);

        /// <inheritdoc/>
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart)
            => throw NotBuiltYet("Drawing indexed", DrawRow);

        /// <inheritdoc/>
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
            => throw NotBuiltYet("Dispatching compute", DrawRow);

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => throw NotBuiltYet("Uploading to a buffer at record time", RingRow);

        /// <inheritdoc/>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => throw NotBuiltYet("Uploading to a buffer at record time", RingRow);

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

        /// <summary>
        /// Release this list's pools, deferred behind the timeline (V-F9). A list disposed with submissions
        /// outstanding cannot destroy its pools, so it hands them to the device's retire list at the highest value
        /// any of its slots was submitted at, and they are destroyed once the counter passes it.
        /// <para>
        /// NO REFCOUNT, unlike the incumbent, which also works. The retire list exists for resources anyway, so a
        /// second lifetime mechanism for one object type would be a second rule about when a deferred destroy is
        /// safe, and this backend deliberately has exactly one.
        /// </para>
        /// <para>
        /// DISPOSING MID-RECORDING IS LEGAL AND ENDS NOTHING. <c>vkDestroyCommandPool</c> frees every buffer
        /// allocated from the pool whatever state it is in, so sealing a record nobody will submit would be a
        /// native call bought for nothing. What it does mean is that the recording is discarded, which is what
        /// disposing a list mid-record asks for.
        /// </para>
        /// <para>
        /// IDEMPOTENT, because a consumer disposing a list twice is a teardown-order accident rather than a
        /// defect, and retiring the same pools twice would double-destroy them.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _recording = false;
            _sealedSlot = NoSeal;

            _ring.RetireInto(_retired);
        }

        // The row that owns each unbuilt member, as a full URL, because these messages are read by somebody who
        // has just hit one and needs to know whether to wait for a row or file a bug.
        const string RenderingRow =
            "the dynamic-rendering row (https://github.com/APKiwiOrg/KhaozEngine/issues/522)";
        const string BindRow = "the bind-flush row (https://github.com/APKiwiOrg/KhaozEngine/issues/521)";
        const string DrawRow = "the draw-and-dispatch row (https://github.com/APKiwiOrg/KhaozEngine/issues/525)";
        const string RingRow = "the uniform-ring row (https://github.com/APKiwiOrg/KhaozEngine/issues/518)";

        // Named rather than a bare NotImplementedException, and it names WHAT IS LIVE as well as what is not,
        // which is the shape VulkanGpuDevice's equivalent settled on: a reader who hits this needs to know whether
        // the backend is unfinished or their machine is wrong, and those have different answers.
        static NotSupportedException NotBuiltYet(string what, string row)
            => new($"{what} is not built yet on the native Vulkan backend: it lands in {row}. The list's "
                + "LIFECYCLE is live (work-breakdown row 7, "
                + "https://github.com/APKiwiOrg/KhaozEngine/issues/517): Begin, End, the per-slot command pools "
                + "and the submit path all work, and what is missing is the recording content. This is a "
                + "statement about the package and not about this machine. Select GpuBackendKind.Vulkan, which "
                + "goes through Veldrid, for a fully working Vulkan device.");
    }
}
