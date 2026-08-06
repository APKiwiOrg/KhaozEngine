using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using KhaozEngine.Primitives;
using Silk.NET.Vulkan;

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
    /// submit path on the device. Row 8 (https://github.com/APKiwiOrg/KhaozEngine/issues/518) added the record-time
    /// <c>UpdateBuffer</c> on top of it. The rest of the RECORDING CONTENT belongs to four later rows and each
    /// unbuilt member throws a message naming its own. This paragraph is a ledger and a stale one is worse than
    /// none, which is the same discipline <c>VulkanGpuDevice</c>'s equivalent paragraph is under.</para>
    ///
    /// <para><b>THE MEMBERS THAT ARE LIVE:</b> <see cref="Begin"/>, <see cref="End"/> and <see cref="Dispose"/>,
    /// everything the device's <c>Submit</c> reaches through this type, both <c>UpdateBuffer</c> overloads at
    /// both ends of their routing, and all four resource-set binds. On a RING-BACKED buffer an update is a memcpy
    /// into the current segment which records nothing at all (9.2), and on any other buffer it is a staged copy
    /// through this list's own arena with a barrier narrowed to the destination's real usage, which row 9 wired
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/519). A resource-set bind RECORDS ONLY into
    /// <see cref="VulkanBindRecords"/> and issues nothing until a draw flushes it, which is row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521).</para>
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
        readonly IVulkanRecordUploads? _uploads;

        // ONE PER BIND POINT, because the seam's graphics and compute bindings are separate and Vulkan's are too.
        // Row 11's whole schedule lives in these two objects, which hold no set, no layout object and nothing that
        // reaches the descriptor pool: see VulkanBoundSet for the V-D2 obligation that shapes them.
        readonly VulkanBindRecords _graphicsBinds;
        readonly VulkanBindRecords _computeBinds;

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
        /// <param name="uploads">This list's staging arena and copy recorder (9.3), or null while there is no
        /// non-uniform buffer that could reach it. See <see cref="IVulkanRecordUploads"/> for why the list holds
        /// the interface rather than the arena, and why null is correct today rather than a placeholder.</param>
        /// <param name="assertBoundSetLayouts">Decision V-R7's draw-time half: under
        /// <c>KE_VULKAN_VALIDATION</c> the bind flush additionally asserts that every bound set's layout IS the
        /// current pipeline layout's set layout at that index. The device reads it off the instance it leased, so
        /// the assertion follows the same lever the layer itself does.</param>
        internal VulkanCommandList(VulkanCommandPoolRing ring, VulkanRetireList retired,
            IVulkanRecordUploads? uploads = null, bool assertBoundSetLayouts = false)
        {
            ArgumentNullException.ThrowIfNull(ring);
            ArgumentNullException.ThrowIfNull(retired);

            _ring = ring;
            _retired = retired;
            _uploads = uploads;
            _graphicsBinds = new VulkanBindRecords(PipelineBindPoint.Graphics, assertBoundSetLayouts);
            _computeBinds = new VulkanBindRecords(PipelineBindPoint.Compute, assertBoundSetLayouts);
        }

        /// <summary>The pools, exposed for the submit path and for tests. Every other caller names slots.</summary>
        internal VulkanCommandPoolRing Ring => _ring;

        /// <summary>
        /// THE GRAPHICS BIND SCHEDULE (V-R5 to V-R7). Exposed because row 13
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523) drives its
        /// <see cref="VulkanBindRecords.SetPipelineLayout"/> from <c>SetPipeline</c>, and because the device-free
        /// tests drive the whole schedule through it before either <c>SetPipeline</c> or the draw members exist.
        /// </summary>
        internal VulkanBindRecords GraphicsBinds => _graphicsBinds;

        /// <summary>The compute bind schedule. Separate records, separate <c>VkPipelineLayout</c>, separate
        /// flush.</summary>
        internal VulkanBindRecords ComputeBinds => _computeBinds;

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
        /// THE RECORDER STATE RESET GOES HERE. Section 6.1 lists what a <c>Begin</c> resets on this backend: the
        /// framebuffer, both pipelines, both dirty arrays, the scissor, and the list-local layout map. Row 11
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521) added the two dirty arrays and, with them, the
        /// pipeline LAYOUT each is bound under. The framebuffer and the scissor are row 12's
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522) and the layout map is row 14's
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

            int slot = _ring.Advance();

            // THE STAGING ARENA OPENS THE SLOT THE RING JUST ADVANCED ONTO (9.3), which is the one boundary at
            // which its blocks are provably finished with: Advance waited for that slot's last submission before it
            // reset the pool, and the blocks being handed back are the ones that slot filled the previous time
            // round. Recycling the WHOLE arena here would hand back the blocks the last record's submission is
            // still reading.
            _uploads?.BeginSlot(slot);

            // ROWS 12 AND 14 RESET THEIR RECORDER STATE HERE TOO, between the native begin and the flag. See the
            // remarks above for the full list and for why this is the only correct place for it.
            //
            // ROW 11's HALF: a fresh VkCommandBuffer has no descriptor set and no pipeline bound, so both records
            // forget their slots AND the pipeline layout they were bound under. Keeping either would let the first
            // flush of the next recording skip a bind as clean against state that lives on another buffer.
            _graphicsBinds.Reset();
            _computeBinds.Reset();

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
        /// <remarks>
        /// STILL REFUSES, AND ROW 13 (https://github.com/APKiwiOrg/KhaozEngine/issues/523) IS WHERE IT LANDS,
        /// because a pipeline is what carries the <c>VkPipelineLayout</c> this row's compatibility prefix compares.
        /// The prefix computation and both of its guards are already here: row 13's <c>SetPipeline</c> emits
        /// <c>vkCmdBindPipeline</c> and then calls
        /// <see cref="VulkanBindRecords.SetPipelineLayout"/> on <see cref="GraphicsBinds"/> with the pipeline's own
        /// layout handle and set-layout sequence, which invalidates the recorded slots from the first incompatible
        /// set onward. Nothing else about clause 4 is left to write.
        /// </remarks>
        public void SetPipeline(IGpuPipeline p) => throw NotBuiltYet("Binding a graphics pipeline", PipelineRow);

        /// <inheritdoc/>
        /// <remarks>
        /// CLAUSE 1, RECORD ONLY (V-R5). No native call and no descriptor work: the slot's record takes the set's
        /// handle, its layout handle and its dynamic uniform array, and goes dirty if either the set or the offset
        /// moved. The bind itself happens at the next draw, one <c>vkCmdBindDescriptorSets</c> per contiguous run
        /// of dirty slots.
        /// <para>
        /// A RECORD MADE OUTSIDE A RECORDING IS DISCARDED RATHER THAN REFUSED, which is the same answer
        /// <see cref="UpdateBuffer{T}(IGpuBuffer,uint,in T)"/> gives and is safe for a different reason:
        /// <see cref="Begin"/> resets both records, so a bind made before one cannot leak into the recording that
        /// follows.
        /// </para>
        /// </remarks>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => Bind(_graphicsBinds, slot, set, 0);

        /// <inheritdoc/>
        /// <remarks>The same record, carrying the caller's per-draw byte offset, which the flush adds on top of the
        /// ring base for the ONE element the layout declared dynamic (V-D4).</remarks>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => Bind(_graphicsBinds, slot, set, dynamicOffset);

        /// <inheritdoc/>
        /// <remarks>Row 13's (https://github.com/APKiwiOrg/KhaozEngine/issues/523), on the compute arm, and the
        /// same shape as <see cref="SetPipeline"/>: it calls
        /// <see cref="VulkanBindRecords.SetPipelineLayout"/> on <see cref="ComputeBinds"/>.</remarks>
        public void SetComputePipeline(IGpuComputePipeline p)
            => throw NotBuiltYet("Binding a compute pipeline", PipelineRow);

        /// <inheritdoc/>
        /// <remarks>The compute arm of <see cref="SetGraphicsResourceSet(uint,IGpuResourceSet)"/>, into its own
        /// records: a graphics bind does not feed a dispatch and this does not feed a draw.</remarks>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => Bind(_computeBinds, slot, set, 0);

        /// <inheritdoc/>
        /// <remarks>The compute arm of the offset-carrying overload.</remarks>
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => Bind(_computeBinds, slot, set, dynamicOffset);

        /// <summary>
        /// THE PRE-COMMAND HOOK, GRAPHICS ARM (clause 2). Row 15
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) calls this FIRST in <c>Draw</c> and
        /// <c>DrawIndexed</c>, before the vertex and index binds and before the draw itself, then issues. It emits
        /// one <c>vkCmdBindDescriptorSets</c> per contiguous run of dirty slots and leaves every slot clean.
        /// <para>
        /// GENERIC OVER THE SINK AND <c>ref</c> RATHER THAN <c>in</c>, so the JIT monomorphizes the seam away and
        /// no defensive copy is made per call on the per-draw path. That is V-T2's whole "generic-constrained to a
        /// struct" clause arriving at its first real caller.
        /// </para>
        /// </summary>
        internal void FlushGraphicsBinds<TSink>(ref TSink sink) where TSink : struct, IVkCmdSink
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _graphicsBinds.Flush(ref sink);
        }

        /// <summary>The compute arm, which <c>Dispatch</c> calls for the same reason and in the same
        /// place.</summary>
        internal void FlushComputeBinds<TSink>(ref TSink sink) where TSink : struct, IVkCmdSink
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _computeBinds.Flush(ref sink);
        }

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
        /// <remarks>The single-value overload, which is what every shipped renderer's per-draw uniform write is.
        /// Same routing as the span overload below.</remarks>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged
            => Upload(b, offsetBytes,
                MemoryMarshal.AsBytes(
                    MemoryMarshal.CreateReadOnlySpan(ref Unsafe.AsRef(in data), 1)));

        /// <inheritdoc/>
        /// <remarks>
        /// THE RECORD-TIME WRITE, AND THE ONE ROUTING DECISION BOTH LEVELS MAKE (9.2, 9.3). A ring-backed uniform
        /// buffer takes a <c>memcpy</c> into the CURRENT segment and records nothing at all: no staging buffer, no
        /// <c>vkCmdCopyBuffer</c>, no barrier and NO RENDER-PASS SPLIT. Everything else stages through this list's
        /// arena and records a copy plus a narrowed barrier, with the pass split those copies unavoidably cause.
        /// <para>
        /// CURRENT-SEGMENT ONLY, deliberately, and the DEVICE-level <c>UpdateBuffer</c> is the other shape: it
        /// reaches every segment so a value written once persists for the buffer's life (V-M8). The split is the
        /// CALL rather than a usage hint on the buffer, because the call is what knows whether it happens once.
        /// Every shipped record-time uniform write is unconditional per frame, so replicating those would be
        /// <c>FramesInFlight</c> memcpys for a value the next frame overwrites, on the hot path.
        /// </para>
        /// <para>
        /// BOTH LEGS ARE LIVE since <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/519">row 9</see>,
        /// which built the buffers and the uploader behind the arena leg. What still refuses here is a buffer from
        /// ANOTHER backend, which holds no <c>VkBuffer</c> to copy into. See
        /// <see cref="IVulkanRecordUploads"/> and <see cref="VulkanListUploads"/>.
        /// </para>
        /// </remarks>
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged
            => Upload(b, offsetBytes, MemoryMarshal.AsBytes(data));

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
        /// THE STAGING ARENA DISPOSES ALONGSIDE THE POOLS, deferred the same way and for the same reason: an
        /// in-flight submission can still be reading a block this list's arena filled, exactly as it can still be
        /// reading a command buffer from this list's pool. <see cref="IVulkanRecordUploads"/> is
        /// <see cref="IDisposable"/> so row 9's <see cref="VulkanListUploads"/>, which wraps
        /// <see cref="VulkanStagingArena"/>, has an owner for that arena's lifetime. See <see cref="IVulkanStagingSource.Destroy"/> for the deferral
        /// contract the native free must satisfy on the far side of it.
        /// </para>
        /// <para>
        /// DISPOSING MID-RECORDING IS LEGAL AND ENDS NOTHING. <c>vkDestroyCommandPool</c> frees every buffer
        /// allocated from the pool whatever state it is in, so sealing a record nobody will submit would be a
        /// native call bought for nothing. What it does mean is that the recording is discarded, which is what
        /// disposing a list mid-record asks for.
        /// </para>
        /// <para>
        /// IDEMPOTENT, because a consumer disposing a list twice is a teardown-order accident rather than a
        /// defect, and retiring the same pools twice would double-destroy them. The same guard covers the arena:
        /// <c>_uploads?.Dispose()</c> is only reached once, on the first Dispose.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _recording = false;
            _sealedSlot = NoSeal;

            _ring.RetireInto(_retired);
            _uploads?.Dispose();
        }

        // THE RECORD ITSELF, in ONE place rather than at each of the four overloads, so the two arms and the two
        // offset shapes cannot drift apart by an edit to one of them. Everything it does is a type check and a
        // compare-and-store: see VulkanBindRecords for why that is the whole of a bind at record time.
        void Bind(VulkanBindRecords records, uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            // NULL IS A LEGAL RECORD AND NOT A CALLER ERROR (clause 5). It says the slot holds no set, and the
            // flush skips it rather than unbinding: a descriptor slot nothing reads costs nothing to leave.
            records.Record(slot, set is null ? null : VulkanResourceSet.Require(set, "a native Vulkan bind"),
                dynamicOffset);
        }

        // THE ROUTING ITSELF, in ONE place rather than at each of the two overloads, so a uniform write and a bulk
        // write cannot drift apart by an edit to one of them.
        void Upload(IGpuBuffer buffer, uint offsetBytes, ReadOnlySpan<byte> data)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            ArgumentNullException.ThrowIfNull(buffer);

            if (buffer is IVulkanRingBacked { Ring: { } ring })
            {
                ring.Write(offsetBytes, data);
                return;
            }

            if (_uploads is null || buffer is not IVulkanUploadDestination destination)
            {
                throw new ArgumentException(
                    "That buffer cannot be written by a native Vulkan command list: it holds no VkBuffer to copy "
                    + "into, which means it was created by another GPU backend. Create buffers through the device "
                    + "you record against. (A list built with no staging arena reaches this too, which is only a "
                    + "list constructed by a test rather than by the device.)", nameof(buffer));
            }

            _uploads.Upload(destination, offsetBytes, data);
        }

        // The row that owns each unbuilt member, as a full URL, because these messages are read by somebody who
        // has just hit one and needs to know whether to wait for a row or file a bug.
        const string RenderingRow =
            "the dynamic-rendering row (https://github.com/APKiwiOrg/KhaozEngine/issues/522)";
        const string PipelineRow = "the pipeline row (https://github.com/APKiwiOrg/KhaozEngine/issues/523)";
        const string DrawRow = "the draw-and-dispatch row (https://github.com/APKiwiOrg/KhaozEngine/issues/525)";
        const string ResourcesRow = "the resources row (https://github.com/APKiwiOrg/KhaozEngine/issues/519)";

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
