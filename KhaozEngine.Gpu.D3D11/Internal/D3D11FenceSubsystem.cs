using System;
using System.Diagnostics;
using System.Threading;
using KhaozEngine.Gpu.Internal;

namespace KhaozEngine.Gpu.D3D11.Internal
{
    /// <summary>
    /// EVERYTHING THE SEAM CALLS A FENCE, in one device-owned object: the capability answer, the fence factory,
    /// the signal the replay tail raises, the real <c>WaitForIdle</c> drain and the per-frame drain telemetry.
    /// Decisions C5 and C6 together.
    /// <para>
    /// It sits on ONE <see cref="ID3D11FenceTimeline"/> and adds the parts that are engine logic rather than
    /// native calls, which is why nearly all of it is tested on macOS and Linux: the target lifecycle, the drain
    /// loop, the kill switch, the liveness behaviour and the counters are all driven through a fake timeline.
    /// </para>
    ///
    /// <para><b>The submit lock, and the one poll that does not take it.</b> The lock is the device's (decision
    /// W4) and is passed in rather than created here. A SIGNAL always takes it, on both mechanisms, because
    /// signalling is a context call, and so does the drain's one flush. A POLL takes it only where the mechanism
    /// needs it: the monotonic fence's <c>GetCompletedValue</c> is a read on a free-threaded fence object and is
    /// done lock-free, while the event-query fallback polls the IMMEDIATE CONTEXT and cannot be.</para>
    ///
    /// <para><b>That difference is deliberately visible rather than smoothed over, and it is the fallback's one
    /// honest deviation.</b> The seam documents that a fence poll never waits. On the primary mechanism, which is
    /// every machine from Windows 10 1703 on, that holds exactly: <c>IGpuFence.Signaled</c> takes no lock and
    /// waits for nothing. On the event-query fallback a poll from a thread that is not the submitting one can
    /// wait on the submit lock, and under W4 that lock covers a whole replay, so the wait can be a replay long.
    /// Making the primary path pay that too, purely so the two read alike, would be paying a real cost on every
    /// machine to hide a difference on almost none.</para>
    ///
    /// <para><b>The drain holds the lock for its signal and its flush, and for nothing else</b>, which is the one
    /// piece of the loop below worth reading twice. It signals and flushes under the lock, then waits or polls
    /// with the lock released, so the work it is waiting for can actually be submitted by another thread. A drain
    /// that held the lock throughout would deadlock against exactly the submission that would let it finish.
    /// </para>
    ///
    /// <para><b>A caller who already holds the lock is refused, and that refusal is the LAST of the three checks
    /// <see cref="WaitForIdle"/> makes rather than the first.</b> The dead-device return (X3) and the
    /// <c>KE_D3D11_REAL_DRAIN</c> return come ahead of it. Both do nothing at all, so running them with the lock
    /// held is safe by construction, and putting the guard first would break both promises: a teardown-shaped
    /// caller holding the lock on a dead device would get an exception where X3 promises a quiet no-op, and the
    /// kill switch would stop restoring the empty method body it exists to restore. The guard therefore protects
    /// the LIVE drain, which is the only path that can actually hang.</para>
    ///
    /// <para><b>Not thread-safe for its own counters.</b> The telemetry accumulators are driven from the frame
    /// thread, the same contract <c>GpuRetireQueue</c> and the water renderer's counters already have.</para>
    /// </summary>
    internal sealed class D3D11FenceSubsystem : IDisposable, ID3D11SubmitSignal, ID3D11CompletionRead
    {
        // How long ONE blocking wait slice lasts on a mechanism that has a blocking wait. It is not a drain
        // timeout, and the drain deliberately has none: a wait that is satisfied returns the instant the GPU
        // raises the counter, and the slice only bounds how long an unsatisfied one goes before the loop
        // re-checks device liveness. Short enough that a device dying mid-drain releases the caller promptly,
        // long enough that a genuinely hung GPU does not spend the CPU re-arming the wait.
        const int DrainWaitSliceMs = 4;

