using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Diagnostics;

namespace KhaozEngine.Gpu.Metal.Internal
{
    /// <summary>
    /// THE DEVICE'S ONE COMPLETION TIMELINE, and everything the seam calls a fence sits on it: the value every
    /// submission signals, <see cref="IGpuFence"/>, the counted <c>WaitForIdle</c> drain, and the completion
    /// read row 8's uniform ring segment gate polls. Decisions M-F1 to M-F5.
    ///
    /// <para><b>WHY ONE DEVICE-WIDE <c>MTLSharedEvent</c>, because this is the type where that is decided.</b>
    /// The seam promises that a fence handed to a submission made after some earlier work signals only once the
    /// queue has drained through it. With a completion callback per submission that is a CONVENTION, because
    /// callback B firing says nothing at all about submission A, and Metal delivers completion handlers on an
    /// arbitrary internal thread in no guaranteed order. With one monotonic shared event it is a THEOREM: a
    /// queue's signal operations on one event execute in submission order and the values are monotonic, so the
    /// counter reaching 6 requires the signal at 5 to have happened, which requires submission 5 to have
    /// completed. Polling a later fence therefore transitively covers every earlier submission, which is exactly
    /// what <c>RetiredResourcePool</c> relies on and what row 8's segment gate reads. That is V-F2's argument
    /// reaching the same conclusion because it is the same primitive under a different name (M-F4). Stated
    /// precisely, so a gap cannot be read as a hole in it: the counter reaching V implies every submission that
    /// SIGNALS a value at or below V has completed, and a value nobody ever encoded signals nothing and covers
    /// nothing (see <see cref="LastSubmitted"/>).</para>
    ///
    /// <para><b>THE COMPLETION HANDLER IS NOT DELETED ALONG WITH THE FENCE DICTIONARY (M-F2).</b> M-G4 requires
    /// reading <c>MTLCommandBuffer.status</c> and <c>.error</c> at completion in every configuration, so a
    /// handler is registered per submitted command buffer whatever the fence primitive is. The responsibilities
    /// split cleanly and the split is the ruling: the shared event owns ORDERING and
    /// <see cref="MetalCompletionHandler"/> owns REPORTING. That handler takes no lock, touches no dictionary,
    /// sets no event and carries no ordering responsibility at all, which is the answer to the arbitrary
    /// delivery thread. A design that advanced a counter from that callback with <c>++</c> would be depending on
    /// an unstated ordering fact, and one that advanced it with an interlocked maximum would be correct and
    /// would still be re-deriving what the shared event gives for free.</para>
    ///
    /// <para><b>EVERYTHING HERE IS DEVICE-FREE.</b> The native calls are three members on
    /// <see cref="IMetalSharedEvent"/> and the liveness is <see cref="IMetalDeviceLiveness"/>, so the value
    /// allocation, the fence lifecycle, the dead-device answers and the drain accounting all run on a machine
    /// with no Metal at all, which is what lets them run on the Linux and both Windows legs. What is NOT
    /// exercised device-free is a value being signalled because REAL GPU WORK finished, which needs a live queue
    /// and is what <see cref="MetalTimelineProbe"/> measures under a <c>[GpuFact]</c>.</para>
    ///
    /// <para><b>AFTER DEVICE DEATH EVERY ANSWER IS "DONE" (M-F6).</b> <see cref="CompletedValue"/> reports the
    /// last value ever allocated instead of reading a dead device's event, so every fence reads signalled and
    /// every waiter is released. Answering anything else would strand a retire pool forever on a batch it can
    /// never free, which is a teardown-order hazard rather than a hypothetical.</para>
    ///
    /// <para><b>TWO HIGH-WATERS, AND THEY ARE FOR DIFFERENT QUESTIONS.</b> <see cref="LastAllocated"/> is every
    /// value ever handed out and gates disposal-time questions. <see cref="LastSubmitted"/> is every value a
    /// commit actually accepted and is what <c>WaitForIdle</c> targets. They differ by exactly the submissions
    /// that failed, and keeping them apart is what makes a failed submit unable to hang the next drain. Each
    /// property carries its own argument.</para>
    /// </summary>
    internal sealed class MetalTimeline : IDisposable
    {
        static readonly ILogger log = Log.For<MetalTimeline>();

