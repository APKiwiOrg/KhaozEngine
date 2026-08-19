using System;
using System.Collections.Generic;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu
{
    /// <summary>What a <see cref="GpuRetireQueue"/> does when it has no fence to poll: the two answers to "how do
    /// we know the GPU is done with this", for the two situations a renderer can be in. Internal because the
    /// public factories pick it, and a caller picking it directly would be choosing a safety argument without the
    /// context that justifies it.</summary>
    internal enum GpuRetireFallback
    {
        /// <summary>Drain the device once before freeing a ripe batch. For a caller that may retire a resource in
        /// the very frame that referenced it, where the frame count alone is a bet rather than an argument.</summary>
        DrainDevice,

        /// <summary>Free on the frame count alone, with no drain at all on the per-frame path (teardown still
        /// drains once). For a caller that cannot mint a fence and must not stall.</summary>
        FrameCountOnly,
    }

    /// <summary>
    /// The refusal <see cref="GpuRetireQueue.FlushAll"/> gets instead of a drain that proves nothing. Thrown when
    /// the flush is asked for while something is recording on the queue's device, which is the one place the
    /// teardown path can be reached from the middle of a frame.
    /// <para>
    /// WHY A DRAIN THERE IS WORSE THAN A STALL. <see cref="IGpuDevice.WaitForIdle"/> waits out work that has been
    /// SUBMITTED. An open recording has not been submitted, so the drain returns having said nothing at all about
    /// the draws sitting in that list, and the resources freed immediately after it can still be referenced by
    /// them. That is a use-after-free with a drain in front of it, which reads as safe in review and segfaults
    /// under Mesa lavapipe. The per-frame answer is <see cref="GpuRetireQueue.Retire(System.IDisposable)"/> plus
    /// <see cref="GpuRetireQueue.BeginFrame"/>, which is what the whole type is for.
    /// </para>
    /// <para>
    /// It carries <see cref="Owner"/>, the recording that was open, for the same reason
    /// <see cref="GpuNestedRecordingException"/> does: the stack trace names only the caller that was refused
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>).
    /// </para>
    /// </summary>
    public sealed class GpuDrainDuringRecordingException : InvalidOperationException
    {
        /// <summary>Build the refusal. <paramref name="owner"/> is the recording that was already open.</summary>
        public GpuDrainDuringRecordingException(string owner)
            : base(BuildMessage(owner))
        {
            Owner = owner;
        }

        /// <summary>Who was recording on the device when the flush was refused.</summary>
        public string Owner { get; }

        /// <summary>The message text, built here so a test can assert the wording without catching anything.
        /// </summary>
        public static string BuildMessage(string owner) =>
            $"GpuRetireQueue.FlushAll was called while {owner} is recording on this device. FlushAll, and the "
            + "Dispose that calls it, are the TEARDOWN path: they drain the device with IGpuDevice.WaitForIdle "
            + "and then destroy everything pending. A drain only waits out work that was already SUBMITTED, so "
            + "an open recording is not covered by it, and a resource freed behind that drain can still be "
            + "referenced by the draws in the open list. Retire the resource instead and let BeginFrame destroy "
            + "it once the frame count or the fence says the GPU is done, and flush or dispose the queue outside "
            + "the frame's recording, where a stall costs nothing.";
    }

    /// <summary>Deferred disposal for GPU resources freed mid-life (a streamed mesh unloaded while the scene keeps
    /// running, a sprite atlas whose descriptor set fell out of the working set). Retirements are grouped into a
    /// per-frame BATCH at the next frame boundary, each batch is stamped with a GPU fence submitted at that
    /// boundary, and a batch is destroyed on the first later frame boundary whose fence polls signaled. Nothing
    /// blocks: a burst of unloads costs one empty fenced submission, not a pipeline stall.
    /// <para><b>The seam owns this so a renderer does not have to.</b> The idiom was hand-rolled per renderer
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/80">#80</see>), which is how two 3D
    /// renderers ended up disposing grown buffers inline instead, and how
    /// <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/84">#84</see> left a full
    /// <see cref="IGpuDevice.WaitForIdle"/> on <c>SpriteBatch</c>'s per-frame path. A renderer that feeds this
    /// gets the safe behaviour by construction rather than by remembering to copy another renderer.</para>
    /// <para>The rule this preserves: a GPU resource is never destroyed while queued work may still reference it
    /// (Mesa lavapipe runs submissions on its own thread and segfaults on the use-after-free, which is why the
    /// disposal sites drained at all, see 8c2a6c6b). Nothing here weakens that, and the fence path is strictly
    /// stronger than the frame count it replaces:</para>
    /// <list type="number">
    /// <item>A batch sealed as the counter moves to F holds exactly the resources retired while the counter read
    /// F-1 or less. Every command that could reference one of them was recorded into a frame whose command list
    /// was already submitted by then, because a host submits each frame's work between consecutive
    /// <see cref="BeginFrame"/> calls (that is the contract, see the host note below).</item>
    /// <item>The batch's fence is submitted at that same boundary, so it sits AFTER all of that work in the
    /// submission stream. Polling it signaled therefore carries the same guarantee the <c>WaitForIdle</c> it
    /// replaced carried, taken at seal time rather than at free time. See <c>GpuRetireBarrier</c> for the
    /// spec citation.</item>
    /// <item>Batches are freed strictly oldest-first and the sweep STOPS at the first unsignaled fence, so a batch
    /// is only ever destroyed after every older batch already was.</item>
    /// </list>
    /// <para><b>The fallback, in two flavours.</b> On a device with no GPU-completion fence
    /// (<see cref="GpuCapabilities.SupportsCompletionFences"/> false: Direct3D11 and OpenGL, where Veldrid's fence
    /// is a CPU-side submit receipt) the barrier is absent, and a batch instead waits out
    /// <see cref="FrameDelay"/> frame boundaries. <see cref="Create"/> then destroys it behind one
    /// <c>WaitForIdle</c>, which is exactly the pre-fence behaviour, so an unfenced backend loses the speed-up and
    /// keeps every safety property it had. <see cref="CreateFrameCounted"/> destroys it on the frame count alone,
    /// which is the weaker argument and is why that factory carries its own contract.</para>
    /// <para><b>Not thread-safe.</b> It is the frame loop and nothing else: every member touches the same
    /// unsynchronized lists and frame counter, so retiring from a background build thread while the frame thread is
    /// in <see cref="BeginFrame"/> corrupts it. A worker that wants a resource freed hands it to the frame thread
    /// first, the way the streamer's apply step does.</para>
    /// <para><b>The safety valve.</b> On the fence path a batch lives until its fence signals, so a CPU that runs
    /// away from the GPU (a software rasterizer, a weak GPU, an offscreen loop with no swapchain to throttle it)
    /// holds more and more of them and the holding grows without bound
    /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/425">#425</see>). Past
    /// <see cref="MaxSealedBatches"/> sealed batches the queue stops waiting and pays ONE
    /// <see cref="IGpuDevice.WaitForIdle"/>, which proves every submitted batch complete, and frees the whole
    /// holding behind it. That is a designed bound written against the fence path rather than one inherited from
    /// whatever backpressure a backend's ring allocator happens to apply. It costs at most one drain per
    /// <see cref="MaxSealedBatches"/> frames of sustained fall-behind, and <see cref="ValveDrains"/> counts them,
    /// so a device that keeps up never reaches it and reports zero. The two frame-counted policies need no valve:
    /// a batch there dies on the count alone, which caps the holding at <see cref="FrameDelay"/> batches by
    /// construction.</para>
    /// <para><b>It only frees on <see cref="BeginFrame"/>.</b> Nothing here is time-driven, so a renderer that
    /// retires but never reaches a frame boundary holds every retired resource until <see cref="FlushAll"/> or
    /// teardown, which for a streaming world is the whole unloaded ring. A host that drives the renderer without a
    /// frame boundary (a tool, a test, an offscreen render) must call one of the two itself.</para>
    /// <para><b>Host contract.</b> A frame's command list must be submitted before the NEXT
    /// <see cref="BeginFrame"/>. Every frame-loop host the engine ships does this (the windowed loop submits and
    /// presents at the end of the frame, and each offscreen capture submits inside the call that rendered), and
    /// point 1 above rests on it. A host that recorded draws across two Begin calls and submitted
    /// once at the end would break it, and would have been equally broken by the frame-count scheme.</para></summary>
    public sealed class GpuRetireQueue : IDisposable
    {
        /// <summary>Frame boundaries a retired resource waits before it is destroyed ON THE FALLBACK PATH (no
        /// GPU-completion fence). Three covers the deepest CPU-ahead-of-GPU window a vsynced frame loop reaches, so
        /// by the time the drain runs the referencing work has long completed. The fence path does not use it: a
        /// signaled fence is proof, where a frame count is only a bet that three frames is enough.</summary>
        public const int DefaultFrameDelay = 3;

        /// <summary>Sealed batches the queue holds behind unsignaled fences before the safety valve trades the poll
        /// for one drain (see the valve note on this type). Eight is comfortably above the deepest a healthy frame
        /// loop reaches: the CPU runs at most <c>KE_METAL_FRAMES_IN_FLIGHT</c> / <c>KE_VULKAN_FRAMES_IN_FLIGHT</c> /
        /// <c>KE_D3D11_FRAMES_IN_FLIGHT</c> frames ahead (default 3), and every one of those frames would have to
        /// have retired something to seal a batch, so a device that keeps up never comes near it. A consumer that
        /// raises that knob past 8 raises this with it, or it buys a drain it did not need.</summary>
        public const int DefaultMaxSealedBatches = 8;

        readonly Action _drainDevice;
        readonly IRetireBarrier? _barrier;
        readonly GpuRetireFallback _fallback;
        // The device the open-recording register is asked about before FlushAll drains it. Null only for the
        // internal ctor a test drives with a bare drain action, where there is no device to be recording on.
        readonly IGpuDevice? _device;
        // Retired resources, appended in retirement order and freed from the front. The retirement FRAME is not
        // stored per entry: batching at the frame boundary means one batch is exactly one frame's retirements, so
        // the batch carries the single stamp the fallback ripeness test needs.
        readonly List<IDisposable> _pending = new();
        // Sealed batches, oldest first. Each owns a contiguous run at the FRONT of _pending, so freeing one is a
        // RemoveRange from index 0 and the "entries are in non-decreasing frame order" invariant does the rest.
        readonly List<Batch> _batches = new();
        // Entries covered by _batches. The tail beyond it is this frame's retirements, not yet sealed.
        int _batched;

        // Frame boundaries seen. Wraps at int.MaxValue after about a year of continuous 60 Hz play. The age
        // subtraction below stays correct across the wrap because it is unchecked two's complement.
        int _frame;

        /// <summary>
        /// The default queue for a renderer whose frame boundary sits OUTSIDE the frame's own command-list
        /// recording: fence-polled ripeness wherever the device can signal on GPU completion, and the pre-fence
        /// frame count plus one <see cref="IGpuDevice.WaitForIdle"/> everywhere else. A device with no completion
        /// fence is the supported fallback, not an error.
        /// <para>Minting a fence means opening a command list of its own, so this must be built for, and its
        /// <see cref="BeginFrame"/> called from, a point where nothing is recording on the device. From inside an
        /// open recording the seam refuses that by name
        /// (<see cref="GpuNestedRecordingException"/>, <see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>).
        /// A renderer whose only per-frame hook is inside the record phase wants
        /// <see cref="CreateFrameCounted"/> instead.</para>
        /// </summary>
        /// <param name="device">The device whose resources this queue frees.</param>
        /// <param name="frameDelay">Frame boundaries a batch waits on the fallback path (clamped to at least 1).</param>
        /// <param name="maxSealedBatches">Sealed batches held behind unsignaled fences before the safety valve
        /// falls back to one drain (clamped to at least 1). See <see cref="MaxSealedBatches"/>.</param>
        public static GpuRetireQueue Create(IGpuDevice device, int frameDelay = DefaultFrameDelay,
            int maxSealedBatches = DefaultMaxSealedBatches)
        {
            ArgumentNullException.ThrowIfNull(device);
            return new GpuRetireQueue(device.WaitForIdle, GpuRetireBarrier.TryCreate(device),
                GpuRetireFallback.DrainDevice, frameDelay, device, maxSealedBatches);
        }

        /// <summary>
        /// A queue that NEVER mints a fence and NEVER drains on the per-frame path: a batch is destroyed once
        /// <paramref name="frameDelay"/> frame boundaries have passed, and the only <see cref="IGpuDevice.WaitForIdle"/>
        /// left is the single one at teardown, where a stall costs nothing. For a renderer whose frame boundary is
        /// INSIDE the frame's command-list recording, which is where <c>SpriteBatch</c> lives: its <c>NewFrame</c>
        /// runs in the record phase, so a fence submitted there would be the second recording the seam refuses
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>), and a drain there is the
        /// per-frame stall this whole type exists to remove
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/84">#84</see>).
        /// <para><b>The contract the caller takes on, and the number it is measured against.</b>
        /// <paramref name="frameDelay"/> boundaries must be MORE than the deepest the CPU ever runs ahead of the
        /// GPU, because that count is the whole argument here. That depth is not the swapchain's image count: on
        /// the engine's own backends it is the pipeline depth their frames-in-flight knob sets
        /// (<c>KE_METAL_FRAMES_IN_FLIGHT</c>, <c>KE_VULKAN_FRAMES_IN_FLIGHT</c>, <c>KE_D3D11_FRAMES_IN_FLIGHT</c>:
        /// default 3, settable up to 16), because that is where the backend stops the CPU and waits. A delay of 4
        /// therefore holds at the default and at a depth of 4, and stops holding above it, so a consumer who
        /// raises the knob past 4 raises this with it. <c>SpriteBatch</c> passes 4 (<c>RingDepth + 1</c>), which
        /// coincides with that bound at the default depth and is the same number its vertex ring already bets on
        /// when it rewrites a slot it last handed the GPU <c>RingDepth</c> frames ago.</para>
        /// <para><b>The two bets fail differently, which is why this one is spelled out.</b> A ring slot rewritten
        /// a frame early tears that frame's geometry, and the next frame draws correctly. A batch destroyed a
        /// frame early frees memory the GPU is still reading, which is a use-after-free rather than an artifact
        /// (Mesa lavapipe segfaults on it). So the ring sitting at its own margin is not a reason to relax this
        /// one, and this factory stays weaker than <see cref="Create"/>, which is why picking it is a decision
        /// rather than a default.</para>
        /// </summary>
        /// <param name="device">The device whose resources this queue frees. Drained once, at teardown.</param>
        /// <param name="frameDelay">Frame boundaries a batch waits before it is destroyed (clamped to at least 1).</param>
        public static GpuRetireQueue CreateFrameCounted(IGpuDevice device, int frameDelay)
        {
            ArgumentNullException.ThrowIfNull(device);
            // No maxSealedBatches knob here on purpose: a batch dies on the frame count alone, so the holding is
            // capped at frameDelay batches by construction and there is nothing for a valve to bound (#425).
            return new GpuRetireQueue(device.WaitForIdle, null, GpuRetireFallback.FrameCountOnly, frameDelay, device);
        }

        /// <summary>Build a queue over the device drain it runs before destroying anything on the fallback path
        /// (normally <c>IGpuDevice.WaitForIdle</c>) and, when the device can signal a fence on GPU completion, the
        /// <paramref name="barrier"/> that replaces that drain with a poll. A null barrier is the supported
        /// fallback, not an error. <paramref name="frameDelay"/> below 1 is clamped to 1, so a resource is never
        /// destroyed inside the call that retired it. Internal so a test can drive the ripeness policy by hand;
        /// production builds one through <see cref="Create"/> or <see cref="CreateFrameCounted"/>.
        /// <para><paramref name="device"/> is only ever asked whether something is recording on it, which is what
        /// <see cref="FlushAll"/> refuses on. A test driving a bare drain action passes none, and gets no
        /// refusal, because there is no device for a recording to be open on.</para></summary>
        internal GpuRetireQueue(Action drainDevice, IRetireBarrier? barrier = null,
            GpuRetireFallback fallback = GpuRetireFallback.DrainDevice, int frameDelay = DefaultFrameDelay,
            IGpuDevice? device = null, int maxSealedBatches = DefaultMaxSealedBatches)
        {
            _drainDevice = drainDevice ?? throw new ArgumentNullException(nameof(drainDevice));
            _barrier = barrier;
            _fallback = fallback;
            _device = device;
            FrameDelay = frameDelay < 1 ? 1 : frameDelay;
            MaxSealedBatches = maxSealedBatches < 1 ? 1 : maxSealedBatches;
        }

        /// <summary>Frame boundaries a retired resource waits on the FALLBACK path (at least 1).</summary>
        public int FrameDelay { get; }

        /// <summary>Sealed batches held behind unsignaled fences before the safety valve trades the fence poll for
        /// one <see cref="IGpuDevice.WaitForIdle"/> and frees the whole holding behind it (at least 1). This is the
        /// bound on how far the queue lets a CPU running ahead of the GPU grow it
        /// (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/425">#425</see>): after any
        /// <see cref="BeginFrame"/>, no more than this many batches are held.
        /// <para>It bounds BATCHES, not resources. One batch is one frame's retirements, so a caller that retires
        /// its whole world in a single frame still holds all of it for a frame or three, which is the caller's own
        /// burst and not something a deferral policy can shrink.</para>
        /// <para>Set below <see cref="FrameDelay"/> it also preempts the frame-count fallback, which costs that path
        /// its drain a boundary or two early and nothing else, since that path was going to drain anyway.</para>
        /// </summary>
        public int MaxSealedBatches { get; }

        /// <summary>How many times the safety valve has fired: the drains this queue paid because the GPU had not
        /// reached <see cref="MaxSealedBatches"/> batches' worth of fences. Zero on a device that keeps up, which
        /// is the whole claim the fence path makes, so a non-zero reading is the honest signal that the CPU is
        /// running away from the GPU rather than a defect in the queue. Diagnostic only, never a gate on a shared
        /// machine: which device you are on decides it.</summary>
        public int ValveDrains { get; private set; }

        /// <summary>Resources retired but not yet destroyed. Counts the whole holding, sealed batches and this
        /// frame's not-yet-sealed tail alike, which is what a scene's retired-resource count surfaces.</summary>
        public int PendingCount => _pending.Count;

        /// <summary>Batches sealed and waiting on their fence, one per frame boundary that had something to seal.
        /// This is the number the safety valve bounds, so it never exceeds <see cref="MaxSealedBatches"/> once a
        /// <see cref="BeginFrame"/> has returned. Public because the valve's contract is written in batches rather
        /// than resources: <see cref="PendingCount"/> tells you how much is being held, this tells you how far
        /// behind the GPU is.</summary>
        public int SealedBatchCount => _batches.Count;

        /// <summary>Hand a resource over to the queue. Costs nothing at the call site: no drain, no destroy, no
        /// submission. A null resource is ignored, so an optional resource (a per-mesh material set) needs no
        /// caller-side check.</summary>
        public void Retire(IDisposable? resource)
        {
            if (resource is null) return;
            _pending.Add(resource);
        }

        /// <summary>Retire up to three related resources in one call (a mesh's vertex buffer, index buffer and
        /// optional material set), so the caller needs no null checks and no per-resource statement.</summary>
        public void Retire(IDisposable? a, IDisposable? b, IDisposable? c)
        {
            Retire(a); Retire(b); Retire(c);
        }

        /// <summary>Advance one frame: destroy every batch the GPU has provably finished with, then seal whatever
        /// was retired during the frame just ended behind a fresh fence. A frame with nothing pending and nothing
        /// newly retired touches the device not at all.</summary>
        public void BeginFrame()
        {
            _frame++;
            // SEAL FIRST, then sweep. Two reasons, and the second is the one that is easy to get wrong.
            // (a) It keeps the fallback path bit-for-bit identical to the pre-fence per-entry scheme at every
            //     FrameDelay, including the degenerate FrameDelay of 1 where a batch was ripe on the very boundary
            //     that closed it. Sweeping first would have quietly added a frame there.
            // (b) It is safe on the fence path even though it polls a fence submitted moments earlier in the same
            //     call. A fence that already reads signaled means the GPU really has finished everything submitted
            //     before it, so freeing right then is correct rather than lucky. The sweep still cannot reach the
            //     new batch before every older one has gone, because it walks the list from the front and stops.
            SealNewBatch();
            FreeRipeBatches();
        }

        // Free the ripe PREFIX of _batches, stopping at the first batch that is not ripe. Stopping (rather than
        // skipping ahead to a later batch whose fence happens to be signaled) is load-bearing: it is what makes
        // "this batch died" imply "every older batch died first".
        void FreeRipeBatches()
        {
            // THE SAFETY VALVE (#425). Past the cap, stop asking the fences and pay one drain instead. A drain
            // waits out everything SUBMITTED, and every batch here was sealed behind a fence submitted at an
            // earlier boundary (or, for the batch sealed moments ago in this same call, at this one), so the drain
            // proves the lot of them complete and the whole holding is freed behind it. Freeing all of it rather
            // than just the oldest is what makes the cost one drain per MaxSealedBatches frames of fall-behind
            // instead of one per frame: trimming back to the cap would put the count over it again on the very
            // next retiring frame.
            //
            // Only where the policy permits a drain on the frame path. FrameCountOnly must not (#84), and does not
            // need to: its batches die on the count, which caps the holding at FrameDelay all by itself.
            bool valveOpen = _batches.Count > MaxSealedBatches && _fallback == GpuRetireFallback.DrainDevice;
            bool drained = false;
            if (valveOpen) { _drainDevice(); drained = true; ValveDrains++; }

            while (_batches.Count > 0)
            {
                Batch b = _batches[0];
                // Past the valve the ripeness question is already answered for every batch, so it is not asked.
                // Re-polling the fences instead would rest on a backend surfacing its signal by the time
                // WaitForIdle returns, which Metal signals from a completion handler and need not have run yet.
                if (!valveOpen)
                {
                    if (b.Fence is { } fence)
                    {
                        if (!fence.Signaled) break;                        // the GPU has not reached the seal point yet
                    }
                    else
                    {
                        if (_frame - b.MaxRetiredAt < FrameDelay) break;   // fallback: the pre-fence frame count
                        // One drain covers every batch freed here, and FrameCountOnly skips it entirely: that
                        // policy's whole point is that no WaitForIdle reaches the per-frame path (#84).
                        if (!drained && _fallback == GpuRetireFallback.DrainDevice) { _drainDevice(); drained = true; }
                    }
                }

                for (int i = 0; i < b.Count; i++) _pending[i].Dispose();
                _pending.RemoveRange(0, b.Count);
                _batched -= b.Count;
                _batches.RemoveAt(0);
                if (b.Fence is { } spent) _barrier!.Release(spent);
            }
        }

        // Seal what was retired since the last boundary into its own batch and mark the submission stream with a
        // fence. Appends, so the new batch is always behind every older one.
        void SealNewBatch()
        {
            int fresh = _pending.Count - _batched;
            if (fresh == 0) return;
            // The newest resource in this batch was retired while the counter read _frame - 1, so the fallback
            // ripeness test above stays the same "_frame - RetiredAt >= FrameDelay" the per-entry scheme used.
            _batches.Add(new Batch(fresh, _barrier?.Submit(), _frame - 1));
            _batched = _pending.Count;
        }

        /// <summary>Destroy everything pending right now, draining once first. For teardown, where waiting out a
        /// fence (or the frame delay) would leak the tail. This path keeps the drain on purpose even where fences
        /// are available, and even for a <see cref="CreateFrameCounted"/> queue: shutdown is the one place
        /// correctness is worth more than the stall, and a poll would have to spin.
        /// <para><b>TEARDOWN ONLY, and that is enforced rather than described.</b> Called with something pending
        /// while anything is recording on the device, it refuses with
        /// <see cref="GpuDrainDuringRecordingException"/> and frees nothing, because the drain it opens with says
        /// nothing about a list that has not been submitted, so the disposals behind it would be a use-after-free
        /// (see that type). The per-frame path is <see cref="Retire(System.IDisposable)"/> plus
        /// <see cref="BeginFrame"/>, neither of which drains.</para>
        /// <para><b>An EMPTY flush stays a no-op, deliberately, even mid-recording.</b> With nothing pending
        /// there is no drain and no disposal, so there is nothing for the refusal to protect, and refusing anyway
        /// would break the recovery path the seam's own nested-recording refusal creates: a capture refused
        /// mid-frame (<see href="https://github.com/APKiwiOrg/KhaozEngine/issues/424">#424</see>) tears down the
        /// half-built renderer it had already constructed, from inside the outer recording, and that teardown
        /// frees nothing. Turning that into a second exception would replace the useful diagnosis with an
        /// unrelated one and leak the renderer as well.</para></summary>
        /// <exception cref="GpuDrainDuringRecordingException">Something is recording on this queue's device and
        /// this queue has resources to free. Nothing was drained and nothing was destroyed.</exception>
        public void FlushAll()
        {
            // A batch always owns at least one entry, so an empty holding means there are no batches either and
            // there is nothing to drain for. This shortcut comes BEFORE the refusal below on purpose: see the
            // empty-flush paragraph on this member.
            if (_pending.Count == 0) return;

            // THE REFUSAL, on the one call that would actually drain and destroy, which is the only shape of this
            // that can be unsafe. Same shape as the seam's nested-recording refusal (#424): a device-level
            // operation that cannot be correct mid-recording says so by name rather than doing half of it.
            if (_device is { } device && GpuRecording.OpenOwner(device) is { } owner)
                throw new GpuDrainDuringRecordingException(owner);

            // Drain BEFORE anything else: it is what makes both the disposals below and the fence recycling safe
            // (a recycled fence gets Reset on its next submit, and resetting one still in flight is a validation
            // error, so the drain has to have retired every in-flight submission first).
            _drainDevice();
            for (int i = 0; i < _pending.Count; i++) _pending[i].Dispose();
            _pending.Clear();
            foreach (Batch b in _batches) if (b.Fence is { } fence) _barrier!.Release(fence);
            _batches.Clear();
            _batched = 0;
        }

        /// <summary>Flush everything pending, then free the barrier's own GPU objects. Renderer teardown, and it
        /// inherits <see cref="FlushAll"/>'s refusal exactly, including the empty case: tearing a renderer down
        /// from inside an open recording is the same use-after-free when there is a tail to free, and nothing at
        /// all when there is not.</summary>
        /// <exception cref="GpuDrainDuringRecordingException">Something is recording on this queue's device and
        /// this queue has resources to free. Nothing was freed, including the barrier.</exception>
        public void Dispose()
        {
            FlushAll();
            _barrier?.Dispose();
        }

        // One frame's worth of retirements, sealed at a frame boundary. A null fence means this batch is on the
        // frame-count fallback (no barrier at all, or a barrier that could not issue one).
        readonly struct Batch
        {
            public readonly int Count;
            public readonly IGpuFence? Fence;
            public readonly int MaxRetiredAt;
            public Batch(int count, IGpuFence? fence, int maxRetiredAt)
            {
                Count = count; Fence = fence; MaxRetiredAt = maxRetiredAt;
            }
        }
    }
}