        readonly ID3D11FenceTimeline _timeline;
        readonly object _submitLock;
        readonly IDeviceLiveness _liveness;
        readonly bool _realDrain;

        // The last value handed out by the timeline through this subsystem. Read after device death in place of
        // asking the timeline, since a destroyed device's objects must not be touched and everything issued is
        // complete by then anyway.
        ulong _issued;

        int _drainCount;
        long _drainTicks;
        D3D11DrainStats _lastFrame;

        // The same drains, never rolled. See WaitTotals for why the per-frame roll alone cannot answer the
        // M2 question once a telemetry session samples it on its own cadence. Two plain fields rather than the
        // pair struct, so TotalDrain can read each half volatile for a sampler on another thread.
        long _totalDrainCount;
        long _totalDrainTicks;

        /// <summary>
        /// Build the subsystem over <paramref name="timeline"/>, taking ownership of it.
        /// </summary>
        /// <param name="timeline">The device's completion timeline. Disposed with this subsystem.</param>
        /// <param name="submitLock">The device's single submit lock (decision W4). Not created here, because the
        /// same lock has to cover replay, present and the resize apply.</param>
        /// <param name="liveness">The device's liveness latch, or null while the device does not have one yet, in
        /// which case the device is treated as alive forever. See <see cref="IDeviceLiveness"/>.</param>
        /// <param name="realDrain">Whether <see cref="WaitForIdle"/> really drains. Resolved from
        /// <see cref="D3D11RealDrain"/> by the caller rather than read from the environment here, so the
        /// behaviour is testable without touching process state.</param>
        internal D3D11FenceSubsystem(
            ID3D11FenceTimeline timeline,
            object submitLock,
            IDeviceLiveness? liveness = null,
            bool realDrain = true)
        {
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _submitLock = submitLock ?? throw new ArgumentNullException(nameof(submitLock));
            _liveness = liveness ?? LiveDevice.Instance;
            _realDrain = realDrain;
        }

        /// <summary>
        /// THE ONE PERMITTED CAPABILITY DIFFERENCE from the incumbent Direct3D 11 backend (decision C5, section
        /// 11): true, on BOTH mechanisms. Read from here by the native device's capability assembly.
        /// <para>
        /// It is a constant rather than a question because both timelines are real completion signals. The
        /// incumbent reports false for a specific reason, which is that Veldrid's Direct3D 11 fence is a
        /// <c>ManualResetEvent</c> set when <c>ExecuteCommandList</c> returns, so it says the SUBMISSION happened
        /// and not that the GPU finished. Nothing here has that shape, so nothing here has to report it.
        /// </para>
        /// <para>
        /// Turning this true is what flips four things downstream on the day the native device lands:
        /// <c>GpuRetireBarrier.TryCreate</c> stops returning null, <c>GpuRetireQueue</c> takes its fenced
        /// path instead of the frame-count fallback, and <c>RetireFenceGpuTests</c> and
        /// <c>Scene3DUnloadDrainTests</c> stop skipping.
        /// </para>
        /// </summary>
        internal bool SupportsCompletionFences => true;

        /// <summary>Which timeline mechanism this device got, for the session log. See
        /// <see cref="D3D11FenceMechanism"/>.</summary>
        internal D3D11FenceMechanism Mechanism => _timeline.Mechanism;

        /// <summary>Whether <see cref="WaitForIdle"/> really drains on this run, or is the no-op the
        /// <c>KE_D3D11_REAL_DRAIN</c> kill switch restores.</summary>
        internal bool RealDrainEnabled => _realDrain;

        /// <summary>The device's liveness, read through the hook. Fences read it before anything else.</summary>
        internal bool IsDeviceDead => _liveness.IsDead;

        /// <summary>The drains of the frame that has ENDED. Rolled by <see cref="BeginFrame"/>. This is the M2
        /// measurement.</summary>
        internal D3D11DrainStats LastFrameDrain => _lastFrame;