        /// <summary>
        /// How long one <c>waitUntilSignaledValue:timeoutMS:</c> attempt blocks before
        /// <see cref="WaitForIdle"/> re-checks device liveness and goes round again. See that member for why the
        /// drain is sliced at all rather than passing a timeout nobody expects to reach.
        /// </summary>
        internal const ulong DrainSliceMs = 250;

        /// <summary>
        /// PARITY, NOT AN UPGRADE, and that is worth stating where a reader will look for it (M-F4).
        /// <c>KhaozEngine.Gpu.Internal.VeldridMap.SupportsCompletionFences</c> already answers true for
        /// <c>GraphicsBackend.Metal</c>, with a doc comment explaining that Metal registers the fence against the
        /// command buffer and sets it from the completion handler. Phase 2's C5 was an UPGRADE because Veldrid's
        /// Direct3D 11 fence is a submit receipt rather than a completion signal, so nobody should look for that
        /// win twice here. The consequence for the gates is that <c>RetireFenceGpuTests</c> and
        /// <c>Scene3DUnloadDrainTests</c> already RUN on this leg, so the criterion is NO NEW SKIPS rather than
        /// two fewer.
        /// <para>
        /// It is a constant rather than a question because a shared event is unconditionally a real completion
        /// signal. Row 16 (https://github.com/APKiwiOrg/KhaozEngine/issues/582) reads it from here when it
        /// assembles the capability struct, which is the same shape <c>D3D11FenceSubsystem</c> uses: the
        /// subsystem that owns the mechanism owns the answer.
        /// </para>
        /// </summary>
        internal const bool SupportsCompletionFences = true;

        readonly IMetalSharedEvent _event;
        readonly IMetalDeviceLiveness _liveness;

        // The last value ALLOCATED to a submission, which is not the same as the last value the GPU has reached
        // and not the same as the last value a submission actually took to the queue. Read after device death in
        // place of asking the event, since everything issued is complete by then anyway.
        ulong _issued;

        // The highest value a commit ACCEPTED, raised by RegisterSubmitted and never by the allocation. See
        // LastSubmitted for why the two are different fields.
        ulong _submitted;

        long _totalDrainCount;
        long _totalDrainTicks;

        // Volatile because CompletedValue reads it from whatever thread is polling a fence, which is not the
        // teardown thread that writes it.
        volatile bool _disposed;

        /// <param name="sharedEvent">The device's shared event. Disposed with this timeline.</param>
        /// <param name="liveness">The device's liveness token, or null while a caller does not have one, in
        /// which case the device is treated as alive forever. See <see cref="IMetalDeviceLiveness"/> for why
        /// defaulting to alive is the safe direction.</param>
        internal MetalTimeline(IMetalSharedEvent sharedEvent, IMetalDeviceLiveness? liveness = null)
        {
            ArgumentNullException.ThrowIfNull(sharedEvent);

            _event = sharedEvent;
            _liveness = liveness ?? MetalLiveDevice.Instance;
        }

        /// <summary>The device's liveness, read through the hook. Fences read it before anything else.</summary>
        internal bool IsDeviceDead => _liveness.IsDead;

        /// <summary>
        /// THE HIGHEST VALUE A COMMIT ACTUALLY ACCEPTED, and therefore the value the GPU has to reach for every
        /// submission ever MADE to have completed. 0 before anything has been submitted.
        /// <para>This is what <c>WaitForIdle</c> waits for, which is the whole of M-F5: "the GPU is idle" and
        /// "the counter has reached the last value a submission took to the queue" are the same statement on a
        /// device with one queue that all work goes through (M-N2).</para>
        /// <para>
        /// IT IS THE REGISTERED HIGH-WATER AND NOT THE ALLOCATION HIGH-WATER, which is the structural fix phase
        /// 3 took at its row 7 (https://github.com/APKiwiOrg/KhaozEngine/issues/517) and this row inherits
        /// rather than rediscovers. A submission allocates its value, encodes the signal and then commits, and
        /// anything between the allocation and a successful commit can throw. If this property reported the
        /// allocation instead, a value nothing will ever signal would sit permanently above anything the GPU can
        /// reach and the next drain would block until its liveness check released it. Raising it only after the
        /// commit returned makes the target reachable BY CONSTRUCTION rather than by a repair that has to run
        /// correctly on the worst path in the backend.
        /// </para>
        /// <para>
        /// THE GAP IS HARMLESS AND THE THEOREM SURVIVES IT. A value nobody signals is a hole in the value SPACE,
        /// not in the ORDER: a later submission signalling a higher value still leaves the sequence strictly
        /// increasing, because the counter steps over the hole. What the one-timeline theorem says is that the
        /// counter reaching V implies every submission signalling a value at or below V has completed, and a
        /// submission that was never made signals nothing and has nothing to cover.
        /// </para>
        /// </summary>
        internal ulong LastSubmitted => Volatile.Read(ref _submitted);

