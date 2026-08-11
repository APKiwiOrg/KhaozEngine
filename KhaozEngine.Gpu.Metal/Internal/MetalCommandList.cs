using System;
using KhaozEngine.Gpu.Internal;

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
    /// <para><b>WHAT THIS ROW OWNS IS THE LIFECYCLE, and the recording CONTENT belongs to the rows above it.</b>
    /// This is work-breakdown row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/573): the buffer per
    /// <see cref="Begin"/>, the encoder transitions, <see cref="End"/>, the seal the submit path reads, disposal,
    /// and the two seams the later rows record through. Those rows have all landed, so there is no unbuilt member
    /// left on this type and no ledger file beside it any more: <c>MetalCommandList.Unbuilt.cs</c> carried one
    /// under the same discipline the device's did, and row 14 deleted it with its last entry.</para>
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
        readonly MetalRenderPassSchedule _passes;
        readonly MetalRingAllocator _rings;
        readonly MetalStagingArena _arena;
        readonly IMetalBlitApi _blit;
        readonly IDeviceLiveness _liveness;
        readonly object _owner;

        // THE COUNTED SEAM, HELD BOXED AND UNBOXED PER COMMAND. The encoder scope holds the same reference for
        // the BOUNDARY path, which is 6.4's one virtual call per pass. This field is the DRAW path's, and each
        // draw type-tests it back to a struct so the generic body is monomorphized. See MetalRelayEncoderSink for
        // why that fork exists rather than the list being generic over its sink.
        readonly IMetalEncoderSink _sink;

        // THE TWO UNCOUNTED EMISSION SEAMS THIS LIST REACHES DIRECTLY. The schedule holds the render one too, for
        // the descriptor and the two dynamic-state setters; what the list itself sends through it is the
        // pipeline-state block, which is a draw's business rather than a pass's.
        readonly IMetalRenderApi _render;
        readonly IMetalComputeApi _compute;

        // THE BOUND-PIPELINE RECORD (M-R8, M-R4). Allocated with the list rather than per recording, because it
        // is reset at every Begin and a per-recording allocation would buy nothing.
        readonly MetalPipelineBinding _pipelines = new();

        // The RETAINED buffer this list holds, or Zero when it holds none. One field for both states a held
        // buffer can be in (recording, and sealed but not yet submitted) because the ownership question is the
        // same in both: this list released it or it did not.
        IntPtr _commandBuffer;

        // THE UNIFORM VERSION THIS RECORDING TARGETS (M-M3): the ring segment Begin claimed, captured ONCE and
        // read by every record-time ring write and by the submit that tells the allocator which segment its
        // submission reads. Negative before the first Begin, which is a state neither reader can reach: a write
        // needs _recording and a submit needs a seal, and both come from a Begin that sets this.
        int _segment = -1;

        bool _recording;
        bool _sealed;
        bool _disposed;

        /// <param name="buffers">Where a <see cref="Begin"/> gets its command buffer and where a discarded or
        /// committed one goes back. See <see cref="IMetalCommandBufferSource"/>.</param>
        /// <param name="uncommitted">The device's one uncommitted-buffer counter, which section 6.1's bound is
        /// asserted over.</param>
        /// <param name="sink">The budget seam every encoder boundary emits through
        /// (<see cref="IMetalEncoderSink"/>).</param>
        /// <param name="render">The UNCOUNTED render seam: the pass descriptor and the two encoder-scoped
        /// setters (<see cref="IMetalRenderApi"/>). Separate from <paramref name="sink"/> because nothing on it
        /// scales with draw count, which is the line M-T2's budget is drawn along.</param>
        /// <param name="clearMode">M-A2's position, captured once per list so a recording cannot straddle two
        /// policies. The device passes <see cref="MetalClearPolicy.Current"/> and a test passes a literal.</param>
        /// <param name="owner">The device that created this list, held as an opaque token and compared by
        /// REFERENCE at the submit. See <see cref="Owner"/>.</param>
        /// <param name="rings">The device's ONE ring allocator (M-M3). <see cref="Begin"/> is this backend's
        /// frame boundary, so this is where the segment rotates and where the only wait in the whole recording
        /// path happens.</param>
        /// <param name="arena">This list's OWN staging arena (M-M8), where a record-time upload to a non-uniform
        /// buffer puts its bytes. Per list rather than per device, so two lists recording concurrently never
        /// touch the same blocks and the record path takes no lock. Disposed with the list.</param>
        /// <param name="blit">The one copy a bulk upload emits (<see cref="IMetalBlitApi"/>).</param>
        /// <param name="liveness">The creating device's liveness token, which IS its identity
        /// (<see cref="MetalResourceOwnership"/>). Held so <c>UpdateBuffer</c> can refuse a buffer another device
        /// created, in row 6's shape rather than a third mechanism. It is deliberately NOT what
        /// <see cref="Owner"/> is: a list is refused at the submit by the device INSTANCE, because that is the
        /// object whose submit lock orders the queue, and the two questions are different.</param>
        /// <param name="bufferOffsetAlignment">The DEVICE's own reported buffer-offset alignment
        /// (<c>MetalDeviceFacts.BufferOffsetAlignment</c>), which every composed bind offset has to be a multiple
        /// of. Carried down to both <see cref="MetalBindRecords"/> arms rather than read from a constant, because
        /// M-M3's 256-byte ring stride is the SPACING of the segments and not what the driver requires of an
        /// offset. A test passes a fixed value, which is what keeps the check device-free.</param>
        /// <param name="compute">The COMPUTE-encoder-scoped state setter (<see cref="IMetalComputeApi"/>), which
        /// is the third of the uncounted emission seams and carries exactly one member. Separate from
        /// <paramref name="render"/> because a compute encoder is a different protocol, and separate from
        /// <paramref name="sink"/> because nothing about a pipeline-state bind scales with dispatch count.</param>
        internal MetalCommandList(IMetalCommandBufferSource buffers, MetalUncommittedBuffers uncommitted,
            IMetalEncoderSink sink, object owner, MetalRingAllocator rings, MetalStagingArena arena,
            IMetalBlitApi blit, IDeviceLiveness liveness, IMetalRenderApi render, IMetalComputeApi compute,
            uint bufferOffsetAlignment, MetalClearMode clearMode = MetalClearMode.PerAttachment)
        {
            ArgumentNullException.ThrowIfNull(buffers);
            ArgumentNullException.ThrowIfNull(uncommitted);
            ArgumentNullException.ThrowIfNull(sink);
            ArgumentNullException.ThrowIfNull(owner);
            ArgumentNullException.ThrowIfNull(rings);
            ArgumentNullException.ThrowIfNull(arena);
            ArgumentNullException.ThrowIfNull(blit);
            ArgumentNullException.ThrowIfNull(liveness);
            ArgumentNullException.ThrowIfNull(render);
            ArgumentNullException.ThrowIfNull(compute);

            _buffers = buffers;
            _uncommitted = uncommitted;
            _sink = sink;
            _encoders = new MetalEncoderScope(sink);
            _passes = new MetalRenderPassSchedule(_encoders, render, clearMode);
            _owner = owner;
            _rings = rings;
            _arena = arena;
            _blit = blit;
            _liveness = liveness;
            _render = render;
            _compute = compute;

            // The two bind arms, which cannot be field initialisers because they need the device's alignment.
            // MetalCommandList.Binds.cs declares them.
            _graphicsBinds = MetalBindRecords.ForGraphics(bufferOffsetAlignment);
            _computeBinds = MetalBindRecords.ForCompute(bufferOffsetAlignment);
        }

        /// <summary>
        /// THE DEVICE THIS LIST WAS CREATED BY, which is what makes the submit path's refusal a statement about
        /// identity rather than about type. A process can hold up to
        /// <see cref="MetalCompletionHandler.MaxRegisteredQueues"/> live native Metal devices, so "is a
        /// <see cref="MetalCommandList"/>" and "is THIS device's command list" are genuinely different questions
        /// and only the second one is the one worth asking.
        /// <para>
        /// AN OPAQUE TOKEN RATHER THAN A TYPED DEVICE, so this type stays constructible with no <c>MTLDevice</c>
        /// anywhere and the recording contract keeps running on the Linux and Windows legs. Reference identity is
        /// all the submit path compares, and the device passes <c>this</c>.
        /// </para>
        /// </summary>
        internal object Owner => _owner;

        /// <summary>
        /// THE ENCODER LIFECYCLE (M-R1, M-R4). Exposed because rows 12, 13 and 14 drive every one of their
        /// commands through it, and because the device-free tests drive the transitions before any of those
        /// members exist.
        /// </summary>
        internal MetalEncoderScope Encoders => _encoders;

        /// <summary>True between <see cref="Begin"/> and <see cref="End"/>.</summary>
        internal bool IsRecording => _recording;

        /// <summary>
        /// THE RING SEGMENT THIS RECORDING CAPTURED at its <see cref="Begin"/>, or -1 before the first one. Every
        /// record-time uniform write goes into it, row 13's bind offsets will be composed against it, and
        /// <see cref="MarkSubmitted"/> hands it to the allocator as the segment this submission reads.
        /// <para>
        /// CAPTURED RATHER THAN RE-READ, which is the whole of the segment-per-recording model: a concurrent
        /// list's <c>Begin</c> rotates <see cref="MetalRingAllocator.CurrentSegment"/> under this recording
        /// (M-R3 permits N of them), so a write that asked the allocator each time could land in a version this
        /// recording never binds.
        /// </para>
        /// </summary>
        internal int RingSegment => _segment;

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
        /// THE RING'S SEGMENT IS CLAIMED AND CAPTURED HERE, and it is the ONE thing this backend's Begin gates on
        /// (M-R2). This IS the rotation boundary on this backend, where both siblings put theirs at
        /// <c>Present</c>: they each have a second per-list index that advances at <c>Begin</c> and this backend
        /// has none, and hanging the acquire off a present that the headless golden path never calls would leave
        /// the ring rotating never. The segment it returns is this recording's uniform VERSION for as long as the
        /// recording lasts, which is why it is stored rather than re-read.
        /// <see cref="MetalRingAllocator.BeginRecording"/> carries the whole argument.
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

            // THE RING'S SEGMENT, and the ONLY place in a recording that can block (M-M3, M-R2). It advances the
            // rotation and waits there until the submission that last read the segment it claims has completed.
            // AFTER the acquisition rather than before it, so the uncommitted-buffer count during the wait is the
            // one MetalFramesInFlight.UncommittedBufferBound is stated over, and BEFORE the recording flag flips,
            // so nothing can be recorded into a segment the GPU is still reading. CAPTURED, because this
            // recording's writes and binds all belong to the version claimed here and another list beginning
            // meanwhile moves the allocator's own current segment.
            _segment = _rings.BeginRecording();

            // AND THE ARENA ROTATES ONTO THE SAME SLOT, taking the completion value the gate above just read
            // rather than polling the timeline a second time. It gives back the blocks that slot filled last time
            // round when, and only when, the submission that read them has completed, and it never waits.
            _arena.BeginSlot(_segment, _rings.CompletedValue);

            // THE RECORDER STATE RESET GOES HERE, immediately after the acquisition and before the recording flag
            // flips. A reset added anywhere else is a reset that a re-Begun list can be observed without. Today
            // that is the encoder scope, which bumps its epoch so no record from the discarded recording can read
            // as valid (M-R4), the pass schedule, which drops the bound framebuffer, the pending clears, the
            // scissor-test gate and the viewport and scissor stamps, row 13's three record sets, which drop every
            // recorded slot, the adopted index table and every vertex stream, row 11's bound-pipeline record,
            // which forgets both pipelines, and row 14's index binding.
            //
            // THE SCOPE GOES FIRST, because the schedule's stamps are compared against the scope's epoch and the
            // BeginRecording bump is what makes every one of them stale. Clearing them after that bump is belt
            // and braces rather than the mechanism, which is deliberate: the stamps carry the answer for the
            // ordinary encoder boundary as well, and only one of the two paths can be tested through a Begin.
            _encoders.BeginRecording(buffer);
            _passes.Reset();
            _graphicsBinds.Reset();
            _computeBinds.Reset();
            _streams.Reset();

            // BOTH PIPELINES FORGOTTEN. The epoch bump above already invalidates the STATE BLOCK's stamp, so this
            // is specifically about which pipeline is bound: that is recorder state and survives an encoder
            // boundary on purpose, so only a new recording clears it.
            _pipelines.Reset();

            // AND THE INDEX BINDING, for the same reason and with no epoch stamp at all. Metal takes the index
            // buffer IN the draw call, so it never reaches an argument table and no encoder boundary can discard
            // it, which makes a Begin the only thing that clears it. See MetalIndexBinding.
            _indices.Reset();

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
        /// AND THE CLEAR-ONLY FLUSH COMES FIRST (M-A3), which is the SECOND of the incumbent's two forcing sites
        /// (the other is a framebuffer change). A framebuffer plus clears plus an <c>End</c> with no draw must
        /// still CLEAR, and a golden depends on it. <see cref="MetalRenderPassSchedule.EndPass"/> is the one
        /// helper that decides it: a begin CONSUMES the pending array, so a pending clear still sitting there is
        /// itself the proof that no draw came, and no second flag has to be kept in step.
        /// </para>
        /// <para>
        /// THE UNCONDITIONAL <c>EnsureNoEncoder</c> STAYS BEHIND IT rather than being replaced by it, because the
        /// two answer different questions. The flush closes a RENDER pass. What must not survive an <c>End</c> is
        /// an encoder of ANY kind, including the blit encoder a record-time upload left open, and that is the
        /// native obligation a committable command buffer has.
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

            _passes.EndPass();
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
        /// <para>
        /// AND THE ENCODER SCOPE FORGETS THE BUFFER TOO, because the seal gates the SUBMIT path and nothing else.
        /// A scope still holding the committed handle would let a post-submit <c>Ensure</c> open an encoder on a
        /// buffer Metal has already taken, which is a driver-side failed assertion that aborts the process rather
        /// than anything this backend could report. See <see cref="MetalEncoderScope.ForgetCommandBuffer"/>.
        /// </para>
        /// <para>
        /// AND THE STAGING ARENA LEARNS THE VALUE HERE, which is its whole recycling proof (M-M8). The blocks
        /// this recording leased are read by the submission that just committed, so the slot they are in may not
        /// be handed back until the device timeline reaches <paramref name="signalledValue"/>. The Vulkan sibling
        /// gets that proof for free from its command-pool ring and this backend has no pool at all (M-R2), so the
        /// arena carries it per slot instead.
        /// </para>
        /// <para>
        /// AND THE UNIFORM RING LEARNS IT THROUGH THE SAME PLUMBING, for the same kind of reason: this submission
        /// reads the ring segment this recording captured, so that segment may not be claimed again until the
        /// timeline reaches this value. Recording it HERE, with the submission's own value, is what the ring's
        /// gate rests on. The rejected shape read the last submitted value when the segment stopped being
        /// current, which under-records the perfectly ordinary End, other list's Begin, then Submit interleaving.
        /// </para>
        /// <para>
        /// A LIST THAT NEVER BEGAN HAS NO SEGMENT TO OWN, which is the -1 guard rather than a live case: a submit
        /// requires a seal and a seal requires a Begin. It costs one comparison and stops a future caller that
        /// reaches here another way from recording an owner for segment 0 that nothing reads.
        /// </para>
        /// </summary>
        /// <param name="signalledValue">The timeline value the committed buffer signals, from
        /// <see cref="MetalTimeline.EncodeSignalForSubmit"/>.</param>
        internal void MarkSubmitted(ulong signalledValue)
        {
            _arena.RecordSubmitted(signalledValue);
            if (_segment >= 0) _rings.RecordSegmentOwner(_segment, signalledValue);

            _sealed = false;
            _commandBuffer = IntPtr.Zero;
            _encoders.ForgetCommandBuffer();
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
        /// DISPOSING MID-RECORDING IS LEGAL AND IT STILL ENDS THE OPEN ENCODER. The recording is discarded, which
        /// is what disposing a list mid-record asks for, and the buffer is released without being committed, but
        /// the encoder is NOT dropped on the way out: the sink retains every encoder it opens and
        /// <c>EndEncoding</c> is the only place that release happens, so an abandoned encoder leaks its own +1 and,
        /// through the reference an encoder holds on its command buffer, keeps that buffer alive after this list
        /// has released it. The queue then never gets its uncommitted slot back, and since a queue blocks in
        /// <c>-commandBuffer</c> at its maximum of 64 uncommitted buffers, the leak presents as a frame loop that
        /// hangs rather than as a counter anything reports. <see cref="MetalUncommittedBuffers"/> cannot see it
        /// either, because the release below has already counted the buffer as gone. One native call on a buffer
        /// nobody will commit is what that costs, and it is bought through
        /// <see cref="MetalEncoderScope.EnsureNoEncoder"/> rather than here, so the scope stays the single owner of
        /// every encoder transition.
        /// </para>
        /// <para>
        /// THERE IS NO DEFERRED DESTROY AND NO RETIRE LIST HERE (M-H3), which is the whole shape the Vulkan
        /// sibling needs and this backend does not: an <c>MTLCommandBuffer</c> retains every resource it
        /// references until it completes, so releasing this list's reference to a buffer the GPU is still running
        /// frees nothing the GPU is reading. That property is also why
        /// <c>commandBufferWithUnretainedReferences</c> is never used anywhere in this backend: taking it would
        /// remove exactly that retain and put the retire list back.
        /// </para>
        /// <para>
        /// THE STAGING ARENA DIES WITH THE LIST, blocks and all, and it is safe for the same reason (M-H3): a
        /// submitted blit retains the block it reads until it completes, so releasing the arena's own reference
        /// here frees nothing the GPU is using.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _recording = false;

            // BEFORE the buffer goes back, and through the scope. See this member's remarks: an encoder dropped
            // here holds its own retain and its command buffer's, so the queue slot never frees.
            _encoders.EnsureNoEncoder();

            ReleaseHeldBuffer();

            _arena.Dispose();
        }

        // The ONE place a held buffer goes back, so the three exits (a re-Begin, a dispose, and the sealed record
        // nobody submitted) cannot drift apart by an edit to one of them. The submit path is deliberately NOT one
        // of them: it hands ownership back through MarkSubmitted after the commit, because the release there is
        // paired with a native call this type does not make.
        void ReleaseHeldBuffer()
        {
            // FIRST, and unconditionally, for MarkSubmitted's reason in the other direction: a scope pointing at a
            // buffer this list has released would open an encoder on a released Objective-C object, which is a
            // use-after-free rather than a driver assertion.
            _encoders.ForgetCommandBuffer();

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