        /// <summary>
        /// The SAME drains accumulated since the device was created, which is the half a telemetry session can
        /// carry. <see cref="BeginFrame"/> never rolls it, so two sampled rows bracket a window exactly and M2's
        /// per-frame figure is their difference over the frames between them. See <see cref="WaitTotals"/>.
        /// <para>
        /// READ A FIELD AT A TIME, because the sampler asking for it is on whatever thread the consumer runs its
        /// telemetry on while the frame thread records drains. Each half is whole, the PAIR may be one drain apart.
        /// </para>
        /// </summary>
        internal WaitTotals TotalDrain => WaitTotals.Sample(ref _totalDrainCount, ref _totalDrainTicks);

        /// <summary>
        /// The timeline's completed value, lock-free where the mechanism allows it and under the submit lock
        /// where it does not (see the class note). After device death this answers with the last value issued
        /// instead of touching the timeline, so everything ever signalled reads complete and no call reaches a
        /// destroyed device's objects.
        /// <para>
        /// PUBLIC BECAUSE IT IS <see cref="ID3D11CompletionRead"/>, which is the read half of the timeline that
        /// the constant-buffer ring recycles a segment against (decision U5). That interface is the whole reason
        /// row 8 depends on this row: a ring gated on a submit RECEIPT hands out a segment the GPU is still
        /// reading. The dead-device answer above is load-bearing for it too, since it is what releases a segment
        /// wait during teardown.
        /// </para>
        /// </summary>
        public ulong CompletedValue
        {
            get
            {
                if (_liveness.IsDead) return _issued;

                return ReadCompleted();
            }
        }

        /// <summary>A fresh, unarmed fence. The seam's <c>IGpuResourceFactory.CreateFence</c> lands here, and
        /// unlike the Veldrid device there is no capability gate in front of it, because
        /// <see cref="SupportsCompletionFences"/> is unconditionally true.</summary>
        internal IGpuFence CreateFence() => new D3D11GpuFence(this);

        /// <summary>
        /// THE SIGNAL AT THE END OF REPLAY (decision C5), and the entry point the replay tail calls. Advances the
        /// timeline by one, arms <paramref name="fence"/> with the value if one was handed to the submission, and
        /// returns that value.
        /// <para>
        /// The replay tail reaches it through <see cref="ID3D11SubmitSignal"/>, which
        /// <see cref="D3D11CommandDrivers.Submit{TEmitter}"/> raises after the last command of the submission and
        /// inside the submit lock. The interface exists so the submit path can be driven with no timeline behind
        /// it at all, and this subsystem is its one shipped implementation.
        /// </para>
        /// <para>
        /// CALL IT ONCE PER SUBMIT, AFTER the last command of the replay has been emitted and while the submit
        /// lock is held. The lock is re-entrant, so taking it again here costs nothing when the caller already
        /// holds it, which is the normal case. Placing the call before the last command instead would signal a
        /// point the GPU reaches before the submission is finished, and a fence polled at that point would report
        /// work complete that has not been issued yet.
        /// </para>
        /// <para>
        /// A SUBMIT WITH NO FENCE STILL SIGNALS, deliberately. The timeline has to advance with the submission
        /// stream for a later fence's value to cover the earlier work at all, and the constant-buffer ring's
        /// segment recycling reads the same counter. A signal costs one native call, which is the right price for
        /// making every submission a point the timeline can name.
        /// </para>
        /// <para>After device death this is a no-op that returns the last value issued, matching every other
        /// member (decision X3).</para>
        /// <para>
        /// THE ERROR PATHS ADVANCE THE TIMELINE, and that is the behaviour rather than an oversight. By the time
        /// <c>Arm</c> can throw, the submission has already consumed a timeline value, so that value is spent and
        /// the next signal takes the one after it. Monotonicity is the property that matters and it is preserved:
        /// no value is handed out twice, and a spent one leaves a gap that nothing reads. The rejected
        /// submission's fence is left exactly as it was, unarmed if it was unarmed and holding its earlier target
        /// if it was already armed, so a throw never quietly retargets a fence.
        /// </para>
        /// <para>
        /// THE FOREIGN-FENCE CHECK RUNS BEFORE THE LIVENESS CHECK, so a fence from another backend is rejected
        /// even after the device has died, where every other path here is a quiet no-op (X3). Handing this device
        /// another backend's fence is a programming error at any point in a device's life, and teardown is
        /// exactly where going quiet about it would hide it: that is when the retire pool is still running, and a
        /// fence it believes was armed is one it waits on forever.
        /// </para>
        /// </summary>
        /// <param name="fence">The fence handed to <c>Submit(IGpuCommandList, IGpuFence)</c>, or null for the
        /// fenceless <c>Submit(IGpuCommandList)</c>.</param>
        /// <returns>The timeline value this submission signalled.</returns>
        public ulong SignalEndOfReplay(IGpuFence? fence)
        {
            D3D11GpuFence? own = null;
            if (fence is not null)
            {
                own = fence as D3D11GpuFence
                    ?? throw new ArgumentException(
                        $"A {fence.GetType().Name} was handed to the native Direct3D 11 device as a fence. Only a "
                        + "fence this backend created can be armed, because a fence from another backend has "
                        + "another backend's completion signal behind it.", nameof(fence));
            }

            if (_liveness.IsDead) return _issued;

            lock (_submitLock)
            {
                _issued = _timeline.Signal();
                own?.Arm(_issued);
                return _issued;
            }
        }

