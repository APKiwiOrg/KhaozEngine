using System;
using System.Diagnostics;
using System.Threading;

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
    /// <para><b>The submit lock, and why every timeline touch goes through it.</b> The lock is the device's
    /// (decision W4) and is passed in rather than created here. The monotonic-fence path would not strictly need
    /// it for a poll, since <c>GetCompletedValue</c> is a read on the fence object, but the event-query fallback
    /// polls the IMMEDIATE CONTEXT, which is not free-threaded. Taking the lock on both paths is what keeps the
    /// two mechanisms interchangeable, which is the property everything above the timeline is built on. It is
    /// held for one native call at a time and never across a wait, so a drain does not block submission.</para>
    ///
    /// <para><b>The drain does not hold the lock while it spins</b>, which is the one piece of the loop below
    /// worth reading twice. It signals under the lock, then polls under the lock, releasing between polls, so the
    /// work it is waiting for can actually be submitted by another thread. A drain that held the lock throughout
    /// would deadlock against exactly the submission that would let it finish.</para>
    ///
    /// <para><b>Not thread-safe for its own counters.</b> The telemetry accumulators are driven from the frame
    /// thread, the same contract <c>RetiredResourcePool</c> and the water renderer's counters already have.</para>
    /// </summary>
    internal sealed class D3D11FenceSubsystem : IDisposable
    {
        readonly ID3D11FenceTimeline _timeline;
        readonly object _submitLock;
        readonly ID3D11DeviceLiveness _liveness;
        readonly bool _realDrain;

        // The last value handed out by the timeline through this subsystem. Read after device death in place of
        // asking the timeline, since a destroyed device's objects must not be touched and everything issued is
        // complete by then anyway.
        ulong _issued;

        int _drainCount;
        long _drainTicks;
        D3D11DrainStats _lastFrame;

        /// <summary>
        /// Build the subsystem over <paramref name="timeline"/>, taking ownership of it.
        /// </summary>
        /// <param name="timeline">The device's completion timeline. Disposed with this subsystem.</param>
        /// <param name="submitLock">The device's single submit lock (decision W4). Not created here, because the
        /// same lock has to cover replay, present and the resize apply.</param>
        /// <param name="liveness">The device's liveness latch, or null while the device does not have one yet, in
        /// which case the device is treated as alive forever. See <see cref="ID3D11DeviceLiveness"/>.</param>
        /// <param name="realDrain">Whether <see cref="WaitForIdle"/> really drains. Resolved from
        /// <see cref="D3D11RealDrain"/> by the caller rather than read from the environment here, so the
        /// behaviour is testable without touching process state.</param>
        internal D3D11FenceSubsystem(
            ID3D11FenceTimeline timeline,
            object submitLock,
            ID3D11DeviceLiveness? liveness = null,
            bool realDrain = true)
        {
            _timeline = timeline ?? throw new ArgumentNullException(nameof(timeline));
            _submitLock = submitLock ?? throw new ArgumentNullException(nameof(submitLock));
            _liveness = liveness ?? D3D11LiveDevice.Instance;
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
        /// <c>GpuRetireBarrier.TryCreate</c> stops returning null, <c>RetiredResourcePool</c> takes its fenced
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
        /// The timeline's completed value, polled under the submit lock. After device death this answers with the
        /// last value issued instead of touching the timeline, so everything ever signalled reads complete and no
        /// call reaches a destroyed device's objects.
        /// </summary>
        internal ulong CompletedValue
        {
            get
            {
                if (_liveness.IsDead) return _issued;

                lock (_submitLock) return _timeline.CompletedValue;
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
        /// </summary>
        /// <param name="fence">The fence handed to <c>Submit(IGpuCommandList, IGpuFence)</c>, or null for the
        /// fenceless <c>Submit(IGpuCommandList)</c>.</param>
        /// <returns>The timeline value this submission signalled.</returns>
        internal ulong SignalEndOfReplay(IGpuFence? fence)
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
        /// kill switch is down. Everything else drains and is counted, because the point of the counters is to
        /// price what the drain costs.
        /// </para>
        /// <para>
        /// THE SPIN HAS NO TIMEOUT, on purpose. A GPU that never reaches the signalled point has hung, and the
        /// honest behaviour there is the same block <c>vkQueueWaitIdle</c> and the Metal equivalent give. A
        /// timeout would turn a hang into silent forward progress over work that has not happened, which is worse
        /// in exactly the way this backend exists to avoid. The one escape is the liveness check inside the loop,
        /// so a device that dies mid-drain (Direct3D's own reset after a hang) releases the caller.
        /// </para>
        /// </summary>
        internal void WaitForIdle()
        {
            if (_liveness.IsDead) return;
            if (!_realDrain) return;

            ulong target;
            lock (_submitLock) { _issued = _timeline.Signal(); target = _issued; }

            long start = Stopwatch.GetTimestamp();
            var spin = new SpinWait();
            while (!_liveness.IsDead)
            {
                ulong completed;
                lock (_submitLock) completed = _timeline.CompletedValue;
                if (completed >= target) break;

                spin.SpinOnce();
            }

            _drainCount++;
            _drainTicks += Stopwatch.GetTimestamp() - start;
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
    }
}