        /// <summary>
        /// The highest value ever handed out by <see cref="EncodeSignalForSubmit"/>, whether or not its commit
        /// succeeded. 0 before anything has been allocated.
        /// <para>
        /// THIS IS THE CONSERVATIVE BOUND and <see cref="LastSubmitted"/> is not, which is the one place the two
        /// readings genuinely differ in what they are FOR. A caller asking "could any submission still reference
        /// me" wants the most conservative answer, and the allocation high-water is it: a submission whose value
        /// was taken but whose commit has not returned yet is invisible to the registered high-water for a few
        /// instructions, and answering with the lower number in that window would say idle about work that is
        /// about to run.
        /// </para>
        /// <para>
        /// ON THIS BACKEND NOTHING GATES A DESTROY ON IT, which is the difference from the Vulkan sibling worth
        /// naming here rather than leaving a reader to infer. There is no retire list (M-H3): an
        /// <c>MTLCommandBuffer</c> retains every resource its encoders reference until it completes, so a
        /// resource disposed while a submitted buffer still references it stays alive and Objective-C reference
        /// counting does what V-F9's deferred-disposal machinery does over there. This property is kept because
        /// the two questions are genuinely different and because the dead-device answer below is defined in
        /// terms of it, not because something here is waiting on it.
        /// </para>
        /// </summary>
        internal ulong LastAllocated => Volatile.Read(ref _issued);

        /// <summary>
        /// The SAME drains accumulated since the device was created, which is the half a telemetry session can
        /// carry. Nothing rolls it, so two sampled rows bracket a window exactly. Row 16 reads it for
        /// <c>GpuDeviceCounters.DrainCount</c> and <c>DrainMs</c>. See <see cref="MetalWaitTotals"/>.
        /// </summary>
        internal MetalWaitTotals TotalDrain => MetalWaitTotals.Sample(ref _totalDrainCount, ref _totalDrainTicks);

        /// <summary>
        /// The counter's current value, as a NON-BLOCKING read, or the last value allocated once the device is
        /// dead.
        /// <para>
        /// LIVENESS IS CHECKED ON BOTH SIDES OF THE READ, and the second check is the one worth reading twice.
        /// The device can die WHILE this read is in flight, because M-G4's error latch flips liveness from
        /// Metal's own completion thread, in which case the number the driver handed back describes a device
        /// that is already gone. Asking again afterwards is what stops that number reaching a fence, and it
        /// costs one volatile read on a path that just crossed the Objective-C boundary.
        /// </para>
        /// <para>
        /// DISPOSAL IS CHECKED FIRST, AS DEFENCE IN DEPTH RATHER THAN AS THE ONLY DEFENCE. M-F6's teardown order
        /// flips liveness BEFORE this timeline is disposed, so the liveness check below already covers every
        /// poll a correctly ordered teardown can produce, and this guard is unreachable on that path. What it
        /// covers is the path where the order was not honoured: <see cref="Dispose"/> releases the
        /// <c>MTLSharedEvent</c> unconditionally, so a fence polled after disposal without the flip would send
        /// <c>signaledValue</c> to a released Objective-C object, which is a use-after-free rather than a wrong
        /// number. The documented order stays the contract and this stops a violation of it from being
        /// unsurvivable. The disposed answer is the dead answer, for M-F6's reason: a timeline that is gone has
        /// nothing left to finish.
        /// </para>
        /// </summary>
        internal ulong CompletedValue
        {
            get
            {
                // LastAllocated rather than LastSubmitted on the dead path, because "after death every answer is
                // done" has to release every waiter, and the allocation high-water can sit above the registered
                // one. Answering with the smaller of the two would leave exactly the fences armed in that window
                // unreleased at exactly the moment nothing can ever advance the counter again.
                if (_disposed || _liveness.IsDead) return LastAllocated;

                ulong read = _event.Read();
                return _liveness.IsDead ? LastAllocated : read;
            }
        }

