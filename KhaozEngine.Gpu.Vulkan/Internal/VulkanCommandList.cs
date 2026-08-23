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
    /// <para><b>EVERY SEAM MEMBER IS BUILT, AND ROW 15 IS WHERE THAT BECAME TRUE</b>
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525). This file is work-breakdown row 7
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/517), which owns the list's LIFECYCLE: the pools, the slot
    /// machinery, <see cref="Begin"/>, <see cref="End"/>, disposal, and the submit path on the device. Every later
    /// row filled in the recording content on top of it, and the last of them landed the draws, the dispatches,
    /// the copies, the mip chain and the resolve. There is no refusing member left in this type: what used to be a
    /// ledger of unbuilt ones is the list below of where each subsystem's decisions live.</para>
    ///
    /// <para><b>WHERE EACH MEMBER'S DECISIONS LIVE, because almost none of them are in this file.</b> Both
    /// <c>UpdateBuffer</c> overloads route through <see cref="IVulkanRecordUploads"/>: on a RING-BACKED buffer an
    /// update is a memcpy into the current segment which records nothing at all (9.2), and on any other buffer a
    /// staged copy through this list's own arena with a barrier narrowed to the destination's real usage, which
    /// row 9 wired (https://github.com/APKiwiOrg/KhaozEngine/issues/519). A resource-set bind RECORDS ONLY into
    /// <see cref="VulkanBindRecords"/> and issues nothing until a draw flushes it, which is row 11
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/521). A framebuffer bind, both clears and both scissor
    /// members record into <see cref="VulkanRenderingSchedule"/> and issue nothing until a draw opens the render
    /// pass instance, which is row 12 (https://github.com/APKiwiOrg/KhaozEngine/issues/522). Both pipeline binds
    /// emit <c>vkCmdBindPipeline</c> and adopt the pipeline's layout in the matching bind records, which is row 13
    /// (https://github.com/APKiwiOrg/KhaozEngine/issues/523). <see cref="VulkanLayoutTracker"/> transitions every
    /// attachment at the deferred begin and restores every touched texture to rest at <see cref="End"/>, which is
    /// row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/524). The vertex and index binds, the draws and the
    /// dispatch are <see cref="VulkanDrawRecorder"/>'s, in <c>VulkanCommandList.Draws.cs</c>, and the copies, the
    /// mip chain and the resolve are <see cref="VulkanTransferPlan"/>'s, in
    /// <c>VulkanCommandList.Transfers.cs</c>.</para>
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
    internal sealed partial class VulkanCommandList : IGpuCommandList, IVulkanRenderingScope
    {
        readonly VulkanCommandPoolRing _ring;
        readonly VulkanRetireList _retired;
        readonly IVulkanRecordUploads? _uploads;

        // ONE PER BIND POINT, because the seam's graphics and compute bindings are separate and Vulkan's are too.
        // Row 11's whole schedule lives in these two objects, which hold no set, no layout object and nothing that
        // reaches the descriptor pool: see VulkanBoundSet for the V-D2 obligation that shapes them.
        readonly VulkanBindRecords _graphicsBinds;
        readonly VulkanBindRecords _computeBinds;

        // ROW 12's WHOLE SCHEDULE, or null on a list built with no rendering seam, which is only a list a test
        // constructed. It holds the bound framebuffer as PLAIN DATA rather than as a VulkanFramebuffer, which is
        // the same obligation VulkanBoundSet discharges for the bind records: see VulkanBoundFramebuffer.
        readonly VulkanRenderingSchedule? _rendering;

        // ROW 13's ONE RECORD-TIME CALL, vkCmdBindPipeline, or null on a list built without it. It is
        // DELIBERATELY NOT the pipeline CREATION seam: see IVulkanPipelineBinder for why the split is load-bearing
        // rather than tidy.
        readonly IVulkanPipelineBinder? _pipelineBinder;

        // ROW 14's LIST-LOCAL LAYOUT MAP (V-F6 to V-F8), or null on a list with no barrier seam. Begin forgets it
        // and End restores every touched texture through it: see VulkanLayoutTracker.
        readonly VulkanLayoutTracker? _layouts;

        // ROW 15's PRE-COMMAND ORDER plus the vertex and index bind state and the dependent-dispatch hazard set,
        // or null on a list with no draw seam. It is a TYPE rather than seven more members here because the ORDER
        // is what can be wrong (https://github.com/APKiwiOrg/KhaozEngine/issues/556): see VulkanDrawRecorder.
        readonly VulkanDrawRecorder? _draws;

        // ROW 15's OTHER HALF, the six transfer members' one seam, or null on a list without it. The DECISIONS
        // (which layouts, which regions, which blit chain) are VulkanTransferPlan's and are device-free.
        readonly IVulkanTransferSink? _transfers;

        // The pipeline currently bound at each bind point, as a bare handle. Section 6.1 lists "both pipelines"
        // among what a Begin resets, and 6.2 clause 4 is what they are for: a rebind of the pipeline already
        // current does nothing at all, which is the fork's pipeline-identity guard kept.
        ulong _boundGraphicsPipeline;
        ulong _boundComputePipeline;

        // WHETHER THIS RECORDING EVER BOUND THE DEVICE'S SWAPCHAIN FRAMEBUFFER. Sticky across the whole recording
        // rather than a reading of what is bound now, because what it answers is "did this submission order the
        // swapchain image's rendering", and a rebind to some other target later does not undo that.
        bool _boundSwapchain;

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
        /// <param name="render">The six native rendering calls row 12's schedule drives, or null while there is
        /// no device behind this list. Held as a schedule rather than as the seam so the deferred begin, the
        /// clear folding and the framebuffer-change guard all sit in one device-free type.</param>
        /// <param name="pipelines">Row 13's one record-time call, <c>vkCmdBindPipeline</c>, or null while there is
        /// no device behind this list. It can bind a pipeline and cannot make one, which is the whole reason it is
        /// a different seam from the one that creates them.</param>
        /// <param name="layouts">Row 14's list-local layout tracker (V-F7), or null with no device behind this
        /// list. <see cref="Begin"/> resets it and <see cref="End"/> restores through it.</param>
        /// <param name="draws">Row 15's record-time draw, dispatch and geometry-bind calls, or null with no device
        /// behind this list. The ORDER around them is <see cref="VulkanDrawRecorder"/>'s and is built here, so
        /// every list gets the same one.</param>
        /// <param name="transfers">Row 15's six transfer calls (the copies, the blit chain and the resolve), or
        /// null with no device behind this list.</param>
        internal VulkanCommandList(VulkanCommandPoolRing ring, VulkanRetireList retired,
            IVulkanRecordUploads? uploads = null, bool assertBoundSetLayouts = false,
            IVulkanRenderApi? render = null, IVulkanPipelineBinder? pipelines = null,
            VulkanLayoutTracker? layouts = null, IVulkanDrawEmitter? draws = null,
            IVulkanTransferSink? transfers = null)
        {
            ArgumentNullException.ThrowIfNull(ring);
            ArgumentNullException.ThrowIfNull(retired);

            _ring = ring;
            _retired = retired;
            _uploads = uploads;
            _graphicsBinds = new VulkanBindRecords(PipelineBindPoint.Graphics, assertBoundSetLayouts);
            _computeBinds = new VulkanBindRecords(PipelineBindPoint.Compute, assertBoundSetLayouts);
            _rendering = render is null ? null : new VulkanRenderingSchedule(render, layouts);
            _pipelineBinder = pipelines;
            _layouts = layouts;
            _draws = draws is null ? null : new VulkanDrawRecorder(draws, layouts);
            _transfers = transfers;

            // THE UPLOADER TAKES THIS LIST AS ITS RENDERING SCOPE, so a bulk staged UpdateBuffer ends the render
            // pass instance before it records its vkCmdCopyBuffer (V-A4). Wired HERE rather than by the device
            // because this list owns both ends of it: the uploader is disposed with this list, and the scope IS
            // this list, so the cycle closes inside one constructor and no construction path can leave it
            // half-wired. A list with NO rendering seam hands nothing over, because then there is genuinely no
            // pass to end and the uploader's null scope is the right answer.
            if (_rendering is not null) _uploads?.UseRenderingScope(this);
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

        /// <summary>
        /// THE RENDERING SCHEDULE (V-A1 to V-A6). Exposed because row 15
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) drives <see cref="VulkanRenderingSchedule.PrepareDraw"/>
        /// from every draw and <see cref="VulkanRenderingSchedule.EndRendering"/> from every command illegal
        /// inside a render pass instance, and because the device-free tests drive the whole deferred begin through
        /// it before either exists.
        /// </summary>
        /// <exception cref="NotSupportedException">This list was built with no rendering seam, which is only a
        /// list a test constructed.</exception>
        internal VulkanRenderingSchedule Rendering
            => _rendering ?? throw new NotSupportedException(
                "This native Vulkan command list was built with no rendering seam, so it can bind no framebuffer "
                + "and open no render pass instance. Every list the device hands out has one: this is a list "
                + "constructed directly by a test.");

        /// <summary>
        /// Whether this recording bound the device's swapchain framebuffer, which is what decides whether the
        /// frame's semaphore pair rides THIS submit (https://github.com/APKiwiOrg/KhaozEngine/issues/557). See
        /// <see cref="IVulkanBoundFramebufferSource.IsSwapchain"/> for why the question is asked of the list.
        /// </summary>
        internal bool BoundSwapchainFramebuffer => _boundSwapchain;

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
        /// pipeline LAYOUT each is bound under, and row 12
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/522) added the framebuffer, the scissor and the open
        /// render pass instance, and row 14 (https://github.com/APKiwiOrg/KhaozEngine/issues/524) the layout map.
        /// Every one of those resets sits immediately after the native calls below and before the recording flag
        /// flips. A reset added anywhere else is a reset that a re-Begun list can be observed without.
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

            // ROW 14's HALF: a fresh VkCommandBuffer has recorded no transition and every list assumes every
            // texture is at REST (V-F7), so a retained map would skip a barrier against a record nobody submitted.
            _layouts?.Reset();

            // ROW 11's HALF: a fresh VkCommandBuffer has no descriptor set and no pipeline bound, so both records
            // forget their slots AND the pipeline layout they were bound under. Keeping either would let the first
            // flush of the next recording skip a bind as clean against state that lives on another buffer.
            _graphicsBinds.Reset();
            _computeBinds.Reset();

            // ROW 12's HALF, for the same reason: a fresh buffer has no framebuffer bound, no open render pass
            // instance and no viewport or scissor, so a retained bound framebuffer would let the next recording's
            // first SetFramebuffer take the redundant path and draw into a target this buffer never bound.
            _rendering?.Reset();

            // AND THE SWAPCHAIN ANSWER WITH IT, for the same reason: the bind belonged to a recording that was
            // discarded, and a submission of THIS one orders nothing that recording did.
            _boundSwapchain = false;

            // ROW 13's HALF, and the same argument a third time: a fresh VkCommandBuffer has no pipeline bound at
            // either bind point, so a retained handle would let the next recording's first SetPipeline take the
            // identity guard's redundant path and draw with whatever pipeline the driver's own state happened to
            // hold. Both, because the two bind points are tracked separately (V-C1).
            _boundGraphicsPipeline = 0;
            _boundComputePipeline = 0;

            // ROW 15's HALF, and the same argument a fourth time: a fresh VkCommandBuffer has no vertex buffer at
            // any binding and no index buffer, so a retained record would let the next recording's first bind take
            // the identity guard's redundant path and draw out of whatever the driver's own state held. It also
            // drops the dependent-dispatch hazard set, whose writes belonged to a recording nobody submitted.
            _draws?.Reset();

            _recording = true;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// <c>vkEndCommandBuffer</c>, and the seal that makes the record submittable.
        /// <para>
        /// THE RENDER PASS INSTANCE CLOSES FIRST (V-A4), and if the pass recorded clears that no draw consumed,
        /// they are flushed through a begin and end pair on the way out (V-A3). That is the clear-only case, which
        /// the incumbent forced at two sites and a golden depends on. Sealing a buffer with an instance still open
        /// is a call the driver refuses.
        /// </para>
        /// <para>
        /// THE RESTING-LAYOUT RESTORE GOES NEXT (V-F7), after that and before the native end. Every texture has a
        /// canonical resting layout assigned at creation, a list tracks its transitions LOCALLY, and <c>End</c>
        /// restores every texture it touched before sealing, which is what makes two lists composable in any
        /// submit order. It has to be before the native end: a barrier recorded after <c>vkEndCommandBuffer</c> is
        /// a call against a sealed buffer.
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

            // THE INSTANCE CLOSES BEFORE ANYTHING ELSE, including the clear-only flush a pass with no draw owes.
            _rendering?.EndRendering(CurrentBuffer);

            // EVERY TOUCHED TEXTURE BACK TO REST (V-F7), as ONE batched barrier, after the instance closed and
            // before the native end. The remarks above say why that order is fixed rather than incidental.
            _layouts?.RestoreResting(CurrentBuffer);

            int slot = _ring.Slot;
            _ring.EndRecording(slot);

            _recording = false;
            _sealedSlot = slot;
        }

        /// <inheritdoc/>
        /// <remarks>
        /// RECORDS ONLY, in the common case, because the begin is DEFERRED to the first draw (V-A2). What it can
        /// still emit is the outgoing pass: an open instance is closed, and clears the outgoing framebuffer
        /// collected without a draw are flushed through a begin and end pair (V-A3).
        /// <para>
        /// A REBIND OF THE FRAMEBUFFER ALREADY BOUND DOES NOTHING AT ALL, which is Veldrid's own identity guard
        /// reproduced whole rather than narrowed to the viewport. See <see cref="VulkanRenderingSchedule"/> for
        /// why both halves of that are load-bearing and what an unconditional emit costs.
        /// </para>
        /// <para>
        /// THE FRAMEBUFFER IS RESOLVED BEFORE THE GUARD, so a framebuffer from another backend is refused whether
        /// or not the bind was redundant. That is the same order the native Direct3D 11 emitter takes, for the
        /// same reason: a guard-first order lets the same mistake pass silently on the second bind.
        /// </para>
        /// </remarks>
        public void SetFramebuffer(IGpuFramebuffer fb)
        {
            VulkanRenderingSchedule rendering = RequireRendering("Binding a framebuffer");

            // THROUGH THE INTERFACE rather than through VulkanFramebuffer, so the device's own swapchain
            // framebuffer binds down the identical path. Its identity is stable across every recreate (V-W5) and
            // its attachment moves to the acquired image at every present boundary, which is invisible from here:
            // what arrives is the same flattened record of handles, integers and enums either way.
            IVulkanBoundFramebufferSource framebuffer =
                VulkanBindableFramebuffer.Require(fb, "a native Vulkan framebuffer bind");

            // BEFORE THE IDENTITY GUARD INSIDE THE SCHEDULE, and sticky, so a redundant rebind cannot clear it and
            // a later bind of some other target cannot either.
            _boundSwapchain |= framebuffer.IsSwapchain;

            rendering.SetFramebuffer(CurrentBuffer, framebuffer.AsBound);
        }

        /// <inheritdoc/>
        /// <remarks>Folds into <c>loadOp = CLEAR</c> when the pass has not opened yet, and is a
        /// <c>vkCmdClearAttachments</c> when it has (V-A2).</remarks>
        public void ClearColorTarget(uint index, Color rgba)
            => RequireRendering("Clearing a colour target").ClearColourTarget(CurrentBuffer, index, rgba);

        /// <inheritdoc/>
        /// <remarks>The depth arm of the same rule. The stencil plane clears to zero alongside it on a combined
        /// format, matching the incumbent, because the seam carries no stencil value to pass instead.</remarks>
        public void ClearDepthStencil(float depth)
            => RequireRendering("Clearing the depth attachment").ClearDepthStencil(CurrentBuffer, depth);

        /// <inheritdoc/>
        /// <remarks>Records the rectangle, which the next draw emits. A non-zero <paramref name="index"/> is
        /// refused by name: see <see cref="VulkanRenderingSchedule"/>.</remarks>
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h)
            => RequireRendering("Setting a scissor rect").SetScissorRect(index, x, y, w, h);

        /// <inheritdoc/>
        /// <remarks>Restores the bound framebuffer's full extent, which is what a framebuffer CHANGE applies
        /// anyway.</remarks>
        public void SetFullScissorRects() => RequireRendering("Resetting the scissor rects").SetFullScissorRects();

        /// <summary>
        /// THE PRE-DRAW HOOK, RENDERING ARM (V-A2, V-A5). Row 15
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/525) calls this FIRST in <c>Draw</c> and
        /// <c>DrawIndexed</c>, before <see cref="FlushGraphicsBinds{TSink}"/> and before the vertex and index
        /// binds: it opens the render pass instance if the pass has not opened yet, folding every pending clear
        /// into a <c>loadOp</c>, and then emits the viewport and the scissor if a framebuffer change marked them.
        /// </summary>
        internal void PrepareDraw() => RequireRendering("Drawing").PrepareDraw(CurrentBuffer);

        /// <summary>
        /// THE END-BEFORE-ANYTHING-ILLEGAL INVARIANT (V-A4), as the ONE helper every such command calls: a
        /// dispatch, a resolve, a copy and a mip generation are all illegal inside a render pass instance, so each
        /// ends the pending rendering first.
        /// <para>
        /// THE BULK UPLOAD PATH CALLS IT, through <see cref="IVulkanRenderingScope"/> below: a staged
        /// <c>UpdateBuffer</c> records a <c>vkCmdCopyBuffer</c>, which is as illegal inside a pass as a dispatch
        /// is. The dispatch, resolve and mip-generation callers arrived in rows 13 and 15 and call this rather
        /// than writing the rule a second time.
        /// </para>
        /// <para>
        /// SAFE TO CALL WHEN NOTHING IS OPEN, so a caller never has to ask first, and it takes the clear-only
        /// flush with it when a pass collected clears that no draw consumed.
        /// </para>
        /// </summary>
        internal void EndRenderingBeforeIllegalCommand()
            => RequireRendering("This command").EndRendering(CurrentBuffer);

        /// <summary>
        /// THE UPLOAD PATH'S ARM OF THAT INVARIANT, as <see cref="IVulkanRenderingScope"/> asks for it, and the
        /// helper above unchanged rather than a second copy of the rule.
        /// <para>
        /// EXPLICIT, because it is not a member a caller inside this backend should reach for: the one call site
        /// is <see cref="VulkanBufferUpload.Record"/>, through the scope this list hands its own uploader in the
        /// constructor, and every other illegal command names
        /// <see cref="EndRenderingBeforeIllegalCommand"/> directly.
        /// </para>
        /// </summary>
        void IVulkanRenderingScope.EndActiveRendering() => EndRenderingBeforeIllegalCommand();

        /// <inheritdoc/>
        /// <remarks>
        /// CLAUSE 4 (V-R6). <c>vkCmdBindPipeline</c> at the graphics bind point, then
        /// <see cref="VulkanBindRecords.SetPipelineLayout"/> on <see cref="GraphicsBinds"/> with the pipeline's own
        /// layout handle and set-layout sequence, which invalidates recorded descriptor slots from the first
        /// INCOMPATIBLE set onward. Row 11 (https://github.com/APKiwiOrg/KhaozEngine/issues/521) landed that
        /// computation and both of decision V-R7's guards one row early, so this member is the wiring and nothing
        /// more.
        /// <para>
        /// A REBIND OF THE PIPELINE ALREADY CURRENT DOES NOTHING AT ALL, which is the fork's pipeline-identity
        /// guard kept. It is a stronger skip than the layout guard underneath it: two DIFFERENT pipelines sharing
        /// a layout still emit their bind (they are different programs) and still invalidate nothing, and the same
        /// pipeline twice emits neither.
        /// </para>
        /// <para>
        /// THE PIPELINE IS RESOLVED AND CHECKED FOR LIFE BEFORE THE GUARD, so one from another backend and one
        /// already disposed are both refused whether or not the bind was redundant. Same order
        /// <see cref="SetFramebuffer"/> takes, for the same reason: a guard-first order lets a foreign pipeline
        /// pass silently on the second bind, and would never catch a disposed one at all, since its zero handle
        /// equals the identity <see cref="Begin"/> resets to.
        /// </para>
        /// </remarks>
        public void SetPipeline(IGpuPipeline p)
        {
            IVulkanPipelineBinder binder = RequireBinder("Binding a graphics pipeline");
            VulkanGraphicsPipeline pipeline = VulkanGraphicsPipeline.Require(
                p, "a native Vulkan graphics pipeline bind");
            RequireLivePipeline(pipeline.Handle, "graphics");

            if (pipeline.Handle == _boundGraphicsPipeline) return;

            binder.BindPipeline(CurrentBuffer, compute: false, pipeline.Handle);
            _boundGraphicsPipeline = pipeline.Handle;

            _graphicsBinds.SetPipelineLayout(pipeline.PipelineLayout, pipeline.SetLayouts);
        }

        /// <inheritdoc/>
        /// <remarks>
        /// CLAUSE 1, RECORD ONLY (V-R5). No native call and no descriptor work: the slot's record takes the set's
        /// handle, its layout handle and its dynamic uniform array, and goes dirty if either the set or the offset
        /// moved. The bind itself happens at the next draw, one <c>vkCmdBindDescriptorSets</c> per contiguous run
        /// of dirty slots.
        /// <para>
        /// A RECORD MADE OUTSIDE A RECORDING IS DISCARDED RATHER THAN REFUSED, and that is these records' own
        /// semantics rather than anything shared with the write path:
        /// <see cref="UpdateBuffer{T}(IGpuBuffer,uint,in T)"/> discards nothing, it writes a ring-backed buffer
        /// immediately and routes everything else to the staging arena. What makes the discard safe here is
        /// <see cref="Begin"/>, which resets both records, so a bind made before a recording cannot leak into the
        /// one that follows.
        /// </para>
        /// </remarks>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => Bind(_graphicsBinds, slot, set, 0);

        /// <inheritdoc/>
        /// <remarks>The same record, carrying the caller's per-draw byte offset, which the flush adds on top of the
        /// ring base for the ONE element the layout declared dynamic (V-D4).</remarks>
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
            => Bind(_graphicsBinds, slot, set, dynamicOffset);

        /// <inheritdoc/>
        /// <remarks>
        /// THE COMPUTE ARM of <see cref="SetPipeline"/>, into <see cref="ComputeBinds"/>, with its own identity
        /// guard and its own pipeline-layout record: graphics and compute bindings are tracked separately
        /// (V-C1), so a compute switch never invalidates a graphics slot.
        /// <para>
        /// IT ENDS ANY PENDING RENDERING FIRST (V-A4, section 13), which is the one real difference between the
        /// two arms. <c>vkCmdBindPipeline</c> is itself legal inside a render pass instance at either bind point,
        /// so this is the design stating the compute arm's rule at the bind rather than only at the dispatch,
        /// which is where the invariant would otherwise be discovered. It happens AFTER the identity guard, so a
        /// redundant rebind does not split a pass either.
        /// </para>
        /// </remarks>
        public void SetComputePipeline(IGpuComputePipeline p)
        {
            IVulkanPipelineBinder binder = RequireBinder("Binding a compute pipeline");
            VulkanComputePipeline pipeline = VulkanComputePipeline.Require(
                p, "a native Vulkan compute pipeline bind");
            RequireLivePipeline(pipeline.Handle, "compute");

            if (pipeline.Handle == _boundComputePipeline) return;

            EndRenderingBeforeIllegalCommand();

            binder.BindPipeline(CurrentBuffer, compute: true, pipeline.Handle);
            _boundComputePipeline = pipeline.Handle;

            _computeBinds.SetPipelineLayout(pipeline.PipelineLayout, pipeline.SetLayouts);
        }

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

        // THE THREE THINGS EVERY RENDERING MEMBER NEEDS TRUE, in ONE place rather than at each of the seven.
        //
        // RECORDING IS REQUIRED HERE AND NOWHERE ELSE IN THIS TYPE, and the asymmetry with the bind records is
        // deliberate rather than an oversight. A resource-set bind made outside a recording is DISCARDED, because
        // it touches nothing but this list's own array and Begin resets it. A rendering member can EMIT: a
        // framebuffer change flushes the outgoing pass, and a clear after the pass opened is a vkCmdClearAttachments
        // immediately. A vkCmd* against a buffer that vkBeginCommandBuffer has not seen is undefined behaviour
        // rather than a no-op, so this is refused by name instead.
        VulkanRenderingSchedule RequireRendering(string what)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_recording)
            {
                throw new InvalidOperationException(
                    what + " on a native Vulkan command list needs an open recording, and this list is not "
                    + "recording. Call Begin first. Unlike a resource-set bind, which records into this list's "
                    + "own array and is discarded, a rendering member can emit a vkCmd* immediately, and a "
                    + "command recorded into a VkCommandBuffer that was never begun is undefined behaviour.");
            }

            return Rendering;
        }

        // THE SAME THREE THINGS FOR THE PIPELINE ARM, and recording is required here for the reason it is there: a
        // pipeline bind EMITS a vkCmdBindPipeline immediately, and a vkCmd* against a buffer that
        // vkBeginCommandBuffer has not seen is undefined behaviour rather than a no-op.
        IVulkanPipelineBinder RequireBinder(string what)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (!_recording)
            {
                throw new InvalidOperationException(
                    what + " on a native Vulkan command list needs an open recording, and this list is not "
                    + "recording. Call Begin first. Unlike a resource-set bind, which records into this list's "
                    + "own array and is discarded, a pipeline bind emits a vkCmdBindPipeline immediately.");
            }

            return _pipelineBinder ?? throw new NotSupportedException(
                "This native Vulkan command list was built with no pipeline seam, so it can bind no pipeline. "
                + "Every list the device hands out has one: this is a list constructed directly by a test.");
        }

        // A DISPOSED PIPELINE IS REFUSED BEFORE THE IDENTITY GUARD, because the guard cannot see it. Dispose
        // zeroes the handle and Begin resets both bound handles to 0, so the FIRST bind of a recording with a
        // disposed pipeline compares 0 == 0, returns, and records nothing while reading as a redundant rebind.
        // The draw after it then runs under whatever was bound before, which renders wrong without throwing.
        static void RequireLivePipeline(ulong handle, string what)
        {
            if (handle != 0) return;

            throw new ObjectDisposedException("VkPipeline",
                "The " + what + " pipeline handed to a native Vulkan bind carries the null VkPipeline, which is "
                + "what Dispose leaves behind. Binding it would record nothing at all, and the identity guard "
                + "cannot catch that, because a recording that has just begun has nothing bound either.");
        }

        // The buffer the current slot is recording into, which every emitted vkCmd* names. Only meaningful while
        // recording, which is exactly what RequireRendering has just established at every call site.
        ulong CurrentBuffer => _ring.BufferAt(_ring.Slot);

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

    }
}
