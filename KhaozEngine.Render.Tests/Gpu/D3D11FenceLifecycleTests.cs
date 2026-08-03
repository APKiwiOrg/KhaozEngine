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
        }

        /// <summary>A fence from another backend cannot be armed, and saying so beats an
        /// <c>InvalidCastException</c> with no backend name in it.</summary>
        [Fact]
        public void AFenceFromAnotherBackend_IsRejectedByName()
        {
            var timeline = new FakeD3D11FenceTimeline();
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            ArgumentException ex =
                Assert.Throws<ArgumentException>(() => fences.SignalEndOfReplay(new ForeignFence()));
            Assert.Contains(nameof(ForeignFence), ex.Message, StringComparison.Ordinal);
        }

        /// <summary>Arming a disposed fence is a defect on the path where it matters (it is reached from
        /// <c>Submit</c>), while polling and resetting one stay quiet, because those are reached at teardown
        /// where a wrapper outliving its owner is normal.</summary>
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

        /// <summary>The capability is a constant and true on both mechanisms (decision C5, the one permitted
        /// difference from the incumbent Direct3D 11 backend).</summary>
        [Fact]
        public void CompletionFencesAreSupported_OnTheMonotonicFence()
            => AssertCompletionFencesSupported(D3D11FenceMechanism.MonotonicFence);

        /// <summary>The same on the fallback, which is the half of C5 a reader is most likely to assume is a
        /// degraded mode. It is not: an event query is a completion signal too.</summary>
        [Fact]
        public void CompletionFencesAreSupported_OnTheEventQueryFallback()
            => AssertCompletionFencesSupported(D3D11FenceMechanism.EventQuery);

        // Not a [Theory], because an InlineData carrying the mechanism would need the enum to be public and it is
        // deliberately internal to this backend.
        static void AssertCompletionFencesSupported(D3D11FenceMechanism mechanism)
        {
            var timeline = new FakeD3D11FenceTimeline { Mechanism = mechanism };
            using D3D11FenceSubsystem fences = Subsystem(timeline);

            Assert.True(fences.SupportsCompletionFences);
            Assert.Equal(mechanism, fences.Mechanism);
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