        /// <summary>
        /// Take the next value for a submission and encode its signal into <paramref name="commandBuffer"/>
        /// (M-F1), returning the value that buffer's completion will reach. The ONLY place a value is ever
        /// handed out.
        /// <para>
        /// ALLOCATION AND ENCODE ARE ONE STEP ON PURPOSE, which is the shape difference from the Vulkan sibling,
        /// where the value is allocated and then named in the submit struct. Metal encodes the signal INTO the
        /// buffer before commit, so there is no second place the pair could come apart, and a value that was
        /// allocated but never encoded would be a hole the caller has no way to create by accident.
        /// </para>
        /// <para>
        /// THREAD-SAFE BECAUSE THE SUBMIT PATH IS. <see cref="Interlocked.Increment(ref ulong)"/> is the whole
        /// mechanism for the allocation, and it is why this is not merely <c>_issued + 1</c>: two submissions
        /// sharing a value would make a fence on the first read signalled when only the second had finished.
        /// </para>
        /// <para>
        /// <b>PRECONDITION ON THE CALLER, AND ROW 7 SATISFIES IT</b>
        /// (https://github.com/APKiwiOrg/KhaozEngine/issues/573). This must be called INSIDE the lock that
        /// orders <c>commit</c>, and every submit must call it. Metal command buffers execute in ENQUEUE order
        /// on a queue and <c>commit</c> enqueues, so committing under one lock is what makes submit order the
        /// observable order (M-N2), and encoding inside that same lock is what stops two threads encoding in one
        /// order and committing in another. That is the half of the precondition this member cannot enforce and
        /// the whole reason the theorem in this type's summary holds.
        /// </para>
        /// </summary>
        /// <param name="commandBuffer">The <c>MTLCommandBuffer</c> about to be committed.</param>
        /// <returns>The value that submission signals.</returns>
        internal ulong EncodeSignalForSubmit(IntPtr commandBuffer)
        {
            ulong value = Interlocked.Increment(ref _issued);
            _event.EncodeSignal(commandBuffer, value);
            return value;
        }

        /// <summary>
        /// Record that a <c>commit</c> ACCEPTED <paramref name="value"/>, raising <see cref="LastSubmitted"/> to
        /// it. Called by row 7's submit path immediately after the commit returned, inside the same lock the
        /// value was allocated in, and by nothing else.
        /// <para>
        /// MONOTONIC BY ASSERTION RATHER THAN BY ARITHMETIC. The submit lock already orders allocation and
        /// registration together, so values arrive here in increasing order and a plain write would be correct.
        /// The comparison is kept anyway because it is free next to a driver call and because it turns a future
        /// caller that registers out of order into a value that stays put rather than a counter that goes
        /// backwards, and a target that went backwards would release a fence over work that has not finished.
        /// </para>
        /// </summary>
        /// <param name="value">The value the committed submission will signal.</param>
        internal void RegisterSubmitted(ulong value)
        {
            if (value > Volatile.Read(ref _submitted)) Volatile.Write(ref _submitted, value);
        }

        /// <summary>A fresh, unarmed fence on this timeline. The seam's <c>IGpuResourceFactory.CreateFence</c>
        /// lands here when row 6 (https://github.com/APKiwiOrg/KhaozEngine/issues/572) builds the factory, and
        /// there is no capability gate in front of it, because <see cref="SupportsCompletionFences"/> is
        /// unconditionally true on this backend.</summary>
        internal MetalGpuFence CreateFence() => new(this);

