using System;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// The seam's <see cref="IGpuCommandList"/> on the native Metal backend: a fresh <c>MTLCommandBuffer</c> per
    /// <see cref="Begin"/> and a <see cref="MetalEncoderScope"/> over it, encoding at RECORD TIME.
    ///
    /// <para><b>THERE IS NO OP STREAM, AND THAT IS A DECISION RATHER THAN AN OMISSION (M-R1).</b> No second
    /// driver, no <c>KE_METAL_RECORD</c> and no A/B. An <c>MTLCommandBuffer</c> between <c>-commandBuffer</c> and
    /// <c>-commit</c> IS a driver-encoded command stream and the encoders write into it directly, so a managed op
    /// stream in front of it would encode twice, allocate once more, and move the driver-side encode inside the
    /// submit lock, which is the one serialised point in the frame. Phase 2's section 16 predicted this before
    /// either phase-3 draft existed, phase 3 confirmed it, and Metal gives no new reason to revisit it. Metal's
    /// encoders ARE the deferred command buffer phase 2 had to build in managed memory.</para>
    ///
    /// <para><b>THERE IS NO COMMAND-BUFFER POOL TO RESET EITHER (M-R2), AND V-R2 DOES NOT PORT.</b> The Vulkan
    /// sibling's row 7 built a <c>VulkanCommandPoolRing</c> per list because a pool cannot be reset while its
    /// buffers are in flight. An <c>MTLCommandBuffer</c> is single-use, the queue owns its memory, and there is no
    /// reset, no pool object and no allocator to choose between, so that whole type has no occupant here. The
    /// <c>FramesInFlight</c> depth survives and lives on the uniform ring's acquire ALONE
    /// (<see cref="MetalFramesInFlight"/>), which is why <c>BackpressureStallCount</c> means one thing on this
    /// backend where it means two on Vulkan.</para>
    ///
    /// <para><b>WHAT THIS ROW OWNS IS THE LIFECYCLE, and every other member names the row that builds it.</b>
    /// This is work-breakdown row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/573): the buffer per
    /// <see cref="Begin"/>, the encoder transitions, <see cref="End"/>, the seal the submit path reads, disposal,
    /// and the two seams the later rows record through. The recording CONTENT is theirs, and
    /// <c>MetalCommandList.Unbuilt.cs</c> is the ledger of which row owns which member. That file is under the
    /// same discipline the device's is: it is a ledger, and a stale one is worse than none, so a row that fills a
    /// member deletes its entry.</para>
    ///
    /// <para><b>N LISTS RECORD CONCURRENTLY ON THIS BACKEND, AND THE PORTABLE CONTRACT IS UNCHANGED (M-R3).</b>
    /// The seam documents exactly one open recording per device, and that rule is what portable code is written
    /// against. This backend is more permissive as a BACKEND PROPERTY, and it gets there from Metal's own object
    /// model rather than from a barrier design: each list holds its own command buffer and its own encoders, and
    /// this design has NO shared record-time state at all. No layout tracker (M-H3), no barrier batch, no device
    /// state cache. It is not a promise of the interface and code that relies on it does not port. And the decay
    /// warning applies for the third time: <c>OpenListTrackingGpuDevice</c> passes trivially on this leg and is
    /// NOT evidence about this backend.</para>
    ///
    /// <para><b>ONE LIST, ONE THREAD AT A TIME.</b> Nothing in this type is synchronised, because there is
    /// nothing to synchronise against: the buffer is this list's and the encoder scope is this list's. Driving ONE
    /// list from two threads is a data race here and would be one inside the driver too. The one shared thing a
    /// list touches is the device's uncommitted-buffer counter, which is interlocked for exactly that
    /// reason.</para>
    /// </summary>
    internal sealed partial class MetalCommandList : IGpuCommandList
    {
        readonly IMetalCommandBufferSource _buffers;
        readonly MetalUncommittedBuffers _uncommitted;
        readonly MetalEncoderScope _encoders;

        // The RETAINED buffer this list holds, or Zero when it holds none. One field for both states a held
        // buffer can be in (recording, and sealed but not yet submitted) because the ownership question is the
        // same in both: this list released it or it did not.
        IntPtr _commandBuffer;

        bool _recording;
        bool _sealed;
        bool _disposed;

        /// <param name="buffers">Where a <see cref="Begin"/> gets its command buffer and where a discarded or
        /// committed one goes back. See <see cref="IMetalCommandBufferSource"/>.</param>
        /// <param name="uncommitted">The device's one uncommitted-buffer counter, which section 6.1's bound is
        /// asserted over.</param>
        /// <param name="sink">The budget seam every encoder boundary emits through
        /// (<see cref="IMetalEncoderSink"/>).</param>
        internal MetalCommandList(IMetalCommandBufferSource buffers, MetalUncommittedBuffers uncommitted,
            IMetalEncoderSink sink)
        {
            ArgumentNullException.ThrowIfNull(buffers);
            ArgumentNullException.ThrowIfNull(uncommitted);
            ArgumentNullException.ThrowIfNull(sink);

            _buffers = buffers;
            _uncommitted = uncommitted;
            _encoders = new MetalEncoderScope(sink);
        }

        /// <summary>
        /// THE ENCODER LIFECYCLE (M-R1, M-R4). Exposed because rows 12, 13 and 14 drive every one of their
        /// commands through it, and because the device-free tests drive the transitions before any of those
        /// members exist.
        /// </summary>
        internal MetalEncoderScope Encoders => _encoders;

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        internal bool IsRecording => _recording;

        /// <summary>True once <see cref="End"/> has sealed a record that has not been superseded by a later
        /// <see cref="Begin"/> or consumed by a submit. What the submit path requires before it will commit
        /// anything.</summary>
        internal bool IsSealed => _sealed;

        /// <summary>
        /// The <c>MTLCommandBuffer</c> the sealed record lives in, as the submit path names it. Still owned by
        /// this list: the submit path reads it, commits it, and hands ownership back through
        /// <see cref="MarkSubmitted"/>.
        /// </summary>
        /// <exception cref="InvalidOperationException">Nothing is sealed.</exception>
        internal IntPtr SealedCommandBuffer
        {
            get
            {
                if (!_sealed)
                {
                    throw new InvalidOperationException(
                        "A native Metal command list was submitted without a sealed recording. Call Begin, "
                        + "record, then End before submitting: the seam documents that a list submitted without "
                        + "End is a half-recorded frame and that a backend is free to refuse it, and this backend "
                        + "does. A command buffer with an encoder still open cannot legally be committed at all, "
                        + "and one this list never took from the queue has nothing to commit.");
                }

                return _commandBuffer;
            }
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Takes a FRESH <c>MTLCommandBuffer</c> from the queue, retains it, and resets everything the recorder
        /// tracks.
        /// <para>
        /// A SECOND <c>Begin</c> WITHOUT AN <c>End</c> IS REFUSED rather than silently restarting the recording.
        /// The seam says a Begin discards what came before, and on a backend with a pool that is a reset. Here it
        /// would mean abandoning a command buffer with an encoder possibly open on it, so refusing names the
        /// sequencing error at the call that made it instead of surfacing it later as a validation failure inside
        /// the driver.
        /// </para>
        /// <para>
        /// A SEALED RECORD NOBODY SUBMITTED IS DISCARDED HERE, which is the case the seam's own wording covers:
        /// a list is reusable frame after frame, so a Begin after an End that was never submitted is legal and
        /// throws nothing. The buffer is released and the uncommitted count drops, because a buffer that will
        /// never be committed is a buffer the queue is still counting against its own maximum.
        /// </para>
        /// <para>
        /// THE RING'S FRAME SLOT IS WAITED ON HERE, and it is the ONE thing this backend's Begin gates on
        /// (M-R2). Row 8 (https://github.com/APKiwiOrg/KhaozEngine/issues/574) is what puts the wait in: until
        /// then there is no ring, so there is nothing to wait for, and the wait is not a placeholder that could be
        /// forgotten because the ring's acquire is where it belongs rather than a second call site here.
        /// </para>
        /// </remarks>
        public void Begin()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_recording)
            {
                throw new InvalidOperationException(
                    "Begin was called on a native Metal command list that is already recording. Call End first. "
                    + "A second Begin cannot restart the recording: this backend takes a fresh MTLCommandBuffer "
                    + "per Begin, so restarting would abandon a buffer that may have an encoder open on it, and "
                    + "an encoder left open is a command buffer that cannot be committed.");
            }

            // The sealed-but-unsubmitted buffer goes back BEFORE the new one is taken, so a list re-Begun in a
            // loop holds one buffer rather than accumulating one per iteration against the queue's own maximum.
            ReleaseHeldBuffer();

            IntPtr buffer = _buffers.Acquire();
            if (buffer == IntPtr.Zero)
            {
                throw new InvalidOperationException(
                    "The native Metal backend's command queue would not hand out an MTLCommandBuffer. That is a "
                    + "device already in trouble rather than a caller error: -commandBuffer takes no arguments to "
                    + "get wrong, and it blocks rather than failing when the queue is merely full. If a "
                    + "command-buffer failure was latched earlier in this session, the telemetry session header's "
                    + "deviceLossReason names it.");
            }

            _commandBuffer = buffer;
            _uncommitted.Acquired();

            // THE RECORDER STATE RESET GOES HERE, immediately after the acquisition and before the recording flag
            // flips. A reset added anywhere else is a reset that a re-Begun list can be observed without. Today
            // that is the encoder scope, which bumps its epoch so no record from the discarded recording can read
            // as valid (M-R4). Rows 11 to 14 add theirs to this ONE place: the bound framebuffer, both pipelines,
            // both dirty arrays, the pending-clear array, the vertex-stream records, the index-buffer record, and
            // the viewport and scissor marks.
            _encoders.BeginRecording(buffer);

            _recording = true;
            _sealed = false;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// Closes any open encoder and seals the record for submission.
        /// <para>
        /// THE ENCODER CLOSES FIRST AND UNCONDITIONALLY. Committing a command buffer with an encoder still open
        /// is a call Metal refuses, and there is no equivalent of <c>vkEndCommandBuffer</c> to seal against: the
        /// seal here is this backend's own bookkeeping, so the native obligation is exactly one
        /// <c>-endEncoding</c> and no more.
        /// </para>
        /// <para>
        /// THE CLEAR-ONLY FLUSH IS ROW 12's HALF OF THIS (M-A3): a framebuffer plus clears plus an <c>End</c>
        /// with no draw must still CLEAR, which the incumbent forces at two sites and which a golden depends on.
        /// It cannot be written here yet because there is no pending-clear array until row 12
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/578) builds one, and
        /// <see cref="MetalEncoderScope.EnsureNoEncoder"/> already returns which kind it ended, which is what
        /// that row reads instead of adding a second flag.
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
                throw new InvalidOperationException(_sealed
                    ? "End was called twice on a native Metal command list. The recording is already sealed and "
                        + "ready to submit."
                    : "End was called on a native Metal command list that is not recording. Call Begin first.");
            }

            _encoders.EnsureNoEncoder();

            _recording = false;
            _sealed = true;
        }

        /// <summary>
        /// The sealed buffer has been committed, so this list no longer owns it: release the retain
        /// <see cref="Begin"/> took and drop the uncommitted count. Called by the device's submit path INSIDE the
        /// submit lock, immediately after <c>-commit</c> returned, and by nothing else.
        /// <para>
        /// AFTER THE COMMIT RATHER THAN BEFORE IT. A committed command buffer is retained by the queue until it
        /// completes, so releasing here cannot free it under the GPU, and releasing BEFORE the commit would mean
        /// committing through a handle this process no longer holds a reference to.
        /// </para>
        /// <para>
        /// THE SEAL IS CLEARED WITH IT, so a second submit of the same list without a new recording is refused by
        /// <see cref="SealedCommandBuffer"/> rather than committing a buffer twice, which is a call Metal
        /// refuses and which would take a second timeline value with it.
        /// </para>
        /// </summary>
        internal void MarkSubmitted()
        {
            _sealed = false;
            _commandBuffer = IntPtr.Zero;
            _uncommitted.Released();
        }

        /// <summary>
        /// Give up a sealed recording WITHOUT committing it, leaving the list reusable. The submit path's
        /// dead-device arm, and nothing else.
        /// <para>
        /// A submit on a device that has already been lost is a no-op rather than a throw (the seam has no
        /// recovery path and the frame loop above it is not written to handle one), but the buffer still has to
        /// go back: holding it would keep it counted against the queue's own uncommitted maximum for the life of
        /// the process, on a device where nothing will ever commit again.
        /// </para>
        /// </summary>
        internal void DiscardRecording() => ReleaseHeldBuffer();

        /// <summary>
        /// Release whatever this list still holds. IDEMPOTENT, because a consumer disposing a list twice is a
        /// teardown-order accident rather than a defect.
        /// <para>
        /// DISPOSING MID-RECORDING IS LEGAL AND ENDS NOTHING. The recording is discarded, which is what disposing
        /// a list mid-record asks for, and the buffer is released without being committed: an
        /// <c>-endEncoding</c> on a buffer nobody will commit would be a native call bought for nothing.
        /// </para>
        /// <para>
        /// THERE IS NO DEFERRED DESTROY AND NO RETIRE LIST HERE (M-H3), which is the whole shape the Vulkan
        /// sibling needs and this backend does not: an <c>MTLCommandBuffer</c> retains every resource it
        /// references until it completes, so releasing this list's reference to a buffer the GPU is still running
        /// frees nothing the GPU is reading. That property is also why
        /// <c>commandBufferWithUnretainedReferences</c> is never used anywhere in this backend: taking it would
        /// remove exactly that retain and put the retire list back.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _recording = false;

            ReleaseHeldBuffer();
        }

        // The ONE place a held buffer goes back, so the three exits (a re-Begin, a dispose, and the sealed record
        // nobody submitted) cannot drift apart by an edit to one of them. The submit path is deliberately NOT one
        // of them: it hands ownership back through MarkSubmitted after the commit, because the release there is
        // paired with a native call this type does not make.
        void ReleaseHeldBuffer()
        {
            if (_commandBuffer == IntPtr.Zero)
            {
                _sealed = false;
                return;
            }

            IntPtr buffer = _commandBuffer;
            _commandBuffer = IntPtr.Zero;
            _sealed = false;

            _buffers.Release(buffer);
            _uncommitted.Released();
        }
    }
}
