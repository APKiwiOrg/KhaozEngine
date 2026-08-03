using System;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.D3D11.Internal;
using Xunit;

namespace KhaozEngine.Tests.Gpu
{
    /// <summary>
    /// The native Direct3D 11 fence as the seam sees it (decisions C5 and X3): the monotonic target lifecycle,
    /// what <c>Reset</c> means when the counter is device-wide, the threshold <c>Signaled</c> reads, and what all
    /// of it answers once the device is dead.
    /// <para>
    /// Every test here runs on macOS and Linux, driven through a fake timeline. That is the whole reason
    /// <c>ID3D11FenceTimeline</c> is an interface: what is left behind it is two native calls per mechanism, and
    /// the lifecycle above it is where a defect would be silent. A fence that reported signalled one submission
    /// early frees resources the GPU is still reading, and the corruption surfaces somewhere else entirely.
    /// </para>
    /// </summary>
    public sealed class D3D11FenceLifecycleTests
    {
        static D3D11FenceSubsystem Subsystem(
            FakeD3D11FenceTimeline timeline, ID3D11DeviceLiveness? liveness = null, bool realDrain = true)
            => new(timeline, new object(), liveness, realDrain);

        /// <summary>A fresh fence is UNARMED and reads unsignalled, because the seam requires a fence to be
        /// unsignaled when it is submitted.</summary>
        [Fact]
        public void AFreshFence_IsUnarmedAndUnsignaled()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            var fence = (D3D11GpuFence)fences.CreateFence();

            Assert.Equal(0UL, fence.Target);
            Assert.False(fence.Signaled);
        }