        /// <summary>
        /// THE DRAIN (M-F5): <c>waitUntilSignaledValue:timeoutMS:</c> on the last submitted value, counted into
        /// <c>DrainCount</c> and <c>DrainMs</c>.
        ///
        /// <para><b>NOT <c>waitUntilCompleted</c> ON A RETAINED LAST COMMAND BUFFER</b>, which is what the
        /// incumbent does. That needs the buffer kept alive under a lock to be read at all and gives nothing to
        /// count without extra bookkeeping, where a value on the timeline is both the thing to wait for and the
        /// thing to time. <b>There is no C6-style bet here</b>: the incumbent's drain is already real, and phase
        /// 2's win was in making an empty method body exist, so nobody should look for that win twice.</para>
        ///
        /// <para><b>IT WAITS IN SLICES, AND THAT IS THIS BACKEND'S ONE ADDITION TO M-F5's SENTENCE.</b> The
        /// Vulkan sibling waits with an infinite timeout and argues that blocking forever on a hung GPU is the
        /// honest behaviour, and that argument is kept: this member blocks for as long as the GPU takes, and a
        /// slice expiring is not forward progress. What the slice buys is the DEAD-DEVICE case, which is a
        /// different thing entirely on Metal. Here a command-buffer failure is asynchronous: the signal never
        /// arrives, and the only notification is M-G4's error latch flipping liveness from Metal's own
        /// completion thread. A single unbounded wait would not observe that flip and would block until the
        /// process was killed, on the exact teardown path M-F6 exists to keep clear. So the wait is re-issued
        /// until it reaches the value or liveness says there is nothing left to wait for. Metal's call takes a
        /// timeout where Vulkan's does not, which is why this costs nothing to spell.</para>
        ///
        /// <para><b>THE SLICE HAS ONE OBSERVABLE COST, AND IT IS <see cref="DrainSliceMs"/> OF TEARDOWN
        /// LATENCY.</b> The liveness flip is not delivered to a blocked waiter, so a drain in flight when the
        /// device dies keeps blocking until its CURRENT slice expires, up to 250ms after the flip. Nothing else
        /// changes: a healthy drain returns the moment the value arrives, and a shorter slice would only trade
        /// that latency for more native calls on every long drain. Teardown after a device loss is the one path
        /// that pays it, and 250ms there is the price of the flip being observable at all.</para>
        ///
        /// <para><b>THREE CASES RETURN WITHOUT COUNTING, and each is a case where nothing blocked.</b> The
        /// device is dead (M-F6, a dead device has nothing to wait for). Nothing has ever been submitted, so
        /// there is no point on the timeline to wait for at all, which is also the state a device that has only
        /// ever FAILED a submit is in. And the counter has ALREADY passed the last submitted value, which is a
        /// caller who asked and found the GPU caught up. The seam's own <c>DrainCount</c> doc says a wait that
        /// found the GPU already caught up is not counted, and this is a backend where honouring that costs one
        /// non-blocking read, because the target is the last SUBMITTED value rather than a fresh point signalled
        /// per drain.</para>
        ///
        /// <para><b>A WAIT THAT ENDED BECAUSE THE DEVICE DIED IS STILL COUNTED.</b> It blocked, for the time
        /// recorded, and dropping it would under-report exactly the drains a post-mortem cares about. What is
        /// not counted is a wait that never happened.</para>
        ///
        /// <para><b>THE COUNTERS ARE ACCUMULATED WITH <see cref="Interlocked"/></b>, because this drain
        /// deliberately holds no lock, so two threads can be inside it at once and a plain <c>++</c> would lose
        /// entries. It costs two interlocked adds on a path that just spent milliseconds blocked.</para>
        /// </summary>
        internal void WaitForIdle()
        {
            if (_liveness.IsDead) return;

            ulong target = LastSubmitted;
            if (target == 0) return;
            if (CompletedValue >= target) return;

            long start = Stopwatch.GetTimestamp();
            bool reached = WaitInSlices(target);
            long elapsed = Stopwatch.GetTimestamp() - start;

            Interlocked.Increment(ref _totalDrainCount);
            Interlocked.Add(ref _totalDrainTicks, elapsed);

            // A false is the device having died mid-drain, which the error latch already reported with the
            // command buffer's own status and error. This line says what it meant for the CALLER, which the
            // latch cannot know: the drain returned without the GPU having reached the point that was asked for.
            // It can fire at most once per device, because every later drain returns at the liveness check.
            if (!reached)
            {
                log.Warn($"The native Metal backend's WaitForIdle stopped waiting for timeline value {target} "
                    + "because the device is dead. The work that was outstanding has not completed and never "
                    + "will. Every fence on this device now reads signalled and every later drain returns "
                    + "immediately, which is what releases anything waiting on it during teardown.");
            }
        }