        /// <summary>
        /// THE REAL <c>WaitForIdle</c> (decision C6): signal a fresh point and poll until the GPU reaches it.
        /// Replaces the empty method body the Veldrid Direct3D 11 backend has always had.
        /// <para>
        /// It returns immediately, and counts nothing, in the two cases where it is deliberately not a drain: the
        /// device is dead (X3, a destroyed device has nothing to wait for) and the <c>KE_D3D11_REAL_DRAIN</c>
        /// kill switch is down. BOTH ARE CHECKED FIRST, ahead of the submit-lock guard below, because neither
        /// touches anything and a caller holding the lock is therefore harmless to them. Everything else drains
        /// and is counted, because the point of the counters is to price what the drain costs.
        /// </para>
        /// <para>
        /// THE SIGNAL IS FLUSHED, EXACTLY ONCE. The immediate context buffers commands, so a signal placed at the
        /// tail of a buffer the driver has not been handed is a point the GPU may never reach, and a drain
        /// waiting on it would wait for something nobody asked for. One flush per drain, after the signal and
        /// before the first poll, is what makes the wait terminate. The fence poll on the seam side stays
        /// non-flushing, because that one must stay cheap enough to do constantly.
        /// </para>
        /// <para>
        /// NO ITERATION EVER SLEEPS A MILLISECOND. That is a hard requirement rather than a preference: one
        /// <c>Thread.Sleep(1)</c> is more than the whole 0.2 ms per-frame drain budget this is measured against
        /// (M2), and more again at Windows' default timer resolution, so a drain that escalated to one would make
        /// the soak a measurement of the scheduler and settle decision C6 on a number about nothing. A plain
        /// <c>SpinWait.SpinOnce()</c> escalates to exactly that after 20 iterations, which is why the spin below
        /// goes through <see cref="D3D11DrainSpin"/> and its documented <c>sleep1Threshold</c> instead. The
        /// monotonic mechanism blocks on the fence itself, which wakes on the GPU's own signal with no
        /// granularity cost, and the event-query fallback yields without ever sleeping.
        /// </para>
        /// <para>
        /// THE WAIT HAS NO TIMEOUT, on purpose. A GPU that never reaches the signalled point has hung, and the
        /// honest behaviour there is the same block <c>vkQueueWaitIdle</c> and the Metal equivalent give. A
        /// timeout would turn a hang into silent forward progress over work that has not happened, which is worse
        /// in exactly the way this backend exists to avoid. The per-slice timeout below is not that: it only
        /// bounds how long one wait goes before the liveness check runs again, so a device that dies mid-drain
        /// (Direct3D's own reset after a hang) releases the caller.
        /// </para>
        /// <para>
        /// A CALLER HOLDING THE SUBMIT LOCK IS REFUSED, BY NAME, ON THE LIVE DRAIN AND ONLY THERE (decision W4,
        /// and the enforcement half of the paragraph above). The drain releases the lock around its wait precisely
        /// so the submission it is waiting for can be made, and a caller that already held the lock re-enters it
        /// here rather than acquiring it, so the release inside this method releases NOTHING: the outer level
        /// survives, no other thread can submit, and the drain waits for work that can never arrive. That is a
        /// hang with no name on it, at teardown, which is where a hang is hardest to attribute. The check costs
        /// one <see cref="Monitor.IsEntered"/> per drain and turns the whole family into a message naming the
        /// rule.
        /// </para>
        /// <para>
        /// IT COMES AFTER THE TWO NO-OP RETURNS, WHICH IS THE ORDER THAT MATTERS. Teardown is exactly where a
        /// caller legitimately holds the submit lock and where the device is exactly as likely to be dead, so a
        /// guard placed ahead of the X3 return would hand that caller an exception in place of the quiet no-op X3
        /// promises, and would leave <c>KE_D3D11_REAL_DRAIN=0</c> throwing where it is supposed to restore the
        /// empty method body verbatim. Neither return does anything, so neither can be harmed by running under
        /// the lock, which is why the safe ordering is also the useful one.
        /// </para>
        /// </summary>
        internal void WaitForIdle()
        {
            // The two returns that do nothing come first, so a caller holding the submit lock reaches the guard
            // only on the path that can actually hang. See the ordering paragraph on this method.
            if (_liveness.IsDead) return;
            if (!_realDrain) return;

            if (Monitor.IsEntered(_submitLock))
            {
                throw new InvalidOperationException(
                    "WaitForIdle was called on the native Direct3D 11 device while the caller already held the "
                    + "submit lock. The drain signals and flushes under the lock and then RELEASES it to wait, so "
                    + "that the work it is waiting for can still be submitted. Re-entering the lock here releases "
                    + "nothing, so the drain would wait for a submission no other thread can make. Call it "
                    + "outside the frame's critical section.");
            }

            ulong target;
            lock (_submitLock)
            {
                _issued = _timeline.Signal();
                target = _issued;
                _timeline.Flush();
            }

            long start = Stopwatch.GetTimestamp();
            var spin = new SpinWait();
            while (!_liveness.IsDead)
            {
                if (ReadCompleted() >= target) break;

                // The wait, or the spin for a mechanism that has none. The spin goes through D3D11DrainSpin
                // rather than being written out here, so the threshold that keeps it from sleeping a millisecond
                // is a named constant with the reason attached instead of a bare -1 nobody would query.
                if (!_timeline.TryWaitForValue(target, DrainWaitSliceMs)) D3D11DrainSpin.SpinOnce(ref spin);
            }

            long elapsed = Stopwatch.GetTimestamp() - start;
            _drainCount++;
            _drainTicks += elapsed;
            _totalDrainCount++;
            _totalDrainTicks += elapsed;
        }

        /// <summary>
        /// Close the frame just built and start a fresh accumulator: what was accumulating becomes
        /// <see cref="LastFrameDrain"/>. Called once per frame from the device's present, which is the same
        /// boundary every other per-frame counter in the engine uses.
        /// </summary>
        internal void BeginFrame()
        {
            _lastFrame = new D3D11DrainStats(_drainCount, _drainTicks * 1000d / Stopwatch.Frequency);
            _drainCount = 0;
            _drainTicks = 0L;
        }

        /// <summary>Release the timeline. Outstanding <see cref="D3D11GpuFence"/> instances hold no device object
        /// of their own, so they simply stop being able to observe anything new.</summary>
        public void Dispose() => _timeline.Dispose();

        // The timeline's completed value under whatever synchronisation its mechanism actually needs, and the one
        // place that decision is taken. Lock-free on a free-threaded poll, which is what keeps IGpuFence.Signaled
        // off the submit lock on the primary mechanism. See the class note for the deviation on the fallback.
        ulong ReadCompleted()
        {
            if (_timeline.PollIsFreeThreaded) return _timeline.CompletedValue;

            lock (_submitLock) return _timeline.CompletedValue;
        }
    }
}