        /// <summary>An unarmed fence never reads signalled no matter how far the timeline has run, which is what
        /// keeps a fence created mid-session usable: it is about the submission it is handed to, not about how
        /// much work the device has done in its life.</summary>
        [Fact]
        public void AnUnarmedFence_StaysUnsignaledWhileTheTimelineRunsOn()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);
            var fence = (D3D11GpuFence)fences.CreateFence();

            fences.SignalEndOfReplay(null);
            fences.SignalEndOfReplay(null);
            timeline.Completed = 2UL;

            Assert.False(fence.Signaled);
        }

        /// <summary>
        /// THE THRESHOLD. A fence armed at value N reads signalled once the completed value has REACHED N, not
        /// passed it, and stays unsignalled at N minus one. Asserted at the boundary on both sides, because an
        /// off-by-one in either direction is the whole defect: too early frees live resources, too late strands a
        /// pool forever.
        /// </summary>
        [Fact]
        public void AnArmedFence_SignalsExactlyWhenTheCompletedValueReachesItsTarget()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);
            IGpuFence fence = fences.CreateFence();

            fences.SignalEndOfReplay(null);                 // some earlier submission
            ulong target = fences.SignalEndOfReplay(fence); // ours
            Assert.Equal(2UL, target);

            timeline.Completed = 1UL;
            Assert.False(fence.Signaled);

            timeline.Completed = 2UL;
            Assert.True(fence.Signaled);

            timeline.Completed = 7UL;
            Assert.True(fence.Signaled);
        }

        /// <summary>
        /// RESET UNARMS, and the next submission arms with a strictly HIGHER value. That is the whole of "re-arms
        /// with a fresh target" on a device-wide monotonic counter: there is nothing to wind back, and the fresh
        /// target the seam asks for is what the next signal produces anyway.
        /// </summary>
        [Fact]
        public void Reset_UnarmsSoTheNextSubmissionArmsWithAHigherTarget()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);
            IGpuFence fence = fences.CreateFence();

            fences.SignalEndOfReplay(fence);
            timeline.Completed = 1UL;
            Assert.True(fence.Signaled);

            fence.Reset();
            Assert.False(fence.Signaled);
            Assert.Equal(0UL, ((D3D11GpuFence)fence).Target);

            fences.SignalEndOfReplay(fence);
            Assert.Equal(2UL, ((D3D11GpuFence)fence).Target);
            Assert.False(fence.Signaled);   // the completed value is still 1
        }

        /// <summary>
        /// Submitting a fence that is still armed is loud. The quiet alternative, overwriting the target, makes
        /// the earlier submission's completion unobservable, and a consumer polling for it frees resources the
        /// GPU is still reading. <c>GpuRetireBarrier</c> is the shipped consumer and it resets before every
        /// reuse, so nothing legitimate reaches this.
        /// <para>
        /// The throw is also where the timeline's behaviour on a rejected submit is pinned. The submission had
        /// already consumed a value by the time <c>Arm</c> could throw, so the timeline HAS advanced and that
        /// value is spent. It is deliberate, and it is what keeps the counter monotonic: nothing is handed out
        /// twice, the gap is read by nobody, and the fence keeps the target it already had rather than being
        /// quietly retargeted by a submission that failed.
        /// </para>
        /// </summary>
        [Fact]
        public void ArmingAnAlreadyArmedFence_ThrowsInsteadOfOverwritingTheTarget()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);
            IGpuFence fence = fences.CreateFence();

            fences.SignalEndOfReplay(fence);

            InvalidOperationException ex =
                Assert.Throws<InvalidOperationException>(() => fences.SignalEndOfReplay(fence));
            Assert.Contains("Reset", ex.Message, StringComparison.Ordinal);

            Assert.Equal(2, timeline.SignalCount);              // the failed submit still signalled
            Assert.Equal(1UL, ((D3D11GpuFence)fence).Target);   // and did not retarget the fence
        }

        /// <summary>A fence from another backend cannot be armed, and saying so beats an
        /// <c>InvalidCastException</c> with no backend name in it. Unlike the two throws above, this one costs
        /// the timeline nothing: the type check runs before anything is signalled.</summary>
        [Fact]
        public void AFenceFromAnotherBackend_IsRejectedByNameBeforeAnythingIsSignalled()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            ArgumentException ex =
                Assert.Throws<ArgumentException>(() => fences.SignalEndOfReplay(new ForeignFence()));
            Assert.Contains(nameof(ForeignFence), ex.Message, StringComparison.Ordinal);
            Assert.Equal(0, timeline.SignalCount);
        }

        /// <summary>
        /// THE FOREIGN-FENCE CHECK OUTLIVES THE DEVICE, which is the one place decision X3's quiet no-op does not
        /// apply. Handing this device another backend's fence is a programming error at any point in a device's
        /// life, and teardown is exactly where staying quiet would hide it: the retire pool is still running
        /// there, and a fence it believes was armed is one it waits on forever.
        /// </summary>
        [Fact]
        public void AFenceFromAnotherBackend_IsStillRejectedAfterTheDeviceIsDead()
        {
            var timeline = new FakeD3D11FenceTimeline();
            var liveness = new FakeD3D11DeviceLiveness { IsDead = true };
            using D3D11FenceSubsystem fences = Subsystem(timeline, liveness);

            Assert.Throws<ArgumentException>(() => fences.SignalEndOfReplay(new ForeignFence()));
            Assert.Equal(0, timeline.SignalCount);
        }

        /// <summary>Arming a disposed fence is a defect on the path where it matters (it is reached from
        /// <c>Submit</c>), while polling and resetting one stay quiet, because those are reached at teardown
        /// where a wrapper outliving its owner is normal. This throw spends a timeline value too, for the same
        /// reason the already-armed one does: the value belongs to the submission, not to the fence.</summary>
        [Fact]
        public void ADisposedFence_ThrowsOnArmingAndStaysQuietOnPollingAndResetting()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);
            IGpuFence fence = fences.CreateFence();

            fence.Dispose();

            Assert.False(fence.Signaled);
            fence.Reset();
            Assert.Throws<ObjectDisposedException>(() => fences.SignalEndOfReplay(fence));

            Assert.Equal(1, timeline.SignalCount);
            Assert.Equal(0UL, ((D3D11GpuFence)fence).Target);
        }

        /// <summary>
        /// A submit with no fence still advances the timeline. The counter has to track the submission stream for
        /// a later fence's value to cover the earlier work at all, and the constant-buffer ring's segment
        /// recycling reads the same counter.
        /// </summary>
        [Fact]
        public void AFencelessSubmit_StillAdvancesTheTimeline()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            Assert.Equal(1UL, fences.SignalEndOfReplay(null));
            Assert.Equal(2UL, fences.SignalEndOfReplay(null));
            Assert.Equal(2, timeline.SignalCount);
        }

        /// <summary>
        /// THE CAPABILITY IS A CONSTANT (decision C5, the one permitted difference from the incumbent Direct3D 11
        /// backend), and the mechanism is reported verbatim for the session log. One test over both enum values,
        /// because that is the honest shape of the claim: the answer does not depend on the input, and two tests
        /// varying an input that feeds nothing asserted a hardcoded true twice.
        /// <para>
        /// The fallback is the half a reader is most likely to assume is a degraded mode. It is not, as far as
        /// this capability goes: an event query is a completion signal too. Where the mechanisms DO differ is the
        /// lock-free poll and the blocking wait, and those are asserted against the timeline capability
        /// properties themselves, below and in <c>D3D11DrainTests</c>, rather than against the enum.
        /// </para>
        /// </summary>
        [Fact]
        public void CompletionFencesAreSupported_AndTheMechanismIsReportedVerbatim()
        {
            // Not a [Theory], because an InlineData carrying the mechanism would need the enum to be public and
            // it is deliberately internal to this backend.
            foreach (D3D11FenceMechanism mechanism in
                new[] { D3D11FenceMechanism.MonotonicFence, D3D11FenceMechanism.EventQuery })
            {
                var timeline = new FakeD3D11FenceTimeline { Mechanism = mechanism };
                using D3D11FenceSubsystem fences = Subsystem(timeline);

                Assert.True(fences.SupportsCompletionFences);
                Assert.Equal(mechanism, fences.Mechanism);
            }
        }

        // ---- The submit lock, and the poll that does not take it ----

        /// <summary>
        /// A POLL ON THE PRIMARY MECHANISM TAKES NO LOCK, which is what makes the seam's "a fence poll never
        /// waits" literally true there. <c>GetCompletedValue</c> is a read on a free-threaded fence object, and
        /// under decision W4 the submit lock covers a whole replay, so a poll that took it could wait for one.
        /// The signal in the same test still takes the lock, because signalling is a context call on every
        /// mechanism, and asserting the pair is what stops a future edit from making the signal lock-free too.
        /// </summary>
        [Fact]
        public void OnTheMonotonicFence_APollTakesNoSubmitLockWhileTheSignalStillDoes()
        {
            var submitLock = new object();
            var timeline = new FakeD3D11FenceTimeline { SubmitLock = submitLock };
            using var fences = new D3D11FenceSubsystem(timeline, submitLock);
            IGpuFence fence = fences.CreateFence();

            fences.SignalEndOfReplay(fence);
            Assert.True(timeline.LastSignalHeldTheSubmitLock);

            _ = fence.Signaled;
            Assert.False(timeline.LastPollHeldTheSubmitLock);

            _ = fences.CompletedValue;
            Assert.False(timeline.LastPollHeldTheSubmitLock);
        }

        /// <summary>
        /// ON THE FALLBACK THE POLL IS SERIALISED, and that deviation is pinned here rather than left as prose.
        /// The event-query poll runs on the immediate context, which is not free-threaded, so it has to take the
        /// submit lock and a cross-thread poll can therefore wait as long as a replay. Keeping the difference
        /// costs one branch on a mechanism nearly no machine takes, and levelling it would cost a lock on every
        /// poll on the mechanism nearly every machine takes.
        /// </summary>
        [Fact]
        public void OnTheEventQueryFallback_APollIsSerialisedByTheSubmitLock()
        {
            var submitLock = new object();
            var timeline = new FakeD3D11FenceTimeline
            {
                SubmitLock = submitLock,
                Mechanism = D3D11FenceMechanism.EventQuery,
                PollIsFreeThreaded = false,
            };
            using var fences = new D3D11FenceSubsystem(timeline, submitLock);
            IGpuFence fence = fences.CreateFence();

            fences.SignalEndOfReplay(fence);
            _ = fence.Signaled;

            Assert.True(timeline.LastPollHeldTheSubmitLock);
        }

        /// <summary>The subsystem owns the timeline, which is what lets the device dispose one object rather than
        /// knowing which mechanism it got.</summary>
        [Fact]
        public void DisposingTheSubsystem_DisposesTheTimeline()
        {
            var timeline = new FakeD3D11FenceTimeline();
            Subsystem(timeline).Dispose();

            Assert.True(timeline.Disposed);
        }

        // ---- Decision X3: after the device dies ----

        /// <summary>
        /// A FENCE READS SIGNALLED ONCE THE DEVICE IS DEAD, armed or not. A destroyed device has no outstanding
        /// work, so "is it done" is yes. Answering no would strand <c>RetiredResourcePool</c> forever on a batch
        /// it can never free, and teardown order is exactly where a wrapper outliving its device is normal.
        /// </summary>
        [Fact]
        public void AfterDeviceDeath_EveryFenceReadsSignaled()
        {
            var timeline = new FakeD3D11FenceTimeline();
            var liveness = new FakeD3D11DeviceLiveness();
            using D3D11FenceSubsystem fences = Subsystem(timeline, liveness);
            IGpuFence armed = fences.CreateFence();
            IGpuFence unarmed = fences.CreateFence();
            fences.SignalEndOfReplay(armed);

            Assert.False(armed.Signaled);
            Assert.False(unarmed.Signaled);

            liveness.IsDead = true;

            Assert.True(armed.Signaled);
            Assert.True(unarmed.Signaled);
        }

        /// <summary>
        /// A dead device's timeline is never touched again, on any path. Its Direct3D objects are gone, so a poll
        /// or a signal reaching them is a call into freed memory rather than a wrong answer.
        /// </summary>
        [Fact]
        public void AfterDeviceDeath_NothingTouchesTheTimelineAgain()
        {
            var timeline = new FakeD3D11FenceTimeline();
            var liveness = new FakeD3D11DeviceLiveness();
            using D3D11FenceSubsystem fences = Subsystem(timeline, liveness);
            IGpuFence fence = fences.CreateFence();
            fences.SignalEndOfReplay(null);

            liveness.IsDead = true;
            int signalsBefore = timeline.SignalCount;
            int pollsBefore = timeline.PollCount;

            fences.SignalEndOfReplay(fence);
            fences.WaitForIdle();
            _ = fence.Signaled;
            _ = fences.CompletedValue;

            Assert.Equal(signalsBefore, timeline.SignalCount);
            Assert.Equal(pollsBefore, timeline.PollCount);
        }

        /// <summary>After death the completed value answers with the last value issued, so everything ever
        /// signalled reads complete without asking a destroyed device.</summary>
        [Fact]
        public void AfterDeviceDeath_TheCompletedValueIsEverythingEverIssued()
        {
            var timeline = new FakeD3D11FenceTimeline();
            var liveness = new FakeD3D11DeviceLiveness();
            using D3D11FenceSubsystem fences = Subsystem(timeline, liveness);
            fences.SignalEndOfReplay(null);
            fences.SignalEndOfReplay(null);

            liveness.IsDead = true;

            Assert.Equal(2UL, fences.CompletedValue);
        }

        /// <summary>
        /// THE SAME X3 BEHAVIOUR AGAINST THE REAL LATCH, which is the whole of the wiring between this row and
        /// the resources row: <see cref="D3D11DeviceLiveness"/> is the device's shared volatile token, and it
        /// rides this subsystem's liveness argument directly rather than through an adapter.
        /// <para>
        /// Worth a test of its own even though the fake already covers the behaviour, because the fake is the
        /// shape this row ASSUMED and the latch is the shape the other row BUILT. The two agreeing is the claim,
        /// and it is one an edit to either side could break silently: a latch whose read surface drifted would
        /// leave every fence assertion above passing against a stand-in for a type nothing uses.
        /// </para>
        /// </summary>
        [Fact]
        public void TheDevicesRealLivenessLatch_DrivesTheSameX3Behaviour()
        {
            var timeline = new FakeD3D11FenceTimeline();
            var liveness = new D3D11DeviceLiveness();
            using D3D11FenceSubsystem fences = Subsystem(timeline, liveness);
            IGpuFence fence = fences.CreateFence();
            fences.SignalEndOfReplay(fence);

            Assert.False(fences.IsDeviceDead);
            Assert.False(fence.Signaled);

            liveness.MarkDead();

            Assert.True(fences.IsDeviceDead);
            Assert.True(fence.Signaled);

            // And the drain is a no-op: it never signals, never polls and never waits on a dead device.
            int signalsBefore = timeline.SignalCount;
            int pollsBefore = timeline.PollCount;
            fences.WaitForIdle();

            Assert.Equal(signalsBefore, timeline.SignalCount);
            Assert.Equal(pollsBefore, timeline.PollCount);
            Assert.Equal(0, timeline.WaitCallCount);
        }

        /// <summary>The default when no liveness token has been wired in is ALIVE, which is the safe default: a
        /// default of dead would make every fence read signalled from the start, which is the X3 behaviour
        /// arriving before the device has died.</summary>
        [Fact]
        public void WithNoLivenessTokenWiredIn_TheDeviceIsAlive()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            Assert.False(fences.IsDeviceDead);
            Assert.False(fences.CreateFence().Signaled);
        }

        sealed class ForeignFence : IGpuFence
        {
            public bool Signaled => false;
            public void Reset() { }
            public void Dispose() { }
        }
    }
}