        /// <summary>
        /// BLOCK UNTIL THE COUNTER REACHES <paramref name="target"/>, in the same slices
        /// <see cref="WaitForIdle"/> uses and for the same reason, and return whether it got there. The uniform
        /// ring's segment gate is the caller (M-M3, https://github.com/APKiwiOrg/KhaozEngine/issues/574) and it
        /// is the only one.
        ///
        /// <para><b>IT DOES NOT TOUCH <c>DrainCount</c> OR <c>DrainMs</c>, AND THAT IS THE POINT OF IT BEING A
        /// SEPARATE MEMBER RATHER THAN <see cref="WaitForIdle"/> WITH AN ARGUMENT.</b> Those two channels are the
        /// seam's reading of explicit device drains, which is a caller asking the CPU to stop until the GPU has
        /// caught up. A segment stall is the opposite reading: nobody asked for it, and it says the pipeline is
        /// deeper than <c>KE_METAL_FRAMES_IN_FLIGHT</c> allows. It goes to
        /// <c>BackpressureStallCount</c> and <c>BackpressureStallMs</c> through
        /// <see cref="MetalBackpressure"/>, which the ring records into, and mixing the two would make MM4's
        /// zero-stall exit criterion unreadable behind whatever drains the frame loop happens to do.</para>
        ///
        /// <para><b>THE CALLER TIMES IT rather than this member</b>, for the same reason: the accumulator that
        /// gets the entry belongs to the ring, and a member that both waited and recorded would have to know
        /// which of the two accumulators to write into.</para>
        ///
        /// <para><b>THE CALLER ALSO POLLS FIRST.</b> A wait that found the GPU already caught up is not a stall,
        /// and this member has no way to distinguish the two: <c>waitUntilSignaledValue:timeoutMS:</c> returns
        /// true immediately in that case, and the elapsed time is a few microseconds rather than zero.</para>
        /// </summary>
        /// <param name="target">The timeline value to wait for.</param>
        /// <returns>True when the counter reached it. False when the wait ended because the device died, which
        /// on this backend is a command-buffer failure latched from Metal's own completion thread, so the value
        /// being waited for will never arrive at all.</returns>
        internal bool WaitForValue(ulong target)
        {
            if (target == 0) return true;
            if (_liveness.IsDead) return false;

            return WaitInSlices(target);
        }

        // The slice loop. See WaitForIdle's third paragraph for why it is a loop rather than one wait: the only
        // way out other than the value arriving is the liveness flip, and nothing delivers that to a blocked
        // waiter.
        bool WaitInSlices(ulong target)
        {
            while (true)
            {
                if (_event.WaitUntil(target, DrainSliceMs)) return true;
                if (_liveness.IsDead) return false;
            }
        }

        /// <summary>
        /// Release the shared event, ONCE. Called from the device's teardown after its drain and after the
        /// liveness flip, which is M-F6's order.
        /// <para>
        /// THE RELEASE IS NOT GATED ON LIVENESS, unlike the Vulkan sibling's, and the difference is Metal's
        /// object model rather than an oversight. <c>vkDestroyDevice</c> destroys every object made from the
        /// device, so a destroy after it aborts the process through the loader, which is why that backend skips
        /// its native destroy on a dead device. An <c>MTLSharedEvent</c> is an ordinary reference-counted
        /// Objective-C object with no such rule, so skipping the release here would leak it on exactly the
        /// teardown path that matters. See <see cref="MetalSharedEvent.Dispose"/>.
        /// </para>
        /// <para>
        /// OUTSTANDING FENCES HOLD NO OBJECT OF THEIR OWN, so they simply stop being able to observe anything
        /// new. A poll after this point reads through the liveness token, which teardown flipped first, and
        /// through <see cref="CompletedValue"/>'s disposal guard if it did not. That guard also covers
        /// <see cref="WaitForIdle"/> for free: its target is <see cref="LastSubmitted"/>, which can never exceed
        /// <see cref="LastAllocated"/>, so the caught-up early return fires before the slice loop can touch a
        /// released event.
        /// </para>
        /// </summary>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            _event.Dispose();
        }
    }
}
